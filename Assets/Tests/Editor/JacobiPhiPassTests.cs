using System;
using System.Globalization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class JacobiPhiPassTests
{
    private const string FluidPhi = "fluidPhi";
    private const string FluidD = "fluidD";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const int Resolution = 64;
    private const int PlusX = 20;
    private const int MinusX = 44;
    private const int DipoleY = 32;
    private const float DeltaTime = 1f;

    [Test]
    [Category("GPU")]
    public void Residual_Dipole_FortyIterationsReducesMaxAbsVersusOne()
    {
        ResidualReport one = RunDipole(iterations: 1);
        ResidualReport forty = RunDipole(iterations: 40);

        Assert.Greater(one.MaxAbsResidual, 0f, "1-iter residual must be nonzero on a dipole");
        Assert.Less(forty.MaxAbsResidual, one.MaxAbsResidual);

        float ratio = one.MaxAbsResidual / forty.MaxAbsResidual;
        float rel = FieldTestHarness.RelativeTolerance(GraphicsFormat.R32_SFloat);
        float sumTol = Mathf.Max(1e-3f, rel * Resolution * Resolution);
        Assert.Less(Mathf.Abs(forty.SumPhi), sumTol, "ΣΦ after 40 iters, zero-mean dipole");

        string report =
            $"4.1 dipole 64² D[{PlusX},{DipoleY}]=+1 D[{MinusX},{DipoleY}]=-1: " +
            $"max|r|_1={one.MaxAbsResidual.ToString("G9", CultureInfo.InvariantCulture)} " +
            $"max|r|_40={forty.MaxAbsResidual.ToString("G9", CultureInfo.InvariantCulture)} " +
            $"ratio={ratio.ToString("G9", CultureInfo.InvariantCulture)} " +
            $"ΣΦ_40={forty.SumPhi.ToString("G9", CultureInfo.InvariantCulture)} " +
            $"sumTol={sumTol.ToString("G9", CultureInfo.InvariantCulture)}";
        TestContext.WriteLine(report);
        Debug.Log(report);

        if (ratio < 10f)
        {
            string note =
                $"4.1 residual ratio {ratio.ToString("G9", CultureInfo.InvariantCulture)}× " +
                "is below 10× (low-frequency Jacobi mode on 64²); not tuning iterations/source.";
            TestContext.WriteLine(note);
            Debug.Log(note);
        }
    }

    [Test]
    [Category("GPU")]
    public void DivergenceField_AfterFortyIterations_MatchesSeedBitwise()
    {
        float[] seed = DipoleSeed();
        using (FieldTestHarness harness = CreateSquareHarness(Resolution, new Vector2(32f, 32f)))
        {
            harness.SeedScalar(FluidD, seed);
            JacobiPhiPass pass = new JacobiPhiPass { Iterations = 40 };
            Assert.AreEqual(40, pass.RepeatCount);
            pass.Initialize(harness.Context);
            harness.RunPass(pass, DeltaTime);

            float[] obtained = harness.ReadScalar(FluidD);
            Assert.AreEqual(seed.Length, obtained.Length);
            for (int i = 0; i < seed.Length; i++)
            {
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(seed[i]),
                    BitConverter.SingleToInt32Bits(obtained[i]),
                    $"4.2 fluidD[{i}] obtained={obtained[i]:G9} seed={seed[i]:G9}");
            }
        }
    }

    [Test]
    [Category("GPU")]
    public void Initialize_MismatchedResolution_ThrowsMatchingResolutionAndPlane()
    {
        FieldDescriptor phi = Descriptor(
            FluidPhi, GraphicsFormat.R32_SFloat, new Vector2Int(32, 32), new Vector2(10f, 10f));
        FieldDescriptor divergence = Descriptor(
            FluidD, GraphicsFormat.R32_SFloat, new Vector2Int(64, 64), new Vector2(10f, 10f));

        using (FieldTestHarness harness = new FieldTestHarness(new[] { phi, divergence }))
        {
            JacobiPhiPass pass = new JacobiPhiPass();
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
        FieldDescriptor phi = Descriptor(
            FluidPhi, GraphicsFormat.R32_SFloat, new Vector2Int(32, 32), new Vector2(10f, 20f));
        FieldDescriptor divergence = Descriptor(
            FluidD, GraphicsFormat.R32_SFloat, new Vector2Int(32, 32), new Vector2(10f, 20f));

        using (FieldTestHarness harness = new FieldTestHarness(new[] { phi, divergence }))
        {
            JacobiPhiPass pass = new JacobiPhiPass();
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
    public void Descriptor_FluidPhi_IsR32SFloat()
    {
        FieldDescriptor phi = Descriptor(
            FluidPhi, GraphicsFormat.R32_SFloat,
            new Vector2Int(Resolution, Resolution), new Vector2(32f, 32f));
        Assert.AreEqual(GraphicsFormat.R32_SFloat, phi.Format, "4.5: fluidPhi must be R32_SFloat");
    }

    private static ResidualReport RunDipole(int iterations)
    {
        using (FieldTestHarness harness = CreateSquareHarness(Resolution, new Vector2(32f, 32f)))
        {
            harness.SeedScalar(FluidD, DipoleSeed());
            JacobiPhiPass pass = new JacobiPhiPass { Iterations = iterations };
            Assert.AreEqual(iterations, pass.RepeatCount);
            pass.Initialize(harness.Context);
            harness.RunPass(pass, DeltaTime);

            float[] phi = harness.ReadScalar(FluidPhi);
            float[] divergence = harness.ReadScalar(FluidD);
            return MeasureResidual(phi, divergence, Resolution, Resolution);
        }
    }

    private static FieldTestHarness CreateSquareHarness(int resolution, Vector2 size)
    {
        FieldDescriptor phi = Descriptor(
            FluidPhi, GraphicsFormat.R32_SFloat, new Vector2Int(resolution, resolution), size);
        FieldDescriptor divergence = Descriptor(
            FluidD, GraphicsFormat.R32_SFloat, new Vector2Int(resolution, resolution), size);
        Assert.AreEqual(GraphicsFormat.R32_SFloat, phi.Format);
        Assert.AreEqual(GraphicsFormat.R32_SFloat, divergence.Format);
        return new FieldTestHarness(new[] { phi, divergence }, FluidCompute);
    }

    private static float[] DipoleSeed()
    {
        float[] seed = new float[Resolution * Resolution];
        seed[DipoleY * Resolution + PlusX] = 1f;
        seed[DipoleY * Resolution + MinusX] = -1f;
        return seed;
    }

    private static ResidualReport MeasureResidual(float[] phi, float[] divergence, int width, int height)
    {
        float maxAbs = 0f;
        float sumPhi = 0f;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                sumPhi += phi[y * width + x];
            }
        }

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                float n = LoadClamped(phi, x, y + 1, width, height);
                float s = LoadClamped(phi, x, y - 1, width, height);
                float e = LoadClamped(phi, x + 1, y, width, height);
                float w = LoadClamped(phi, x - 1, y, width, height);
                float c = phi[y * width + x];
                float d = divergence[y * width + x];
                float residual = n + s + e + w - 4f * c - d;
                float abs = Mathf.Abs(residual);
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                }
            }
        }

        return new ResidualReport(maxAbs, sumPhi);
    }

    private static float LoadClamped(float[] values, int x, int y, int width, int height)
    {
        int cx = Mathf.Clamp(x, 0, width - 1);
        int cy = Mathf.Clamp(y, 0, height - 1);
        return values[cy * width + cx];
    }

    private static FieldDescriptor Descriptor(
        string name,
        GraphicsFormat format,
        Vector2Int resolution,
        Vector2 size)
    {
        return FieldTestHarness.Descriptor(
            name, FieldSemantic.Scalar, format, resolution, size, Color.clear);
    }

    private readonly struct ResidualReport
    {
        public ResidualReport(float maxAbsResidual, float sumPhi)
        {
            MaxAbsResidual = maxAbsResidual;
            SumPhi = sumPhi;
        }

        public float MaxAbsResidual { get; }
        public float SumPhi { get; }
    }
}
