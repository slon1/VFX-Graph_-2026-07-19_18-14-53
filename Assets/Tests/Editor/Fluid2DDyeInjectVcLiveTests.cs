using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
[Category("GPU")]
public class Fluid2DDyeInjectVcLiveTests
{
    private const string Velocity = "velocity";
    private const string FluidD = "fluidD";
    private const string FluidPhi = "fluidPhi";
    private const string Dye = "dye";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const string FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute";
    private const string GrayScottCompute = "Assets/Shaders/GPU/Passes/GrayScottPasses.compute";
    private const string TouchCompute = "Assets/Shaders/GPU/Passes/TouchGrayScottPasses.compute";
    private const float SizeWorld = 32f;
    private const float Dt = 1f / 60f;
    private const float Duration = 0.5f;
    private const float TotalTime = 3f;
    private const float TouchRadius = 1f;
    private const float TouchStrength = 10f;
    private const float MaxValue = 1f;
    private const int TimedFrames = 8;
    private const int InteriorMargin = 2;
    private static readonly float[] PathY = { 0.25f, 0.35f, 0.5f, 0.65f, 0.75f };

    [Test]
    [Timeout(900000)]
    public void Stroke_SafetyFilamentAndVcDelta()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine("F3.4 live stroke dt=1/60 duration=0.5 total=3 radius=1 strength=10");
        RunGeometry(512, report);
        RunGeometry(128, report);
        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
    }

    [Test]
    public void StamChain_DyeInjectVc_RecordsCpuDriverMsPerFrame()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine(
            "profile=Fluid2D_DyeInjectVC velocity=128 dye=512 size=32 Jacobi=40 " +
            "timer=cpu_driver_not_gpu");

        using (FieldTestHarness harness = CreateHarness(512))
        using (DyeInjectVcChain chain = DyeInjectVcChain.Create(harness, 1f, true))
        {
            harness.SetTouches(new[] { TouchAt(Vector3.zero, Vector3.zero) });
            chain.RunFrame(harness, Dt);

            Stopwatch watch = Stopwatch.StartNew();
            for (int frame = 0; frame < TimedFrames; frame++)
            {
                chain.RunFrame(harness, Dt);
            }

            watch.Stop();
            double msPerFrame = watch.Elapsed.TotalMilliseconds / TimedFrames;
            report.Append("elapsedMs/N=");
            report.Append(msPerFrame.ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" N=");
            report.Append(TimedFrames.ToString(CultureInfo.InvariantCulture));
            report.AppendLine(" timer=cpu_driver_not_gpu Editor velocity=128 dye=512");
            report.AppendLine("ref F3.1 elapsedMs/N=0.373");
        }

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
        Assert.Pass(text);
    }

    private static void RunGeometry(int dyeRes, StringBuilder report)
    {
        RunSeeded(dyeRes, report);
        RunPaired(dyeRes, report);
    }

    private static void RunSeeded(int dyeRes, StringBuilder report)
    {
        int totalFrames = Mathf.RoundToInt(TotalTime / Dt);
        Vector3 start = StrokeStart();
        Vector3 end = StrokeEnd();

        using (FieldTestHarness harness = CreateHarness(dyeRes))
        using (DyeInjectVcChain chain = DyeInjectVcChain.Create(harness, 1f, true))
        {
            bool hasPrev = false;
            Vector3 prev = Vector3.zero;
            for (int frame = 0; frame < totalFrames; frame++)
            {
                harness.SetTouches(StrokeTouches(frame, start, end, ref hasPrev, ref prev));
                chain.RunFrame(harness, Dt);
                float[] dye = harness.ReadScalar(Dye);
                Vector2[] velocity = harness.ReadVelocity(Velocity);
                AssertBounded(dye, velocity, $"V1-S dyeRes={dyeRes} frame={frame}");
            }
        }

        report.Append("dye");
        report.Append(dyeRes.ToString(CultureInfo.InvariantCulture));
        report.AppendLine(" V1-S safety ok");
    }

    private static void RunPaired(int dyeRes, StringBuilder report)
    {
        int totalFrames = Mathf.RoundToInt(TotalTime / Dt);
        int lastStroke = LastStrokeFrame(totalFrames);
        Assert.GreaterOrEqual(lastStroke, 0, "stroke produced no frames");
        Vector3 start = StrokeStart();
        Vector3 end = StrokeEnd();
        int[] passFrame = PassingFrames(start, end, lastStroke);

        using (FieldTestHarness withVc = CreateHarness(dyeRes))
        using (FieldTestHarness baseline = CreateHarness(dyeRes))
        using (DyeInjectVcChain chainVc = DyeInjectVcChain.Create(withVc, 0f, true))
        using (DyeInjectVcChain chainBase = DyeInjectVcChain.Create(baseline, 0f, false))
        {
            bool hasPrev = false;
            Vector3 prev = Vector3.zero;
            float bestDiff = 0f;
            int bestFrame = -1;
            float[] dyeVcStroke = null;
            float[] dyeBaseStroke = null;
            float[] dyeVcEnd = null;
            float[] dyeBaseEnd = null;
            Vector2[] velVcStroke = null;
            Vector2[] velBaseStroke = null;
            Vector2[] velVcEnd = null;
            Vector2[] velBaseEnd = null;

            for (int frame = 0; frame < totalFrames; frame++)
            {
                TouchForce[] touches = StrokeTouches(frame, start, end, ref hasPrev, ref prev);
                withVc.SetTouches(touches);
                baseline.SetTouches(touches);

                chainVc.RunFrame(withVc, Dt);
                chainBase.RunFrame(baseline, Dt);

                float[] dyeVc = withVc.ReadScalar(Dye);
                float[] dyeBase = baseline.ReadScalar(Dye);
                Vector2[] velVc = withVc.ReadVelocity(Velocity);
                Vector2[] velBase = baseline.ReadVelocity(Velocity);
                AssertBounded(dyeVc, velVc, $"V1-N dyeRes={dyeRes} frame={frame}");
                AssertFinite(dyeBase, velBase, $"V0-N dyeRes={dyeRes} frame={frame}");

                float diff = MaxInteriorDiff(velVc, velBase);
                if (diff > bestDiff)
                {
                    bestDiff = diff;
                    bestFrame = frame;
                }

                for (int i = 0; i < PathY.Length; i++)
                {
                    if (passFrame[i] != frame)
                    {
                        continue;
                    }

                    float window = MaxInWindow(dyeVc, dyeRes, 0.5f, PathY[i]);
                    Assert.Greater(
                        window,
                        0.05f,
                        $"V1-N dye{dyeRes} pass UV.y={PathY[i]} frame={frame} maxInWindow={window:G9}");
                }

                if (frame == lastStroke)
                {
                    dyeVcStroke = dyeVc;
                    dyeBaseStroke = dyeBase;
                    velVcStroke = velVc;
                    velBaseStroke = velBase;
                    float head = MaxInWindow(dyeVc, dyeRes, 0.5f, 0.75f);
                    Assert.Greater(
                        head,
                        0.05f,
                        $"V1-N dye{dyeRes} head UV.y=0.75 frame={frame} maxInWindow={head:G9}");
                }

                if (frame == totalFrames - 1)
                {
                    dyeVcEnd = dyeVc;
                    dyeBaseEnd = dyeBase;
                    velVcEnd = velVc;
                    velBaseEnd = velBase;
                }
            }

            Assert.Greater(bestDiff, 1e-6f, $"L3 dye{dyeRes} max|uV1-uV0|={bestDiff:G9} frame={bestFrame}");
            AppendPairReport(
                report, dyeRes, passFrame, lastStroke, bestDiff, bestFrame,
                dyeVcStroke, dyeBaseStroke, dyeVcEnd, dyeBaseEnd,
                velVcStroke, velBaseStroke, velVcEnd, velBaseEnd);
        }
    }

    private static void AppendPairReport(
        StringBuilder report,
        int dyeRes,
        int[] passFrame,
        int lastStroke,
        float bestDiff,
        int bestFrame,
        float[] dyeVcStroke,
        float[] dyeBaseStroke,
        float[] dyeVcEnd,
        float[] dyeBaseEnd,
        Vector2[] velVcStroke,
        Vector2[] velBaseStroke,
        Vector2[] velVcEnd,
        Vector2[] velBaseEnd)
    {
        string tag = "dye" + dyeRes.ToString(CultureInfo.InvariantCulture);
        report.Append(tag);
        report.Append(" L3 max|uV1-uV0|=");
        report.Append(bestDiff.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" frame=");
        report.Append(bestFrame.ToString(CultureInfo.InvariantCulture));
        report.Append(" lastStroke=");
        report.AppendLine(lastStroke.ToString(CultureInfo.InvariantCulture));

        for (int i = 0; i < PathY.Length; i++)
        {
            report.Append(tag);
            report.Append(" passFrame y=");
            report.Append(PathY[i].ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" frame=");
            report.AppendLine(passFrame[i].ToString(CultureInfo.InvariantCulture));
        }

        AppendVelocitySnapshot(report, tag, "stroke", velVcStroke, velBaseStroke);
        AppendVelocitySnapshot(report, tag, "3s", velVcEnd, velBaseEnd);

        float[] early = { 0.25f, 0.35f, 0.5f, 0.65f };
        for (int i = 0; i < early.Length; i++)
        {
            float v1 = MaxInWindow(dyeVcStroke, dyeRes, 0.5f, early[i]);
            float v0 = MaxInWindow(dyeBaseStroke, dyeRes, 0.5f, early[i]);
            report.Append(tag);
            report.Append(" windowAfterStroke y=");
            report.Append(early[i].ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" V1=");
            report.Append(v1.ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" V0=");
            report.AppendLine(v0.ToString("G9", CultureInfo.InvariantCulture));
        }

        if (dyeRes == 512)
        {
            report.Append(tag);
            report.Append(" peakY0.35 V1=");
            report.Append(AxisSample(dyeVcStroke, dyeRes, 0.35f).ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" peakY0.65 V1=");
            report.AppendLine(AxisSample(dyeVcStroke, dyeRes, 0.65f).ToString("G9", CultureInfo.InvariantCulture));
            report.Append(tag);
            report.Append(" FWHM@stroke V1=");
            report.Append(FwhmAcross(dyeVcStroke, dyeRes).ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" V0=");
            report.Append(FwhmAcross(dyeBaseStroke, dyeRes).ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" FWHM@3s V1=");
            report.Append(FwhmAcross(dyeVcEnd, dyeRes).ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" V0=");
            report.AppendLine(FwhmAcross(dyeBaseEnd, dyeRes).ToString("G9", CultureInfo.InvariantCulture));
        }
    }

    private static void AppendVelocitySnapshot(
        StringBuilder report, string tag, string when, Vector2[] vc, Vector2[] baseline)
    {
        report.Append(tag);
        report.Append(' ');
        report.Append(when);
        report.Append(" max|w| V1=");
        report.Append(MaxAbsVorticity(vc).ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" V0=");
        report.Append(MaxAbsVorticity(baseline).ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" ring|u| V1=");
        report.Append(MaxSpeedRing(vc, 0, 1).ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" V0=");
        report.Append(MaxSpeedRing(baseline, 0, 1).ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" texel2|u| V1=");
        report.Append(MaxSpeedRing(vc, 2, 2).ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" V0=");
        report.Append(MaxSpeedRing(baseline, 2, 2).ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" sum|u|^2 V1=");
        report.Append(KineticEnergy(vc).ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" V0=");
        report.AppendLine(KineticEnergy(baseline).ToString("G9", CultureInfo.InvariantCulture));
    }

    private static int LastStrokeFrame(int totalFrames)
    {
        int last = -1;
        for (int frame = 0; frame < totalFrames; frame++)
        {
            if (frame * Dt < Duration)
            {
                last = frame;
            }
        }

        return last;
    }

    private static int[] PassingFrames(Vector3 start, Vector3 end, int lastStroke)
    {
        int[] frames = new int[PathY.Length];
        for (int i = 0; i < PathY.Length; i++)
        {
            frames[i] = Mathf.Approximately(PathY[i], 0.75f)
                ? lastStroke
                : NearestStrokeFrame(PathY[i], start, end, lastStroke);
        }

        return frames;
    }

    private static int NearestStrokeFrame(float targetUvY, Vector3 start, Vector3 end, int lastStroke)
    {
        int best = 0;
        float bestDist = float.PositiveInfinity;
        Vector3 origin = Vector3.zero;
        Vector3 axisV = Vector3.forward;
        for (int frame = 0; frame <= lastStroke; frame++)
        {
            ScriptedTouchSample sample = ScriptedTouchStroke.Evaluate(frame * Dt, Duration, start, end);
            float uvY = Vector3.Dot(sample.Position - origin, axisV) / SizeWorld + 0.5f;
            float dist = Mathf.Abs(uvY - targetUvY);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = frame;
            }
        }

        return best;
    }

    private static TouchForce[] StrokeTouches(
        int frame,
        Vector3 start,
        Vector3 end,
        ref bool hasPrev,
        ref Vector3 prev)
    {
        float elapsed = frame * Dt;
        if (elapsed >= Duration)
        {
            return Array.Empty<TouchForce>();
        }

        ScriptedTouchSample sample = ScriptedTouchStroke.Evaluate(elapsed, Duration, start, end);
        Vector3 pos = sample.Position;
        Vector3 delta = hasPrev ? pos - prev : Vector3.zero;
        prev = pos;
        hasPrev = true;
        return new[] { TouchAt(pos, delta) };
    }

    private static Vector3 StrokeStart()
    {
        return ScriptedTouchStroke.UvToWorld(
            new Vector2(0.5f, 0.25f), Vector3.zero, Vector3.right, Vector3.forward, new Vector2(SizeWorld, SizeWorld));
    }

    private static Vector3 StrokeEnd()
    {
        return ScriptedTouchStroke.UvToWorld(
            new Vector2(0.5f, 0.75f), Vector3.zero, Vector3.right, Vector3.forward, new Vector2(SizeWorld, SizeWorld));
    }

    private static void AssertBounded(float[] dye, Vector2[] velocity, string label)
    {
        AssertFinite(dye, velocity, label);
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < dye.Length; i++)
        {
            if (dye[i] < min)
            {
                min = dye[i];
            }

            if (dye[i] > max)
            {
                max = dye[i];
            }
        }

        Assert.GreaterOrEqual(min, 0f, $"{label} min={min:G9}");
        Assert.LessOrEqual(max, MaxValue + 1e-3f, $"{label} max={max:G9}");
    }

    private static void AssertFinite(float[] dye, Vector2[] velocity, string label)
    {
        for (int i = 0; i < dye.Length; i++)
        {
            if (float.IsNaN(dye[i]) || float.IsInfinity(dye[i]))
            {
                Assert.Fail($"{label} dye[{i}]={dye[i]}");
            }
        }

        for (int i = 0; i < velocity.Length; i++)
        {
            if (float.IsNaN(velocity[i].x) || float.IsInfinity(velocity[i].x) ||
                float.IsNaN(velocity[i].y) || float.IsInfinity(velocity[i].y))
            {
                Assert.Fail($"{label} velocity[{i}]=({velocity[i].x},{velocity[i].y})");
            }
        }
    }

    private static float MaxInteriorDiff(Vector2[] vc, Vector2[] baseline)
    {
        int res = Mathf.RoundToInt(Mathf.Sqrt(vc.Length));
        Assert.AreEqual(res * res, vc.Length);
        Assert.AreEqual(vc.Length, baseline.Length);
        float best = 0f;
        for (int y = InteriorMargin; y < res - InteriorMargin; y++)
        {
            for (int x = InteriorMargin; x < res - InteriorMargin; x++)
            {
                Vector2 delta = vc[y * res + x] - baseline[y * res + x];
                float norm = delta.magnitude;
                if (norm > best)
                {
                    best = norm;
                }
            }
        }

        return best;
    }

    private static float MaxAbsVorticity(Vector2[] velocity)
    {
        int res = Mathf.RoundToInt(Mathf.Sqrt(velocity.Length));
        float best = 0f;
        for (int y = InteriorMargin; y < res - InteriorMargin; y++)
        {
            for (int x = InteriorMargin; x < res - InteriorMargin; x++)
            {
                float omega = VorticityAt(velocity, res, x, y);
                float abs = Mathf.Abs(omega);
                if (abs > best)
                {
                    best = abs;
                }
            }
        }

        return best;
    }

    private static float VorticityAt(Vector2[] velocity, int res, int x, int y)
    {
        Vector2 uE = LoadClamped(velocity, res, x + 1, y);
        Vector2 uW = LoadClamped(velocity, res, x - 1, y);
        Vector2 uN = LoadClamped(velocity, res, x, y + 1);
        Vector2 uS = LoadClamped(velocity, res, x, y - 1);
        return (uE.y - uW.y) - (uN.x - uS.x);
    }

    private static Vector2 LoadClamped(Vector2[] velocity, int res, int x, int y)
    {
        x = Mathf.Clamp(x, 0, res - 1);
        y = Mathf.Clamp(y, 0, res - 1);
        return velocity[y * res + x];
    }

    private static float MaxSpeedRing(Vector2[] velocity, int minDist, int maxDist)
    {
        int res = Mathf.RoundToInt(Mathf.Sqrt(velocity.Length));
        float best = 0f;
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                int dist = Mathf.Min(Mathf.Min(x, y), Mathf.Min(res - 1 - x, res - 1 - y));
                if (dist < minDist || dist > maxDist)
                {
                    continue;
                }

                float speed = velocity[y * res + x].magnitude;
                if (speed > best)
                {
                    best = speed;
                }
            }
        }

        return best;
    }

    private static float KineticEnergy(Vector2[] velocity)
    {
        float sum = 0f;
        for (int i = 0; i < velocity.Length; i++)
        {
            sum += velocity[i].sqrMagnitude;
        }

        return sum;
    }

    private static float MaxInWindow(float[] dye, int res, float uvX, float uvY)
    {
        Vector3 point = World(uvX, uvY);
        float max = 0f;
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                Vector3 center = TexelCenter(x, y, res);
                if (Mathf.Abs(center.x - point.x) > TouchRadius || Mathf.Abs(center.z - point.z) > TouchRadius)
                {
                    continue;
                }

                float value = dye[y * res + x];
                if (value > max)
                {
                    max = value;
                }
            }
        }

        return max;
    }

    private static float AxisSample(float[] dye, int res, float uvY)
    {
        Vector3 point = World(0.5f, uvY);
        int best = 0;
        float bestDist = float.PositiveInfinity;
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float dist = Vector3.Distance(TexelCenter(x, y, res), point);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = y * res + x;
                }
            }
        }

        return dye[best];
    }

    private static float FwhmAcross(float[] dye, int res)
    {
        int row = 0;
        float best = float.PositiveInfinity;
        for (int y = 0; y < res; y++)
        {
            float dz = Mathf.Abs(TexelCenter(0, y, res).z);
            if (dz < best)
            {
                best = dz;
                row = y;
            }
        }

        int peakX = 0;
        float peak = 0f;
        for (int x = 0; x < res; x++)
        {
            float value = dye[row * res + x];
            if (value > peak)
            {
                peak = value;
                peakX = x;
            }
        }

        if (peak <= 0f)
        {
            return 0f;
        }

        float half = peak * 0.5f;
        int left = peakX;
        int right = peakX;
        for (int x = peakX; x >= 0; x--)
        {
            if (dye[row * res + x] < half)
            {
                break;
            }

            left = x;
        }

        for (int x = peakX; x < res; x++)
        {
            if (dye[row * res + x] < half)
            {
                break;
            }

            right = x;
        }

        return (right - left) * (SizeWorld / res);
    }

    private static Vector3 World(float uvX, float uvY)
    {
        return ScriptedTouchStroke.UvToWorld(
            new Vector2(uvX, uvY),
            Vector3.zero,
            Vector3.right,
            Vector3.forward,
            new Vector2(SizeWorld, SizeWorld));
    }

    private static Vector3 TexelCenter(int x, int y, int res)
    {
        float texel = SizeWorld / res;
        float origin = -SizeWorld * 0.5f;
        return new Vector3((x + 0.5f) * texel + origin, 0f, (y + 0.5f) * texel + origin);
    }

    private static TouchForce TouchAt(Vector3 pos, Vector3 delta)
    {
        return new TouchForce
        {
            Position = pos,
            Delta = delta,
            Radius = TouchRadius,
            Strength = TouchStrength,
        };
    }

    private static FieldTestHarness CreateHarness(int dyeRes)
    {
        Vector2 size = new Vector2(SizeWorld, SizeWorld);
        Vector2Int velRes = new Vector2Int(128, 128);
        Vector2Int dye = new Vector2Int(dyeRes, dyeRes);
        return new FieldTestHarness(
            new[]
            {
                FieldTestHarness.Descriptor(
                    Velocity, FieldSemantic.Velocity, GraphicsFormat.R16G16_SFloat, velRes, size, Color.clear),
                FieldTestHarness.Descriptor(
                    FluidD, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat, velRes, size, Color.clear),
                FieldTestHarness.Descriptor(
                    FluidPhi, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat, velRes, size, Color.clear),
                FieldTestHarness.Descriptor(
                    Dye, FieldSemantic.Scalar, GraphicsFormat.R16_SFloat, dye, size, Color.clear),
            },
            FluidCompute,
            FieldCompute,
            GrayScottCompute,
            TouchCompute);
    }

    private sealed class DyeInjectVcChain : IDisposable
    {
        private readonly ZeroMeanScalarPass zeroMean;
        private readonly VorticityConfinementPass vorticity;

        private DyeInjectVcChain(
            TouchInjectVelocityFieldPass touch,
            SeedScalarDiskPass seed,
            TouchInjectScalarFieldPass inject,
            VorticityConfinementPass vorticity,
            DivergenceFieldPass divergence,
            ZeroMeanScalarPass zeroMean,
            JacobiPhiPass jacobi,
            SubtractPhiGradientPass subtract,
            SolidWallVelocityPass wallAfterProject,
            AdvectVelocityFieldPass advectVelocity,
            SolidWallVelocityPass wallAfterAdvect,
            AdvectScalarPass advectDye)
        {
            Touch = touch;
            Seed = seed;
            Inject = inject;
            this.vorticity = vorticity;
            Divergence = divergence;
            this.zeroMean = zeroMean;
            Jacobi = jacobi;
            Subtract = subtract;
            WallAfterProject = wallAfterProject;
            AdvectVelocity = advectVelocity;
            WallAfterAdvect = wallAfterAdvect;
            AdvectDye = advectDye;
        }

        private TouchInjectVelocityFieldPass Touch { get; }
        private SeedScalarDiskPass Seed { get; }
        private TouchInjectScalarFieldPass Inject { get; }
        private DivergenceFieldPass Divergence { get; }
        private JacobiPhiPass Jacobi { get; }
        private SubtractPhiGradientPass Subtract { get; }
        private SolidWallVelocityPass WallAfterProject { get; }
        private AdvectVelocityFieldPass AdvectVelocity { get; }
        private SolidWallVelocityPass WallAfterAdvect { get; }
        private AdvectScalarPass AdvectDye { get; }

        public static DyeInjectVcChain Create(FieldTestHarness harness, float seedValue, bool withVorticity)
        {
            TouchInjectVelocityFieldPass touch = new TouchInjectVelocityFieldPass();
            SeedScalarDiskPass seed = new SeedScalarDiskPass
            {
                FieldName = Dye,
                CenterUV = new Vector2(0.5f, 0.5f),
                RadiusUV = 0.04f,
                Value = seedValue,
            };
            TouchInjectScalarFieldPass inject = new TouchInjectScalarFieldPass
            {
                FieldName = Dye,
                InjectRate = 60f,
                MaxValue = MaxValue,
                RadiusScale = 1f,
            };
            VorticityConfinementPass vorticity = withVorticity
                ? new VorticityConfinementPass
                {
                    VelocityField = Velocity,
                    EpsilonVc = 1f,
                    BorderMargin = 2,
                }
                : null;
            DivergenceFieldPass divergence = new DivergenceFieldPass();
            ZeroMeanScalarPass zeroMean = new ZeroMeanScalarPass();
            JacobiPhiPass jacobi = new JacobiPhiPass { Iterations = 40 };
            SubtractPhiGradientPass subtract = new SubtractPhiGradientPass();
            SolidWallVelocityPass wallAfterProject = new SolidWallVelocityPass();
            AdvectVelocityFieldPass advectVelocity = new AdvectVelocityFieldPass
            {
                FieldName = Velocity,
                DissipationRate = 0f,
            };
            SolidWallVelocityPass wallAfterAdvect = new SolidWallVelocityPass();
            AdvectScalarPass advectDye = new AdvectScalarPass
            {
                ScalarField = Dye,
                VelocityField = Velocity,
                DissipationRate = 0f,
            };

            touch.Initialize(harness.Context);
            seed.Initialize(harness.Context);
            inject.Initialize(harness.Context);
            vorticity?.Initialize(harness.Context);
            divergence.Initialize(harness.Context);
            zeroMean.Initialize(harness.Context);
            jacobi.Initialize(harness.Context);
            subtract.Initialize(harness.Context);
            wallAfterProject.Initialize(harness.Context);
            advectVelocity.Initialize(harness.Context);
            wallAfterAdvect.Initialize(harness.Context);
            advectDye.Initialize(harness.Context);

            return new DyeInjectVcChain(
                touch, seed, inject, vorticity, divergence, zeroMean, jacobi, subtract,
                wallAfterProject, advectVelocity, wallAfterAdvect, advectDye);
        }

        public void RunFrame(FieldTestHarness harness, float dt)
        {
            harness.RunPass(Touch, dt);
            harness.RunPass(Seed, dt);
            harness.RunPass(Inject, dt);
            if (vorticity != null)
            {
                harness.RunPass(vorticity, dt);
            }

            harness.RunPass(Divergence, dt);
            harness.RunPass(zeroMean, dt);
            harness.RunPass(Jacobi, dt);
            harness.RunPass(Subtract, dt);
            harness.RunPass(WallAfterProject, dt);
            harness.RunPass(AdvectVelocity, dt);
            harness.RunPass(WallAfterAdvect, dt);
            harness.RunPass(AdvectDye, dt);
        }

        public void Dispose()
        {
            zeroMean.Dispose();
        }
    }
}
