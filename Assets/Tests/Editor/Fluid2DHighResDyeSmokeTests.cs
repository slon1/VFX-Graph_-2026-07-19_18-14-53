using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
[Category("GPU")]
public class Fluid2DHighResDyeSmokeTests
{
    private const string Velocity = "velocity";
    private const string FluidD = "fluidD";
    private const string FluidPhi = "fluidPhi";
    private const string Dye = "dye";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const string FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute";
    private const string GrayScottCompute = "Assets/Shaders/GPU/Passes/GrayScottPasses.compute";
    private const int VelocityRes = 128;
    private const int DyeRes = 512;
    private const float SizeWorld = 32f;
    private const float DeltaTime = 1f;
    private const int TimedFrames = 8;

    [Test]
    public void CrossResComposition_InitializeAndOneFrame_Finite()
    {
        using (FieldTestHarness harness = CreateLiveHarness())
        using (HighResChain chain = HighResChain.Create(harness, jacobiIterations: 8))
        {
            Assert.DoesNotThrow(() => chain.RunFrame(harness));
            AssertFiniteScalar(harness.ReadScalar(Dye), "dye after one frame");
            AssertFiniteVelocity(harness.ReadVelocity(Velocity), "velocity after one frame");
        }
    }

    [Test]
    public void StamChain_HighResDye_RecordsCpuDriverMsPerFrame()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine(
            "profile=Fluid2D_HighResDye velocity=128 dye=512 size=32 Jacobi=40 " +
            "timer=cpu_driver_not_gpu");

        using (FieldTestHarness harness = CreateLiveHarness())
        using (HighResChain chain = HighResChain.Create(harness, jacobiIterations: 40))
        {
            chain.RunFrame(harness);

            Stopwatch watch = Stopwatch.StartNew();
            for (int frame = 0; frame < TimedFrames; frame++)
            {
                chain.RunSolver(harness);
            }

            watch.Stop();
            double msPerFrame = watch.Elapsed.TotalMilliseconds / TimedFrames;
            report.Append("elapsedMs/N=");
            report.Append(msPerFrame.ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" N=");
            report.Append(TimedFrames.ToString(CultureInfo.InvariantCulture));
            report.AppendLine(" timer=cpu_driver_not_gpu Editor velocity=128 dye=512");
            report.AppendLine("ref F2.0 elapsedMs/N=0.208 F2.1 elapsedMs/N=0.272");
        }

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
        Assert.Pass(text);
    }

    private static FieldTestHarness CreateLiveHarness()
    {
        Vector2 size = new Vector2(SizeWorld, SizeWorld);
        Vector2Int velRes = new Vector2Int(VelocityRes, VelocityRes);
        Vector2Int dyeRes = new Vector2Int(DyeRes, DyeRes);
        FieldDescriptor velocity = FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R16G16_SFloat,
            velRes, size, Color.clear);
        FieldDescriptor fluidD = FieldTestHarness.Descriptor(
            FluidD, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            velRes, size, Color.clear);
        FieldDescriptor fluidPhi = FieldTestHarness.Descriptor(
            FluidPhi, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            velRes, size, Color.clear);
        FieldDescriptor dye = FieldTestHarness.Descriptor(
            Dye, FieldSemantic.Scalar, GraphicsFormat.R16_SFloat,
            dyeRes, size, Color.clear);
        return new FieldTestHarness(
            new[] { velocity, fluidD, fluidPhi, dye },
            FluidCompute, FieldCompute, GrayScottCompute);
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

    private sealed class HighResChain : IDisposable
    {
        private readonly ZeroMeanScalarPass zeroMean;

        private HighResChain(
            TouchInjectVelocityFieldPass touch,
            SeedScalarDiskPass seed,
            DivergenceFieldPass divergence,
            ZeroMeanScalarPass zeroMean,
            JacobiPhiPass jacobi,
            SubtractPhiGradientPass subtract,
            SolidWallVelocityPass wallAfterProject,
            AdvectVelocityFieldPass advectVelocity,
            SolidWallVelocityPass wallAfterAdvect,
            AdvectScalarPass advectDye)
        {
            Touch = touch;
            Seed = seed;
            Divergence = divergence;
            this.zeroMean = zeroMean;
            Jacobi = jacobi;
            Subtract = subtract;
            WallAfterProject = wallAfterProject;
            AdvectVelocity = advectVelocity;
            WallAfterAdvect = wallAfterAdvect;
            AdvectDye = advectDye;
        }

        public TouchInjectVelocityFieldPass Touch { get; }
        public SeedScalarDiskPass Seed { get; }
        public DivergenceFieldPass Divergence { get; }
        public JacobiPhiPass Jacobi { get; }
        public SubtractPhiGradientPass Subtract { get; }
        public SolidWallVelocityPass WallAfterProject { get; }
        public AdvectVelocityFieldPass AdvectVelocity { get; }
        public SolidWallVelocityPass WallAfterAdvect { get; }
        public AdvectScalarPass AdvectDye { get; }

        public static HighResChain Create(FieldTestHarness harness, int jacobiIterations)
        {
            TouchInjectVelocityFieldPass touch = new TouchInjectVelocityFieldPass();
            SeedScalarDiskPass seed = new SeedScalarDiskPass
            {
                FieldName = Dye,
                CenterUV = new Vector2(0.5f, 0.5f),
                RadiusUV = 0.16f,
                Value = 1f,
            };
            DivergenceFieldPass divergence = new DivergenceFieldPass();
            ZeroMeanScalarPass zeroMean = new ZeroMeanScalarPass();
            JacobiPhiPass jacobi = new JacobiPhiPass { Iterations = jacobiIterations };
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

            touch.Initialize(harness.Context);
            seed.Initialize(harness.Context);
            divergence.Initialize(harness.Context);
            zeroMean.Initialize(harness.Context);
            jacobi.Initialize(harness.Context);
            subtract.Initialize(harness.Context);
            wallAfterProject.Initialize(harness.Context);
            advectVelocity.Initialize(harness.Context);
            wallAfterAdvect.Initialize(harness.Context);
            advectDye.Initialize(harness.Context);

            return new HighResChain(
                touch,
                seed,
                divergence,
                zeroMean,
                jacobi,
                subtract,
                wallAfterProject,
                advectVelocity,
                wallAfterAdvect,
                advectDye);
        }

        public void RunFrame(FieldTestHarness harness)
        {
            // TouchInject needs World TouchBuffer; harness Context has none.
            harness.RunPass(Seed, DeltaTime);
            RunSolver(harness);
        }

        public void RunSolver(FieldTestHarness harness)
        {
            harness.RunPass(Divergence, DeltaTime);
            harness.RunPass(zeroMean, DeltaTime);
            harness.RunPass(Jacobi, DeltaTime);
            harness.RunPass(Subtract, DeltaTime);
            harness.RunPass(WallAfterProject, DeltaTime);
            harness.RunPass(AdvectVelocity, DeltaTime);
            harness.RunPass(WallAfterAdvect, DeltaTime);
            harness.RunPass(AdvectDye, DeltaTime);
        }

        public void Dispose()
        {
            zeroMean.Dispose();
        }
    }
}
