using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
[Category("GPU")]
public class Fluid2DDyeInjectLiveTests
{
    private const string Velocity = "velocity";
    private const string FluidD = "fluidD";
    private const string FluidPhi = "fluidPhi";
    private const string Dye = "dye";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const string FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute";
    private const string GrayScottCompute = "Assets/Shaders/GPU/Passes/GrayScottPasses.compute";
    private const string TouchCompute = "Assets/Shaders/GPU/Passes/TouchGrayScottPasses.compute";
    private const float SizeWorld = 32f;
    private const float Dt = 1f / 60f;
    private const float Duration = 0.5f;
    private const float TotalTime = 3f;
    private const float TouchRadius = 1f;
    private const float TouchStrength = 10f;
    private const float MaxValue = 1f;
    private const int TimedFrames = 8;

    [Test]
    [Timeout(900000)]
    public void Stroke_SafetyOnS_FilamentOnN()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine("F3.1 live stroke dt=1/60 duration=0.5 total=3 radius=1 strength=10");
        RunGeometry(512, report);
        RunGeometry(128, report);
        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
    }

    [Test]
    public void StamChain_DyeInject_RecordsCpuDriverMsPerFrame()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine(
            "profile=Fluid2D_DyeInject velocity=128 dye=512 size=32 Jacobi=40 " +
            "timer=cpu_driver_not_gpu");

        using (FieldTestHarness harness = CreateHarness(512))
        using (DyeInjectChain chain = DyeInjectChain.Create(harness, 1f))
        {
            harness.SetTouches(new[] { TouchAt(Vector3.zero, Vector3.zero) });
            chain.RunFrame(harness, Dt);

            Stopwatch watch = Stopwatch.StartNew();
            for (int frame = 0; frame < TimedFrames; frame++)
            {
                chain.RunFrame(harness, Dt);
            }

            watch.Stop();
            double msPerFrame = watch.Elapsed.TotalMilliseconds / TimedFrames;
            report.Append("elapsedMs/N=");
            report.Append(msPerFrame.ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" N=");
            report.Append(TimedFrames.ToString(CultureInfo.InvariantCulture));
            report.AppendLine(" timer=cpu_driver_not_gpu Editor velocity=128 dye=512");
            report.AppendLine("ref F3.0 elapsedMs/N=0.184");
        }

        string text = report.ToString();
        TestContext.WriteLine(text);
        UnityEngine.Debug.Log(text);
        Assert.Pass(text);
    }

    private static void RunGeometry(int dyeRes, StringBuilder report)
    {
        RunOne(dyeRes, seedValue: 1f, checkFilament: false, report);
        RunOne(dyeRes, seedValue: 0f, checkFilament: true, report);
    }

    private static void RunOne(int dyeRes, float seedValue, bool checkFilament, StringBuilder report)
    {
        int strokeFrames = Mathf.RoundToInt(Duration / Dt);
        int totalFrames = Mathf.RoundToInt(TotalTime / Dt);
        Vector2 size = new Vector2(SizeWorld, SizeWorld);
        Vector3 origin = Vector3.zero;
        Vector3 axisU = Vector3.right;
        Vector3 axisV = Vector3.forward;
        Vector3 start = ScriptedTouchStroke.UvToWorld(new Vector2(0.5f, 0.25f), origin, axisU, axisV, size);
        Vector3 end = ScriptedTouchStroke.UvToWorld(new Vector2(0.5f, 0.75f), origin, axisU, axisV, size);

        using (FieldTestHarness harness = CreateHarness(dyeRes))
        using (DyeInjectChain chain = DyeInjectChain.Create(harness, seedValue))
        {
            bool hasPrev = false;
            Vector3 prev = Vector3.zero;
            float[] dyeAtStroke = null;
            float[] dyeAtEnd = null;

            for (int frame = 0; frame < totalFrames; frame++)
            {
                float elapsed = frame * Dt;
                if (elapsed < Duration)
                {
                    ScriptedTouchSample sample = ScriptedTouchStroke.Evaluate(elapsed, Duration, start, end);
                    Vector3 pos = sample.Position;
                    Vector3 delta = hasPrev ? pos - prev : Vector3.zero;
                    prev = pos;
                    hasPrev = true;
                    harness.SetTouches(new[] { TouchAt(pos, delta) });
                }
                else
                {
                    harness.SetTouches(Array.Empty<TouchForce>());
                }

                chain.RunFrame(harness, Dt);
                float[] dye = harness.ReadScalar(Dye);
                Vector2[] velocity = harness.ReadVelocity(Velocity);
                AssertField(dye, velocity, $"dyeRes={dyeRes} seed={seedValue} frame={frame}");

                if (frame == strokeFrames - 1)
                {
                    dyeAtStroke = dye;
                }

                if (frame == totalFrames - 1)
                {
                    dyeAtEnd = dye;
                }
            }

            Assert.AreEqual(30, strokeFrames, "stroke frame count");
            Assert.IsNotNull(dyeAtStroke);
            Assert.IsNotNull(dyeAtEnd);

            string tag = $"dye{dyeRes} seed={seedValue}";
            if (checkFilament)
            {
                float[] pathY = { 0.25f, 0.35f, 0.5f, 0.65f, 0.75f };
                for (int i = 0; i < pathY.Length; i++)
                {
                    float window = MaxInWindow(dyeAtStroke, dyeRes, 0.5f, pathY[i]);
                    Assert.Greater(
                        window,
                        0.05f,
                        $"{tag} UV.y={pathY[i]} maxInWindow={window:G9}");
                }

                float peak35 = AxisSample(dyeAtStroke, dyeRes, 0.35f);
                float peak65 = AxisSample(dyeAtStroke, dyeRes, 0.65f);
                report.Append(tag);
                report.Append(" peakY0.35=");
                report.Append(peak35.ToString("G9", CultureInfo.InvariantCulture));
                report.Append(" peakY0.65=");
                report.Append(peak65.ToString("G9", CultureInfo.InvariantCulture));
                report.Append(" expect≈0.71");
                report.AppendLine();
            }

            float fwhmStroke = FwhmAcross(dyeAtStroke, dyeRes);
            float fwhmEnd = FwhmAcross(dyeAtEnd, dyeRes);
            report.Append(tag);
            report.Append(" FWHM@0.5s=");
            report.Append(fwhmStroke.ToString("G9", CultureInfo.InvariantCulture));
            report.Append(" FWHM@3s=");
            report.Append(fwhmEnd.ToString("G9", CultureInfo.InvariantCulture));
            report.AppendLine(" worldUnits centerToCenter y=0.5");
        }
    }

    private static void AssertField(float[] dye, Vector2[] velocity, string label)
    {
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < dye.Length; i++)
        {
            if (float.IsNaN(dye[i]) || float.IsInfinity(dye[i]))
            {
                Assert.Fail($"{label} dye[{i}]={dye[i]}");
            }

            if (dye[i] < min)
            {
                min = dye[i];
            }

            if (dye[i] > max)
            {
                max = dye[i];
            }
        }

        Assert.GreaterOrEqual(min, 0f, $"{label} min={min:G9}");
        Assert.LessOrEqual(max, MaxValue + 1e-3f, $"{label} max={max:G9}");

        for (int i = 0; i < velocity.Length; i++)
        {
            if (float.IsNaN(velocity[i].x) || float.IsInfinity(velocity[i].x) ||
                float.IsNaN(velocity[i].y) || float.IsInfinity(velocity[i].y))
            {
                Assert.Fail($"{label} velocity[{i}]=({velocity[i].x},{velocity[i].y})");
            }
        }
    }

    private static float MaxInWindow(float[] dye, int res, float uvX, float uvY)
    {
        Vector3 point = World(uvX, uvY);
        float max = 0f;
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                Vector3 center = TexelCenter(x, y, res);
                if (Mathf.Abs(center.x - point.x) > TouchRadius || Mathf.Abs(center.z - point.z) > TouchRadius)
                {
                    continue;
                }

                float value = dye[y * res + x];
                if (value > max)
                {
                    max = value;
                }
            }
        }

        return max;
    }

    private static float AxisSample(float[] dye, int res, float uvY)
    {
        Vector3 point = World(0.5f, uvY);
        int best = 0;
        float bestDist = float.PositiveInfinity;
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float dist = Vector3.Distance(TexelCenter(x, y, res), point);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = y * res + x;
                }
            }
        }

        return dye[best];
    }

    private static float FwhmAcross(float[] dye, int res)
    {
        int row = 0;
        float best = float.PositiveInfinity;
        for (int y = 0; y < res; y++)
        {
            float dz = Mathf.Abs(TexelCenter(0, y, res).z);
            if (dz < best)
            {
                best = dz;
                row = y;
            }
        }

        int peakX = 0;
        float peak = 0f;
        for (int x = 0; x < res; x++)
        {
            float value = dye[row * res + x];
            if (value > peak)
            {
                peak = value;
                peakX = x;
            }
        }

        if (peak <= 0f)
        {
            return 0f;
        }

        float half = peak * 0.5f;
        int left = peakX;
        int right = peakX;
        for (int x = peakX; x >= 0; x--)
        {
            if (dye[row * res + x] < half)
            {
                break;
            }

            left = x;
        }

        for (int x = peakX; x < res; x++)
        {
            if (dye[row * res + x] < half)
            {
                break;
            }

            right = x;
        }

        return (right - left) * (SizeWorld / res);
    }

    private static Vector3 World(float uvX, float uvY)
    {
        return ScriptedTouchStroke.UvToWorld(
            new Vector2(uvX, uvY),
            Vector3.zero,
            Vector3.right,
            Vector3.forward,
            new Vector2(SizeWorld, SizeWorld));
    }

    private static Vector3 TexelCenter(int x, int y, int res)
    {
        float texel = SizeWorld / res;
        float origin = -SizeWorld * 0.5f;
        return new Vector3((x + 0.5f) * texel + origin, 0f, (y + 0.5f) * texel + origin);
    }

    private static TouchForce TouchAt(Vector3 pos, Vector3 delta)
    {
        return new TouchForce
        {
            Position = pos,
            Delta = delta,
            Radius = TouchRadius,
            Strength = TouchStrength,
        };
    }

    private static FieldTestHarness CreateHarness(int dyeRes)
    {
        Vector2 size = new Vector2(SizeWorld, SizeWorld);
        Vector2Int velRes = new Vector2Int(128, 128);
        Vector2Int dye = new Vector2Int(dyeRes, dyeRes);
        return new FieldTestHarness(
            new[]
            {
                FieldTestHarness.Descriptor(
                    Velocity, FieldSemantic.Velocity, GraphicsFormat.R16G16_SFloat, velRes, size, Color.clear),
                FieldTestHarness.Descriptor(
                    FluidD, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat, velRes, size, Color.clear),
                FieldTestHarness.Descriptor(
                    FluidPhi, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat, velRes, size, Color.clear),
                FieldTestHarness.Descriptor(
                    Dye, FieldSemantic.Scalar, GraphicsFormat.R16_SFloat, dye, size, Color.clear),
            },
            FluidCompute,
            FieldCompute,
            GrayScottCompute,
            TouchCompute);
    }

    private sealed class DyeInjectChain : IDisposable
    {
        private readonly ZeroMeanScalarPass zeroMean;

        private DyeInjectChain(
            TouchInjectVelocityFieldPass touch,
            SeedScalarDiskPass seed,
            TouchInjectScalarFieldPass inject,
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
            Inject = inject;
            Divergence = divergence;
            this.zeroMean = zeroMean;
            Jacobi = jacobi;
            Subtract = subtract;
            WallAfterProject = wallAfterProject;
            AdvectVelocity = advectVelocity;
            WallAfterAdvect = wallAfterAdvect;
            AdvectDye = advectDye;
        }

        private TouchInjectVelocityFieldPass Touch { get; }
        private SeedScalarDiskPass Seed { get; }
        private TouchInjectScalarFieldPass Inject { get; }
        private DivergenceFieldPass Divergence { get; }
        private JacobiPhiPass Jacobi { get; }
        private SubtractPhiGradientPass Subtract { get; }
        private SolidWallVelocityPass WallAfterProject { get; }
        private AdvectVelocityFieldPass AdvectVelocity { get; }
        private SolidWallVelocityPass WallAfterAdvect { get; }
        private AdvectScalarPass AdvectDye { get; }

        public static DyeInjectChain Create(FieldTestHarness harness, float seedValue)
        {
            TouchInjectVelocityFieldPass touch = new TouchInjectVelocityFieldPass();
            SeedScalarDiskPass seed = new SeedScalarDiskPass
            {
                FieldName = Dye,
                CenterUV = new Vector2(0.5f, 0.5f),
                RadiusUV = 0.04f,
                Value = seedValue,
            };
            TouchInjectScalarFieldPass inject = new TouchInjectScalarFieldPass
            {
                FieldName = Dye,
                InjectRate = 60f,
                MaxValue = MaxValue,
                RadiusScale = 1f,
            };
            DivergenceFieldPass divergence = new DivergenceFieldPass();
            ZeroMeanScalarPass zeroMean = new ZeroMeanScalarPass();
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

            touch.Initialize(harness.Context);
            seed.Initialize(harness.Context);
            inject.Initialize(harness.Context);
            divergence.Initialize(harness.Context);
            zeroMean.Initialize(harness.Context);
            jacobi.Initialize(harness.Context);
            subtract.Initialize(harness.Context);
            wallAfterProject.Initialize(harness.Context);
            advectVelocity.Initialize(harness.Context);
            wallAfterAdvect.Initialize(harness.Context);
            advectDye.Initialize(harness.Context);

            return new DyeInjectChain(
                touch, seed, inject, divergence, zeroMean, jacobi, subtract,
                wallAfterProject, advectVelocity, wallAfterAdvect, advectDye);
        }

        public void RunFrame(FieldTestHarness harness, float dt)
        {
            harness.RunPass(Touch, dt);
            harness.RunPass(Seed, dt);
            harness.RunPass(Inject, dt);
            harness.RunPass(Divergence, dt);
            harness.RunPass(zeroMean, dt);
            harness.RunPass(Jacobi, dt);
            harness.RunPass(Subtract, dt);
            harness.RunPass(WallAfterProject, dt);
            harness.RunPass(AdvectVelocity, dt);
            harness.RunPass(WallAfterAdvect, dt);
            harness.RunPass(AdvectDye, dt);
        }

        public void Dispose()
        {
            zeroMean.Dispose();
        }
    }
}
