using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class FieldNeighborCollectTests
{
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string FieldPath = "Assets/Shaders/GPU/Passes/FieldNeighborPasses.compute";
    private const double FixedUnit = 1.0 / FieldNeighborSet.FixedPointScale;

    [Test]
    public void Gather_OneParticleOnTexelCenter_IsOne()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(8f);
        Vector2 p = layout.Origin + new Vector2(3f, 2f);
        using (Gpu gpu = new Gpu(new[] { p }, new[] { Vector2.right }, new[] { 0 }, layout, FieldCollectMode.Gather))
        {
            gpu.Build();
            float[] raw = gpu.ReadRaw();
            int best = 0;
            for (int i = 1; i < layout.CellCount; i++)
            {
                if (raw[i * 6] > raw[best * 6])
                {
                    best = i;
                }
            }

            int expect = 2 * layout.Resolution.x + 3;
            Assert.AreEqual(expect, best);
            Assert.AreEqual(1f, raw[expect * 6], 1e-4f);
            Assert.AreEqual(1d, Sum(raw, 0), 1e-4d);
        }
    }

    [Test]
    public void Gather_RandomFiveThousand_MatchesReference()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(64f);
        int count = 5000;
        var random = new System.Random(3);
        Vector2[] xz = new Vector2[count];
        Vector2[] headings = new Vector2[count];
        int[] teams = new int[count];
        for (int i = 0; i < count; i++)
        {
            xz[i] = layout.Origin + new Vector2(
                (float)random.NextDouble() * layout.Size.x,
                (float)random.NextDouble() * layout.Size.y);
            float angle = (float)(random.NextDouble() * Math.PI * 2.0);
            headings[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            teams[i] = i & 1;
        }

        double[] expected = FieldNeighborReference.Collect(xz, headings, teams, layout);
        using (Gpu gpu = new Gpu(xz, headings, teams, layout, FieldCollectMode.Gather))
        {
            gpu.Build();
            float[] actual = gpu.ReadRaw();
            AssertClose(expected, actual, 1e-4);
        }
    }

    [Test]
    public void Gather_SeamCornersAndCellEdges_MatchReference()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(8f);
        Vector2 origin = layout.Origin;
        Vector2 size = layout.Size;
        Vector2[] xz =
        {
            origin,
            origin + new Vector2(size.x - 1e-3f, 0.25f),
            origin + new Vector2(0.25f, size.y - 1e-3f),
            origin + size - new Vector2(1e-3f, 1e-3f),
            origin + new Vector2(2f, 3.5f),
            origin + new Vector2(1.5f, 1.5f),
        };
        Vector2[] headings = new Vector2[xz.Length];
        int[] teams = new int[xz.Length];
        for (int i = 0; i < xz.Length; i++)
        {
            headings[i] = new Vector2(i % 2 == 0 ? 1f : -1f, 0.25f);
            teams[i] = i % 2;
        }

        double[] expected = FieldNeighborReference.Collect(xz, headings, teams, layout);
        using (Gpu gpu = new Gpu(xz, headings, teams, layout, FieldCollectMode.Gather))
        {
            gpu.Build();
            AssertClose(expected, gpu.ReadRaw(), 1e-4);
        }
    }

    [Test]
    public void Gather_SumN_EqualsTeamCounts_AndEmptyTeamIsZero()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        Vector2[] xz = { layout.Origin + new Vector2(1.2f, 2.2f), layout.Origin + new Vector2(4.7f, 5.1f) };
        Vector2[] headings = { Vector2.right, Vector2.up };
        int[] teams = { 0, 0 };
        using (Gpu gpu = new Gpu(xz, headings, teams, layout, FieldCollectMode.Gather))
        {
            gpu.Build();
            float[] raw = gpu.ReadRaw();
            Assert.AreEqual(2d, Sum(raw, 0), 1e-4d);
            Assert.AreEqual(0d, Sum(raw, 1), 1e-5d);
        }
    }

    [Test]
    public void Gather_SecondRun_MatchesTheFirst()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        Vector2[] xz = { layout.Origin + new Vector2(2f, 2f), layout.Origin + new Vector2(3.25f, 7.5f) };
        Vector2[] headings = { new Vector2(0.6f, 0.8f), new Vector2(-1f, 0f) };
        int[] teams = { 0, 1 };
        using (Gpu gpu = new Gpu(xz, headings, teams, layout, FieldCollectMode.Gather))
        {
            gpu.Build();
            float[] first = gpu.ReadRaw();
            gpu.Build();
            float[] second = gpu.ReadRaw();
            AssertClose(ToDouble(first), second, 0);
        }
    }

    [Test]
    public void Scatter_MatchesReferenceWithinFixedPoint()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        var random = new System.Random(11);
        int count = 64;
        Vector2[] xz = new Vector2[count];
        Vector2[] headings = new Vector2[count];
        int[] teams = new int[count];
        for (int i = 0; i < count; i++)
        {
            xz[i] = layout.Origin + new Vector2(
                (float)random.NextDouble() * layout.Size.x,
                (float)random.NextDouble() * layout.Size.y);
            headings[i] = Vector2.right;
            teams[i] = i & 1;
        }

        double[] expected = FieldNeighborReference.Collect(xz, headings, teams, layout);
        using (Gpu gpu = new Gpu(xz, headings, teams, layout, FieldCollectMode.Scatter))
        {
            gpu.Build();
            float[] actual = gpu.ReadResolvedAccum();
            AssertScatter(expected, actual, xz, headings, teams, layout);
            Assert.AreEqual(count / 2d, Sum(actual, 0), 0.1d);
            Assert.AreEqual(count / 2d, Sum(actual, 1), 0.1d);
        }
    }

    [Test]
    public void Scatter_MatchesGatherOnTheSameSnapshot()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        Vector2[] xz =
        {
            layout.Origin + new Vector2(1.2f, 1.2f),
            layout.Origin + new Vector2(8.05f, 3.4f),
            layout.Origin + new Vector2(15.9f, 0.1f),
        };
        Vector2[] headings = { Vector2.right, Vector2.up, new Vector2(-0.5f, 0.5f) };
        int[] teams = { 0, 1, 0 };
        float[] gather;
        using (Gpu gpu = new Gpu(xz, headings, teams, layout, FieldCollectMode.Gather))
        {
            gpu.Build();
            gather = gpu.ReadRaw();
        }

        using (Gpu gpu = new Gpu(xz, headings, teams, layout, FieldCollectMode.Scatter))
        {
            gpu.Build();
            float[] scatter = gpu.ReadResolvedAccum();
            double[] gatherD = ToDouble(gather);
            AssertScatter(gatherD, scatter, xz, headings, teams, layout);
        }
    }

    [Test]
    public void Scatter_FourHundredThousand_SumNErrorBelowTenth()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(224f);
        const int count = 400000;
        var random = new System.Random(1);
        Vector2[] xz = new Vector2[count];
        Vector2[] headings = new Vector2[count];
        int[] teams = new int[count];
        for (int i = 0; i < count; i++)
        {
            xz[i] = layout.Origin + new Vector2(
                (float)random.NextDouble() * (layout.Size.x - 1e-3f),
                (float)random.NextDouble() * (layout.Size.y - 1e-3f));
            headings[i] = Vector2.right;
            teams[i] = i & 1;
        }

        using (Gpu gpu = new Gpu(xz, headings, teams, layout, FieldCollectMode.Scatter))
        {
            gpu.Build();
            float[] raw = gpu.ReadResolvedAccum();
            double n0 = Sum(raw, 0);
            double n1 = Sum(raw, 1);
            Assert.That(Math.Abs(n0 - count / 2d), Is.LessThan(0.1d));
            Assert.That(Math.Abs(n1 - count / 2d), Is.LessThan(0.1d));
        }
    }

    private static void AssertScatter(
        double[] expected,
        float[] actual,
        Vector2[] xz,
        Vector2[] headings,
        int[] teams,
        SpatialHashLayout layout)
    {
        int[] support = Support(xz, teams, layout);
        for (int i = 0; i < expected.Length; i++)
        {
            int texel = i / 6;
            int channel = i % 6;
            int team = channel / 3;
            int particles = support[texel * 2 + team];
            double tol = Math.Max(1, particles) * FixedUnit;
            Assert.That(Math.Abs(actual[i] - expected[i]), Is.LessThanOrEqualTo(tol + 1e-6), "channel " + i);
        }
    }

    private static int[] Support(Vector2[] xz, int[] teams, SpatialHashLayout layout)
    {
        int cells = layout.CellCount;
        int[] count = new int[cells * 2];
        for (int i = 0; i < xz.Length; i++)
        {
            if (teams[i] != 0 && teams[i] != 1)
            {
                continue;
            }

            Vector2 u = (xz[i] - layout.Origin) / layout.CellSize;
            int ix = (int)Math.Floor(u.x);
            int iy = (int)Math.Floor(u.y);
            double fx = u.x - ix;
            double fy = u.y - iy;
            Touch(count, layout, ix, iy, teams[i], (1 - fx) * (1 - fy));
            Touch(count, layout, ix + 1, iy, teams[i], fx * (1 - fy));
            Touch(count, layout, ix, iy + 1, teams[i], (1 - fx) * fy);
            Touch(count, layout, ix + 1, iy + 1, teams[i], fx * fy);
        }

        return count;
    }

    private static void Touch(int[] count, SpatialHashLayout layout, int x, int y, int team, double weight)
    {
        if (weight <= 0)
        {
            return;
        }

        int resX = layout.Resolution.x;
        int resY = layout.Resolution.y;
        int xx = x % resX;
        if (xx < 0)
        {
            xx += resX;
        }

        int yy = y % resY;
        if (yy < 0)
        {
            yy += resY;
        }

        count[(yy * resX + xx) * 2 + team]++;
    }

    private static void AssertClose(double[] expected, float[] actual, double tol)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(Math.Abs(actual[i] - expected[i]), Is.LessThanOrEqualTo(tol), "channel " + i);
        }
    }

    private static double[] ToDouble(float[] value)
    {
        double[] copy = new double[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            copy[i] = value[i];
        }

        return copy;
    }

    private static double Sum(float[] raw, int team)
    {
        double sum = 0d;
        for (int i = team * 3; i < raw.Length; i += 6)
        {
            sum += raw[i];
        }

        return sum;
    }

    private static SpatialHashLayout World(float size)
    {
        float half = size * 0.5f;
        return SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(half, 0f, half), 1f);
    }

    private sealed class Gpu : IDisposable
    {
        private readonly BuildSpatialHashPass hash;
        private readonly FieldNeighborCollectPass collect;
        private readonly SimContext context;
        private readonly ParticleSet particles;
        private readonly FieldSet fields;
        private readonly GraphicsBuffer teams;
        private readonly FieldCollectMode mode;

        public Gpu(Vector2[] xz, Vector2[] headings, int[] teamIds, SpatialHashLayout layout, FieldCollectMode mode)
        {
            this.mode = mode;
            int count = xz.Length;
            Vector3 half = new Vector3(layout.Size.x * 0.5f, 0f, layout.Size.y * 0.5f);
            ComputeShader hashShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(HashPath);
            ComputeShader fieldShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(FieldPath);
            Assert.IsNotNull(hashShader);
            Assert.IsNotNull(fieldShader);
            particles = new ParticleSet();
            fields = new FieldSet();
            particles.EnsureCapacity(count);
            particles.RegisterAttribute(BuiltinAttributes.Position);
            particles.RegisterAttribute(BuiltinAttributes.Heading);
            particles.RegisterAttribute(BuiltinAttributes.TeamId);
            Vector3[] positions = new Vector3[count];
            Vector3[] heading3 = new Vector3[count];
            uint[] teamsU = new uint[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = new Vector3(xz[i].x, 0f, xz[i].y);
                heading3[i] = new Vector3(headings[i].x, 0f, headings[i].y);
                teamsU[i] = (uint)teamIds[i];
            }

            particles.Get(BuiltinAttributes.Position).SetData(positions);
            particles.Get(BuiltinAttributes.Heading).SetData(heading3);
            particles.Get(BuiltinAttributes.TeamId).SetData(teamsU);
            teams = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
            slots[0].Radii = new Vector4(3f, 3f, 3f, 4f);
            slots[1].Radii = new Vector4(3f, 3f, 3f, 4f);
            teams.SetData(slots);
            context = new SimContext(particles, fields, new[] { hashShader, fieldShader }, null);
            context.Cmd = new CommandBuffer { name = "FieldNeighbor" };
            context.Teams = teams;
            hash = new BuildSpatialHashPass
            {
                Center = Vector3.zero,
                Extents = half,
                MinCellSize = layout.CellSize.x,
                Wrap = true,
            };
            collect = new FieldNeighborCollectPass
            {
                Mode = mode,
                Center = Vector3.zero,
                Extents = half,
                TexelSize = layout.CellSize.x,
            };
            if (mode == FieldCollectMode.Gather)
            {
                hash.Initialize(context);
            }

            collect.Initialize(context);
        }

        public void Build()
        {
            if (mode == FieldCollectMode.Gather)
            {
                hash.Execute(context, 0f);
            }

            collect.Execute(context, 0f);
            Graphics.ExecuteCommandBuffer(context.Cmd);
            context.Cmd.Clear();
        }

        public float[] ReadRaw()
        {
            float[] raw = new float[context.FieldNeighbor.Layout.CellCount * 6];
            context.FieldNeighbor.Raw.GetData(raw);
            return raw;
        }

        public float[] ReadResolvedAccum()
        {
            uint[] bits = new uint[context.FieldNeighbor.Layout.CellCount * 6];
            context.FieldNeighbor.Accum.GetData(bits);
            float[] raw = new float[bits.Length];
            for (int i = 0; i < bits.Length; i++)
            {
                raw[i] = unchecked((int)bits[i]) / (float)FieldNeighborSet.FixedPointScale;
            }

            return raw;
        }

        public void Dispose()
        {
            collect.Dispose();
            hash.Dispose();
            context.Cmd?.Release();
            teams?.Release();
            particles?.Dispose();
            fields?.Dispose();
        }
    }
}
