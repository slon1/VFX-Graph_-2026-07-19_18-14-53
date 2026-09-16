using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
[Category("GPU")]
public class Fluid2DProductionProfileTests
{
    private const string Velocity = "velocity";
    private const string FluidD = "fluidD";
    private const string FluidPhi = "fluidPhi";
    private const string FluidDDiag = "fluidD_diag";
    private const string Dye = "dye";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const string FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute";
    private const string GrayScottCompute = "Assets/Shaders/GPU/Passes/GrayScottPasses.compute";
    private const int Resolution = 128;
    private const float SizeWorld = 32f;
    private const float TexelSize = SizeWorld / Resolution;
    private const float DeltaTime = 1f;
    private const float Amplitude = TexelSize;
    private const int LambdaTexels = 16;
    private const int Periods = 8;
    private const int FrameCount = 8;
    private const float NyquistAmplitude = 0.1f;
    private const int TimedFrames = 8;

    [Test]
    public void StamChain_R16_ReportsInteriorAndBorderDivergence()
    {
        StringBuilder report = new StringBuilder();
        AppendProfileHeader(report);
        report.AppendLine("frame, point, maxAbsInterior, maxAbsBorder");

        using (FieldTestHarness harness = CreateHarness())
        using (ProductionChain chain = ProductionChain.Create(harness))
        {
            FieldDescriptor velocityDesc = VelocityDescriptor();
            harness.SeedVelocity(Velocity, TaylorGreenSeed(velocityDesc, Amplitude));

            DivergenceFieldPass diag = CreateDiagnosticDivergence(harness);
            Measure(harness, diag, 0, "seed", report, readDye: true);

            Diag frame1 = default;
            Diag frame8 = default;
            for (int frame = 1; frame <= FrameCount; frame++)
            {
                chain.RunFrame(harness);
                Diag after = Measure(harness, diag, frame, "afterChain", report, readDye: true);
                if (frame == 1)
                {
                    frame1 = after;
                }

                if (frame == FrameCount)
                {
                    frame8 = after;
                }
            }

            report.Append("logged frame1 interior=");
            report.Append(frame1.Interior.ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" frame8 interior=");
            report.AppendLine(frame8.Interior.ToString("G9", CultureInfo.InvariantCulture));
        }

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
        Assert.Pass(text);
    }

    [Test]
    public void OddEven_DyeAtRest_ReportsCheckerboardEnergy()
    {
        StringBuilder report = new StringBuilder();
        AppendProfileHeader(report);
        report.AppendLine("case=dye_u0");
        report.AppendLine("case, point, E");

        using (FieldTestHarness harness = CreateHarness())
        using (ProductionChain chain = ProductionChain.Create(harness))
        {
            harness.SeedScalar(Dye, CheckerboardScalar(1f));
            float eBefore = CheckerboardEnergy(harness.ReadScalar(Dye));
            AssertFiniteScalar(harness.ReadScalar(Dye), "dye seed");
            AppendEnergy(report, "dye_u0", "before", eBefore);

            for (int frame = 1; frame <= FrameCount; frame++)
            {
                chain.RunFrame(harness);
            }

            float[] dyeAfter = harness.ReadScalar(Dye);
            AssertFiniteScalar(dyeAfter, "dye afterChain");
            float eAfter = CheckerboardEnergy(dyeAfter);
            AppendEnergy(report, "dye_u0", "afterChain", eAfter);
            report.AppendLine(
                "note: DissipationRate=0 u=0 — E(dye) expected almost flat; not an assert");
        }

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
        Assert.Pass(text);
    }

    [Test]
    public void OddEven_VelocityNyquist_ReportsCheckerboardEnergy()
    {
        StringBuilder report = new StringBuilder();
        AppendProfileHeader(report);
        report.Append("A_nyquist=");
        report.AppendLine(NyquistAmplitude.ToString("G9", CultureInfo.InvariantCulture));
        report.AppendLine("case, point, E");

        using (FieldTestHarness harness = CreateHarness())
        using (ProductionChain chain = ProductionChain.Create(harness))
        {
            harness.SeedVelocity(Velocity, CheckerboardVelocityX(NyquistAmplitude));
            DivergenceFieldPass diag = CreateDiagnosticDivergence(harness);

            LogNyquist(harness, diag, "seed", report);
            chain.RunProjection(harness);
            LogNyquist(harness, diag, "afterProjection", report);

            chain.RunAfterProjection(harness);
            for (int frame = 2; frame <= FrameCount; frame++)
            {
                chain.RunFrame(harness);
            }

            LogNyquist(harness, diag, "afterChain", report);
        }

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
        Assert.Pass(text);
    }

    [Test]
    public void StamChain_RecordsCpuDriverMsPerFrame()
    {
        StringBuilder report = new StringBuilder();
        AppendProfileHeader(report);

        using (FieldTestHarness harness = CreateHarness())
        using (ProductionChain chain = ProductionChain.Create(harness))
        {
            FieldDescriptor velocityDesc = VelocityDescriptor();
            harness.SeedVelocity(Velocity, TaylorGreenSeed(velocityDesc, Amplitude));
            chain.RunFrame(harness);

            Stopwatch watch = Stopwatch.StartNew();
            for (int frame = 0; frame < TimedFrames; frame++)
            {
                chain.RunFrame(harness);
            }

            watch.Stop();
            double msPerFrame = watch.Elapsed.TotalMilliseconds / TimedFrames;
            report.Append("elapsedMs/N=");
            report.Append(msPerFrame.ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" N=");
            report.Append(TimedFrames.ToString(CultureInfo.InvariantCulture));
            report.AppendLine(" timer=cpu_driver_not_gpu Editor res=128");
        }

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
        Assert.Pass(text);
    }

    [Test]
    public void SeedScalarDisk_LeavesNonzeroDye()
    {
        using (FieldTestHarness harness = CreateHarness(includeGrayScott: true))
        using (ProductionChain chain = ProductionChain.Create(harness))
        {
            SeedScalarDiskPass seed = new SeedScalarDiskPass { FieldName = Dye };
            seed.Initialize(harness.Context);
            harness.RunPass(seed, DeltaTime);

            for (int frame = 1; frame <= FrameCount; frame++)
            {
                chain.RunFrame(harness);
            }

            float[] dye = harness.ReadScalar(Dye);
            AssertFiniteScalar(dye, "dye after seed+chain");
            float max = MaxAbsAll(dye);
            Assert.Greater(max, 0f, "max(dye) > 0 after SeedScalarDisk + 8 frames");
        }
    }

    private static void LogNyquist(
        FieldTestHarness harness,
        DivergenceFieldPass diag,
        string point,
        StringBuilder report)
    {
        Vector2[] velocity = harness.ReadVelocity(Velocity);
        AssertFiniteVelocity(velocity, point);
        AppendEnergy(report, "u.x", point, CheckerboardEnergyX(velocity));

        harness.RunPass(diag, DeltaTime);
        float[] d = harness.ReadScalar(FluidDDiag);
        AssertFiniteScalar(d, "fluidD_diag " + point);
        AppendEnergy(report, "D", point, CheckerboardEnergy(d));
    }

    private static void AppendEnergy(StringBuilder report, string name, string point, float energy)
    {
        report.Append(name);
        report.Append(", ");
        report.Append(point);
        report.Append(", ");
        report.AppendLine(energy.ToString("G9", CultureInfo.InvariantCulture));
    }

    private static void AppendProfileHeader(StringBuilder report)
    {
        report.AppendLine(
            "profile=Fluid2D formats R16G16/R32/R16 res=128 size=32 h=0.25 A=0.25 dt=1 " +
            "lambdaTexels=16 periods=8 texelCFL=1 Jacobi=40 DissipationRate=0");
    }

    private static FieldTestHarness CreateHarness(bool includeGrayScott = false)
    {
        Vector2Int res = new Vector2Int(Resolution, Resolution);
        Vector2 size = new Vector2(SizeWorld, SizeWorld);
        FieldDescriptor velocity = FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R16G16_SFloat,
            res, size, Color.clear);
        FieldDescriptor fluidD = ScalarDescriptor(FluidD, GraphicsFormat.R32_SFloat, res, size);
        FieldDescriptor fluidPhi = ScalarDescriptor(FluidPhi, GraphicsFormat.R32_SFloat, res, size);
        FieldDescriptor fluidDDiag = ScalarDescriptor(FluidDDiag, GraphicsFormat.R32_SFloat, res, size);
        FieldDescriptor dye = ScalarDescriptor(Dye, GraphicsFormat.R16_SFloat, res, size);
        Assert.AreEqual(GraphicsFormat.R16G16_SFloat, velocity.Format);
        Assert.AreEqual(GraphicsFormat.R32_SFloat, fluidD.Format);
        Assert.AreEqual(GraphicsFormat.R16_SFloat, dye.Format);

        FieldDescriptor[] fields = { velocity, fluidD, fluidPhi, fluidDDiag, dye };
        if (includeGrayScott)
        {
            return new FieldTestHarness(fields, FluidCompute, FieldCompute, GrayScottCompute);
        }

        return new FieldTestHarness(fields, FluidCompute, FieldCompute);
    }

    private static FieldDescriptor VelocityDescriptor()
    {
        return FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R16G16_SFloat,
            new Vector2Int(Resolution, Resolution),
            new Vector2(SizeWorld, SizeWorld),
            Color.clear);
    }

    private static FieldDescriptor ScalarDescriptor(
        string name, GraphicsFormat format, Vector2Int res, Vector2 size)
    {
        return FieldTestHarness.Descriptor(
            name, FieldSemantic.Scalar, format, res, size, Color.clear);
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

    private static Diag Measure(
        FieldTestHarness harness,
        DivergenceFieldPass diag,
        int frame,
        string point,
        StringBuilder report,
        bool readDye)
    {
        harness.RunPass(diag, DeltaTime);
        float[] d = harness.ReadScalar(FluidDDiag);
        Vector2[] velocity = harness.ReadVelocity(Velocity);
        AssertFiniteScalar(d, "fluidD_diag " + point);
        AssertFiniteVelocity(velocity, point);
        if (readDye)
        {
            AssertFiniteScalar(harness.ReadScalar(Dye), "dye " + point);
        }

        float interior = MaxAbsInterior(d);
        float border = MaxAbsBorder(d);
        report.Append(frame.ToString(CultureInfo.InvariantCulture));
        report.Append(", ");
        report.Append(point);
        report.Append(", ");
        report.Append(interior.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(", ");
        report.AppendLine(border.ToString("G9", CultureInfo.InvariantCulture));
        return new Diag(frame, interior, border);
    }

    private static Vector2[] TaylorGreenSeed(FieldDescriptor velocityDesc, float amplitude)
    {
        Vector2[] values = new Vector2[Resolution * Resolution];
        float kappa = 2f * Mathf.PI / (LambdaTexels * TexelSize);
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

    private static float[] CheckerboardScalar(float amplitude)
    {
        float[] values = new float[Resolution * Resolution];
        for (int y = 0; y < Resolution; y++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                values[y * Resolution + x] = CheckerSign(x, y) * amplitude;
            }
        }

        return values;
    }

    private static Vector2[] CheckerboardVelocityX(float amplitude)
    {
        Vector2[] values = new Vector2[Resolution * Resolution];
        for (int y = 0; y < Resolution; y++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                values[y * Resolution + x] = new Vector2(CheckerSign(x, y) * amplitude, 0f);
            }
        }

        return values;
    }

    private static float CheckerSign(int x, int y)
    {
        return ((x + y) % 2 == 0) ? 1f : -1f;
    }

    private static float CheckerboardEnergy(float[] values)
    {
        double sum = 0d;
        int count = 0;
        for (int y = 1; y < Resolution - 1; y++)
        {
            for (int x = 1; x < Resolution - 1; x++)
            {
                sum += values[y * Resolution + x] * CheckerSign(x, y);
                count++;
            }
        }

        return Mathf.Abs((float)(sum / count));
    }

    private static float CheckerboardEnergyX(Vector2[] values)
    {
        double sum = 0d;
        int count = 0;
        for (int y = 1; y < Resolution - 1; y++)
        {
            for (int x = 1; x < Resolution - 1; x++)
            {
                sum += values[y * Resolution + x].x * CheckerSign(x, y);
                count++;
            }
        }

        return Mathf.Abs((float)(sum / count));
    }

    private static float MaxAbsInterior(float[] values)
    {
        float maxAbs = 0f;
        for (int y = 1; y < Resolution - 1; y++)
        {
            for (int x = 1; x < Resolution - 1; x++)
            {
                float abs = Mathf.Abs(values[y * Resolution + x]);
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                }
            }
        }

        return maxAbs;
    }

    private static float MaxAbsBorder(float[] values)
    {
        float maxAbs = 0f;
        for (int y = 0; y < Resolution; y++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                if (x != 0 && x != Resolution - 1 && y != 0 && y != Resolution - 1)
                {
                    continue;
                }

                float abs = Mathf.Abs(values[y * Resolution + x]);
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                }
            }
        }

        return maxAbs;
    }

    private static float MaxAbsAll(float[] values)
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

    private static void AssertFiniteScalar(float[] values, string label)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (float.IsNaN(values[i]) || float.IsInfinity(values[i]))
            {
                Assert.Fail($"NaN/Inf {label}[{i}]={values[i]}");
            }
        }
    }

    private static void AssertFiniteVelocity(Vector2[] values, string point)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (float.IsNaN(values[i].x) || float.IsInfinity(values[i].x) ||
                float.IsNaN(values[i].y) || float.IsInfinity(values[i].y))
            {
                Assert.Fail(
                    $"NaN/Inf velocity[{i}]=({values[i].x},{values[i].y}) point={point}");
            }
        }
    }

    private readonly struct Diag
    {
        public Diag(int frame, float interior, float border)
        {
            Frame = frame;
            Interior = interior;
            Border = border;
        }

        public int Frame { get; }
        public float Interior { get; }
        public float Border { get; }
    }

    private sealed class ProductionChain : IDisposable
    {
        private readonly ZeroMeanScalarPass zeroMean;

        private ProductionChain(
            DivergenceFieldPass divergence,
            ZeroMeanScalarPass zeroMean,
            JacobiPhiPass jacobi,
            SubtractPhiGradientPass subtract,
            SolidWallVelocityPass wallAfterProject,
            AdvectVelocityFieldPass advectVelocity,
            SolidWallVelocityPass wallAfterAdvect,
            AdvectScalarPass advectDye)
        {
            Divergence = divergence;
            this.zeroMean = zeroMean;
            Jacobi = jacobi;
            Subtract = subtract;
            WallAfterProject = wallAfterProject;
            AdvectVelocity = advectVelocity;
            WallAfterAdvect = wallAfterAdvect;
            AdvectDye = advectDye;
        }

        public DivergenceFieldPass Divergence { get; }
        public JacobiPhiPass Jacobi { get; }
        public SubtractPhiGradientPass Subtract { get; }
        public SolidWallVelocityPass WallAfterProject { get; }
        public AdvectVelocityFieldPass AdvectVelocity { get; }
        public SolidWallVelocityPass WallAfterAdvect { get; }
        public AdvectScalarPass AdvectDye { get; }

        public static ProductionChain Create(FieldTestHarness harness)
        {
            ZeroMeanScalarPass zeroMean = new ZeroMeanScalarPass();
            DivergenceFieldPass divergence = new DivergenceFieldPass();
            JacobiPhiPass jacobi = new JacobiPhiPass { Iterations = 40 };
            SubtractPhiGradientPass subtract = new SubtractPhiGradientPass();
            SolidWallVelocityPass wallAfterProject = new SolidWallVelocityPass();
            AdvectVelocityFieldPass advectVelocity = new AdvectVelocityFieldPass
            {
                FieldName = Velocity,
                DissipationRate = 0f,
            };
            SolidWallVelocityPass wallAfterAdvect = new SolidWallVelocityPass();
            AdvectScalarPass advectDye = new AdvectScalarPass
            {
                ScalarField = Dye,
                VelocityField = Velocity,
                DissipationRate = 0f,
            };

            divergence.Initialize(harness.Context);
            zeroMean.Initialize(harness.Context);
            jacobi.Initialize(harness.Context);
            subtract.Initialize(harness.Context);
            wallAfterProject.Initialize(harness.Context);
            advectVelocity.Initialize(harness.Context);
            wallAfterAdvect.Initialize(harness.Context);
            advectDye.Initialize(harness.Context);
            Assert.AreEqual(40, jacobi.RepeatCount);

            return new ProductionChain(
                divergence,
                zeroMean,
                jacobi,
                subtract,
                wallAfterProject,
                advectVelocity,
                wallAfterAdvect,
                advectDye);
        }

        public void RunProjection(FieldTestHarness harness)
        {
            harness.RunPass(Divergence, DeltaTime);
            harness.RunPass(zeroMean, DeltaTime);
            harness.RunPass(Jacobi, DeltaTime);
            harness.RunPass(Subtract, DeltaTime);
        }

        public void RunAfterProjection(FieldTestHarness harness)
        {
            harness.RunPass(WallAfterProject, DeltaTime);
            harness.RunPass(AdvectVelocity, DeltaTime);
            harness.RunPass(WallAfterAdvect, DeltaTime);
            harness.RunPass(AdvectDye, DeltaTime);
        }

        public void RunFrame(FieldTestHarness harness)
        {
            RunProjection(harness);
            RunAfterProjection(harness);
        }

        public void Dispose()
        {
            zeroMean.Dispose();
        }
    }
}
