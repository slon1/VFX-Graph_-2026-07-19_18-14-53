using System;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashClassicVelPresetTests
{
    private const string VelPath = "Assets/Effects/Boids_hash_classic_vel.asset";
    private const string ClassicPath = "Assets/Effects/Boids_hash_classic.asset";
    private const string HashPath = "Assets/Effects/Boids_hash.asset";
    private const string HashShaderPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string DebugPath = "Assets/Shaders/GPU/Passes/HashDebugPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const float DeltaTime = 1f / 60f;

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

        DisposeEffectCopy();
    }

    [Test]
    public void Preset_MatchesClassicVelComposition()
    {
        EffectAsset asset = LoadAsset(VelPath);
        Assert.IsInstanceOf<SwarmSource>(asset.ResolveSource());
        Assert.AreEqual(1f, asset.SimulationSpeed);
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
        Assert.AreEqual(4f, team.MinSpeed);
        Assert.AreEqual(8f, team.MaxSpeed);
        Assert.AreEqual(12f, team.MaxForce);
        Assert.AreEqual(5f, team.ForceGain);
        Assert.AreEqual(0, asset.SwarmPoints.Count);

        IReadOnlyList<SimPass> passes = asset.Passes;
        Assert.AreEqual(8, passes.Count);
        Assert.IsInstanceOf<BuildSpatialHashPass>(passes[0]);
        Assert.IsInstanceOf<HashCountsToFieldPass>(passes[1]);
        Assert.IsInstanceOf<ClearVelocityPass>(passes[2]);
        Assert.IsInstanceOf<BoidNeighborForcePass>(passes[3]);
        Assert.IsInstanceOf<TeamClassicSteerPass>(passes[4]);
        Assert.IsInstanceOf<IntegratePass>(passes[5]);
        Assert.IsInstanceOf<BoxBoundsPass>(passes[6]);
        Assert.IsInstanceOf<HeadingToValuePass>(passes[7]);
        for (int i = 0; i < passes.Count; i++)
        {
            Assert.IsFalse(passes[i] is TeamHeadingSteerPass, "TeamHeadingSteer at " + i);
            Assert.IsFalse(passes[i] is SwarmPointForcePass, "SwarmPointForce at " + i);
        }

        BuildSpatialHashPass hash = (BuildSpatialHashPass)passes[0];
        BoxBoundsPass box = (BoxBoundsPass)passes[6];
        Assert.AreEqual(3f, hash.MinCellSize);
        Assert.IsTrue(hash.Wrap);
        Assert.AreEqual(Vector3.zero, hash.Center);
        Assert.AreEqual(new Vector3(45f, 0f, 45f), hash.Extents);
        Assert.AreEqual(hash.Center, box.Center);
        Assert.AreEqual(hash.Extents, box.Extents);
        Assert.AreEqual(BoundsBehaviour.Wrap, box.Behaviour);

        BoidNeighborForcePass force = (BoidNeighborForcePass)passes[3];
        Assert.IsTrue(force.AlignFromVelocity);
        Assert.AreEqual(1, force.WalkMode);
        Assert.AreEqual(48, force.MaxNeighbors);
        Assert.IsFalse(force.CountCapHits);
    }

    [Test]
    public void Validator_ClassicVel_IsQuiet()
    {
        EffectAsset asset = LoadAsset(VelPath);
        IReadOnlyList<string> warnings = SpatialHashValidator.Validate(asset.Passes, asset);
        Assert.AreEqual(0, warnings.Count, string.Join(" | ", warnings));
    }

    [Test]
    public void Rebuild_ClassicVel_KeepsThreeThousandParticles()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset(VelPath, "BoidsHashClassicVel_Rebuild");
        host = new GameObject("BoidsHashClassicVel_World");
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
        System.Reflection.FieldInfo particlesField = typeof(SimulationWorld).GetField(
            "particles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(particlesField);
        ParticleSet particles = (ParticleSet)particlesField.GetValue(world);
        Assert.AreEqual(3000, particles.Count);
    }

    [Test]
    [Timeout(180000)]
    public void Chain_ClassicVel_ThreeHundredFrames_StaysInTheCorridor()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Run(VelPath, "classic_vel", 300, assertCorridor: true, logMetrics: false);
    }

    [Test]
    [Timeout(180000)]
    public void Metrics_LogsSpeedSpreadAtFourFrames()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Run(HashPath, "Boids_hash", 600, assertCorridor: false, logMetrics: true);
        Run(ClassicPath, "classic", 600, assertCorridor: false, logMetrics: true);
        Run(VelPath, "classic_vel", 600, assertCorridor: false, logMetrics: true);
    }

    private void Run(string assetPath, string label, int frames, bool assertCorridor, bool logMetrics)
    {
        EffectAsset asset = InstantiateAsset(assetPath, label + "_TestCopy");
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "BoidsHashClassicVelChain" };
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

            int count = particles.Count;
            Assert.AreEqual(3000, count);
            uint[] teamBefore = new uint[count];
            particles.Get(BuiltinAttributes.TeamId).GetData(teamBefore);

            Vector3[] position = new Vector3[count];
            Vector3[] heading = new Vector3[count];
            Vector3[] velocity = new Vector3[count];
            Vector3[] inertial = new Vector3[count];
            for (int frame = 1; frame <= frames; frame++)
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
                if (logMetrics && (frame == 100 || frame == 200 || frame == 300 || frame == 600))
                {
                    ReadParticles(particles, position, heading, velocity, inertial, readInertial: false);
                    LogMetrics(label, frame, heading, velocity);
                }
            }

            ReadParticles(particles, position, heading, velocity, inertial, readInertial: assertCorridor);
            if (assertCorridor)
            {
                uint[] teamAfter = new uint[count];
                particles.Get(BuiltinAttributes.TeamId).GetData(teamAfter);
                for (int i = 0; i < count; i++)
                {
                    AssertFinite(position[i], "position " + i);
                    AssertFinite(heading[i], "heading " + i);
                    AssertFinite(velocity[i], "velocity " + i);
                    AssertFinite(inertial[i], "inertialVelocity " + i);
                    Assert.GreaterOrEqual(position[i].x, -45f - 1e-3f);
                    Assert.LessOrEqual(position[i].x, 45f + 1e-3f);
                    Assert.GreaterOrEqual(position[i].z, -45f - 1e-3f);
                    Assert.LessOrEqual(position[i].z, 45f + 1e-3f);
                    float speed = new Vector2(velocity[i].x, velocity[i].z).magnitude;
                    Assert.GreaterOrEqual(speed, 4f - 1e-3f, "speed " + i);
                    Assert.LessOrEqual(speed, 8f + 1e-3f, "speed " + i);
                    Assert.AreEqual(0f, velocity[i].y, 1e-4f, "velocity.y " + i);
                    Assert.AreEqual(1f, heading[i].magnitude, 1e-3f, "heading " + i);
                    Vector3 direction = velocity[i].normalized;
                    Assert.AreEqual(direction.x, heading[i].x, 1e-3f, "heading.x " + i);
                    Assert.AreEqual(direction.y, heading[i].y, 1e-3f, "heading.y " + i);
                    Assert.AreEqual(direction.z, heading[i].z, 1e-3f, "heading.z " + i);
                    Assert.AreEqual(teamBefore[i], teamAfter[i], "teamId " + i);
                }

                Assert.AreEqual(3000, particles.Count);
                BoidNeighborForcePass force = (BoidNeighborForcePass)passes[3];
                Assert.IsTrue(force.EffectiveAlignFromVelocity);
            }
        }
        finally
        {
            teams?.Dispose();
            cmd.Release();
            fields.Dispose();
            particles.Dispose();
        }
    }

    private static void ReadParticles(
        ParticleSet particles,
        Vector3[] position,
        Vector3[] heading,
        Vector3[] velocity,
        Vector3[] inertial,
        bool readInertial)
    {
        particles.Get(BuiltinAttributes.Position).GetData(position);
        particles.Get(BuiltinAttributes.Heading).GetData(heading);
        particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
        if (readInertial)
        {
            particles.Get(BuiltinAttributes.InertialVelocity).GetData(inertial);
        }
    }

    private static void LogMetrics(string label, int frame, Vector3[] heading, Vector3[] velocity)
    {
        int count = velocity.Length;
        double speedSum = 0.0;
        double speedSq = 0.0;
        Vector3 headingSum = Vector3.zero;
        int low = 0;
        int high = 0;
        for (int i = 0; i < count; i++)
        {
            float speed = velocity[i].magnitude;
            speedSum += speed;
            speedSq += (double)speed * speed;
            headingSum += heading[i];
            if (speed < 4f + 1e-3f)
            {
                low++;
            }

            if (speed > 8f - 1e-3f)
            {
                high++;
            }
        }

        double mean = speedSum / count;
        double variance = speedSq / count - mean * mean;
        if (variance < 0.0)
        {
            variance = 0.0;
        }

        string line = string.Format(
            CultureInfo.InvariantCulture,
            "P942METRIC {0} frame={1} speed={2:F4} stdev={3:F4} pol={4:F4} low={5:F4} high={6:F4}",
            label,
            frame,
            mean,
            Math.Sqrt(variance),
            (headingSum / count).magnitude,
            low / (float)count,
            high / (float)count);
        TestContext.WriteLine(line);
        Debug.Log(line);
    }

    private EffectAsset LoadAsset(string assetPath)
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(assetPath);
        Assert.IsNotNull(asset, assetPath + " must exist. Create it from Tools/M3D.");
        return asset;
    }

    private EffectAsset InstantiateAsset(string assetPath, string copyName)
    {
        DisposeEffectCopy();
        effectCopy = UnityEngine.Object.Instantiate(LoadAsset(assetPath));
        effectCopy.name = copyName;
        return effectCopy;
    }

    private void DisposeEffectCopy()
    {
        if (effectCopy == null)
        {
            return;
        }

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

    private static ComputeShader[] LoadLibrary()
    {
        ComputeShader hash = AssetDatabase.LoadAssetAtPath<ComputeShader>(HashShaderPath);
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
