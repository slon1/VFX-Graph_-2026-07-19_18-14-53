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
public class BoidNeighborForcePackedTests
{
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";
    private const float DirY = 0.25f;

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

    [Test]
    public void PackedRecord_MatchesPositionsTeamsAndDirBound()
    {
        float minDenormal = BitConverter.Int32BitsToSingle(1);
        var dirValues = new List<float>
        {
            0f, 1f, -1f, 0.5f, -0.5f,
            1e-30f, -1e-30f,
            1e-40f, -1e-40f,
            1e-45f, -1e-45f,
            minDenormal, -minDenormal,
        };
        var random = new System.Random(7);
        for (int i = 0; i < 4; i++)
        {
            dirValues.Add(Next(random, -1f, 1f));
        }

        uint[] teams = { 0u, 1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 20u, uint.MaxValue };
        int count = dirValues.Count * teams.Length;
        Vector3[] positions = new Vector3[count];
        Vector3[] headings = new Vector3[count];
        uint[] teamIds = new uint[count];
        int n = 0;
        for (int t = 0; t < teams.Length; t++)
        {
            for (int d = 0; d < dirValues.Count; d++)
            {
                positions[n] = new Vector3(-14f + (n % 14) * 2f, 0f, -14f + (n / 14) * 2f);
                headings[n] = new Vector3(dirValues[d], 0f, DirY);
                teamIds[n] = teams[t];
                n++;
            }
        }

        Prepared prepared = Begin(positions, headings, teamIds, Slots(Profile(1f, 1f, 1f, 1f, 0f, 0f, 0f, 1f, 1f)), true, new Vector3(16f, 0f, 16f), 4f, 0, false, false, true);
        RecordHash(prepared);
        Dispatch(prepared);
        Assert.IsTrue(prepared.Hash.Hash.PackedWritten);

        var indices = new uint[count];
        var sortedPos = new Vector2[count];
        var sortedDir = new Vector2[count];
        var packed = new PackedRecord[count];
        prepared.Hash.Hash.SortedIndices.GetData(indices);
        prepared.Hash.Hash.SortedPositions.GetData(sortedPos);
        prepared.Hash.Hash.SortedDirections.GetData(sortedDir);
        prepared.Hash.Hash.SortedPacked.GetData(packed);

        int flushed = 0;
        for (int slot = 0; slot < count; slot++)
        {
            uint id = indices[slot];
            float inputX = headings[id].x;
            Assert.AreEqual(BitConverter.SingleToInt32Bits(sortedPos[slot].x), BitConverter.SingleToInt32Bits(positions[id].x), "pos x " + slot);
            Assert.AreEqual(BitConverter.SingleToInt32Bits(sortedPos[slot].y), BitConverter.SingleToInt32Bits(positions[id].z), "pos z " + slot);
            Assert.AreEqual(unchecked((uint)BitConverter.SingleToInt32Bits(sortedPos[slot].x)), packed[slot].X, "packed x " + slot);
            Assert.AreEqual(unchecked((uint)BitConverter.SingleToInt32Bits(sortedPos[slot].y)), packed[slot].Y, "packed y " + slot);
            Assert.AreEqual(unchecked((uint)BitConverter.SingleToInt32Bits(sortedDir[slot].y)), packed[slot].W, "packed w " + slot);
            Assert.AreEqual(Math.Min(teamIds[id], 7u), packed[slot].Z & 7u, "team " + slot);

            if (IsSubnormal(inputX) && BitConverter.SingleToInt32Bits(sortedDir[slot].x) == 0)
            {
                flushed++;
                TestContext.WriteLine("flushed denormal id " + id + " input " + inputX);
                continue;
            }

            float recovered = BitConverter.Int32BitsToSingle(unchecked((int)(packed[slot].Z & ~7u)));
            float delta = Mathf.Abs(recovered - inputX);
            float limit = PackLimit(inputX);
            Assert.That(delta, Is.LessThanOrEqualTo(limit), "dir.x " + slot + " input " + inputX + " recovered " + recovered);
        }

        TestContext.WriteLine("flushed denormals " + flushed + " of " + count);
    }

    [Test]
    public void Oracle_TwoTeams_Walk2MatchesWalk1()
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
        AssertScenario("two teams", positions, headings, teamIds, slots, true, new Vector3(16f, 0f, 16f), 4f, 0, false, false, true, true);
    }

    [Test]
    public void CapOff_AndCapDense_Walk2MatchesWalk1()
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
        AssertScenario("cap off", positions, headings, teamIds, slots, false, extents, 2f, 0, false, false, false, true);
        AssertScenario("cap dense", positions, headings, teamIds, slots, false, extents, 2f, 1, true, false, false, false);
    }

    [Test]
    public void Seam_Wrap_NoWrap_AndContiguousShift_MatchWalk1()
    {
        Vector3[] seamPos = { new Vector3(-7.7f, 0f, 0f), new Vector3(7.7f, 0f, 0f) };
        Vector3[] seamHead = { Vector3.right, Vector3.left };
        uint[] teams = { 0u, 0u };
        TeamParams[] seamSlots = Slots(Profile(1f, 1f, 1f, 1f, 1.2f, 0.5f, 0.4f, 6f, 4f));
        AssertScenario("seam", seamPos, seamHead, teams, seamSlots, true, new Vector3(8f, 0f, 8f), 2f, 0, false, false, false, true);

        Vector3[] far = { new Vector3(-2.9f, 0f, 0f), new Vector3(2.9f, 0f, 0f) };
        Vector3[] headings = { Vector3.right, Vector3.right };
        TeamParams[] farSlots = Slots(Profile(6f, 6f, 6f, 1f, 1f, 0f, 0f, 0f, 0f));
        AssertScenario("no wrap far", far, headings, teams, farSlots, false, new Vector3(3f, 0f, 3f), 2f, 0, false, false, false, false);

        Vector3[] near = { Vector3.zero, new Vector3(0.4f, 0f, 0.1f) };
        AssertScenario("no wrap interior", near, headings, teams, farSlots, false, new Vector3(8f, 0f, 8f), 2f, 0, false, false, false, true);

        Vector3[] positions =
        {
            new Vector3(-2.6f, 0f, -2.6f),
            new Vector3(-2.4f, 0f, -2.5f),
            new Vector3(-2.5f, 0f, -2.3f),
            new Vector3(-2.2f, 0f, -2.4f),
            new Vector3(-2.5f, 0f, -2.5f),
            new Vector3(2.5f, 0f, 2.5f),
        };
        Vector3[] shiftHead =
        {
            Vector3.right, Vector3.up, Vector3.left, Vector3.right, Vector3.right, Vector3.left,
        };
        uint[] shiftTeams = { 0u, 0u, 0u, 0u, 0u, 0u };
        TeamParams[] shiftSlots = Slots(Profile(2f, 2f, 2f, 1f, 1.2f, 0.5f, 0.4f, 4f, 2f));
        AssertScenario("shift seam", positions, shiftHead, shiftTeams, shiftSlots, true, new Vector3(3f, 0f, 3f), 2f, 0, false, false, false, true);
    }

    [Test]
    public void TeamId_Above7_AndSparseCounts_MatchWalk1()
    {
        Vector3[] positions = { Vector3.zero, new Vector3(0.5f, 0f, 0f) };
        Vector3[] headings = { Vector3.right, Vector3.right };
        uint[] teamIds = { 100u, 0u };
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = Params(2f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f);
        slots[7] = Params(2f, 0f, 0f, 1f, 5f, 0f, 0f, 9f, 8f);
        AssertScenario("team 100", positions, headings, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f, 0, false, true, false, true);

        const int count = 70;
        Vector3[] sparsePos = new Vector3[count];
        Vector3[] sparseHead = new Vector3[count];
        uint[] sparseTeams = new uint[count];
        for (int i = 0; i < count; i++)
        {
            sparsePos[i] = new Vector3(-6f + (i % 10) * 1.3f, 0f, -6f + (i / 10) * 1.7f);
            float angle = i * 0.2f;
            sparseHead[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            sparseTeams[i] = (uint)(i & 1);
        }

        TeamParams[] sparseSlots = Slots(
            Profile(1.5f, 1.5f, 1.5f, 1f, 1f, 0.4f, 0.3f, 4f, 2f),
            Profile(1.5f, 1.5f, 1.5f, 0.5f, 0.8f, 0.2f, 0.2f, 4f, 2f));
        AssertScenario("70 sparse", sparsePos, sparseHead, sparseTeams, sparseSlots, true, new Vector3(16f, 0f, 16f), 4f, 48, true, false, false, false);

        Vector3[] oneCell = new Vector3[17];
        Vector3[] oneHead = new Vector3[17];
        uint[] oneTeams = new uint[17];
        for (int i = 0; i < oneCell.Length; i++)
        {
            oneCell[i] = new Vector3(0.03f * i, 0f, 0.02f * (i % 3));
            oneHead[i] = Vector3.right;
        }

        AssertScenario("one cell", oneCell, oneHead, oneTeams, Slots(Profile(2f, 2f, 2f, 1f, 1f, 0.2f, 0.2f, 1f, 1f)), false, new Vector3(8f, 0f, 8f), 2f, 0, false, false, false, true);
    }

    [Test]
    public void Random_ThirtySeeds_Walk2MatchesWalk1()
    {
        Vector3 worldExtents = new Vector3(16f, 0f, 16f);
        const float minCell = 4f;
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, worldExtents, minCell);
        int worst = 0;
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
                positions[i] = new Vector3(Next(random, -16f, 16f), 0f, Next(random, -16f, 16f));
                teamIds[i] = (uint)(i & 1);
                float baseAngle = teamIds[i] == 0u ? 0f : Mathf.PI;
                float angle = baseAngle + Next(random, -1.2f, 1.2f);
                headings[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                int ix = Mathf.FloorToInt((positions[i].x - layout.Origin.x) / layout.CellSize.x);
                int iy = Mathf.FloorToInt((positions[i].z - layout.Origin.y) / layout.CellSize.y);
                if (ix == 0 || ix == layout.Resolution.x - 1 || iy == 0 || iy == layout.Resolution.y - 1)
                {
                    edge++;
                }
            }

            if (wrap)
            {
                Assert.Greater(edge, 0, "seed " + seed);
            }

            TeamParams[] slots = Slots(
                Profile(3.5f, 3.5f, 3.5f, 1.4f, 1f, 0.5f, 0.4f, 4f, 2f),
                Profile(3.5f, 3.5f, 3.5f, 0.4f, 0.7f, 0.3f, 0.3f, 3f, 1f));
            int ulp = AssertScenario(
                "seed " + seed + " n " + count + " wrap " + wrap + " cap " + cap,
                positions, headings, teamIds, slots, wrap, worldExtents, minCell, cap, cap > 0, false, false, cap == 0);
            worst = Math.Max(worst, ulp);
        }

        TestContext.WriteLine("random walk2 max ulp " + worst);
    }

    [Test]
    public void Packed_IsNotWrittenForWalk0OrWalk1()
    {
        Vector3[] positions = { Vector3.zero, new Vector3(0.4f, 0f, 0.2f), new Vector3(1f, 0f, -0.3f), new Vector3(-0.5f, 0f, 0.6f) };
        Vector3[] headings = { Vector3.right, Vector3.left, Vector3.right, Vector3.left };
        uint[] teams = { 0u, 0u, 1u, 1u };
        Prepared prepared = Begin(positions, headings, teams, Slots(Profile(1f, 1f, 1f, 1f, 1f, 0.2f, 0.2f, 1f, 1f)), false, new Vector3(8f, 0f, 8f), 2f, 0, false, false, false);
        var sentinel = new PackedRecord[positions.Length];
        for (int i = 0; i < sentinel.Length; i++)
        {
            sentinel[i] = new PackedRecord { X = 0xDEADBEEFu, Y = 0xDEADBEEFu, Z = 0xDEADBEEFu, W = 0xDEADBEEFu };
        }

        prepared.Hash.Hash.SortedPacked.SetData(sentinel);
        RecordHash(prepared);
        Dispatch(prepared);
        Assert.IsFalse(prepared.Hash.Hash.PackedWritten);
        var after = new PackedRecord[positions.Length];
        prepared.Hash.Hash.SortedPacked.GetData(after);
        for (int i = 0; i < after.Length; i++)
        {
            Assert.AreEqual(0xDEADBEEFu, after[i].X);
            Assert.AreEqual(0xDEADBEEFu, after[i].Y);
            Assert.AreEqual(0xDEADBEEFu, after[i].Z);
            Assert.AreEqual(0xDEADBEEFu, after[i].W);
        }

        prepared.Force.WalkMode = 2;
        RecordHash(prepared);
        Dispatch(prepared);
        Assert.IsTrue(prepared.Hash.Hash.PackedWritten);
        prepared.Hash.Hash.SortedPacked.GetData(after);
        bool changed = false;
        for (int i = 0; i < after.Length; i++)
        {
            if (after[i].X != 0xDEADBEEFu || after[i].Y != 0xDEADBEEFu || after[i].Z != 0xDEADBEEFu || after[i].W != 0xDEADBEEFu)
            {
                changed = true;
            }
        }

        Assert.IsTrue(changed);
    }

    [Test]
    public void WalkMode_RequestsPacked_AndFallbacksMatch()
    {
        Vector3[] positions = { Vector3.zero, new Vector3(0.4f, 0f, 0.1f) };
        Vector3[] headings = { Vector3.right, Vector3.right };
        uint[] teams = { 0u, 0u };
        TeamParams[] slots = Slots(Profile(2f, 2f, 2f, 1f, 1f, 0.4f, 0.2f, 1f, 1f));
        Prepared prepared = Begin(positions, headings, teams, slots, false, new Vector3(8f, 0f, 8f), 2f, 0, false, false, false);
        Assert.IsFalse(prepared.Hash.Hash.PackedRequested);
        prepared.Force.WalkMode = 2;
        Assert.IsTrue(prepared.Hash.Hash.PackedRequested);
        prepared.Force.WalkMode = 0;
        Assert.IsFalse(prepared.Hash.Hash.PackedRequested);
        prepared.Force.WalkMode = 1;
        Assert.IsFalse(prepared.Hash.Hash.PackedRequested);

        RecordHash(prepared);
        RunRecorded(prepared, 1);
        Vector3[] walk1 = ReadVelocity(prepared);
        RunRecorded(prepared, 2);
        RunRecorded(prepared, 2);
        LogAssert.NoUnexpectedReceived();
        LogAssert.Expect(LogType.Warning, new Regex("пакет не пишется"));
        RunRecorded(prepared, 2);
        Assert.AreEqual(1, prepared.Force.EffectiveWalkMode);
        AssertCloseWalks("unpacked walk 2", walk1, ReadVelocity(prepared));

        prepared.Force.WalkMode = 2;
        RecordHash(prepared);
        ZeroVelocity(prepared);
        prepared.Force.Execute(prepared.Context, 0f);
        Dispatch(prepared);
        Assert.AreEqual(2, prepared.Force.EffectiveWalkMode);

        Prepared guarded = Begin(positions, headings, teams, slots, true, new Vector3(8f, 0f, 8f), 2f, 0, false, false, true);
        RecordHash(guarded);
        RunRecorded(guarded, 0);
        Vector3[] walk0 = ReadVelocity(guarded);
        guarded.Context.TeamMaxRadius = 100f;
        LogAssert.Expect(LogType.Warning, new Regex("executing walk 0"));
        RunRecorded(guarded, 2);
        Assert.AreEqual(0, guarded.Force.EffectiveWalkMode);
        AssertBits(walk0, ReadVelocity(guarded));
        RunRecorded(guarded, 2);
        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void MissingPackedKernel_WarnsOnce_AndRunsWalk1()
    {
        Vector3[] positions = { Vector3.zero, new Vector3(0.3f, 0f, 0.1f) };
        Vector3[] headings = { Vector3.right, Vector3.left };
        uint[] teams = { 0u, 0u };
        Prepared prepared = Begin(positions, headings, teams, Slots(Profile(1f, 1f, 1f, 1f, 1f, 0.2f, 0.2f, 1f, 1f)), true, new Vector3(8f, 0f, 8f), 2f, 0, false, false, true);
        RecordHash(prepared);
        FieldInfo field = typeof(BoidNeighborForcePass).GetField("packedKernel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(prepared.Force, default(KernelHandle));
        LogAssert.Expect(LogType.Warning, new Regex("кернел обхода не найден"));
        RunRecorded(prepared, 2);
        Assert.AreEqual(1, prepared.Force.EffectiveWalkMode);
        RunRecorded(prepared, 2);
        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void Walk_SwitchesDuringTheRun_OnOneWorld()
    {
        Vector3[] positions = { Vector3.zero, new Vector3(0.5f, 0f, 0.2f), new Vector3(-0.4f, 0f, 0.3f) };
        Vector3[] headings = { Vector3.right, new Vector3(0.2f, 0f, 0.9f), Vector3.left };
        uint[] teams = { 0u, 0u, 0u };
        TeamParams[] slots = Slots(Profile(2f, 2f, 2f, 1f, 1f, 0.5f, 0.3f, 2f, 1f));
        Prepared prepared = Begin(positions, headings, teams, slots, true, new Vector3(8f, 0f, 8f), 2f, 0, false, false, false);
        RecordHash(prepared);
        RunRecorded(prepared, 0);
        Vector3[] before = ReadVelocity(prepared);
        RunRecorded(prepared, 1);
        prepared.Force.WalkMode = 2;
        RecordHash(prepared);
        RunRecorded(prepared, 2);
        Assert.AreEqual(2, prepared.Force.EffectiveWalkMode);
        RunRecorded(prepared, 1);
        RunRecorded(prepared, 0);
        Vector3[] after = ReadVelocity(prepared);
        AssertBits(before, after);
        prepared.Force.WalkMode = 2;
        RecordHash(prepared);
        RunRecorded(prepared, 2);
        Assert.AreEqual(2, prepared.Force.EffectiveWalkMode);
        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void CancellingHeadings_StayInsideAnalyticBound()
    {
        Vector3[] positions =
        {
            Vector3.zero,
            new Vector3(0.2f, 0f, 0f),
            new Vector3(-0.1f, 0f, 0.15f),
            new Vector3(0.05f, 0f, -0.2f),
        };
        float x0 = DirX(0.60f, 4);
        float x1 = DirX(0.51f, 3);
        float x2 = DirX(0.51f, 4);
        float x3 = DirX(-(x1 + x2 - 0.03f), 3);
        AssertPackPattern(x0, 4);
        AssertPackPattern(x1, 3);
        AssertPackPattern(x2, 4);
        AssertPackPattern(x3, 3);
        Vector2 align0 = new Vector2(x1 + x2 + x3, 0.25f + 0.30f - 0.51f);
        Assert.That(align0.x, Is.EqualTo(0.03f).Within(1e-4f));
        Assert.That(align0.y, Is.EqualTo(0.04f).Within(1e-4f));
        Vector3[] headings =
        {
            new Vector3(x0, 0f, 0.20f),
            new Vector3(x1, 0f, 0.25f),
            new Vector3(x2, 0f, 0.30f),
            new Vector3(x3, 0f, -0.51f),
        };
        uint[] teams = { 0u, 0u, 0u, 0u };
        TeamParams[] slots = Slots(Profile(2f, 2f, 2f, 1f, 0f, 1f, 0f, 1f, 1f));
        Vector3 extents = new Vector3(8f, 0f, 8f);
        Prepared prepared = Begin(positions, headings, teams, slots, false, extents, 2f, 0, false, false, true);
        RecordHash(prepared);
        RunRecorded(prepared, 1);
        Vector3[] walk1 = ReadVelocity(prepared);
        RunRecorded(prepared, 2);
        Vector3[] walk2 = ReadVelocity(prepared);
        float worst = 0f;
        for (int i = 0; i < positions.Length; i++)
        {
            AlignSample sample = AlignOf(i, positions, headings, teams, slots, false, extents, 2f);
            float deviation = (walk2[i] - walk1[i]).magnitude;
            worst = Mathf.Max(worst, deviation);
            if (sample.Count == 0 || sample.Magnitude < 1e-6f)
            {
                continue;
            }

            float bound = 1.5f * sample.Weight * sample.Count * 4.8e-7f / sample.Magnitude;
            Assert.That(deviation, Is.LessThanOrEqualTo(bound), "i " + i + " align " + sample.Magnitude + " deviation " + deviation + " bound " + bound);
        }

        Assert.That(worst, Is.GreaterThan(0f), "тест пустой: отклонение walk 2 от walk 1 равно 0");
        string deviationText = worst.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        TestContext.WriteLine("cancelling deviation " + deviationText);
    }

    private static float DirX(float magnitude, int lowBits)
    {
        int bits = BitConverter.SingleToInt32Bits(magnitude);
        bits = (bits & ~7) | lowBits;
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static void AssertPackPattern(float value, int lowBits)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        Assert.AreEqual(lowBits, bits & 7);
        float abs = Mathf.Abs(value);
        Assert.That(abs, Is.GreaterThanOrEqualTo(0.5f).And.LessThan(1f));
    }

    private int AssertScenario(
        string label,
        Vector3[] positions,
        Vector3[] headings,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell,
        int cap,
        bool countCap,
        bool directSlot7,
        bool excludeSmallAlign,
        bool checkOracle)
    {
        Prepared prepared = Begin(positions, headings, teamIds, slots, wrap, extents, minCell, cap, countCap, directSlot7, true);
        RecordHash(prepared);
        RunRecorded(prepared, 0);
        if (countCap)
        {
            AsyncGPUReadback.WaitAllRequests();
        }

        Vector3[] walk0 = ReadVelocity(prepared);
        float cap0 = countCap ? prepared.Force.LastCapHitFraction : -1f;
        RunRecorded(prepared, 1);
        if (countCap)
        {
            AsyncGPUReadback.WaitAllRequests();
        }

        Vector3[] walk1 = ReadVelocity(prepared);
        float cap1 = countCap ? prepared.Force.LastCapHitFraction : -1f;
        RunRecorded(prepared, 2);
        if (countCap)
        {
            AsyncGPUReadback.WaitAllRequests();
        }

        Vector3[] walk2 = ReadVelocity(prepared);
        float cap2 = countCap ? prepared.Force.LastCapHitFraction : -1f;
        Assert.AreEqual(2, prepared.Force.EffectiveWalkMode, label);
        AssertCloseWalks(label + " walk1", walk0, walk1);
        int ulp = MaxUlp(walk1, walk2);
        int excluded = 0;
        for (int i = 0; i < positions.Length; i++)
        {
            bool skip = false;
            if (excludeSmallAlign)
            {
                AlignSample sample = AlignOf(i, positions, headings, teamIds, slots, wrap, extents, minCell);
                if (sample.Magnitude < 0.3f)
                {
                    excluded++;
                    skip = true;
                }
            }

            if (skip)
            {
                continue;
            }

            AssertClose(walk2[i].x, walk1[i].x, label + " x " + i);
            AssertClose(walk2[i].y, walk1[i].y, label + " y " + i);
            AssertClose(walk2[i].z, walk1[i].z, label + " z " + i);
            AssertClose(walk2[i].x, walk0[i].x, label + " walk0 x " + i);
            AssertClose(walk2[i].z, walk0[i].z, label + " walk0 z " + i);
            if (checkOracle)
            {
                Vector2 size = SpatialHashSet.ComputeLayout(Vector3.zero, extents, minCell).Size;
                Vector2 expected = OracleForce(i, positions, headings, teamIds, slots, wrap, size);
                AssertClose(walk2[i].x, expected.x, label + " oracle x " + i);
                AssertClose(walk2[i].z, expected.y, label + " oracle z " + i);
            }
        }

        TestContext.WriteLine(label + " walk2 max ulp " + ulp + " excluded " + excluded + " of " + positions.Length);
        if (excludeSmallAlign)
        {
            Assert.That(excluded, Is.LessThanOrEqualTo(positions.Length * 0.05f), label + " excluded " + excluded);
        }

        if (countCap)
        {
            Assert.AreEqual(cap1, cap2, 0f, label + " cap");
            Assert.AreEqual(cap0, cap1, 0f, label);
        }

        return ulp;
    }

    private Prepared Begin(
        Vector3[] positions,
        Vector3[] headings,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell,
        int maxNeighbors,
        bool countCapHits,
        bool directSlot7,
        bool requestPacked)
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

        var teamBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
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

        prepared.Cmd = new CommandBuffer { name = "PackedWalk" };
        owned.Add(prepared.Cmd);
        prepared.Context = new SimContext(
            prepared.Particles,
            fields,
            new[] { Load(DynamicsPath), Load(HashPath), Load(BoidPath) },
            null);
        prepared.Context.Cmd = prepared.Cmd;
        prepared.Context.Teams = teamBuffer;
        prepared.Hash = new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = extents,
            MinCellSize = minCell,
            Wrap = wrap,
        };
        owned.Add(prepared.Hash);
        prepared.Clear = new ClearVelocityPass();
        prepared.Force = new BoidNeighborForcePass
        {
            MaxNeighbors = maxNeighbors,
            CountCapHits = countCapHits,
        };
        owned.Add(prepared.Force);
        prepared.Clear.Initialize(prepared.Context);
        prepared.Hash.Initialize(prepared.Context);
        if (requestPacked)
        {
            prepared.Force.WalkMode = 2;
        }

        prepared.Force.Initialize(prepared.Context);
        return prepared;
    }

    private void RecordHash(Prepared prepared)
    {
        prepared.Cmd.Clear();
        prepared.Clear.Execute(prepared.Context, 0f);
        prepared.Hash.Execute(prepared.Context, 0f);
        Dispatch(prepared);
        prepared.Cmd.Clear();
    }

    private void RunRecorded(Prepared prepared, int walkMode)
    {
        prepared.Cmd.Clear();
        ZeroVelocity(prepared);
        prepared.Force.ResetCapHitWindow();
        prepared.Force.WalkMode = walkMode;
        prepared.Force.Execute(prepared.Context, 0f);
        Dispatch(prepared);
    }

    private static void ZeroVelocity(Prepared prepared)
    {
        prepared.Particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[prepared.Count]);
    }

    private static void Dispatch(Prepared prepared)
    {
        Graphics.ExecuteCommandBuffer(prepared.Cmd);
    }

    private static Vector3[] ReadVelocity(Prepared prepared)
    {
        Vector3[] velocity = new Vector3[prepared.Count];
        prepared.Particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
        return velocity;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PackedRecord
    {
        public uint X;
        public uint Y;
        public uint Z;
        public uint W;
    }

    private struct AlignSample
    {
        public float Magnitude;
        public int Count;
        public float Weight;
    }

    private static AlignSample AlignOf(
        int index,
        Vector3[] positions,
        Vector3[] headings,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell)
    {
        uint selfTeam = Math.Min(teamIds[index], 7u);
        TeamParams team = slots[selfTeam];
        float alignR2 = team.Radii.y * team.Radii.y;
        Vector2 self = new Vector2(positions[index].x, positions[index].z);
        Vector2 size = SpatialHashSet.ComputeLayout(Vector3.zero, extents, minCell).Size;
        Vector2 align = Vector2.zero;
        int count = 0;
        for (int j = 0; j < positions.Length; j++)
        {
            if (j == index)
            {
                continue;
            }

            Vector2 d = new Vector2(positions[j].x, positions[j].z) - self;
            if (wrap)
            {
                d = new Vector2(d.x - size.x * Mathf.Round(d.x / size.x), d.y - size.y * Mathf.Round(d.y / size.y));
            }

            float r2 = Vector2.Dot(d, d);
            if (r2 < 1e-6f || Math.Min(teamIds[j], 7u) != selfTeam || r2 >= alignR2)
            {
                continue;
            }

            align += new Vector2(headings[j].x, headings[j].z);
            count++;
        }

        return new AlignSample { Magnitude = align.magnitude, Count = count, Weight = team.Weights.y };
    }

    private static bool IsSubnormal(float value)
    {
        uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
        return (bits & 0x7f800000u) == 0u && (bits & 0x7fffffffu) != 0u;
    }

    private static float PackLimit(float value)
    {
        float relative = Mathf.Abs(value) / 2097152f;
        float absolute = BitConverter.Int32BitsToSingle(4);
        return Mathf.Max(relative, absolute);
    }

    private static void AssertCloseWalks(string label, Vector3[] expected, Vector3[] actual)
    {
        Assert.AreEqual(expected.Length, actual.Length, label);
        for (int i = 0; i < expected.Length; i++)
        {
            AssertClose(actual[i].x, expected[i].x, label + " x " + i);
            AssertClose(actual[i].y, expected[i].y, label + " y " + i);
            AssertClose(actual[i].z, expected[i].z, label + " z " + i);
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

    private static Vector2 OracleForce(
        int index, Vector3[] positions, Vector3[] headings, uint[] teamIds, TeamParams[] slots, bool wrap, Vector2 size)
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
            Vector2 d = new Vector2(positions[j].x, positions[j].z) - self;
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
                sep += (-d / (r2 + 1e-3f)) * (same ? 1f : team.Radii.w);
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
        for (int i = 0; i < left.Length; i++)
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
                profile.SeparationRadius, profile.AlignmentRadius, profile.CohesionRadius,
                profile.InterGroupSeparationMultiplier, profile.SeparationWeight, profile.AlignmentWeight,
                profile.CohesionWeight, profile.Cruise, profile.Turn);
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
        public BuildSpatialHashPass Hash;
        public ClearVelocityPass Clear;
        public BoidNeighborForcePass Force;
        public SimContext Context;
        public CommandBuffer Cmd;
        public int Count;
    }
}
