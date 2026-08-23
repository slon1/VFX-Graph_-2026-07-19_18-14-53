using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
[Category("GPU")]
public class HarnessDiffuseTests
{
    private const string Field = "density";
    private const string DiffuseCompute = "Assets/Shaders/GPU/Passes/DiffusePasses.compute";
    private const int Width = 32;
    private const int Height = 32;
    private const int CenterX = 16;
    private const int CenterY = 16;

    [Test]
    public void Diffuse_DeltaSeed_ConservesSum_ObeysMaximumPrinciple()
    {
        const float rate = 0.2f;
        const float dt = 1f;
        const int iterations = 10;

        using (FieldTestHarness harness = CreateHarness())
        {
            float[] seed = DeltaSeed();
            harness.SeedScalar(Field, seed);

            DiffuseFieldPass pass = CreatePass(harness, rate);
            harness.RunPass(pass, dt, iterations);

            float[] obtained = harness.ReadScalar(Field);
            float sumBefore = Sum(seed);
            float sumAfter = Sum(obtained);
            float minBefore = Min(seed);
            float maxBefore = Max(seed);
            float minAfter = Min(obtained);
            float maxAfter = Max(obtained);

            TestContext.WriteLine(
                $"sum before={sumBefore:G9} after={sumAfter:G9} Δ={sumAfter - sumBefore:G9}; " +
                $"min {minBefore:G9}→{minAfter:G9} max {maxBefore:G9}→{maxAfter:G9}");
            // Peak 0.0385 vs continuum 1/(4π D n) ≈ 0.0398 (D=0.2, n=10) is independent
            // confirmation the kernel is doing diffusion, not merely matching a copy of
            // its own discrete stencil.

            GraphicsFormat format = GraphicsFormat.R32_SFloat;
            float rel = FieldTestHarness.RelativeTolerance(format);
            Assert.That(
                Mathf.Abs(sumAfter - sumBefore),
                Is.LessThanOrEqualTo(Mathf.Max(rel * Mathf.Max(Mathf.Abs(sumBefore), Mathf.Abs(sumAfter)), rel)),
                $"Neumann 5-point must conserve Σ: before={sumBefore:G9} after={sumAfter:G9}");
            Assert.That(maxAfter, Is.LessThanOrEqualTo(maxBefore + rel), "maximum principle: max must not grow");
            Assert.That(minAfter, Is.GreaterThanOrEqualTo(minBefore - rel), "maximum principle: min must not fall");
        }
    }

    [Test]
    public void ExplicitDiffuse_AboveCflBound_ViolatesMaximumPrinciple()
    {
        const float rate = 0.5f;
        const float dt = 1f;

        using (FieldTestHarness harness = CreateHarness())
        {
            float[] seed = DeltaSeed();
            harness.SeedScalar(Field, seed);

            DiffuseFieldPass pass = CreatePass(harness, rate);
            harness.RunPass(pass, dt, repeat: 1);

            float[] obtained = harness.ReadScalar(Field);
            float minBefore = Min(seed);
            float minAfter = Min(obtained);
            TestContext.WriteLine(
                $"CFL r=0.5 after 1 iter: min {minBefore:G9}→{minAfter:G9} center={obtained[CenterY * Width + CenterX]:G9}");

            Assert.That(
                minAfter,
                Is.LessThan(minBefore),
                "explicit 5-point at r=0.5: center becomes −c on the first iteration (ADR-006).");
        }
    }

    [Test]
    public void Diffuse_MatchesCpuFivePointOracle()
    {
        const float rate = 0.2f;
        const float dt = 1f;
        const int iterations = 10;

        using (FieldTestHarness harness = CreateHarness())
        {
            float[] seed = DeltaSeed();
            harness.SeedScalar(Field, seed);

            DiffuseFieldPass pass = CreatePass(harness, rate);
            harness.RunPass(pass, dt, iterations);

            float[] obtained = harness.ReadScalar(Field);
            float[] expected = DiffuseCpu(seed, Width, Height, rate, dt, iterations);
            float maxAbs = MaxAbsDelta(obtained, expected);
            TestContext.WriteLine($"CPU oracle max|Δ|={maxAbs:G9} (floor 1e-5; larger is a kernel bug)");

            FieldTestHarness.AssertApproximately(
                obtained,
                expected,
                GraphicsFormat.R32_SFloat,
                "GPU DiffuseField vs CPU 5-point (float)",
                absoluteFloor: 1e-5f);
        }
    }

    [Test]
    public void Diffuse_OneStepDelta_IsTexelLaplacian_WhenHIsNotOne()
    {
        // ADR-016 DoD 6: CPU oracle copies the same stencil, so Size≠Resolution
        // is the only check that a /h² "fix" cannot silently match. Size=10,
        // 32² → h=0.3125, 1/h²=10.24; world centre would be ≈−39.96, not −3.
        const int res = 32;
        const float worldSize = 10f;
        const float rate = 1f;
        const float dt = 1f;
        const int cx = 16;
        const int cy = 16;
        float h = worldSize / res;
        Assume.That(Mathf.Abs(h - 1f), Is.GreaterThan(1e-4f), "h must not be 1");

        FieldDescriptor desc = FieldTestHarness.Descriptor(
            Field,
            FieldSemantic.Scalar,
            GraphicsFormat.R32_SFloat,
            new Vector2Int(res, res),
            new Vector2(worldSize, worldSize),
            Color.clear);

        using (FieldTestHarness harness = new FieldTestHarness(new[] { desc }, DiffuseCompute))
        {
            float[] seed = new float[res * res];
            seed[cy * res + cx] = 1f;
            harness.SeedScalar(Field, seed);

            DiffuseFieldPass pass = CreatePass(harness, rate);
            harness.RunPass(pass, dt, repeat: 1);

            float[] obtained = harness.ReadScalar(Field);
            float center = obtained[cy * res + cx];
            float north = obtained[(cy + 1) * res + cx];
            float south = obtained[(cy - 1) * res + cx];
            float east = obtained[cy * res + (cx + 1)];
            float west = obtained[cy * res + (cx - 1)];

            TestContext.WriteLine(
                $"h={h:G9} 1/h²={1f / (h * h):G9} centre={center:G9} " +
                $"N={north:G9} S={south:G9} E={east:G9} W={west:G9} " +
                $"(texel: −3 / +1; world centre would be {1f + (1f / (h * h)) * -4f:G9})");

            const float floor = 1e-5f;
            Assert.That(center, Is.EqualTo(-3f).Within(floor), "texel Laplacian centre");
            Assert.That(north, Is.EqualTo(1f).Within(floor), "north neighbor");
            Assert.That(south, Is.EqualTo(1f).Within(floor), "south neighbor");
            Assert.That(east, Is.EqualTo(1f).Within(floor), "east neighbor");
            Assert.That(west, Is.EqualTo(1f).Within(floor), "west neighbor");
        }
    }

    [Test]
    public void Diffuse_SymmetricSeed_StaysSymmetric()
    {
        // Offset/axis bugs that still telescope (Σ conserved), e.g. east neighbor at +2.
        // A sign typo (n+s+e−w−4c) is already caught by mass conservation.
        // CPU-oracle and symmetry reporting the same ~3.73e-9 is expected: the CPU
        // stencil is exactly symmetric, so GPU asymmetry *is* its deviation from the
        // oracle at the mirrored cell.
        const float rate = 0.2f;
        const float dt = 1f;
        const int iterations = 10;

        using (FieldTestHarness harness = CreateHarness())
        {
            float[] seed = DeltaSeed();
            harness.SeedScalar(Field, seed);

            DiffuseFieldPass pass = CreatePass(harness, rate);
            harness.RunPass(pass, dt, iterations);

            float[] obtained = harness.ReadScalar(Field);
            float maxAsym = 0f;
            int radius = Mathf.Min(CenterX, Width - 1 - CenterX, CenterY, Height - 1 - CenterY);
            for (int k = 1; k <= radius; k++)
            {
                maxAsym = Mathf.Max(maxAsym, Mathf.Abs(
                    obtained[CenterY * Width + (CenterX + k)] - obtained[CenterY * Width + (CenterX - k)]));
                maxAsym = Mathf.Max(maxAsym, Mathf.Abs(
                    obtained[(CenterY + k) * Width + CenterX] - obtained[(CenterY - k) * Width + CenterX]));
            }

            TestContext.WriteLine($"symmetry max|Δ|={maxAsym:G9}");
            Assert.That(
                maxAsym,
                Is.LessThanOrEqualTo(FieldTestHarness.RelativeTolerance(GraphicsFormat.R32_SFloat)),
                "symmetric seed must stay symmetric about the seeded texel");
        }
    }

    private static FieldTestHarness CreateHarness()
    {
        FieldDescriptor desc = FieldTestHarness.Descriptor(
            Field,
            FieldSemantic.Scalar,
            GraphicsFormat.R32_SFloat,
            new Vector2Int(Width, Height),
            new Vector2(Width, Height),
            Color.clear);
        return new FieldTestHarness(new[] { desc }, DiffuseCompute);
    }

    private static DiffuseFieldPass CreatePass(FieldTestHarness harness, float rate)
    {
        DiffuseFieldPass pass = new DiffuseFieldPass
        {
            FieldName = Field,
            DiffusionRate = rate,
        };
        pass.Initialize(harness.Context);
        return pass;
    }

    private static float[] DeltaSeed()
    {
        float[] seed = new float[Width * Height];
        seed[CenterY * Width + CenterX] = 1f;
        return seed;
    }

    private static float[] DiffuseCpu(float[] src, int w, int h, float rate, float dt, int iterations)
    {
        float[] a = (float[])src.Clone();
        float[] b = new float[a.Length];
        float r = rate * dt;
        for (int it = 0; it < iterations; it++)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float c = a[y * w + x];
                    float n = a[Clamp(y + 1, h) * w + x];
                    float s = a[Clamp(y - 1, h) * w + x];
                    float e = a[y * w + Clamp(x + 1, w)];
                    float west = a[y * w + Clamp(x - 1, w)];
                    b[y * w + x] = c + r * (n + s + e + west - 4f * c);
                }
            }

            float[] tmp = a;
            a = b;
            b = tmp;
        }

        return a;
    }

    private static int Clamp(int q, int size)
    {
        if (q < 0)
        {
            return 0;
        }

        if (q > size - 1)
        {
            return size - 1;
        }

        return q;
    }

    private static float Sum(float[] values)
    {
        float s = 0f;
        for (int i = 0; i < values.Length; i++)
        {
            s += values[i];
        }

        return s;
    }

    private static float Min(float[] values)
    {
        float m = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] < m)
            {
                m = values[i];
            }
        }

        return m;
    }

    private static float Max(float[] values)
    {
        float m = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > m)
            {
                m = values[i];
            }
        }

        return m;
    }

    private static float MaxAbsDelta(float[] obtained, float[] expected)
    {
        float m = 0f;
        for (int i = 0; i < expected.Length; i++)
        {
            m = Mathf.Max(m, Mathf.Abs(obtained[i] - expected[i]));
        }

        return m;
    }
}
