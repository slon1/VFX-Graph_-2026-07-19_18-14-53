using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class FieldNeighborConvolveTests
{
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string FieldPath = "Assets/Shaders/GPU/Passes/FieldNeighborPasses.compute";

    [Test]
    public void Gather_RandomSnapshot_MatchesReference()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        var random = new System.Random(5);
        const int count = 40;
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

        RunAgainstReference(xz, headings, teams, layout, FieldCollectMode.Gather, 4d);
    }

    [Test]
    public void Gather_Seam_MatchesReference()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        Vector2[] xz =
        {
            layout.Origin + new Vector2(0.15f, 0.2f),
            layout.Origin + new Vector2(layout.Size.x - 0.2f, 0.35f),
            layout.Origin + new Vector2(layout.Size.x - 0.05f, layout.Size.y - 0.1f),
        };
        Vector2[] headings = { Vector2.right, Vector2.up, new Vector2(-0.6f, 0.8f) };
        int[] teams = { 0, 1, 0 };
        RunAgainstReference(xz, headings, teams, layout, FieldCollectMode.Gather, 4d);
    }

    [Test]
    public void Inter_ChangesPhi_AndLeavesNg()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        Vector2 p = layout.Origin + new Vector2(8.3f, 7.4f);
        Vector2[] xz = { p, p + new Vector2(1.2f, -0.4f) };
        Vector2[] headings = { Vector2.right, Vector2.up };
        int[] teams = { 0, 1 };
        float[] low;
        float[] high;
        using (Gpu gpu = new Gpu(xz, headings, teams, layout, FieldCollectMode.Gather, 1f))
        {
            gpu.Build();
            low = gpu.ReadField();
        }

        using (Gpu gpu = new Gpu(xz, headings, teams, layout, FieldCollectMode.Gather, 4f))
        {
            gpu.Build();
            high = gpu.ReadField();
        }

        double[] raw = FieldNeighborReference.Collect(xz, headings, teams, layout);
        FieldNeighborReference.Convolve(raw, layout, 3d, new[] { 1d, 1d }, out double[] phiLow, out _);
        FieldNeighborReference.Convolve(raw, layout, 3d, new[] { 4d, 4d }, out double[] phiHigh, out _);
        bool phiDiffers = false;
        for (int i = 0; i < phiLow.Length; i++)
        {
            if (Math.Abs(phiHigh[i] - phiLow[i]) > 1e-3)
            {
                phiDiffers = true;
                break;
            }
        }

        Assert.IsTrue(phiDiffers);
        for (int cell = 0; cell < layout.CellCount; cell++)
        {
            Assert.AreEqual(low[cell * 16 + 3], high[cell * 16 + 3], 1e-4f);
            Assert.AreEqual(low[cell * 16 + 11], high[cell * 16 + 11], 1e-4f);
        }
    }

    [Test]
    public void OneParticle_GradientPointsAway_AndAlignmentIsHeading()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        Vector2 heading = new Vector2(0.6f, -0.8f);
        Vector2 p = layout.Origin + new Vector2(8f, 8f);
        using (Gpu gpu = new Gpu(new[] { p }, new[] { heading }, new[] { 0 }, layout, FieldCollectMode.Gather, 4f))
        {
            gpu.Build();
            float[] field = gpu.ReadField();
            int resX = layout.Resolution.x;
            int center = 8 * resX + 8;
            AssertClose(field, center, 4, heading.x);
            AssertClose(field, center, 5, heading.y);
            AssertAway(field, layout, 9, 8, axisX: true, positive: true);
            AssertAway(field, layout, 7, 8, axisX: true, positive: false);
            AssertAway(field, layout, 8, 9, axisX: false, positive: true);
            AssertAway(field, layout, 8, 7, axisX: false, positive: false);
        }
    }

    [Test]
    public void OneParticle_KPlusCenterIsPosition()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        Vector2 p = layout.Origin + new Vector2(8.25f, 7.75f);
        using (Gpu gpu = new Gpu(new[] { p }, new[] { Vector2.right }, new[] { 0 }, layout, FieldCollectMode.Gather, 1f))
        {
            gpu.Build();
            float[] field = gpu.ReadField();
            int resX = layout.Resolution.x;
            int ix = 8;
            int iy = 7;
            for (int y = iy; y <= iy + 1; y++)
            {
                for (int x = ix; x <= ix + 1; x++)
                {
                    int cell = y * resX + x;
                    float ng = field[cell * 16 + 3];
                    Assert.Greater(ng, 0.5f);
                    Vector2 center = layout.Origin + new Vector2(x * layout.CellSize.x, y * layout.CellSize.y);
                    Vector2 reconstructed = center + new Vector2(field[cell * 16 + 6], field[cell * 16 + 7]) / ng;
                    Assert.AreEqual(p.x, reconstructed.x, 1e-3f);
                    Assert.AreEqual(p.y, reconstructed.y, 1e-3f);
                }
            }
        }
    }

    [Test]
    public void EmptyTeam_HasNoNaN()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        Vector2[] xz =
        {
            layout.Origin + new Vector2(4.2f, 5.5f),
            layout.Origin + new Vector2(9.1f, 3.3f),
        };
        using (Gpu gpu = new Gpu(xz, new[] { Vector2.right, Vector2.up }, new[] { 0, 0 }, layout, FieldCollectMode.Gather, 4f))
        {
            gpu.Build();
            float[] field = gpu.ReadField();
            Vector2[] phi = gpu.ReadPhi();
            for (int i = 0; i < field.Length; i++)
            {
                Assert.IsFalse(float.IsNaN(field[i]) || float.IsInfinity(field[i]), "field " + i);
            }

            for (int i = 0; i < phi.Length; i++)
            {
                Assert.IsFalse(float.IsNaN(phi[i].x) || float.IsNaN(phi[i].y));
            }

            for (int cell = 0; cell < layout.CellCount; cell++)
            {
                int b = cell * 16 + 8;
                Assert.AreEqual(0f, field[b + 3], 1e-4f);
                Assert.AreEqual(0f, field[b + 4], 1e-4f);
                Assert.AreEqual(0f, field[b + 5], 1e-4f);
                Assert.AreEqual(0f, field[b + 6], 1e-4f);
                Assert.AreEqual(0f, field[b + 7], 1e-4f);
            }
        }
    }

    [Test]
    public void Scatter_CenterParticle_MatchesReference()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = World(16f);
        Vector2 p = layout.Origin + new Vector2(6f, 9f);
        RunAgainstReference(new[] { p }, new[] { Vector2.right }, new[] { 0 }, layout, FieldCollectMode.Scatter, 1d);
    }

    private static void RunAgainstReference(
        Vector2[] xz,
        Vector2[] headings,
        int[] teams,
        SpatialHashLayout layout,
        FieldCollectMode mode,
        double inter)
    {
        double[] raw = FieldNeighborReference.Collect(xz, headings, teams, layout);
        FieldNeighborReference.Convolve(raw, layout, 3d, new[] { inter, inter }, out double[] phi, out double[] field);
        FieldNeighborReference.Gradient(phi, layout, field);
        using (Gpu gpu = new Gpu(xz, headings, teams, layout, mode, (float)inter))
        {
            gpu.Build();
            float[] actual = gpu.ReadField();
            Vector2[] actualPhi = gpu.ReadPhi();
            for (int i = 0; i < field.Length; i++)
            {
                double tol = Math.Max(1e-4, 1e-3 * Math.Abs(field[i]));
                Assert.That(Math.Abs(actual[i] - field[i]), Is.LessThanOrEqualTo(tol), "field " + i);
            }

            for (int i = 0; i < phi.Length; i += 2)
            {
                double tolX = Math.Max(1e-4, 1e-3 * Math.Abs(phi[i]));
                double tolY = Math.Max(1e-4, 1e-3 * Math.Abs(phi[i + 1]));
                Assert.That(Math.Abs(actualPhi[i / 2].x - phi[i]), Is.LessThanOrEqualTo(tolX), "phi " + i);
                Assert.That(Math.Abs(actualPhi[i / 2].y - phi[i + 1]), Is.LessThanOrEqualTo(tolY), "phi " + (i + 1));
            }
        }
    }

    private static void AssertAway(float[] field, SpatialHashLayout layout, int x, int y, bool axisX, bool positive)
    {
        int cell = y * layout.Resolution.x + x;
        float g = field[cell * 16 + (axisX ? 0 : 1)];
        if (positive)
        {
            Assert.Greater(g, 1e-4f);
        }
        else
        {
            Assert.Less(g, -1e-4f);
        }
    }

    private static void AssertClose(float[] field, int cell, int channel, float expected)
    {
        Assert.AreEqual(expected, field[cell * 16 + channel], 1e-3f);
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
        private readonly FieldNeighborConvolvePass convolve;
        private readonly SimContext context;
        private readonly ParticleSet particles;
        private readonly FieldSet fields;
        private readonly GraphicsBuffer teams;

        public Gpu(
            Vector2[] xz,
            Vector2[] headings,
            int[] teamIds,
            SpatialHashLayout layout,
            FieldCollectMode mode,
            float inter)
        {
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
            slots[0].Radii = new Vector4(3f, 3f, 3f, inter);
            slots[1].Radii = new Vector4(3f, 3f, 3f, inter);
            teams.SetData(slots);
            context = new SimContext(particles, fields, new[] { hashShader, fieldShader }, null);
            context.Cmd = new CommandBuffer { name = "FieldConvolve" };
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
            convolve = new FieldNeighborConvolvePass();
            if (mode == FieldCollectMode.Gather)
            {
                hash.Initialize(context);
            }

            collect.Initialize(context);
            convolve.Initialize(context);
        }

        public void Build()
        {
            if (collect.Mode == FieldCollectMode.Gather)
            {
                hash.Execute(context, 0f);
            }

            collect.Execute(context, 0f);
            convolve.Execute(context, 0f);
            Graphics.ExecuteCommandBuffer(context.Cmd);
            context.Cmd.Clear();
        }

        public float[] ReadField()
        {
            Vector4[] slots = new Vector4[context.FieldNeighbor.Layout.CellCount * 4];
            context.FieldNeighbor.Field.GetData(slots);
            float[] flat = new float[slots.Length * 4];
            for (int i = 0; i < slots.Length; i++)
            {
                flat[i * 4] = slots[i].x;
                flat[i * 4 + 1] = slots[i].y;
                flat[i * 4 + 2] = slots[i].z;
                flat[i * 4 + 3] = slots[i].w;
            }

            return flat;
        }

        public Vector2[] ReadPhi()
        {
            Vector2[] phi = new Vector2[context.FieldNeighbor.Layout.CellCount];
            context.FieldNeighbor.Phi.GetData(phi);
            return phi;
        }

        public void Dispose()
        {
            convolve.Dispose();
            collect.Dispose();
            hash.Dispose();
            context.Cmd?.Release();
            teams?.Release();
            particles?.Dispose();
            fields?.Dispose();
        }
    }
}
