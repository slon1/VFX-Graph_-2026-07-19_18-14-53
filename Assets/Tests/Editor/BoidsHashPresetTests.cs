using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashPresetTests
{
    private const string AssetPath = "Assets/Effects/Boids_hash.asset";
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string DebugPath = "Assets/Shaders/GPU/Passes/HashDebugPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const float DeltaTime = 1f / 60f;
    private const int Frames = 300;

    private GameObject host;
    private EffectAsset effectCopy;

    [TearDown]
    public void TearDown()
    {
        if (host != null)
        {
            SimulationWorldTestCleanup.DestroyHost(host);
            host = null;
        }

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
    public void Preset_MatchesB1()
    {
        EffectAsset asset = LoadAsset();
        Assert.IsInstanceOf<SwarmSource>(asset.ResolveSource());
        Assert.AreEqual(0.5f, asset.ParticleSize);

        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(1, swarm.Seed);
        Assert.AreEqual(1, swarm.Spawns.Count);
        SwarmSource.Spawn spawn = swarm.Spawns[0];
        Assert.AreEqual(0, spawn.TeamIndex);
        Assert.AreEqual(3000, spawn.Count);
        Assert.AreEqual(Vector2.zero, spawn.Center);
        Assert.AreEqual(44f, spawn.Radius);
        Assert.AreEqual(Vector2.zero, spawn.InitialDirection);

        Assert.AreEqual(1, asset.Teams.Count);
        TeamProfile team = asset.Teams[0];
        Assert.AreEqual("Swarm", team.Name);
        Assert.AreEqual(3f, team.SeparationRadius);
        Assert.AreEqual(3f, team.AlignmentRadius);
        Assert.AreEqual(3f, team.CohesionRadius);
        Assert.AreEqual(4f, team.InterGroupSeparationMultiplier);
        Assert.AreEqual(1.2f, team.SeparationWeight);
        Assert.AreEqual(0.8f, team.AlignmentWeight);
        Assert.AreEqual(0.6f, team.CohesionWeight);
        Assert.AreEqual(6f, team.Cruise);
        Assert.AreEqual(4f, team.Turn);

        IReadOnlyList<SimPass> passes = asset.Passes;
        Assert.AreEqual(8, passes.Count);
        Assert.IsInstanceOf<BuildSpatialHashPass>(passes[0]);
        Assert.IsInstanceOf<HashCountsToFieldPass>(passes[1]);
        Assert.IsInstanceOf<ClearVelocityPass>(passes[2]);
        Assert.IsInstanceOf<BoidNeighborForcePass>(passes[3]);
        Assert.IsInstanceOf<TeamHeadingSteerPass>(passes[4]);
        Assert.IsInstanceOf<IntegratePass>(passes[5]);
        Assert.IsInstanceOf<BoxBoundsPass>(passes[6]);
        Assert.IsInstanceOf<HeadingToValuePass>(passes[7]);

        BuildSpatialHashPass hash = (BuildSpatialHashPass)passes[0];
        BoxBoundsPass box = (BoxBoundsPass)passes[6];
        Assert.AreEqual(Vector3.zero, hash.Center);
        Assert.AreEqual(new Vector3(45f, 0f, 45f), hash.Extents);
        Assert.AreEqual(3f, hash.MinCellSize);
        Assert.IsTrue(hash.Wrap);
        Assert.AreEqual(hash.Center, box.Center);
        Assert.AreEqual(hash.Extents, box.Extents);
        Assert.AreEqual(BoundsBehaviour.Wrap, box.Behaviour);
        Assert.AreEqual("hashCount", ((HashCountsToFieldPass)passes[1]).FieldName);
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
    public void Rebuild_WithoutVisualEffect_KeepsThreeThousandParticles()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset();
        host = new GameObject("BoidsHash_World");
        host.SetActive(false);
        SimulationWorld world = host.AddComponent<SimulationWorld>();

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = asset;
        worldSo.FindProperty("visualEffect").objectReferenceValue = null;
        SerializedProperty library = worldSo.FindProperty("passLibrary");
        ComputeShader[] shaders = LoadLibrary();
        library.arraySize = shaders.Length;
        for (int i = 0; i < shaders.Length; i++)
        {
            library.GetArrayElementAtIndex(i).objectReferenceValue = shaders[i];
        }

        worldSo.ApplyModifiedPropertiesWithoutUndo();

        Assert.DoesNotThrow(() => world.Rebuild());
        Assert.IsTrue(world.enabled);
        FieldInfo particlesField = typeof(SimulationWorld).GetField(
            "particles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(particlesField);
        ParticleSet particles = (ParticleSet)particlesField.GetValue(world);
        Assert.AreEqual(3000, particles.Count);
    }

    [Test]
    [Timeout(180000)]
    public void Chain_ThreeHundredFrames_StaysFiniteInsideBox()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset();
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "BoidsHashChain" };
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
            particles.Get(BuiltinAttributes.Position).GetData(position);
            particles.Get(BuiltinAttributes.Heading).GetData(heading);
            particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
            for (int i = 0; i < count; i++)
            {
                AssertFinite(position[i], "position " + i);
                AssertFinite(heading[i], "heading " + i);
                AssertFinite(velocity[i], "velocity " + i);
                Assert.AreEqual(1f, heading[i].magnitude, 1e-4f, "heading " + i);
                Assert.AreEqual(6f, velocity[i].magnitude, 1e-4f, "velocity " + i);
                Assert.GreaterOrEqual(position[i].x, -45f - 1e-3f);
                Assert.LessOrEqual(position[i].x, 45f + 1e-3f);
                Assert.GreaterOrEqual(position[i].z, -45f - 1e-3f);
                Assert.LessOrEqual(position[i].z, 45f + 1e-3f);
            }

            SimField hashCount = fields.Get("hashCount");
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(hashCount.Current);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError);
            NativeArray<ushort> raw = request.GetData<ushort>();
            int sum = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                sum += Mathf.RoundToInt(Mathf.HalfToFloat(raw[i]));
            }

            Assert.AreEqual(3000, sum);
        }
        finally
        {
            teams?.Dispose();
            cmd.Release();
            fields.Dispose();
            particles.Dispose();
        }
    }

    [Test]
    public void CountCapHits_DefaultsFalse()
    {
        EffectAsset asset = LoadAsset();
        BoidNeighborForcePass force = null;
        IReadOnlyList<SimPass> passes = asset.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is BoidNeighborForcePass found)
            {
                force = found;
            }
        }

        Assert.IsNotNull(force);
        Assert.IsFalse(force.CountCapHits);
        Assert.AreEqual(48, force.MaxNeighbors);
    }

    private EffectAsset LoadAsset()
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        Assert.IsNotNull(asset, AssetPath + " must exist. Create it with Tools/M3D/Create Boids Hash Effect.");
        return asset;
    }

    private EffectAsset InstantiateAsset()
    {
        effectCopy = UnityEngine.Object.Instantiate(LoadAsset());
        effectCopy.name = "Boids_hash_TestCopy";
        return effectCopy;
    }

    private ComputeShader[] LoadLibrary()
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
