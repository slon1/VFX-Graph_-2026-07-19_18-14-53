using System;
using System.Globalization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class SubtractPhiGradientPassTests
{
    private const string Velocity = "velocity";
    private const string FluidPhi = "fluidPhi";
    private const string FluidD = "fluidD";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const int SizeWorld = 32;
    private const int Resolution = 64;
    private const int HarmonicK = 8;
    private const float DeltaTime = 1f;

    [Test]
    [Category("GPU")]
    public void ConstantPhi_LeavesVelocityUnchanged()
    {
        Vector2 seedValue = new Vector2(1.25f, -0.4f);
        using (FieldTestHarness harness = CreateVelocityPhiHarness())
        {
            Vector2[] seed = FillVelocity(seedValue);
            harness.SeedVelocity(Velocity, seed);
            harness.SeedScalar(FluidPhi, FillScalar(3f));

            SubtractPhiGradientPass pass = new SubtractPhiGradientPass();
            pass.Initialize(harness.Context);
            harness.RunPass(pass, DeltaTime);

            Vector2[] obtained = harness.ReadVelocity(Velocity);
            FieldTestHarness.AssertApproximately(
                obtained, seed, GraphicsFormat.R32G32_SFloat, "3.1 constant Φ");
        }
    }

    [Test]
    [Category("GPU")]
    public void LinearPhi_MatchesCpuClampOracleOnWholeGrid()
    {
        using (FieldTestHarness harness = CreateVelocityPhiHarness())
        {
            Vector2[] velocitySeed = FillVelocity(new Vector2(1f, 0f));
            float[] phiSeed = new float[Resolution * Resolution];
            for (int y = 0; y < Resolution; y++)
            {
                for (int x = 0; x < Resolution; x++)
                {
                    phiSeed[y * Resolution + x] = 4f * x;
                }
            }

            harness.SeedVelocity(Velocity, velocitySeed);
            harness.SeedScalar(FluidPhi, phiSeed);

            SubtractPhiGradientPass pass = new SubtractPhiGradientPass();
            pass.Initialize(harness.Context);
            harness.RunPass(pass, DeltaTime);

            Vector2[] obtained = harness.ReadVelocity(Velocity);
            Vector2[] expected = CpuSubtractOracle(velocitySeed, phiSeed, Resolution, Resolution);
            FieldTestHarness.AssertApproximately(
                obtained, expected, GraphicsFormat.R32G32_SFloat, "3.2 linear Φ clamp oracle");
        }
    }

    [Test]
    [Category("GPU")]
    public void PhiField_AfterSubtract_MatchesSeedBitwise()
    {
        float[] seed = FillScalar(3f);
        using (FieldTestHarness harness = CreateVelocityPhiHarness())
        {
            harness.SeedVelocity(Velocity, FillVelocity(new Vector2(1.25f, -0.4f)));
            harness.SeedScalar(FluidPhi, seed);

            SubtractPhiGradientPass pass = new SubtractPhiGradientPass();
            pass.Initialize(harness.Context);
            harness.RunPass(pass, DeltaTime);

            float[] obtained = harness.ReadScalar(FluidPhi);
            Assert.AreEqual(seed.Length, obtained.Length);
            for (int i = 0; i < seed.Length; i++)
            {
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(seed[i]),
                    BitConverter.SingleToInt32Bits(obtained[i]),
                    $"3.3 fluidPhi[{i}] obtained={obtained[i]:G9} seed={seed[i]:G9}");
            }
        }
    }

    [Test]
    [Category("GPU")]
    public void Initialize_MismatchedResolution_ThrowsMatchingResolutionAndPlane()
    {
        FieldDescriptor velocity = VelocityDescriptor(
            new Vector2Int(32, 32), new Vector2(10f, 10f));
        FieldDescriptor phi = ScalarDescriptor(
            FluidPhi, new Vector2Int(64, 64), new Vector2(10f, 10f));

        using (FieldTestHarness harness = new FieldTestHarness(new[] { velocity, phi }))
        {
            SubtractPhiGradientPass pass = new SubtractPhiGradientPass();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => pass.Initialize(harness.Context));
            TestContext.WriteLine(ex.Message);
            StringAssert.Contains("matching Resolution and plane", ex.Message);
            StringAssert.DoesNotContain("ADR-016 §2.1", ex.Message);
        }
    }

    [Test]
    [Category("GPU")]
    public void Validator_NonSquareTexel_ThrowsWithAdr016()
    {
        FieldDescriptor velocity = VelocityDescriptor(
            new Vector2Int(32, 32), new Vector2(10f, 20f));
        FieldDescriptor phi = ScalarDescriptor(
            FluidPhi, new Vector2Int(32, 32), new Vector2(10f, 20f));

        using (FieldTestHarness harness = new FieldTestHarness(new[] { velocity, phi }))
        {
            SubtractPhiGradientPass pass = new SubtractPhiGradientPass();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => SquareTexelValidator.Validate(new SimPass[] { pass }, harness.Context.Fields));
            TestContext.WriteLine(ex.Message);
            StringAssert.Contains(pass.DisplayName, ex.Message);
            StringAssert.Contains("ADR-016 §2.1", ex.Message);
            StringAssert.Contains("hx=", ex.Message);
            StringAssert.Contains("hy=", ex.Message);
        }
    }

    [Test]
    [Category("GPU")]
    public void ProjectionChain_HarmonicK8_ReducesInteriorMaxAbsDivergence()
    {
        FieldDescriptor velocityDesc = VelocityDescriptor(
            new Vector2Int(Resolution, Resolution), new Vector2(SizeWorld, SizeWorld));
        FieldDescriptor fluidD = ScalarDescriptor(
            FluidD, new Vector2Int(Resolution, Resolution), new Vector2(SizeWorld, SizeWorld));
        FieldDescriptor fluidPhi = ScalarDescriptor(
            FluidPhi, new Vector2Int(Resolution, Resolution), new Vector2(SizeWorld, SizeWorld));

        using (FieldTestHarness harness = new FieldTestHarness(
            new[] { velocityDesc, fluidD, fluidPhi }, FluidCompute))
        {
            harness.SeedVelocity(Velocity, HarmonicSeed(velocityDesc));

            DivergenceFieldPass divergence = new DivergenceFieldPass();
            divergence.Initialize(harness.Context);
            harness.RunPass(divergence, DeltaTime);

            float[] dBefore = harness.ReadScalar(FluidD);
            float meanD = MeanAll(dBefore);
            float maxBefore = MaxAbsInterior(dBefore, Resolution, Resolution);
            Assert.Greater(maxBefore, 0f, "3.6: interior max|D| must be nonzero before projection");
            float absMeanOverMax = Mathf.Abs(meanD) / maxBefore;
            Assert.Less(
                absMeanOverMax, 0.1f,
                "3.6 gate |mean(D)|/max|D|_interior < 0.1 (broken seed/PlanePosition, not Subtract)");

            JacobiPhiPass jacobi = new JacobiPhiPass { Iterations = 40 };
            Assert.AreEqual(40, jacobi.RepeatCount);
            jacobi.Initialize(harness.Context);
            harness.RunPass(jacobi, DeltaTime);

            SubtractPhiGradientPass subtract = new SubtractPhiGradientPass();
            subtract.Initialize(harness.Context);
            harness.RunPass(subtract, DeltaTime);

            harness.RunPass(divergence, DeltaTime);
            float[] dAfter = harness.ReadScalar(FluidD);
            float maxAfter = MaxAbsInterior(dAfter, Resolution, Resolution);
            float ratio = maxBefore / maxAfter;

            string report =
                $"3.6 k={HarmonicK} 64² Size={SizeWorld}: " +
                $"meanD={meanD.ToString("G9", CultureInfo.InvariantCulture)} " +
                $"absMeanOverMax={absMeanOverMax.ToString("G9", CultureInfo.InvariantCulture)} " +
                $"maxBefore={maxBefore.ToString("G9", CultureInfo.InvariantCulture)} " +
                $"maxAfter={maxAfter.ToString("G9", CultureInfo.InvariantCulture)} " +
                $"ratio={ratio.ToString("G9", CultureInfo.InvariantCulture)}";
            TestContext.WriteLine(report);
            Debug.Log(report);

            Assert.Less(maxAfter, maxBefore / 3f, report);
        }
    }

    private static Vector2[] HarmonicSeed(FieldDescriptor velocityDesc)
    {
        Vector2[] values = new Vector2[Resolution * Resolution];
        float twoPiKOverL = 2f * Mathf.PI * HarmonicK / SizeWorld;
        for (int y = 0; y < Resolution; y++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                Vector2 plane = PlanePosition(velocityDesc, x, y);
                values[y * Resolution + x] = new Vector2(
                    Mathf.Sin(twoPiKOverL * plane.x),
                    Mathf.Sin(twoPiKOverL * plane.y));
            }
        }

        return values;
    }

    private static Vector2 PlanePosition(FieldDescriptor descriptor, int x, int y)
    {
        Vector2Int res = descriptor.Resolution;
        float u = (x + 0.5f) / res.x;
        float v = (y + 0.5f) / res.y;
        Vector3 world = descriptor.Origin
            + descriptor.AxisU * descriptor.Size.x * (u - 0.5f)
            + descriptor.AxisV * descriptor.Size.y * (v - 0.5f);
        Vector3 local = world - descriptor.Origin;
        return new Vector2(
            Vector3.Dot(local, descriptor.AxisU),
            Vector3.Dot(local, descriptor.AxisV));
    }

    private static Vector2[] CpuSubtractOracle(Vector2[] velocity, float[] phi, int width, int height)
    {
        Vector2[] expected = new Vector2[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float n = LoadClamped(phi, x, y + 1, width, height);
                float s = LoadClamped(phi, x, y - 1, width, height);
                float e = LoadClamped(phi, x + 1, y, width, height);
                float w = LoadClamped(phi, x - 1, y, width, height);
                Vector2 u = velocity[y * width + x];
                expected[y * width + x] = u - new Vector2((e - w) * 0.25f, (n - s) * 0.25f);
            }
        }

        return expected;
    }

    private static float LoadClamped(float[] values, int x, int y, int width, int height)
    {
        int cx = Mathf.Clamp(x, 0, width - 1);
        int cy = Mathf.Clamp(y, 0, height - 1);
        return values[cy * width + cx];
    }

    private static float MeanAll(float[] values)
    {
        double sum = 0d;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return (float)(sum / values.Length);
    }

    private static float MaxAbsInterior(float[] values, int width, int height)
    {
        float maxAbs = 0f;
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                float abs = Mathf.Abs(values[y * width + x]);
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                }
            }
        }

        return maxAbs;
    }

    private static FieldTestHarness CreateVelocityPhiHarness()
    {
        FieldDescriptor velocity = VelocityDescriptor(
            new Vector2Int(Resolution, Resolution), new Vector2(SizeWorld, SizeWorld));
        FieldDescriptor phi = ScalarDescriptor(
            FluidPhi, new Vector2Int(Resolution, Resolution), new Vector2(SizeWorld, SizeWorld));
        Assert.AreEqual(GraphicsFormat.R32G32_SFloat, velocity.Format);
        Assert.AreEqual(GraphicsFormat.R32_SFloat, phi.Format);
        return new FieldTestHarness(new[] { velocity, phi }, FluidCompute);
    }

    private static Vector2[] FillVelocity(Vector2 value)
    {
        Vector2[] values = new Vector2[Resolution * Resolution];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = value;
        }

        return values;
    }

    private static float[] FillScalar(float value)
    {
        float[] values = new float[Resolution * Resolution];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = value;
        }

        return values;
    }

    private static FieldDescriptor VelocityDescriptor(Vector2Int resolution, Vector2 size)
    {
        return FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R32G32_SFloat,
            resolution, size, Color.clear);
    }

    private static FieldDescriptor ScalarDescriptor(string name, Vector2Int resolution, Vector2 size)
    {
        return FieldTestHarness.Descriptor(
            name, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            resolution, size, Color.clear);
    }
}
