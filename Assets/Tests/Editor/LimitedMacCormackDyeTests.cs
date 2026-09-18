using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class LimitedMacCormackDyeTests
{
    private const string Dye = "dye";
    private const string Scratch = "dyeMacScratch";
    private const string Velocity = "velocity";
    private const string FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute";
    private const int Res = 64;
    private static readonly Vector2 Size = new Vector2(Res, Res);
    private const float DeltaTime = 1f;
    private const int Steps = 8;
    private const float Sigma = 1.5f;
    private const float Amp = 1f;
    private const float CenterX = 20.5f;
    private const float CenterY = 32.5f;
    private const float OffGridCarrierX = 1.7f;
    private const float OffGridExpectedComX = 13.6f;

    [Test]
    public void Contract_DyePingPongA_ScratchReadB()
    {
        LimitedMacCormackCombinePass pass = new LimitedMacCormackCombinePass();

        Assert.AreEqual("Limited MacCormack Combine", pass.DisplayName);
        Assert.AreEqual(PassCategory.Transport, pass.Category);
        Assert.IsFalse(pass.RequiresSquareTexel);
        Assert.AreEqual(Dye, pass.ScalarField);
        Assert.AreEqual(Scratch, pass.ScratchField);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("MacCormackCombine", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldWrites.Count);
        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual(Dye, write.FieldName);
        Assert.AreEqual(FieldAccess.WritePingPong, write.Access);
        Assert.AreEqual(FieldSemantic.Scalar, write.RequiredSemantic);
        Assert.AreEqual(1, write.Channels);
        Assert.AreEqual(FieldSlotRole.A, write.Role);

        Assert.AreEqual(1, pass.FieldReads.Count);
        FieldRequest read = pass.FieldReads[0];
        Assert.AreEqual(Scratch, read.FieldName);
        Assert.AreEqual(FieldAccess.Read, read.Access);
        Assert.AreEqual(FieldSemantic.Scalar, read.RequiredSemantic);
        Assert.AreEqual(1, read.Channels);
        Assert.AreEqual(FieldSlotRole.B, read.Role);
    }

    [Test]
    [Category("GPU")]
    public void Chain_UZero_MatchesSeedBitwise()
    {
        float[] seed = UniqueScalar();
        using (FieldTestHarness harness = CreateHarness(GraphicsFormat.R32_SFloat))
        {
            AssumeDtOverHIsOne();
            harness.SeedVelocity(Velocity, UniformVelocity(Vector2.zero));
            harness.SeedScalar(Dye, seed);
            harness.SeedScalar(Scratch, FillScalar(999f));

            MacCormackChain chain = CreateChain(harness);
            RunChain(harness, chain, DeltaTime);

            float[] obtained = harness.ReadScalar(Dye);
            AssertBitwiseEqual(seed, obtained, "u=0 UAV-Load canary");
            AssertFiniteNonNegative(obtained, "u=0 dye");
        }
    }

    [Test]
    [Category("GPU")]
    public void Chain_ConstantDye_UnchangedForSkewVelocity()
    {
        const float value = 0.4f;
        float[] seed = FillScalar(value);
        using (FieldTestHarness harness = CreateHarness(GraphicsFormat.R32_SFloat))
        {
            harness.SeedVelocity(Velocity, UniformVelocity(new Vector2(1.25f, -0.4f)));
            harness.SeedScalar(Dye, seed);
            harness.SeedScalar(Scratch, FillScalar(0f));

            MacCormackChain chain = CreateChain(harness);
            RunChain(harness, chain, DeltaTime);

            float[] obtained = harness.ReadScalar(Dye);
            FieldTestHarness.AssertApproximately(
                obtained, seed, GraphicsFormat.R32_SFloat, "constant dye 0.4");
        }
    }

    [Test]
    [Category("GPU")]
    public void Chain_IntegerEightSteps_ComMovesEightAndVelocityBitwise()
    {
        Vector2[] velocitySeed = UniformVelocity(new Vector2(1f, 0f));
        using (FieldTestHarness harness = CreateHarness(GraphicsFormat.R32_SFloat))
        {
            AssumeDtOverHIsOne();
            harness.SeedVelocity(Velocity, velocitySeed);
            harness.SeedScalar(Dye, GaussianDye(Amp, Sigma, CenterX, CenterY));
            harness.SeedScalar(Scratch, FillScalar(0f));

            float comXBefore = ComX(harness.ReadScalar(Dye));
            MacCormackChain chain = CreateChain(harness);
            for (int i = 0; i < Steps; i++)
            {
                RunChain(harness, chain, DeltaTime);
            }

            float[] after = harness.ReadScalar(Dye);
            float dComX = ComX(after) - comXBefore;
            Assert.That(Mathf.Abs(dComX - 8f), Is.LessThan(0.5f), "dCOM_x vs 8");
            Assert.That(dComX, Is.LessThan(10f), "dCOM_x<10");

            Vector2[] velocityAfter = harness.ReadVelocity(Velocity);
            AssertVelocityBitwiseEqual(velocitySeed, velocityAfter, "velocity after MacCormack");
        }
    }

    [Test]
    [Category("GPU")]
    public void Chain_GaussianEightSteps_PeakStrictlyAboveBilinear()
    {
        float[] gaussian = GaussianDye(Amp, Sigma, CenterX, CenterY);
        Vector2[] velocity = UniformVelocity(new Vector2(OffGridCarrierX, 0f));

        float bilinearPeak;
        float bilinearComX;
        using (FieldTestHarness bilinear = CreateBilinearHarness())
        {
            AssumeDtOverHIsOne();
            bilinear.SeedVelocity(Velocity, velocity);
            bilinear.SeedScalar(Dye, gaussian);
            float comXBefore = ComX(bilinear.ReadScalar(Dye));
            AdvectScalarPass pass = new AdvectScalarPass
            {
                ScalarField = Dye,
                VelocityField = Velocity,
                DissipationRate = 0f,
            };
            pass.Initialize(bilinear.Context);
            bilinear.RunPass(pass, DeltaTime, Steps);
            float[] afterBilinear = bilinear.ReadScalar(Dye);
            bilinearPeak = MaxInterior(afterBilinear);
            bilinearComX = ComX(afterBilinear) - comXBefore;
        }

        using (FieldTestHarness harness = CreateHarness(GraphicsFormat.R32_SFloat))
        {
            AssumeDtOverHIsOne();
            harness.SeedVelocity(Velocity, velocity);
            harness.SeedScalar(Dye, gaussian);
            harness.SeedScalar(Scratch, FillScalar(0f));
            float comXBefore = ComX(harness.ReadScalar(Dye));

            MacCormackChain chain = CreateChain(harness);
            Stopwatch timer = Stopwatch.StartNew();
            for (int i = 0; i < Steps; i++)
            {
                RunChain(harness, chain, DeltaTime);
            }

            timer.Stop();
            float elapsedMsPerStep = (float)timer.Elapsed.TotalMilliseconds / Steps;

            float[] after = harness.ReadScalar(Dye);
            float dComX = ComX(after) - comXBefore;
            float macPeak = MaxInterior(after);
            float minDye = MinAll(after);

            TestContext.WriteLine(
                "F2.2 Gaussian σ=" + Sigma + " amp=" + Amp +
                " center=(" + CenterX + "," + CenterY + ") carrier=(" +
                OffGridCarrierX.ToString("G9", CultureInfo.InvariantCulture) +
                ",0): dCOM_x=" +
                dComX.ToString("G9", CultureInfo.InvariantCulture) +
                " macPeak=" + macPeak.ToString("G9", CultureInfo.InvariantCulture) +
                " bilinearPeak=" + bilinearPeak.ToString("G9", CultureInfo.InvariantCulture) +
                " bilinear_dCOM_x=" + bilinearComX.ToString("G9", CultureInfo.InvariantCulture) +
                " min=" + minDye.ToString("G9", CultureInfo.InvariantCulture) +
                " elapsedMs/N=" + elapsedMsPerStep.ToString("G9", CultureInfo.InvariantCulture) +
                " timer=cpu_driver_not_gpu");

            Assert.That(
                Mathf.Abs(dComX - OffGridExpectedComX), Is.LessThan(0.5f),
                "dCOM_x vs 13.6 (off-grid 1.7×8)");
            Assert.That(dComX, Is.LessThan(15f), "dCOM_x<15");
            AssertFiniteNonNegative(after, "Gaussian R32 dye");
            Assert.Greater(
                macPeak, bilinearPeak,
                "max interior dye strictly > bilinear (mac=" +
                macPeak.ToString("G9", CultureInfo.InvariantCulture) +
                " bilinear=" +
                bilinearPeak.ToString("G9", CultureInfo.InvariantCulture) + ")");
        }
    }

    [Test]
    [Category("GPU")]
    public void Chain_R16GaussianEightSteps_FiniteNonNegative()
    {
        using (FieldTestHarness harness = CreateHarness(GraphicsFormat.R16_SFloat))
        {
            AssumeDtOverHIsOne();
            harness.SeedVelocity(Velocity, UniformVelocity(new Vector2(1f, 0f)));
            harness.SeedScalar(Dye, GaussianDye(Amp, Sigma, CenterX, CenterY));
            harness.SeedScalar(Scratch, FillScalar(0f));

            MacCormackChain chain = CreateChain(harness);
            for (int i = 0; i < Steps; i++)
            {
                RunChain(harness, chain, DeltaTime);
            }

            float[] after = harness.ReadScalar(Dye);
            AssertFiniteNonNegative(after, "Gaussian R16 dye");
        }
    }

    private struct MacCormackChain
    {
        public CopyScalarPass Copy;
        public AdvectScalarPass Forward;
        public AdvectScalarPass Reverse;
        public LimitedMacCormackCombinePass Combine;
    }

    private static FieldTestHarness CreateHarness(GraphicsFormat scalarFormat)
    {
        FieldDescriptor dye = FieldTestHarness.Descriptor(
            Dye, FieldSemantic.Scalar, scalarFormat,
            new Vector2Int(Res, Res), Size, Color.clear);
        FieldDescriptor scratch = FieldTestHarness.Descriptor(
            Scratch, FieldSemantic.Scalar, scalarFormat,
            new Vector2Int(Res, Res), Size, Color.clear);
        FieldDescriptor velocity = FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R32G32_SFloat,
            new Vector2Int(Res, Res), Size, Color.clear);
        return new FieldTestHarness(new[] { dye, velocity, scratch }, FieldCompute);
    }

    private static FieldTestHarness CreateBilinearHarness()
    {
        FieldDescriptor dye = FieldTestHarness.Descriptor(
            Dye, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            new Vector2Int(Res, Res), Size, Color.clear);
        FieldDescriptor velocity = FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R32G32_SFloat,
            new Vector2Int(Res, Res), Size, Color.clear);
        return new FieldTestHarness(new[] { dye, velocity }, FieldCompute);
    }

    private static MacCormackChain CreateChain(FieldTestHarness harness)
    {
        CopyScalarPass copy = new CopyScalarPass
        {
            ScalarField = Dye,
            ScratchField = Scratch,
        };
        copy.Initialize(harness.Context);

        AdvectScalarPass forward = new AdvectScalarPass
        {
            ScalarField = Dye,
            VelocityField = Velocity,
            DissipationRate = 0f,
            Reverse = false,
        };
        forward.Initialize(harness.Context);

        AdvectScalarPass reverse = new AdvectScalarPass
        {
            ScalarField = Dye,
            VelocityField = Velocity,
            DissipationRate = 0f,
            Reverse = true,
        };
        reverse.Initialize(harness.Context);

        LimitedMacCormackCombinePass combine = new LimitedMacCormackCombinePass
        {
            ScalarField = Dye,
            ScratchField = Scratch,
        };
        combine.Initialize(harness.Context);

        return new MacCormackChain
        {
            Copy = copy,
            Forward = forward,
            Reverse = reverse,
            Combine = combine,
        };
    }

    private static void RunChain(FieldTestHarness harness, MacCormackChain chain, float deltaTime)
    {
        harness.RunPass(chain.Copy, deltaTime);
        harness.RunPass(chain.Forward, deltaTime);
        harness.RunPass(chain.Reverse, deltaTime);
        harness.RunPass(chain.Combine, deltaTime);
    }

    private static void AssumeDtOverHIsOne()
    {
        float h = Size.x / Res;
        Assume.That(DeltaTime / h, Is.EqualTo(1f).Within(1e-6f), "requires dt/h = 1");
        Assume.That(Size.y / Res, Is.EqualTo(h).Within(1e-6f));
    }

    private static Vector2[] UniformVelocity(Vector2 value)
    {
        Vector2[] field = new Vector2[Res * Res];
        for (int i = 0; i < field.Length; i++)
        {
            field[i] = value;
        }

        return field;
    }

    private static float[] FillScalar(float value)
    {
        float[] field = new float[Res * Res];
        for (int i = 0; i < field.Length; i++)
        {
            field[i] = value;
        }

        return field;
    }

    private static float[] UniqueScalar()
    {
        float[] field = new float[Res * Res];
        for (int i = 0; i < field.Length; i++)
        {
            field[i] = i + 0.125f;
        }

        return field;
    }

    private static float[] GaussianDye(float amp, float sigma, float cx, float cy)
    {
        float[] field = new float[Res * Res];
        float twoSigma2 = 2f * sigma * sigma;
        for (int y = 0; y < Res; y++)
        {
            for (int x = 0; x < Res; x++)
            {
                float dx = (x + 0.5f) - cx;
                float dy = (y + 0.5f) - cy;
                field[y * Res + x] = amp * Mathf.Exp(-(dx * dx + dy * dy) / twoSigma2);
            }
        }

        return field;
    }

    private static float ComX(float[] field)
    {
        float moment = 0f;
        float mass = 0f;
        for (int y = 0; y < Res; y++)
        {
            for (int x = 0; x < Res; x++)
            {
                float extra = field[y * Res + x];
                moment += extra * (x + 0.5f);
                mass += extra;
            }
        }

        Assert.That(mass, Is.GreaterThan(0f), "COM mass");
        return moment / mass;
    }

    private static float MaxInterior(float[] field)
    {
        float max = float.NegativeInfinity;
        for (int y = 1; y < Res - 1; y++)
        {
            for (int x = 1; x < Res - 1; x++)
            {
                max = Mathf.Max(max, field[y * Res + x]);
            }
        }

        return max;
    }

    private static float MinAll(float[] field)
    {
        float min = float.PositiveInfinity;
        for (int i = 0; i < field.Length; i++)
        {
            min = Mathf.Min(min, field[i]);
        }

        return min;
    }

    private static void AssertFiniteNonNegative(float[] values, string label)
    {
        float min = float.PositiveInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            Assert.IsFalse(
                float.IsNaN(values[i]) || float.IsInfinity(values[i]),
                $"{label} [{i}] Inf/NaN value={values[i]:G9}");
            min = Mathf.Min(min, values[i]);
        }

        Assert.That(min, Is.GreaterThanOrEqualTo(0f), $"{label} min={min:G9}");
    }

    private static void AssertBitwiseEqual(float[] expected, float[] obtained, string label)
    {
        Assert.AreEqual(expected.Length, obtained.Length, $"{label}: length");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(
                BitConverter.SingleToInt32Bits(expected[i]),
                BitConverter.SingleToInt32Bits(obtained[i]),
                $"{label} [{i}] expected={expected[i]:G9} obtained={obtained[i]:G9}");
        }
    }

    private static void AssertVelocityBitwiseEqual(Vector2[] expected, Vector2[] obtained, string label)
    {
        Assert.AreEqual(expected.Length, obtained.Length, $"{label}: length");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(
                BitConverter.SingleToInt32Bits(expected[i].x),
                BitConverter.SingleToInt32Bits(obtained[i].x),
                $"{label} [{i}].x expected={expected[i].x:G9} obtained={obtained[i].x:G9}");
            Assert.AreEqual(
                BitConverter.SingleToInt32Bits(expected[i].y),
                BitConverter.SingleToInt32Bits(obtained[i].y),
                $"{label} [{i}].y expected={expected[i].y:G9} obtained={obtained[i].y:G9}");
        }
    }
}
