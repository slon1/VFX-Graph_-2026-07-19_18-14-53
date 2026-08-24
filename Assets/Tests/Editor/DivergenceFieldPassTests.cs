using System;
using System.Globalization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class DivergenceFieldPassTests
{
    private const string Velocity = "velocity";
    private const string FluidD = "fluidD";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const int SizeWorld = 32;
    private const int Resolution = 64;

    [Test]
    [Category("GPU")]
    public void Divergence_UniformVelocity_InteriorIsZero()
    {
        RunOracleCase(
            "uniform",
            seed: (plane, _) => new Vector2(1.25f, -0.4f),
            expectedD: _ => 0f);
    }

    [Test]
    [Category("GPU")]
    public void Divergence_LinearExpansion_InteriorIsFourH()
    {
        float h = SizeWorld / (float)Resolution;
        float expected = 4f * h;
        RunOracleCase(
            "expansion u=(x,y)",
            seed: (plane, _) => plane,
            expectedD: _ => expected);
    }

    [Test]
    [Category("GPU")]
    public void Divergence_Rotational_InteriorIsZero()
    {
        RunOracleCase(
            "rotational u=(-y,x)",
            seed: (plane, _) => new Vector2(-plane.y, plane.x),
            expectedD: _ => 0f);
    }

    [Test]
    [Category("GPU")]
    public void Validator_NonSquareTexel_ThrowsWithPassNameAndAdr016()
    {
        FieldDescriptor velocity = Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R16G16_SFloat,
            new Vector2Int(32, 32), new Vector2(10f, 20f));
        FieldDescriptor fluidD = Descriptor(
            FluidD, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            new Vector2Int(32, 32), new Vector2(10f, 20f));

        using (FieldTestHarness harness = new FieldTestHarness(new[] { velocity, fluidD }))
        {
            DivergenceFieldPass pass = new DivergenceFieldPass();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => SquareTexelValidator.Validate(new SimPass[] { pass }, harness.Context.Fields));
            TestContext.WriteLine(ex.Message);
            StringAssert.Contains(pass.DisplayName, ex.Message);
            StringAssert.Contains("hx=0.3125", ex.Message);
            StringAssert.Contains("hy=0.625", ex.Message);
            StringAssert.Contains("ADR-016 §2.1", ex.Message);
        }
    }

    [Test]
    [Category("GPU")]
    public void Validator_MismatchedResolution_ThrowsWithBothFieldsAndAdr017()
    {
        FieldDescriptor velocity = Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R16G16_SFloat,
            new Vector2Int(32, 32), new Vector2(10f, 10f));
        FieldDescriptor fluidD = Descriptor(
            FluidD, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            new Vector2Int(64, 64), new Vector2(10f, 10f));

        using (FieldTestHarness harness = new FieldTestHarness(new[] { velocity, fluidD }))
        {
            DivergenceFieldPass pass = new DivergenceFieldPass();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => SquareTexelValidator.Validate(new SimPass[] { pass }, harness.Context.Fields));
            TestContext.WriteLine(ex.Message);
            StringAssert.Contains(Velocity, ex.Message);
            StringAssert.Contains(FluidD, ex.Message);
            StringAssert.Contains("(32, 32)", ex.Message);
            StringAssert.Contains("(64, 64)", ex.Message);
            StringAssert.Contains("ADR-017 §1", ex.Message);
        }
    }

    [Test]
    [Category("GPU")]
    public void Validator_NonSquareDomain_SquareTexel_Passes()
    {
        Vector2Int res = new Vector2Int(256, 144);
        Vector2 size = new Vector2(16f, 9f);
        FieldDescriptor velocity = Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R16G16_SFloat, res, size);
        FieldDescriptor fluidD = Descriptor(
            FluidD, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat, res, size);

        using (FieldTestHarness harness = new FieldTestHarness(new[] { velocity, fluidD }))
        {
            Assert.DoesNotThrow(
                () => SquareTexelValidator.Validate(
                    new SimPass[] { new DivergenceFieldPass() }, harness.Context.Fields));
        }
    }

    [Test]
    [Category("GPU")]
    public void Validator_DisabledPass_SkipsSquareAndResolutionChecks()
    {
        FieldDescriptor velocity = Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R16G16_SFloat,
            new Vector2Int(32, 32), new Vector2(10f, 20f));
        FieldDescriptor fluidD = Descriptor(
            FluidD, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            new Vector2Int(64, 64), new Vector2(16f, 9f));

        using (FieldTestHarness harness = new FieldTestHarness(new[] { velocity, fluidD }))
        {
            DivergenceFieldPass pass = new DivergenceFieldPass();
            pass.Enabled = false;
            Assert.DoesNotThrow(
                () => SquareTexelValidator.Validate(new SimPass[] { pass }, harness.Context.Fields));
        }
    }

    private static void RunOracleCase(
        string label,
        Func<Vector2, Vector2Int, Vector2> seed,
        Func<Vector2, float> expectedD)
    {
        FieldDescriptor velocity = Descriptor(
            Velocity,
            FieldSemantic.Velocity,
            GraphicsFormat.R32G32_SFloat,
            new Vector2Int(Resolution, Resolution),
            new Vector2(SizeWorld, SizeWorld));
        FieldDescriptor fluidD = Descriptor(
            FluidD,
            FieldSemantic.Scalar,
            GraphicsFormat.R32_SFloat,
            new Vector2Int(Resolution, Resolution),
            new Vector2(SizeWorld, SizeWorld));

        Assert.AreEqual(GraphicsFormat.R32_SFloat, fluidD.Format, "5.5: fluidD must be R32_SFloat");

        using (FieldTestHarness harness = new FieldTestHarness(
            new[] { velocity, fluidD }, FluidCompute))
        {
            Vector2[] values = new Vector2[Resolution * Resolution];
            for (int y = 0; y < Resolution; y++)
            {
                for (int x = 0; x < Resolution; x++)
                {
                    Vector2 plane = PlanePosition(velocity, x, y);
                    values[y * Resolution + x] = seed(plane, new Vector2Int(x, y));
                }
            }

            harness.SeedVelocity(Velocity, values);
            DivergenceFieldPass pass = new DivergenceFieldPass();
            pass.Initialize(harness.Context);
            harness.RunPass(pass, 1f);

            float[] obtained = harness.ReadScalar(FluidD);
            float rel = FieldTestHarness.RelativeTolerance(GraphicsFormat.R32G32_SFloat);
            int checkedCount = 0;
            float maxDelta = 0f;
            int center = (Resolution / 2) * Resolution + (Resolution / 2);
            float centerExpected = expectedD(PlanePosition(velocity, Resolution / 2, Resolution / 2));
            float centerObtained = obtained[center];
            float centerDelta = Mathf.Abs(centerObtained - centerExpected);

            for (int y = 1; y < Resolution - 1; y++)
            {
                for (int x = 1; x < Resolution - 1; x++)
                {
                    int i = y * Resolution + x;
                    float expected = expectedD(PlanePosition(velocity, x, y));
                    float delta = Mathf.Abs(obtained[i] - expected);
                    if (delta > maxDelta)
                    {
                        maxDelta = delta;
                    }

                    float scale = Mathf.Max(Mathf.Abs(expected), Mathf.Abs(obtained[i]));
                    float tol = Mathf.Max(rel * scale, rel);
                    Assert.LessOrEqual(
                        delta, tol,
                        $"{label}: texel ({x},{y}) obtained={obtained[i]} expected={expected} Δ={delta}");
                    checkedCount++;
                }
            }

            Assert.Greater(checkedCount, 0);
            string report =
                $"{label} oracle R32G32_SFloat: interior={checkedCount} " +
                $"center obtained={centerObtained.ToString(CultureInfo.InvariantCulture)} " +
                $"expected={centerExpected.ToString(CultureInfo.InvariantCulture)} " +
                $"Δ={centerDelta.ToString(CultureInfo.InvariantCulture)} " +
                $"maxΔ={maxDelta.ToString(CultureInfo.InvariantCulture)} relTol={rel.ToString(CultureInfo.InvariantCulture)}";
            TestContext.WriteLine(report);
            Debug.Log(report);
        }
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

    private static FieldDescriptor Descriptor(
        string name,
        FieldSemantic semantic,
        GraphicsFormat format,
        Vector2Int resolution,
        Vector2 size)
    {
        return FieldTestHarness.Descriptor(name, semantic, format, resolution, size, Color.clear);
    }
}
