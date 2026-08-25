using System;
using System.Globalization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class ZeroMeanScalarPassTests
{
    private const string FluidD = "fluidD";
    private const string FluidPhi = "fluidPhi";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const int Resolution = 64;
    private const int SizeWorld = 32;
    private const int PlusX = 20;
    private const int MinusX = 44;
    private const int DipoleY = 32;
    private const float Bias = 256f;
    private const float DeltaTime = 1f;

    [Test]
    [Category("GPU")]
    public void ConstantField_MeanIsRemoved()
    {
        using (FieldTestHarness harness = CreateDivergenceHarness())
        {
            ZeroMeanScalarPass pass = new ZeroMeanScalarPass();
            try
            {
                float[] seed = FillScalar(1f);
                harness.SeedScalar(FluidD, seed);
                pass.Initialize(harness.Context);
                Assert.AreEqual(512, pass.Scale, "3.1 Scale on 64²");

                float meanBefore = MeanAll(seed);
                harness.RunPass(pass, DeltaTime);

                float[] obtained = harness.ReadScalar(FluidD);
                float meanAfter = MeanAll(obtained);
                float maxAbsAfter = MaxAbs(obtained);
                float tol = 2f / pass.Scale;

                string report =
                    $"3.1 N={Resolution * Resolution} Scale={pass.Scale} Bias={Bias.ToString("G9", CultureInfo.InvariantCulture)} " +
                    $"meanBefore={meanBefore.ToString("G9", CultureInfo.InvariantCulture)} " +
                    $"meanAfter={meanAfter.ToString("G9", CultureInfo.InvariantCulture)} " +
                    $"maxAbsAfter={maxAbsAfter.ToString("G9", CultureInfo.InvariantCulture)}";
                TestContext.WriteLine(report);
                Debug.Log(report);

                Assert.Less(Mathf.Abs(meanAfter), tol, report);
                Assert.Less(maxAbsAfter, tol, report);
            }
            finally
            {
                pass.Dispose();
            }
        }
    }

    [Test]
    [Category("GPU")]
    public void SignedHalves_BothEnterTheSum()
    {
        using (FieldTestHarness harness = CreateDivergenceHarness())
        {
            ZeroMeanScalarPass pass = new ZeroMeanScalarPass();
            try
            {
                float[] seed = new float[Resolution * Resolution];
                int half = seed.Length / 2;
                for (int i = 0; i < half; i++)
                {
                    seed[i] = 1f;
                }

                for (int i = half; i < seed.Length; i++)
                {
                    seed[i] = -1f;
                }

                harness.SeedScalar(FluidD, seed);
                pass.Initialize(harness.Context);
                harness.RunPass(pass, DeltaTime);

                float[] obtained = harness.ReadScalar(FluidD);
                float meanAfter = MeanAll(obtained);
                float tol = 2f / pass.Scale;
                string report =
                    $"3.2 Scale={pass.Scale} meanAfter={meanAfter.ToString("G9", CultureInfo.InvariantCulture)}";
                TestContext.WriteLine(report);
                Debug.Log(report);

                Assert.Less(Mathf.Abs(meanAfter), tol, report);
            }
            finally
            {
                pass.Dispose();
            }
        }
    }

    [Test]
    [Category("GPU")]
    public void Dipole_IsNearNoOp()
    {
        using (FieldTestHarness harness = CreateDivergenceHarness())
        {
            ZeroMeanScalarPass pass = new ZeroMeanScalarPass();
            try
            {
                float[] seed = DipoleSeed();
                harness.SeedScalar(FluidD, seed);
                pass.Initialize(harness.Context);
                harness.RunPass(pass, DeltaTime);

                float[] obtained = harness.ReadScalar(FluidD);
                FieldTestHarness.AssertApproximately(
                    obtained, seed, GraphicsFormat.R32_SFloat, "3.3 dipole nearly no-op");
            }
            finally
            {
                pass.Dispose();
            }
        }
    }

    [Test]
    [Category("GPU")]
    public void ConstantDivergence_JacobiDoesNotDriftMeanPhi()
    {
        using (FieldTestHarness harness = CreatePhiHarness())
        {
            ZeroMeanScalarPass zeroMean = new ZeroMeanScalarPass();
            try
            {
                harness.SeedScalar(FluidD, FillScalar(1f));
                zeroMean.Initialize(harness.Context);
                harness.RunPass(zeroMean, DeltaTime);

                float meanDAfter = MeanAll(harness.ReadScalar(FluidD));

                JacobiPhiPass jacobi = new JacobiPhiPass { Iterations = 40 };
                Assert.AreEqual(40, jacobi.RepeatCount);
                jacobi.Initialize(harness.Context);
                harness.RunPass(jacobi, DeltaTime);

                float meanPhi = MeanAll(harness.ReadScalar(FluidPhi));
                string report =
                    $"3.4 Scale={zeroMean.Scale} " +
                    $"meanD_after={meanDAfter.ToString("G9", CultureInfo.InvariantCulture)} " +
                    $"meanPhi={meanPhi.ToString("G9", CultureInfo.InvariantCulture)}";
                TestContext.WriteLine(report);
                Debug.Log(report);

                Assert.Less(Mathf.Abs(meanPhi), 0.1f, report);
            }
            finally
            {
                zeroMean.Dispose();
            }
        }
    }

    [Test]
    [Category("GPU")]
    public void WarmStart_SecondFrameDoesNotAccumulateMeanPhi()
    {
        using (FieldTestHarness harness = CreatePhiHarness())
        {
            ZeroMeanScalarPass zeroMean = new ZeroMeanScalarPass();
            try
            {
                zeroMean.Initialize(harness.Context);
                JacobiPhiPass jacobi = new JacobiPhiPass { Iterations = 40 };
                Assert.AreEqual(40, jacobi.RepeatCount);
                jacobi.Initialize(harness.Context);

                harness.SeedScalar(FluidD, FillScalar(1f));
                harness.RunPass(zeroMean, DeltaTime);
                harness.RunPass(jacobi, DeltaTime);
                float meanPhi1 = MeanAll(harness.ReadScalar(FluidPhi));

                harness.SeedScalar(FluidD, FillScalar(1f));
                harness.RunPass(zeroMean, DeltaTime);
                harness.RunPass(jacobi, DeltaTime);
                float meanPhi2 = MeanAll(harness.ReadScalar(FluidPhi));

                string report =
                    $"3.5 Scale={zeroMean.Scale} " +
                    $"meanPhi1={meanPhi1.ToString("G9", CultureInfo.InvariantCulture)} " +
                    $"meanPhi2={meanPhi2.ToString("G9", CultureInfo.InvariantCulture)}";
                TestContext.WriteLine(report);
                Debug.Log(report);

                Assert.Less(Mathf.Abs(meanPhi1), 0.1f, report);
                Assert.Less(Mathf.Abs(meanPhi2), 0.1f, report);
            }
            finally
            {
                zeroMean.Dispose();
            }
        }
    }

    private static FieldTestHarness CreateDivergenceHarness()
    {
        FieldDescriptor divergence = ScalarDescriptor(
            FluidD, new Vector2Int(Resolution, Resolution), new Vector2(SizeWorld, SizeWorld));
        Assert.AreEqual(GraphicsFormat.R32_SFloat, divergence.Format);
        return new FieldTestHarness(new[] { divergence }, FluidCompute);
    }

    private static FieldTestHarness CreatePhiHarness()
    {
        FieldDescriptor divergence = ScalarDescriptor(
            FluidD, new Vector2Int(Resolution, Resolution), new Vector2(SizeWorld, SizeWorld));
        FieldDescriptor phi = ScalarDescriptor(
            FluidPhi, new Vector2Int(Resolution, Resolution), new Vector2(SizeWorld, SizeWorld));
        Assert.AreEqual(GraphicsFormat.R32_SFloat, divergence.Format);
        Assert.AreEqual(GraphicsFormat.R32_SFloat, phi.Format);
        return new FieldTestHarness(new[] { divergence, phi }, FluidCompute);
    }

    private static float[] DipoleSeed()
    {
        float[] seed = new float[Resolution * Resolution];
        seed[DipoleY * Resolution + PlusX] = 1f;
        seed[DipoleY * Resolution + MinusX] = -1f;
        return seed;
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

    private static float MeanAll(float[] values)
    {
        double sum = 0d;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return (float)(sum / values.Length);
    }

    private static float MaxAbs(float[] values)
    {
        float maxAbs = 0f;
        for (int i = 0; i < values.Length; i++)
        {
            float abs = Mathf.Abs(values[i]);
            if (abs > maxAbs)
            {
                maxAbs = abs;
            }
        }

        return maxAbs;
    }

    private static FieldDescriptor ScalarDescriptor(string name, Vector2Int resolution, Vector2 size)
    {
        return FieldTestHarness.Descriptor(
            name, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            resolution, size, Color.clear);
    }
}
