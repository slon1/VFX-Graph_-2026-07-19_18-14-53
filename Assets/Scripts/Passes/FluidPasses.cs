using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Raw central-difference divergence D = uE.x − uW.x + uN.y − uS.y (ADR-016 §2).
/// WriteInPlace on fluidD; velocity is Read. Square texel required (ADR-017).
/// Default field name is velocity, not flockVel — Fluid2D must not share the boids field.
/// </summary>
[Serializable]
public sealed class DivergenceFieldPass : FieldKernelPass
{
    [SerializeField] private string velocityField = "velocity";
    [SerializeField] private string divergenceField = "fluidD";

    [NonSerialized] private FieldRequest[] fieldReadsCache;
    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string VelocityField
    {
        get => velocityField;
        set => velocityField = value;
    }

    public string DivergenceField
    {
        get => divergenceField;
        set => divergenceField = value;
    }

    public override string DisplayName => "Divergence";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "Divergence";
    public override bool RequiresSquareTexel => true;

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, velocityField,
            FieldAccess.Read, FieldSemantic.Velocity, 2, FieldSlotRole.A);

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, divergenceField,
            FieldAccess.WriteInPlace, FieldSemantic.Scalar, 1, FieldSlotRole.B);
}

/// <summary>
/// Jacobi iteration on Φ: ΦC ← (ΦN+ΦS+ΦE+ΦW − D) / 4 (ADR-016 §2, ADR-018).
/// WritePingPong Role A on fluidPhi; fluidD is Read Role B. Square texel required.
/// RepeatCount is the iteration count (first real consumer of ADR-015).
/// </summary>
[Serializable]
public sealed class JacobiPhiPass : FieldKernelPass
{
    [SerializeField] private string phiField = "fluidPhi";
    [SerializeField] private string divergenceField = "fluidD";
    [SerializeField, Range(1, 80)] private int iterations = 40;

    [NonSerialized] private FieldRequest[] fieldReadsCache;
    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string PhiField
    {
        get => phiField;
        set => phiField = value;
    }

    public string DivergenceField
    {
        get => divergenceField;
        set => divergenceField = value;
    }

    public int Iterations
    {
        get => iterations;
        set => iterations = value;
    }

    public override string DisplayName => "Jacobi";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "Jacobi";
    public override bool RequiresSquareTexel => true;
    public override int RepeatCount => iterations;

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, divergenceField,
            FieldAccess.Read, FieldSemantic.Scalar, 1, FieldSlotRole.B);

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, phiField,
            FieldAccess.WritePingPong, FieldSemantic.Scalar, 1, FieldSlotRole.A);
}

/// <summary>
/// Subtract Φ gradient: u ← u* − ((ΦE−ΦW)/4, (ΦN−ΦS)/4) (ADR-016 §2, ADR-020).
/// WriteInPlace Role A on velocity; fluidPhi is Read Role B. Square texel required.
/// Reads u* from FieldWriteA (WriteInPlace binds WriteId only).
/// </summary>
[Serializable]
public sealed class SubtractPhiGradientPass : FieldKernelPass
{
    [SerializeField] private string velocityField = "velocity";
    [SerializeField] private string phiField = "fluidPhi";

    [NonSerialized] private FieldRequest[] fieldReadsCache;
    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string VelocityField
    {
        get => velocityField;
        set => velocityField = value;
    }

    public string PhiField
    {
        get => phiField;
        set => phiField = value;
    }

    public override string DisplayName => "Subtract Phi Gradient";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "SubtractPhiGradient";
    public override bool RequiresSquareTexel => true;

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, phiField,
            FieldAccess.Read, FieldSemantic.Scalar, 1, FieldSlotRole.B);

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, velocityField,
            FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2, FieldSlotRole.A);
}

/// <summary>
/// Subtract mean(D) over all texels so Jacobi sees a compatible Neumann RHS (ADR-018 §5.1).
/// Three kernels in one Execute; not FieldKernelPass (sealed single-kernel Execute).
/// </summary>
[Serializable]
public sealed class ZeroMeanScalarPass : SimPass, IDisposable
{
    private const float Bias = 256f;
    private const int FieldThreads = 8;

    [SerializeField] private string scalarField = "fluidD";

    [NonSerialized] private FieldRequest[] fieldWritesCache;
    private KernelHandle clearKernel;
    private KernelHandle accumKernel;
    private KernelHandle applyKernel;
    private GraphicsBuffer meanAccum;
    private FieldDescriptor descriptor;
    private int texelCount;
    private int scale;
    private int meanAccumId;
    private int meanBiasId;
    private int meanScaleId;
    private int texelCountId;

    public string ScalarField
    {
        get => scalarField;
        set => scalarField = value;
    }

    public int Scale => scale;

    public override string DisplayName => "Zero Mean Scalar";
    public override PassCategory Category => PassCategory.Transport;
    public override bool RequiresSquareTexel => false;
    public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.None;

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, scalarField,
            FieldAccess.WriteInPlace, FieldSemantic.Scalar, 1, FieldSlotRole.A);

    public override void Initialize(SimContext context)
    {
        clearKernel = context.FindKernel("ZeroMeanClear");
        accumKernel = context.FindKernel("ZeroMeanAccum");
        applyKernel = context.FindKernel("ZeroMeanApply");
        meanAccumId = Shader.PropertyToID("MeanAccum");
        meanBiasId = Shader.PropertyToID("MeanBias");
        meanScaleId = Shader.PropertyToID("MeanScale");
        texelCountId = Shader.PropertyToID("TexelCount");

        descriptor = context.Fields.Get(scalarField).Descriptor;
        Vector2Int res = descriptor.Resolution;
        texelCount = res.x * res.y;
        scale = Mathf.Max(1, (1 << 30) / (2 * texelCount * 256));

        Dispose();
        meanAccum = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
    }

    public override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;
        if (!clearKernel.IsValid || !accumKernel.IsValid || !applyKernel.IsValid ||
            meanAccum == null || descriptor == null)
        {
            return;
        }

        RenderTexture current = context.Fields.Get(scalarField).Current;
        Vector2Int res = descriptor.Resolution;
        int groupsX = (res.x + FieldThreads - 1) / FieldThreads;
        int groupsY = (res.y + FieldThreads - 1) / FieldThreads;

        context.Cmd.SetComputeBufferParam(
            clearKernel.Shader, clearKernel.Index, meanAccumId, meanAccum);
        context.Cmd.DispatchCompute(clearKernel.Shader, clearKernel.Index, 1, 1, 1);

        FieldShaderParams.Push(context.Cmd, accumKernel.Shader, descriptor);
        context.Cmd.SetComputeFloatParam(accumKernel.Shader, meanBiasId, Bias);
        context.Cmd.SetComputeFloatParam(accumKernel.Shader, meanScaleId, scale);
        context.Cmd.SetComputeTextureParam(
            accumKernel.Shader, accumKernel.Index, SimShaderIds.FieldWriteA, current);
        context.Cmd.SetComputeBufferParam(
            accumKernel.Shader, accumKernel.Index, meanAccumId, meanAccum);
        context.Cmd.DispatchCompute(accumKernel.Shader, accumKernel.Index, groupsX, groupsY, 1);

        FieldShaderParams.Push(context.Cmd, applyKernel.Shader, descriptor);
        context.Cmd.SetComputeFloatParam(applyKernel.Shader, meanBiasId, Bias);
        context.Cmd.SetComputeFloatParam(applyKernel.Shader, meanScaleId, scale);
        context.Cmd.SetComputeIntParam(applyKernel.Shader, texelCountId, texelCount);
        context.Cmd.SetComputeTextureParam(
            applyKernel.Shader, applyKernel.Index, SimShaderIds.FieldWriteA, current);
        context.Cmd.SetComputeBufferParam(
            applyKernel.Shader, applyKernel.Index, meanAccumId, meanAccum);
        context.Cmd.DispatchCompute(applyKernel.Shader, applyKernel.Index, groupsX, groupsY, 1);

        LastExecuteDispatched = true;
    }

    public void Dispose()
    {
        if (meanAccum != null)
        {
            meanAccum.Dispose();
            meanAccum = null;
        }
    }
}
