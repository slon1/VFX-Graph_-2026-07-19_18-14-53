using System;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashClassicPresetTests
{
    private const string ClassicPath = "Assets/Effects/Boids_hash_classic.asset";
    private const string PointsPath = "Assets/Effects/Boids_hash_classic_points.asset";
    private const string HashPath = "Assets/Effects/Boids_hash.asset";
    private const string HashShaderPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string DebugPath = "Assets/Shaders/GPU/Passes/HashDebugPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const float DeltaTime = 1f / 60f;
    private const int Frames = 300;
    private const float HashPeriod = 90f;
    private static readonly Vector2 Repulsion = new Vector2(0f, -22f);

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
    public void Preset_Classic_MatchesComposition()
    {
        AssertComposition(LoadAsset(ClassicPath), withPoints: false);
    }

    [Test]
    public void Preset_ClassicPoints_MatchesComposition()
    {
        EffectAsset asset = LoadAsset(PointsPath);
        AssertComposition(asset, withPoints: true);
        IReadOnlyList<SwarmPoint> points = asset.SwarmPoints;
        Assert.AreEqual(3, points.Count);
        AssertPoint(points[0], new Vector2(-20f, 15f), 0.6f, 10f);
        AssertPoint(points[1], new Vector2(18f, 20f), 1.5f, 10f);
        AssertPoint(points[2], Repulsion, -1.5f, 12f);
    }

    [Test]
    public void Validator_Classic_IsQuiet()
    {
        EffectAsset asset = LoadAsset(ClassicPath);
        IReadOnlyList<string> warnings = SpatialHashValidator.Validate(asset.Passes, asset);
        Assert.AreEqual(0, warnings.Count, string.Join(" | ", warnings));
    }

    [Test]
    public void Validator_ClassicPoints_IsQuiet()
    {
        EffectAsset asset = LoadAsset(PointsPath);
        IReadOnlyList<string> hashWarnings = SpatialHashValidator.Validate(asset.Passes, asset);
        IReadOnlyList<string> pointWarnings = SwarmPointValidator.Validate(asset.Passes, asset);
        Assert.AreEqual(0, hashWarnings.Count, string.Join(" | ", hashWarnings));
        Assert.AreEqual(0, pointWarnings.Count, string.Join(" | ", pointWarnings));
    }

    [Test]
    public void Rebuild_Classic_KeepsThreeThousandParticles()
    {
        RebuildKeepsCount(ClassicPath);
    }

    [Test]
    public void Rebuild_ClassicPoints_KeepsThreeThousandParticles()
    {
        RebuildKeepsCount(PointsPath);
    }

    [Test]
    [Timeout(180000)]
    public void Chain_Classic_ThreeHundredFrames_StaysInTheCorridor()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Simulate(ClassicPath, "classic", assertCorridor: true, pointMetrics: false);
        Simulate(HashPath, "Boids_hash", assertCorridor: false, pointMetrics: false);
    }

    [Test]
    [Timeout(180000)]
    public void Chain_ClassicPoints_ThreeHundredFrames_StaysInTheCorridor()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Simulate(PointsPath, "classic_points", assertCorridor: true, pointMetrics: true);
    }

    private static void AssertComposition(EffectAsset asset, bool withPoints)
    {
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

        IReadOnlyList<SimPass> passes = asset.Passes;
        int expected = withPoints ? 9 : 8;
        Assert.AreEqual(expected, passes.Count);
        int index = 0;
        Assert.IsInstanceOf<BuildSpatialHashPass>(passes[index++]);
        Assert.IsInstanceOf<HashCountsToFieldPass>(passes[index++]);
        Assert.IsInstanceOf<ClearVelocityPass>(passes[index++]);
        Assert.IsInstanceOf<BoidNeighborForcePass>(passes[index++]);
        if (withPoints)
        {
            Assert.IsInstanceOf<SwarmPointForcePass>(passes[index++]);
        }

        Assert.IsInstanceOf<TeamClassicSteerPass>(passes[index++]);
        Assert.IsInstanceOf<IntegratePass>(passes[index++]);
        Assert.IsInstanceOf<BoxBoundsPass>(passes[index++]);
        Assert.IsInstanceOf<HeadingToValuePass>(passes[index]);

        for (int i = 0; i < passes.Count; i++)
        {
            Assert.IsFalse(passes[i] is TeamHeadingSteerPass, "TeamHeadingSteer at " + i);
        }

        BuildSpatialHashPass hash = (BuildSpatialHashPass)passes[0];
        BoxBoundsPass box = (BoxBoundsPass)passes[withPoints ? 7 : 6];
        Assert.AreEqual(3f, hash.MinCellSize);
        Assert.IsTrue(hash.Wrap);
        Assert.AreEqual(Vector3.zero, hash.Center);
        Assert.AreEqual(new Vector3(45f, 0f, 45f), hash.Extents);
        Assert.AreEqual(hash.Center, box.Center);
        Assert.AreEqual(hash.Extents, box.Extents);
        Assert.AreEqual(BoundsBehaviour.Wrap, box.Behaviour);

        BoidNeighborForcePass force = (BoidNeighborForcePass)passes[3];
        Assert.AreEqual(48, force.MaxNeighbors);
        Assert.AreEqual(1, force.WalkMode);
        Assert.IsFalse(force.CountCapHits);
        if (!withPoints)
        {
            Assert.AreEqual(0, asset.SwarmPoints.Count);
        }
    }

    private static void AssertPoint(SwarmPoint point, Vector2 position, float weight, float radius)
    {
        Assert.AreEqual(position, point.PositionXZ);
        Assert.AreEqual(weight, point.Weight);
        Assert.AreEqual(radius, point.Radius);
        Assert.AreEqual(0.25f, point.Edge);
    }

    private void RebuildKeepsCount(string assetPath)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset(assetPath, "BoidsHashClassic_Rebuild");
        host = new GameObject("BoidsHashClassic_World");
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

    private void Simulate(string assetPath, string label, bool assertCorridor, bool pointMetrics)
    {
        EffectAsset asset = InstantiateAsset(assetPath, label + "_TestCopy");
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "BoidsHashClassicChain" };
        GraphicsBuffer teams = null;
        GraphicsBuffer points = null;
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
            if (asset.SwarmPoints.Count > 0)
            {
                points = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    SwarmPointParams.Float4Count,
                    sizeof(float) * 4);
                SwarmPointParams.Upload(points, asset.SwarmPoints);
                context.SwarmPoints = points;
                int live = asset.SwarmPoints.Count;
                context.SwarmPointCount = live > SwarmPoint.MaxPoints ? SwarmPoint.MaxPoints : live;
            }

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
            Vector3[] inertial = assertCorridor ? new Vector3[count] : null;
            for (int frame = 1; frame <= Frames; frame++)
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
                if (frame == 100 || frame == 200 || frame == 300)
                {
                    ReadParticles(particles, position, heading, velocity, inertial);
                    LogMetrics(label, frame, position, heading, velocity, pointMetrics);
                }
            }

            if (!assertCorridor)
            {
                return;
            }

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
        }
        finally
        {
            points?.Dispose();
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
        Vector3[] inertial)
    {
        particles.Get(BuiltinAttributes.Position).GetData(position);
        particles.Get(BuiltinAttributes.Heading).GetData(heading);
        particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
        if (inertial != null)
        {
            particles.Get(BuiltinAttributes.InertialVelocity).GetData(inertial);
        }
    }

    private static void LogMetrics(
        string label,
        int frame,
        Vector3[] position,
        Vector3[] heading,
        Vector3[] velocity,
        bool pointMetrics)
    {
        int count = velocity.Length;
        double speedSum = 0.0;
        Vector3 headingSum = Vector3.zero;
        int low = 0;
        int high = 0;
        float minDistance = float.PositiveInfinity;
        int near6 = 0;
        int near9 = 0;
        for (int i = 0; i < count; i++)
        {
            float speed = velocity[i].magnitude;
            speedSum += speed;
            headingSum += heading[i];
            if (speed < 4f + 1e-3f)
            {
                low++;
            }

            if (speed > 8f - 1e-3f)
            {
                high++;
            }

            if (!pointMetrics)
            {
                continue;
            }

            float distance = TorusDistance(new Vector2(position[i].x, position[i].z), Repulsion);
            if (distance < minDistance)
            {
                minDistance = distance;
            }

            if (distance < 6f)
            {
                near6++;
            }

            if (distance < 9f)
            {
                near9++;
            }
        }

        float average = (float)(speedSum / count);
        float polarization = (headingSum / count).magnitude;
        string line = string.Format(
            CultureInfo.InvariantCulture,
            "P92METRIC {0} frame={1} speed={2:F4} pol={3:F4} low={4:F4} high={5:F4}",
            label,
            frame,
            average,
            polarization,
            low / (float)count,
            high / (float)count);
        if (pointMetrics)
        {
            line += string.Format(
                CultureInfo.InvariantCulture,
                " dist={0:F4} near6={1:F4} near9={2:F4}",
                minDistance,
                near6 / (float)count,
                near9 / (float)count);
        }

        TestContext.WriteLine(line);
        Debug.Log(line);
    }

    private static float TorusDistance(Vector2 position, Vector2 point)
    {
        Vector2 delta = point - position;
        delta.x -= HashPeriod * Mathf.Round(delta.x / HashPeriod);
        delta.y -= HashPeriod * Mathf.Round(delta.y / HashPeriod);
        return delta.magnitude;
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
