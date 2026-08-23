using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
[Category("GPU")]
public class HarnessSamplerTests
{
    private const int Res = 64;
    private const string Field = "density";

    [Test]
    public void BilinearSampleLevel_BetweenTexels_ReturnsU_IndependentOfFilterMode()
    {
        FieldDescriptor desc = FieldTestHarness.Descriptor(
            Field,
            FieldSemantic.Scalar,
            GraphicsFormat.R16_SFloat,
            new Vector2Int(Res, Res),
            new Vector2(Res, Res),
            Color.clear);

        using (FieldTestHarness harness = new FieldTestHarness(new[] { desc }))
        {
            float[] seed = new float[Res * Res];
            for (int y = 0; y < Res; y++)
            {
                for (int x = 0; x < Res; x++)
                {
                    seed[y * Res + x] = (x + 0.5f) / Res;
                }
            }

            harness.SeedScalar(Field, seed);

            Vector2[] uvs =
            {
                new Vector2(16.75f / Res, 0.5f),
                new Vector2(17f / Res, 0.5f),
            };
            float[] expected = { uvs[0].x, uvs[1].x };

            RenderTexture rt = harness.Context.Fields.Get(Field).Current;
            rt.filterMode = FilterMode.Point;
            float[] point = harness.ProbeSampleLevel(Field, uvs);

            rt.filterMode = FilterMode.Bilinear;
            float[] bilinear = harness.ProbeSampleLevel(Field, uvs);

            WriteProbe("quarter", uvs[0].x, point[0], bilinear[0], 16.5f / Res);
            WriteProbe("half-texel", uvs[1].x, point[1], bilinear[1], 16.5f / Res);

            FieldTestHarness.AssertApproximately(
                point, expected, GraphicsFormat.R16_SFloat, "Point filterMode probe");
            FieldTestHarness.AssertApproximately(
                bilinear, expected, GraphicsFormat.R16_SFloat, "Bilinear filterMode probe");
            FieldTestHarness.AssertApproximately(
                point, bilinear, GraphicsFormat.R16_SFloat,
                "inline sampler_linear_clamp must ignore RT.filterMode");
        }
    }

    private static void WriteProbe(string name, float expected, float point, float bilinear, float nearest)
    {
            TestContext.WriteLine(
                $"{name}: expected={expected:G9} point_rt={point:G9} bilinear_rt={bilinear:G9} " +
                $"Δpoint={Mathf.Abs(point - expected):G9} Δbilinear={Mathf.Abs(bilinear - expected):G9} " +
                $"|Δ| to nearest {nearest:G9} from bilinear={Mathf.Abs(bilinear - nearest):G9}");
    }
}
