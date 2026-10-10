using NUnit.Framework;
using UnityEditor;
using UnityEngine;

[TestFixture]
public class ParticleDirectionTests
{
    private const string ComputePath = "Assets/Tests/Editor/ParticleDirectionAddressing.compute";
    private static readonly Vector3 AxisZ = new Vector3(0f, 0f, 1f);
    private static readonly Vector3 AxisY = new Vector3(0f, 1f, 0f);

    [Test]
    public void DirectionToValue_MatchesTheCases()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        Assert.That(shader != null, "ParticleDirectionAddressing.compute must be imported.");
        Assert.That(shader.HasKernel("DirectionAddressing"));

        Vector3[] velocity =
        {
            new Vector3(0f, 0f, 1f),
            new Vector3(0f, 0f, -1f),
            new Vector3(1f, 0f, 0f),
            new Vector3(-1f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            Vector3.zero,
            new Vector3(0f, 0f, 4f),
            new Vector3(1f, 0f, 1f),
            new Vector3(0f, 1f, 0f),
        };
        Vector3[] axis =
        {
            AxisZ, AxisZ, AxisZ, AxisZ, AxisZ, AxisZ, AxisZ, AxisZ, AxisY,
        };
        float diagonal = 0.5f + 0.5f / Mathf.Sqrt(2f);
        float[] expected = { 1f, 0f, 0.5f, 0.5f, 0.5f, 0.5f, 1f, diagonal, 1f };

        float[] got = Sample(shader, velocity, axis);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i], got[i], 1e-5f, "case " + i);
        }
    }

    private static float[] Sample(ComputeShader shader, Vector3[] velocity, Vector3[] axis)
    {
        int count = velocity.Length;
        int kernel = shader.FindKernel("DirectionAddressing");
        GraphicsBuffer velocityBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
        GraphicsBuffer axisBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
        GraphicsBuffer outBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float));
        try
        {
            velocityBuf.SetData(velocity);
            axisBuf.SetData(axis);
            shader.SetBuffer(kernel, "_InVelocity", velocityBuf);
            shader.SetBuffer(kernel, "_InAxis", axisBuf);
            shader.SetBuffer(kernel, "_OutValue", outBuf);
            shader.SetInt("_CaseCount", count);
            shader.Dispatch(kernel, 1, 1, 1);
            float[] output = new float[count];
            outBuf.GetData(output);
            return output;
        }
        finally
        {
            velocityBuf.Release();
            axisBuf.Release();
            outBuf.Release();
        }
    }
}
