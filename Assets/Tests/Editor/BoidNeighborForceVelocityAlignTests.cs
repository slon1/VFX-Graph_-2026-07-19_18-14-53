using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

[TestFixture]
public class BoidNeighborForceVelocityAlignTests
{
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";

    [Test]
    public void Random_ThirtySeeds_MatchesOracle()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3 extents = new Vector3(32f, 0f, 32f);
        const float minCell = 4f;
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, extents, minCell);
        for (int seed = 1; seed <= 30; seed++)
        {
            var random = new System.Random(seed);
            int count = 70 + ((seed * 17) % 90);
            if (count % 64 == 0)
            {
                count++;
            }

            bool wrap = seed % 2 == 0;
            Vector3[] positions = new Vector3[count];
            Vector3[] headings = new Vector3[count];
            Vector3[] velocities = new Vector3[count];
            uint[] teamIds = new uint[count];
            var used = new HashSet<int>();
            int edge = 0;
            for (int i = 0; i < count; i++)
            {
                float margin = 1e-3f;
                positions[i] = new Vector3(
                    Next(random, -extents.x + margin, extents.x - margin),
                    Next(random, -1f, 1f),
                    Next(random, -extents.z + margin, extents.z - margin));
                float angle = Next(random, 0f, Mathf.PI * 2f);
                headings[i] = new Vector3(Mathf.Cos(angle), Next(random, -1f, 1f), Mathf.Sin(angle));
                velocities[i] = new Vector3(Next(random, -8f, 8f), Next(random, -3f, 3f), Next(random, -8f, 8f));
                teamIds[i] = (i & 1) == 0 ? 0u : (uint)(8 + (i % 5));
                int ix = Mathf.FloorToInt((positions[i].x - layout.Origin.x) / layout.CellSize.x);
                int iy = Mathf.FloorToInt((positions[i].z - layout.Origin.y) / layout.CellSize.y);
                ix = Mathf.Clamp(ix, 0, layout.Resolution.x - 1);
                iy = Mathf.Clamp(iy, 0, layout.Resolution.y - 1);
                used.Add(iy * layout.Resolution.x + ix);
                if (ix == 0 || ix == layout.Resolution.x - 1 || iy == 0 || iy == layout.Resolution.y - 1)
                {
                    edge++;
                }
            }

            if (wrap)
            {
                Assert.Greater(edge, 0, "seed " + seed);
            }

            Assert.AreNotEqual(0, count % 64, "seed " + seed);
            Assert.Less(used.Count, layout.CellCount, "seed " + seed);
            TeamParams[] slots = TwoTeams(8f, 6f);
            Vector3[] force = RunVelocity(
                positions, headings, velocities, teamIds, slots, wrap, extents, minCell, 0);
            AssertOracle("seed " + seed, force, positions, velocities, teamIds, slots, wrap, extents, minCell);
        }
    }

    [Test]
    public void Seam_MatchesOracle()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3[] positions =
        {
            new Vector3(-2.6f, 0f, -2.6f),
            new Vector3(-2.4f, 0.2f, -2.5f),
            new Vector3(-2.5f, 0f, -2.3f),
            new Vector3(-2.2f, -0.4f, -2.4f),
            new Vector3(-2.5f, 0f, -2.5f),
            new Vector3(2.5f, 1f, 2.5f),
        };
        Vector3[] headings = new Vector3[positions.Length];
        Vector3[] velocities =
        {
            new Vector3(4f, 1f, 0f),
            new Vector3(-2f, 0f, 3f),
            new Vector3(1f, -4f, -1f),
            new Vector3(0f, 2f, 5f),
            new Vector3(6f, 0f, -2f),
            new Vector3(-3f, 1f, 4f),
        };
        uint[] teamIds = { 0u, 0u, 9u, 0u, 0u, 9u };
        for (int i = 0; i < headings.Length; i++)
        {
            headings[i] = new Vector3(0.2f * i, 1f, -0.3f);
        }

        Vector3 extents = new Vector3(3f, 0f, 3f);
        TeamParams[] slots = TwoTeams(8f, 5f);
        Vector3[] force = RunVelocity(positions, headings, velocities, teamIds, slots, true, extents, 2f, 0);
        AssertOracle("seam", force, positions, velocities, teamIds, slots, true, extents, 2f);
    }

    [Test]
    public void DenseCap_AlignWeightZero_MatchesWalk1()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        const int count = 24;
        Vector3[] positions = new Vector3[count];
        Vector3[] headings = new Vector3[count];
        Vector3[] velocities = new Vector3[count];
        uint[] teamIds = new uint[count];
        for (int i = 0; i < count; i++)
        {
            float angle = i * Mathf.PI * 2f / count;
            positions[i] = new Vector3(Mathf.Cos(angle) * 0.35f, 0.1f * i, Mathf.Sin(angle) * 0.35f);
            headings[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            velocities[i] = new Vector3(Mathf.Sin(angle) * 7f, 2f, Mathf.Cos(angle) * -4f);
            teamIds[i] = (uint)(i % 3 == 0 ? 10 : 0);
        }

        TeamParams[] slots = TwoTeams(8f, 8f);
        slots[0].Weights.y = 0f;
        slots[7].Weights.y = 0f;
        using (Gpu gpu = new Gpu(positions, headings, velocities, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f, 4))
        {
            Vector3[] velocityAlign = gpu.Dispatch();
            gpu.Force.AlignFromVelocity = false;
            Vector3[] walk1 = gpu.Dispatch();
            Assert.IsFalse(gpu.Force.EffectiveAlignFromVelocity);
            AssertAbs("dense cap", velocityAlign, walk1, 1e-6f);
        }
    }

    [Test]
    public void EqualSpeeds_AddNoAlignment()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3[] positions = { Vector3.zero, new Vector3(0.4f, 1f, 0.2f), new Vector3(-0.3f, -1f, 0.15f) };
        Vector3[] headings = { Vector3.right, Vector3.forward, Vector3.left };
        Vector3[] velocities = { new Vector3(5f, 9f, -1f), new Vector3(5f, -4f, -1f), new Vector3(5f, 0f, -1f) };
        uint[] teamIds = { 0u, 0u, 0u };
        TeamParams[] slots = OneTeam(2f, 2f, 2f, 1f, 0.8f, 0.5f, 8f);
        Vector3 extents = new Vector3(8f, 0f, 8f);
        Vector3[] force = RunVelocity(positions, headings, velocities, teamIds, slots, false, extents, 2f, 0);
        slots[0].Weights.y = 0f;
        Vector2[] without = OracleAll(positions, velocities, teamIds, slots, false, extents, 2f);
        for (int i = 0; i < force.Length; i++)
        {
            AssertClose(force[i].x, without[i].x, "equal x " + i);
            AssertClose(force[i].z, without[i].y, "equal z " + i);
        }
    }

    [Test]
    public void AdrExample_OneNeighbor_MatchesTheStatedContribution()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3[] positions = { Vector3.zero, new Vector3(0.6f, 2f, 0f) };
        Vector3[] headings = { Vector3.up, Vector3.left };
        uint[] teamIds = { 0u, 0u };
        TeamParams[] slots = OneTeam(0.1f, 2f, 0.1f, 1f, 0.8f, 0f, 8f);
        Vector3 extents = new Vector3(8f, 0f, 8f);
        Vector3[] sameWay = { new Vector3(6f, 3f, 0f), new Vector3(8f, -2f, 0f) };
        Vector3[] forward = RunVelocity(positions, headings, sameWay, teamIds, slots, false, extents, 2f, 0);
        Assert.That(Mathf.Abs(forward[0].x - 0.2f), Is.LessThanOrEqualTo(1e-4f), "forward x " + forward[0].x);
        Assert.That(Mathf.Abs(forward[0].z), Is.LessThanOrEqualTo(1e-4f), "forward z");

        Vector3[] opposite = { new Vector3(6f, 3f, 0f), new Vector3(-6f, 1f, 0f) };
        Vector3[] back = RunVelocity(positions, headings, opposite, teamIds, slots, false, extents, 2f, 0);
        Assert.That(Mathf.Abs(back[0].x - (-1.2f)), Is.LessThanOrEqualTo(1e-4f), "back x " + back[0].x);
        Assert.That(Mathf.Abs(back[0].z), Is.LessThanOrEqualTo(1e-4f), "back z");
    }

    [Test]
    public void NoNeighbors_AlignmentIsZero()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3[] positions = { new Vector3(1f, 0f, -1f) };
        Vector3[] headings = { Vector3.right };
        Vector3[] velocities = { new Vector3(6f, 1f, 2f) };
        uint[] teamIds = { 0u };
        TeamParams[] slots = OneTeam(1f, 2f, 1f, 1f, 0.8f, 0.4f, 8f);
        Vector3[] force = RunVelocity(
            positions, headings, velocities, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f, 0);
        Assert.That(force[0].sqrMagnitude, Is.LessThanOrEqualTo(1e-8f));
    }

    [Test]
    public void Heading_DoesNotChangeTheForce()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3[] positions = { Vector3.zero, new Vector3(0.5f, 0f, 0.2f) };
        Vector3[] headings = { Vector3.right, Vector3.left };
        Vector3[] velocities = { new Vector3(2f, 0f, 1f), new Vector3(-4f, 5f, 3f) };
        uint[] teamIds = { 0u, 0u };
        TeamParams[] slots = OneTeam(0.1f, 2f, 0.1f, 0f, 0.8f, 0f, 8f);
        using (Gpu gpu = new Gpu(positions, headings, velocities, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f, 0))
        {
            Vector3[] first = gpu.Dispatch();
            Assert.IsTrue(gpu.Force.EffectiveAlignFromVelocity);
            Assert.AreEqual(1, gpu.Force.EffectiveWalkMode);
            gpu.UploadHeadings(new[] { Vector3.forward, new Vector3(0f, 1f, -1f) });
            Vector3[] second = gpu.Dispatch();
            AssertBits(first, second);
        }
    }

    [Test]
    public void OtherTeam_IsExcludedFromAlignment()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3[] positions = { Vector3.zero, new Vector3(0.4f, 0f, 0.1f) };
        Vector3[] headings = { Vector3.right, Vector3.right };
        Vector3[] velocities = { new Vector3(1f, 0f, 0f), new Vector3(8f, 0f, 0f) };
        uint[] teamIds = { 0u, 1u };
        TeamParams[] slots = TwoTeams(8f, 8f);
        slots[0].Radii = new Vector4(0.05f, 2f, 0.05f, 0f);
        slots[0].Weights = new Vector4(0f, 0.8f, 0f, 0f);
        Vector3[] force = RunVelocity(
            positions, headings, velocities, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f, 0);
        Assert.That(force[0].sqrMagnitude, Is.LessThanOrEqualTo(1e-8f));
    }

    [Test]
    public void Contract_FlagDefaultsOff_AndRejectsTheWrongWalk()
    {
        BoidNeighborForcePass pass = new BoidNeighborForcePass();
        Assert.IsFalse(pass.AlignFromVelocity);
        Assert.IsFalse(pass.EffectiveAlignFromVelocity);
        Assert.AreEqual(1, pass.WalkMode);
        Assert.Throws<ArgumentOutOfRangeException>(() => pass.WalkMode = 3);
        Assert.DoesNotThrow(() => pass.WalkMode = 0);
        Assert.Throws<InvalidOperationException>(() => pass.AlignFromVelocity = true);
        pass.WalkMode = 2;
        Assert.Throws<InvalidOperationException>(() => pass.AlignFromVelocity = true);
        pass.WalkMode = 1;
        pass.AlignFromVelocity = true;
        Assert.IsTrue(Contains(pass.Reads, BuiltinAttributes.InertialVelocity));
        pass.AlignFromVelocity = false;
        Assert.IsFalse(Contains(pass.Reads, BuiltinAttributes.InertialVelocity));
        Assert.AreEqual(2, pass.Reads.Count);
    }

    [Test]
    public void ZeroParticles_ExecuteDoesNotDispatch()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        ComputeShader hashShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(HashPath);
        ComputeShader boids = AssetDatabase.LoadAssetAtPath<ComputeShader>(BoidPath);
        Assert.IsNotNull(hashShader);
        Assert.IsNotNull(boids);
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        GraphicsBuffer teams = null;
        CommandBuffer cmd = null;
        BuildSpatialHashPass hash = new BuildSpatialHashPass
        {
            Extents = new Vector3(8f, 0f, 8f),
            MinCellSize = 2f,
            Wrap = false,
        };
        BoidNeighborForcePass force = new BoidNeighborForcePass { AlignFromVelocity = true };
        try
        {
            particles.EnsureCapacity(0);
            teams = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            teams.SetData(OneTeam(1f, 1f, 1f, 0f, 0.5f, 0f, 8f));
            cmd = new CommandBuffer { name = "VelocityAlignZero" };
            SimContext context = new SimContext(particles, fields, new[] { hashShader, boids }, null);
            context.Cmd = cmd;
            context.Teams = teams;
            hash.Initialize(context);
            force.Initialize(context);
            Assert.DoesNotThrow(() => hash.Execute(context, 0f));
            Assert.DoesNotThrow(() => force.Execute(context, 0f));
            Assert.IsFalse(hash.LastExecuteDispatched);
            Assert.IsFalse(force.LastExecuteDispatched);
        }
        finally
        {
            force.Dispose();
            hash.Dispose();
            cmd?.Release();
            teams?.Release();
            particles.Dispose();
            fields.Dispose();
        }
    }

    [Test]
    public void Execute_MissingVelocitySnapshot_ThrowsWhenParticlesExist()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (Gpu gpu = new Gpu(
            new[] { Vector3.zero }, new[] { Vector3.right }, new[] { Vector3.right }, new[] { 0u },
            OneTeam(1f, 1f, 1f, 0f, 0.5f, 0f, 8f), false, new Vector3(8f, 0f, 8f), 2f, 0))
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => gpu.Force.Execute(gpu.Context, 0f));
            StringAssert.Contains("velocity snapshot", exception.Message);
        }
    }

    [Test]
    public void Execute_FlagWithoutKernel_Throws()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3[] positions = { Vector3.zero, new Vector3(0.4f, 0f, 0.1f) };
        Vector3[] headings = { Vector3.right, Vector3.left };
        Vector3[] velocities = { new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f) };
        uint[] teamIds = { 0u, 0u };
        using (Gpu gpu = new Gpu(
            positions, headings, velocities, teamIds, OneTeam(1f, 1f, 1f, 0f, 0.5f, 0f, 8f),
            false, new Vector3(8f, 0f, 8f), 2f, 0))
        {
            gpu.Dispatch();
            FieldInfo field = typeof(BoidNeighborForcePass).GetField(
                "velocityKernel", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field.SetValue(gpu.Force, default(KernelHandle));
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => gpu.Force.Execute(gpu.Context, 0f));
            StringAssert.Contains("velocity alignment kernel not found", exception.Message);
        }
    }

    [Test]
    public void Execute_RadiusAboveTheCell_Throws()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3[] positions = { Vector3.zero, new Vector3(0.4f, 0f, 0.1f) };
        Vector3[] headings = { Vector3.right, Vector3.left };
        Vector3[] velocities = { Vector3.right, Vector3.left };
        uint[] teamIds = { 0u, 0u };
        using (Gpu gpu = new Gpu(
            positions, headings, velocities, teamIds, OneTeam(1f, 1f, 1f, 0f, 0.5f, 0f, 8f),
            false, new Vector3(8f, 0f, 8f), 2f, 0))
        {
            gpu.Dispatch();
            gpu.Context.TeamMaxRadius = 100f;
            LogAssert.Expect(LogType.Warning, new Regex("executing walk 0"));
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => gpu.Force.Execute(gpu.Context, 0f));
            StringAssert.Contains("AlignFromVelocity", exception.Message);
            StringAssert.Contains("radius", exception.Message);
        }
    }

    [Test]
    public void Initialize_FlagWithWalkModeZero_Throws()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (Gpu gpu = new Gpu(
            new[] { Vector3.zero }, new[] { Vector3.right }, new[] { Vector3.right }, new[] { 0u },
            OneTeam(1f, 1f, 1f, 0f, 0f, 0f, 8f), false, new Vector3(8f, 0f, 8f), 2f, 0, initializeForce: false))
        {
            FieldInfo field = typeof(BoidNeighborForcePass).GetField(
                "alignFromVelocity", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            gpu.Force.WalkMode = 0;
            field.SetValue(gpu.Force, true);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => gpu.Force.Initialize(gpu.Context));
            StringAssert.Contains("WalkMode 1", exception.Message);
        }
    }

    [Test]
    public void Validator_RequiresClassicSteerAndWalk1_AndIgnoresADisabledFlag()
    {
        BuildSpatialHashPass hash = new BuildSpatialHashPass { Wrap = false };
        BoidNeighborForcePass force = new BoidNeighborForcePass { AlignFromVelocity = true };
        EffectAsset missing = Effect(hash, force);
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => SpatialHashValidator.Validate(missing.Passes, missing));
            StringAssert.Contains("Boid Neighbor Force", exception.Message);
            StringAssert.Contains("Team Classic Steer", exception.Message);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(missing);
        }

        BuildSpatialHashPass hashWalk = new BuildSpatialHashPass { Wrap = false };
        BoidNeighborForcePass wrongWalk = new BoidNeighborForcePass();
        wrongWalk.AlignFromVelocity = true;
        wrongWalk.WalkMode = 2;
        EffectAsset walk = Effect(hashWalk, wrongWalk, new TeamClassicSteerPass());
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => SpatialHashValidator.Validate(walk.Passes, walk));
            StringAssert.Contains("Boid Neighbor Force", exception.Message);
            StringAssert.Contains("WalkMode 1", exception.Message);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(walk);
        }

        BuildSpatialHashPass hashOk = new BuildSpatialHashPass { Wrap = false };
        EffectAsset ok = Effect(hashOk, new BoidNeighborForcePass { AlignFromVelocity = true }, new TeamClassicSteerPass());
        BuildSpatialHashPass hashOff = new BuildSpatialHashPass { Wrap = false };
        BoidNeighborForcePass off = new BoidNeighborForcePass();
        off.WalkMode = 0;
        EffectAsset disabledFlag = Effect(hashOff, off);
        try
        {
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(ok.Passes, ok));
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(disabledFlag.Passes, disabledFlag));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(ok);
            UnityEngine.Object.DestroyImmediate(disabledFlag);
        }
    }

    [Test]
    public void Chain_SecondFrameUsesTheSpeedWrittenOnTheFirst()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3[] positions = { Vector3.zero, new Vector3(0.5f, 0f, 0.1f) };
        Vector3[] headings = { Vector3.right, Vector3.forward };
        Vector3[] velocities = { Vector3.zero, Vector3.zero };
        uint[] teamIds = { 0u, 0u };
        TeamParams[] slots = OneTeam(0.05f, 3f, 0.05f, 0f, 0.8f, 0f, 8f);
        slots[0].Classic = new Vector4(4f, 8f, 0f, 1f);
        const float dt = 0.05f;
        using (Gpu gpu = new Gpu(positions, headings, velocities, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f, 0))
        {
            var clear = new ClearVelocityPass();
            var steer = new TeamClassicSteerPass();
            var integrate = new IntegratePass();
            clear.Initialize(gpu.Context);
            steer.Initialize(gpu.Context);
            integrate.Initialize(gpu.Context);

            clear.Execute(gpu.Context, dt);
            gpu.Hash.Execute(gpu.Context, dt);
            gpu.Force.Execute(gpu.Context, dt);
            gpu.Flush();
            Vector3[] firstForce = gpu.ReadVelocity();
            Assert.That(firstForce[0].sqrMagnitude, Is.LessThanOrEqualTo(1e-8f));
            Assert.That(firstForce[1].sqrMagnitude, Is.LessThanOrEqualTo(1e-8f));

            steer.Execute(gpu.Context, dt);
            integrate.Execute(gpu.Context, dt);
            gpu.Flush();
            Vector3[] inertial = gpu.ReadInertial();
            Assert.That(Mathf.Abs(inertial[0].x - 6f), Is.LessThanOrEqualTo(1e-3f));
            Assert.That(Mathf.Abs(inertial[1].z - 6f), Is.LessThanOrEqualTo(1e-3f));

            clear.Execute(gpu.Context, dt);
            gpu.Hash.Execute(gpu.Context, dt);
            gpu.Force.Execute(gpu.Context, dt);
            gpu.Flush();
            Vector3[] second = gpu.ReadVelocity();
            Vector3[] moved = gpu.ReadPositions();
            AssertOracle("frame 2", second, moved, inertial, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f);
            Assert.Greater(second[0].sqrMagnitude, 1e-4f);
        }
    }

    private static Vector3[] RunVelocity(
        Vector3[] positions,
        Vector3[] headings,
        Vector3[] velocities,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell,
        int maxNeighbors)
    {
        using (Gpu gpu = new Gpu(positions, headings, velocities, teamIds, slots, wrap, extents, minCell, maxNeighbors))
        {
            return gpu.Dispatch();
        }
    }

    private static void AssertOracle(
        string label,
        Vector3[] velocity,
        Vector3[] positions,
        Vector3[] speeds,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell)
    {
        Vector2[] expected = OracleAll(positions, speeds, teamIds, slots, wrap, extents, minCell);
        for (int i = 0; i < velocity.Length; i++)
        {
            AssertClose(velocity[i].x, expected[i].x, label + " x " + i);
            AssertClose(velocity[i].y, 0f, label + " y " + i);
            AssertClose(velocity[i].z, expected[i].y, label + " z " + i);
        }
    }

    private static Vector2[] OracleAll(
        Vector3[] positions,
        Vector3[] speeds,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell)
    {
        Vector2 size = SpatialHashSet.ComputeLayout(Vector3.zero, extents, minCell).Size;
        Vector2[] force = new Vector2[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            force[i] = OracleForceVel(i, positions, speeds, teamIds, slots, wrap, size);
        }

        return force;
    }

    private static Vector2 OracleForceVel(
        int index,
        Vector3[] positions,
        Vector3[] speeds,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector2 size)
    {
        uint selfTeam = Math.Min(teamIds[index], 7u);
        TeamParams team = slots[selfTeam];
        float sepR2 = team.Radii.x * team.Radii.x;
        float alignR2 = team.Radii.y * team.Radii.y;
        float cohR2 = team.Radii.z * team.Radii.z;
        float maxR2 = Mathf.Max(sepR2, Mathf.Max(alignR2, cohR2));
        Vector2 self = new Vector2(positions[index].x, positions[index].z);
        Vector2 sep = Vector2.zero;
        Vector2 align = Vector2.zero;
        Vector2 coh = Vector2.zero;
        int sepN = 0;
        int alignN = 0;
        int cohN = 0;
        for (int j = 0; j < positions.Length; j++)
        {
            Vector2 other = new Vector2(positions[j].x, positions[j].z);
            Vector2 d = other - self;
            if (wrap)
            {
                d = new Vector2(d.x - size.x * Mathf.Round(d.x / size.x), d.y - size.y * Mathf.Round(d.y / size.y));
            }

            float r2 = Vector2.Dot(d, d);
            if (r2 < 1e-6f || r2 >= maxR2)
            {
                continue;
            }

            uint neighborTeam = Math.Min(teamIds[j], 7u);
            bool same = neighborTeam == selfTeam;
            if (r2 < sepR2)
            {
                float mul = same ? 1f : team.Radii.w;
                sep += (-d / (r2 + 1e-3f)) * mul;
                sepN++;
            }

            if (same && r2 < alignR2)
            {
                align += new Vector2(speeds[j].x, speeds[j].z);
                alignN++;
            }

            if (same && r2 < cohR2)
            {
                coh += d;
                cohN++;
            }
        }

        Vector2 force = Vector2.zero;
        if (sepN > 0)
        {
            force += (sep / sepN) * team.Weights.x;
        }

        if (alignN > 0)
        {
            float invMax = team.Classic.y > 1e-6f ? 1f / team.Classic.y : 0f;
            Vector2 own = new Vector2(speeds[index].x, speeds[index].z);
            force += ((align / alignN) - own) * (invMax * team.Weights.y);
        }

        if (cohN > 0)
        {
            Vector2 mean = coh / cohN;
            if (Vector2.Dot(mean, mean) > 1e-6f)
            {
                force += mean.normalized * team.Weights.z;
            }
        }

        return force;
    }

    private static TeamParams[] OneTeam(
        float sep, float align, float coh, float wSep, float wAlign, float wCoh, float maxSpeed)
    {
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = Slot(sep, align, coh, 1f, wSep, wAlign, wCoh, maxSpeed);
        return slots;
    }

    private static TeamParams[] TwoTeams(float maxA, float maxB)
    {
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = Slot(3.5f, 3.5f, 3.5f, 1.4f, 1f, 0.5f, 0.4f, maxA);
        slots[7] = Slot(3.5f, 3.5f, 3.5f, 0.4f, 0.7f, 0.3f, 0.3f, maxB);
        return slots;
    }

    private static TeamParams Slot(
        float sep, float align, float coh, float inter,
        float wSep, float wAlign, float wCoh, float maxSpeed)
    {
        return new TeamParams
        {
            Radii = new Vector4(sep, align, coh, inter),
            Weights = new Vector4(wSep, wAlign, wCoh, 0f),
            Motion = Vector4.zero,
            Classic = new Vector4(0f, maxSpeed, 0f, 1f),
        };
    }

    private static EffectAsset Effect(params SimPass[] passes)
    {
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(DataSourceKind.Cube, 1f, passes, null);
        effect.SetTeams(new[] { new TeamProfile { MinSpeed = 4f, MaxSpeed = 8f } });
        return effect;
    }

    private static bool Contains(System.Collections.Generic.IReadOnlyList<AttributeId> ids, AttributeId id)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i].Name == id.Name)
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertClose(float actual, float expected, string label)
    {
        float tolerance = 1e-4f + 1e-4f * Mathf.Abs(expected);
        Assert.That(
            Mathf.Abs(actual - expected),
            Is.LessThanOrEqualTo(tolerance),
            label + " actual " + actual + " expected " + expected);
    }

    private static void AssertAbs(string label, Vector3[] actual, Vector3[] expected, float tolerance)
    {
        Assert.AreEqual(expected.Length, actual.Length, label);
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.That(Mathf.Abs(actual[i].x - expected[i].x), Is.LessThanOrEqualTo(tolerance), label + " x " + i);
            Assert.That(Mathf.Abs(actual[i].y - expected[i].y), Is.LessThanOrEqualTo(tolerance), label + " y " + i);
            Assert.That(Mathf.Abs(actual[i].z - expected[i].z), Is.LessThanOrEqualTo(tolerance), label + " z " + i);
        }
    }

    private static void AssertBits(Vector3[] expected, Vector3[] actual)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(BitConverter.SingleToInt32Bits(expected[i].x), BitConverter.SingleToInt32Bits(actual[i].x));
            Assert.AreEqual(BitConverter.SingleToInt32Bits(expected[i].y), BitConverter.SingleToInt32Bits(actual[i].y));
            Assert.AreEqual(BitConverter.SingleToInt32Bits(expected[i].z), BitConverter.SingleToInt32Bits(actual[i].z));
        }
    }

    private static float Next(System.Random random, float min, float max)
    {
        return min + (float)random.NextDouble() * (max - min);
    }

    private sealed class Gpu : IDisposable
    {
        public readonly BuildSpatialHashPass Hash;
        public readonly BoidNeighborForcePass Force;
        public readonly SimContext Context;
        private readonly ParticleSet particles;
        private readonly FieldSet fields;
        private readonly GraphicsBuffer teams;
        private readonly int count;

        public Gpu(
            Vector3[] positions,
            Vector3[] headings,
            Vector3[] velocities,
            uint[] teamIds,
            TeamParams[] slots,
            bool wrap,
            Vector3 extents,
            float minCell,
            int maxNeighbors,
            bool initializeForce = true)
        {
            count = positions.Length;
            ComputeShader dynamics = AssetDatabase.LoadAssetAtPath<ComputeShader>(DynamicsPath);
            ComputeShader hashShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(HashPath);
            ComputeShader boids = AssetDatabase.LoadAssetAtPath<ComputeShader>(BoidPath);
            Assert.IsNotNull(dynamics);
            Assert.IsNotNull(hashShader);
            Assert.IsNotNull(boids);
            particles = new ParticleSet();
            fields = new FieldSet();
            particles.EnsureCapacity(count);
            particles.RegisterAttribute(BuiltinAttributes.Position);
            particles.RegisterAttribute(BuiltinAttributes.Heading);
            particles.RegisterAttribute(BuiltinAttributes.TeamId);
            particles.RegisterAttribute(BuiltinAttributes.Velocity);
            particles.RegisterAttribute(BuiltinAttributes.InertialVelocity);
            particles.Get(BuiltinAttributes.Position).SetData(positions);
            particles.Get(BuiltinAttributes.Heading).SetData(headings);
            particles.Get(BuiltinAttributes.TeamId).SetData(teamIds);
            particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[count]);
            particles.Get(BuiltinAttributes.InertialVelocity).SetData(velocities);

            teams = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            teams.SetData(slots);
            Context = new SimContext(particles, fields, new[] { dynamics, hashShader, boids }, null);
            Context.Cmd = new CommandBuffer { name = "VelocityAlign" };
            Context.Teams = teams;
            Hash = new BuildSpatialHashPass
            {
                Center = Vector3.zero,
                Extents = extents,
                MinCellSize = minCell,
                Wrap = wrap,
            };
            Force = new BoidNeighborForcePass
            {
                MaxNeighbors = maxNeighbors,
                AlignFromVelocity = true,
            };
            Hash.Initialize(Context);
            if (initializeForce)
            {
                Force.Initialize(Context);
            }
        }

        public Vector3[] Dispatch()
        {
            particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[count]);
            Hash.Execute(Context, 0f);
            Force.Execute(Context, 0f);
            Flush();
            return ReadVelocity();
        }

        public void UploadHeadings(Vector3[] headings)
        {
            particles.Get(BuiltinAttributes.Heading).SetData(headings);
        }

        public void Flush()
        {
            Graphics.ExecuteCommandBuffer(Context.Cmd);
            Context.Cmd.Clear();
        }

        public Vector3[] ReadVelocity()
        {
            Vector3[] velocity = new Vector3[count];
            particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
            return velocity;
        }

        public Vector3[] ReadInertial()
        {
            Vector3[] velocity = new Vector3[count];
            particles.Get(BuiltinAttributes.InertialVelocity).GetData(velocity);
            return velocity;
        }

        public Vector3[] ReadPositions()
        {
            Vector3[] value = new Vector3[count];
            particles.Get(BuiltinAttributes.Position).GetData(value);
            return value;
        }

        public void Dispose()
        {
            Force.Dispose();
            Hash.Dispose();
            Context.Cmd?.Release();
            teams?.Release();
            particles?.Dispose();
            fields?.Dispose();
        }
    }
}
