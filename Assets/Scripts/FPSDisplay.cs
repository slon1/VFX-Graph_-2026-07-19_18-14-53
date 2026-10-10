using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// On-screen frame-time window for device runs. Attach to any scene object.
/// The measure is frame time, not fps: a 60 Hz panel can hide a longer sim.
/// </summary>
/// <remarks>
/// S10 on Vulkan returns cpu and gpu timings. cpuFrameTime equals the vsync
/// interval (cpuAvg matched frameAvg, about 16.7 ms) and does not show headroom.
/// cpuMainThreadFrameTime and cpuRenderThreadFrameTime are the load.
/// cpuMainThreadPresentWaitTime is the wait inside the present.
/// Player Settings Frame Timing Stats is off; a release build needs that checkbox.
/// FrameTimingManager delivers a sample several frames late. A 30 s average absorbs that.
/// 10 s buckets stop at 48 (8 minutes). Later buckets are not recorded.
/// The count on screen is the configured source count, not the live set.
/// The world logs "N points"; compare them once by hand.
/// SimulationWorld.OnDisable tears the world down, so enabled = false is not a sim pause.
/// Device log: adb logcat -s Unity | findstr M3D_PERF
/// Device CSV: adb pull from Android/data/(package)/files/m3d_perf.csv
/// </remarks>
public sealed class FPSDisplay : MonoBehaviour
{
	private const int MaxBuckets = 48;
	private const int ButtonRows = 4;
	private const int QuadsSearchAttempts = 8;
	private const float BucketSeconds = 10f;
	private const float SearchSeconds = 1f;
	private const float ThermalSeconds = 1f;
	private const float SampleFpsBudget = 240f;
	private const string CsvFileName = "m3d_perf.csv";
	private const string CsvSuffixName = "m3d_perf_res.csv";
	private const string CsvHeaderLine =
		"count,maxNeighbors,force,render,debug,phase,t,frameAvg,frameP95,frameMax,long,cpuFrame,cpuMainAvg,cpuMainP95,cpuRenderAvg,cpuRenderP95,cpuWaitAvg,cpuWaitP95,gpuAvg,gpuP95,thermal,thermalStart,thermalMax,buckets,res,hist";
	private const string CsvHeader = CsvHeaderLine + "\n";

	// 16.7 / 33.3 / 50 ms are the vsync clusters. Bins 2 and 3 catch frames that fall between them.
	private static readonly float[] HistEdges = { 15f, 18f, 25f, 36f, 55f };

	private static readonly string[] PhaseNames = { "WARMUP", "MEASURE", "DONE" };
	private static readonly string[] ThermalNames =
	{
		"none", "light", "moderate", "severe", "critical", "emergency", "shutdown"
	};

	private enum Phase
	{
		Warmup = 0,
		Measure = 1,
		Done = 2,
	}

	[SerializeField] private float updateInterval = 0.5f;
	[SerializeField] private int fontSize = 32;
	[SerializeField] private Color textColor = Color.green;
	[SerializeField] private float warmupSeconds = 30f;
	[SerializeField] private float measureSeconds = 30f;
	[SerializeField] private float longFrameMs = 17.5f;

	private readonly StringBuilder text = new StringBuilder(512);
	private readonly GUIContent windowContent = new GUIContent("Restart window");
	private readonly GUIContent max48Content = new GUIContent("MaxN 48");
	private readonly GUIContent max64Content = new GUIContent("MaxN 64");
	private readonly GUIContent forceOnContent = new GUIContent("Force on");
	private readonly GUIContent forceOffContent = new GUIContent("Force off");
	private readonly GUIContent renderOnContent = new GUIContent("Render on");
	private readonly GUIContent renderOffContent = new GUIContent("Render off");
	private readonly GUIContent debugOnContent = new GUIContent("Debug on");
	private readonly GUIContent debugOffContent = new GUIContent("Debug off");
	private readonly GUIContent debugNaContent = new GUIContent("Debug n/a");
	private readonly GUIContent reloadContent = new GUIContent("Reload scene");

	private struct Series
	{
		public float[] Samples;
		public float[] Sort;
		public int Count;
		public float Sum;
		public bool Valid;
		public float Avg;
		public float P95;
	}

	private SimulationWorld world;
	private BoidNeighborForcePass forcePass;
	private HashCountsToFieldPass hashCountsPass;
	private GameObject debugQuads;
	private bool worldResolved;
	private bool quadsSearchDone;
	private int quadsSearchAttempts;
	private float quadsSearchTimer;
	private int particleCount = -1;
	private float searchTimer;

	private Phase phase;
	private float windowElapsed;
	private float measureElapsed;
	private float blockElapsed;
	private float displayTimer;
	private float lastDtMs;
	private bool doneLogged;

	private int sampleCap;
	private float[] frameSamples;
	private float[] frameSort;
	private int frameCount;
	private float frameSum;
	private float frameMax;
	private int longCount;
	private int[] hist;

	private Series cpuFrame;
	private Series cpuMain;
	private Series cpuRender;
	private Series cpuWait;
	private Series gpuTiming;
	private FrameTiming[] timingScratch;

	private float[] bucketAvg;
	private int closedBuckets;
	private int bucketFrames;
	private float bucketSum;

	private int statsFrame = -1;
	private bool statFrameValid;
	private float statFrameAvg;
	private float statFrameP95;
	private float statFrameMax;
	private float statLong;

	private string display = "";
	private GUIStyle labelStyle;
	private GUIStyle buttonStyle;

	private string csvPath;
	private bool csvChecked;
	private bool infoLogged;

	private int thermalStatus = -1;
	private int thermalStart = -1;
	private int thermalMax = -1;

#if UNITY_EDITOR
	private int savedMaxNeighbors;
	private bool savedForceEnabled;
	private bool savedRenderParticles;
	private bool savedHashEnabled;
	private bool savedQuadsActive;
	private bool savedQuadsNoted;
	private bool hasSavedBaseline;
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
	private IntPtr powerManagerGlobal;
	private IntPtr thermalMethod;
	private bool thermalReady;
	private float thermalTimer;
	private readonly jvalue[] jniArgs = new jvalue[1];
#endif

	private void Start()
	{
		Application.targetFrameRate = 120;
		QualitySettings.vSyncCount = 0;
		LogDeviceInfo();
	}

	private void OnEnable()
	{
		AllocateSamples();
		if (world == null)
		{
			worldResolved = false;
			forcePass = null;
			hashCountsPass = null;
			debugQuads = null;
			quadsSearchDone = false;
			quadsSearchAttempts = 0;
			particleCount = -1;
		}

		csvPath = Path.Combine(Application.persistentDataPath, CsvFileName);
		TryResolveWorld();
#if UNITY_ANDROID && !UNITY_EDITOR
		InitThermal();
		PollThermal();
#endif
		RebuildDisplay();
	}

	private void OnDisable()
	{
#if UNITY_EDITOR
		RestoreBaseline();
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
		ReleaseThermal();
#endif
	}

	private void Update()
	{
		float dt = Time.unscaledDeltaTime;
		if (dt < 0f)
		{
			dt = 0f;
		}

		lastDtMs = dt * 1000f;
		windowElapsed += dt;

		bool storeSample = phase == Phase.Measure;
		CaptureTiming(storeSample);
		if (storeSample)
		{
			RecordFrame(lastDtMs);
			measureElapsed += dt;
			if (measureElapsed >= NonNegative(measureSeconds))
			{
				phase = Phase.Done;
			}
		}
		else if (phase == Phase.Warmup && windowElapsed >= NonNegative(warmupSeconds))
		{
			phase = Phase.Measure;
		}

		bool wroteLog = false;
		bucketSum += lastDtMs;
		bucketFrames++;
		blockElapsed += dt;
		if (blockElapsed >= BucketSeconds)
		{
			CloseBucket();
			blockElapsed -= BucketSeconds;
			if (blockElapsed > BucketSeconds)
			{
				blockElapsed = 0f;
			}

			WriteLog();
			wroteLog = true;
		}

		if (phase == Phase.Done && !doneLogged)
		{
			doneLogged = true;
			if (!wroteLog)
			{
				WriteLog();
			}
		}

		if (!worldResolved)
		{
			searchTimer += dt;
			if (searchTimer >= SearchSeconds)
			{
				searchTimer = 0f;
				TryResolveWorld();
			}
		}
		else if (!quadsSearchDone)
		{
			quadsSearchTimer += dt;
			if (quadsSearchTimer >= SearchSeconds)
			{
				quadsSearchTimer = 0f;
				TryFindQuads();
			}
		}

#if UNITY_ANDROID && !UNITY_EDITOR
		if (thermalReady)
		{
			thermalTimer += dt;
			if (thermalTimer >= ThermalSeconds)
			{
				thermalTimer = 0f;
				PollThermal();
			}
		}
#endif

		displayTimer += dt;
		float interval = updateInterval > 0f ? updateInterval : 0.5f;
		if (displayTimer >= interval)
		{
			displayTimer = 0f;
			RebuildDisplay();
		}
	}

	private void OnGUI()
	{
		EnsureStyles();
		float minSide = Mathf.Min(Screen.width, Screen.height);
		float scale = minSide > 1f ? minSide / 1080f : 1f;
		float width = Screen.width / scale;
		float height = Screen.height / scale;
		int size = labelStyle.fontSize;
		float margin = 12f;
		float gap = 8f;
		float line = size * 1.35f;
		float buttonH = size * 2.2f;
		float buttonBlock = (buttonH + gap) * ButtonRows;
		float textH = height - margin * 2f - buttonBlock - gap;
		float minText = line * 9f;
		if (textH < minText)
		{
			textH = minText;
		}

		float reloadW = size * 11f;
		Matrix4x4 savedMatrix = GUI.matrix;
		GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);

		GUI.Label(new Rect(margin, margin, width - margin * 3f - reloadW, textH), display, labelStyle);
		if (GUI.Button(new Rect(width - margin - reloadW, margin, reloadW, buttonH), reloadContent, buttonStyle))
		{
			ReloadScene();
		}

		float y = margin + textH + gap;
		float fullW = width - margin * 2f;
		float colW = (fullW - gap) * 0.5f;
		if (GUI.Button(new Rect(margin, y, fullW, buttonH), windowContent, buttonStyle))
		{
			ResetWindow();
		}

		y += buttonH + gap;
		if (GUI.Button(new Rect(margin, y, colW, buttonH), max48Content, buttonStyle))
		{
			SetNeighbors(48);
		}

		if (GUI.Button(new Rect(margin + colW + gap, y, colW, buttonH), max64Content, buttonStyle))
		{
			SetNeighbors(64);
		}

		y += buttonH + gap;
		bool forceOn = forcePass != null && forcePass.Enabled;
		bool renderOn = world != null && world.RenderParticles;
		if (GUI.Button(new Rect(margin, y, colW, buttonH), forceOn ? forceOnContent : forceOffContent, buttonStyle))
		{
			ToggleForce();
		}

		if (GUI.Button(new Rect(margin + colW + gap, y, colW, buttonH), renderOn ? renderOnContent : renderOffContent, buttonStyle))
		{
			ToggleRender();
		}

		y += buttonH + gap;
		bool debugKnown = hashCountsPass != null && debugQuads != null;
		bool debugOn = debugKnown && hashCountsPass.Enabled && debugQuads.activeSelf;
		GUIContent debugContent = !debugKnown ? debugNaContent : debugOn ? debugOnContent : debugOffContent;
		if (GUI.Button(new Rect(margin, y, fullW, buttonH), debugContent, buttonStyle))
		{
			ToggleDebug();
		}

		GUI.matrix = savedMatrix;
	}

	private void AllocateSamples()
	{
		if (frameSamples != null)
		{
			return;
		}

		float seconds = measureSeconds > 0f ? measureSeconds : 30f;
		int cap = Mathf.CeilToInt(seconds * SampleFpsBudget) + 8;
		if (cap < 64)
		{
			cap = 64;
		}

		sampleCap = cap;
		frameSamples = new float[cap];
		frameSort = new float[cap];
		AllocateSeries(ref cpuFrame, cap);
		AllocateSeries(ref cpuMain, cap);
		AllocateSeries(ref cpuRender, cap);
		AllocateSeries(ref cpuWait, cap);
		AllocateSeries(ref gpuTiming, cap);
		timingScratch = new FrameTiming[1];
		bucketAvg = new float[MaxBuckets];
		hist = new int[HistEdges.Length + 1];
	}

	private static void AllocateSeries(ref Series series, int cap)
	{
		series.Samples = new float[cap];
		series.Sort = new float[cap];
	}

	private void TryResolveWorld()
	{
		if (worldResolved)
		{
			return;
		}

		SimulationWorld found = FindAnyObjectByType<SimulationWorld>();
		if (found == null)
		{
			return;
		}

		world = found;
		worldResolved = true;
		particleCount = -1;
		forcePass = null;
		hashCountsPass = null;

		EffectAsset effect = world.Effect;
		if (effect != null)
		{
			IDataSource source = effect.ResolveSource();
			if (source is SwarmSource swarm)
			{
				particleCount = SumSpawns(swarm);
			}
			else if (source is CubeSource cube)
			{
				int resolution = cube.Resolution;
				if (resolution <= 0)
				{
					particleCount = 0;
				}
				else
				{
					long count = (long)resolution * resolution * resolution;
					particleCount = count > int.MaxValue ? -1 : (int)count;
				}
			}

			var passes = effect.Passes;
			if (passes != null)
			{
				for (int i = 0; i < passes.Count; i++)
				{
					if (forcePass == null && passes[i] is BoidNeighborForcePass pass)
					{
						forcePass = pass;
					}
					else if (hashCountsPass == null && passes[i] is HashCountsToFieldPass hashPass)
					{
						hashCountsPass = hashPass;
					}
				}
			}
		}

		TryFindQuads();
#if UNITY_EDITOR
		CaptureBaseline();
#endif
	}

	private void TryFindQuads()
	{
		if (quadsSearchDone || world == null)
		{
			return;
		}

		quadsSearchAttempts++;
		Transform child = world.transform.Find("FieldDebugQuads");
		if (child == null)
		{
			if (quadsSearchAttempts >= QuadsSearchAttempts)
			{
				quadsSearchDone = true;
			}

			return;
		}

		debugQuads = child.gameObject;
		quadsSearchDone = true;
#if UNITY_EDITOR
		if (hasSavedBaseline && !savedQuadsNoted)
		{
			savedQuadsActive = debugQuads.activeSelf;
			savedQuadsNoted = true;
		}
#endif
	}

	private static int SumSpawns(SwarmSource swarm)
	{
		var spawns = swarm.Spawns;
		if (spawns == null)
		{
			return 0;
		}

		int sum = 0;
		for (int i = 0; i < spawns.Count; i++)
		{
			SwarmSource.Spawn spawn = spawns[i];
			if (spawn != null)
			{
				sum += spawn.Count;
			}
		}

		return sum;
	}

#if UNITY_EDITOR
	private void CaptureBaseline()
	{
		if (hasSavedBaseline)
		{
			return;
		}

		savedMaxNeighbors = forcePass != null ? forcePass.MaxNeighbors : 0;
		savedForceEnabled = forcePass != null && forcePass.Enabled;
		savedRenderParticles = world != null && world.RenderParticles;
		savedHashEnabled = hashCountsPass != null && hashCountsPass.Enabled;
		if (debugQuads != null)
		{
			savedQuadsActive = debugQuads.activeSelf;
			savedQuadsNoted = true;
		}

		hasSavedBaseline = true;
	}

	private void RestoreBaseline()
	{
		if (!hasSavedBaseline)
		{
			return;
		}

		if (forcePass != null)
		{
			forcePass.MaxNeighbors = savedMaxNeighbors;
			forcePass.Enabled = savedForceEnabled;
		}

		if (world != null)
		{
			world.RenderParticles = savedRenderParticles;
		}

		if (hashCountsPass != null)
		{
			hashCountsPass.Enabled = savedHashEnabled;
		}

		if (savedQuadsNoted && debugQuads != null)
		{
			debugQuads.SetActive(savedQuadsActive);
		}
	}
#endif

	/// <summary>
	/// Neighbor walk only. Without the force, the hash and steer keep running.
	/// ClearVelocity leaves velocity at zero, so particles coast on the last heading.
	/// </summary>
	private void ToggleForce()
	{
		if (forcePass != null)
		{
			forcePass.Enabled = !forcePass.Enabled;
		}

		ResetWindow();
	}

	private void ToggleRender()
	{
		if (world != null)
		{
			world.RenderParticles = !world.RenderParticles;
		}

		ResetWindow();
	}

	private void ToggleDebug()
	{
		bool turnOn;
		if (hashCountsPass != null && debugQuads != null)
		{
			turnOn = !(hashCountsPass.Enabled && debugQuads.activeSelf);
		}
		else if (hashCountsPass != null)
		{
			turnOn = !hashCountsPass.Enabled;
		}
		else if (debugQuads != null)
		{
			turnOn = !debugQuads.activeSelf;
		}
		else
		{
			ResetWindow();
			return;
		}

		if (hashCountsPass != null)
		{
			hashCountsPass.Enabled = turnOn;
		}

		if (debugQuads != null)
		{
			debugQuads.SetActive(turnOn);
		}

		ResetWindow();
	}

	private void SetNeighbors(int value)
	{
		if (forcePass != null)
		{
			forcePass.MaxNeighbors = value;
		}

		ResetWindow();
	}

	private void ReloadScene()
	{
		Scene scene = SceneManager.GetActiveScene();
		SceneManager.LoadScene(scene.name);
	}

	private void ResetWindow()
	{
		phase = Phase.Warmup;
		windowElapsed = 0f;
		measureElapsed = 0f;
		blockElapsed = 0f;
		displayTimer = 0f;
		doneLogged = false;
		frameCount = 0;
		frameSum = 0f;
		frameMax = 0f;
		longCount = 0;
		ClearSeries(ref cpuFrame);
		ClearSeries(ref cpuMain);
		ClearSeries(ref cpuRender);
		ClearSeries(ref cpuWait);
		ClearSeries(ref gpuTiming);
		thermalStart = thermalStatus >= 0 && thermalStatus <= 6 ? thermalStatus : -1;
		thermalMax = thermalStart;
		closedBuckets = 0;
		bucketFrames = 0;
		bucketSum = 0f;
		statsFrame = -1;
		if (hist != null)
		{
			Array.Clear(hist, 0, hist.Length);
		}

		RebuildDisplay();
	}

	private void RecordFrame(float ms)
	{
		CountHist(ms);
		if (frameCount >= sampleCap)
		{
			return;
		}

		frameSamples[frameCount++] = ms;
		frameSum += ms;
		if (ms > frameMax)
		{
			frameMax = ms;
		}

		if (ms > longFrameMs)
		{
			longCount++;
		}
	}

	private void CountHist(float ms)
	{
		if (hist == null)
		{
			return;
		}

		int bin = 0;
		while (bin < HistEdges.Length && ms >= HistEdges[bin])
		{
			bin++;
		}

		hist[bin]++;
	}

	private void CaptureTiming(bool store)
	{
		FrameTimingManager.CaptureFrameTimings();
		if (timingScratch == null)
		{
			return;
		}

		uint got = FrameTimingManager.GetLatestTimings(1, timingScratch);
		if (!store || got == 0)
		{
			return;
		}

		FrameTiming sample = timingScratch[0];
		PushSample(ref cpuFrame, sample.cpuFrameTime);
		PushSample(ref cpuMain, sample.cpuMainThreadFrameTime);
		PushSample(ref cpuRender, sample.cpuRenderThreadFrameTime);
		PushSample(ref cpuWait, sample.cpuMainThreadPresentWaitTime);
		PushSample(ref gpuTiming, sample.gpuFrameTime);
	}

	private static void ClearSeries(ref Series series)
	{
		series.Count = 0;
		series.Sum = 0f;
		series.Valid = false;
		series.Avg = 0f;
		series.P95 = 0f;
	}

	private void PushSample(ref Series series, double value)
	{
		if (value <= 0.0 || double.IsNaN(value) || series.Count >= sampleCap || series.Samples == null)
		{
			return;
		}

		float ms = (float)value;
		series.Samples[series.Count++] = ms;
		series.Sum += ms;
	}

	private void FinishSeries(ref Series series)
	{
		series.Valid = series.Count > 0;
		if (!series.Valid)
		{
			return;
		}

		series.Avg = series.Sum / series.Count;
		series.P95 = Percentile95(series.Samples, series.Sort, series.Count);
	}

	private void CloseBucket()
	{
		// Buckets past MaxBuckets are dropped on purpose.
		if (closedBuckets >= MaxBuckets || bucketFrames <= 0)
		{
			bucketSum = 0f;
			bucketFrames = 0;
			return;
		}

		bucketAvg[closedBuckets] = bucketSum / bucketFrames;
		closedBuckets++;
		bucketSum = 0f;
		bucketFrames = 0;
	}

	private void EnsureStats()
	{
		if (statsFrame == Time.frameCount)
		{
			return;
		}

		statsFrame = Time.frameCount;
		statFrameValid = frameCount > 0;
		if (statFrameValid)
		{
			statFrameAvg = frameSum / frameCount;
			statFrameMax = frameMax;
			statLong = longCount / (float)frameCount;
			statFrameP95 = Percentile95(frameSamples, frameSort, frameCount);
		}

		FinishSeries(ref cpuFrame);
		FinishSeries(ref cpuMain);
		FinishSeries(ref cpuRender);
		FinishSeries(ref cpuWait);
		FinishSeries(ref gpuTiming);
	}

	private static float Percentile95(float[] source, float[] scratch, int count)
	{
		if (count <= 0)
		{
			return 0f;
		}

		Array.Copy(source, scratch, count);
		Array.Sort(scratch, 0, count);
		int index = Mathf.CeilToInt(0.95f * count) - 1;
		if (index < 0)
		{
			index = 0;
		}
		else if (index >= count)
		{
			index = count - 1;
		}

		return scratch[index];
	}

	private void RebuildDisplay()
	{
		EnsureStats();
		text.Clear();
		text.Append(PhaseNames[(int)phase]);
		if (phase != Phase.Done)
		{
			float left = phase == Phase.Warmup
				? NonNegative(warmupSeconds) - windowElapsed
				: NonNegative(measureSeconds) - measureElapsed;
			if (left < 0f)
			{
				left = 0f;
			}

			text.Append(' ');
			text.Append(Mathf.CeilToInt(left));
			text.Append('s');
		}

		text.Append("  ");
		AppendNum(text, lastDtMs, "F1");
		text.Append(" ms\nframe ");
		if (!statFrameValid)
		{
			text.Append("n/a\n");
		}
		else
		{
			text.Append("avg ");
			AppendNum(text, statFrameAvg, "F1");
			text.Append("  p95 ");
			AppendNum(text, statFrameP95, "F1");
			text.Append("  max ");
			AppendNum(text, statFrameMax, "F1");
			text.Append("  long ");
			AppendNum(text, statLong, "F3");
			text.Append('\n');
		}

		text.Append("hist ");
		AppendHist(text, ' ');
		text.Append('\n');

		AppendPairLine(text, "cpuMain ", cpuMain.Valid, cpuMain.Avg, cpuMain.P95);
		AppendPairLine(text, "cpuRender ", cpuRender.Valid, cpuRender.Avg, cpuRender.P95);
		AppendPairLine(text, "gpu ", gpuTiming.Valid, gpuTiming.Avg, gpuTiming.P95);
		text.Append("10s ");
		AppendBuckets(text, ' ');
		text.Append("\nthermal ");
		AppendThermal(text);
		text.Append("  start ");
		AppendThermalIndex(text, thermalStart);
		text.Append("  max ");
		AppendThermalIndex(text, thermalMax);
		text.Append("\ncount ");
		AppendCount(text);
		text.Append("  nn ");
		if (forcePass == null)
		{
			text.Append("n/a");
		}
		else
		{
			text.Append(forcePass.MaxNeighbors);
		}

		text.Append("  force ");
		if (forcePass == null)
		{
			text.Append("n/a");
		}
		else
		{
			text.Append(forcePass.Enabled ? "on" : "off");
		}

		text.Append("  render ");
		if (world == null)
		{
			text.Append("n/a");
		}
		else
		{
			text.Append(world.RenderParticles ? "on" : "off");
		}

		text.Append("  debug ");
		AppendDebugWord(text);
		display = text.ToString();
	}

	private void WriteLog()
	{
		EnsureStats();
		text.Clear();
		text.Append("M3D_PERF count=");
		if (!worldResolved || particleCount < 0)
		{
			text.Append("na");
		}
		else
		{
			text.Append(particleCount);
		}
		text.Append(" maxNeighbors=");
		if (forcePass == null)
		{
			text.Append("na");
		}
		else
		{
			text.Append(forcePass.MaxNeighbors);
		}

		text.Append(" force=");
		if (forcePass == null)
		{
			text.Append("na");
		}
		else
		{
			text.Append(forcePass.Enabled ? '1' : '0');
		}

		text.Append(" render=");
		if (world == null)
		{
			text.Append("na");
		}
		else
		{
			text.Append(world.RenderParticles ? '1' : '0');
		}

		text.Append(" debug=");
		AppendDebugFlag(text);
		text.Append(" phase=");
		text.Append(PhaseNames[(int)phase]);
		text.Append(" t=");
		AppendNum(text, windowElapsed, "F1");
		text.Append(" frameAvg=");
		AppendOptional(text, statFrameValid, statFrameAvg, "F2");
		text.Append(" frameP95=");
		AppendOptional(text, statFrameValid, statFrameP95, "F2");
		text.Append(" frameMax=");
		AppendOptional(text, statFrameValid, statFrameMax, "F2");
		text.Append(" long=");
		AppendOptional(text, statFrameValid, statLong, "F3");
		text.Append(" cpuFrame=");
		AppendOptional(text, cpuFrame.Valid, cpuFrame.Avg, "F2");
		text.Append(" cpuMainAvg=");
		AppendOptional(text, cpuMain.Valid, cpuMain.Avg, "F2");
		text.Append(" cpuMainP95=");
		AppendOptional(text, cpuMain.Valid, cpuMain.P95, "F2");
		text.Append(" cpuRenderAvg=");
		AppendOptional(text, cpuRender.Valid, cpuRender.Avg, "F2");
		text.Append(" cpuRenderP95=");
		AppendOptional(text, cpuRender.Valid, cpuRender.P95, "F2");
		text.Append(" cpuWaitAvg=");
		AppendOptional(text, cpuWait.Valid, cpuWait.Avg, "F2");
		text.Append(" cpuWaitP95=");
		AppendOptional(text, cpuWait.Valid, cpuWait.P95, "F2");
		text.Append(" gpuAvg=");
		AppendOptional(text, gpuTiming.Valid, gpuTiming.Avg, "F2");
		text.Append(" gpuP95=");
		AppendOptional(text, gpuTiming.Valid, gpuTiming.P95, "F2");
		text.Append(" thermal=");
		AppendThermalFlag(text, thermalStatus);
		text.Append(" thermalStart=");
		AppendThermalFlag(text, thermalStart);
		text.Append(" thermalMax=");
		AppendThermalFlag(text, thermalMax);
		text.Append(" buckets=");
		AppendBuckets(text, '/');
		text.Append(" res=");
		text.Append(Screen.width);
		text.Append('x');
		text.Append(Screen.height);
		text.Append(" hist=");
		AppendHist(text, '/');
		string line = text.ToString();
		Debug.Log(line);
		try
		{
			AppendCsv(line);
		}
		catch (Exception exception)
		{
			Debug.LogWarning("M3D_PERF csv " + exception.Message);
		}
	}

	private void AppendCsv(string line)
	{
		if (!csvChecked)
		{
			csvChecked = true;
			csvPath = ChooseCsvPath();
		}

		File.AppendAllText(csvPath, line);
		File.AppendAllText(csvPath, "\n");
	}

	private static string ChooseCsvPath()
	{
		string directory = Application.persistentDataPath;
		string primary = Path.Combine(directory, CsvFileName);
		if (HeaderMatches(primary))
		{
			return primary;
		}

		string suffix = Path.Combine(directory, CsvSuffixName);
		if (!HeaderMatches(suffix))
		{
			File.WriteAllText(suffix, CsvHeader);
		}

		return suffix;
	}

	private static bool HeaderMatches(string path)
	{
		if (!File.Exists(path) || new FileInfo(path).Length == 0)
		{
			File.WriteAllText(path, CsvHeader);
			return true;
		}

		string first;
		using (var reader = new StreamReader(path))
		{
			first = reader.ReadLine();
		}

		return first == CsvHeaderLine;
	}

	private void LogDeviceInfo()
	{
		if (infoLogged)
		{
			return;
		}

		infoLogged = true;
		Debug.Log(
			"M3D_PERF_INFO device=" + SystemInfo.deviceModel
			+ " api=" + SystemInfo.graphicsDeviceType
			+ " gpu=" + SystemInfo.graphicsDeviceName
			+ " screen=" + Screen.width.ToString(CultureInfo.InvariantCulture)
			+ "x" + Screen.height.ToString(CultureInfo.InvariantCulture)
			+ " unity=" + Application.unityVersion);
	}

	private void AppendDebugFlag(StringBuilder sb)
	{
		if (hashCountsPass == null || debugQuads == null)
		{
			sb.Append("na");
			return;
		}

		sb.Append(hashCountsPass.Enabled && debugQuads.activeSelf ? '1' : '0');
	}

	private void AppendDebugWord(StringBuilder sb)
	{
		if (hashCountsPass == null || debugQuads == null)
		{
			sb.Append("n/a");
			return;
		}

		sb.Append(hashCountsPass.Enabled && debugQuads.activeSelf ? "on" : "off");
	}

	private static void AppendThermalFlag(StringBuilder sb, int status)
	{
		if (status < 0 || status > 6)
		{
			sb.Append("na");
			return;
		}

		sb.Append(status);
	}

	private static void AppendThermalIndex(StringBuilder sb, int status)
	{
		if (status < 0 || status >= ThermalNames.Length)
		{
			sb.Append("n/a");
			return;
		}

		sb.Append(status);
	}

	private void AppendCount(StringBuilder sb)
	{
		if (!worldResolved || particleCount < 0)
		{
			sb.Append("n/a");
			return;
		}

		sb.Append(particleCount);
	}

	private void AppendThermal(StringBuilder sb)
	{
		if (thermalStatus < 0 || thermalStatus >= ThermalNames.Length)
		{
			sb.Append("n/a");
			return;
		}

		sb.Append(thermalStatus);
		sb.Append(' ');
		sb.Append(ThermalNames[thermalStatus]);
	}

	private static void AppendPairLine(StringBuilder sb, string label, bool valid, float avg, float p95)
	{
		sb.Append(label);
		if (!valid)
		{
			sb.Append("n/a\n");
			return;
		}

		sb.Append("avg ");
		AppendNum(sb, avg, "F1");
		sb.Append("  p95 ");
		AppendNum(sb, p95, "F1");
		sb.Append('\n');
	}

	private void AppendHist(StringBuilder sb, char separator)
	{
		int total = 0;
		if (hist != null)
		{
			for (int i = 0; i < hist.Length; i++)
			{
				total += hist[i];
			}
		}

		if (total == 0)
		{
			sb.Append("na");
			return;
		}

		for (int i = 0; i < hist.Length; i++)
		{
			if (i > 0)
			{
				sb.Append(separator);
			}

			sb.Append(hist[i]);
		}
	}

	private void AppendBuckets(StringBuilder sb, char separator)
	{
		bool any = false;
		for (int i = 0; i < closedBuckets; i++)
		{
			if (any)
			{
				sb.Append(separator);
			}

			AppendNum(sb, bucketAvg[i], "F1");
			any = true;
		}

		if (bucketFrames > 0 && closedBuckets < MaxBuckets)
		{
			if (any)
			{
				sb.Append(separator);
			}

			AppendNum(sb, bucketSum / bucketFrames, "F1");
			any = true;
		}

		if (!any)
		{
			sb.Append("na");
		}
	}

	private static void AppendOptional(StringBuilder sb, bool valid, float value, string format)
	{
		if (!valid)
		{
			sb.Append("na");
			return;
		}

		AppendNum(sb, value, format);
	}

	private static void AppendNum(StringBuilder sb, float value, string format)
	{
		sb.Append(value.ToString(format, CultureInfo.InvariantCulture));
	}

	private void EnsureStyles()
	{
		if (labelStyle == null)
		{
			labelStyle = new GUIStyle(GUI.skin.label);
			labelStyle.wordWrap = true;
			buttonStyle = new GUIStyle(GUI.skin.button);
			buttonStyle.wordWrap = true;
		}

		int size = fontSize < 8 ? 8 : fontSize;
		labelStyle.fontSize = size;
		labelStyle.normal.textColor = textColor;
		buttonStyle.fontSize = size;
	}

	private static float NonNegative(float value)
	{
		return value > 0f ? value : 0f;
	}

#if UNITY_ANDROID && !UNITY_EDITOR
	private void InitThermal()
	{
		thermalReady = false;
		thermalStatus = -1;
		try
		{
			IntPtr versionClass = AndroidJNI.FindClass("android/os/Build$VERSION");
			if (versionClass == IntPtr.Zero)
			{
				return;
			}

			IntPtr sdkField = AndroidJNI.GetStaticFieldID(versionClass, "SDK_INT", "I");
			int sdk = AndroidJNI.GetStaticIntField(versionClass, sdkField);
			DeleteLocal(versionClass);
			if (sdk < 29)
			{
				return;
			}

			IntPtr unityPlayer = AndroidJNI.FindClass("com/unity3d/player/UnityPlayer");
			IntPtr activityField = AndroidJNI.GetStaticFieldID(
				unityPlayer, "currentActivity", "Landroid/app/Activity;");
			IntPtr activity = AndroidJNI.GetStaticObjectField(unityPlayer, activityField);
			DeleteLocal(unityPlayer);

			IntPtr contextClass = AndroidJNI.FindClass("android/content/Context");
			IntPtr powerField = AndroidJNI.GetStaticFieldID(
				contextClass, "POWER_SERVICE", "Ljava/lang/String;");
			IntPtr powerService = AndroidJNI.GetStaticObjectField(contextClass, powerField);
			IntPtr getService = AndroidJNI.GetMethodID(
				contextClass, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;");
			jniArgs[0].l = powerService;
			IntPtr powerManager = AndroidJNI.CallObjectMethod(activity, getService, jniArgs);
			DeleteLocal(powerService);
			DeleteLocal(contextClass);
			DeleteLocal(activity);

			IntPtr powerClass = AndroidJNI.FindClass("android/os/PowerManager");
			thermalMethod = AndroidJNI.GetMethodID(powerClass, "getCurrentThermalStatus", "()I");
			DeleteLocal(powerClass);
			if (powerManager == IntPtr.Zero || thermalMethod == IntPtr.Zero)
			{
				DeleteLocal(powerManager);
				return;
			}

			powerManagerGlobal = AndroidJNI.NewGlobalRef(powerManager);
			DeleteLocal(powerManager);
			thermalReady = powerManagerGlobal != IntPtr.Zero;
		}
		catch (Exception)
		{
			thermalReady = false;
			thermalStatus = -1;
			ReleaseThermal();
		}
	}

	private void PollThermal()
	{
		if (!thermalReady || powerManagerGlobal == IntPtr.Zero || thermalMethod == IntPtr.Zero)
		{
			return;
		}

		try
		{
			int status = AndroidJNI.CallIntMethod(powerManagerGlobal, thermalMethod, null);
			if (status < 0 || status > 6)
			{
				thermalStatus = -1;
				return;
			}

			thermalStatus = status;
			if (thermalStart < 0)
			{
				thermalStart = status;
			}

			if (thermalMax < status)
			{
				thermalMax = status;
			}
		}
		catch (Exception)
		{
			thermalReady = false;
			thermalStatus = -1;
		}
	}

	private void ReleaseThermal()
	{
		if (powerManagerGlobal != IntPtr.Zero)
		{
			AndroidJNI.DeleteGlobalRef(powerManagerGlobal);
			powerManagerGlobal = IntPtr.Zero;
		}

		thermalMethod = IntPtr.Zero;
		thermalReady = false;
	}

	private static void DeleteLocal(IntPtr local)
	{
		if (local != IntPtr.Zero)
		{
			AndroidJNI.DeleteLocalRef(local);
		}
	}
#endif
}
