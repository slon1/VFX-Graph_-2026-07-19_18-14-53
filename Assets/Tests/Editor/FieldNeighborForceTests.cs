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
    public void GatherTexel_PointSixAndPointFive_ConvolveMatchesToleranceA()
    {
        AssertConvolved(MakeSceneCrowd(0.6f), FieldCollectMode.Gather, 1e-4, 1e-3, additive: false);
        AssertConvolved(MakeSceneCrowd(0.5f), FieldCollectMode.Gather, 1e-4, 1e-3, additive: false);
    }

    // Регрессионный предел по разбросу фиксированной точки на плотности 0,6 частицы на тексель, не оценка точности.
    [Test]
    [Description("регрессионный предел по разбросу фиксированной точки на плотности 0,6 частицы на тексель, не оценка точности")]
    public void ScatterTexel_FieldChannels_StayInsideTheFixedPointSpread()
    {
        AssertConvolved(MakeSceneCrowd(1f), FieldCollectMode.Scatter, 1e-3, 1e-3, additive: true);
        AssertConvolved(MakeSceneCrowd(0.6f), FieldCollectMode.Scatter, 1e-3, 1e-3, additive: true);
        AssertConvolved(MakeSceneCrowd(0.5f), FieldCollectMode.Scatter, 1e-3, 1e-3, additive: true);
    }

    [Test]
    [Description("регрессионный предел по разбросу фиксированной точки на плотности 0,6 частицы на тексель, не оценка точности")]
    public void ScatterOriginalSnapshot_FieldChannels_StayInsideTheFixedPointSpread()
    {
        AssertConvolved(MakeCrowd(), FieldCollectMode.Scatter, 1e-3, 1e-3, additive: true);
    }

    // Граница по float32-обусловленности. Веса и индексы CIC — с GPU.
    [Test]
    [Description("граница по float32-обусловленности")]
    public void SceneTexel_ForceRead_Float32ConditionBound()
    {
        const float Epsilon = 1f / 16777216f;
        string cpu = VariantLine(false);
        string gpu = VariantLine(true);
        ConditionRow worst = default;
        ConditionRow[] calibration = MeasureFloatForce(0.6f, 1, true);
        double rMax = MaxRatio(calibration, Epsilon, ref worst);
        double maxError = MaxError(calibration);
        if (maxError > 5e-5)
        {
            Assert.Fail(
                "cpu " + cpu + "\ngpu " + gpu
                + "\nостаётся порядка 1e-4"
                + "\n" + worst.Text()
                + ComponentSplit(0.6f, 1, worst.Index));
        }

        int c = (int)Math.Ceiling(2.0 * rMax);
        if (c > 256)
        {
            Assert.Fail("c=" + c + " rMax=" + rMax.ToString("G6") + " " + worst.Text() + "\ncpu " + cpu + "\ngpu " + gpu);
        }

        string failures = CheckBound(1f, 1, c, Epsilon)
            + CheckBound(0.5f, 1, c, Epsilon)
            + CheckBound(0.6f, 2, c, Epsilon)
            + CheckBound(0.6f, 3, c, Epsilon);
        if (failures.Length > 0)
        {
            Assert.Fail(
                "c=" + c + " rMax=" + rMax.ToString("G6") + " " + worst.Text()
                + "\ncpu " + cpu + "\ngpu " + gpu + "\n" + failures);
        }

        Assert.Pass(
            "c=" + c + " rMax=" + rMax.ToString("G6") + " " + worst.Text()
            + "\ncpu " + cpu + "\ngpu " + gpu);
    }

    [Test]
    [Description("веса GPU против double в допуске 2 ulp(u) в текселях")]
    public void SceneTexel_GpuCicWeights_MatchDoubleWithinTwoUlps()
    {
        string text = UlpCase(0.6f, 1) + UlpCase(0.6f, 2) + UlpCase(0.6f, 3) + UlpCase(1f, 1) + UlpCase(0.5f, 1);
        if (text.Contains("n="))
        {
            Assert.Fail(text);
        }

        Assert.Pass(text);
    }

    [Test]
    public void SceneTexel_SeamAndCorners_MatchTheGpuField()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SceneSeam(0.6f);
        SceneSeam(0.5f);
    }

    [Test]
    public void SceneTexel_RefreshThree_ReadsThePreviousField()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SceneRefresh(0.6f);
        SceneRefresh(0.5f);
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

    [Test]
    public void ForceStage_RepeatsIntoScratch_AndOffSkipsTheDispatch()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = Layout(16f);
        Vector2[] positions = { layout.Origin + new Vector2(3.3f, 4.7f) };
        Vector2[] headings = { new Vector2(0.6f, 0.8f) };
        int[] teams = { 0 };
        using (Rig rig = new Rig(positions, headings, teams, layout, 1))
        {
            rig.SetForceDiagnostics(FieldRepeatStage.Force, 4);
            rig.Step();
            Assert.IsNotNull(rig.ForceScratch);
            Assert.AreEqual(1, rig.ForceScratch.count);
            Assert.AreEqual(1, rig.ForcePassRepeatCount);
            Vector3 velocity = rig.ReadVelocity()[0];
            Assert.AreEqual(0f, velocity.x, 1e-3f);
            Assert.AreEqual(0f, velocity.z, 1e-3f);
            rig.SetForceDiagnostics(FieldRepeatStage.Force, 1);
            Assert.IsNull(rig.ForceScratch);
        }

        using (Rig rig = new Rig(positions, headings, teams, layout, 1))
        {
            rig.SetForceDiagnostics(FieldRepeatStage.Off, 4);
            rig.Step();
            Vector3 velocity = rig.ReadVelocity()[0];
            Assert.AreEqual(0f, velocity.x, 0f);
            Assert.AreEqual(0f, velocity.y, 0f);
            Assert.AreEqual(0f, velocity.z, 0f);
            Assert.IsNull(rig.ForceScratch);
        }

        using (Rig rig = new Rig(positions, headings, teams, layout, 1))
        {
            rig.SetForceDiagnostics(FieldRepeatStage.All, 2);
            rig.Step();
            Assert.IsNotNull(rig.ForceScratch);
            rig.DisposeForce();
            Assert.IsNull(rig.ForceScratch);
        }

        using (Rig rig = new Rig(positions, headings, teams, layout, 1))
        {
            rig.SetForceDiagnostics(FieldRepeatStage.Collect, 4);
            rig.Step();
            Assert.IsNull(rig.ForceScratch);
            Assert.AreEqual(1, rig.ForcePassRepeatCount);
        }
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

    private static void AssertConvolved(
        Crowd crowd,
        FieldCollectMode mode,
        double floor,
        double relative,
        bool additive)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        double[] raw = FieldNeighborReference.Collect(
            crowd.Positions, crowd.Headings, crowd.Teams, crowd.Layout);
        FieldNeighborReference.Convolve(raw, crowd.Layout, 3.0, Inter, out double[] phi, out double[] field);
        FieldNeighborReference.Gradient(phi, crowd.Layout, field);
        float texel = crowd.Layout.CellSize.x;
        using (Rig rig = new Rig(crowd.Positions, crowd.Headings, crowd.Teams, crowd.Layout, 1, mode))
        {
            rig.Step();
            if (mode == FieldCollectMode.Scatter)
            {
                AssertCorners(crowd, rig.ReadCicWeights(), texel);
                double n0 = SumTeam(rig.ReadAccum(), 0);
                double n1 = SumTeam(rig.ReadAccum(), 1);
                string sum = "texel " + texel + " N0=" + n0.ToString("G8") + " N1=" + n1.ToString("G8");
                Assert.AreEqual(crowd.Positions.Length / 2d, n0, 0.1d, sum);
                Assert.AreEqual(crowd.Positions.Length / 2d, n1, 0.1d, sum);
                AssertAccum(raw, rig.ReadAccum(), texel);
            }

            AssertPhi(phi, rig.ReadPhi(), texel, floor, relative, additive);
            AssertField(field, rig.ReadField(), texel, floor, relative, additive);
        }
    }

    private struct ConditionRow
    {
        public int Index;
        public float Texel;
        public int Seed;
        public double Error;
        public double Sensitivity;
        public double Ratio;
        public float Sa;
        public float Sk;
        public float Sg;
        public float AbsAs;
        public float AbsKs;
        public float AbsNs;

        public string Text()
        {
            return "texel " + Texel.ToString("G4") + " seed " + Seed + " i=" + Index
                + " e=" + Error.ToString("G6") + " s=" + Sensitivity.ToString("G6")
                + " r=" + Ratio.ToString("G6")
                + " SA=" + Sa.ToString("G6") + " SK=" + Sk.ToString("G6") + " SG=" + Sg.ToString("G6")
                + " |As|=" + AbsAs.ToString("G6") + " |Ks|=" + AbsKs.ToString("G6")
                + " |Ns|=" + AbsNs.ToString("G6");
        }
    }

    private static string VariantLine(bool gpuWeights)
    {
        return "t0.6s1 " + WorstText(0.6f, 1, gpuWeights)
            + " | t0.6s2 " + WorstText(0.6f, 2, gpuWeights)
            + " | t0.6s3 " + WorstText(0.6f, 3, gpuWeights)
            + " | t1s1 " + WorstText(1f, 1, gpuWeights)
            + " | t0.5s1 " + WorstText(0.5f, 1, gpuWeights);
    }

    private static string WorstText(float texel, int seed, bool gpuWeights)
    {
        ConditionRow worst = default;
        ConditionRow[] rows = MeasureFloatForce(texel, seed, gpuWeights);
        double max = 0d;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Error > max)
            {
                max = rows[i].Error;
                worst = rows[i];
            }
        }

        return "max=" + max.ToString("G6") + " i=" + worst.Index + " e=" + worst.Error.ToString("G6");
    }

    private static double MaxError(ConditionRow[] rows)
    {
        double max = 0d;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Error > max)
            {
                max = rows[i].Error;
            }
        }

        return max;
    }

    private static ConditionRow[] MeasureFloatForce(float texel, int seed, bool gpuWeights = false)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Crowd crowd = MakeSceneCrowd(texel, seed);
        using (Rig rig = new Rig(crowd.Positions, crowd.Headings, crowd.Teams, crowd.Layout, 1))
        {
            rig.Step();
            Vector2[] cic = gpuWeights ? rig.ReadCicWeights() : null;
            Vector4[] slots = rig.ReadField();
            Vector3[] velocity = rig.ReadVelocity();
            FieldForceReference.FloatSample[] read = FieldForceReference.FloatRead(
                slots, crowd.Positions, crowd.Headings, crowd.Teams, Weights, crowd.Layout, cic);
            var rows = new ConditionRow[read.Length];
            for (int i = 0; i < read.Length; i++)
            {
                double error = Math.Max(
                    Math.Abs(read[i].Fx - velocity[i].x),
                    Math.Abs(read[i].Fy - velocity[i].z));
                rows[i] = new ConditionRow
                {
                    Index = i,
                    Texel = texel,
                    Seed = seed,
                    Error = error,
                    Sa = read[i].Sa,
                    Sk = read[i].Sk,
                    Sg = read[i].Sg,
                    AbsAs = read[i].AbsAs,
                    AbsKs = read[i].AbsKs,
                    AbsNs = read[i].AbsNs,
                };
            }

            return rows;
        }
    }

    private static string ComponentSplit(float texel, int seed, int index)
    {
        Crowd crowd = MakeSceneCrowd(texel, seed);
        using (Rig rig = new Rig(crowd.Positions, crowd.Headings, crowd.Teams, crowd.Layout, 1))
        {
            rig.Step();
            Vector2[] cic = rig.ReadCicWeights();
            var masks = new[]
            {
                new Vector3(Weights[0].x, 0f, 0f),
                new Vector3(0f, Weights[0].y, 0f),
                new Vector3(0f, 0f, Weights[0].z),
            };
            string[] names = { "sep", "align", "coh" };
            string text = "";
            for (int m = 0; m < masks.Length; m++)
            {
                var teamWeights = new[] { masks[m], masks[m] };
                rig.SetWeights(masks[m], masks[m]);
                rig.RerunForce();
                Vector3[] velocity = rig.ReadVelocity();
                FieldForceReference.FloatSample[] read = FieldForceReference.FloatRead(
                    rig.ReadField(), crowd.Positions, crowd.Headings, crowd.Teams, teamWeights, crowd.Layout, cic);
                double at = 0d;
                double max = 0d;
                int which = 0;
                for (int i = 0; i < read.Length; i++)
                {
                    double error = Math.Max(
                        Math.Abs(read[i].Fx - velocity[i].x),
                        Math.Abs(read[i].Fy - velocity[i].z));
                    if (i == index)
                    {
                        at = error;
                    }

                    if (error > max)
                    {
                        max = error;
                        which = i;
                    }
                }

                text += "\n" + names[m] + " i=" + index + " e=" + at.ToString("G6")
                    + " max=" + max.ToString("G6") + " at=" + which;
            }

            return text;
        }
    }

    private static string UlpCase(float texel, int seed)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Crowd crowd = MakeSceneCrowd(texel, seed);
        using (Rig rig = new Rig(crowd.Positions, crowd.Headings, crowd.Teams, crowd.Layout, 1))
        {
            Vector2[] cic = rig.ReadCicWeights();
            int resX = crowd.Layout.Resolution.x;
            int resY = crowd.Layout.Resolution.y;
            double originX = crowd.Layout.Origin.x;
            double originY = crowd.Layout.Origin.y;
            double cellX = crowd.Layout.CellSize.x;
            double cellY = crowd.Layout.CellSize.y;
            double worst = 0d;
            int worstIndex = 0;
            string axis = "x";
            double worstU = 0d;
            double worstTol = 0d;
            int over = 0;
            for (int i = 0; i < crowd.Positions.Length; i++)
            {
                double ux = (crowd.Positions[i].x - originX) / cellX;
                double uy = (crowd.Positions[i].y - originY) / cellY;
                int b = i * 4;
                int cell = (int)cic[b].x;
                double gx = (cell % resX) + (double)cic[b + 1].y + cic[b + 3].y;
                double gy = (cell / resX) + (double)cic[b + 2].y + cic[b + 3].y;
                double dx = Toroidal(gx, WrapDouble(ux, resX), resX);
                double dy = Toroidal(gy, WrapDouble(uy, resY), resY);
                double tolX = 2.0 * FloatUlp(ux);
                double tolY = 2.0 * FloatUlp(uy);
                double breachX = tolX > 0d ? dx / tolX : 0d;
                double breachY = tolY > 0d ? dy / tolY : 0d;
                if (breachX > worst)
                {
                    worst = breachX;
                    worstIndex = i;
                    axis = "x";
                    worstU = ux;
                    worstTol = tolX;
                }

                if (breachY > worst)
                {
                    worst = breachY;
                    worstIndex = i;
                    axis = "y";
                    worstU = uy;
                    worstTol = tolY;
                }

                if (dx > tolX)
                {
                    over++;
                }

                if (dy > tolY)
                {
                    over++;
                }
            }

            string line = "ulp texel " + texel.ToString("G4") + " seed " + seed
                + " maxBreach=" + worst.ToString("G4")
                + " i=" + worstIndex + " " + axis
                + " u=" + worstU.ToString("G8") + " tol=" + worstTol.ToString("G4");
            if (over > 0)
            {
                line += " n=" + over;
            }

            return line + "\n";
        }
    }

    private static double WrapDouble(double u, int res)
    {
        double wrapped = u - res * Math.Floor(u / res);
        if (wrapped < 0d)
        {
            wrapped += res;
        }

        return wrapped;
    }

    private static double Toroidal(double a, double b, int res)
    {
        double delta = a - b;
        delta -= res * Math.Round(delta / res);
        return Math.Abs(delta);
    }

    private static double FloatUlp(double value)
    {
        double abs = Math.Abs(value);
        if (abs == 0d || abs < 1.1754943508222875e-38)
        {
            return 1.401298464324817e-45;
        }

        int exp = (int)Math.Floor(Math.Log(abs) / Math.Log(2.0));
        double bin = Math.Pow(2.0, exp);
        if (bin > abs)
        {
            exp--;
        }
        else if (abs >= bin * 2.0)
        {
            exp++;
        }

        return Math.Pow(2.0, exp - 23);
    }

    private static double Sensitivity(ConditionRow row, float epsilon)
    {
        double sa = row.Sa;
        double sk = row.Sk;
        double sg = row.Sg;
        return Weights[0].y * epsilon * sa / Math.Max(row.AbsAs, 1e-3f)
            + Weights[0].z * epsilon * sk / Math.Max(row.AbsKs, 1e-3f)
            + Weights[0].x * epsilon * sg / Math.Max(row.AbsNs, 1e-3f);
    }

    private static double MaxRatio(ConditionRow[] rows, float epsilon, ref ConditionRow worst)
    {
        double rMax = 0d;
        for (int i = 0; i < rows.Length; i++)
        {
            double s = Sensitivity(rows[i], epsilon);
            rows[i].Sensitivity = s;
            double ratio = s > 0d ? rows[i].Error / s : (rows[i].Error > 0d ? double.PositiveInfinity : 0d);
            rows[i].Ratio = ratio;
            if (ratio > rMax)
            {
                rMax = ratio;
                worst = rows[i];
            }
        }

        return rMax;
    }

    private static string CheckBound(float texel, int seed, int c, float epsilon)
    {
        ConditionRow[] rows = MeasureFloatForce(texel, seed, true);
        ConditionRow worst = default;
        double worstOver = 0d;
        int count = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            double s = Sensitivity(rows[i], epsilon);
            double bound = 1e-5 + c * s;
            double over = rows[i].Error - bound;
            if (over > worstOver)
            {
                worstOver = over;
                rows[i].Sensitivity = s;
                rows[i].Ratio = s > 0d ? rows[i].Error / s : 0d;
                worst = rows[i];
            }

            if (rows[i].Error > bound)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return "";
        }

        return "fail texel " + texel.ToString("G4") + " seed " + seed + " n=" + count
            + " " + worst.Text() + " bound=" + (1e-5 + c * worst.Sensitivity).ToString("G6") + "\n";
    }


    private static void SceneSeam(float texel)
    {
        SpatialHashLayout layout = SceneLayout(texel);
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
        using (Rig rig = new Rig(positions, headings, teams, layout, 1))
        {
            rig.Step();
            FieldForceReference.Sample[] read = FieldForceReference.FieldRead(
                Pack(rig.ReadField()), positions, headings, teams, Weights, layout);
            ErrorStats stats = Measure(read, rig.ReadVelocity());
            Assert.That(
                stats.MaxAbs,
                Is.LessThanOrEqualTo(1e-5),
                "seam texel " + texel + " maxAbs=" + stats.MaxAbs.ToString("G6"));
        }
    }

    private static void SceneRefresh(float texel)
    {
        SpatialHashLayout layout = SceneLayout(texel);
        Vector2[] first =
        {
            layout.Origin + new Vector2(4f, 4f),
            layout.Origin + new Vector2(5.5f, 4.25f),
            layout.Origin + new Vector2(6.2f, 5f),
            layout.Origin + new Vector2(4.4f, 5.6f),
        };
        Vector2[] second =
        {
            layout.Origin + new Vector2(20f, 12f),
            layout.Origin + new Vector2(21.2f, 12.4f),
            layout.Origin + new Vector2(20.4f, 13.5f),
            layout.Origin + new Vector2(22f, 13f),
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
                Assert.AreEqual(built[i].x, held[i].x, "held x " + i);
                Assert.AreEqual(built[i].y, held[i].y, "held y " + i);
                Assert.AreEqual(built[i].z, held[i].z, "held z " + i);
                Assert.AreEqual(built[i].w, held[i].w, "held w " + i);
            }

            FieldForceReference.Sample[] read = FieldForceReference.FieldRead(
                Pack(built), second, headings, teams, Weights, layout);
            ErrorStats stats = Measure(read, rig.ReadVelocity());
            Assert.That(
                stats.MaxAbs,
                Is.LessThanOrEqualTo(1e-5),
                "refresh texel " + texel + " maxAbs=" + stats.MaxAbs.ToString("G6"));
        }
    }

    private static void AssertCorners(Crowd crowd, Vector2[] cic, float texel)
    {
        SpatialHashLayout layout = crowd.Layout;
        int resX = layout.Resolution.x;
        int resY = layout.Resolution.y;
        int mismatches = 0;
        int example = -1;
        int cpuIndex = 0;
        int gpuIndex = 0;
        float cpuU = 0f;
        for (int i = 0; i < crowd.Positions.Length; i++)
        {
            Vector2 u = (crowd.Positions[i] - layout.Origin) / layout.CellSize;
            int ix = (int)Math.Floor(u.x);
            int iy = (int)Math.Floor(u.y);
            for (int corner = 0; corner < 4; corner++)
            {
                int dx = (corner & 1) == 0 ? 0 : 1;
                int dy = corner < 2 ? 0 : 1;
                int wrapped = WrapIndex(ix + dx, iy + dy, resX, resY);
                int gpu = (int)cic[i * 4 + corner].x;
                if (wrapped != gpu)
                {
                    mismatches++;
                    if (example < 0)
                    {
                        example = i;
                        cpuIndex = wrapped;
                        gpuIndex = gpu;
                        cpuU = corner + u.x;
                    }
                }
            }
        }

        Assert.That(
            mismatches,
            Is.EqualTo(0),
            "texel " + texel + " corner mismatches " + mismatches +
            " example " + example + " u " + cpuU.ToString("G9") +
            " cpu " + cpuIndex + " gpu " + gpuIndex);
    }

    private static int WrapIndex(int x, int y, int resX, int resY)
    {
        int wx = x % resX;
        if (wx < 0)
        {
            wx += resX;
        }

        int wy = y % resY;
        if (wy < 0)
        {
            wy += resY;
        }

        return wy * resX + wx;
    }

    private static void AssertAccum(double[] expected, uint[] accum, float texel)
    {
        double worst = 0d;
        int index = -1;
        double exp = 0d;
        double act = 0d;
        int bad = 0;
        double l1 = 0d;
        int count = Math.Min(expected.Length, accum.Length);
        for (int i = 0; i < count; i++)
        {
            double value = unchecked((int)accum[i]) / (double)FieldNeighborSet.FixedPointScale;
            double gap = Math.Abs(expected[i] - value);
            l1 += gap;
            if (gap > 0.01d)
            {
                bad++;
            }

            if (gap > worst)
            {
                worst = gap;
                index = i;
                exp = expected[i];
                act = value;
            }
        }

        int cell = index / 6;
        int res = (int)System.Math.Sqrt(count / 6.0);
        Assert.That(
            worst,
            Is.LessThanOrEqualTo(1e-3),
            "texel " + texel + " accum[" + index + "] cell " + cell +
            " xy " + (cell % res) + "," + (cell / res) +
            " ch " + (index % 6) +
            " exp=" + exp.ToString("G6") + " act=" + act.ToString("G6") +
            " bad=" + bad + " l1=" + l1.ToString("G6"));
    }

    private static void AssertMass(double[] expected, Vector4[] slots, float texel)
    {
        double worst = 0d;
        int index = -1;
        double exp = 0d;
        double act = 0d;
        for (int i = 0; i < slots.Length; i++)
        {
            double[] value = { slots[i].x, slots[i].y, slots[i].z, slots[i].w };
            for (int c = 0; c < 4; c++)
            {
                int flat = i * 4 + c;
                int channel = flat % 16;
                if (channel != 2 && channel != 3 && channel != 10 && channel != 11)
                {
                    continue;
                }

                double gap = Math.Abs(expected[flat] - value[c]);
                if (gap > worst)
                {
                    worst = gap;
                    index = flat;
                    exp = expected[flat];
                    act = value[c];
                }
            }
        }

        Assert.That(
            worst,
            Is.LessThanOrEqualTo(1e-3),
            "texel " + texel + " mass[" + index + "] exp=" + exp.ToString("G6") +
            " act=" + act.ToString("G6") + " maxAbs=" + worst.ToString("G6"));
    }

    private static void AssertField(
        double[] expected,
        Vector4[] slots,
        float texel,
        double floor,
        double relative,
        bool additive)
    {
        double breach = 0d;
        int index = -1;
        double exp = 0d;
        double act = 0d;
        for (int i = 0; i < slots.Length; i++)
        {
            double[] value = { slots[i].x, slots[i].y, slots[i].z, slots[i].w };
            for (int c = 0; c < 4; c++)
            {
                int flat = i * 4 + c;
                double gap = Math.Abs(expected[flat] - value[c]);
                double scale = Math.Abs(expected[flat]);
                double tol = additive ? floor + relative * scale : Math.Max(floor, relative * scale);
                double over = gap / tol;
                if (over > breach)
                {
                    breach = over;
                    index = flat;
                    exp = expected[flat];
                    act = value[c];
                }
            }
        }

        double g = 0d;
        double n = 0d;
        double a = 0d;
        double k = 0d;
        for (int i = 0; i < slots.Length; i++)
        {
            double[] value = { slots[i].x, slots[i].y, slots[i].z, slots[i].w };
            for (int c = 0; c < 4; c++)
            {
                int flat = i * 4 + c;
                double gap = Math.Abs(expected[flat] - value[c]);
                int channel = flat % 16;
                if (channel == 0 || channel == 1 || channel == 8 || channel == 9)
                {
                    g = Math.Max(g, gap);
                }
                else if (channel == 2 || channel == 3 || channel == 10 || channel == 11)
                {
                    n = Math.Max(n, gap);
                }
                else if (channel == 4 || channel == 5 || channel == 12 || channel == 13)
                {
                    a = Math.Max(a, gap);
                }
                else
                {
                    k = Math.Max(k, gap);
                }
            }
        }

        Assert.That(
            breach,
            Is.LessThanOrEqualTo(1d),
            "texel " + texel + " field[" + index + "] exp=" + exp.ToString("G6") +
            " act=" + act.ToString("G6") + " breach=" + breach.ToString("G6") +
            " maxAbs G=" + g.ToString("G6") + " N=" + n.ToString("G6") +
            " A=" + a.ToString("G6") + " K=" + k.ToString("G6"));
    }

    private static void AssertPhi(
        double[] expected,
        Vector2[] phi,
        float texel,
        double floor,
        double relative,
        bool additive)
    {
        double breach = 0d;
        int index = -1;
        double exp = 0d;
        double act = 0d;
        for (int i = 0; i < phi.Length; i++)
        {
            NotePhi(expected[i * 2], phi[i].x, i * 2, floor, relative, additive, ref breach, ref index, ref exp, ref act);
            NotePhi(expected[i * 2 + 1], phi[i].y, i * 2 + 1, floor, relative, additive, ref breach, ref index, ref exp, ref act);
        }

        Assert.That(
            breach,
            Is.LessThanOrEqualTo(1d),
            "texel " + texel + " phi[" + index + "] exp=" + exp.ToString("G6") +
            " act=" + act.ToString("G6") + " breach=" + breach.ToString("G6"));
    }

    private static void NotePhi(
        double expected,
        double actual,
        int index,
        double floor,
        double relative,
        bool additive,
        ref double breach,
        ref int worst,
        ref double exp,
        ref double act)
    {
        double gap = Math.Abs(expected - actual);
        double scale = Math.Abs(expected);
        double tol = additive ? floor + relative * scale : Math.Max(floor, relative * scale);
        double over = gap / tol;
        if (over > breach)
        {
            breach = over;
            worst = index;
            exp = expected;
            act = actual;
        }
    }

    private static double SumTeam(uint[] accum, int team)
    {
        double sum = 0d;
        int channel = team * 3;
        for (int i = channel; i < accum.Length; i += 6)
        {
            sum += unchecked((int)accum[i]) / (double)FieldNeighborSet.FixedPointScale;
        }

        return sum;
    }

    private static SpatialHashLayout Layout(float size)
    {
        float half = size * 0.5f;
        return SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(half, 0f, half), 1f);
    }

    private static SpatialHashLayout SceneLayout(float texel)
    {
        return SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(45f, 0f, 45f), texel);
    }

    private static Crowd MakeSceneCrowd(float texel, int seed = 1)
    {
        SpatialHashLayout layout = SceneLayout(texel);
        const int count = 5000;
        var random = new System.Random(seed);
        var positions = new Vector2[count];
        var headings = new Vector2[count];
        var teams = new int[count];
        float spanX = layout.Size.x - 1e-3f;
        float spanY = layout.Size.y - 1e-3f;
        for (int i = 0; i < count; i++)
        {
            positions[i] = layout.Origin + new Vector2(
                (float)(random.NextDouble() * spanX),
                (float)(random.NextDouble() * spanY));
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
        private readonly TeamParams[] teamSlots;
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
            teamSlots = new TeamParams[TeamProfile.MaxTeams];
            teamSlots[0].Radii = new Vector4(3f, 3f, 3f, 4f);
            teamSlots[1].Radii = new Vector4(3f, 3f, 3f, 4f);
            teamSlots[0].Weights = new Vector4(Weights[0].x, Weights[0].y, Weights[0].z, 0f);
            teamSlots[1].Weights = new Vector4(Weights[1].x, Weights[1].y, Weights[1].z, 0f);
            teams.SetData(teamSlots);
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

        public void SetForceDiagnostics(FieldRepeatStage stage, int repeats)
        {
            force.FieldStage = stage;
            force.FieldRepeatCount = repeats;
        }

        public GraphicsBuffer ForceScratch => force.ForceScratch;

        public int ForcePassRepeatCount => force.RepeatCount;

        public void DisposeForce()
        {
            force.Dispose();
        }

        public void SetWeights(Vector3 own, Vector3 other)
        {
            teamSlots[0].Weights = new Vector4(own.x, own.y, own.z, 0f);
            teamSlots[1].Weights = new Vector4(other.x, other.y, other.z, 0f);
            teams.SetData(teamSlots);
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

        public void RerunForce()
        {
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

        public Vector2[] ReadPhi()
        {
            var phi = new Vector2[context.FieldNeighbor.Layout.CellCount];
            context.FieldNeighbor.Phi.GetData(phi);
            return phi;
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
