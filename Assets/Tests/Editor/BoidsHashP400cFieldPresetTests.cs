using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashP400cFieldPresetTests
{
    private const string GatherPath = "Assets/Effects/Boids_hash_P400c_field_gather.asset";
    private const string ScatterPath = "Assets/Effects/Boids_hash_P400c_field_scatter.asset";
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
    public void GatherPreset_MatchesP400cFieldConstants()
    {
        AssertPreset(Load(GatherPath), FieldCollectMode.Gather, withHash: true);
    }

    [Test]
    public void ScatterPreset_MatchesP400cFieldConstants()
    {
        AssertPreset(Load(ScatterPath), FieldCollectMode.Scatter, withHash: false);
    }

    [Test]
    public void Validator_AcceptsBothPresetsWithoutWarnings()
    {
        AssertAccepted(Load(GatherPath));
        AssertAccepted(Load(ScatterPath));
    }

    [Test]
    [Timeout(180000)]
    public void GatherChain_ThirtyFrames_StaysFiniteAndConservesN()
    {
        RunChain(GatherPath, FieldCollectMode.Gather);
    }

    [Test]
    [Timeout(180000)]
    public void ScatterChain_ThirtyFrames_StaysFiniteAndConservesN()
    {
        RunChain(ScatterPath, FieldCollectMode.Scatter);
    }

    private void RunChain(string path, FieldCollectMode mode)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset(path);
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "BoidsHashP400cFieldChain" };
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
            teams = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
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
                Assert.GreaterOrEqual(position[i].x, -WorldExtent - 1e-3f);
                Assert.LessOrEqual(position[i].x, WorldExtent + 1e-3f);
                Assert.GreaterOrEqual(position[i].z, -WorldExtent - 1e-3f);
                Assert.LessOrEqual(position[i].z, WorldExtent + 1e-3f);
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
            FieldNeighborCollectPass collect = FindCollect(passes);
            Assert.AreEqual(mode, collect.Mode);
            double n0 = SumN(collect, 0);
            double n1 = SumN(collect, 1);
            double tolerance = mode == FieldCollectMode.Scatter ? 0.1d : 1e-2d;
            Assert.AreEqual(ParticlesPerTeam, n0, tolerance);
            Assert.AreEqual(ParticlesPerTeam, n1, tolerance);
        }
        finally
        {
            teams?.Dispose();
            cmd.Release();
            fields.Dispose();
            particles.Dispose();
        }
    }

    private static double SumN(FieldNeighborCollectPass collect, int team)
    {
        int cells = collect.Field.Layout.CellCount;
        int channels = FieldNeighborSet.ChannelsPerTexel;
        if (collect.Mode == FieldCollectMode.Scatter)
        {
            uint[] bits = new uint[cells * channels];
            collect.Field.Accum.GetData(bits);
            double sum = 0d;
            for (int i = team * 3; i < bits.Length; i += channels)
            {
                sum += unchecked((int)bits[i]) / (double)FieldNeighborSet.FixedPointScale;
            }

            return sum;
        }

        float[] raw = new float[cells * channels];
        collect.Field.Raw.GetData(raw);
        double total = 0d;
        for (int i = team * 3; i < raw.Length; i += channels)
        {
            total += raw[i];
        }

        return total;
    }

    private static void AssertPreset(EffectAsset asset, FieldCollectMode mode, bool withHash)
    {
        Assert.IsInstanceOf<SwarmSource>(asset.ResolveSource());
        Assert.AreEqual(0.5f, asset.ParticleSize);
        Assert.AreEqual(1f, asset.SimulationSpeed);
        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(1, swarm.Seed);
        Assert.AreEqual(0f, swarm.JitterDegrees);
        Assert.AreEqual(2, swarm.Spawns.Count);
        Assert.AreEqual(ParticlesPerTeam, swarm.Spawns[0].Count);
        Assert.AreEqual(ParticlesPerTeam, swarm.Spawns[1].Count);
        Assert.AreEqual(2, asset.Teams.Count);
        Assert.AreEqual(3f, asset.Teams[0].SeparationRadius);
        Assert.AreEqual(4f, asset.Teams[0].InterGroupSeparationMultiplier);
        Assert.AreEqual(3f, asset.Teams[1].SeparationRadius);
        IReadOnlyList<SimPass> passes = asset.Passes;
        Assert.AreEqual(withHash ? 8 : 7, passes.Count);
        int index = 0;
        if (withHash)
        {
            BuildSpatialHashPass hash = (BuildSpatialHashPass)passes[index++];
            Assert.AreEqual(1f, hash.MinCellSize);
            Assert.AreEqual(1, hash.RefreshInterval);
            Assert.IsTrue(hash.Wrap);
            Assert.AreEqual(new Vector3(WorldExtent, 0f, WorldExtent), hash.Extents);
        }

        FieldNeighborCollectPass collect = (FieldNeighborCollectPass)passes[index++];
        Assert.AreEqual(mode, collect.Mode);
        Assert.AreEqual(1f, collect.TexelSize);
        Assert.AreEqual(1, collect.RefreshInterval);
        Assert.AreEqual(new Vector3(WorldExtent, 0f, WorldExtent), collect.Extents);
        Assert.IsInstanceOf<FieldNeighborConvolvePass>(passes[index++]);
        Assert.IsInstanceOf<ClearVelocityPass>(passes[index++]);
        Assert.IsInstanceOf<TeamHeadingSteerPass>(passes[index++]);
        Assert.IsInstanceOf<IntegratePass>(passes[index++]);
        BoxBoundsPass box = (BoxBoundsPass)passes[index++];
        Assert.AreEqual(BoundsBehaviour.Wrap, box.Behaviour);
        Assert.AreEqual(new Vector3(WorldExtent, 0f, WorldExtent), box.Extents);
        Assert.IsInstanceOf<HeadingToValuePass>(passes[index]);
        for (int i = 0; i < passes.Count; i++)
        {
            Assert.IsFalse(passes[i] is BoidNeighborForcePass);
        }
    }

    private static void AssertAccepted(EffectAsset asset)
    {
        IReadOnlyList<string> warnings = null;
        Assert.DoesNotThrow(() => warnings = SpatialHashValidator.Validate(asset.Passes, asset));
        Assert.AreEqual(0, warnings.Count);
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

        Assert.Fail("collect pass missing");
        return null;
    }

    private static EffectAsset Load(string path)
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        Assert.IsNotNull(asset, path + " must exist. Create it with Tools/M3D/Create Boids Hash P400c Field Effects.");
        return asset;
    }

    private EffectAsset InstantiateAsset(string path)
    {
        effectCopy = UnityEngine.Object.Instantiate(Load(path));
        effectCopy.name = "Boids_hash_P400c_Field_TestCopy";
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
