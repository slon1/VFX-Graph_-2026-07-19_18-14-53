using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashP400cPresetTests
{
    private const string AssetPath = "Assets/Effects/Boids_hash_P400c.asset";
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string DebugPath = "Assets/Shaders/GPU/Passes/HashDebugPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
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
    public void Preset_MatchesP400cConstants()
    {
        EffectAsset asset = LoadAsset();
        Assert.IsInstanceOf<SwarmSource>(asset.ResolveSource());
        Assert.AreEqual(0.5f, asset.ParticleSize);
        Assert.AreEqual(1f, asset.SimulationSpeed);

        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(1, swarm.Seed);
        Assert.AreEqual(0f, swarm.JitterDegrees);
        Assert.AreEqual(2, swarm.Spawns.Count);
        AssertSpawn(swarm.Spawns[0], 0, ParticlesPerTeam, new Vector2(-55f, 0f), 45f, new Vector2(1f, 0f));
        AssertSpawn(swarm.Spawns[1], 1, ParticlesPerTeam, new Vector2(55f, 0f), 45f, new Vector2(-1f, 0f));

        Assert.AreEqual(2, asset.Teams.Count);
        AssertTeam(asset.Teams[0], "RedBlue");
        AssertTeam(asset.Teams[1], "YellowGreen");

        IReadOnlyList<SimPass> passes = asset.Passes;
        Assert.AreEqual(7, passes.Count);
        Assert.IsInstanceOf<BuildSpatialHashPass>(passes[0]);
        Assert.IsInstanceOf<ClearVelocityPass>(passes[1]);
        Assert.IsInstanceOf<BoidNeighborForcePass>(passes[2]);
        Assert.IsInstanceOf<TeamHeadingSteerPass>(passes[3]);
        Assert.IsInstanceOf<IntegratePass>(passes[4]);
        Assert.IsInstanceOf<BoxBoundsPass>(passes[5]);
        Assert.IsInstanceOf<HeadingToValuePass>(passes[6]);

        BuildSpatialHashPass hash = (BuildSpatialHashPass)passes[0];
        BoxBoundsPass box = (BoxBoundsPass)passes[5];
        Vector3 extents = new Vector3(WorldExtent, 0f, WorldExtent);
        Assert.AreEqual(Vector3.zero, hash.Center);
        Assert.AreEqual(extents, hash.Extents);
        Assert.AreEqual(3f, hash.MinCellSize);
        Assert.IsTrue(hash.Wrap);
        Assert.AreEqual(hash.Center, box.Center);
        Assert.AreEqual(hash.Extents, box.Extents);
        Assert.AreEqual(BoundsBehaviour.Wrap, box.Behaviour);

        BoidNeighborForcePass force = (BoidNeighborForcePass)passes[2];
        Assert.AreEqual(48, force.MaxNeighbors);
        Assert.AreEqual(1, force.WalkMode);
        Assert.IsFalse(force.CountCapHits);
        Assert.AreEqual(0, asset.Fields.Count);
    }

    [Test]
    public void Validator_AcceptsPresetWithoutWarnings()
    {
        EffectAsset asset = LoadAsset();
        IReadOnlyList<string> warnings = null;
        Assert.DoesNotThrow(() => warnings = SpatialHashValidator.Validate(asset.Passes, asset));
        Assert.IsNotNull(warnings);
        Assert.AreEqual(0, warnings.Count);
    }

    [Test]
    [Timeout(180000)]
    public void Chain_ThirtyFrames_StaysFiniteInsideBox()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset();
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "BoidsHashP400cChain" };
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
                    SimPass pass = passes[i];
                    pass.Execute(context, DeltaTime);
                    if (pass.LastExecuteDispatched)
                    {
                        SwapPingPong(fields, pass);
                    }
                }

                Graphics.ExecuteCommandBuffer(cmd);
            }

            int count = particles.Count;
            Vector3[] position = new Vector3[count];
            Vector3[] heading = new Vector3[count];
            Vector3[] velocity = new Vector3[count];
            uint[] teamId = new uint[count];
            particles.Get(BuiltinAttributes.Position).GetData(position);
            particles.Get(BuiltinAttributes.Heading).GetData(heading);
            particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
            particles.Get(BuiltinAttributes.TeamId).GetData(teamId);
            int fire = 0;
            int ice = 0;
            for (int i = 0; i < count; i++)
            {
                AssertFinite(position[i], "position " + i);
                AssertFinite(heading[i], "heading " + i);
                AssertFinite(velocity[i], "velocity " + i);
                Assert.AreEqual(1f, heading[i].magnitude, 1e-4f, "heading " + i);
                Assert.AreEqual(6f, velocity[i].magnitude, 1e-4f, "velocity " + i);
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
            Assert.AreEqual(ParticlesPerTeam * 2, count);
        }
        finally
        {
            teams?.Dispose();
            cmd.Release();
            fields.Dispose();
            particles.Dispose();
        }
    }

    private static void AssertSpawn(
        SwarmSource.Spawn spawn, int teamIndex, int count, Vector2 center, float radius, Vector2 direction)
    {
        Assert.AreEqual(teamIndex, spawn.TeamIndex);
        Assert.AreEqual(count, spawn.Count);
        Assert.AreEqual(center, spawn.Center);
        Assert.AreEqual(radius, spawn.Radius);
        Assert.AreEqual(direction, spawn.InitialDirection);
    }

    private static void AssertTeam(TeamProfile team, string name)
    {
        Assert.AreEqual(name, team.Name);
        Assert.AreEqual(3f, team.SeparationRadius);
        Assert.AreEqual(3f, team.AlignmentRadius);
        Assert.AreEqual(3f, team.CohesionRadius);
        Assert.AreEqual(4f, team.InterGroupSeparationMultiplier);
        Assert.AreEqual(1.2f, team.SeparationWeight);
        Assert.AreEqual(0.8f, team.AlignmentWeight);
        Assert.AreEqual(0.6f, team.CohesionWeight);
        Assert.AreEqual(6f, team.Cruise);
        Assert.AreEqual(4f, team.Turn);
    }

    private static EffectAsset LoadAsset()
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        Assert.IsNotNull(
            asset, AssetPath + " must exist. Create it with Tools/M3D/Create Boids Hash P400c Effect.");
        return asset;
    }

    private EffectAsset InstantiateAsset()
    {
        effectCopy = UnityEngine.Object.Instantiate(LoadAsset());
        effectCopy.name = "Boids_hash_P400c_TestCopy";
        return effectCopy;
    }

    private static ComputeShader[] LoadLibrary()
    {
        ComputeShader hash = AssetDatabase.LoadAssetAtPath<ComputeShader>(HashPath);
        ComputeShader debug = AssetDatabase.LoadAssetAtPath<ComputeShader>(DebugPath);
        ComputeShader dynamics = AssetDatabase.LoadAssetAtPath<ComputeShader>(DynamicsPath);
        ComputeShader boids = AssetDatabase.LoadAssetAtPath<ComputeShader>(BoidPath);
        Assert.IsNotNull(hash);
        Assert.IsNotNull(debug);
        Assert.IsNotNull(dynamics);
        Assert.IsNotNull(boids);
        return new[] { hash, debug, dynamics, boids };
    }

    private static void RegisterLikeWorld(ParticleSet particles, EffectAsset asset)
    {
        IReadOnlyList<SimPass> passes = asset.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            RegisterMissing(particles, passes[i].Reads);
            RegisterMissing(particles, passes[i].Writes);
        }

        if (!particles.Schema.Has(BuiltinAttributes.Position))
        {
            RegisterZeroed(particles, BuiltinAttributes.Position);
        }
    }

    private static void RegisterMissing(ParticleSet particles, IReadOnlyList<AttributeId> ids)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (!particles.Schema.Has(ids[i]))
            {
                RegisterZeroed(particles, ids[i]);
            }
        }
    }

    private static void RegisterZeroed(ParticleSet particles, AttributeId id)
    {
        GraphicsBuffer buffer = particles.RegisterAttribute(id);
        buffer.SetData(new byte[buffer.count * buffer.stride]);
    }

    private static void SwapPingPong(FieldSet fields, SimPass pass)
    {
        IReadOnlyList<FieldRequest> writes = pass.FieldWrites;
        for (int i = 0; i < writes.Count; i++)
        {
            if (writes[i].Access == FieldAccess.WritePingPong)
            {
                fields.Swap(writes[i].FieldName);
            }
        }
    }

    private static void AssertFinite(Vector3 value, string label)
    {
        Assert.IsFalse(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z), label);
        Assert.IsFalse(float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z), label);
    }
}
