using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class SpatialHashGpuTests
{
    private const string ComputePath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";

    private ParticleSet particles;
    private FieldSet fields;
    private CommandBuffer cmd;
    private ComputeShader shader;
    private BuildSpatialHashPass pass;

    [SetUp]
    public void SetUp()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        Assume.That(shader != null, "SpatialHashPasses.compute must be imported.");
        particles = new ParticleSet();
        fields = new FieldSet();
        cmd = new CommandBuffer { name = "SpatialHashGpu" };
    }

    [TearDown]
    public void TearDown()
    {
        pass?.Dispose();
        pass = null;
        cmd?.Release();
        cmd = null;
        particles?.Dispose();
        particles = null;
        fields?.Dispose();
        fields = null;
    }

    [Test]
    public void CellSets_MatchCpu_MultiBlock()
    {
        RunMatch(new Vector3(20f, 0f, 20f), 1f, true, 5000, 42);
        Assert.AreEqual(1600, pass.Hash.Layout.CellCount);
        Assert.AreEqual(7, pass.Hash.Layout.BlockCount);
    }

    [Test]
    public void CellSets_MatchCpu_256Cells()
    {
        RunMatch(new Vector3(8f, 0f, 8f), 1f, true, 2000, 7);
        Assert.AreEqual(256, pass.Hash.Layout.CellCount);
        Assert.AreEqual(1, pass.Hash.Layout.BlockCount);
    }

    [Test]
    public void CellSets_MatchCpu_257Cells()
    {
        RunMatch(new Vector3(128.5f, 0f, 0.5f), 1f, false, 2000, 11);
        Assert.AreEqual(257, pass.Hash.Layout.Resolution.x);
        Assert.AreEqual(1, pass.Hash.Layout.Resolution.y);
        Assert.AreEqual(2, pass.Hash.Layout.BlockCount);
    }

    [Test]
    public void CellSets_MatchCpu_65536Cells()
    {
        RunMatch(new Vector3(128f, 0f, 128f), 1f, true, 2000, 13);
        Assert.AreEqual(65536, pass.Hash.Layout.CellCount);
        Assert.AreEqual(256, pass.Hash.Layout.BlockCount);
    }

    [Test]
    public void Snapshot_MatchesSourceAttributes()
    {
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(20f, 0f, 20f), 1f);
        const int count = 1000;
        Vector3[] positions = RandomPositions(layout, count, 99);
        Vector3[] headings = new Vector3[count];
        uint[] teams = new uint[count];
        System.Random rng = new System.Random(100);
        for (int i = 0; i < count; i++)
        {
            headings[i] = new Vector3(NextSigned(rng), NextSigned(rng), NextSigned(rng));
            teams[i] = (uint)rng.Next(0, 8);
        }

        Run(new Vector3(20f, 0f, 20f), 1f, true, positions, headings, teams);

        uint[] indices = new uint[count];
        Vector2[] sortedPositions = new Vector2[count];
        Vector2[] sortedDirections = new Vector2[count];
        uint[] sortedTeams = new uint[count];
        pass.Hash.SortedIndices.GetData(indices);
        pass.Hash.SortedPositions.GetData(sortedPositions);
        pass.Hash.SortedDirections.GetData(sortedDirections);
        pass.Hash.SortedTeams.GetData(sortedTeams);

        bool[] seen = new bool[count];
        for (int slot = 0; slot < count; slot++)
        {
            uint source = indices[slot];
            Assert.Less(source, (uint)count);
            Assert.IsFalse(seen[source], "SortedIndices is not a permutation.");
            seen[source] = true;
            AssertBits(sortedPositions[slot].x, positions[source].x);
            AssertBits(sortedPositions[slot].y, positions[source].z);
            AssertBits(sortedDirections[slot].x, headings[source].x);
            AssertBits(sortedDirections[slot].y, headings[source].z);
            Assert.AreEqual(teams[source], sortedTeams[slot]);
        }
    }

    [Test]
    public void DenseSingleCell()
    {
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(8f, 0f, 8f), 1f);
        const int count = 1000;
        Vector3 point = new Vector3(-4.5f, 0f, -2.5f);
        Vector3[] positions = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = point;
        }

        Run(new Vector3(8f, 0f, 8f), 1f, true, positions, null, null);

        uint[] counts = new uint[layout.CellCount];
        pass.Hash.CellCounts.GetData(counts);
        int cell = 5 * layout.Resolution.x + 3;
        Assert.AreEqual(1000u, counts[cell]);
        for (int i = 0; i < counts.Length; i++)
        {
            if (i != cell)
            {
                Assert.AreEqual(0u, counts[i]);
            }
        }
    }

    [Test]
    public void Wrap_UpperBoundary_GoesToFirstCell()
    {
        int cellA = CellOf(new Vector3(8f, 0f, 1f), wrap: true);
        int cellB = CellOf(new Vector3(1f, 0f, 8f), wrap: true);
        SpatialHashLayout layout = pass.Hash.Layout;
        Assert.AreEqual(0, cellA % layout.Resolution.x);
        Assert.AreEqual(0, cellB / layout.Resolution.x);
    }

    [Test]
    public void Wrap_BelowLowerBoundary_GoesToLastCell()
    {
        float below = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(-8f) + 1);
        int cellA = CellOf(new Vector3(below, 0f, 0f), wrap: true);
        int cellB = CellOf(new Vector3(0f, 0f, below), wrap: true);
        SpatialHashLayout layout = pass.Hash.Layout;
        Assert.AreEqual(layout.Resolution.x - 1, cellA % layout.Resolution.x);
        Assert.AreEqual(layout.Resolution.y - 1, cellB / layout.Resolution.x);
    }

    [Test]
    public void NoWrap_OutOfRange_IsClamped()
    {
        int high = CellOf(new Vector3(100f, 0f, 0f), wrap: false);
        int low = CellOf(new Vector3(-100f, 0f, 0f), wrap: false);
        SpatialHashLayout layout = pass.Hash.Layout;
        Assert.AreEqual(layout.Resolution.x - 1, high % layout.Resolution.x);
        Assert.AreEqual(0, low % layout.Resolution.x);
    }

    [Test]
    public void ZeroParticles_ExecuteIsNoOp()
    {
        pass = new BuildSpatialHashPass
        {
            Extents = new Vector3(8f, 0f, 8f),
            MinCellSize = 2f,
            Wrap = true,
        };
        SimContext context = new SimContext(particles, fields, new[] { shader }, null);
        context.Cmd = cmd;
        Assert.DoesNotThrow(() => pass.Initialize(context));
        Assert.DoesNotThrow(() => pass.Execute(context, 0f));
        Assert.IsFalse(pass.LastExecuteDispatched);
        Assert.AreEqual(0, pass.Hash.ParticleCount);
    }

    private int CellOf(Vector3 position, bool wrap)
    {
        Run(new Vector3(8f, 0f, 8f), 2f, wrap, new[] { position }, null, null);
        uint[] cells = new uint[1];
        pass.Hash.ParticleCells.GetData(cells);
        return (int)cells[0];
    }

    private void RunMatch(Vector3 extents, float minCellSize, bool wrap, int count, int seed)
    {
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, extents, minCellSize);
        Vector3[] positions = RandomPositions(layout, count, seed);
        Run(extents, minCellSize, wrap, positions, null, null);

        int cells = layout.CellCount;
        uint[] counts = new uint[cells];
        uint[] starts = new uint[cells];
        uint[] indices = new uint[count];
        pass.Hash.CellCounts.GetData(counts);
        pass.Hash.CellStarts.GetData(starts);
        pass.Hash.SortedIndices.GetData(indices);

        long sum = 0;
        Assert.AreEqual(0u, starts[0]);
        for (int i = 0; i < cells; i++)
        {
            sum += counts[i];
            if (i > 0)
            {
                Assert.GreaterOrEqual(starts[i], starts[i - 1]);
            }
        }

        Assert.AreEqual(count, sum);

        List<uint>[] expected = new List<uint>[cells];
        for (int i = 0; i < cells; i++)
        {
            expected[i] = new List<uint>();
        }

        for (int i = 0; i < count; i++)
        {
            expected[CpuCell(positions[i], layout, wrap)].Add((uint)i);
        }

        for (int cell = 0; cell < cells; cell++)
        {
            var actual = new HashSet<uint>();
            uint start = starts[cell];
            for (uint k = 0; k < counts[cell]; k++)
            {
                actual.Add(indices[start + k]);
            }

            Assert.IsTrue(actual.SetEquals(expected[cell]), "Cell " + cell + " set mismatch.");
        }
    }

    private void Run(
        Vector3 extents,
        float minCellSize,
        bool wrap,
        Vector3[] positions,
        Vector3[] headings,
        uint[] teams)
    {
        int count = positions.Length;
        particles.EnsureCapacity(count);
        particles.RegisterAttribute(BuiltinAttributes.Position);
        particles.RegisterAttribute(BuiltinAttributes.Heading);
        particles.RegisterAttribute(BuiltinAttributes.TeamId);

        if (headings == null)
        {
            headings = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                headings[i] = Vector3.right;
            }
        }

        if (teams == null)
        {
            teams = new uint[count];
        }

        particles.Get(BuiltinAttributes.Position).SetData(positions);
        particles.Get(BuiltinAttributes.Heading).SetData(headings);
        particles.Get(BuiltinAttributes.TeamId).SetData(teams);

        pass?.Dispose();
        pass = new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = extents,
            MinCellSize = minCellSize,
            Wrap = wrap,
        };

        SimContext context = new SimContext(particles, fields, new[] { shader }, null);
        context.Cmd = cmd;
        pass.Initialize(context);
        pass.Execute(context, 0f);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
    }

    private static Vector3[] RandomPositions(SpatialHashLayout layout, int count, int seed)
    {
        System.Random rng = new System.Random(seed);
        Vector3[] positions = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector3(
                Interior(rng, layout.Origin.x, layout.CellSize.x, layout.Resolution.x),
                0f,
                Interior(rng, layout.Origin.y, layout.CellSize.y, layout.Resolution.y));
        }

        return positions;
    }

    private static float Interior(System.Random rng, float origin, float cell, int resolution)
    {
        int c = rng.Next(resolution);
        float margin = 1e-3f * cell;
        float lo = origin + c * cell + margin;
        float hi = origin + (c + 1f) * cell - margin;
        return lo + (float)rng.NextDouble() * (hi - lo);
    }

    private static int CpuCell(Vector3 position, SpatialHashLayout layout, bool wrap)
    {
        int x = Axis(position.x, layout.Origin.x, layout.CellSize.x, layout.Resolution.x, wrap);
        int z = Axis(position.z, layout.Origin.y, layout.CellSize.y, layout.Resolution.y, wrap);
        int index = z * layout.Resolution.x + x;
        int last = layout.CellCount - 1;
        if (index > last)
        {
            index = last;
        }

        return index;
    }

    private static int Axis(float value, float origin, float cell, int resolution, bool wrap)
    {
        int c = Mathf.FloorToInt((value - origin) / cell);
        if (!wrap)
        {
            if (c < 0)
            {
                return 0;
            }

            return c > resolution - 1 ? resolution - 1 : c;
        }

        int wrapped = c % resolution;
        if (wrapped < 0)
        {
            wrapped += resolution;
        }

        return wrapped;
    }

    private static float NextSigned(System.Random rng)
    {
        return (float)(rng.NextDouble() * 2.0 - 1.0);
    }

    private static void AssertBits(float actual, float expected)
    {
        Assert.AreEqual(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
    }
}
