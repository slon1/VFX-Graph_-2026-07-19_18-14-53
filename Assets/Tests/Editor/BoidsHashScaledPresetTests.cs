using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[TestFixture]
public class BoidsHashScaledPresetTests
{
    private const float DeltaTime = 1f / 60f;
    private const int Frames = 30;
    private const string CreateMenuHint = "Tools/M3D/Create Boids Hash Scaled Effects";

    private static readonly ScaleCase[] CCases =
    {
        new ScaleCase("56k", 0.375f, 28125, 42f),
        new ScaleCase("100k", 0.5f, 50000, 56f),
        new ScaleCase("156k", 0.625f, 78125, 70f),
        new ScaleCase("225k", 0.75f, 112500, 84f),
        new ScaleCase("306k", 0.875f, 153125, 98f),
    };

    private static readonly FixedCase[] WCases =
    {
        new FixedCase("100k", 50000),
        new FixedCase("200k", 100000),
        new FixedCase("300k", 150000),
    };

    private EffectAsset effectCopy;

    [TearDown]
    public void TearDown()
    {
        if (effectCopy != null)
        {
            DisposePasses(effectCopy);
            UnityEngine.Object.DestroyImmediate(effectCopy);
            effectCopy = null;
        }
    }

    [Test]
    public void FamilyC_Constants_MatchScaleTable()
    {
        for (int i = 0; i < CCases.Length; i++)
        {
            ScaleCase c = CCases[i];
            AssertBaseConstants(Load(BaseAsset(c.Label)), c);
            AssertPressConstants(Load(PressAsset(c.Label)), c.PerTeam, c.Extent, particleSize: 0.5f);
            AssertSceneCamera(BaseScene(c.Label), c.Extent);
            AssertSceneCamera(PressScene(c.Label), c.Extent);
        }
    }

    [Test]
    public void FamilyW_Constants_MatchFixedWorld()
    {
        for (int i = 0; i < WCases.Length; i++)
        {
            FixedCase w = WCases[i];
            AssertPressConstants(Load(WAsset(w.Label)), w.PerTeam, 112f, particleSize: 0.5f);
            AssertSceneCamera(WScene(w.Label), 112f);
        }
    }

    [Test]
    public void SizeProbe_ParticleSizes_Match()
    {
        AssertPressConstants(Load(SizeAsset("s025")), 200000, 112f, particleSize: 0.25f);
        AssertPressConstants(Load(SizeAsset("s0125")), 200000, 112f, particleSize: 0.125f);
        AssertSceneCamera(SizeScene("S025"), 112f);
        AssertSceneCamera(SizeScene("S0125"), 112f);
    }

    [Test]
    public void Validator_AcceptsEveryScaledAssetWithoutWarnings()
    {
        for (int i = 0; i < CCases.Length; i++)
        {
            AssertAccepted(Load(BaseAsset(CCases[i].Label)));
            AssertAccepted(Load(PressAsset(CCases[i].Label)));
        }

        for (int i = 0; i < WCases.Length; i++)
        {
            AssertAccepted(Load(WAsset(WCases[i].Label)));
        }

        AssertAccepted(Load(SizeAsset("s025")));
        AssertAccepted(Load(SizeAsset("s0125")));
    }

    [Test]
    public void Scenes_AreEnabledInTheBuild()
    {
        for (int i = 0; i < CCases.Length; i++)
        {
            AssertEnabled(BaseScene(CCases[i].Label));
            AssertEnabled(PressScene(CCases[i].Label));
        }

        for (int i = 0; i < WCases.Length; i++)
        {
            AssertEnabled(WScene(WCases[i].Label));
        }

        AssertEnabled(SizeScene("S025"));
        AssertEnabled(SizeScene("S0125"));
    }

    [Test]
    [Timeout(180000)]
    public void PressChains_MinMaxAndW300_ThirtyFrames_StayFiniteAndConserveBothN()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        double s56 = RunPressChain(PressAsset("56k"), 28125, 42f);
        double s306 = RunPressChain(PressAsset("306k"), 153125, 98f);
        double w300 = RunPressChain(WAsset("300k"), 150000, 112f);
        Assert.Pass(
            "seconds56k=" + s56.ToString("G6")
            + " seconds306k=" + s306.ToString("G6")
            + " secondsW300k=" + w300.ToString("G6"));
    }

    private static void AssertBaseConstants(EffectAsset asset, ScaleCase c)
    {
        Assert.AreEqual(1f, asset.SimulationSpeed);
        Assert.AreEqual(0.5f, asset.ParticleSize);
        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(c.PerTeam, swarm.Spawns[0].Count);
        Assert.AreEqual(c.PerTeam, swarm.Spawns[1].Count);
        Assert.AreEqual(new Vector2(-55f * c.Scale, 0f), swarm.Spawns[0].Center);
        Assert.AreEqual(new Vector2(55f * c.Scale, 0f), swarm.Spawns[1].Center);
        Assert.AreEqual(45f * c.Scale, swarm.Spawns[0].Radius);
        Assert.AreEqual(45f * c.Scale, swarm.Spawns[1].Radius);
        IReadOnlyList<SimPass> passes = asset.Passes;
        Assert.AreEqual(7, passes.Count);
        BuildSpatialHashPass hash = (BuildSpatialHashPass)passes[0];
        BoxBoundsPass box = (BoxBoundsPass)passes[5];
        Vector3 extents = new Vector3(c.Extent, 0f, c.Extent);
        Assert.AreEqual(extents, hash.Extents);
        Assert.AreEqual(extents, box.Extents);
        Assert.AreEqual(48, ((BoidNeighborForcePass)passes[2]).MaxNeighbors);
    }

    private static void AssertPressConstants(EffectAsset asset, int perTeam, float extent, float particleSize)
    {
        Assert.AreEqual(1f, asset.SimulationSpeed);
        Assert.AreEqual(particleSize, asset.ParticleSize);
        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(perTeam, swarm.Spawns[0].Count);
        Assert.AreEqual(perTeam, swarm.Spawns[1].Count);
        IReadOnlyList<SimPass> passes = asset.Passes;
        Assert.AreEqual(9, passes.Count);
        FieldNeighborCollectPass collect = (FieldNeighborCollectPass)passes[0];
        Assert.AreEqual(FieldCollectMode.Scatter, collect.Mode);
        Assert.AreEqual(1f, collect.TexelSize);
        Assert.AreEqual(3, collect.RefreshInterval);
        Vector3 extents = new Vector3(extent, 0f, extent);
        Assert.AreEqual(extents, collect.Extents);
        FieldDensityPressurePass pressure = (FieldDensityPressurePass)passes[4];
        Assert.AreEqual(0.25f, pressure.PressureTexel);
        Assert.AreEqual(0.8f, pressure.Weight);
        Assert.AreEqual(25f, pressure.G0);
        Assert.IsTrue(pressure.Jitter);
        Assert.AreEqual(1, pressure.JitterSeed);
        Assert.AreEqual(collect.Extents, pressure.Extents);
        Assert.AreEqual(extents, ((BoxBoundsPass)passes[7]).Extents);
    }

    private static void AssertSceneCamera(string scenePath, float cameraSize)
    {
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath), scenePath);
        string previous = SceneManager.GetActiveScene().path;
        try
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            Camera camera = Camera.main;
            Assert.IsNotNull(camera, scenePath + " camera");
            Assert.IsTrue(camera.orthographic, scenePath);
            Assert.AreEqual(cameraSize, camera.orthographicSize, 1e-4f, scenePath);
            Assert.IsNotNull(camera.GetComponent<FPSDisplay>(), scenePath + " FPSDisplay");
            Assert.IsTrue(scene.IsValid());
        }
        finally
        {
            if (!string.IsNullOrEmpty(previous))
            {
                EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
            }
        }
    }

    private double RunPressChain(string path, int perTeam, float worldExtent)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        EffectAsset asset = InstantiateAsset(path);
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "ScaledPressChain" };
        GraphicsBuffer teams = null;
        try
        {
            SwarmSource swarm = (SwarmSource)asset.ResolveSource();
            swarm.Setup(particles);
            RegisterLikeWorld(particles, asset);
            Graphics.CopyBuffer(
                particles.Get(BuiltinAttributes.RestPosition),
                particles.Get(BuiltinAttributes.Position));
            fields.Allocate(asset.Fields, cmd);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            teams = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            TeamParams.Upload(teams, asset.Teams);
            SimContext context = new SimContext(particles, fields, LoadLibrary(), null);
            context.Cmd = cmd;
            context.Teams = teams;
            IReadOnlyList<SimPass> passes = asset.Passes;
            for (int i = 0; i < passes.Count; i++)
            {
                passes[i].Initialize(context);
            }

            for (int frame = 0; frame < Frames; frame++)
            {
                cmd.Clear();
                for (int i = 0; i < passes.Count; i++)
                {
                    passes[i].Execute(context, DeltaTime);
                }

                Graphics.ExecuteCommandBuffer(cmd);
            }

            int count = particles.Count;
            Vector3[] position = new Vector3[count];
            Vector3[] heading = new Vector3[count];
            particles.Get(BuiltinAttributes.Position).GetData(position);
            particles.Get(BuiltinAttributes.Heading).GetData(heading);
            for (int i = 0; i < count; i++)
            {
                AssertFinite(position[i]);
                AssertFinite(heading[i]);
                Assert.GreaterOrEqual(position[i].x, -worldExtent - 1e-3f);
                Assert.LessOrEqual(position[i].x, worldExtent + 1e-3f);
                Assert.GreaterOrEqual(position[i].z, -worldExtent - 1e-3f);
                Assert.LessOrEqual(position[i].z, worldExtent + 1e-3f);
            }

            FieldNeighborCollectPass collect = FindCollect(asset.Passes);
            Assert.AreEqual(perTeam, SumCollectN(collect, 0), 0.1d);
            Assert.AreEqual(perTeam, SumCollectN(collect, 1), 0.1d);
            FieldDensityPressurePass pressure = FindPressure(asset.Passes);
            Assert.AreEqual(perTeam * 16384L, SumPressureN(pressure, 0));
            Assert.AreEqual(perTeam * 16384L, SumPressureN(pressure, 1));
            watch.Stop();
            return watch.ElapsedMilliseconds / 1000.0;
        }
        finally
        {
            DisposePasses(asset);
            teams?.Dispose();
            cmd.Release();
            fields.Dispose();
            particles.Dispose();
        }
    }

    private static void AssertAccepted(EffectAsset asset)
    {
        IReadOnlyList<string> warnings = SpatialHashValidator.Validate(asset.Passes, asset);
        Assert.IsEmpty(warnings, string.Join("; ", warnings));
    }

    private static void AssertEnabled(string scenePath)
    {
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath), scenePath);
        Assert.IsTrue(IsEnabledInBuild(scenePath), scenePath);
    }

    private static bool IsEnabledInBuild(string scenePath)
    {
        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
        for (int i = 0; i < scenes.Length; i++)
        {
            if (scenes[i].path == scenePath && scenes[i].enabled)
            {
                return true;
            }
        }

        return false;
    }

    private static double SumCollectN(FieldNeighborCollectPass collect, int team)
    {
        int cells = collect.Field.Layout.CellCount;
        int channels = FieldNeighborSet.ChannelsPerTexel;
        uint[] bits = new uint[cells * channels];
        collect.Field.Accum.GetData(bits);
        double sum = 0d;
        for (int i = team * 3; i < bits.Length; i += channels)
        {
            sum += unchecked((int)bits[i]) / (double)FieldNeighborSet.FixedPointScale;
        }

        return sum;
    }

    private static long SumPressureN(FieldDensityPressurePass pressure, int team)
    {
        uint[] bits = new uint[pressure.Density.count];
        pressure.Density.GetData(bits);
        long sum = 0;
        for (int cell = 0; cell < bits.Length / 2; cell++)
        {
            sum += bits[cell * 2 + team];
        }

        return sum;
    }

    private static FieldNeighborCollectPass FindCollect(IReadOnlyList<SimPass> passes)
    {
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is FieldNeighborCollectPass collect)
            {
                return collect;
            }
        }

        Assert.Fail("collect missing");
        return null;
    }

    private static FieldDensityPressurePass FindPressure(IReadOnlyList<SimPass> passes)
    {
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is FieldDensityPressurePass pressure)
            {
                return pressure;
            }
        }

        Assert.Fail("pressure missing");
        return null;
    }

    private static string BaseAsset(string label) =>
        "Assets/Effects/Boids_hash_P" + label + "_base.asset";

    private static string PressAsset(string label) =>
        "Assets/Effects/Boids_hash_P" + label + "_press_K3.asset";

    private static string BaseScene(string label) =>
        "Assets/Scenes/Boids_Hash_P" + label + "_Base.unity";

    private static string PressScene(string label) =>
        "Assets/Scenes/Boids_Hash_P" + label + "_Press_K3.unity";

    private static string WAsset(string label) =>
        "Assets/Effects/Boids_hash_W224_" + label + "_press_K3.asset";

    private static string WScene(string label) =>
        "Assets/Scenes/Boids_Hash_W224_" + label + "_Press_K3.unity";

    private static string SizeAsset(string suffix) =>
        "Assets/Effects/Boids_hash_P400c_field_press_K3_" + suffix + ".asset";

    private static string SizeScene(string suffix) =>
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K3_" + suffix + ".unity";

    private static EffectAsset Load(string path)
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        Assert.IsNotNull(asset, path + " must exist. Create it with " + CreateMenuHint + ".");
        return asset;
    }

    private EffectAsset InstantiateAsset(string path)
    {
        if (effectCopy != null)
        {
            UnityEngine.Object.DestroyImmediate(effectCopy);
            effectCopy = null;
        }

        effectCopy = UnityEngine.Object.Instantiate(Load(path));
        effectCopy.name = "Boids_hash_scaled_TestCopy";
        return effectCopy;
    }

    private static void DisposePasses(EffectAsset asset)
    {
        IReadOnlyList<SimPass> passes = asset.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static ComputeShader[] LoadLibrary()
    {
        string[] paths =
        {
            "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute",
            "Assets/Shaders/GPU/Passes/HashDebugPasses.compute",
            "Assets/Shaders/GPU/Passes/DynamicsPasses.compute",
            "Assets/Shaders/GPU/Passes/BoidsPasses.compute",
            "Assets/Shaders/GPU/Passes/FieldNeighborPasses.compute",
            "Assets/Shaders/GPU/Passes/FieldDensityPressure.compute",
        };
        ComputeShader[] shaders = new ComputeShader[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            shaders[i] = AssetDatabase.LoadAssetAtPath<ComputeShader>(paths[i]);
            Assert.IsNotNull(shaders[i], paths[i]);
        }

        return shaders;
    }

    private static void RegisterLikeWorld(ParticleSet particles, EffectAsset asset)
    {
        IReadOnlyList<SimPass> passes = asset.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            RegisterMissing(particles, passes[i].Reads);
            RegisterMissing(particles, passes[i].Writes);
        }
    }

    private static void RegisterMissing(ParticleSet particles, IReadOnlyList<AttributeId> ids)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (!particles.Schema.Has(ids[i]))
            {
                GraphicsBuffer buffer = particles.RegisterAttribute(ids[i]);
                buffer.SetData(new byte[buffer.count * buffer.stride]);
            }
        }
    }

    private static void AssertFinite(Vector3 value)
    {
        Assert.IsFalse(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z));
        Assert.IsFalse(float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
    }

    private readonly struct ScaleCase
    {
        public readonly string Label;
        public readonly float Scale;
        public readonly int PerTeam;
        public readonly float Extent;

        public ScaleCase(string label, float scale, int perTeam, float extent)
        {
            Label = label;
            Scale = scale;
            PerTeam = perTeam;
            Extent = extent;
        }
    }

    private readonly struct FixedCase
    {
        public readonly string Label;
        public readonly int PerTeam;

        public FixedCase(string label, int perTeam)
        {
            Label = label;
            PerTeam = perTeam;
        }
    }
}
