using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashP400cPressurePresetTests
{
    private const string FieldPath = "Assets/Effects/Boids_hash_P400c_field.asset";
    private const string PressK1Path = "Assets/Effects/Boids_hash_P400c_field_press_K1.asset";
    private const string PressK3Path = "Assets/Effects/Boids_hash_P400c_field_press_K3.asset";
    private const string PressK1Scene = "Assets/Scenes/Boids_Hash_P400c_Field_Press_K1.unity";
    private const string PressK3Scene = "Assets/Scenes/Boids_Hash_P400c_Field_Press_K3.unity";
    private const float DeltaTime = 1f / 60f;
    private const int Frames = 30;
    private const int ParticlesPerTeam = 200000;
    private const float WorldExtent = 112f;

    private EffectAsset effectCopy;

    [TearDown]
    public void TearDown()
    {
        if (effectCopy != null)
        {
            IReadOnlyList<SimPass> passes = effectCopy.Passes;
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i] is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            UnityEngine.Object.DestroyImmediate(effectCopy);
            effectCopy = null;
        }
    }

    [Test]
    public void PressK1Preset_MatchesPressureConstants()
    {
        AssertPressureChain(Load(PressK1Path), refreshInterval: 1);
    }

    [Test]
    public void PressK3Preset_MatchesPressureConstants()
    {
        AssertPressureChain(Load(PressK3Path), refreshInterval: 3);
    }

    [Test]
    public void PressPresets_MatchFieldTwinExceptRefreshAndPressure()
    {
        AssertPressureTwin(PressK1Path, refreshInterval: 1);
        AssertPressureTwin(PressK3Path, refreshInterval: 3);
    }

    [Test]
    public void Validator_AcceptsBothPresetsWithoutWarnings()
    {
        AssertAccepted(Load(PressK1Path));
        AssertAccepted(Load(PressK3Path));
    }

    [Test]
    public void Scenes_AreEnabledInTheBuild()
    {
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(PressK1Scene), PressK1Scene);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(PressK3Scene), PressK3Scene);
        Assert.IsTrue(IsEnabledInBuild(PressK1Scene), PressK1Scene);
        Assert.IsTrue(IsEnabledInBuild(PressK3Scene), PressK3Scene);
    }

    [Test]
    [Timeout(180000)]
    public void PressChains_ThirtyFrames_StayFiniteAndConserveBothN()
    {
        double k1 = RunChain(PressK1Path);
        double k3 = RunChain(PressK3Path);
        Assert.Pass("secondsK1=" + k1.ToString("G6") + " secondsK3=" + k3.ToString("G6"));
    }

    private static void AssertPressureChain(EffectAsset asset, int refreshInterval)
    {
        Assert.AreEqual(1f, asset.SimulationSpeed);
        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(1, swarm.Seed);
        Assert.AreEqual(2, swarm.Spawns.Count);
        Assert.AreEqual(ParticlesPerTeam, swarm.Spawns[0].Count);
        Assert.AreEqual(ParticlesPerTeam, swarm.Spawns[1].Count);
        IReadOnlyList<SimPass> passes = asset.Passes;
        Assert.AreEqual(9, passes.Count);
        FieldNeighborCollectPass collect = (FieldNeighborCollectPass)passes[0];
        Assert.AreEqual(FieldCollectMode.Scatter, collect.Mode);
        Assert.AreEqual(1f, collect.TexelSize);
        Assert.AreEqual(refreshInterval, collect.RefreshInterval);
        Assert.AreEqual(Vector3.zero, collect.Center);
        Assert.AreEqual(new Vector3(WorldExtent, 0f, WorldExtent), collect.Extents);
        Assert.IsInstanceOf<FieldNeighborConvolvePass>(passes[1]);
        Assert.IsInstanceOf<ClearVelocityPass>(passes[2]);
        Assert.IsInstanceOf<FieldNeighborForcePass>(passes[3]);
        FieldDensityPressurePass pressure = (FieldDensityPressurePass)passes[4];
        Assert.AreEqual(0.25f, pressure.PressureTexel);
        Assert.AreEqual(0.8f, pressure.Weight);
        Assert.AreEqual(25f, pressure.G0);
        Assert.IsTrue(pressure.Jitter);
        Assert.AreEqual(1, pressure.JitterSeed);
        Assert.AreEqual(collect.Center, pressure.Center);
        Assert.AreEqual(collect.Extents, pressure.Extents);
        Assert.IsInstanceOf<TeamHeadingSteerPass>(passes[5]);
        Assert.IsInstanceOf<IntegratePass>(passes[6]);
        Assert.IsInstanceOf<BoxBoundsPass>(passes[7]);
        Assert.IsInstanceOf<HeadingToValuePass>(passes[8]);
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(
            pressure.Center, pressure.Extents, pressure.PressureTexel);
        Assert.AreEqual(new Vector2Int(896, 896), layout.Resolution);
        Assert.LessOrEqual(layout.CellCount, FieldDensityPressurePass.MaxCells);
    }

    private static void AssertPressureTwin(string path, int refreshInterval)
    {
        EffectAsset field = Load(FieldPath);
        EffectAsset twin = Load(path);
        Assert.AreEqual(field.SimulationSpeed, twin.SimulationSpeed);
        Assert.AreEqual(field.ParticleSize, twin.ParticleSize);
        SwarmSource fieldSwarm = (SwarmSource)field.ResolveSource();
        SwarmSource twinSwarm = (SwarmSource)twin.ResolveSource();
        Assert.AreEqual(fieldSwarm.Seed, twinSwarm.Seed);
        Assert.AreEqual(fieldSwarm.Spawns.Count, twinSwarm.Spawns.Count);
        for (int i = 0; i < fieldSwarm.Spawns.Count; i++)
        {
            Assert.AreEqual(fieldSwarm.Spawns[i].TeamIndex, twinSwarm.Spawns[i].TeamIndex);
            Assert.AreEqual(fieldSwarm.Spawns[i].Count, twinSwarm.Spawns[i].Count);
            Assert.AreEqual(fieldSwarm.Spawns[i].Center, twinSwarm.Spawns[i].Center);
            Assert.AreEqual(fieldSwarm.Spawns[i].Radius, twinSwarm.Spawns[i].Radius);
        }

        Assert.AreEqual(field.Teams.Count, twin.Teams.Count);
        Assert.AreEqual(field.Passes.Count + 1, twin.Passes.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(field.Passes[i].GetType(), twin.Passes[i].GetType());
        }

        Assert.IsInstanceOf<FieldDensityPressurePass>(twin.Passes[4]);
        for (int i = 4; i < field.Passes.Count; i++)
        {
            Assert.AreEqual(field.Passes[i].GetType(), twin.Passes[i + 1].GetType());
        }

        FieldNeighborCollectPass twinCollect = (FieldNeighborCollectPass)twin.Passes[0];
        Assert.AreEqual(refreshInterval, twinCollect.RefreshInterval);
        Assert.AreEqual(FieldCollectMode.Scatter, twinCollect.Mode);
        Assert.AreEqual(1f, twinCollect.TexelSize);
    }

    private double RunChain(string path)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        EffectAsset asset = InstantiateAsset(path);
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "P400cPressureChain" };
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
                Assert.GreaterOrEqual(position[i].x, -WorldExtent - 1e-3f);
                Assert.LessOrEqual(position[i].x, WorldExtent + 1e-3f);
                Assert.GreaterOrEqual(position[i].z, -WorldExtent - 1e-3f);
                Assert.LessOrEqual(position[i].z, WorldExtent + 1e-3f);
            }

            FieldNeighborCollectPass collect = FindCollect(asset.Passes);
            Assert.AreEqual(ParticlesPerTeam, SumCollectN(collect, 0), 0.1d);
            Assert.AreEqual(ParticlesPerTeam, SumCollectN(collect, 1), 0.1d);
            FieldDensityPressurePass pressure = FindPressure(asset.Passes);
            Assert.AreEqual(ParticlesPerTeam * 16384L, SumPressureN(pressure, 0));
            Assert.AreEqual(ParticlesPerTeam * 16384L, SumPressureN(pressure, 1));
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
        Assert.IsEmpty(warnings);
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

    private static EffectAsset Load(string path)
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        Assert.IsNotNull(asset, path + " must exist. Create it with Tools/M3D/Create Boids Hash P400c Pressure Effects.");
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
        effectCopy.name = "Boids_hash_P400c_press_TestCopy";
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
}
