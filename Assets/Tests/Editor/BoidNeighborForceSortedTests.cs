using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

[TestFixture]
public class BoidNeighborForceSortedTests
{
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";

    // GPU walk 0 on the one-particle-per-cell lattice, captured before BoidNeighborForceSorted existed.
    private static readonly uint[] Walk0LatticeBits =
    {
        0x00000000, 0x00000000, 0x00000000,
        0x3EBD1219, 0x00000000, 0x3EAC9E22,
        0x3EEEA72E, 0x00000000, 0x3E3925A9,
        0x00000000, 0x00000000, 0x00000000,
        0xBEFBCA6A, 0x00000000, 0x3DB90584,
        0xBEC4901F, 0x00000000, 0x3E862FC6,
        0xBF0CB556, 0x00000000, 0x3E862FC6,
        0xBE995F42, 0x00000000, 0xBECCF2AE,
        0x3D39B209, 0x00000000, 0x3EFEEC38,
        0xBEC4901F, 0x00000000, 0x3DC554E5,
        0xBF0CB556, 0x00000000, 0x3DC554E9,
        0xBEDA086D, 0x00000000, 0x3E8621F0,
        0x00000000, 0x00000000, 0x00000000,
        0x3E66D50D, 0x00000000, 0xBEE4848D,
        0x3D476870, 0x00000000, 0xBEFEC80A,
        0x00000000, 0x00000000, 0x00000000,
    };

    private readonly List<IDisposable> owned = new List<IDisposable>();

    [TearDown]
    public void TearDown()
    {
        for (int i = owned.Count - 1; i >= 0; i--)
        {
            owned[i]?.Dispose();
        }

        owned.Clear();
    }

    // Recorded 2026-10-18 on NVIDIA GeForce GTX 1080, driver 32.0.15.8180, Direct3D 12.
    // The bits were taken before BoidNeighborForceSorted existed, on the tree of 8b5450f.
    // Bit-exact: another GPU or driver may fold fma differently and fail this test
    // without a logic error. The project does not exclude categories from EditMode,
    // so GpuBitExact stays in the normal run. The tolerance test next to this one
    // does not depend on fma.
    [Test]
    [Category("GpuBitExact")]
    public void Walk0_OneParticlePerCell_MatchesBitsFromBeforeTheEdit()
    {
        const int side = 4;
        const float step = 3f;
        int count = side * side;
        Vector3[] positions = new Vector3[count];
        Vector3[] headings = new Vector3[count];
        uint[] teamIds = new uint[count];
        for (int z = 0; z < side; z++)
        {
            for (int x = 0; x < side; x++)
            {
                int i = z * side + x;
                positions[i] = new Vector3(-4.5f + x * step, 0f, -4.5f + z * step);
                float angle = i * 0.37f;
                headings[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            }
        }

        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(8f, 0f, 8f), 2f);
        Assert.Greater(step, layout.CellSize.x);
        Assert.Greater(step, layout.CellSize.y);
        var cells = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            Vector2Int c = Vector2Int.FloorToInt(
                (new Vector2(positions[i].x, positions[i].z) - layout.Origin) / layout.CellSize);
            Assert.IsTrue(cells.Add(c.y * layout.Resolution.x + c.x), "two particles share a cell");
        }

        TeamParams[] slots = Slots(Profile(3.1f, 3.1f, 3.1f, 1f, 1.2f, 0.5f, 0.4f, 6f, 4f));
        Prepared prepared = Prepare(
            positions, headings, teamIds, slots, true, new Vector3(8f, 0f, 8f), 2f, 48, false, false);
        Run(prepared, 0);
        Vector3[] velocity = ReadVelocity(prepared);
        Assert.AreEqual(count * 3, Walk0LatticeBits.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.AreEqual(Walk0LatticeBits[i * 3], unchecked((uint)BitConverter.SingleToInt32Bits(velocity[i].x)), "x " + i);
            Assert.AreEqual(Walk0LatticeBits[i * 3 + 1], unchecked((uint)BitConverter.SingleToInt32Bits(velocity[i].y)), "y " + i);
            Assert.AreEqual(Walk0LatticeBits[i * 3 + 2], unchecked((uint)BitConverter.SingleToInt32Bits(velocity[i].z)), "z " + i);
        }
    }

    [Test]
    public void Walk0_OneParticlePerCell_MatchesRecordedBitsWithinOracleTolerance()
    {
        const int side = 4;
        const float step = 3f;
        int count = side * side;
        Vector3[] positions = new Vector3[count];
        Vector3[] headings = new Vector3[count];
        uint[] teamIds = new uint[count];
        for (int z = 0; z < side; z++)
        {
            for (int x = 0; x < side; x++)
            {
                int i = z * side + x;
                positions[i] = new Vector3(-4.5f + x * step, 0f, -4.5f + z * step);
                float angle = i * 0.37f;
                headings[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            }
        }

        TeamParams[] slots = Slots(Profile(3.1f, 3.1f, 3.1f, 1f, 1.2f, 0.5f, 0.4f, 6f, 4f));
        Prepared prepared = Prepare(
            positions, headings, teamIds, slots, true, new Vector3(8f, 0f, 8f), 2f, 48, false, false);
        Run(prepared, 0);
        Vector3[] velocity = ReadVelocity(prepared);
        for (int i = 0; i < count; i++)
        {
            float expectedX = BitConverter.Int32BitsToSingle(unchecked((int)Walk0LatticeBits[i * 3]));
            float expectedY = BitConverter.Int32BitsToSingle(unchecked((int)Walk0LatticeBits[i * 3 + 1]));
            float expectedZ = BitConverter.Int32BitsToSingle(unchecked((int)Walk0LatticeBits[i * 3 + 2]));
            AssertClose(velocity[i].x, expectedX, "recorded x " + i);
            AssertClose(velocity[i].y, expectedY, "recorded y " + i);
            AssertClose(velocity[i].z, expectedZ, "recorded z " + i);
        }
    }

    [Test]
    public void Oracle_TwoTeams_Walk1MatchesWalk0AndOracle()
    {
        const int side = 14;
        const float step = 0.8f;
        int count = side * side;
        Vector3[] positions = new Vector3[count];
        Vector3[] headings = new Vector3[count];
        uint[] teamIds = new uint[count];
        for (int z = 0; z < side; z++)
        {
            for (int x = 0; x < side; x++)
            {
                int i = z * side + x;
                positions[i] = new Vector3(-12f + x * step, 0f, -12f + z * step);
                float angle = i * 0.37f;
                headings[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                teamIds[i] = (uint)((x + z) & 1);
            }
        }

        TeamParams[] slots = Slots(
            Profile(1.5f, 1.35f, 1.25f, 2.5f, 1.2f, 0.8f, 0.6f, 6f, 4f),
            Profile(1.5f, 1.35f, 1.25f, 0.35f, 0.4f, 0.2f, 0.9f, 3f, 1f));
        Vector3 extents = new Vector3(16f, 0f, 16f);
        Pair pair = RunPair(positions, headings, teamIds, slots, true, extents, 4f, 0, false, false);
        AssertWalks("two teams", pair);
        AssertOracle("two teams", pair.Walk1, positions, headings, teamIds, slots, true, extents, 4f);
    }

    [Test]
    public void CapOff_AndCapDense_Walk1MatchesWalk0()
    {
        Vector3[] positions = new Vector3[24];
        Vector3[] headings = new Vector3[24];
        uint[] teamIds = new uint[24];
        for (int i = 0; i < 24; i++)
        {
            positions[i] = new Vector3(0.02f * i, 0f, 0.01f * (i % 5));
            headings[i] = Vector3.right;
        }

        TeamParams[] slots = Slots(Profile(2f, 2f, 2f, 1f, 1f, 0f, 0f, 0f, 0f));
        Vector3 extents = new Vector3(8f, 0f, 8f);
        Pair open = RunPair(positions, headings, teamIds, slots, false, extents, 2f, 0, false, false);
        AssertWalks("cap off", open);
        AssertOracle("cap off", open.Walk1, positions, headings, teamIds, slots, false, extents, 2f);

        Pair capped = RunPair(positions, headings, teamIds, slots, false, extents, 2f, 1, true, false);
        AssertWalks("cap dense", capped);
        Assert.Greater(capped.Cap0, 0.9f);
        Assert.AreEqual(capped.Cap0, capped.Cap1, 0f);
    }

    [Test]
    public void Seam_Wrap_AndNoWrap_Walk1MatchesWalk0()
    {
        Vector3[] seamPos = { new Vector3(-7.7f, 0f, 0f), new Vector3(7.7f, 0f, 0f) };
        Vector3[] seamHead = { Vector3.right, Vector3.left };
        uint[] teams = { 0u, 0u };
        TeamParams[] seamSlots = Slots(Profile(1f, 1f, 1f, 1f, 1.2f, 0.5f, 0.4f, 6f, 4f));
        Vector3 seamExtents = new Vector3(8f, 0f, 8f);
        Pair seam = RunPair(seamPos, seamHead, teams, seamSlots, true, seamExtents, 2f, 0, false, false);
        AssertWalks("seam", seam);
        AssertOracle("seam", seam.Walk1, seamPos, seamHead, teams, seamSlots, true, seamExtents, 2f);
        Assert.Greater(seam.Walk1[0].sqrMagnitude, 1e-6f);

        Vector3[] far = { new Vector3(-2.9f, 0f, 0f), new Vector3(2.9f, 0f, 0f) };
        Vector3[] headings = { Vector3.right, Vector3.right };
        TeamParams[] farSlots = Slots(Profile(6f, 6f, 6f, 1f, 1f, 0f, 0f, 0f, 0f));
        Pair noWrap = RunPair(far, headings, teams, farSlots, false, new Vector3(3f, 0f, 3f), 2f, 0, false, false);
        AssertWalks("no wrap far", noWrap);
        Assert.Less(noWrap.Walk1[0].sqrMagnitude, 1e-8f);
        Assert.Less(noWrap.Walk1[1].sqrMagnitude, 1e-8f);

        Vector3[] near = { Vector3.zero, new Vector3(0.4f, 0f, 0.1f) };
        Vector3 nearExtents = new Vector3(8f, 0f, 8f);
        Pair interior = RunPair(near, headings, teams, farSlots, false, nearExtents, 2f, 0, false, false);
        AssertWalks("no wrap interior", interior);
        AssertOracle("no wrap interior", interior.Walk1, near, headings, teams, farSlots, false, nearExtents, 2f);
    }

    [Test]
    public void TeamId_Above7_Walk1MatchesWalk0AndOracle()
    {
        Vector3[] positions = { Vector3.zero, new Vector3(0.5f, 0f, 0f) };
        Vector3[] headings = { Vector3.right, Vector3.right };
        uint[] teamIds = { 100u, 0u };
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = Params(2f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f);
        slots[7] = Params(2f, 0f, 0f, 1f, 5f, 0f, 0f, 9f, 8f);
        Vector3 extents = new Vector3(8f, 0f, 8f);
        Pair pair = RunPair(positions, headings, teamIds, slots, false, extents, 2f, 0, false, true);
        AssertWalks("team 100", pair);
        AssertOracle("team 100", pair.Walk1, positions, headings, teamIds, slots, false, extents, 2f);
        Assert.Greater(pair.Walk1[0].sqrMagnitude, 1e-6f);
    }

    [Test]
    public void CountNotMultipleOf64_OneCell_AndEmptyGaps_MatchWalk0()
    {
        const int count = 70;
        Vector3[] positions = new Vector3[count];
        Vector3[] headings = new Vector3[count];
        uint[] teamIds = new uint[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector3(-6f + (i % 10) * 1.3f, 0f, -6f + (i / 10) * 1.7f);
            headings[i] = new Vector3(Mathf.Cos(i * 0.2f), 0f, Mathf.Sin(i * 0.2f));
            teamIds[i] = (uint)(i & 1);
        }

        TeamParams[] slots = Slots(
            Profile(1.5f, 1.5f, 1.5f, 1f, 1f, 0.4f, 0.3f, 4f, 2f),
            Profile(1.5f, 1.5f, 1.5f, 0.5f, 0.8f, 0.2f, 0.2f, 4f, 2f));
        Pair sparse = RunPair(positions, headings, teamIds, slots, true, new Vector3(16f, 0f, 16f), 4f, 48, true, false);
        AssertWalks("70 sparse", sparse);
        Assert.AreEqual(sparse.Cap0, sparse.Cap1, 0f);

        Vector3[] packed = new Vector3[17];
        Vector3[] packedHead = new Vector3[17];
        uint[] packedTeams = new uint[17];
        for (int i = 0; i < packed.Length; i++)
        {
            packed[i] = new Vector3(0.03f * i, 0f, 0.02f * (i % 3));
            packedHead[i] = Vector3.right;
        }

        TeamParams[] oneCell = Slots(Profile(2f, 2f, 2f, 1f, 1f, 0.2f, 0.2f, 1f, 1f));
        Pair cell = RunPair(packed, packedHead, packedTeams, oneCell, false, new Vector3(8f, 0f, 8f), 2f, 0, false, false);
        AssertWalks("one cell", cell);
        AssertOracle("one cell", cell.Walk1, packed, packedHead, packedTeams, oneCell, false, new Vector3(8f, 0f, 8f), 2f);
    }

    [Test]
    public void Seam_ContiguousSlots_DifferentShift_MatchesWalk0()
    {
        Vector3[] positions =
        {
            new Vector3(-2.6f, 0f, -2.6f),
            new Vector3(-2.4f, 0f, -2.5f),
            new Vector3(-2.5f, 0f, -2.3f),
            new Vector3(-2.2f, 0f, -2.4f),
            new Vector3(-2.5f, 0f, -2.5f),
            new Vector3(2.5f, 0f, 2.5f),
        };
        Vector3[] headings =
        {
            Vector3.right, Vector3.up, Vector3.left, Vector3.right, Vector3.right, Vector3.left,
        };
        uint[] teamIds = { 0u, 0u, 0u, 0u, 0u, 0u };
        TeamParams[] slots = Slots(Profile(2f, 2f, 2f, 1f, 1.2f, 0.5f, 0.4f, 4f, 2f));
        Vector3 extents = new Vector3(3f, 0f, 3f);
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, extents, 2f);
        Assert.AreEqual(3, layout.Resolution.x);
        Assert.AreEqual(3, layout.Resolution.y);
        Pair pair = RunPair(positions, headings, teamIds, slots, true, extents, 2f, 0, false, false);
        AssertWalks("shift seam", pair);
        AssertOracle("shift seam", pair.Walk1, positions, headings, teamIds, slots, true, extents, 2f);
        Assert.Greater(pair.Walk1[4].sqrMagnitude, 1e-6f);
    }

    [Test]
    public void Random_ThirtySeeds_Walk1MatchesWalk0()
    {
        int worst = 0;
        int worstWrap = 0;
        Vector3 worldExtents = new Vector3(16f, 0f, 16f);
        const float minCell = 4f;
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, worldExtents, minCell);
        for (int seed = 1; seed <= 30; seed++)
        {
            var random = new System.Random(seed);
            int count = 500 + ((seed * 87) % 2501);
            bool wrap = seed % 2 == 0;
            int[] caps = { 0, 16, 48 };
            int cap = caps[seed % 3];
            Vector3[] positions = new Vector3[count];
            Vector3[] headings = new Vector3[count];
            uint[] teamIds = new uint[count];
            int edge = 0;
            for (int i = 0; i < count; i++)
            {
                positions[i] = new Vector3(
                    Next(random, -worldExtents.x, worldExtents.x),
                    0f,
                    Next(random, -worldExtents.z, worldExtents.z));
                float angle = Next(random, 0f, Mathf.PI * 2f);
                headings[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                teamIds[i] = (uint)(i & 1);
                int ix = Mathf.FloorToInt((positions[i].x - layout.Origin.x) / layout.CellSize.x);
                int iy = Mathf.FloorToInt((positions[i].z - layout.Origin.y) / layout.CellSize.y);
                if (ix == 0 || ix == layout.Resolution.x - 1 || iy == 0 || iy == layout.Resolution.y - 1)
                {
                    edge++;
                }
            }

            if (wrap)
            {
                Assert.Greater(edge, 0, "seed " + seed + " does not touch an edge cell");
            }

            TeamParams[] slots = Slots(
                Profile(3.5f, 3.5f, 3.5f, 1.4f, 1f, 0.5f, 0.4f, 4f, 2f),
                Profile(3.5f, 3.5f, 3.5f, 0.4f, 0.7f, 0.3f, 0.3f, 3f, 1f));
            Pair pair = RunPair(
                positions, headings, teamIds, slots, wrap, worldExtents, minCell, cap, cap > 0, false);
            int ulp = MaxUlp(pair.Walk0, pair.Walk1);
            if (ulp > worst)
            {
                worst = ulp;
            }

            if (wrap && ulp > worstWrap)
            {
                worstWrap = ulp;
            }

            AssertCloseWalks("seed " + seed + " n " + count + " wrap " + wrap + " cap " + cap, pair);
            if (cap > 0)
            {
                Assert.AreEqual(pair.Cap0, pair.Cap1, 0f, "cap seed " + seed);
            }
        }

        TestContext.WriteLine("random max ulp " + worst + " wrap max ulp " + worstWrap);
    }

    [Test]
    public void Walk_SwitchesDuringTheRun_OnTheSameHash()
    {
        Vector3[] positions = { Vector3.zero, new Vector3(0.4f, 0f, 0.2f), new Vector3(-0.3f, 0f, 0.5f) };
        Vector3[] headings = { Vector3.right, Vector3.left, Vector3.forward };
        uint[] teamIds = { 0u, 0u, 1u };
        TeamParams[] slots = Slots(
            Profile(1.5f, 1.5f, 1.5f, 1f, 1f, 0.4f, 0.3f, 2f, 1f),
            Profile(1.5f, 1.5f, 1.5f, 2f, 0.5f, 0.2f, 0.2f, 2f, 1f));
        Prepared prepared = Prepare(
            positions, headings, teamIds, slots, true, new Vector3(8f, 0f, 8f), 2f, 0, false, false);
        Run(prepared, 0);
        Vector3[] first = ReadVelocity(prepared);
        Rerun(prepared, 1);
        Vector3[] middle = ReadVelocity(prepared);
        Rerun(prepared, 0);
        Vector3[] back = ReadVelocity(prepared);
        var pair = new Pair { Walk0 = first, Walk1 = middle };
        AssertWalks("switch", pair);
        AssertCloseWalks("switch back", new Pair { Walk0 = first, Walk1 = back });
    }

    [Test]
    public void Guard_LargeRadius_RunsWalk0Once_ZeroRadiusDoesNotGuard()
    {
        Vector3[] positions = { Vector3.zero, new Vector3(0.4f, 0f, 0.1f) };
        Vector3[] headings = { Vector3.right, Vector3.right };
        uint[] teamIds = { 0u, 0u };
        TeamParams[] slots = Slots(Profile(1f, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 0f));
        Prepared prepared = Prepare(
            positions, headings, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f, 0, false, false);
        Run(prepared, 0);
        Vector3[] walk0 = ReadVelocity(prepared);

        prepared.Context.TeamMaxRadius = 100f;
        LogAssert.Expect(LogType.Warning, new Regex("executing walk 0"));
        Rerun(prepared, 1);
        Vector3[] guarded = ReadVelocity(prepared);
        AssertBits(walk0, guarded);
        Rerun(prepared, 1);
        LogAssert.NoUnexpectedReceived();

        Prepared quiet = Prepare(
            positions, headings, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f, 0, false, false);
        quiet.Context.TeamMaxRadius = 0f;
        Run(quiet, 1);
        LogAssert.NoUnexpectedReceived();
        Vector3[] unguarded = ReadVelocity(quiet);
        AssertCloseWalks("radius 0", new Pair { Walk0 = walk0, Walk1 = unguarded });
    }

    [Test]
    public void WalkMode_RejectsOutOfRange()
    {
        var pass = new BoidNeighborForcePass();
        Assert.AreEqual(1, pass.WalkMode);
        Assert.Throws<ArgumentOutOfRangeException>(() => pass.WalkMode = 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => pass.WalkMode = -1);
        Assert.DoesNotThrow(() => pass.WalkMode = 2);
        Assert.AreEqual(2, pass.WalkMode);
    }

    private Pair RunPair(
        Vector3[] positions,
        Vector3[] headings,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell,
        int maxNeighbors,
        bool countCapHits,
        bool directSlot7)
    {
        Prepared prepared = Prepare(
            positions, headings, teamIds, slots, wrap, extents, minCell, maxNeighbors, countCapHits, directSlot7);
        Run(prepared, 0);
        if (countCapHits)
        {
            AsyncGPUReadback.WaitAllRequests();
        }

        var pair = new Pair
        {
            Walk0 = ReadVelocity(prepared),
            Cap0 = countCapHits ? prepared.Force.LastCapHitFraction : -1f,
        };
        Rerun(prepared, 1);
        if (countCapHits)
        {
            AsyncGPUReadback.WaitAllRequests();
        }

        pair.Walk1 = ReadVelocity(prepared);
        pair.Cap1 = countCapHits ? prepared.Force.LastCapHitFraction : -1f;
        return pair;
    }

    private void Rerun(Prepared prepared, int walkMode)
    {
        prepared.Cmd.Clear();
        prepared.Particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[prepared.Count]);
        prepared.Force.ResetCapHitWindow();
        prepared.Force.WalkMode = walkMode;
        prepared.Force.Execute(prepared.Context, 0f);
        Graphics.ExecuteCommandBuffer(prepared.Cmd);
    }

    private void DispatchRecorded()
    {
        Graphics.ExecuteCommandBuffer(preparedCmd);
    }

    private CommandBuffer preparedCmd;

    private Vector3[] ReadVelocity(Prepared prepared)
    {
        Vector3[] velocity = new Vector3[prepared.Count];
        prepared.Particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
        return velocity;
    }

    private Prepared Prepare(
        Vector3[] positions,
        Vector3[] headings,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell,
        int maxNeighbors,
        bool countCapHits,
        bool directSlot7)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        var prepared = new Prepared { Count = positions.Length };
        prepared.Particles = new ParticleSet();
        owned.Add(prepared.Particles);
        var fields = new FieldSet();
        owned.Add(fields);
        prepared.Particles.EnsureCapacity(prepared.Count);
        prepared.Particles.RegisterAttribute(BuiltinAttributes.Position);
        prepared.Particles.RegisterAttribute(BuiltinAttributes.Heading);
        prepared.Particles.RegisterAttribute(BuiltinAttributes.TeamId);
        prepared.Particles.RegisterAttribute(BuiltinAttributes.Velocity);
        prepared.Particles.Get(BuiltinAttributes.Position).SetData(positions);
        prepared.Particles.Get(BuiltinAttributes.Heading).SetData(headings);
        prepared.Particles.Get(BuiltinAttributes.TeamId).SetData(teamIds);
        prepared.Particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[prepared.Count]);

        var teamBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
        owned.Add(teamBuffer);
        if (directSlot7)
        {
            var uploaded = new TeamParams[TeamProfile.MaxTeams];
            uploaded[0] = slots[0];
            teamBuffer.SetData(uploaded);
            teamBuffer.SetData(new[] { slots[7] }, 0, 7, 1);
        }
        else
        {
            teamBuffer.SetData(slots);
        }

        prepared.Cmd = new CommandBuffer { name = "SortedWalk" };
        preparedCmd = prepared.Cmd;
        owned.Add(prepared.Cmd);
        ComputeShader dynamics = Load(DynamicsPath);
        ComputeShader hashShader = Load(HashPath);
        ComputeShader boids = Load(BoidPath);
        prepared.Context = new SimContext(prepared.Particles, fields, new[] { dynamics, hashShader, boids }, null);
        prepared.Context.Cmd = prepared.Cmd;
        prepared.Context.Teams = teamBuffer;

        var hash = new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = extents,
            MinCellSize = minCell,
            Wrap = wrap,
        };
        owned.Add(hash);
        prepared.Force = new BoidNeighborForcePass
        {
            MaxNeighbors = maxNeighbors,
            CountCapHits = countCapHits,
        };
        owned.Add(prepared.Force);
        var clear = new ClearVelocityPass();
        clear.Initialize(prepared.Context);
        hash.Initialize(prepared.Context);
        prepared.Force.Initialize(prepared.Context);
        clear.Execute(prepared.Context, 0f);
        hash.Execute(prepared.Context, 0f);
        return prepared;
    }

    private void Run(Prepared prepared, int walkMode)
    {
        prepared.Force.WalkMode = walkMode;
        prepared.Force.Execute(prepared.Context, 0f);
        DispatchRecorded();
    }

    private void AssertWalks(string label, Pair pair)
    {
        int ulp = MaxUlp(pair.Walk0, pair.Walk1);
        TestContext.WriteLine(label + " max ulp " + ulp);
        AssertCloseWalks(label, pair);
    }

    private static void AssertCloseWalks(string label, Pair pair)
    {
        Assert.AreEqual(pair.Walk0.Length, pair.Walk1.Length, label);
        for (int i = 0; i < pair.Walk0.Length; i++)
        {
            AssertClose(pair.Walk1[i].x, pair.Walk0[i].x, label + " x " + i);
            AssertClose(pair.Walk1[i].y, pair.Walk0[i].y, label + " y " + i);
            AssertClose(pair.Walk1[i].z, pair.Walk0[i].z, label + " z " + i);
        }
    }

    private static void AssertBits(Vector3[] expected, Vector3[] actual)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(BitConverter.SingleToInt32Bits(expected[i].x), BitConverter.SingleToInt32Bits(actual[i].x), "x " + i);
            Assert.AreEqual(BitConverter.SingleToInt32Bits(expected[i].y), BitConverter.SingleToInt32Bits(actual[i].y), "y " + i);
            Assert.AreEqual(BitConverter.SingleToInt32Bits(expected[i].z), BitConverter.SingleToInt32Bits(actual[i].z), "z " + i);
        }
    }

    private static void AssertOracle(
        string label,
        Vector3[] velocity,
        Vector3[] positions,
        Vector3[] headings,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell)
    {
        Vector2 size = SpatialHashSet.ComputeLayout(Vector3.zero, extents, minCell).Size;
        for (int i = 0; i < velocity.Length; i++)
        {
            Vector2 expected = OracleForce(i, positions, headings, teamIds, slots, wrap, size);
            AssertClose(velocity[i].x, expected.x, label + " oracle x " + i);
            AssertClose(velocity[i].y, 0f, label + " oracle y " + i);
            AssertClose(velocity[i].z, expected.y, label + " oracle z " + i);
        }
    }

    private static Vector2 OracleForce(
        int index,
        Vector3[] positions,
        Vector3[] headings,
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
                align += new Vector2(headings[j].x, headings[j].z);
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

        if (Vector2.Dot(align, align) > 1e-6f)
        {
            force += align.normalized * team.Weights.y;
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

    private static int MaxUlp(Vector3[] left, Vector3[] right)
    {
        int max = 0;
        int count = Math.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            max = Math.Max(max, Ulp(left[i].x, right[i].x));
            max = Math.Max(max, Ulp(left[i].y, right[i].y));
            max = Math.Max(max, Ulp(left[i].z, right[i].z));
        }

        return max;
    }

    private static int Ulp(float a, float b)
    {
        if (a == b)
        {
            return 0;
        }

        if (float.IsNaN(a) || float.IsNaN(b))
        {
            return int.MaxValue;
        }

        long ia = Ordered(a);
        long ib = Ordered(b);
        long distance = Math.Abs(ia - ib);
        return distance > int.MaxValue ? int.MaxValue : (int)distance;
    }

    private static long Ordered(float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        if (bits < 0)
        {
            bits = ~bits;
        }

        return (uint)bits;
    }

    private static void AssertClose(float actual, float expected, string label)
    {
        float tolerance = 1e-4f + 1e-4f * Mathf.Abs(expected);
        Assert.That(Mathf.Abs(actual - expected), Is.LessThanOrEqualTo(tolerance), label + " actual " + actual + " expected " + expected);
    }

    private static float Next(System.Random random, float min, float max)
    {
        return min + (float)random.NextDouble() * (max - min);
    }

    private static TeamParams[] Slots(params TeamProfile[] profiles)
    {
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        for (int i = 0; i < profiles.Length; i++)
        {
            TeamProfile profile = profiles[i];
            slots[i] = Params(
                profile.SeparationRadius,
                profile.AlignmentRadius,
                profile.CohesionRadius,
                profile.InterGroupSeparationMultiplier,
                profile.SeparationWeight,
                profile.AlignmentWeight,
                profile.CohesionWeight,
                profile.Cruise,
                profile.Turn);
        }

        return slots;
    }

    private static TeamParams Params(
        float sep, float align, float coh, float multiplier,
        float wSep, float wAlign, float wCoh, float cruise, float turn)
    {
        return new TeamParams
        {
            Radii = new Vector4(sep, align, coh, multiplier),
            Weights = new Vector4(wSep, wAlign, wCoh, 0f),
            Motion = new Vector4(cruise, turn, 0f, 0f),
        };
    }

    private static TeamProfile Profile(
        float sep, float align, float coh, float multiplier,
        float wSep, float wAlign, float wCoh, float cruise, float turn)
    {
        return new TeamProfile
        {
            SeparationRadius = sep,
            AlignmentRadius = align,
            CohesionRadius = coh,
            InterGroupSeparationMultiplier = multiplier,
            SeparationWeight = wSep,
            AlignmentWeight = wAlign,
            CohesionWeight = wCoh,
            Cruise = cruise,
            Turn = turn,
        };
    }

    private ComputeShader Load(string path)
    {
        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
        Assume.That(shader != null, path);
        return shader;
    }

    private sealed class Prepared
    {
        public ParticleSet Particles;
        public BoidNeighborForcePass Force;
        public SimContext Context;
        public CommandBuffer Cmd;
        public int Count;
    }

    private struct Pair
    {
        public Vector3[] Walk0;
        public Vector3[] Walk1;
        public float Cap0;
        public float Cap1;
    }
}
