using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-frame reset of a field's Current to <see cref="FieldDescriptor.ClearValue"/>
/// (WriteInPlace, no compute). Place before P2G / splat accumulators.
/// </summary>
[Serializable]
public sealed class ClearFieldPass : SimPass
{
    [SerializeField] private string fieldName = "velocity";
    [SerializeField] private FieldSemantic requiredSemantic = FieldSemantic.Velocity;
    [SerializeField, Min(1)] private int channels = 2;

    [NonSerialized] private FieldRequest[] fieldWritesCache;
    private SimField field;

    public override string DisplayName => "Clear Field";
    public override PassCategory Category => PassCategory.Emit;
    public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.None;

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, fieldName,
            FieldAccess.WriteInPlace, requiredSemantic, channels);

    public override void Initialize(SimContext context)
    {
        field = context.Fields.Get(fieldName);
    }

    public override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;
        if (field == null)
        {
            return;
        }

        field.ClearCurrent(context.Cmd);
        LastExecuteDispatched = true;
    }
}

/// <summary>
/// Additive splat of touch motion into a velocity field (WriteInPlace — no swap).
/// </summary>
[Serializable]
public sealed class TouchInjectVelocityFieldPass : FieldKernelPass
{
    private static readonly int MaxFieldSpeedId = Shader.PropertyToID("MaxFieldSpeed");

    [SerializeField] private string velocityFieldName = "velocity";
    [SerializeField, Min(0f)] private float maxFieldSpeed = 20f;

    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public float MaxFieldSpeed
    {
        get => maxFieldSpeed;
        set => maxFieldSpeed = value;
    }

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
        SetFloat(context, MaxFieldSpeedId, maxFieldSpeed);
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

    public string FieldName
    {
        get => fieldName;
        set => fieldName = value;
    }

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
/// Explicit 5-point Laplacian diffusion on a scalar field (WritePingPong).
/// UV-index scheme: new = C + DiffusionRate * DeltaTime * (N+S+E+W-4C).
/// Empirically keep DiffusionRate * DeltaTime ≲ 0.2–0.25 or checkerboard instability appears.
/// Scalar Decay is out of scope (M2b.3.1). Prefer several mild Diffuse passes over one huge rate.
/// </summary>
[Serializable]
public sealed class DiffuseFieldPass : FieldKernelPass
{
    [SerializeField] private string fieldName = "density";
    [SerializeField, Min(0f)] private float diffusionRate = 0.15f;

    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string FieldName
    {
        get => fieldName;
        set => fieldName = value;
    }

    public float DiffusionRate
    {
        get => diffusionRate;
        set => diffusionRate = value;
    }

    public override string DisplayName => "Diffuse Field";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "DiffuseField";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, fieldName,
            FieldAccess.WritePingPong, FieldSemantic.Scalar, 1);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
        SetFloat(context, SimShaderIds.DiffusionRate, diffusionRate);
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
        velocityReadId = SimShaderIds.FieldRead;
    }

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SimField field = context.Fields.Get(velocityFieldName);
        context.Cmd.SetComputeTextureParam(Kernel.Shader, Kernel.Index, velocityReadId, field.Current);
        FieldShaderParams.Push(context.Cmd, Kernel.Shader, fieldDescriptor);
        SetFloat(context, SampleStrengthId, strength);
    }
}

/// <summary>
/// Hybrid G2P force: central-difference gradient of a scalar field at each particle,
/// added as acceleration (direction * Strength * dt). Raw finite differences — does not
/// require or assume prior Diffuse; noisy on sharp/raw fields by design.
/// Negative Strength moves against the gradient (descent / separation).
/// </summary>
[Serializable]
public sealed class SampleGradientFieldPass : ParticleKernelPass
{
    private static readonly int SampleStrengthId = Shader.PropertyToID("SampleStrength");

    [SerializeField] private string fieldName = "density";
    [SerializeField] private float strength = 1f;

    private FieldDescriptor fieldDescriptor;
    private int fieldReadId;

    [NonSerialized] private FieldRequest[] fieldReadsCache;

    public float Strength
    {
        get => strength;
        set => strength = value;
    }

    public override string DisplayName => "Sample Gradient Field";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "SampleGradient";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, fieldName,
            FieldAccess.Read, FieldSemantic.Scalar, 1);

    public override void Initialize(SimContext context)
    {
        base.Initialize(context);
        fieldDescriptor = context.Fields.Get(fieldName).Descriptor;
        fieldReadId = SimShaderIds.FieldRead;
    }

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SimField field = context.Fields.Get(fieldName);
        context.Cmd.SetComputeTextureParam(Kernel.Shader, Kernel.Index, fieldReadId, field.Current);
        FieldShaderParams.Push(context.Cmd, Kernel.Shader, fieldDescriptor);
        SetFloat(context, SampleStrengthId, strength);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}
