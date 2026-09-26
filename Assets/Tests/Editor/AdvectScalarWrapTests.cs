using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class AdvectScalarWrapTests
{
    private const string Trail = "trail";
    private const string Velocity = "velocity";
    private const string FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute";
    private const int Resolution = 8;

    [Test]
    public void OneStep_RightEdge_LandsOnLeftTexel()
    {
        Assume.That(SystemInfo.supportsComputeShaders);

        FieldDescriptor trail = FieldTestHarness.Descriptor(
            Trail,
            FieldSemantic.Scalar,
            GraphicsFormat.R32_SFloat,
            new Vector2Int(Resolution, Resolution),
            new Vector2(Resolution, Resolution),
            Color.clear);
        FieldDescriptor velocity = FieldTestHarness.Descriptor(
            Velocity,
            FieldSemantic.Velocity,
            GraphicsFormat.R32G32_SFloat,
            new Vector2Int(Resolution, Resolution),
            new Vector2(Resolution, Resolution),
            Color.clear);

        using (FieldTestHarness harness = new FieldTestHarness(new[] { trail, velocity }, FieldCompute))
        {
            float[] scalar = new float[Resolution * Resolution];
            scalar[4 * Resolution + 7] = 1f;
            Vector2[] speed = new Vector2[Resolution * Resolution];
            for (int i = 0; i < speed.Length; i++)
            {
                speed[i] = new Vector2(1f, 0f);
            }

            harness.SeedScalar(Trail, scalar);
            harness.SeedVelocity(Velocity, speed);

            AdvectScalarPass pass = new AdvectScalarPass
            {
                ScalarField = Trail,
                VelocityField = Velocity,
                DissipationRate = 0f,
                WrapUv = true,
            };
            pass.Initialize(harness.Context);
            harness.RunPass(pass, 1f);

            float[] after = harness.ReadScalar(Trail);
            Assert.Greater(after[4 * Resolution + 0], 0.9f);
            Assert.Less(after[4 * Resolution + 7], 0.1f);
        }
    }
}
