using System;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
[Category("GPU")]
public class HarrisOrderExperimentTests
{
    private const string Velocity = "velocity";
    private const string FluidD = "fluidD";
    private const string FluidPhi = "fluidPhi";
    private const string FluidDDiag = "fluidD_diag";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const string FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute";
    private const int SizeWorld = 64;
    private const int Resolution = 64;
    private const float DeltaTime = 1f;
    private const int FrameCount = 8;
    private const float SeedInteriorMaxAbs = 1e-3f;
    private const float AfterAdvectAbsoluteFloor = 1e-3f;
    private const float AfterAdvectRelativeFactor = 10f;

    [Test]
    public void ProjectThenAdvect_Vs_Harris_ReportsInteriorAndBorderDivergence()
    {
        float kappa = 2f * Mathf.PI / 4f;
        RunExperiment(lambdaTexels: 4, amplitude: kappa, requireProjectionReducesOnB: false);
    }

    [Test]
    public void ProjectThenAdvect_Vs_Harris_Lambda8_WorkingMode()
    {
        RunExperiment(lambdaTexels: 8, amplitude: 1f, requireProjectionReducesOnB: true);
    }

    private static void RunExperiment(int lambdaTexels, float amplitude, bool requireProjectionReducesOnB)
    {
        int periods = Resolution / lambdaTexels;
        float displacement = amplitude * DeltaTime;
        FieldDescriptor seedDesc = VelocityDescriptor(
            new Vector2Int(Resolution, Resolution), new Vector2(SizeWorld, SizeWorld));
        Vector2[] seed = TaylorGreenSeed(seedDesc, lambdaTexels, amplitude);
        StringBuilder report = new StringBuilder();
        report.Append("lambdaTexels=");
        report.Append(lambdaTexels.ToString(CultureInfo.InvariantCulture));
        report.Append(" periodsOnN64=");
        report.Append(periods.ToString(CultureInfo.InvariantCulture));
        report.Append(" A=");
        report.Append(amplitude.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" displacementTexels=");
        report.AppendLine(displacement.ToString("G9", CultureInfo.InvariantCulture));
        report.AppendLine("frame, order, point, maxAbsInterior, maxAbsBorder");

        float[] afterChainA = new float[FrameCount];
        float[] afterChainB = new float[FrameCount];
        float[] afterChainBorderA = new float[FrameCount];
        float[] afterChainBorderB = new float[FrameCount];

        float seedInteriorA;
        float afterAdvectInteriorA1;
        using (FieldTestHarness harnessA = CreateHarness())
        using (ZeroMeanScalarPass zeroMeanA = new ZeroMeanScalarPass())
        {
            harnessA.SeedVelocity(Velocity, seed);
            DivergenceFieldPass diagA = CreateDiagnosticDivergence(harnessA);
            seedInteriorA = MeasureSeedInterior(harnessA, diagA, "A", report);
            Assert.Less(
                seedInteriorA, SeedInteriorMaxAbs,
                "seed maxAbsInterior(A) < 1e-3 (seed/descriptor, not passes)");

            DivergenceFieldPass chainDivA = new DivergenceFieldPass();
            chainDivA.Initialize(harnessA.Context);
            zeroMeanA.Initialize(harnessA.Context);
            JacobiPhiPass jacobiA = new JacobiPhiPass { Iterations = 40 };
            Assert.AreEqual(40, jacobiA.RepeatCount);
            jacobiA.Initialize(harnessA.Context);
            SubtractPhiGradientPass subtractA = new SubtractPhiGradientPass();
            subtractA.Initialize(harnessA.Context);
            SolidWallVelocityPass wallA1 = new SolidWallVelocityPass();
            wallA1.Initialize(harnessA.Context);
            AdvectVelocityFieldPass advectA = CreateAdvect(harnessA);
            SolidWallVelocityPass wallA2 = new SolidWallVelocityPass();
            wallA2.Initialize(harnessA.Context);

            afterAdvectInteriorA1 = float.NaN;
            for (int frame = 1; frame <= FrameCount; frame++)
            {
                harnessA.RunPass(chainDivA, DeltaTime);
                harnessA.RunPass(zeroMeanA, DeltaTime);
                harnessA.RunPass(jacobiA, DeltaTime);
                harnessA.RunPass(subtractA, DeltaTime);
                harnessA.RunPass(wallA1, DeltaTime);
                harnessA.RunPass(advectA, DeltaTime);
                Diag afterAdvect = Measure(
                    harnessA, diagA, frame, "A", "afterAdvect", report);
                if (frame == 1)
                {
                    afterAdvectInteriorA1 = afterAdvect.Interior;
                }

                harnessA.RunPass(wallA2, DeltaTime);
                Diag afterChain = Measure(harnessA, diagA, frame, "A", "afterChain", report);
                afterChainA[frame - 1] = afterChain.Interior;
                afterChainBorderA[frame - 1] = afterChain.Border;
            }
        }

        float seedInteriorB;
        float afterAdvectInteriorB1;
        float afterChainInteriorB1 = float.NaN;
        using (FieldTestHarness harnessB = CreateHarness())
        using (ZeroMeanScalarPass zeroMeanB = new ZeroMeanScalarPass())
        {
            harnessB.SeedVelocity(Velocity, seed);
            DivergenceFieldPass diagB = CreateDiagnosticDivergence(harnessB);
            seedInteriorB = MeasureSeedInterior(harnessB, diagB, "B", report);
            Assert.Less(
                seedInteriorB, SeedInteriorMaxAbs,
                "seed maxAbsInterior(B) < 1e-3 (seed/descriptor, not passes)");

            AdvectVelocityFieldPass advectB = CreateAdvect(harnessB);
            DivergenceFieldPass chainDivB = new DivergenceFieldPass();
            chainDivB.Initialize(harnessB.Context);
            zeroMeanB.Initialize(harnessB.Context);
            JacobiPhiPass jacobiB = new JacobiPhiPass { Iterations = 40 };
            Assert.AreEqual(40, jacobiB.RepeatCount);
            jacobiB.Initialize(harnessB.Context);
            SubtractPhiGradientPass subtractB = new SubtractPhiGradientPass();
            subtractB.Initialize(harnessB.Context);
            SolidWallVelocityPass wallB = new SolidWallVelocityPass();
            wallB.Initialize(harnessB.Context);

            afterAdvectInteriorB1 = float.NaN;
            for (int frame = 1; frame <= FrameCount; frame++)
            {
                harnessB.RunPass(advectB, DeltaTime);
                Diag afterAdvect = Measure(
                    harnessB, diagB, frame, "B", "afterAdvect", report);
                if (frame == 1)
                {
                    afterAdvectInteriorB1 = afterAdvect.Interior;
                }

                harnessB.RunPass(chainDivB, DeltaTime);
                harnessB.RunPass(zeroMeanB, DeltaTime);
                harnessB.RunPass(jacobiB, DeltaTime);
                harnessB.RunPass(subtractB, DeltaTime);
                harnessB.RunPass(wallB, DeltaTime);
                Diag afterChain = Measure(harnessB, diagB, frame, "B", "afterChain", report);
                afterChainB[frame - 1] = afterChain.Interior;
                afterChainBorderB[frame - 1] = afterChain.Border;
                if (frame == 1)
                {
                    afterChainInteriorB1 = afterChain.Interior;
                }
            }
        }

        AssertAfterAdvectAlive(afterAdvectInteriorA1, seedInteriorA, "A");
        AssertAfterAdvectAlive(afterAdvectInteriorB1, seedInteriorB, "B");

        if (requireProjectionReducesOnB)
        {
            Assert.Less(
                afterChainInteriorB1,
                afterAdvectInteriorB1,
                "gate 4: B frame 1 afterChain < afterAdvect " +
                $"(projection grew D: afterAdvect={afterAdvectInteriorB1.ToString("G9", CultureInfo.InvariantCulture)} " +
                $"afterChain={afterChainInteriorB1.ToString("G9", CultureInfo.InvariantCulture)}; " +
                "lambda is in the Jacobi hole, do not interpret A vs B)");
        }

        int halfCount = 0;
        report.AppendLine("afterChain interior A vs B:");
        for (int i = 0; i < FrameCount; i++)
        {
            float ratio = afterChainB[i] / afterChainA[i];
            bool half = afterChainB[i] < afterChainA[i] / 2f;
            if (half)
            {
                halfCount++;
            }

            report.Append("frame ");
            report.Append((i + 1).ToString(CultureInfo.InvariantCulture));
            report.Append(" A=");
            report.Append(afterChainA[i].ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" B=");
            report.Append(afterChainB[i].ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" B/A=");
            report.Append(ratio.ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" borderA=");
            report.Append(afterChainBorderA[i].ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" borderB=");
            report.Append(afterChainBorderB[i].ToString("G9", CultureInfo.InvariantCulture));
            report.AppendLine(half ? " B<A/2=yes" : " B<A/2=no");
        }

        report.Append("afterChain B<A/2 on ");
        report.Append(halfCount.ToString(CultureInfo.InvariantCulture));
        report.Append('/');
        report.Append(FrameCount.ToString(CultureInfo.InvariantCulture));
        report.AppendLine(" frames (not an assert)");

        string text = report.ToString();
        TestContext.WriteLine(text);
        Debug.Log(text);
        Assert.Pass(text);
    }

    private static void AssertAfterAdvectAlive(float afterAdvect, float seedInterior, string order)
    {
        Assert.Greater(
            afterAdvect, AfterAdvectAbsoluteFloor,
            $"frame 1 afterAdvect maxAbsInterior({order}) > {AfterAdvectAbsoluteFloor}");
        Assert.Greater(
            afterAdvect, AfterAdvectRelativeFactor * seedInterior,
            $"frame 1 afterAdvect maxAbsInterior({order}) > {AfterAdvectRelativeFactor}× seed");
    }

    private static FieldTestHarness CreateHarness()
    {
        Vector2Int res = new Vector2Int(Resolution, Resolution);
        Vector2 size = new Vector2(SizeWorld, SizeWorld);
        FieldDescriptor velocity = VelocityDescriptor(res, size);
        FieldDescriptor fluidD = ScalarDescriptor(FluidD, res, size);
        FieldDescriptor fluidPhi = ScalarDescriptor(FluidPhi, res, size);
        FieldDescriptor fluidDDiag = ScalarDescriptor(FluidDDiag, res, size);
        Assert.AreEqual(GraphicsFormat.R32G32_SFloat, velocity.Format);
        Assert.AreEqual(GraphicsFormat.R32_SFloat, fluidD.Format);
        return new FieldTestHarness(
            new[] { velocity, fluidD, fluidPhi, fluidDDiag },
            FluidCompute, FieldCompute);
    }

    private static AdvectVelocityFieldPass CreateAdvect(FieldTestHarness harness)
    {
        AdvectVelocityFieldPass pass = new AdvectVelocityFieldPass
        {
            FieldName = Velocity,
            DissipationRate = 0f,
        };
        pass.Initialize(harness.Context);
        return pass;
    }

    private static DivergenceFieldPass CreateDiagnosticDivergence(FieldTestHarness harness)
    {
        DivergenceFieldPass pass = new DivergenceFieldPass
        {
            DivergenceField = FluidDDiag,
        };
        pass.Initialize(harness.Context);
        return pass;
    }

    private static float MeasureSeedInterior(
        FieldTestHarness harness,
        DivergenceFieldPass diag,
        string order,
        StringBuilder report)
    {
        Diag seed = Measure(harness, diag, 0, order, "seed", report);
        return seed.Interior;
    }

    private static Diag Measure(
        FieldTestHarness harness,
        DivergenceFieldPass diag,
        int frame,
        string order,
        string point,
        StringBuilder report)
    {
        harness.RunPass(diag, DeltaTime);
        float[] d = harness.ReadScalar(FluidDDiag);
        Vector2[] velocity = harness.ReadVelocity(Velocity);
        AssertFinite(d, velocity, frame, order, point);

        float interior = MaxAbsInterior(d, Resolution, Resolution);
        float border = MaxAbsBorder(d, Resolution, Resolution);
        report.Append(frame.ToString(CultureInfo.InvariantCulture));
        report.Append(", ");
        report.Append(order);
        report.Append(", ");
        report.Append(point);
        report.Append(", ");
        report.Append(interior.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(", ");
        report.AppendLine(border.ToString("G9", CultureInfo.InvariantCulture));
        return new Diag(interior, border);
    }

    private static void AssertFinite(float[] d, Vector2[] velocity, int frame, string order, string point)
    {
        for (int i = 0; i < d.Length; i++)
        {
            if (float.IsNaN(d[i]) || float.IsInfinity(d[i]))
            {
                Assert.Fail($"NaN/Inf fluidD_diag[{i}]={d[i]} frame={frame} order={order} point={point}");
            }
        }

        for (int i = 0; i < velocity.Length; i++)
        {
            if (float.IsNaN(velocity[i].x) || float.IsInfinity(velocity[i].x) ||
                float.IsNaN(velocity[i].y) || float.IsInfinity(velocity[i].y))
            {
                Assert.Fail(
                    $"NaN/Inf velocity[{i}]=({velocity[i].x},{velocity[i].y}) " +
                    $"frame={frame} order={order} point={point}");
            }
        }
    }

    private static Vector2[] TaylorGreenSeed(
        FieldDescriptor velocityDesc, int lambdaTexels, float amplitude)
    {
        Vector2[] values = new Vector2[Resolution * Resolution];
        float kappa = 2f * Mathf.PI / lambdaTexels;
        for (int y = 0; y < Resolution; y++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                Vector2 plane = PlanePosition(velocityDesc, x, y);
                values[y * Resolution + x] = new Vector2(
                    amplitude * Mathf.Sin(kappa * plane.x) * Mathf.Cos(kappa * plane.y),
                    -amplitude * Mathf.Cos(kappa * plane.x) * Mathf.Sin(kappa * plane.y));
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

    private static float MaxAbsBorder(float[] values, int width, int height)
    {
        float maxAbs = 0f;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (x != 0 && x != width - 1 && y != 0 && y != height - 1)
                {
                    continue;
                }

                float abs = Mathf.Abs(values[y * width + x]);
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                }
            }
        }

        return maxAbs;
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

    private readonly struct Diag
    {
        public Diag(float interior, float border)
        {
            Interior = interior;
            Border = border;
        }

        public float Interior { get; }
        public float Border { get; }
    }
}
