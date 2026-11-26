using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashG2ExperimentTests
{
    private const float DeltaTime = 1f / 60f;
    private const int Frames = 30;

    private static readonly string[] Assets =
    {
        "Assets/Effects/Boids_hash_2teams_field_press_g25_u2.asset",
        "Assets/Effects/Boids_hash_2teams_field_press_g25_r2.asset",
        "Assets/Effects/Boids_hash_2teams_field_press_g25_u2r2.asset",
        "Assets/Effects/Boids_hash_P400c_field_press_K3_U2R2.asset",
        "Assets/Effects/Boids_hash_P400c_field_press_K3_U2.asset",
        "Assets/Effects/Boids_hash_P400c_field_press_K3_R2.asset",
        "Assets/Effects/Boids_hash_P400c_field_press_K1_SortedCollect.asset",
        "Assets/Effects/Boids_hash_P400c_field_press_K1_SortedScatter.asset",
        "Assets/Effects/Boids_hash_P306k_press_K3_U2R2.asset",
        "Assets/Effects/Boids_hash_P506k_press_K3_U2R2.asset",
    };

    private static readonly string[] Scenes =
    {
        "Assets/Scenes/Boids_Hash_2Teams_Field_P_G25_U2.unity",
        "Assets/Scenes/Boids_Hash_2Teams_Field_P_G25_R2.unity",
        "Assets/Scenes/Boids_Hash_2Teams_Field_P_G25_U2R2.unity",
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K3_U2R2.unity",
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K3_U2.unity",
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K3_R2.unity",
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K1_SortedCollect.unity",
        "Assets/Scenes/Boids_Hash_P400c_Field_Press_K1_SortedScatter.unity",
        "Assets/Scenes/Boids_Hash_P306k_Press_K3_U2R2.unity",
        "Assets/Scenes/Boids_Hash_P506k_Press_K3_U2R2.unity",
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
    public void Presets_MatchIntervalsAndSortFlags()
    {
        AssertSmall(Load(Assets[0]), grid: 2, interleave: 1);
        AssertSmall(Load(Assets[1]), grid: 1, interleave: 2);
        AssertSmall(Load(Assets[2]), grid: 2, interleave: 2);
        AssertLarge(Load(Assets[3]), 200000, new Vector3(112f, 0f, 112f), 3, 2, 2, false, false);
        AssertLarge(Load(Assets[4]), 200000, new Vector3(112f, 0f, 112f), 3, 2, 1, false, false);
        AssertLarge(Load(Assets[5]), 200000, new Vector3(112f, 0f, 112f), 3, 1, 2, false, false);
        AssertLarge(Load(Assets[6]), 200000, new Vector3(112f, 0f, 112f), 1, 1, 1, true, false);
        AssertLarge(Load(Assets[7]), 200000, new Vector3(112f, 0f, 112f), 1, 1, 1, false, true);
        AssertLarge(Load(Assets[8]), 153125, new Vector3(98f, 0f, 98f), 3, 2, 2, false, false);
        AssertLarge(Load(Assets[9]), 253125, new Vector3(126f, 0f, 126f), 3, 2, 2, false, false);
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
    public void Chain400k_ThirtyFrames_StaysFiniteAndConservesN()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        RunChain(Assets[3], 200000);
    }

    [Test]
    [Timeout(180000)]
    public void Chain506k_ThirtyFrames_StaysFiniteAndConservesN()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        RunChain(Assets[9], 253125);
    }

    private static void AssertSmall(EffectAsset asset, int grid, int interleave)
    {
        Assert.AreEqual(0.5f, asset.ParticleSize);
        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(1500, swarm.Spawns[0].Count);
        Assert.AreEqual(1500, swarm.Spawns[1].Count);
        AssertPressure(asset.Passes, 0, new Vector3(45f, 0f, 45f), 3, grid, interleave, false, false);
    }

    private static void AssertLarge(
        EffectAsset asset,
        int perTeam,
        Vector3 extents,
        int refresh,
        int grid,
        int interleave,
        bool collectSorted,
        bool scatterSorted)
    {
        Assert.AreEqual(0.5f, asset.ParticleSize);
        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(perTeam, swarm.Spawns[0].Count);
        Assert.AreEqual(perTeam, swarm.Spawns[1].Count);
        int index = 0;
        if (collectSorted || scatterSorted)
        {
            BuildSpatialHashPass hash = (BuildSpatialHashPass)asset.Passes[0];
            Assert.AreEqual(1f, hash.MinCellSize);
            Assert.AreEqual(refresh, hash.RefreshInterval);
            Assert.AreEqual(extents, hash.Extents);
            index = 1;
        }

        AssertPressure(asset.Passes, index, extents, refresh, grid, interleave, collectSorted, scatterSorted);
    }

    private static void AssertPressure(
        IReadOnlyList<SimPass> passes,
        int index,
        Vector3 extents,
        int refresh,
        int grid,
        int interleave,
        bool collectSorted,
        bool scatterSorted)
    {
        FieldNeighborCollectPass collect = (FieldNeighborCollectPass)passes[index];
        Assert.AreEqual(FieldCollectMode.Scatter, collect.Mode);
        Assert.AreEqual(1f, collect.TexelSize);
        Assert.AreEqual(refresh, collect.RefreshInterval);
        Assert.AreEqual(collectSorted, collect.SortedOrder);
        Assert.AreEqual(extents, collect.Extents);
        FieldNeighborForcePass force = (FieldNeighborForcePass)passes[index + 3];
        Assert.IsFalse(force.SortedOrder);
        FieldDensityPressurePass pressure = (FieldDensityPressurePass)passes[index + 4];
        Assert.AreEqual(0.25f, pressure.PressureTexel);
        Assert.AreEqual(0.8f, pressure.Weight);
        Assert.AreEqual(25f, pressure.G0);
        Assert.AreEqual(grid, pressure.GridUpdateInterval);
        Assert.AreEqual(interleave, pressure.ForceInterleave);
        Assert.IsFalse(pressure.SortedOrder);
        Assert.AreEqual(scatterSorted, pressure.ScatterSorted);
        Assert.AreEqual(extents, pressure.Extents);
    }

    private void RunChain(string path, int perTeam)
    {
        EffectAsset asset = InstantiateAsset(path);
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "G2Chain" };
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
                Assert.IsFalse(float.IsNaN(position[i].x) || float.IsNaN(position[i].z), path);
                Assert.IsFalse(float.IsInfinity(position[i].x) || float.IsInfinity(position[i].z), path);
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

            Assert.AreEqual(perTeam * 16384L, SumPressure(pressure, 0), path);
            Assert.AreEqual(perTeam * 16384L, SumPressure(pressure, 1), path);
            Assert.AreEqual(perTeam, SumCollect(collect, 0), 0.1d, path);
            Assert.AreEqual(perTeam, SumCollect(collect, 1), 0.1d, path);
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
