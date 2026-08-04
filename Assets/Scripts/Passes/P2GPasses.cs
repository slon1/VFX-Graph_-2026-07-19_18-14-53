using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Zeros the P2G uint accum buffer for a field (WriteInPlace equivalent on accum, not texture).
/// Place before ParticleToFieldScatterPass each round.
/// </summary>
[Serializable]
public sealed class ClearFieldAccumPass : SimPass
{
    private const int ThreadGroupSize = 64;

    [SerializeField] private string fieldName = "agentVelocity";
    [SerializeField, Min(1)] private int channels = 2;

    [NonSerialized] private FieldAccumClearRequest[] clearsCache;
    private KernelHandle kernel;
    private FieldAccumBuffer accum;
    private int accumBufferId;
    private int elementCountId;

    public override string DisplayName => "Clear Field Accum";
    public override PassCategory Category => PassCategory.Emit;
    public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.None;

    public override IReadOnlyList<FieldAccumClearRequest> FieldAccumClears =>
        FieldAccumClearRequestSets.Single(ref clearsCache, fieldName, channels);

    public override void Initialize(SimContext context)
    {
        kernel = context.FindKernel("ClearUintBuffer");
        accum = context.Fields.GetAccumBuffer(fieldName);
        accumBufferId = Shader.PropertyToID("AccumBuffer");
        elementCountId = Shader.PropertyToID("ElementCount");
    }

    public override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;
        if (!kernel.IsValid || accum == null)
        {
            return;
        }

        int count = accum.ElementCount;
        context.Cmd.SetComputeBufferParam(kernel.Shader, kernel.Index, accumBufferId, accum.Buffer);
        context.Cmd.SetComputeIntParam(kernel.Shader, elementCountId, count);
        int groups = (count + ThreadGroupSize - 1) / ThreadGroupSize;
        context.Cmd.DispatchCompute(kernel.Shader, kernel.Index, groups, 1, 1);
        LastExecuteDispatched = true;
    }
}

/// <summary>
/// Abstract particle→field scatter: dispatch by particle count into FieldAccumBuffer via InterlockedAdd.
/// v1 encodes average-ready fixed-point values; count channel is always last (BufferCount = Channels+1).
/// </summary>
[Serializable]
public abstract class ParticleToFieldScatterPass : SimPass
{
    protected const int ThreadGroupSize = 64;

    private KernelHandle kernel;
    private FieldAccumBuffer accum;
    private FieldDescriptor fieldDescriptor;
    private readonly List<(int propertyId, AttributeId attribute)> attributeBindings =
        new List<(int, AttributeId)>();

    private int accumBufferId;
    private int valueScaleId;
    private int valueBiasId;
    private int bufferCountId;
    private int valueChannelsId;

    [NonSerialized] private FieldAccumRequest[] writesCache;

    public override PassCategory Category => PassCategory.Emit;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.None;

    protected abstract string KernelName { get; }
    protected abstract string TargetFieldName { get; }
    protected abstract int Channels { get; }
    protected abstract float ValueScale { get; }
    protected abstract float ValueBias { get; }

    public sealed override IReadOnlyList<FieldAccumRequest> FieldAccumWrites =>
        FieldAccumRequestSets.Single(ref writesCache, TargetFieldName, Channels, ValueScale, ValueBias);

    public override void Initialize(SimContext context)
    {
        kernel = context.FindKernel(KernelName);
        accum = context.Fields.GetAccumBuffer(TargetFieldName);
        fieldDescriptor = context.Fields.Get(TargetFieldName).Descriptor;

        attributeBindings.Clear();
        HashSet<AttributeId> seen = new HashSet<AttributeId>();
        CollectBindings(Reads, seen);

        accumBufferId = Shader.PropertyToID("AccumBuffer");
        valueScaleId = Shader.PropertyToID("ValueScale");
        valueBiasId = Shader.PropertyToID("ValueBias");
        bufferCountId = Shader.PropertyToID("BufferCount");
        valueChannelsId = Shader.PropertyToID("ValueChannels");
    }

    public sealed override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;
        if (!kernel.IsValid || accum == null || fieldDescriptor == null)
        {
            return;
        }

        int count = context.Particles.Count;
        if (count == 0)
        {
            return;
        }

        for (int i = 0; i < attributeBindings.Count; i++)
        {
            (int propertyId, AttributeId attribute) = attributeBindings[i];
            context.Cmd.SetComputeBufferParam(
                kernel.Shader, kernel.Index, propertyId, context.Particles.Get(attribute));
        }

        FieldShaderParams.Push(context.Cmd, kernel.Shader, fieldDescriptor);
        context.Cmd.SetComputeBufferParam(kernel.Shader, kernel.Index, accumBufferId, accum.Buffer);
        context.Cmd.SetComputeIntParam(kernel.Shader, SimShaderIds.ParticleCount, count);
        context.Cmd.SetComputeIntParam(kernel.Shader, bufferCountId, accum.BufferCount);
        context.Cmd.SetComputeIntParam(kernel.Shader, valueChannelsId, Channels);
        context.Cmd.SetComputeFloatParam(kernel.Shader, valueScaleId, ValueScale);
        context.Cmd.SetComputeFloatParam(kernel.Shader, valueBiasId, ValueBias);
        SetParams(context, deltaTime);

        int groups = (count + ThreadGroupSize - 1) / ThreadGroupSize;
        context.Cmd.DispatchCompute(kernel.Shader, kernel.Index, groups, 1, 1);
        LastExecuteDispatched = true;
    }

    protected virtual void SetParams(SimContext context, float deltaTime)
    {
    }

    private void CollectBindings(IReadOnlyList<AttributeId> ids, HashSet<AttributeId> seen)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (seen.Add(ids[i]))
            {
                attributeBindings.Add((Shader.PropertyToID(ids[i].Name), ids[i]));
            }
        }
    }
}

/// <summary>
/// Abstract normalize: decode accum average and add into SimField.Current (WriteInPlace).
/// Replace vs accumulate-onto-decaying is composition with ClearFieldPass, not a branch here.
/// </summary>
[Serializable]
public abstract class NormalizeFieldAccumPass : SimPass
{
    protected const int ThreadGroupSize = 8;

    private KernelHandle kernel;
    private FieldAccumBuffer accum;
    private FieldDescriptor fieldDescriptor;
    private int writeId;
    private int accumBufferId;
    private int valueScaleId;
    private int valueBiasId;
    private int bufferCountId;
    private int channelsId;

    [NonSerialized] private FieldAccumRequest[] readsCache;
    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public override PassCategory Category => PassCategory.Emit;
    public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.None;

    protected abstract string KernelName { get; }
    protected abstract string FieldName { get; }
    protected abstract int Channels { get; }
    protected abstract float ValueScale { get; }
    protected abstract float ValueBias { get; }
    protected abstract FieldSemantic RequiredSemantic { get; }

    public sealed override IReadOnlyList<FieldAccumRequest> FieldAccumReads =>
        FieldAccumRequestSets.Single(ref readsCache, FieldName, Channels, ValueScale, ValueBias);

    public sealed override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, FieldName,
            FieldAccess.WriteInPlace, RequiredSemantic, Channels);

    public override void Initialize(SimContext context)
    {
        kernel = context.FindKernel(KernelName);
        accum = context.Fields.GetAccumBuffer(FieldName);
        fieldDescriptor = context.Fields.Get(FieldName).Descriptor;
        writeId = SimShaderIds.FieldWrite;
        accumBufferId = Shader.PropertyToID("AccumBuffer");
        valueScaleId = Shader.PropertyToID("ValueScale");
        valueBiasId = Shader.PropertyToID("ValueBias");
        bufferCountId = Shader.PropertyToID("BufferCount");
        channelsId = Shader.PropertyToID("ValueChannels");
    }

    public sealed override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;
        if (!kernel.IsValid || accum == null || fieldDescriptor == null)
        {
            return;
        }

        SimField field = context.Fields.Get(FieldName);
        FieldShaderParams.Push(context.Cmd, kernel.Shader, fieldDescriptor);
        context.Cmd.SetComputeTextureParam(kernel.Shader, kernel.Index, writeId, field.Current);
        context.Cmd.SetComputeBufferParam(kernel.Shader, kernel.Index, accumBufferId, accum.Buffer);
        context.Cmd.SetComputeIntParam(kernel.Shader, bufferCountId, accum.BufferCount);
        context.Cmd.SetComputeIntParam(kernel.Shader, channelsId, Channels);
        context.Cmd.SetComputeFloatParam(kernel.Shader, valueScaleId, ValueScale);
        context.Cmd.SetComputeFloatParam(kernel.Shader, valueBiasId, ValueBias);
        SetParams(context, deltaTime);

        Vector2Int res = fieldDescriptor.Resolution;
        int groupsX = (res.x + ThreadGroupSize - 1) / ThreadGroupSize;
        int groupsY = (res.y + ThreadGroupSize - 1) / ThreadGroupSize;
        context.Cmd.DispatchCompute(kernel.Shader, kernel.Index, groupsX, groupsY, 1);
        LastExecuteDispatched = true;
    }

    protected virtual void SetParams(SimContext context, float deltaTime)
    {
    }
}

/// <summary>
/// Scatters particle velocity projected onto the field plane into agentVelocity accum (average-ready).
/// </summary>
[Serializable]
public sealed class ScatterVelocityToFieldPass : ParticleToFieldScatterPass
{
    [SerializeField] private string targetFieldName = "agentVelocity";
    [SerializeField] private float valueScale = 4096f;
    [SerializeField] private float valueBias = 32f;

    protected override string KernelName => "ScatterVelocity";
    protected override string TargetFieldName => targetFieldName;
    protected override int Channels => 2;
    protected override float ValueScale => valueScale;
    protected override float ValueBias => valueBias;

    public override string DisplayName => "Scatter Velocity To Field";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.PositionVelocity;
}

/// <summary>
/// Decodes average agent velocity per texel and adds into the field (WriteInPlace).
/// </summary>
[Serializable]
public sealed class NormalizeVelocityAccumPass : NormalizeFieldAccumPass
{
    [SerializeField] private string fieldName = "agentVelocity";
    [SerializeField] private float valueScale = 4096f;
    [SerializeField] private float valueBias = 32f;

    protected override string KernelName => "NormalizeVelocityAccum";
    protected override string FieldName => fieldName;
    protected override int Channels => 2;
    protected override float ValueScale => valueScale;
    protected override float ValueBias => valueBias;
    protected override FieldSemantic RequiredSemantic => FieldSemantic.Velocity;

    public override string DisplayName => "Normalize Velocity Accum";
}

/// <summary>
/// Scatters a constant presence contribution (1.0) per particle into a scalar density accum.
/// NormalizeDensityAccumPass decodes as a <b>sum</b> (∝ count), not ADR-002 velocity average.
/// Pair with ClearField(density) each frame for Replace (no Scalar Decay in M2b.2.1).
/// </summary>
[Serializable]
public sealed class ScatterDensityToFieldPass : ParticleToFieldScatterPass
{
    [SerializeField] private string targetFieldName = "density";
    [SerializeField] private float valueScale = 4096f;
    [SerializeField] private float valueBias = 0f;

    protected override string KernelName => "ScatterDensity";
    protected override string TargetFieldName => targetFieldName;
    protected override int Channels => 1;
    protected override float ValueScale => valueScale;
    protected override float ValueBias => valueBias;

    public override string DisplayName => "Scatter Density To Field";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
}

/// <summary>
/// Decodes density accum as sum per texel (raw/Scale − count·Bias, no /count) and adds into
/// the Scalar field. Requires ClearField on the same field each frame for Replace semantics.
/// </summary>
[Serializable]
public sealed class NormalizeDensityAccumPass : NormalizeFieldAccumPass
{
    [SerializeField] private string fieldName = "density";
    [SerializeField] private float valueScale = 4096f;
    [SerializeField] private float valueBias = 0f;

    protected override string KernelName => "NormalizeDensityAccum";
    protected override string FieldName => fieldName;
    protected override int Channels => 1;
    protected override float ValueScale => valueScale;
    protected override float ValueBias => valueBias;
    protected override FieldSemantic RequiredSemantic => FieldSemantic.Scalar;

    public override string DisplayName => "Normalize Density Accum";
}
