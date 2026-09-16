using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class VorticityConfinementPassTests
{
    private const string Velocity = "velocity";
    private const string FluidD = "fluidD";
    private const string FluidPhi = "fluidPhi";
    private const string FluidDDiag = "fluidD_diag";
    private const string Dye = "dye";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const string FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute";
    private const int IdentityResolution = 64;
    private const float IdentitySize = 64f;
    private const int ProfileResolution = 128;
    private const float ProfileSize = 32f;
    private const float TexelSize = ProfileSize / ProfileResolution;
    private const float DeltaTime = 1f;
    private const float Amplitude = TexelSize;
    private const int LambdaTexels = 16;
    private const int Periods = 8;
    private const int FrameCount = 8;
    private const float NyquistAmplitude = 0.1f;
    private const int TimedFrames = 8;
    private static readonly Vector2 UniformSeed = new Vector2(1.25f, -0.4f);

    [Test]
    public void Contract_Transport_WritePingPong_Velocity_SquareTexel()
    {
        VorticityConfinementPass pass = new VorticityConfinementPass();

        Assert.AreEqual("Vorticity Confinement", pass.DisplayName);
        Assert.AreEqual(PassCategory.Transport, pass.Category);
        Assert.IsTrue(pass.RequiresSquareTexel);
        Assert.AreEqual("velocity", pass.VelocityField);
        Assert.AreEqual(1f, pass.EpsilonVc);
        Assert.AreEqual(1, pass.RepeatCount);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("VorticityConfinement", (string)kernelName.GetValue(pass));

        Assert.AreEqual(0, pass.FieldReads.Count);
        Assert.AreEqual(1, pass.FieldWrites.Count);
        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual("velocity", write.FieldName);
        Assert.AreEqual(FieldAccess.WritePingPong, write.Access);
        Assert.AreEqual(FieldSemantic.Velocity, write.RequiredSemantic);
        Assert.AreEqual(2, write.Channels);
    }

    [Test]
    [Category("GPU")]
    public void Validator_NonSquareTexel_ThrowsWithAdr016()
    {
        FieldDescriptor velocity = VelocityDescriptor(
            GraphicsFormat.R32G32_SFloat,
            new Vector2Int(32, 32),
            new Vector2(10f, 20f));

        using (FieldTestHarness harness = new FieldTestHarness(new[] { velocity }))
        {
            VorticityConfinementPass pass = new VorticityConfinementPass();
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
    public void Identity_EpsilonZero_R32_BitwiseAfterSeed()
    {
        AssertIdentityEpsilonZero(GraphicsFormat.R32G32_SFloat);
    }

    [Test]
    [Category("GPU")]
    public void Identity_EpsilonZero_R16_BitwiseAfterSeed()
    {
        AssertIdentityEpsilonZero(GraphicsFormat.R16G16_SFloat);
    }

    [Test]
    [Category("GPU")]
    public void Uniform_EpsilonOne_InteriorBitwiseAfterSeed()
    {
        using (FieldTestHarness harness = CreateIdentityHarness(GraphicsFormat.R32G32_SFloat))
        {
            Vector2[] seed = FillVelocity(IdentityResolution, UniformSeed);
            harness.SeedVelocity(Velocity, seed);
            Vector2[] afterSeed = harness.ReadVelocity(Velocity);

            VorticityConfinementPass pass = CreateVc(harness, 1f);
            harness.RunPass(pass, DeltaTime);
            Vector2[] after = harness.ReadVelocity(Velocity);

            AssertFiniteVelocity(after, "uniform ε=1");
            AssertInteriorBitwiseEqual(afterSeed, after, IdentityResolution, "3.2 uniform interior");
        }
    }

    [Test]
    [Category("GPU")]
    public void TaylorGreen_EpsilonOne_InteriorDeltaNonzero_EpsilonZero_Unchanged()
    {
        FieldDescriptor desc = VelocityDescriptor(
            GraphicsFormat.R16G16_SFloat,
            new Vector2Int(ProfileResolution, ProfileResolution),
            new Vector2(ProfileSize, ProfileSize));
        Vector2[] seed = TaylorGreenSeed(desc, Amplitude);

        using (FieldTestHarness eps1 = CreateProfileVelocityHarness())
        {
            eps1.SeedVelocity(Velocity, seed);
            Vector2[] afterSeed = eps1.ReadVelocity(Velocity);
            VorticityConfinementPass pass = CreateVc(eps1, 1f);
            eps1.RunPass(pass, DeltaTime);
            Vector2[] after = eps1.ReadVelocity(Velocity);
            AssertFiniteVelocity(after, "TG ε=1");
            float maxDelta = MaxAbsDeltaInterior(afterSeed, after, ProfileResolution);
            Assert.Greater(maxDelta, 0f, "3.3 ε=1 max|Δu| interior > 0");
        }

        using (FieldTestHarness eps0 = CreateProfileVelocityHarness())
        {
            eps0.SeedVelocity(Velocity, seed);
            Vector2[] afterSeed = eps0.ReadVelocity(Velocity);
            VorticityConfinementPass pass = CreateVc(eps0, 0f);
            eps0.RunPass(pass, DeltaTime);
            Vector2[] after = eps0.ReadVelocity(Velocity);
            AssertFiniteVelocity(after, "TG ε=0");
            float maxDelta = MaxAbsDeltaInterior(afterSeed, after, ProfileResolution);
            Assert.AreEqual(0f, maxDelta, "3.3 ε=0 max|Δu| interior = 0");
        }
    }

    [Test]
    [Category("GPU")]
    public void HarrisChain_ReportsDivergence_EpsilonZeroGate_AndEpsilonOneCeiling()
    {
        FieldDescriptor desc = VelocityDescriptor(
            GraphicsFormat.R16G16_SFloat,
            new Vector2Int(ProfileResolution, ProfileResolution),
            new Vector2(ProfileSize, ProfileSize));
        Vector2[] seed = TaylorGreenSeed(desc, Amplitude);
        StringBuilder report = new StringBuilder();
        AppendProfileHeader(report, "chain");
        report.AppendLine("frame, variant, point, maxAbsInterior, maxAbsBorder");

        Diag frame1None = default;
        Diag frame8None = default;
        Diag frame1Eps0 = default;
        Diag frame8Eps0 = default;
        Diag frame1Eps1 = default;
        Diag frame8Eps1 = default;

        RunDivergenceChain(seed, null, report, out frame1None, out frame8None);
        RunDivergenceChain(seed, 0f, report, out frame1Eps0, out frame8Eps0);
        RunDivergenceChain(seed, 1f, report, out frame1Eps1, out frame8Eps1);

        report.Append("logged frame1 none=");
        report.Append(frame1None.Interior.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" ε0=");
        report.Append(frame1Eps0.Interior.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" ε1=");
        report.AppendLine(frame1Eps1.Interior.ToString("G9", CultureInfo.InvariantCulture));
        report.Append("logged frame8 none=");
        report.Append(frame8None.Interior.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" ε0=");
        report.Append(frame8Eps0.Interior.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" ε1=");
        report.AppendLine(frame8Eps1.Interior.ToString("G9", CultureInfo.InvariantCulture));
        report.Append("ratio frame1 ε1/none=");
        report.AppendLine(
            (frame1Eps1.Interior / Mathf.Max(frame1None.Interior, 1e-12f))
                .ToString("G9", CultureInfo.InvariantCulture));
        report.Append("ratio frame8 ε1/none=");
        report.AppendLine(
            (frame8Eps1.Interior / Mathf.Max(frame8None.Interior, 1e-12f))
                .ToString("G9", CultureInfo.InvariantCulture));
        report.Append("D_ε1_frame8 / D_none_frame1=");
        report.AppendLine(
            (frame8Eps1.Interior / Mathf.Max(frame1None.Interior, 1e-12f))
                .ToString("G9", CultureInfo.InvariantCulture));

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);

        AssertCloseR16(frame1Eps0.Interior, frame1None.Interior, "3.4 ε=0 vs none frame1 interior D");
        AssertCloseR16(frame8Eps0.Interior, frame8None.Interior, "3.4 ε=0 vs none frame8 interior D");
        Assert.LessOrEqual(
            frame1Eps1.Interior, 2f * frame1None.Interior,
            $"3.4 ε=1 frame1 D={frame1Eps1.Interior:G9} > 2× none={frame1None.Interior:G9}");
        Assert.LessOrEqual(
            frame8Eps1.Interior, 10f * frame8None.Interior,
            $"3.4 ε=1 frame8 D={frame8Eps1.Interior:G9} > 10× none={frame8None.Interior:G9}");
    }

    [Test]
    [Category("GPU")]
    public void OddEven_VelocityNyquist_EpsilonZeroVsOne_LogsCheckerboardEnergy()
    {
        StringBuilder report = new StringBuilder();
        AppendProfileHeader(report, "nyquist");
        report.Append("A_nyquist=");
        report.AppendLine(NyquistAmplitude.ToString("G9", CultureInfo.InvariantCulture));
        report.AppendLine("case, epsilonVc, point, E");

        Vector2[] seed = CheckerboardVelocityX(NyquistAmplitude);
        LogNyquistChain(seed, 0f, report);
        LogNyquistChain(seed, 1f, report);
        report.AppendLine(
            "note: F2.0 E(u.x) 0.100 → 0.00891 is production Project→Advect, other order, not a threshold");

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
        Assert.Pass(text);
    }

    [Test]
    [Category("GPU")]
    public void OddEven_DyeAtRest_EpsilonOne_CheckerboardEnergyStaysOne()
    {
        StringBuilder report = new StringBuilder();
        AppendProfileHeader(report, "dye_u0");
        report.AppendLine("case=dye_u0 epsilonVc=1");

        using (FieldTestHarness harness = CreateProfileChainHarness())
        using (HarrisChain chain = HarrisChain.Create(harness, 1f))
        {
            harness.SeedScalar(Dye, CheckerboardScalar(1f));
            float eBefore = CheckerboardEnergy(harness.ReadScalar(Dye));
            AssertFiniteScalar(harness.ReadScalar(Dye), "dye seed");
            report.Append("E(dye) before=");
            report.AppendLine(eBefore.ToString("G9", CultureInfo.InvariantCulture));
            Assert.AreEqual(1f, eBefore, 1e-3f, "3.5b E(dye) seed = 1");

            for (int frame = 1; frame <= FrameCount; frame++)
            {
                chain.RunFrame(harness);
            }

            float[] dyeAfter = harness.ReadScalar(Dye);
            AssertFiniteScalar(dyeAfter, "dye afterChain");
            AssertFiniteVelocity(harness.ReadVelocity(Velocity), "u afterChain dye_u0");
            float eAfter = CheckerboardEnergy(dyeAfter);
            report.Append("E(dye) afterChain=");
            report.AppendLine(eAfter.ToString("G9", CultureInfo.InvariantCulture));
            Assert.AreEqual(1f, eAfter, 1e-3f, "3.5b E(dye) afterChain = 1");
        }

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
    }

    [Test]
    [Category("GPU")]
    public void HarrisChain_RecordsCpuDriverMsPerFrame()
    {
        StringBuilder report = new StringBuilder();
        AppendProfileHeader(report, "ms");

        FieldDescriptor desc = VelocityDescriptor(
            GraphicsFormat.R16G16_SFloat,
            new Vector2Int(ProfileResolution, ProfileResolution),
            new Vector2(ProfileSize, ProfileSize));
        Vector2[] seed = TaylorGreenSeed(desc, Amplitude);

        using (FieldTestHarness harness = CreateProfileChainHarness())
        using (HarrisChain chain = HarrisChain.Create(harness, 1f))
        {
            harness.SeedVelocity(Velocity, seed);
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
            report.AppendLine("F2.0 elapsedMs/N=0.208 (record, not a threshold)");
        }

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
        Assert.Pass(text);
    }

    [Test]
    [Category("GPU")]
    public void KineticEnergy_EpsilonOne_AtMostTenTimesEpsilonZero_Frame8()
    {
        FieldDescriptor desc = VelocityDescriptor(
            GraphicsFormat.R16G16_SFloat,
            new Vector2Int(ProfileResolution, ProfileResolution),
            new Vector2(ProfileSize, ProfileSize));
        Vector2[] seed = TaylorGreenSeed(desc, Amplitude);
        StringBuilder report = new StringBuilder();
        AppendProfileHeader(report, "KE");

        float ke0 = RunKeFrame8(seed, 0f, report);
        float ke1 = RunKeFrame8(seed, 1f, report);
        float ratio = ke1 / Mathf.Max(ke0, 1e-12f);
        report.Append("KE_ε0=");
        report.Append(ke0.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" KE_ε1=");
        report.Append(ke1.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" ratio=");
        report.AppendLine(ratio.ToString("G9", CultureInfo.InvariantCulture));

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
        Assert.LessOrEqual(ratio, 10f, $"3.7 KE_ε1/KE_ε0={ratio:G9} > 10");
    }

    private static void AssertIdentityEpsilonZero(GraphicsFormat format)
    {
        using (FieldTestHarness harness = CreateIdentityHarness(format))
        {
            Vector2[] seed = NoiseSeed(IdentityResolution);
            harness.SeedVelocity(Velocity, seed);
            Vector2[] afterSeed = harness.ReadVelocity(Velocity);

            VorticityConfinementPass pass = CreateVc(harness, 0f);
            harness.RunPass(pass, DeltaTime);
            Vector2[] after = harness.ReadVelocity(Velocity);
            AssertFiniteVelocity(after, "identity ε=0 " + format);
            AssertBitwiseEqual(afterSeed, after, "3.1 identity ε=0 " + format);
        }
    }

    private static void RunDivergenceChain(
        Vector2[] seed,
        float? epsilonVc,
        StringBuilder report,
        out Diag frame1,
        out Diag frame8)
    {
        string variant = epsilonVc.HasValue
            ? "eps=" + epsilonVc.Value.ToString("G9", CultureInfo.InvariantCulture)
            : "none";
        using (FieldTestHarness harness = CreateProfileChainHarness())
        using (HarrisChain chain = HarrisChain.Create(harness, epsilonVc))
        {
            harness.SeedVelocity(Velocity, seed);
            DivergenceFieldPass diag = CreateDiagnosticDivergence(harness);
            frame1 = default;
            frame8 = default;
            for (int frame = 1; frame <= FrameCount; frame++)
            {
                chain.RunFrame(harness);
                Diag after = Measure(harness, diag, frame, variant, report);
                if (frame == 1)
                {
                    frame1 = after;
                }

                if (frame == FrameCount)
                {
                    frame8 = after;
                }
            }
        }
    }

    private static void LogNyquistChain(Vector2[] seed, float epsilonVc, StringBuilder report)
    {
        using (FieldTestHarness harness = CreateProfileChainHarness())
        using (HarrisChain chain = HarrisChain.Create(harness, epsilonVc))
        {
            harness.SeedVelocity(Velocity, seed);
            DivergenceFieldPass diag = CreateDiagnosticDivergence(harness);
            LogNyquist(harness, diag, epsilonVc, "seed", report);
            for (int frame = 1; frame <= FrameCount; frame++)
            {
                chain.RunFrame(harness);
            }

            LogNyquist(harness, diag, epsilonVc, "afterChain", report);
        }
    }

    private static float RunKeFrame8(Vector2[] seed, float epsilonVc, StringBuilder report)
    {
        using (FieldTestHarness harness = CreateProfileChainHarness())
        using (HarrisChain chain = HarrisChain.Create(harness, epsilonVc))
        {
            harness.SeedVelocity(Velocity, seed);
            for (int frame = 1; frame <= FrameCount; frame++)
            {
                chain.RunFrame(harness);
            }

            Vector2[] velocity = harness.ReadVelocity(Velocity);
            AssertFiniteVelocity(velocity, "KE ε=" + epsilonVc.ToString("G9", CultureInfo.InvariantCulture));
            float ke = InteriorKineticEnergy(velocity, ProfileResolution);
            report.Append("epsilonVc=");
            report.Append(epsilonVc.ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" KE_interior_frame8=");
            report.AppendLine(ke.ToString("G9", CultureInfo.InvariantCulture));
            return ke;
        }
    }

    private static void LogNyquist(
        FieldTestHarness harness,
        DivergenceFieldPass diag,
        float epsilonVc,
        string point,
        StringBuilder report)
    {
        Vector2[] velocity = harness.ReadVelocity(Velocity);
        AssertFiniteVelocity(velocity, point);
        report.Append("u.x, ");
        report.Append(epsilonVc.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(", ");
        report.Append(point);
        report.Append(", ");
        report.AppendLine(CheckerboardEnergyX(velocity).ToString("G9", CultureInfo.InvariantCulture));

        harness.RunPass(diag, DeltaTime);
        float[] d = harness.ReadScalar(FluidDDiag);
        AssertFiniteScalar(d, "fluidD_diag " + point);
        report.Append("D, ");
        report.Append(epsilonVc.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(", ");
        report.Append(point);
        report.Append(", ");
        report.AppendLine(CheckerboardEnergy(d).ToString("G9", CultureInfo.InvariantCulture));
    }

    private static Diag Measure(
        FieldTestHarness harness,
        DivergenceFieldPass diag,
        int frame,
        string variant,
        StringBuilder report)
    {
        harness.RunPass(diag, DeltaTime);
        float[] d = harness.ReadScalar(FluidDDiag);
        Vector2[] velocity = harness.ReadVelocity(Velocity);
        AssertFiniteScalar(d, "fluidD_diag " + variant);
        AssertFiniteVelocity(velocity, variant);

        float interior = MaxAbsInterior(d, ProfileResolution);
        float border = MaxAbsBorder(d, ProfileResolution);
        report.Append(frame.ToString(CultureInfo.InvariantCulture));
        report.Append(", ");
        report.Append(variant);
        report.Append(", afterChain, ");
        report.Append(interior.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(", ");
        report.AppendLine(border.ToString("G9", CultureInfo.InvariantCulture));
        return new Diag(frame, interior, border);
    }

    private static void AppendProfileHeader(StringBuilder report, string tag)
    {
        report.Append("tag=");
        report.Append(tag);
        report.Append(" h=");
        report.Append(TexelSize.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" A=");
        report.Append(Amplitude.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" dt=");
        report.Append(DeltaTime.ToString("G9", CultureInfo.InvariantCulture));
        report.Append(" lambdaTexels=");
        report.Append(LambdaTexels.ToString(CultureInfo.InvariantCulture));
        report.Append(" periods=");
        report.Append(Periods.ToString(CultureInfo.InvariantCulture));
        report.Append(" texelCFL=");
        report.Append((Amplitude * DeltaTime / TexelSize).ToString("G9", CultureInfo.InvariantCulture));
        report.AppendLine(" formats R16G16/R32/R16 res=128 size=32 Jacobi=40");
    }

    private static void AssertCloseR16(float a, float b, string label)
    {
        float rel = FieldTestHarness.RelativeTolerance(GraphicsFormat.R16G16_SFloat);
        float floor = rel;
        float absDiff = Mathf.Abs(a - b);
        float tol = Mathf.Max(rel * Mathf.Max(Mathf.Abs(a), Mathf.Abs(b)), floor);
        Assert.LessOrEqual(absDiff, tol, $"{label} |{a:G9}-{b:G9}|={absDiff:G9} tol={tol:G9}");
    }

    private static VorticityConfinementPass CreateVc(FieldTestHarness harness, float epsilonVc)
    {
        VorticityConfinementPass pass = new VorticityConfinementPass { EpsilonVc = epsilonVc };
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

    private static FieldTestHarness CreateIdentityHarness(GraphicsFormat format)
    {
        FieldDescriptor velocity = VelocityDescriptor(
            format,
            new Vector2Int(IdentityResolution, IdentityResolution),
            new Vector2(IdentitySize, IdentitySize));
        return new FieldTestHarness(new[] { velocity }, FluidCompute);
    }

    private static FieldTestHarness CreateProfileVelocityHarness()
    {
        FieldDescriptor velocity = VelocityDescriptor(
            GraphicsFormat.R16G16_SFloat,
            new Vector2Int(ProfileResolution, ProfileResolution),
            new Vector2(ProfileSize, ProfileSize));
        Assert.AreEqual(GraphicsFormat.R16G16_SFloat, velocity.Format);
        return new FieldTestHarness(new[] { velocity }, FluidCompute);
    }

    private static FieldTestHarness CreateProfileChainHarness()
    {
        Vector2Int res = new Vector2Int(ProfileResolution, ProfileResolution);
        Vector2 size = new Vector2(ProfileSize, ProfileSize);
        FieldDescriptor velocity = VelocityDescriptor(GraphicsFormat.R16G16_SFloat, res, size);
        FieldDescriptor fluidD = ScalarDescriptor(FluidD, GraphicsFormat.R32_SFloat, res, size);
        FieldDescriptor fluidPhi = ScalarDescriptor(FluidPhi, GraphicsFormat.R32_SFloat, res, size);
        FieldDescriptor fluidDDiag = ScalarDescriptor(FluidDDiag, GraphicsFormat.R32_SFloat, res, size);
        FieldDescriptor dye = ScalarDescriptor(Dye, GraphicsFormat.R16_SFloat, res, size);
        return new FieldTestHarness(
            new[] { velocity, fluidD, fluidPhi, fluidDDiag, dye },
            FluidCompute,
            FieldCompute);
    }

    private static FieldDescriptor VelocityDescriptor(
        GraphicsFormat format, Vector2Int resolution, Vector2 size)
    {
        return FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, format, resolution, size, Color.clear);
    }

    private static FieldDescriptor ScalarDescriptor(
        string name, GraphicsFormat format, Vector2Int res, Vector2 size)
    {
        return FieldTestHarness.Descriptor(
            name, FieldSemantic.Scalar, format, res, size, Color.clear);
    }

    private static Vector2[] FillVelocity(int resolution, Vector2 value)
    {
        Vector2[] values = new Vector2[resolution * resolution];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = value;
        }

        return values;
    }

    private static Vector2[] NoiseSeed(int resolution)
    {
        Vector2[] values = new Vector2[resolution * resolution];
        for (int y = 0; y < resolution; y++)
        {
            for (int x = 0; x < resolution; x++)
            {
                float fx = Hash(x * 0.137f + y * 0.731f);
                float fy = Hash(x * 0.419f + y * 0.283f + 17.2f);
                values[y * resolution + x] = new Vector2(fx * 2f - 1f, fy * 2f - 1f);
            }
        }

        return values;
    }

    private static float Hash(float v)
    {
        float s = Mathf.Sin(v * 12.9898f) * 43758.5453f;
        return s - Mathf.Floor(s);
    }

    private static Vector2[] TaylorGreenSeed(FieldDescriptor velocityDesc, float amplitude)
    {
        Vector2[] values = new Vector2[ProfileResolution * ProfileResolution];
        float kappa = 2f * Mathf.PI / (LambdaTexels * TexelSize);
        for (int y = 0; y < ProfileResolution; y++)
        {
            for (int x = 0; x < ProfileResolution; x++)
            {
                Vector2 plane = PlanePosition(velocityDesc, x, y);
                values[y * ProfileResolution + x] = new Vector2(
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
        float[] values = new float[ProfileResolution * ProfileResolution];
        for (int y = 0; y < ProfileResolution; y++)
        {
            for (int x = 0; x < ProfileResolution; x++)
            {
                values[y * ProfileResolution + x] = CheckerSign(x, y) * amplitude;
            }
        }

        return values;
    }

    private static Vector2[] CheckerboardVelocityX(float amplitude)
    {
        Vector2[] values = new Vector2[ProfileResolution * ProfileResolution];
        for (int y = 0; y < ProfileResolution; y++)
        {
            for (int x = 0; x < ProfileResolution; x++)
            {
                values[y * ProfileResolution + x] = new Vector2(CheckerSign(x, y) * amplitude, 0f);
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
        for (int y = 1; y < ProfileResolution - 1; y++)
        {
            for (int x = 1; x < ProfileResolution - 1; x++)
            {
                sum += values[y * ProfileResolution + x] * CheckerSign(x, y);
                count++;
            }
        }

        return Mathf.Abs((float)(sum / count));
    }

    private static float CheckerboardEnergyX(Vector2[] values)
    {
        double sum = 0d;
        int count = 0;
        for (int y = 1; y < ProfileResolution - 1; y++)
        {
            for (int x = 1; x < ProfileResolution - 1; x++)
            {
                sum += values[y * ProfileResolution + x].x * CheckerSign(x, y);
                count++;
            }
        }

        return Mathf.Abs((float)(sum / count));
    }

    private static float MaxAbsInterior(float[] values, int resolution)
    {
        float maxAbs = 0f;
        for (int y = 1; y < resolution - 1; y++)
        {
            for (int x = 1; x < resolution - 1; x++)
            {
                float abs = Mathf.Abs(values[y * resolution + x]);
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                }
            }
        }

        return maxAbs;
    }

    private static float MaxAbsBorder(float[] values, int resolution)
    {
        float maxAbs = 0f;
        int last = resolution - 1;
        for (int y = 0; y < resolution; y++)
        {
            for (int x = 0; x < resolution; x++)
            {
                if (x != 0 && x != last && y != 0 && y != last)
                {
                    continue;
                }

                float abs = Mathf.Abs(values[y * resolution + x]);
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                }
            }
        }

        return maxAbs;
    }

    private static float MaxAbsDeltaInterior(Vector2[] before, Vector2[] after, int resolution)
    {
        float maxAbs = 0f;
        for (int y = 1; y < resolution - 1; y++)
        {
            for (int x = 1; x < resolution - 1; x++)
            {
                int i = y * resolution + x;
                float dx = after[i].x - before[i].x;
                float dy = after[i].y - before[i].y;
                float mag = Mathf.Sqrt(dx * dx + dy * dy);
                if (mag > maxAbs)
                {
                    maxAbs = mag;
                }
            }
        }

        return maxAbs;
    }

    private static float InteriorKineticEnergy(Vector2[] velocity, int resolution)
    {
        double sum = 0d;
        for (int y = 1; y < resolution - 1; y++)
        {
            for (int x = 1; x < resolution - 1; x++)
            {
                Vector2 u = velocity[y * resolution + x];
                sum += u.x * u.x + u.y * u.y;
            }
        }

        return (float)sum;
    }

    private static void AssertBitwiseEqual(Vector2[] expected, Vector2[] obtained, string label)
    {
        Assert.AreEqual(expected.Length, obtained.Length, label);
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

    private static void AssertInteriorBitwiseEqual(
        Vector2[] expected, Vector2[] obtained, int resolution, string label)
    {
        for (int y = 1; y < resolution - 1; y++)
        {
            for (int x = 1; x < resolution - 1; x++)
            {
                int i = y * resolution + x;
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(expected[i].x),
                    BitConverter.SingleToInt32Bits(obtained[i].x),
                    $"{label} ({x},{y}).x expected={expected[i].x:G9} obtained={obtained[i].x:G9}");
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(expected[i].y),
                    BitConverter.SingleToInt32Bits(obtained[i].y),
                    $"{label} ({x},{y}).y expected={expected[i].y:G9} obtained={obtained[i].y:G9}");
            }
        }
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

    private sealed class HarrisChain : IDisposable
    {
        private readonly ZeroMeanScalarPass zeroMean;
        private readonly VorticityConfinementPass vorticity;

        private HarrisChain(
            AdvectVelocityFieldPass advectVelocity,
            VorticityConfinementPass vorticity,
            DivergenceFieldPass divergence,
            ZeroMeanScalarPass zeroMean,
            JacobiPhiPass jacobi,
            SubtractPhiGradientPass subtract,
            SolidWallVelocityPass wall,
            AdvectScalarPass advectDye)
        {
            AdvectVelocity = advectVelocity;
            this.vorticity = vorticity;
            Divergence = divergence;
            this.zeroMean = zeroMean;
            Jacobi = jacobi;
            Subtract = subtract;
            Wall = wall;
            AdvectDye = advectDye;
        }

        public AdvectVelocityFieldPass AdvectVelocity { get; }
        public DivergenceFieldPass Divergence { get; }
        public JacobiPhiPass Jacobi { get; }
        public SubtractPhiGradientPass Subtract { get; }
        public SolidWallVelocityPass Wall { get; }
        public AdvectScalarPass AdvectDye { get; }

        public static HarrisChain Create(FieldTestHarness harness, float? epsilonVc)
        {
            AdvectVelocityFieldPass advectVelocity = new AdvectVelocityFieldPass
            {
                FieldName = Velocity,
                DissipationRate = 0f,
            };
            VorticityConfinementPass vorticity = null;
            if (epsilonVc.HasValue)
            {
                vorticity = new VorticityConfinementPass { EpsilonVc = epsilonVc.Value };
            }

            DivergenceFieldPass divergence = new DivergenceFieldPass();
            ZeroMeanScalarPass zeroMean = new ZeroMeanScalarPass();
            JacobiPhiPass jacobi = new JacobiPhiPass { Iterations = 40 };
            SubtractPhiGradientPass subtract = new SubtractPhiGradientPass();
            SolidWallVelocityPass wall = new SolidWallVelocityPass();
            AdvectScalarPass advectDye = new AdvectScalarPass
            {
                ScalarField = Dye,
                VelocityField = Velocity,
                DissipationRate = 0f,
            };

            advectVelocity.Initialize(harness.Context);
            if (vorticity != null)
            {
                vorticity.Initialize(harness.Context);
            }

            divergence.Initialize(harness.Context);
            zeroMean.Initialize(harness.Context);
            jacobi.Initialize(harness.Context);
            subtract.Initialize(harness.Context);
            wall.Initialize(harness.Context);
            advectDye.Initialize(harness.Context);
            Assert.AreEqual(40, jacobi.RepeatCount);

            return new HarrisChain(
                advectVelocity, vorticity, divergence, zeroMean, jacobi, subtract, wall, advectDye);
        }

        public void RunFrame(FieldTestHarness harness)
        {
            harness.RunPass(AdvectVelocity, DeltaTime);
            if (vorticity != null)
            {
                harness.RunPass(vorticity, DeltaTime);
            }

            harness.RunPass(Divergence, DeltaTime);
            harness.RunPass(zeroMean, DeltaTime);
            harness.RunPass(Jacobi, DeltaTime);
            harness.RunPass(Subtract, DeltaTime);
            harness.RunPass(Wall, DeltaTime);
            harness.RunPass(AdvectDye, DeltaTime);
        }

        public void Dispose()
        {
            zeroMean.Dispose();
        }
    }
}
