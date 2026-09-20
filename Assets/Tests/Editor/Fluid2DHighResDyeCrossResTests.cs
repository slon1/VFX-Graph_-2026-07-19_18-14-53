using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
[Category("GPU")]
public class Fluid2DHighResDyeCrossResTests
{
    private const string Dye = "dye";
    private const string Velocity = "velocity";
    private const string FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute";
    private const int VelocityRes = 64;
    private static readonly Vector2 Size = new Vector2(VelocityRes, VelocityRes);
    private const float DeltaTime = 1f;
    private const int Steps = 8;
    private const float Amp = 1f;
    private const float CenterXTexel = 20.5f;
    private const float CenterYTexel = 32.5f;
    private const float SigmaTexel = 1.5f;
    private static readonly Vector2 CenterUv = new Vector2(CenterXTexel / VelocityRes, CenterYTexel / VelocityRes);
    private const float SigmaUv = SigmaTexel / VelocityRes;
    private static readonly Vector2 Carrier = new Vector2(1.7f, 0f);

    [Test]
    public void GaussianEightSteps_Peak256StrictlyAbovePeak64()
    {
        Assume.That(DeltaTime / (Size.x / VelocityRes), Is.EqualTo(1f).Within(1e-6f));

        Vector2[] velocitySeed = UniformVelocity(Carrier);
        Row row64 = RunRow(64, velocitySeed);
        Row row128 = RunRow(128, velocitySeed);
        Row row256 = RunRow(256, velocitySeed);

        StringBuilder report = new StringBuilder();
        report.AppendLine(
            "F3.0 cross-res dye UV-seed centerUV=(" +
            CenterUv.x.ToString("G9", CultureInfo.InvariantCulture) + "," +
            CenterUv.y.ToString("G9", CultureInfo.InvariantCulture) +
            ") sigmaUV=" + SigmaUv.ToString("G9", CultureInfo.InvariantCulture) +
            " carrier=(1.7,0) steps=8 Size=64 velocity=64² R32 bilinear");
        AppendRow(report, row64);
        AppendRow(report, row128);
        AppendRow(report, row256);
        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);

        Assert.Greater(
            row256.Peak,
            row64.Peak,
            "peak256=" + row256.Peak.ToString("G9", CultureInfo.InvariantCulture) +
            " peak64=" + row64.Peak.ToString("G9", CultureInfo.InvariantCulture) +
            " (F3.0 same-run; bilinear ADR-028 §7 orient 0.74387)");
    }

    private static Row RunRow(int dyeRes, Vector2[] velocitySeed)
    {
        FieldDescriptor dye = FieldTestHarness.Descriptor(
            Dye, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            new Vector2Int(dyeRes, dyeRes), Size, Color.clear);
        FieldDescriptor velocity = FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R32G32_SFloat,
            new Vector2Int(VelocityRes, VelocityRes), Size, Color.clear);

        using (FieldTestHarness harness = new FieldTestHarness(new[] { dye, velocity }, FieldCompute))
        {
            harness.SeedVelocity(Velocity, velocitySeed);
            harness.SeedScalar(Dye, GaussianDyeUv(dyeRes));

            float comXBefore = ComXWorld(harness.ReadScalar(Dye), dyeRes);

            AdvectScalarPass pass = new AdvectScalarPass
            {
                ScalarField = Dye,
                VelocityField = Velocity,
                DissipationRate = 0f,
                Reverse = false,
            };
            pass.Initialize(harness.Context);

            Stopwatch watch = Stopwatch.StartNew();
            harness.RunPass(pass, DeltaTime, Steps);
            watch.Stop();

            float[] after = harness.ReadScalar(Dye);
            AssertFiniteNonNegative(after, "dye=" + dyeRes);
            float dComX = ComXWorld(after, dyeRes) - comXBefore;
            float peak = MaxInterior(after, dyeRes);
            return new Row(
                dyeRes,
                dComX,
                peak,
                watch.Elapsed.TotalMilliseconds / Steps);
        }
    }

    private static void AppendRow(StringBuilder report, Row row)
    {
        report.Append("dye=");
        report.Append(row.DyeRes.ToString(CultureInfo.InvariantCulture));
        report.Append(" dCOM_x=");
        report.Append(row.DComX.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" |dCOM-13.6|=");
        report.Append(Mathf.Abs(row.DComX - 13.6f).ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" peak=");
        report.Append(row.Peak.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" elapsedMs/N=");
        report.Append(row.MsPerStep.ToString("G9", CultureInfo.InvariantCulture));
        report.AppendLine(" timer=cpu_driver_not_gpu");
    }

    private static float[] GaussianDyeUv(int dyeRes)
    {
        float[] field = new float[dyeRes * dyeRes];
        float twoSigma2 = 2f * SigmaUv * SigmaUv;
        for (int y = 0; y < dyeRes; y++)
        {
            for (int x = 0; x < dyeRes; x++)
            {
                float du = (x + 0.5f) / dyeRes - CenterUv.x;
                float dv = (y + 0.5f) / dyeRes - CenterUv.y;
                field[y * dyeRes + x] = Amp * Mathf.Exp(-(du * du + dv * dv) / twoSigma2);
            }
        }

        return field;
    }

    private static Vector2[] UniformVelocity(Vector2 value)
    {
        Vector2[] field = new Vector2[VelocityRes * VelocityRes];
        for (int i = 0; i < field.Length; i++)
        {
            field[i] = value;
        }

        return field;
    }

    private static float ComXWorld(float[] field, int dyeRes)
    {
        float moment = 0f;
        float mass = 0f;
        for (int y = 0; y < dyeRes; y++)
        {
            for (int x = 0; x < dyeRes; x++)
            {
                float extra = field[y * dyeRes + x];
                float worldX = (x + 0.5f) / dyeRes * Size.x;
                moment += extra * worldX;
                mass += extra;
            }
        }

        Assert.That(mass, Is.GreaterThan(0f), "COM mass dyeRes=" + dyeRes);
        return moment / mass;
    }

    private static float MaxInterior(float[] field, int dyeRes)
    {
        float max = float.NegativeInfinity;
        for (int y = 1; y < dyeRes - 1; y++)
        {
            for (int x = 1; x < dyeRes - 1; x++)
            {
                max = Mathf.Max(max, field[y * dyeRes + x]);
            }
        }

        return max;
    }

    private static void AssertFiniteNonNegative(float[] field, string label)
    {
        for (int i = 0; i < field.Length; i++)
        {
            if (float.IsNaN(field[i]) || float.IsInfinity(field[i]))
            {
                Assert.Fail("NaN/Inf " + label + "[" + i + "]=" + field[i]);
            }

            if (field[i] < 0f)
            {
                Assert.Fail("negative " + label + "[" + i + "]=" + field[i].ToString("G9", CultureInfo.InvariantCulture));
            }
        }
    }

    private readonly struct Row
    {
        public Row(int dyeRes, float dComX, float peak, double msPerStep)
        {
            DyeRes = dyeRes;
            DComX = dComX;
            Peak = peak;
            MsPerStep = msPerStep;
        }

        public int DyeRes { get; }
        public float DComX { get; }
        public float Peak { get; }
        public double MsPerStep { get; }
    }
}
