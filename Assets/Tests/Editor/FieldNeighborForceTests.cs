using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class FieldNeighborForceTests
{
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string FieldPath = "Assets/Shaders/GPU/Passes/FieldNeighborPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";
    private const double PairRelative = 1e-3;
    private const double PairAbsolute = 1e-4;
    private const double RandomRelative = 2e-3;
    private const double RandomAbsolute = 2e-4;

    private static readonly Vector3[] Weights =
    {
        new Vector3(1.2f, 0.8f, 0.6f),
        new Vector3(1.2f, 0.8f, 0.6f),
    };

    private static readonly double[] Inter = { 4.0, 4.0 };

    [Test]
    public void OneParticle_ForceIsZeroWithinAThousandth()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = Layout(16f);
        Vector2[] positions = { layout.Origin + new Vector2(3.3f, 4.7f) };
        Vector2[] headings = { new Vector2(0.6f, 0.8f) };
        int[] teams = { 0 };
        using (Rig rig = new Rig(positions, headings, teams, layout, 1))
        {
            rig.Step();
            Vector3 velocity = rig.ReadVelocity()[0];
            Assert.AreEqual(0f, velocity.x, 1e-3f);
            Assert.AreEqual(0f, velocity.y, 1e-3f);
            Assert.AreEqual(0f, velocity.z, 1e-3f);
        }
    }

    [Test]
    public void OwnPairAndOtherPair_MatchFieldRead()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = Layout(16f);
        Vector2[] positions = { layout.Origin + new Vector2(4f, 4f), layout.Origin + new Vector2(5f, 4f) };
        Vector2[] headings = { new Vector2(1f, 0f), new Vector2(0f, 1f) };
        AssertPair(layout, positions, headings, new[] { 0, 0 });
        AssertPair(layout, positions, headings, new[] { 0, 1 });
    }

    [Test]
    public void SeamAndCorners_MatchFieldRead()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = Layout(16f);
        Vector2 origin = layout.Origin;
        Vector2 size = layout.Size;
        Vector2[] positions =
        {
            origin + new Vector2(size.x - 0.3f, 4f),
            origin + new Vector2(0.4f, 4f),
            origin + new Vector2(size.x - 0.2f, size.y - 0.25f),
            origin + new Vector2(0.3f, size.y - 0.4f),
        };
        Vector2[] headings =
        {
            new Vector2(1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(0.6f, 0.8f),
            new Vector2(-0.8f, 0.6f),
        };
        int[] teams = { 0, 0, 1, 1 };
        AssertAgainstReference(layout, positions, headings, teams, PairRelative, PairAbsolute);
    }

    [Test]
    public void EmptyTeam_StaysFinite()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = Layout(16f);
        Vector2[] positions =
        {
            layout.Origin + new Vector2(4f, 4f),
            layout.Origin + new Vector2(5.2f, 4.4f),
            layout.Origin + new Vector2(4.4f, 5.1f),
        };
        Vector2[] headings =
        {
            new Vector2(1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(-1f, 0f),
        };
        int[] teams = { 0, 0, 0 };
        using (Rig rig = new Rig(positions, headings, teams, layout, 1))
        {
            rig.Step();
            Vector3[] velocity = rig.ReadVelocity();
            for (int i = 0; i < velocity.Length; i++)
            {
                Assert.IsFalse(float.IsNaN(velocity[i].x) || float.IsInfinity(velocity[i].x), "x " + i);
                Assert.IsFalse(float.IsNaN(velocity[i].y) || float.IsInfinity(velocity[i].y), "y " + i);
                Assert.IsFalse(float.IsNaN(velocity[i].z) || float.IsInfinity(velocity[i].z), "z " + i);
            }
        }

        AssertAgainstReference(layout, positions, headings, teams, PairRelative, PairAbsolute);
    }

    [Test]
    public void FiveThousand_VelocityMatchesFieldReadOfTheGpuField()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Crowd crowd = MakeCrowd();
        using (Rig rig = new Rig(crowd.Positions, crowd.Headings, crowd.Teams, crowd.Layout, 1))
        {
            rig.Step();
            FieldForceReference.Sample[] read = FieldForceReference.FieldRead(
                Pack(rig.ReadField()), crowd.Positions, crowd.Headings, crowd.Teams, Weights, crowd.Layout);
            ErrorStats stats = Measure(read, rig.ReadVelocity());
            Assert.That(
                stats.MaxAbs,
                Is.LessThanOrEqualTo(1e-5),
                "gpu-field maxAbs=" + stats.MaxAbs.ToString("G6"));
            Assert.Pass("gpu-field maxAbs=" + stats.MaxAbs.ToString("G6"));
        }
    }

    [Test]
    public void FiveThousand_GatherMatchesTheDoubleReference()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Crowd crowd = MakeCrowd();
        double[] field = BuildField(crowd.Positions, crowd.Headings, crowd.Teams, crowd.Layout);
        FieldForceReference.Sample[] expected = FieldForceReference.FieldRead(
            field, crowd.Positions, crowd.Headings, crowd.Teams, Weights, crowd.Layout);
        using (Rig rig = new Rig(
            crowd.Positions, crowd.Headings, crowd.Teams, crowd.Layout, 1, FieldCollectMode.Gather))
        {
            rig.Step();
            ErrorStats stats = Measure(expected, rig.ReadVelocity());
            string text = "gather maxAbs=" + stats.MaxAbs.ToString("G6") +
                " over=" + stats.Over + "/" + stats.Count +
                " worst=" + stats.Worst +
                " exp=" + stats.Expected +
                " act=" + stats.Actual;
            Assert.That(stats.Over, Is.EqualTo(0), text);
            Assert.Pass(text);
        }
    }

    // Регрессионный предел по наблюдаемому разбросу фиксированной точки.
    [Test]
    [Description("регрессионный предел по наблюдаемому разбросу фиксированной точки")]
    public void FiveThousand_ScatterStaysInsideTheFixedPointSpread()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Crowd crowd = MakeCrowd();
        double[] field = BuildField(crowd.Positions, crowd.Headings, crowd.Teams, crowd.Layout);
        FieldForceReference.Sample[] expected = FieldForceReference.FieldRead(
            field, crowd.Positions, crowd.Headings, crowd.Teams, Weights, crowd.Layout);
        using (Rig rig = new Rig(crowd.Positions, crowd.Headings, crowd.Teams, crowd.Layout, 1))
        {
            rig.Step();
            ErrorStats stats = Measure(expected, rig.ReadVelocity());
            double fraction = stats.Over / (double)stats.Count;
            string text = "scatter maxAbs=" + stats.MaxAbs.ToString("G6") +
                " over=" + stats.Over + "/" + stats.Count +
                " fraction=" + fraction.ToString("G6");
            Assert.That(stats.MaxAbs, Is.LessThanOrEqualTo(3e-3), text);
            Assert.That(fraction, Is.LessThanOrEqualTo(0.01), text);
            Assert.Pass(text);
        }
    }

    [Test]
    public void RefreshThree_ForceReadsThePreviousField()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = Layout(16f);
        Vector2[] first =
        {
            layout.Origin + new Vector2(4f, 4f),
            layout.Origin + new Vector2(5.5f, 4.25f),
            layout.Origin + new Vector2(6.2f, 5f),
            layout.Origin + new Vector2(4.4f, 5.6f),
        };
        Vector2[] second =
        {
            layout.Origin + new Vector2(7f, 3f),
            layout.Origin + new Vector2(8.2f, 3.4f),
            layout.Origin + new Vector2(7.4f, 4.5f),
            layout.Origin + new Vector2(9f, 4f),
        };
        Vector2[] headings =
        {
            new Vector2(1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(-1f, 0f),
            new Vector2(0f, -1f),
        };
        int[] teams = { 0, 0, 1, 1 };
        using (Rig rig = new Rig(first, headings, teams, layout, 3))
        {
            rig.Step();
            Vector4[] built = rig.ReadField();
            rig.SetPositions(second);
            rig.Step();
            rig.Step();
            Vector4[] held = rig.ReadField();
            Assert.AreEqual(built.Length, held.Length);
            for (int i = 0; i < built.Length; i++)
            {
                Assert.AreEqual(built[i].x, held[i].x);
                Assert.AreEqual(built[i].y, held[i].y);
                Assert.AreEqual(built[i].z, held[i].z);
                Assert.AreEqual(built[i].w, held[i].w);
            }

            double[] field = Pack(built);
            FieldForceReference.Sample[] expected = FieldForceReference.FieldRead(
                field, second, headings, teams, Weights, layout);
            Vector3[] velocity = rig.ReadVelocity();
            AssertSamples(expected, velocity, PairRelative, PairAbsolute);
        }
    }

    [Test]
    public void DepositAndRead_ShareCicIndicesAndWeights()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = Layout(16f);
        Vector2[] positions = { layout.Origin + new Vector2(3.3f, 14.75f) };
        Vector2[] headings = { new Vector2(1f, 0f) };
        int[] teams = { 0 };
        using (Rig rig = new Rig(positions, headings, teams, layout, 1))
        {
            Vector2[] probe = rig.ReadCicWeights();
            rig.Step();
            uint[] accum = rig.ReadAccum();
            Assert.AreEqual(4, probe.Length);
            int[] quantized = QuantizeWeights(probe[0].y, probe[1].y, probe[2].y, probe[3].y);
            for (int k = 0; k < 4; k++)
            {
                int index = (int)probe[k].x;
                int stored = unchecked((int)accum[index * 6]);
                Assert.AreEqual(quantized[k], stored, "corner " + k);
            }
        }
    }

    private static void AssertPair(
        SpatialHashLayout layout,
        Vector2[] positions,
        Vector2[] headings,
        int[] teams)
    {
        AssertAgainstReference(layout, positions, headings, teams, PairRelative, PairAbsolute);
    }

    private static void AssertAgainstReference(
        SpatialHashLayout layout,
        Vector2[] positions,
        Vector2[] headings,
        int[] teams,
        double relative,
        double absolute)
    {
        double[] field = BuildField(positions, headings, teams, layout);
        FieldForceReference.Sample[] expected = FieldForceReference.FieldRead(
            field, positions, headings, teams, Weights, layout);
        using (Rig rig = new Rig(positions, headings, teams, layout, 1))
        {
            rig.Step();
            AssertSamples(expected, rig.ReadVelocity(), relative, absolute);
        }
    }

    private static void AssertSamples(
        FieldForceReference.Sample[] expected,
        Vector3[] velocity,
        double relative,
        double absolute)
    {
        Assert.AreEqual(expected.Length, velocity.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            AssertComponent(expected[i].Fx, velocity[i].x, absolute, relative, "x " + i);
            AssertComponent(expected[i].Fy, velocity[i].z, absolute, relative, "z " + i);
            Assert.AreEqual(0f, velocity[i].y, 1e-5f, "y " + i);
        }
    }

    private static void AssertComponent(double expected, double actual, double absolute, double relative, string label)
    {
        double tolerance = Math.Max(absolute, relative * Math.Abs(expected));
        Assert.AreEqual(expected, actual, tolerance, label);
    }

    private static double[] BuildField(
        Vector2[] positions,
        Vector2[] headings,
        int[] teams,
        SpatialHashLayout layout)
    {
        double[] raw = FieldNeighborReference.Collect(positions, headings, teams, layout);
        FieldNeighborReference.Convolve(raw, layout, 3.0, Inter, out double[] phi, out double[] field);
        FieldNeighborReference.Gradient(phi, layout, field);
        return field;
    }

    private static double[] Pack(Vector4[] slots)
    {
        var field = new double[slots.Length * 4];
        for (int i = 0; i < slots.Length; i++)
        {
            field[i * 4] = slots[i].x;
            field[i * 4 + 1] = slots[i].y;
            field[i * 4 + 2] = slots[i].z;
            field[i * 4 + 3] = slots[i].w;
        }

        return field;
    }

    private static int[] QuantizeWeights(float w00, float w10, float w01, float w11)
    {
        float scale = FieldNeighborSet.FixedPointScale;
        float t0 = w00 * scale;
        float t1 = w10 * scale;
        float t2 = w01 * scale;
        float t3 = w11 * scale;
        int q0 = (int)Math.Floor(t0 + 0.5f);
        int q1 = (int)Math.Floor(t1 + 0.5f);
        int q2 = (int)Math.Floor(t2 + 0.5f);
        int q3 = (int)Math.Floor(t3 + 0.5f);
        int diff = (int)scale - (q0 + q1 + q2 + q3);
        for (int step = 0; step < 4 && diff != 0; step++)
        {
            float e0 = q0 - t0;
            float e1 = q1 - t1;
            float e2 = q2 - t2;
            float e3 = q3 - t3;
            int pick = 0;
            float best = diff > 0 ? e0 : -e0;
            float b1 = diff > 0 ? e1 : -e1;
            float b2 = diff > 0 ? e2 : -e2;
            float b3 = diff > 0 ? e3 : -e3;
            if (b1 < best)
            {
                best = b1;
                pick = 1;
            }

            if (b2 < best)
            {
                best = b2;
                pick = 2;
            }

            if (b3 < best)
            {
                pick = 3;
            }

            int stepQ = diff > 0 ? 1 : -1;
            if (pick == 0)
            {
                q0 += stepQ;
            }
            else if (pick == 1)
            {
                q1 += stepQ;
            }
            else if (pick == 2)
            {
                q2 += stepQ;
            }
            else
            {
                q3 += stepQ;
            }

            diff -= stepQ;
        }

        return new[] { q0, q1, q2, q3 };
    }

    private struct ErrorStats
    {
        public double MaxAbs;
        public int Over;
        public int Count;
        public int Worst;
        public string Expected;
        public string Actual;
    }

    private struct Crowd
    {
        public SpatialHashLayout Layout;
        public Vector2[] Positions;
        public Vector2[] Headings;
        public int[] Teams;
    }

    private static Crowd MakeCrowd()
    {
        SpatialHashLayout layout = Layout(64f);
        const int count = 5000;
        var random = new System.Random(1);
        var positions = new Vector2[count];
        var headings = new Vector2[count];
        var teams = new int[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = layout.Origin + new Vector2(
                (float)(random.NextDouble() * layout.Size.x),
                (float)(random.NextDouble() * layout.Size.y));
            double angle = random.NextDouble() * Math.PI * 2.0;
            headings[i] = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            teams[i] = i % 2;
        }

        return new Crowd
        {
            Layout = layout,
            Positions = positions,
            Headings = headings,
            Teams = teams,
        };
    }

    private static ErrorStats Measure(FieldForceReference.Sample[] expected, Vector3[] velocity)
    {
        var stats = new ErrorStats
        {
            Count = expected.Length,
            Worst = -1,
            Expected = "",
            Actual = "",
        };
        for (int i = 0; i < expected.Length; i++)
        {
            double dx = Math.Abs(expected[i].Fx - velocity[i].x);
            double dz = Math.Abs(expected[i].Fy - velocity[i].z);
            double gap = Math.Max(dx, dz);
            if (gap > stats.MaxAbs)
            {
                stats.MaxAbs = gap;
                stats.Worst = i;
                stats.Expected = expected[i].Fx.ToString("G6") + "," + expected[i].Fy.ToString("G6");
                stats.Actual = velocity[i].x.ToString("G6") + "," + velocity[i].z.ToString("G6");
            }

            bool overX = dx > Math.Max(RandomAbsolute, RandomRelative * Math.Abs(expected[i].Fx));
            bool overZ = dz > Math.Max(RandomAbsolute, RandomRelative * Math.Abs(expected[i].Fy));
            if (overX || overZ)
            {
                stats.Over++;
            }
        }

        return stats;
    }

    private static SpatialHashLayout Layout(float size)
    {
        float half = size * 0.5f;
        return SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(half, 0f, half), 1f);
    }

    private sealed class Rig : IDisposable
    {
        private readonly FieldNeighborCollectPass collect;
        private readonly FieldNeighborConvolvePass convolve;
        private readonly ClearVelocityPass clear;
        private readonly FieldNeighborForcePass force;
        private readonly BuildSpatialHashPass hash;
        private readonly SimContext context;
        private readonly ParticleSet particles;
        private readonly FieldSet fields;
        private readonly GraphicsBuffer teams;
        private readonly ComputeShader fieldShader;

        public Rig(
            Vector2[] xz,
            Vector2[] headingXz,
            int[] teamIds,
            SpatialHashLayout layout,
            int refresh,
            FieldCollectMode mode = FieldCollectMode.Scatter)
        {
            int count = xz.Length;
            Vector3 half = new Vector3(layout.Size.x * 0.5f, 0f, layout.Size.y * 0.5f);
            fieldShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(FieldPath);
            ComputeShader dynamics = AssetDatabase.LoadAssetAtPath<ComputeShader>(DynamicsPath);
            ComputeShader hashShader = mode == FieldCollectMode.Gather
                ? AssetDatabase.LoadAssetAtPath<ComputeShader>(HashPath)
                : null;
            Assert.IsNotNull(fieldShader);
            Assert.IsNotNull(dynamics);
            if (mode == FieldCollectMode.Gather)
            {
                Assert.IsNotNull(hashShader);
            }

            particles = new ParticleSet();
            fields = new FieldSet();
            particles.EnsureCapacity(count);
            particles.RegisterAttribute(BuiltinAttributes.Position);
            particles.RegisterAttribute(BuiltinAttributes.Heading);
            particles.RegisterAttribute(BuiltinAttributes.TeamId);
            particles.RegisterAttribute(BuiltinAttributes.Velocity);
            WriteParticles(xz, headingXz, teamIds);
            var zeros = new Vector3[count];
            particles.Get(BuiltinAttributes.Velocity).SetData(zeros);
            teams = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            var slots = new TeamParams[TeamProfile.MaxTeams];
            slots[0].Radii = new Vector4(3f, 3f, 3f, 4f);
            slots[1].Radii = new Vector4(3f, 3f, 3f, 4f);
            slots[0].Weights = new Vector4(Weights[0].x, Weights[0].y, Weights[0].z, 0f);
            slots[1].Weights = new Vector4(Weights[1].x, Weights[1].y, Weights[1].z, 0f);
            teams.SetData(slots);
            ComputeShader[] shaders = mode == FieldCollectMode.Gather
                ? new[] { hashShader, fieldShader, dynamics }
                : new[] { fieldShader, dynamics };
            context = new SimContext(particles, fields, shaders, null);
            context.Cmd = new CommandBuffer { name = "FieldForce" };
            context.Teams = teams;
            hash = null;
            if (mode == FieldCollectMode.Gather)
            {
                hash = new BuildSpatialHashPass
                {
                    Center = Vector3.zero,
                    Extents = half,
                    MinCellSize = layout.CellSize.x,
                    Wrap = true,
                };
                hash.Initialize(context);
            }

            collect = new FieldNeighborCollectPass
            {
                Mode = mode,
                Center = Vector3.zero,
                Extents = half,
                TexelSize = layout.CellSize.x,
                RefreshInterval = refresh,
            };
            convolve = new FieldNeighborConvolvePass();
            clear = new ClearVelocityPass();
            force = new FieldNeighborForcePass();
            collect.Initialize(context);
            convolve.Initialize(context);
            clear.Initialize(context);
            force.Initialize(context);
        }

        public void SetPositions(Vector2[] xz)
        {
            var positions = new Vector3[xz.Length];
            for (int i = 0; i < xz.Length; i++)
            {
                positions[i] = new Vector3(xz[i].x, 0f, xz[i].y);
            }

            particles.Get(BuiltinAttributes.Position).SetData(positions);
        }

        public void Step()
        {
            hash?.Execute(context, 1f / 60f);
            collect.Execute(context, 1f / 60f);
            convolve.Execute(context, 1f / 60f);
            clear.Execute(context, 1f / 60f);
            force.Execute(context, 1f / 60f);
            Graphics.ExecuteCommandBuffer(context.Cmd);
            context.Cmd.Clear();
        }

        public Vector3[] ReadVelocity()
        {
            var velocity = new Vector3[particles.Count];
            particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
            return velocity;
        }

        public Vector4[] ReadField()
        {
            var slots = new Vector4[context.FieldNeighbor.Layout.CellCount * 4];
            context.FieldNeighbor.Field.GetData(slots);
            return slots;
        }

        public uint[] ReadAccum()
        {
            var bits = new uint[context.FieldNeighbor.Layout.CellCount * 6];
            context.FieldNeighbor.Accum.GetData(bits);
            return bits;
        }

        public Vector2[] ReadCicWeights()
        {
            int kernel = fieldShader.FindKernel("FieldCicWeights");
            var output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, particles.Count * 4, sizeof(float) * 2);
            try
            {
                context.FieldNeighbor.PushParams(context.Cmd, fieldShader, 3f, 3f);
                context.Cmd.SetComputeIntParam(fieldShader, Shader.PropertyToID("ParticleCount"), particles.Count);
                context.Cmd.SetComputeBufferParam(
                    fieldShader, kernel, "position", particles.Get(BuiltinAttributes.Position));
                context.Cmd.SetComputeBufferParam(fieldShader, kernel, "CicOut", output);
                int groups = (particles.Count + 63) / 64;
                context.Cmd.DispatchCompute(fieldShader, kernel, groups, 1, 1);
                Graphics.ExecuteCommandBuffer(context.Cmd);
                context.Cmd.Clear();
                var values = new Vector2[particles.Count * 4];
                output.GetData(values);
                return values;
            }
            finally
            {
                output.Release();
            }
        }

        public void Dispose()
        {
            force.Dispose();
            convolve.Dispose();
            collect.Dispose();
            hash?.Dispose();
            context.Cmd?.Release();
            teams?.Release();
            particles?.Dispose();
            fields?.Dispose();
        }

        private void WriteParticles(Vector2[] xz, Vector2[] headingXz, int[] teamIds)
        {
            int count = xz.Length;
            var positions = new Vector3[count];
            var heading3 = new Vector3[count];
            var teamsU = new uint[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = new Vector3(xz[i].x, 0f, xz[i].y);
                heading3[i] = new Vector3(headingXz[i].x, 0f, headingXz[i].y);
                teamsU[i] = (uint)teamIds[i];
            }

            particles.Get(BuiltinAttributes.Position).SetData(positions);
            particles.Get(BuiltinAttributes.Heading).SetData(heading3);
            particles.Get(BuiltinAttributes.TeamId).SetData(teamsU);
        }
    }
}
