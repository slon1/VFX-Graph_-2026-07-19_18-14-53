using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Additive splat of touch motion into a velocity field (WriteInPlace — no swap).
/// </summary>
[Serializable]
public sealed class TouchInjectVelocityFieldPass : FieldKernelPass
{
    [SerializeField] private string velocityFieldName = "velocity";

    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public override string DisplayName => "Touch Inject Velocity Field";
    public override PassCategory Category => PassCategory.Emit;
    protected override string KernelName => "TouchInjectVelocity";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, velocityFieldName,
            FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        BindBuffer(context, SimShaderIds.Touches, context.TouchBuffer);
        SetInt(context, SimShaderIds.TouchCount, context.TouchCount);
    }
}

/// <summary>
/// Per-texel exponential decay. Routed through WritePingPong deliberately so M2a
/// exercises World-owned Swap end-to-end before neighbor-reading ops (advection) arrive.
/// Could run WriteInPlace; do not "optimize" without replacing the Swap proof.
/// </summary>
[Serializable]
public sealed class DecayFieldPass : FieldKernelPass
{
    private static readonly int DecayFactorId = Shader.PropertyToID("DecayFactor");

    [SerializeField] private string fieldName = "velocity";
    [SerializeField, Min(0f)] private float decayRate = 1.5f;

    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public float DecayRate
    {
        get => decayRate;
        set => decayRate = value;
    }

    public override string DisplayName => "Decay Field";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "DecayField";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, fieldName,
            FieldAccess.WritePingPong, FieldSemantic.Velocity, 2);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, DecayFactorId, Mathf.Exp(-decayRate * deltaTime));
    }
}

/// <summary>
/// Hybrid G2P: bilinear-sample velocity field at each particle, add to particle velocity.
/// Declares both FieldReads and particle Writes — stress-tests the dual request lists.
/// </summary>
[Serializable]
public sealed class SampleVelocityFieldPass : ParticleKernelPass
{
    private static readonly int SampleStrengthId = Shader.PropertyToID("SampleStrength");

    [SerializeField] private string velocityFieldName = "velocity";
    [SerializeField] private float strength = 1f;

    private FieldDescriptor fieldDescriptor;
    private int velocityReadId;

    [NonSerialized] private FieldRequest[] fieldReadsCache;

    public float Strength
    {
        get => strength;
        set => strength = value;
    }

    public override string DisplayName => "Sample Velocity Field";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "SampleVelocityField";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, velocityFieldName,
            FieldAccess.Read, FieldSemantic.Velocity, 2);

    public override void Initialize(SimContext context)
    {
        base.Initialize(context);
        fieldDescriptor = context.Fields.Get(velocityFieldName).Descriptor;
        velocityReadId = Shader.PropertyToID(velocityFieldName + "Read");
    }

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SimField field = context.Fields.Get(velocityFieldName);
        context.Cmd.SetComputeTextureParam(Kernel.Shader, Kernel.Index, velocityReadId, field.Current);

        Vector2Int res = fieldDescriptor.Resolution;
        context.Cmd.SetComputeIntParams(
            Kernel.Shader, SimShaderIds.FieldResolution, res.x, res.y, 0, 0);
        context.Cmd.SetComputeVectorParam(
            Kernel.Shader,
            SimShaderIds.FieldTexelSize,
            new Vector4(1f / res.x, 1f / res.y, 0f, 0f));
        context.Cmd.SetComputeVectorParam(Kernel.Shader, SimShaderIds.FieldOrigin, fieldDescriptor.Origin);
        context.Cmd.SetComputeVectorParam(
            Kernel.Shader, SimShaderIds.FieldAxisU, fieldDescriptor.AxisU.normalized);
        context.Cmd.SetComputeVectorParam(
            Kernel.Shader, SimShaderIds.FieldAxisV, fieldDescriptor.AxisV.normalized);
        context.Cmd.SetComputeVectorParam(
            Kernel.Shader,
            SimShaderIds.FieldSize,
            new Vector4(fieldDescriptor.Size.x, fieldDescriptor.Size.y, 0f, 0f));

        SetFloat(context, SampleStrengthId, strength);
    }
}
