using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
[Category("GPU")]
public class HarnessClearTests
{
    [Test]
    public void HarnessClearExecutes_ReadbackMatchesClearValue()
    {
        const float clear = 0.25f;
        FieldDescriptor desc = FieldTestHarness.Descriptor(
            "density",
            FieldSemantic.Scalar,
            GraphicsFormat.R32_SFloat,
            new Vector2Int(8, 8),
            new Vector2(8f, 8f),
            new Color(clear, 0f, 0f, 0f));

        using (FieldTestHarness harness = new FieldTestHarness(new[] { desc }))
        {
            float[] obtained = harness.ReadScalar("density");
            float[] expected = new float[obtained.Length];
            for (int i = 0; i < expected.Length; i++)
            {
                expected[i] = clear;
            }

            FieldTestHarness.AssertApproximately(
                obtained, expected, GraphicsFormat.R32_SFloat, "ClearBoth must be executed");
        }
    }
}
