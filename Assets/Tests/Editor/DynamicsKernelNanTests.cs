using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// GPU probes for F0.4 NaN guards in DynamicsPasses.compute.
/// Not a particle harness: one dispatch, GetData, assert finite.
/// </summary>
[TestFixture]
[Category("GPU")]
public class DynamicsKernelNanTests
{
    private const string ComputePath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";
    private const int Stride = 12;

    [Test]
    public void HeadingSteer_OppositeHeadingAtHalfTurn_IsFinite()
    {
        // h ≈ −desired, k = 0.5 → lerp is zero; old normalize(0) wrote NaN.
        Vector3 heading = DispatchHeading(
            new Vector3(1f, 0f, 0f),
            force: new Vector3(-1f, 0f, 0f),
            turnSpeed: 1f,
            deltaTime: 0.5f,
            cruiseSpeed: 4f);

        AssertFinite("heading after 180° blend", heading);
        Assert.That(heading.magnitude, Is.GreaterThan(0.5f));
    }

    [Test]
    public void HeadingSteer_InfectedHeading_RecoversToFinite()
    {
        // Buffer lives across frames: already-NaN heading must take the safe branch.
        Vector3 heading = DispatchHeading(
            new Vector3(float.NaN, float.NaN, float.NaN),
            force: new Vector3(0f, 0f, 1f),
            turnSpeed: 1f,
            deltaTime: 0.5f,
            cruiseSpeed: 4f);

        AssertFinite("heading recovered from NaN", heading);
    }

    [Test]
    public void BoxBounds_Wrap_ZeroYExtent_KeepsFiniteY()
    {
        Vector3 position = DispatchBoxWrap(
            new Vector3(0f, 5f, 0f),
            center: Vector3.zero,
            extents: new Vector3(2f, 0f, 2f));

        AssertFinite("position after wrap with extents.y = 0", position);
        Assert.AreEqual(5f, position.y, 1e-5f, "zero-extent axis must not wrap");
    }

    private static Vector3 DispatchHeading(
        Vector3 headingSeed,
        Vector3 force,
        float turnSpeed,
        float deltaTime,
        float cruiseSpeed)
    {
        ComputeShader shader = LoadDynamics();
        int kernel = shader.FindKernel("HeadingSteer");
        Assume.That(kernel >= 0);

        GraphicsBuffer heading = NewFloat3(headingSeed);
        GraphicsBuffer velocity = NewFloat3(force);
        CommandBuffer cmd = new CommandBuffer { name = "HeadingSteerNaN" };
        try
        {
            cmd.SetComputeBufferParam(shader, kernel, "heading", heading);
            cmd.SetComputeBufferParam(shader, kernel, "velocity", velocity);
            cmd.SetComputeIntParam(shader, SimShaderIds.ParticleCount, 1);
            cmd.SetComputeFloatParam(shader, SimShaderIds.DeltaTime, deltaTime);
            cmd.SetComputeFloatParam(shader, "TurnSpeed", turnSpeed);
            cmd.SetComputeFloatParam(shader, "CruiseSpeed", cruiseSpeed);
            cmd.DispatchCompute(shader, kernel, 1, 1, 1);
            Graphics.ExecuteCommandBuffer(cmd);

            Vector3[] obtained = new Vector3[1];
            heading.GetData(obtained);
            return obtained[0];
        }
        finally
        {
            cmd.Release();
            heading.Release();
            velocity.Release();
        }
    }

    private static Vector3 DispatchBoxWrap(Vector3 positionSeed, Vector3 center, Vector3 extents)
    {
        ComputeShader shader = LoadDynamics();
        int kernel = shader.FindKernel("BoxBounds");
        Assume.That(kernel >= 0);

        GraphicsBuffer position = NewFloat3(positionSeed);
        GraphicsBuffer velocity = NewFloat3(Vector3.zero);
        CommandBuffer cmd = new CommandBuffer { name = "BoxBoundsNaN" };
        try
        {
            cmd.SetComputeBufferParam(shader, kernel, "position", position);
            cmd.SetComputeBufferParam(shader, kernel, "velocity", velocity);
            cmd.SetComputeIntParam(shader, SimShaderIds.ParticleCount, 1);
            cmd.SetComputeVectorParam(shader, "BoundsCenter", center);
            cmd.SetComputeVectorParam(shader, "BoundsExtents", extents);
            cmd.SetComputeFloatParam(shader, "BoundsBounce", 0.6f);
            cmd.SetComputeIntParam(shader, "BoundsMode", 1);
            cmd.DispatchCompute(shader, kernel, 1, 1, 1);
            Graphics.ExecuteCommandBuffer(cmd);

            Vector3[] obtained = new Vector3[1];
            position.GetData(obtained);
            return obtained[0];
        }
        finally
        {
            cmd.Release();
            position.Release();
            velocity.Release();
        }
    }

    private static ComputeShader LoadDynamics()
    {
        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        Assume.That(shader != null, $"Missing '{ComputePath}'.");
        Assume.That(SystemInfo.supportsComputeShaders);
        return shader;
    }

    private static GraphicsBuffer NewFloat3(Vector3 value)
    {
        GraphicsBuffer buffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured
            | GraphicsBuffer.Target.CopySource
            | GraphicsBuffer.Target.CopyDestination,
            1,
            Stride);
        buffer.SetData(new[] { value });
        return buffer;
    }

    private static void AssertFinite(string message, Vector3 value)
    {
        Assert.IsFalse(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z), message);
        Assert.IsFalse(
            float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z),
            message);
    }
}
