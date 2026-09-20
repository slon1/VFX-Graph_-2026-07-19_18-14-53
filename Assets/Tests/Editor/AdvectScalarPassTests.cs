using System;
using System.Globalization;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class AdvectScalarPassTests
{
    private const string Dye = "dye";
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

    [Test]
    [Category("GPU")]
    public void PassiveGaussian_ComMovesEightTexelsWithoutSelfAdvectionOvershoot()
    {
        using (FieldTestHarness harness = CreateHarness())
        {
            AssumeDtOverHIsOne();
            harness.SeedVelocity(Velocity, UniformVelocity(new Vector2(1f, 0f)));
            harness.SeedScalar(Dye, GaussianDye(Amp, Sigma, CenterX, CenterY));

            float[] before = harness.ReadScalar(Dye);
            float comXBefore = ComX(before);
            float comYBefore = ComY(before);

            AdvectScalarPass pass = CreatePass(harness);
            harness.RunPass(pass, DeltaTime, Steps);

            float[] after = harness.ReadScalar(Dye);
            float dComX = ComX(after) - comXBefore;
            float dComY = ComY(after) - comYBefore;

            TestContext.WriteLine(
                $"3.1 Gaussian σ={Sigma} amp={Amp} center=({CenterX},{CenterY}): " +
                $"dCOM_x={dComX.ToString("G9", CultureInfo.InvariantCulture)} " +
                $"dCOM_y={dComY.ToString("G9", CultureInfo.InvariantCulture)} expected 8");

            Assert.That(Mathf.Abs(dComX - 8f), Is.LessThan(0.5f), "dCOM_x vs 8");
            Assert.That(Mathf.Abs(dComY), Is.LessThan(0.5f), "dCOM_y");
            Assert.That(dComX, Is.LessThan(10f), "dCOM_x<10 (13.7 = broken self-advection)");
        }
    }

    [Test]
    [Category("GPU")]
    public void VelocityRoleB_AfterEightSteps_MatchesSeedBitwise()
    {
        Vector2[] seed = UniformVelocity(new Vector2(1f, 0f));
        using (FieldTestHarness harness = CreateHarness())
        {
            AssumeDtOverHIsOne();
            harness.SeedVelocity(Velocity, seed);
            harness.SeedScalar(Dye, GaussianDye(Amp, Sigma, CenterX, CenterY));

            AdvectScalarPass pass = CreatePass(harness);
            harness.RunPass(pass, DeltaTime, Steps);

            Vector2[] obtained = harness.ReadVelocity(Velocity);
            Assert.AreEqual(seed.Length, obtained.Length);
            for (int i = 0; i < seed.Length; i++)
            {
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(seed[i].x),
                    BitConverter.SingleToInt32Bits(obtained[i].x),
                    $"3.2 velocity[{i}].x obtained={obtained[i].x:G9} seed={seed[i].x:G9}");
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(seed[i].y),
                    BitConverter.SingleToInt32Bits(obtained[i].y),
                    $"3.2 velocity[{i}].y obtained={obtained[i].y:G9} seed={seed[i].y:G9}");
            }
        }
    }

    [Test]
    [Category("GPU")]
    public void ConstantDye_UnchangedForAnyVelocity()
    {
        const float value = 0.4f;
        float[] seed = FillScalar(value);
        using (FieldTestHarness harness = CreateHarness())
        {
            harness.SeedVelocity(Velocity, UniformVelocity(new Vector2(1.25f, -0.4f)));
            harness.SeedScalar(Dye, seed);

            AdvectScalarPass pass = CreatePass(harness);
            harness.RunPass(pass, DeltaTime);

            float[] obtained = harness.ReadScalar(Dye);
            FieldTestHarness.AssertApproximately(
                obtained, seed, GraphicsFormat.R32_SFloat, "3.3 constant dye");
        }
    }

    [Test]
    [Category("GPU")]
    public void Initialize_MismatchedPlane_Throws()
    {
        FieldDescriptor dye = FieldTestHarness.Descriptor(
            Dye, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            new Vector2Int(32, 32), new Vector2(32f, 32f), Color.clear);
        FieldDescriptor velocity = FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R32G32_SFloat,
            new Vector2Int(32, 32), new Vector2(64f, 64f), Color.clear);

        using (FieldTestHarness harness = new FieldTestHarness(new[] { dye, velocity }))
        {
            AdvectScalarPass pass = new AdvectScalarPass();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => pass.Initialize(harness.Context));
            TestContext.WriteLine(ex.Message);
            StringAssert.Contains("matching Resolution and plane", ex.Message);
            StringAssert.DoesNotContain("ADR-016 §2.1", ex.Message);
        }
    }

    [Test]
    [Category("GPU")]
    public void Initialize_MismatchedResolutionSamePlane_DoesNotThrow()
    {
        FieldDescriptor dye = FieldTestHarness.Descriptor(
            Dye, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            new Vector2Int(32, 32), new Vector2(32f, 32f), Color.clear);
        FieldDescriptor velocity = FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R32G32_SFloat,
            new Vector2Int(64, 64), new Vector2(32f, 32f), Color.clear);

        using (FieldTestHarness harness = new FieldTestHarness(new[] { dye, velocity }, FieldCompute))
        {
            AdvectScalarPass pass = new AdvectScalarPass();
            Assert.DoesNotThrow(() => pass.Initialize(harness.Context));
        }
    }

    [Test]
    public void Contract_Roles_DyePingPongA_VelocityReadB()
    {
        AdvectScalarPass pass = new AdvectScalarPass();

        Assert.AreEqual("Advect Scalar", pass.DisplayName);
        Assert.AreEqual(PassCategory.Transport, pass.Category);
        Assert.AreEqual(0f, pass.DissipationRate);
        Assert.IsFalse(pass.RequiresSquareTexel);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("AdvectScalar", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldWrites.Count);
        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual(Dye, write.FieldName);
        Assert.AreEqual(FieldAccess.WritePingPong, write.Access);
        Assert.AreEqual(FieldSemantic.Scalar, write.RequiredSemantic);
        Assert.AreEqual(1, write.Channels);
        Assert.AreEqual(FieldSlotRole.A, write.Role);
        Assert.IsFalse(write.AllowResolutionMismatch);

        Assert.AreEqual(1, pass.FieldReads.Count);
        FieldRequest read = pass.FieldReads[0];
        Assert.AreEqual(Velocity, read.FieldName);
        Assert.AreEqual(FieldAccess.Read, read.Access);
        Assert.AreEqual(FieldSemantic.Velocity, read.RequiredSemantic);
        Assert.AreEqual(2, read.Channels);
        Assert.AreEqual(FieldSlotRole.B, read.Role);
        Assert.IsTrue(read.AllowResolutionMismatch);
    }

    [Test]
    public void Contract_Reverse_DefaultFalse()
    {
        AdvectScalarPass pass = new AdvectScalarPass();
        Assert.IsFalse(pass.Reverse);
    }

    [Test]
    [Category("GPU")]
    public void Reverse_IntegerForwardThenReverse_InteriorMatchesSeedBitwise()
    {
        float[] seed = UniqueScalar();
        using (FieldTestHarness harness = CreateHarness())
        {
            AssumeDtOverHIsOne();
            harness.SeedVelocity(Velocity, UniformVelocity(new Vector2(1f, 0f)));
            harness.SeedScalar(Dye, seed);

            AdvectScalarPass forward = CreatePass(harness);
            AdvectScalarPass reverse = new AdvectScalarPass
            {
                ScalarField = Dye,
                VelocityField = Velocity,
                DissipationRate = 0f,
                Reverse = true,
            };
            reverse.Initialize(harness.Context);

            harness.RunPass(forward, DeltaTime);
            harness.RunPass(reverse, DeltaTime);

            float[] obtained = harness.ReadScalar(Dye);
            AssertInteriorBitwiseEqual(seed, obtained, "forward+reverse interior");
        }
    }

    private static FieldTestHarness CreateHarness()
    {
        FieldDescriptor dye = FieldTestHarness.Descriptor(
            Dye, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            new Vector2Int(Res, Res), Size, Color.clear);
        FieldDescriptor velocity = FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R32G32_SFloat,
            new Vector2Int(Res, Res), Size, Color.clear);
        return new FieldTestHarness(new[] { dye, velocity }, FieldCompute);
    }

    private static AdvectScalarPass CreatePass(FieldTestHarness harness)
    {
        AdvectScalarPass pass = new AdvectScalarPass
        {
            ScalarField = Dye,
            VelocityField = Velocity,
            DissipationRate = 0f,
        };
        pass.Initialize(harness.Context);
        return pass;
    }

    private static void AssumeDtOverHIsOne()
    {
        float h = Size.x / Res;
        Assume.That(DeltaTime / h, Is.EqualTo(1f).Within(1e-6f), "3.1 requires dt/h = 1");
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

    private static void AssertInteriorBitwiseEqual(float[] expected, float[] obtained, string label)
    {
        Assert.AreEqual(expected.Length, obtained.Length, $"{label}: length");
        for (int y = 1; y < Res - 1; y++)
        {
            for (int x = 1; x < Res - 1; x++)
            {
                int i = y * Res + x;
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(expected[i]),
                    BitConverter.SingleToInt32Bits(obtained[i]),
                    $"{label} ({x},{y}) expected={expected[i]:G9} obtained={obtained[i]:G9}");
            }
        }
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

    private static float ComY(float[] field)
    {
        float moment = 0f;
        float mass = 0f;
        for (int y = 0; y < Res; y++)
        {
            for (int x = 0; x < Res; x++)
            {
                float extra = field[y * Res + x];
                moment += extra * (y + 0.5f);
                mass += extra;
            }
        }

        Assert.That(mass, Is.GreaterThan(0f), "COM mass");
        return moment / mass;
    }
}
