using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// EditMode GPU numeric harness (ADR-014). Owns one CommandBuffer, executes it,
/// and repeats World’s Execute + WritePingPong swap loop. Test-only; not a SimPass.
/// </summary>
internal sealed class FieldTestHarness : IDisposable
{
    internal const string ProbesAssetPath = "Assets/Tests/Editor/Shaders/HarnessProbes.compute";

    private static readonly int FillScalarValuesId = Shader.PropertyToID("FillScalarValues");
    private static readonly int FillVelocityValuesId = Shader.PropertyToID("FillVelocityValues");
    private static readonly int ScalarWriteId = Shader.PropertyToID("ScalarWrite");
    private static readonly int VelocityWriteId = Shader.PropertyToID("VelocityWrite");
    private static readonly int ScalarReadId = Shader.PropertyToID("ScalarRead");
    private static readonly int VelocityReadId = Shader.PropertyToID("VelocityRead");
    private static readonly int ReadScalarValuesId = Shader.PropertyToID("ReadScalarValues");
    private static readonly int ReadVelocityValuesId = Shader.PropertyToID("ReadVelocityValues");
    private static readonly int ProbeSrcId = Shader.PropertyToID("ProbeSrc");
    private static readonly int ProbeUVsId = Shader.PropertyToID("ProbeUVs");
    private static readonly int ProbeValuesId = Shader.PropertyToID("ProbeValues");
    private static readonly int ProbeCountId = Shader.PropertyToID("ProbeCount");

    private const int FieldThreads = 8;
    private const int ProbeThreads = 64;

    private readonly ComputeShader probes;
    private readonly int fillScalarKernel;
    private readonly int fillVelocityKernel;
    private readonly int readScalarKernel;
    private readonly int readVelocityKernel;
    private readonly int probeKernel;

    private readonly FieldSet fields;
    private readonly CommandBuffer cmd;
    private readonly SimContext context;

    private GraphicsBuffer fillScalarBuffer;
    private GraphicsBuffer fillVelocityBuffer;
    private GraphicsBuffer readScalarBuffer;
    private GraphicsBuffer readVelocityBuffer;
    private GraphicsBuffer probeUvBuffer;
    private GraphicsBuffer probeValueBuffer;

    internal FieldTestHarness(FieldDescriptor[] descriptors, params string[] computeAssetPaths)
    {
        Assume.That(SystemInfo.supportsComputeShaders, "GPU compute is required for numeric field tests.");
        Assume.That(descriptors != null && descriptors.Length > 0, "At least one FieldDescriptor is required.");

        probes = LoadCompute(ProbesAssetPath);
        Assume.That(probes != null, $"Missing test compute at '{ProbesAssetPath}'.");
        Assume.That(probes.HasKernel("FillScalarFromBuffer"));
        Assume.That(probes.HasKernel("FillVelocityFromBuffer"));
        Assume.That(probes.HasKernel("ReadScalarToBuffer"));
        Assume.That(probes.HasKernel("ReadVelocityToBuffer"));
        Assume.That(probes.HasKernel("ProbeSampleLevelScalar"));

        fillScalarKernel = probes.FindKernel("FillScalarFromBuffer");
        fillVelocityKernel = probes.FindKernel("FillVelocityFromBuffer");
        readScalarKernel = probes.FindKernel("ReadScalarToBuffer");
        readVelocityKernel = probes.FindKernel("ReadVelocityToBuffer");
        probeKernel = probes.FindKernel("ProbeSampleLevelScalar");

        List<ComputeShader> shaders = new List<ComputeShader>(1 + (computeAssetPaths != null ? computeAssetPaths.Length : 0))
        {
            probes,
        };
        if (computeAssetPaths != null)
        {
            for (int i = 0; i < computeAssetPaths.Length; i++)
            {
                ComputeShader shader = LoadCompute(computeAssetPaths[i]);
                Assume.That(shader != null, $"Compute shader not found at '{computeAssetPaths[i]}'.");
                shaders.Add(shader);
            }
        }

        cmd = new CommandBuffer { name = "FieldTestHarness" };
        fields = new FieldSet();
        fields.Allocate(descriptors, cmd);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();

        context = new SimContext(null, fields, shaders.ToArray(), null);
        context.Cmd = cmd;
    }

    internal SimContext Context => context;

    internal static FieldDescriptor Descriptor(
        string name,
        FieldSemantic semantic,
        GraphicsFormat format,
        Vector2Int resolution,
        Vector2 size,
        Color clear)
    {
        FieldDescriptor descriptor = FieldDescriptor.CreateDefault(name, semantic);
        SetPrivate(descriptor, "format", format);
        SetPrivate(descriptor, "resolution", resolution);
        SetPrivate(descriptor, "size", size);
        SetPrivate(descriptor, "clearValue", clear);
        return descriptor;
    }

    internal void SeedScalar(string field, float[] values)
    {
        SimField simField = fields.Get(field);
        int count = TexelCount(simField);
        if (values == null || values.Length != count)
        {
            throw new ArgumentException(
                $"SeedScalar '{field}': expected {count} values, got {values?.Length ?? 0}.");
        }

        EnsureBuffer(ref fillScalarBuffer, count, sizeof(float));
        fillScalarBuffer.SetData(values);

        FieldShaderParams.Push(cmd, probes, simField.Descriptor);
        cmd.SetComputeBufferParam(probes, fillScalarKernel, FillScalarValuesId, fillScalarBuffer);
        cmd.SetComputeTextureParam(probes, fillScalarKernel, ScalarWriteId, simField.Current);
        DispatchField(fillScalarKernel, simField.Descriptor.Resolution);
        Flush();
    }

    internal void SeedVelocity(string field, Vector2[] values)
    {
        SimField simField = fields.Get(field);
        int count = TexelCount(simField);
        if (values == null || values.Length != count)
        {
            throw new ArgumentException(
                $"SeedVelocity '{field}': expected {count} values, got {values?.Length ?? 0}.");
        }

        EnsureBuffer(ref fillVelocityBuffer, count, 2 * sizeof(float));
        fillVelocityBuffer.SetData(values);

        FieldShaderParams.Push(cmd, probes, simField.Descriptor);
        cmd.SetComputeBufferParam(probes, fillVelocityKernel, FillVelocityValuesId, fillVelocityBuffer);
        cmd.SetComputeTextureParam(probes, fillVelocityKernel, VelocityWriteId, simField.Current);
        DispatchField(fillVelocityKernel, simField.Descriptor.Resolution);
        Flush();
    }

    internal float[] ReadScalar(string field)
    {
        SimField simField = fields.Get(field);
        int count = TexelCount(simField);
        EnsureBuffer(ref readScalarBuffer, count, sizeof(float));

        FieldShaderParams.Push(cmd, probes, simField.Descriptor);
        cmd.SetComputeTextureParam(probes, readScalarKernel, ScalarReadId, simField.Current);
        cmd.SetComputeBufferParam(probes, readScalarKernel, ReadScalarValuesId, readScalarBuffer);
        DispatchField(readScalarKernel, simField.Descriptor.Resolution);
        Flush();

        float[] result = new float[count];
        readScalarBuffer.GetData(result);
        return result;
    }

    internal Vector2[] ReadVelocity(string field)
    {
        SimField simField = fields.Get(field);
        int count = TexelCount(simField);
        EnsureBuffer(ref readVelocityBuffer, count, 2 * sizeof(float));

        FieldShaderParams.Push(cmd, probes, simField.Descriptor);
        cmd.SetComputeTextureParam(probes, readVelocityKernel, VelocityReadId, simField.Current);
        cmd.SetComputeBufferParam(probes, readVelocityKernel, ReadVelocityValuesId, readVelocityBuffer);
        DispatchField(readVelocityKernel, simField.Descriptor.Resolution);
        Flush();

        Vector2[] result = new Vector2[count];
        readVelocityBuffer.GetData(result);
        return result;
    }

    internal float[] ProbeSampleLevel(string field, Vector2[] uvs)
    {
        if (uvs == null || uvs.Length == 0)
        {
            throw new ArgumentException("ProbeSampleLevel requires at least one UV.", nameof(uvs));
        }

        SimField simField = fields.Get(field);
        int count = uvs.Length;
        EnsureBuffer(ref probeUvBuffer, count, 2 * sizeof(float));
        EnsureBuffer(ref probeValueBuffer, count, sizeof(float));
        probeUvBuffer.SetData(uvs);

        cmd.SetComputeTextureParam(probes, probeKernel, ProbeSrcId, simField.Current);
        cmd.SetComputeBufferParam(probes, probeKernel, ProbeUVsId, probeUvBuffer);
        cmd.SetComputeBufferParam(probes, probeKernel, ProbeValuesId, probeValueBuffer);
        cmd.SetComputeIntParam(probes, ProbeCountId, count);

        int groups = (count + ProbeThreads - 1) / ProbeThreads;
        cmd.DispatchCompute(probes, probeKernel, groups, 1, 1);
        Flush();

        float[] result = new float[count];
        probeValueBuffer.GetData(result);
        return result;
    }

    /// <summary>
    /// Literal copy of SimulationWorld.Update + SwapPingPongFields.
    /// Two-arg form uses <see cref="SimPass.RepeatCount"/>; three-arg overrides it
    /// for the N-single-executions reference side of ADR-015.
    /// </summary>
    internal void RunPass(SimPass pass, float deltaTime)
    {
        if (pass == null)
        {
            throw new ArgumentNullException(nameof(pass));
        }

        RunPass(pass, deltaTime, pass.RepeatCount);
    }

    internal void RunPass(SimPass pass, float deltaTime, int repeat)
    {
        if (pass == null)
        {
            throw new ArgumentNullException(nameof(pass));
        }

        if (repeat < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(repeat), repeat, "repeat must be >= 1.");
        }

        for (int i = 0; i < repeat; i++)
        {
            pass.Execute(context, deltaTime);
            if (pass.LastExecuteDispatched)
            {
                IReadOnlyList<FieldRequest> writes = pass.FieldWrites;
                for (int w = 0; w < writes.Count; w++)
                {
                    if (writes[w].Access == FieldAccess.WritePingPong)
                    {
                        fields.Swap(writes[w].FieldName);
                    }
                }
            }
        }

        Flush();
    }

    internal void Flush()
    {
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
    }

    internal static float RelativeTolerance(GraphicsFormat format)
    {
        switch (format)
        {
            case GraphicsFormat.R16_SFloat:
            case GraphicsFormat.R16G16_SFloat:
                return 1e-3f;
            case GraphicsFormat.R32_SFloat:
            case GraphicsFormat.R32G32_SFloat:
                return 1e-6f;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(format), format, "No harness tolerance for this format.");
        }
    }

    /// <param name="absoluteFloor">
    /// Absolute floor of the mixed tolerance. Default (−1) uses <see cref="RelativeTolerance"/>.
    /// </param>
    internal static void AssertApproximately(
        float[] obtained,
        float[] expected,
        GraphicsFormat format,
        string message,
        float absoluteFloor = -1f)
    {
        if (obtained == null)
        {
            throw new ArgumentNullException(nameof(obtained));
        }

        if (expected == null)
        {
            throw new ArgumentNullException(nameof(expected));
        }

        Assert.AreEqual(expected.Length, obtained.Length, $"{message}: length");

        float rel = RelativeTolerance(format);
        float floor = absoluteFloor < 0f ? rel : absoluteFloor;
        for (int i = 0; i < expected.Length; i++)
        {
            float scale = Mathf.Max(Mathf.Abs(expected[i]), Mathf.Abs(obtained[i]));
            float tol = Mathf.Max(rel * scale, floor);
            float delta = Mathf.Abs(obtained[i] - expected[i]);
            if (delta > tol)
            {
                Assert.Fail(
                    $"{message}: [{i}] obtained={obtained[i]:G9} expected={expected[i]:G9} " +
                    $"Δ={delta:G9} tol={tol:G9}");
            }
        }
    }

    internal static void AssertApproximately(
        Vector2[] obtained,
        Vector2[] expected,
        GraphicsFormat format,
        string message,
        float absoluteFloor = -1f)
    {
        if (obtained == null)
        {
            throw new ArgumentNullException(nameof(obtained));
        }

        if (expected == null)
        {
            throw new ArgumentNullException(nameof(expected));
        }

        Assert.AreEqual(expected.Length, obtained.Length, $"{message}: length");

        float rel = RelativeTolerance(format);
        float floor = absoluteFloor < 0f ? rel : absoluteFloor;
        for (int i = 0; i < expected.Length; i++)
        {
            AssertComponent(obtained[i].x, expected[i].x, rel, floor, $"{message}: [{i}].x");
            AssertComponent(obtained[i].y, expected[i].y, rel, floor, $"{message}: [{i}].y");
        }
    }

    public void Dispose()
    {
        ReleaseBuffer(ref fillScalarBuffer);
        ReleaseBuffer(ref fillVelocityBuffer);
        ReleaseBuffer(ref readScalarBuffer);
        ReleaseBuffer(ref readVelocityBuffer);
        ReleaseBuffer(ref probeUvBuffer);
        ReleaseBuffer(ref probeValueBuffer);

        fields?.Dispose();

        if (cmd != null)
        {
            cmd.Release();
        }
    }

    private void DispatchField(int kernel, Vector2Int resolution)
    {
        int groupsX = (resolution.x + FieldThreads - 1) / FieldThreads;
        int groupsY = (resolution.y + FieldThreads - 1) / FieldThreads;
        cmd.DispatchCompute(probes, kernel, groupsX, groupsY, 1);
    }

    private static int TexelCount(SimField field)
    {
        Vector2Int res = field.Descriptor.Resolution;
        return res.x * res.y;
    }

    private static ComputeShader LoadCompute(string assetPath)
    {
        return AssetDatabase.LoadAssetAtPath<ComputeShader>(assetPath);
    }

    private static void EnsureBuffer(ref GraphicsBuffer buffer, int count, int stride)
    {
        if (buffer != null && buffer.count >= count && buffer.stride == stride)
        {
            return;
        }

        ReleaseBuffer(ref buffer);
        buffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured
            | GraphicsBuffer.Target.CopySource
            | GraphicsBuffer.Target.CopyDestination,
            count,
            stride);
    }

    private static void ReleaseBuffer(ref GraphicsBuffer buffer)
    {
        if (buffer == null)
        {
            return;
        }

        buffer.Release();
        buffer = null;
    }

    private static void AssertComponent(float obtained, float expected, float rel, float floor, string message)
    {
        float scale = Mathf.Max(Mathf.Abs(expected), Mathf.Abs(obtained));
        float tol = Mathf.Max(rel * scale, floor);
        float delta = Mathf.Abs(obtained - expected);
        if (delta > tol)
        {
            Assert.Fail($"{message} obtained={obtained:G9} expected={expected:G9} Δ={delta:G9} tol={tol:G9}");
        }
    }

    private static void SetPrivate(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, fieldName);
        field.SetValue(target, value);
    }
}
