using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
[Category("GPU")]
public class TouchInjectScalarKernelTests
{
    private const string Field = "dye";
    private const string Compute = "Assets/Shaders/GPU/Passes/TouchGrayScottPasses.compute";
    private const int Res = 64;
    private const float Size = 32f;
    private const float Dt = 0.05f;
    private const float InjectRate = 60f;
    private const float MaxValue = 1f;
    private const float Radius = 1f;
    private const float Tol = 1e-4f;

    [Test]
    public void G1_PointStamp_MatchesProfile()
    {
        Vector3 center = TexelCenter(32, 32, Res);
        float[] dye = Run(Res, center, Vector3.zero, Radius, 1f, InjectRate, MaxValue, Dt, null);
        AssertTexel(dye, 32, 32, Expected(0f), "center d=0");
        AssertTexel(dye, 33, 32, Expected(0.5f), "d=0.5");
        AssertTexel(dye, 34, 32, 0f, "d=1");
        Assert.AreEqual(0f, dye[Index(40, 32)], "d>R strictly 0");
    }

    [Test]
    public void G2_Monotone_AndCeiling()
    {
        float[] initial = new float[Res * Res];
        for (int y = 0; y < Res; y++)
        {
            for (int x = 0; x < Res; x++)
            {
                initial[Index(x, y)] = x < Res / 2 ? 0.9f : 0.2f;
            }
        }

        Vector3 boundary = new Vector3(0f, 0f, TexelCenter(32, 32, Res).z);
        float[] dye = Run(Res, boundary, Vector3.zero, Radius, 1f, InjectRate, 0.5f, Dt, initial);

        for (int i = 0; i < dye.Length; i++)
        {
            Assert.GreaterOrEqual(dye[i], initial[i], $"out<in [{i}] out={dye[i]} in={initial[i]}");
            int x = i % Res;
            if (x < Res / 2)
            {
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(initial[i]),
                    BitConverter.SingleToInt32Bits(dye[i]),
                    $"left bits [{i}] out={dye[i]} in={initial[i]}");
            }
            else
            {
                Assert.LessOrEqual(dye[i], 0.5f, $"right ceiling [{i}] out={dye[i]}");
            }
        }
    }

    [Test]
    public void G3_Capsule_PaintsSegmentCenters()
    {
        Vector3 start = TexelCenter(20, 32, Res);
        Vector3 mid = TexelCenter(23, 32, Res);
        Vector3 end = TexelCenter(26, 32, Res);
        Vector3 delta = end - start;
        Assert.AreEqual(3f * Radius, delta.magnitude, 1e-4f, $"|delta|={delta.magnitude}");

        float[] dye = Run(Res, end, delta, Radius, 1f, InjectRate, MaxValue, Dt, null);
        float full = Expected(0f);
        float half = Expected(0.5f);
        AssertTexel(dye, 20, 32, full, "segment start");
        AssertTexel(dye, 23, 32, full, "segment mid");
        AssertTexel(dye, 26, 32, full, "segment end");
        AssertTexel(dye, 23, 33, half, "perp 0.5R");

        const string pointStamp = "точечный штамп в середине дал бы 0";
        TestContext.WriteLine($"{pointStamp} (d=1.5 R=1)");
        Assert.AreEqual(full, dye[Index(20, 32)], Tol, pointStamp);
    }

    [Test]
    public void G4_StationaryBrush_ComposesInDt()
    {
        Vector3 center = TexelCenter(32, 32, Res);
        float[] once = Run(Res, center, Vector3.zero, Radius, 1f, InjectRate, MaxValue, Dt, null);

        float[] stepped = null;
        using (FieldTestHarness harness = Harness(Res))
        {
            TouchInjectScalarFieldPass pass = Pass(InjectRate, MaxValue, 1f);
            pass.Initialize(harness.Context);
            harness.SetTouches(new[] { Touch(center, Vector3.zero, Radius) });
            for (int i = 0; i < 4; i++)
            {
                harness.RunPass(pass, Dt / 4f);
            }

            stepped = harness.ReadScalar(Field);
        }

        AssertClose(stepped, once, 1e-5f, "G4");
    }

    [Test]
    public void G5_TwoTouches_DoubleRate_AndSeparatedSum()
    {
        Vector3 center = TexelCenter(32, 32, Res);
        TouchForce one = Touch(center, Vector3.zero, Radius);
        float[] doubled = Run(Res, center, Vector3.zero, Radius, 1f, InjectRate * 2f, MaxValue, Dt, null);
        float[] pair;
        using (FieldTestHarness harness = Harness(Res))
        {
            TouchInjectScalarFieldPass pass = Pass(InjectRate, MaxValue, 1f);
            pass.Initialize(harness.Context);
            harness.SetTouches(new[] { one, one });
            harness.RunPass(pass, Dt);
            pair = harness.ReadScalar(Field);
        }

        AssertClose(pair, doubled, 1e-5f, "G5a");

        Vector3 left = TexelCenter(24, 32, Res);
        Vector3 right = TexelCenter(40, 32, Res);
        float apart = Vector3.Distance(left, right);
        Assert.Greater(apart, 2f * Radius, $"separation {apart}");

        float[] both = RunPair(left, right);
        float[] onlyLeft = Run(Res, left, Vector3.zero, Radius, 1f, InjectRate, MaxValue, Dt, null);
        float[] onlyRight = Run(Res, right, Vector3.zero, Radius, 1f, InjectRate, MaxValue, Dt, null);
        float[] sum = new float[onlyLeft.Length];
        for (int i = 0; i < sum.Length; i++)
        {
            sum[i] = onlyLeft[i] + onlyRight[i];
        }

        AssertClose(both, sum, 1e-5f, "G5b");
    }

    [Test]
    public void G6_ZeroTouches_LeavesField()
    {
        float[] initial = Pattern();
        float[] dye;
        using (FieldTestHarness harness = Harness(Res))
        {
            harness.SeedScalar(Field, initial);
            TouchInjectScalarFieldPass pass = Pass(InjectRate, MaxValue, 1f);
            pass.Initialize(harness.Context);
            harness.SetTouches(Array.Empty<TouchForce>());
            harness.RunPass(pass, Dt);
            dye = harness.ReadScalar(Field);
        }

        AssertBits(dye, initial, "G6");
    }

    [Test]
    public void G7_ZeroRate_LeavesField()
    {
        float[] initial = Pattern();
        float[] dye = Run(Res, TexelCenter(32, 32, Res), Vector3.zero, Radius, 1f, 0f, MaxValue, Dt, initial);
        AssertBits(dye, initial, "G7");
    }

    [Test]
    public void G8_CrossRes_IntegralWithin3Percent()
    {
        Vector3 end = new Vector3(4f, 0f, 0f);
        Vector3 delta = new Vector3(8f, 0f, 0f);
        float integral128 = Integral(Run(128, end, delta, Radius, 1f, InjectRate, MaxValue, Dt, null), 128);
        float integral512 = Integral(Run(512, end, delta, Radius, 1f, InjectRate, MaxValue, Dt, null), 512);
        float scale = Mathf.Max(Mathf.Abs(integral128), Mathf.Abs(integral512));
        float rel = Mathf.Abs(integral128 - integral512) / scale;
        Assert.LessOrEqual(
            rel,
            0.03f,
            $"integral128={integral128:G9} integral512={integral512:G9} rel={rel:G9}");
    }

    [Test]
    public void G9_RadiusScale_ProfileOnFineGrid()
    {
        const int res = 256;
        const int origin = 128;
        Vector3 center = TexelCenter(origin, origin, res);
        float[] full = Run(res, center, Vector3.zero, Radius, 1f, InjectRate, MaxValue, Dt, null);
        float[] half = Run(res, center, Vector3.zero, Radius, 0.5f, InjectRate, MaxValue, Dt, null);

        for (int k = 0; k <= 9; k++)
        {
            float d = k * 0.125f;
            float gotFull = full[origin * res + (origin + k)];
            float gotHalf = half[origin * res + (origin + k)];
            float expectFull = Expected(d, 1f);
            float expectHalf = Expected(d, 0.5f);
            Assert.AreEqual(
                expectFull, gotFull, Tol,
                $"scale=1 k={k} d={d:G9} got={gotFull:G9} expected={expectFull:G9}");
            Assert.AreEqual(
                expectHalf, gotHalf, Tol,
                $"scale=0.5 k={k} d={d:G9} got={gotHalf:G9} expected={expectHalf:G9}");
        }

        const int kEdge = 6;
        float atEdgeFull = full[origin * res + (origin + kEdge)];
        float atEdgeHalf = half[origin * res + (origin + kEdge)];
        Assert.AreEqual(0f, atEdgeHalf, $"scale=0.5 d=0.75 got={atEdgeHalf:G9}");
        Assert.Greater(atEdgeFull, 0f, $"scale=1 d=0.75 got={atEdgeFull:G9}");
    }

    private static float[] Run(
        int res,
        Vector3 pos,
        Vector3 delta,
        float radius,
        float radiusScale,
        float injectRate,
        float maxValue,
        float dt,
        float[] initial)
    {
        using (FieldTestHarness harness = Harness(res))
        {
            if (initial != null)
            {
                harness.SeedScalar(Field, initial);
            }

            TouchInjectScalarFieldPass pass = Pass(injectRate, maxValue, radiusScale);
            pass.Initialize(harness.Context);
            harness.SetTouches(new[] { Touch(pos, delta, radius) });
            harness.RunPass(pass, dt);
            return harness.ReadScalar(Field);
        }
    }

    private static float[] RunPair(Vector3 left, Vector3 right)
    {
        using (FieldTestHarness harness = Harness(Res))
        {
            TouchInjectScalarFieldPass pass = Pass(InjectRate, MaxValue, 1f);
            pass.Initialize(harness.Context);
            harness.SetTouches(new[]
            {
                Touch(left, Vector3.zero, Radius),
                Touch(right, Vector3.zero, Radius),
            });
            harness.RunPass(pass, Dt);
            return harness.ReadScalar(Field);
        }
    }

    private static FieldTestHarness Harness(int res)
    {
        FieldDescriptor dye = FieldTestHarness.Descriptor(
            Field,
            FieldSemantic.Scalar,
            GraphicsFormat.R32_SFloat,
            new Vector2Int(res, res),
            new Vector2(Size, Size),
            Color.clear);
        return new FieldTestHarness(new[] { dye }, Compute);
    }

    private static TouchInjectScalarFieldPass Pass(float injectRate, float maxValue, float radiusScale)
    {
        return new TouchInjectScalarFieldPass
        {
            FieldName = Field,
            InjectRate = injectRate,
            MaxValue = maxValue,
            RadiusScale = radiusScale,
        };
    }

    private static TouchForce Touch(Vector3 pos, Vector3 delta, float radius)
    {
        return new TouchForce
        {
            Position = pos,
            Delta = delta,
            Radius = radius,
            Strength = 10f,
        };
    }

    private static float Expected(float distance, float radiusScale = 1f)
    {
        float reach = Radius * radiusScale;
        float f = distance < reach ? (1f - distance / reach) * (1f - distance / reach) : 0f;
        return MaxValue * (1f - Mathf.Exp(-InjectRate * Dt * f));
    }

    private static Vector3 TexelCenter(int x, int y, int res)
    {
        float texel = Size / res;
        float origin = -Size * 0.5f;
        return new Vector3((x + 0.5f) * texel + origin, 0f, (y + 0.5f) * texel + origin);
    }

    private static int Index(int x, int y)
    {
        return y * Res + x;
    }

    private static void AssertTexel(float[] dye, int x, int y, float expected, string label)
    {
        float got = dye[Index(x, y)];
        Assert.AreEqual(expected, got, Tol, $"{label} got={got:G9} expected={expected:G9}");
    }

    private static void AssertClose(float[] got, float[] expected, float tol, string label)
    {
        Assert.AreEqual(expected.Length, got.Length, label);
        for (int i = 0; i < got.Length; i++)
        {
            float delta = Mathf.Abs(got[i] - expected[i]);
            if (delta > tol)
            {
                Assert.Fail($"{label} [{i}] got={got[i]:G9} expected={expected[i]:G9} Δ={delta:G9} tol={tol:G9}");
            }
        }
    }

    private static void AssertBits(float[] got, float[] expected, string label)
    {
        Assert.AreEqual(expected.Length, got.Length, label);
        for (int i = 0; i < got.Length; i++)
        {
            int a = BitConverter.SingleToInt32Bits(expected[i]);
            int b = BitConverter.SingleToInt32Bits(got[i]);
            if (a != b)
            {
                Assert.Fail($"{label} [{i}] got={got[i]:G9} expected={expected[i]:G9}");
            }
        }
    }

    private static float[] Pattern()
    {
        float[] values = new float[Res * Res];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = 0.2f + (i % 17) * 0.01f;
        }

        return values;
    }

    private static float Integral(float[] dye, int res)
    {
        float area = (Size / res) * (Size / res);
        double sum = 0.0;
        for (int i = 0; i < dye.Length; i++)
        {
            sum += dye[i];
        }

        return (float)(sum * area);
    }

}
