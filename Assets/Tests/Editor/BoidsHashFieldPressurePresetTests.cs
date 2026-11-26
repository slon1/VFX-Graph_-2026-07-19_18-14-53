using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashFieldPressurePresetTests
{
    private const string FieldPath = "Assets/Effects/Boids_hash_2teams_field.asset";
    private const string Press08Path = "Assets/Effects/Boids_hash_2teams_field_press08.asset";
    private const string Press10Path = "Assets/Effects/Boids_hash_2teams_field_press10.asset";
    private const string PressG25Path = "Assets/Effects/Boids_hash_2teams_field_press_g25.asset";
    private const string PressMixPath = "Assets/Effects/Boids_hash_2teams_field_press_mix.asset";
    private const string Press08Scene = "Assets/Scenes/Boids_Hash_2Teams_Field_P08.unity";
    private const string Press10Scene = "Assets/Scenes/Boids_Hash_2Teams_Field_P10.unity";
    private const string PressG25Scene = "Assets/Scenes/Boids_Hash_2Teams_Field_P_G25.unity";
    private const string PressMixScene = "Assets/Scenes/Boids_Hash_2Teams_Field_P_MIX.unity";
    private const float DeltaTime = 1f / 60f;
    private const int Frames = 30;
    private const int WideFrames = 60;
    private const int ParticlesPerTeam = 1500;

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
    public void Press08Preset_MatchesPressureConstants()
    {
        AssertPressureChain(Load(Press08Path), 0.8f, 40f);
    }

    [Test]
    public void Press10Preset_MatchesPressureConstants()
    {
        AssertPressureChain(Load(Press10Path), 1.0f, 40f);
    }

    [Test]
    public void PressPresets_MatchFieldTwinExceptPressurePass()
    {
        AssertPressureTwin(Press08Path, 0.8f, 40f);
        AssertPressureTwin(Press10Path, 1.0f, 40f);
    }

    [Test]
    public void Validator_AcceptsBothPresetsWithoutWarnings()
    {
        AssertAccepted(Load(Press08Path));
        AssertAccepted(Load(Press10Path));
    }

    [Test]
    public void Scenes_AreEnabledInTheBuild()
    {
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(Press08Scene), Press08Scene);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(Press10Scene), Press10Scene);
        Assert.IsTrue(IsEnabledInBuild(Press08Scene), Press08Scene);
        Assert.IsTrue(IsEnabledInBuild(Press10Scene), Press10Scene);
    }

    [Test]
    public void PressG25Preset_MatchesPressureConstants()
    {
        AssertPressureChain(Load(PressG25Path), 0.8f, 25f);
    }

    [Test]
    public void PressMixPreset_MatchesPressureConstants()
    {
        AssertPressureChain(Load(PressMixPath), 2.5f, 12f);
    }

    [Test]
    public void OwnerPresets_MatchFieldTwinExceptPressurePass()
    {
        AssertPressureTwin(PressG25Path, 0.8f, 25f);
        AssertPressureTwin(PressMixPath, 2.5f, 12f);
    }

    [Test]
    public void Validator_AcceptsOwnerPresetsWithoutWarnings()
    {
        AssertAccepted(Load(PressG25Path));
        AssertAccepted(Load(PressMixPath));
    }

    [Test]
    public void OwnerScenes_AreEnabledInTheBuild()
    {
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(PressG25Scene), PressG25Scene);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(PressMixScene), PressMixScene);
        Assert.IsTrue(IsEnabledInBuild(PressG25Scene), PressG25Scene);
        Assert.IsTrue(IsEnabledInBuild(PressMixScene), PressMixScene);
    }

    [Test]
    [Timeout(180000)]
    public void PressChains_ThirtyFrames_StayFiniteAndConserveBothN()
    {
        RunChain(Press08Path);
        RunChain(Press10Path);
    }

    [Test]
    [Timeout(180000)]
    public void OwnerPressChains_ThirtyFrames_StayFiniteAndConserveBothN()
    {
        RunChain(PressG25Path);
        RunChain(PressMixPath);
    }

    [Test]
    [Timeout(180000)]
    public void Press08_TwoRunsOnSameInstance_MatchPositionsWithinOneMicro()
    {
        AssertRepro(Press08Path);
    }

    [Test]
    [Timeout(180000)]
    public void IntervalsTwo_TwoRunsOnSameInstance_MatchPositionsWithinOneMicro()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset(PressG25Path);
        FieldDensityPressurePass pressure = FindPressure(asset.Passes);
        pressure.GridUpdateInterval = 2;
        pressure.ForceInterleave = 2;
        Vector3[] first = RunPositions(asset);
        Vector3[] second = RunPositions(asset);
        for (int i = 0; i < first.Length; i++)
        {
            Assert.AreEqual(first[i].x, second[i].x, 1e-6f, "x " + i);
            Assert.AreEqual(first[i].y, second[i].y, 1e-6f, "y " + i);
            Assert.AreEqual(first[i].z, second[i].z, 1e-6f, "z " + i);
        }
    }

    [Test]
    [Timeout(180000)]
    public void PressG25_TwoRunsOnSameInstance_MatchPositionsWithinOneMicro()
    {
        AssertRepro(PressG25Path);
    }

    [Test]
    [Timeout(180000)]
    public void PressMix_TwoRunsOnSameInstance_MatchPositionsWithinOneMicro()
    {
        AssertRepro(PressMixPath);
    }

    [Test]
    [Timeout(180000)]
    public void WideParams_Weight3_G0_12_SixtyFrames_StayFinite()
    {
        RunWideParams(3f, 12f);
    }

    [Test]
    [Timeout(180000)]
    public void WideParams_Weight10_G0_1eMinus3_SixtyFrames_StayFinite()
    {
        RunWideParams(10f, 1e-3f);
    }

    private void AssertRepro(string path)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset(path);
        Vector3[] first = RunPositions(asset);
        Vector3[] second = RunPositions(asset);
        double max = 0.0;
        int worst = 0;
        for (int i = 0; i < first.Length; i++)
        {
            double dx = Math.Abs(first[i].x - second[i].x);
            double dy = Math.Abs(first[i].y - second[i].y);
            double dz = Math.Abs(first[i].z - second[i].z);
            double local = Math.Max(dx, Math.Max(dy, dz));
            if (local > max)
            {
                max = local;
                worst = i;
            }

            Assert.AreEqual(first[i].x, second[i].x, 1e-6f, "x " + i);
            Assert.AreEqual(first[i].y, second[i].y, 1e-6f, "y " + i);
            Assert.AreEqual(first[i].z, second[i].z, 1e-6f, "z " + i);
        }

        Assert.Pass("maxAbs=" + max.ToString("G6") + " worst=" + worst);
    }

    private void RunWideParams(float weight, float g0)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset(Press08Path);
        FieldDensityPressurePass pressure = FindPressure(asset.Passes);
        pressure.Weight = weight;
        pressure.G0 = g0;
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "PressureWideParams" };
        GraphicsBuffer teams = null;
        try
        {
            SetupAndRun(asset, particles, fields, cmd, out teams, WideFrames);
            int count = particles.Count;
            Vector3[] position = new Vector3[count];
            Vector3[] heading = new Vector3[count];
            particles.Get(BuiltinAttributes.Position).GetData(position);
            particles.Get(BuiltinAttributes.Heading).GetData(heading);
            for (int i = 0; i < count; i++)
            {
                AssertFinite(position[i]);
                AssertFinite(heading[i]);
                Assert.GreaterOrEqual(position[i].x, -45f - 1e-3f);
                Assert.LessOrEqual(position[i].x, 45f + 1e-3f);
                Assert.GreaterOrEqual(position[i].z, -45f - 1e-3f);
                Assert.LessOrEqual(position[i].z, 45f + 1e-3f);
            }

            FieldNeighborCollectPass collect = FindCollect(asset.Passes);
            Assert.AreEqual(ParticlesPerTeam, SumCollectN(collect, 0), 0.1d);
            Assert.AreEqual(ParticlesPerTeam, SumCollectN(collect, 1), 0.1d);
            Assert.AreEqual(ParticlesPerTeam * 16384L, SumPressureN(pressure, 0));
            Assert.AreEqual(ParticlesPerTeam * 16384L, SumPressureN(pressure, 1));
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

    [Test]
    public void Initialize_ResetsFrameIndexSoFirstShiftIsZero()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        var positions = new[] { new Vector2(1f, 2f) };
        using (var particles = new ParticleSet())
        using (var fields = new FieldSet())
        using (var cmd = new CommandBuffer { name = "PressureFrameReset" })
        {
            particles.EnsureCapacity(1);
            particles.RegisterAttribute(BuiltinAttributes.Position);
            particles.RegisterAttribute(BuiltinAttributes.Heading);
            particles.RegisterAttribute(BuiltinAttributes.TeamId);
            particles.RegisterAttribute(BuiltinAttributes.Velocity);
            particles.Get(BuiltinAttributes.Position).SetData(new[] { new Vector3(1f, 0f, 2f) });
            particles.Get(BuiltinAttributes.Heading).SetData(new[] { new Vector3(1f, 0f, 0f) });
            particles.Get(BuiltinAttributes.TeamId).SetData(new uint[] { 0 });
            particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[1]);
            GraphicsBuffer teams = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            var slots = new TeamParams[TeamProfile.MaxTeams];
            slots[0].Radii = new Vector4(3f, 3f, 3f, 4f);
            slots[1].Radii = new Vector4(3f, 3f, 3f, 4f);
            teams.SetData(slots);
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/Shaders/GPU/Passes/FieldDensityPressure.compute");
            Assert.IsNotNull(shader);
            var pass = new FieldDensityPressurePass
            {
                Center = Vector3.zero,
                Extents = new Vector3(45f, 0f, 45f),
                PressureTexel = 0.25f,
                Weight = 0.8f,
                G0 = 40f,
                Jitter = true,
                JitterSeed = 1,
            };
            SimContext context = new SimContext(particles, fields, new[] { shader }, null);
            context.Cmd = cmd;
            context.Teams = teams;
            pass.Initialize(context);
            for (int i = 0; i < 5; i++)
            {
                cmd.Clear();
                pass.Execute(context, DeltaTime);
                Graphics.ExecuteCommandBuffer(cmd);
            }

            Assert.Greater(pass.FrameIndex, 0);
            pass.Initialize(context);
            cmd.Clear();
            particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[1]);
            pass.Execute(context, DeltaTime);
            Graphics.ExecuteCommandBuffer(cmd);
            Vector2 expected = FieldDensityPressurePass.ShiftOf(0, 1, pass.Layout.CellSize, true);
            Assert.AreEqual(expected, pass.LastShift);
            pass.Dispose();
            teams.Dispose();
        }
    }

    internal static void AssertPressureChain(EffectAsset asset, float weight, float g0)
    {
        Assert.AreEqual(1f, asset.SimulationSpeed);
        Assert.AreEqual(0.5f, asset.ParticleSize);
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
        Assert.AreEqual(3, collect.RefreshInterval);
        Assert.IsInstanceOf<FieldNeighborConvolvePass>(passes[1]);
        Assert.IsInstanceOf<ClearVelocityPass>(passes[2]);
        Assert.IsInstanceOf<FieldNeighborForcePass>(passes[3]);
        FieldDensityPressurePass pressure = (FieldDensityPressurePass)passes[4];
        Assert.AreEqual(0.25f, pressure.PressureTexel);
        Assert.AreEqual(weight, pressure.Weight);
        Assert.AreEqual(g0, pressure.G0);
        Assert.IsTrue(pressure.Jitter);
        Assert.AreEqual(1, pressure.JitterSeed);
        Assert.AreEqual(collect.Center, pressure.Center);
        Assert.AreEqual(collect.Extents, pressure.Extents);
        Assert.IsInstanceOf<TeamHeadingSteerPass>(passes[5]);
        Assert.IsInstanceOf<IntegratePass>(passes[6]);
        Assert.IsInstanceOf<BoxBoundsPass>(passes[7]);
        Assert.IsInstanceOf<HeadingToValuePass>(passes[8]);
    }

    internal static void AssertPressureTwin(string path, float weight, float g0)
    {
        EffectAsset field = Load(FieldPath);
        EffectAsset twin = Load(path);
        Assert.AreEqual(field.SimulationSpeed, twin.SimulationSpeed);
        Assert.AreEqual(field.ParticleSize, twin.ParticleSize);
        SwarmSource fieldSwarm = (SwarmSource)field.ResolveSource();
        SwarmSource twinSwarm = (SwarmSource)twin.ResolveSource();
        Assert.AreEqual(fieldSwarm.Seed, twinSwarm.Seed);
        Assert.AreEqual(fieldSwarm.JitterDegrees, twinSwarm.JitterDegrees);
        Assert.AreEqual(fieldSwarm.Spawns.Count, twinSwarm.Spawns.Count);
        for (int i = 0; i < fieldSwarm.Spawns.Count; i++)
        {
            Assert.AreEqual(fieldSwarm.Spawns[i].TeamIndex, twinSwarm.Spawns[i].TeamIndex);
            Assert.AreEqual(fieldSwarm.Spawns[i].Count, twinSwarm.Spawns[i].Count);
            Assert.AreEqual(fieldSwarm.Spawns[i].Center, twinSwarm.Spawns[i].Center);
            Assert.AreEqual(fieldSwarm.Spawns[i].Radius, twinSwarm.Spawns[i].Radius);
            Assert.AreEqual(fieldSwarm.Spawns[i].InitialDirection, twinSwarm.Spawns[i].InitialDirection);
        }

        Assert.AreEqual(field.Teams.Count, twin.Teams.Count);
        for (int i = 0; i < field.Teams.Count; i++)
        {
            Assert.AreEqual(field.Teams[i].Name, twin.Teams[i].Name);
            Assert.AreEqual(field.Teams[i].SeparationRadius, twin.Teams[i].SeparationRadius);
            Assert.AreEqual(field.Teams[i].AlignmentRadius, twin.Teams[i].AlignmentRadius);
            Assert.AreEqual(field.Teams[i].CohesionRadius, twin.Teams[i].CohesionRadius);
            Assert.AreEqual(field.Teams[i].InterGroupSeparationMultiplier, twin.Teams[i].InterGroupSeparationMultiplier);
            Assert.AreEqual(field.Teams[i].SeparationWeight, twin.Teams[i].SeparationWeight);
            Assert.AreEqual(field.Teams[i].AlignmentWeight, twin.Teams[i].AlignmentWeight);
            Assert.AreEqual(field.Teams[i].CohesionWeight, twin.Teams[i].CohesionWeight);
            Assert.AreEqual(field.Teams[i].Cruise, twin.Teams[i].Cruise);
            Assert.AreEqual(field.Teams[i].Turn, twin.Teams[i].Turn);
        }

        Assert.AreEqual(field.Passes.Count + 1, twin.Passes.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(field.Passes[i].GetType(), twin.Passes[i].GetType());
        }

        Assert.IsInstanceOf<FieldDensityPressurePass>(twin.Passes[4]);
        FieldDensityPressurePass pressure = (FieldDensityPressurePass)twin.Passes[4];
        Assert.AreEqual(weight, pressure.Weight);
        Assert.AreEqual(g0, pressure.G0);
        for (int i = 4; i < field.Passes.Count; i++)
        {
            Assert.AreEqual(field.Passes[i].GetType(), twin.Passes[i + 1].GetType());
        }

        FieldNeighborCollectPass fieldCollect = (FieldNeighborCollectPass)field.Passes[0];
        FieldNeighborCollectPass twinCollect = (FieldNeighborCollectPass)twin.Passes[0];
        Assert.AreEqual(fieldCollect.Mode, twinCollect.Mode);
        Assert.AreEqual(fieldCollect.RefreshInterval, twinCollect.RefreshInterval);
        Assert.AreEqual(fieldCollect.TexelSize, twinCollect.TexelSize);
        Assert.AreEqual(fieldCollect.Center, twinCollect.Center);
        Assert.AreEqual(fieldCollect.Extents, twinCollect.Extents);
    }

    private void RunChain(string path)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset(path);
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "PressurePresetChain" };
        GraphicsBuffer teams = null;
        try
        {
            SetupAndRun(asset, particles, fields, cmd, out teams, Frames);
            int count = particles.Count;
            Vector3[] position = new Vector3[count];
            Vector3[] heading = new Vector3[count];
            uint[] teamId = new uint[count];
            particles.Get(BuiltinAttributes.Position).GetData(position);
            particles.Get(BuiltinAttributes.Heading).GetData(heading);
            particles.Get(BuiltinAttributes.TeamId).GetData(teamId);
            int fire = 0;
            int ice = 0;
            for (int i = 0; i < count; i++)
            {
                AssertFinite(position[i]);
                AssertFinite(heading[i]);
                Assert.GreaterOrEqual(position[i].x, -45f - 1e-3f);
                Assert.LessOrEqual(position[i].x, 45f + 1e-3f);
                Assert.GreaterOrEqual(position[i].z, -45f - 1e-3f);
                Assert.LessOrEqual(position[i].z, 45f + 1e-3f);
                if (teamId[i] == 0)
                {
                    fire++;
                }
                else if (teamId[i] == 1)
                {
                    ice++;
                }
            }

            Assert.AreEqual(ParticlesPerTeam, fire);
            Assert.AreEqual(ParticlesPerTeam, ice);
            FieldNeighborCollectPass collect = FindCollect(asset.Passes);
            Assert.AreEqual(ParticlesPerTeam, SumCollectN(collect, 0), 0.1d);
            Assert.AreEqual(ParticlesPerTeam, SumCollectN(collect, 1), 0.1d);
            FieldDensityPressurePass pressure = FindPressure(asset.Passes);
            Assert.AreEqual(ParticlesPerTeam * 16384L, SumPressureN(pressure, 0));
            Assert.AreEqual(ParticlesPerTeam * 16384L, SumPressureN(pressure, 1));
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

    private Vector3[] RunPositions(EffectAsset asset)
    {
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "PressureRepro" };
        GraphicsBuffer teams = null;
        try
        {
            SetupAndRun(asset, particles, fields, cmd, out teams, Frames);
            var position = new Vector3[particles.Count];
            particles.Get(BuiltinAttributes.Position).GetData(position);
            return position;
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

    private static void SetupAndRun(
        EffectAsset asset,
        ParticleSet particles,
        FieldSet fields,
        CommandBuffer cmd,
        out GraphicsBuffer teams,
        int frames)
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

        for (int frame = 0; frame < frames; frame++)
        {
            cmd.Clear();
            for (int i = 0; i < passes.Count; i++)
            {
                passes[i].Execute(context, DeltaTime);
            }

            Graphics.ExecuteCommandBuffer(cmd);
        }
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
        Assert.IsNotNull(asset, path + " must exist. Create it with Tools/M3D/Create Boids Hash Field Pressure Effects.");
        return asset;
    }

    private EffectAsset InstantiateAsset(string path)
    {
        effectCopy = UnityEngine.Object.Instantiate(Load(path));
        effectCopy.name = "Boids_hash_field_press_TestCopy";
        return effectCopy;
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
