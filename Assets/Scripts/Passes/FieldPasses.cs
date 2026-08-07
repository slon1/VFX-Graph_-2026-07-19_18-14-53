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
        SetFloat(context, SimShaderIds.DecayFactor, Mathf.Exp(-decayRate * deltaTime));
    }
}

/// <summary>
/// Exponential decay on a scalar field (WritePingPong). Same factor formula as velocity Decay:
/// new = value * exp(-DecayRate * dt). Enables Accumulate-onto-decaying for density
/// (ClearAccum → Scatter → Normalize → Decay) without ClearField each frame.
/// </summary>
[Serializable]
public sealed class DecayFieldScalarPass : FieldKernelPass
{
    [SerializeField] private string fieldName = "density";
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

    public override string DisplayName => "Decay Field (Scalar)";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "DecayFieldScalar";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, fieldName,
            FieldAccess.WritePingPong, FieldSemantic.Scalar, 1);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DecayFactor, Mathf.Exp(-decayRate * deltaTime));
    }
}

/// <summary>
/// Explicit 5-point Laplacian diffusion on a scalar field (WritePingPong).
/// UV-index scheme: new = C + DiffusionRate * DeltaTime * (N+S+E+W-4C).
/// Empirically keep DiffusionRate * DeltaTime ≲ 0.2–0.25 or checkerboard instability appears.
/// Prefer several mild Diffuse passes over one huge rate.
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

/// <summary>
/// Synthetic multi-field proof (ADR-008 / M2c): swap two scalar ping-pong fields via Role A/B slots.
/// Not Gray-Scott — only validates FieldReadA/B + FieldWriteA/B binding.
/// </summary>
[Serializable]
public sealed class SwapFieldsPass : FieldKernelPass
{
    [SerializeField] private string fieldNameA = "fieldA";
    [SerializeField] private string fieldNameB = "fieldB";

    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string FieldNameA
    {
        get => fieldNameA;
        set => fieldNameA = value;
    }

    public string FieldNameB
    {
        get => fieldNameB;
        set => fieldNameB = value;
    }

    public override string DisplayName => "Swap Fields";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "SwapFields";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Pair(
            ref fieldWritesCache,
            fieldNameA, FieldSlotRole.A, FieldAccess.WritePingPong, FieldSemantic.Scalar, 1,
            fieldNameB, FieldSlotRole.B, FieldAccess.WritePingPong, FieldSemantic.Scalar, 1);
}

/// <summary>
/// Gray-Scott reaction-diffusion on two scalar fields (ADR-009 / M2c.1).
/// U=RoleA, V=RoleB, WritePingPong; simultaneous snapshot update in one kernel.
/// </summary>
[Serializable]
public sealed class GrayScottPass : FieldKernelPass
{
    private static readonly int GrayScottDuId = Shader.PropertyToID("GrayScottDu");
    private static readonly int GrayScottDvId = Shader.PropertyToID("GrayScottDv");
    private static readonly int GrayScottFeedId = Shader.PropertyToID("GrayScottFeed");
    private static readonly int GrayScottKillId = Shader.PropertyToID("GrayScottKill");

    [SerializeField] private string fieldNameU = "U";
    [SerializeField] private string fieldNameV = "V";
    [SerializeField, Min(0f)] private float diffusionRateU = 0.16f;
    [SerializeField, Min(0f)] private float diffusionRateV = 0.08f;
    [SerializeField, Min(0f)] private float feedRate = 0.035f;
    [SerializeField, Min(0f)] private float killRate = 0.06f;

    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string FieldNameU
    {
        get => fieldNameU;
        set => fieldNameU = value;
    }

    public string FieldNameV
    {
        get => fieldNameV;
        set => fieldNameV = value;
    }

    public float DiffusionRateU
    {
        get => diffusionRateU;
        set => diffusionRateU = value;
    }

    public float DiffusionRateV
    {
        get => diffusionRateV;
        set => diffusionRateV = value;
    }

    public float FeedRate
    {
        get => feedRate;
        set => feedRate = value;
    }

    public float KillRate
    {
        get => killRate;
        set => killRate = value;
    }

    public override string DisplayName => "Gray-Scott Reaction-Diffusion";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "GrayScottReact";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Pair(
            ref fieldWritesCache,
            fieldNameU, FieldSlotRole.A, FieldAccess.WritePingPong, FieldSemantic.Scalar, 1,
            fieldNameV, FieldSlotRole.B, FieldAccess.WritePingPong, FieldSemantic.Scalar, 1);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
        SetFloat(context, GrayScottDuId, diffusionRateU);
        SetFloat(context, GrayScottDvId, diffusionRateV);
        SetFloat(context, GrayScottFeedId, feedRate);
        SetFloat(context, GrayScottKillId, killRate);
    }
}

/// <summary>
/// One-shot UV disk seed into a scalar field (WriteInPlace). Fires once per Initialize/Rebuild
/// via <see cref="FieldKernelPass.ShouldDispatch"/> + hasFired (ADR-009 / M2c.1).
/// </summary>
[Serializable]
public sealed class SeedScalarDiskPass : FieldKernelPass
{
    private static readonly int SeedCenterUVId = Shader.PropertyToID("SeedCenterUV");
    private static readonly int SeedRadiusUVId = Shader.PropertyToID("SeedRadiusUV");
    private static readonly int SeedValueId = Shader.PropertyToID("SeedValue");

    [SerializeField] private string fieldName = "V";
    [SerializeField] private Vector2 centerUV = new Vector2(0.5f, 0.5f);
    [SerializeField, Min(0f)] private float radiusUV = 0.06f;
    [SerializeField] private float value = 1f;

    [NonSerialized] private FieldRequest[] fieldWritesCache;
    [NonSerialized] private bool hasFired;

    public string FieldName
    {
        get => fieldName;
        set => fieldName = value;
    }

    public Vector2 CenterUV
    {
        get => centerUV;
        set => centerUV = value;
    }

    public float RadiusUV
    {
        get => radiusUV;
        set => radiusUV = value;
    }

    public float Value
    {
        get => value;
        set => this.value = value;
    }

    public override string DisplayName => "Seed Scalar Disk";
    public override PassCategory Category => PassCategory.Emit;
    protected override string KernelName => "SeedScalarDisk";
    protected override bool ShouldDispatch => !hasFired;

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, fieldName,
            FieldAccess.WriteInPlace, FieldSemantic.Scalar, 1);

    public override void Initialize(SimContext context)
    {
        base.Initialize(context);
        hasFired = false;
    }

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetVector(context, SeedCenterUVId, new Vector3(centerUV.x, centerUV.y, 0f));
        SetFloat(context, SeedRadiusUVId, radiusUV);
        SetFloat(context, SeedValueId, value);
        // Guard already passed ShouldDispatch; dispatch will run after this call.
        hasFired = true;
    }
}

