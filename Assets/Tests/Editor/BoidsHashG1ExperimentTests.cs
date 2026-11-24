using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashG1ExperimentTests
{
    private const float DeltaTime = 1f / 60f;
    private const int Frames = 30;
    private const int ParticlesPerTeam = 200000;
    private const float WorldExtent = 112f;

    private static readonly string[] Assets =
    {
        "Assets/Effects/Boids_hash_P400c_field_press_K1_Mb1.asset",
        "Assets/Effects/Boids_hash_P400c_field_press_K1_Mb2.asset",
        "Assets/Effects/Boids_hash_P400c_field_press_K1_Mb3.asset",
        "Assets/Effects/Boids_hash_P400c_field_press_K1_Mb4.asset",
        "Assets/Effects/Boids_hash_P400c_field_press_K1_Sorted.asset",
        "Assets/Effects/Boids_hash_P400c_field_press_K3_Sorted.asset",
    };

    private static readonly string[] Scenes =
    {
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K1_Mb1.unity",
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K1_Mb2.unity",
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K1_Mb3.unity",
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K1_Mb4.unity",
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K1_Sorted.unity",
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K3_Sorted.unity",
    };

    private EffectAsset effectCopy;

    [TearDown]
    public void TearDown()
    {
        if (effectCopy == null)
        {
            return;
        }

        DisposePasses(effectCopy);
        UnityEngine.Object.DestroyImmediate(effectCopy);
        effectCopy = null;
    }

    [Test]
    public void Presets_MatchTheK1ChainAndTheExperimentKnob()
    {
        AssertPattern(Load(Assets[0]), FieldRepeatReadPattern.OneTexel, 1, sorted: false, refresh: 1);
        AssertPattern(Load(Assets[1]), FieldRepeatReadPattern.LinearById, 1, sorted: false, refresh: 1);
        AssertPattern(Load(Assets[2]), FieldRepeatReadPattern.Span, 4096, sorted: false, refresh: 1);
        AssertPattern(Load(Assets[3]), FieldRepeatReadPattern.Span, 16384, sorted: false, refresh: 1);
        AssertPattern(Load(Assets[4]), FieldRepeatReadPattern.Normal, 1, sorted: true, refresh: 1);
        AssertPattern(Load(Assets[5]), FieldRepeatReadPattern.Normal, 1, sorted: true, refresh: 3);
    }

    [Test]
    public void Validator_AcceptsEveryPresetWithoutWarnings()
    {
        for (int i = 0; i < Assets.Length; i++)
        {
            EffectAsset asset = Load(Assets[i]);
            IReadOnlyList<string> warnings = SpatialHashValidator.Validate(asset.Passes, asset);
            Assert.IsEmpty(warnings, Assets[i]);
        }
    }

    [Test]
    public void Scenes_AreEnabledInTheBuild()
    {
        for (int i = 0; i < Scenes.Length; i++)
        {
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(Scenes[i]), Scenes[i]);
            Assert.IsTrue(IsEnabled(Scenes[i]), Scenes[i]);
        }
    }

    [Test]
    [Timeout(180000)]
    public void Chains_ThirtyFrames_StayFiniteAndConserveBothN()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < Assets.Length; i++)
        {
            double seconds = RunChain(Assets[i]);
            text.Append(Assets[i]);
            text.Append('=');
            text.Append(seconds.ToString("G6"));
            text.Append(' ');
        }

        Assert.Pass(text.ToString());
    }

    private static void AssertPattern(
        EffectAsset asset,
        FieldRepeatReadPattern pattern,
        int span,
        bool sorted,
        int refresh)
    {
        Assert.AreEqual(0.5f, asset.ParticleSize);
        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(ParticlesPerTeam, swarm.Spawns[0].Count);
        Assert.AreEqual(ParticlesPerTeam, swarm.Spawns[1].Count);
        int index = 0;
        IReadOnlyList<SimPass> passes = asset.Passes;
        if (sorted)
        {
            BuildSpatialHashPass hash = (BuildSpatialHashPass)passes[0];
            Assert.AreEqual(1f, hash.MinCellSize);
            Assert.AreEqual(refresh, hash.RefreshInterval);
            Assert.AreEqual(new Vector3(WorldExtent, 0f, WorldExtent), hash.Extents);
            index = 1;
        }

        FieldNeighborCollectPass collect = (FieldNeighborCollectPass)passes[index];
        Assert.AreEqual(FieldCollectMode.Scatter, collect.Mode);
        Assert.AreEqual(1f, collect.TexelSize);
        Assert.AreEqual(refresh, collect.RefreshInterval);
        FieldNeighborForcePass force = (FieldNeighborForcePass)passes[index + 3];
        Assert.AreEqual(pattern, force.RepeatReadPattern);
        Assert.AreEqual(span, force.RepeatSpan);
        Assert.AreEqual(sorted, force.SortedOrder);
        FieldDensityPressurePass pressure = (FieldDensityPressurePass)passes[index + 4];
        Assert.AreEqual(0.25f, pressure.PressureTexel);
        Assert.AreEqual(0.8f, pressure.Weight);
        Assert.AreEqual(25f, pressure.G0);
        Assert.AreEqual(sorted, pressure.SortedOrder);
    }

    private double RunChain(string path)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        EffectAsset asset = InstantiateAsset(path);
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "G1Chain" };
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
            particles.Get(BuiltinAttributes.Position).GetData(position);
            for (int i = 0; i < count; i++)
            {
                Assert.IsFalse(float.IsNaN(position[i].x) || float.IsNaN(position[i].z));
                Assert.IsFalse(float.IsInfinity(position[i].x) || float.IsInfinity(position[i].z));
            }

            FieldNeighborCollectPass collect = null;
            FieldDensityPressurePass pressure = null;
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i] is FieldNeighborCollectPass foundCollect)
                {
                    collect = foundCollect;
                }

                if (passes[i] is FieldDensityPressurePass foundPressure)
                {
                    pressure = foundPressure;
                }
            }

            Assert.AreEqual(ParticlesPerTeam * 16384L, SumPressure(pressure, 0));
            Assert.AreEqual(ParticlesPerTeam * 16384L, SumPressure(pressure, 1));
            Assert.AreEqual(ParticlesPerTeam, SumCollect(collect, 0), 0.1d);
            Assert.AreEqual(ParticlesPerTeam, SumCollect(collect, 1), 0.1d);
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

    private static long SumPressure(FieldDensityPressurePass pressure, int team)
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

    private static double SumCollect(FieldNeighborCollectPass collect, int team)
    {
        int channels = FieldNeighborSet.ChannelsPerTexel;
        uint[] bits = new uint[collect.Field.Layout.CellCount * channels];
        collect.Field.Accum.GetData(bits);
        double sum = 0d;
        for (int i = team * 3; i < bits.Length; i += channels)
        {
            sum += unchecked((int)bits[i]) / (double)FieldNeighborSet.FixedPointScale;
        }

        return sum;
    }

    private static EffectAsset Load(string path)
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        Assert.IsNotNull(asset, path);
        return asset;
    }

    private EffectAsset InstantiateAsset(string path)
    {
        effectCopy = UnityEngine.Object.Instantiate(Load(path));
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

    private static bool IsEnabled(string scenePath)
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
}
