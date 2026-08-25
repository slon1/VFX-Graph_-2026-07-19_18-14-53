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
/// Reynolds-style alignment: steer particle velocity toward the locally sampled field
/// velocity (v += (fieldVel - v) * strength * dt), not accumulate onto it. Self-limiting —
/// unlike SampleVelocityFieldPass (Transport, no dt, used by Hybrid/Echo demos), this pass
/// is Force/dt-scaled by design and must not be merged with SampleVelocityFieldPass:
/// different contract, different consumers.
/// </summary>
[Serializable]
public sealed class SteerToVelocityFieldPass : ParticleKernelPass
{
    private static readonly int SteerStrengthId = Shader.PropertyToID("SteerStrength");

    [SerializeField] private string velocityFieldName = "flockVel";
    [SerializeField] private float strength = 1f;

    private FieldDescriptor fieldDescriptor;
    private int velocityReadId;

    [NonSerialized] private FieldRequest[] fieldReadsCache;

    public string VelocityFieldName
    {
        get => velocityFieldName;
        set => velocityFieldName = value;
    }

    public float Strength
    {
        get => strength;
        set => strength = value;
    }

    public override string DisplayName => "Steer To Velocity Field";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "SteerToVelocityField";
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
        SetFloat(context, SteerStrengthId, strength);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}

/// <summary>
/// Alignment as unit field velocity direction * weight (no dt). ADR-012 kinematic boids —
/// not SteerToVelocityField (Reynolds) or SampleVelocityField (Transport).
/// </summary>
[Serializable]
public sealed class AddNormalizedVelocityFieldPass : ParticleKernelPass
{
    private static readonly int AddWeightId = Shader.PropertyToID("AddWeight");

    [SerializeField] private string velocityFieldName = "flockVel";
    [SerializeField] private float weight = 0.8f;

    private FieldDescriptor fieldDescriptor;
    private int velocityReadId;

    [NonSerialized] private FieldRequest[] fieldReadsCache;

    public string VelocityFieldName
    {
        get => velocityFieldName;
        set => velocityFieldName = value;
    }

    public float Weight
    {
        get => weight;
        set => weight = value;
    }

    public override string DisplayName => "Add Normalized Velocity Field";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "AddNormalizedVelocityField";
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
        SetFloat(context, AddWeightId, weight);
    }
}

/// <summary>
/// Explicit 5-point Laplacian diffusion on a 2-channel velocity field (WritePingPong).
/// Same CFL rule as DiffuseFieldPass (rate * dt ≲ 0.2-0.25), applied per-component —
/// Laplacian is separable, no cross-channel coupling. Default matches cohesionDensity's
/// diffusionRate (0.15) — a lower rate does not accumulate a real averaging radius over
/// a realistic pass count at moderate SimulationSpeed (ADR-011 Дефект 3 diffusion-length math).
/// </summary>
[Serializable]
public sealed class DiffuseVelocityFieldPass : FieldKernelPass
{
    [SerializeField] private string fieldName = "flockVel";
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

    public override string DisplayName => "Diffuse Velocity Field";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "DiffuseVelocityField";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, fieldName,
            FieldAccess.WritePingPong, FieldSemantic.Velocity, 2);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
        SetFloat(context, SimShaderIds.DiffusionRate, diffusionRate);
    }
}

/// <summary>
/// Semi-Lagrangian self-advection of a 2-channel velocity field (WritePingPong).
/// Backtrace: sample at uv - vel*dt/FieldSize, clamp UV (Neumann-like border).
/// DissipationRate follows Decay: GPU multiplies by exp(-rate * dt) computed on CPU; 0 = off.
/// </summary>
[Serializable]
public sealed class AdvectVelocityFieldPass : FieldKernelPass
{
    [SerializeField] private string fieldName = "flockVel";
    [SerializeField, Min(0f)] private float dissipationRate = 0f;

    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string FieldName
    {
        get => fieldName;
        set => fieldName = value;
    }

    public float DissipationRate
    {
        get => dissipationRate;
        set => dissipationRate = value;
    }

    public override string DisplayName => "Advect Velocity Field";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "AdvectVelocityField";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, fieldName,
            FieldAccess.WritePingPong, FieldSemantic.Velocity, 2);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
        SetFloat(context, SimShaderIds.Dissipation, Mathf.Exp(-dissipationRate * deltaTime));
    }
}

/// <summary>
/// Passive scalar tracer: dye_next = sample(dye, saturate(uv − u·dt/Size)) * Dissipation.
/// WritePingPong Role A on dye; velocity is Read Role B (not rewritten).
/// </summary>
[Serializable]
public sealed class AdvectScalarPass : FieldKernelPass
{
    [SerializeField] private string scalarField = "dye";
    [SerializeField] private string velocityField = "velocity";
    [SerializeField, Min(0f)] private float dissipationRate = 0f;

    [NonSerialized] private FieldRequest[] fieldReadsCache;
    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string ScalarField
    {
        get => scalarField;
        set => scalarField = value;
    }

    public string VelocityField
    {
        get => velocityField;
        set => velocityField = value;
    }

    public float DissipationRate
    {
        get => dissipationRate;
        set => dissipationRate = value;
    }

    public override string DisplayName => "Advect Scalar";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "AdvectScalar";
    public override bool RequiresSquareTexel => false;

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, velocityField,
            FieldAccess.Read, FieldSemantic.Velocity, 2, FieldSlotRole.B);

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, scalarField,
            FieldAccess.WritePingPong, FieldSemantic.Scalar, 1, FieldSlotRole.A);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
        SetFloat(context, SimShaderIds.Dissipation, Mathf.Exp(-dissipationRate * deltaTime));
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
/// Cohesion/separation as unit gradient direction * weight (no dt). ADR-012 kinematic boids —
/// not SampleGradientField (raw ∇ * strength * dt).
/// </summary>
[Serializable]
public sealed class AddNormalizedGradientFieldPass : ParticleKernelPass
{
    private static readonly int AddWeightId = Shader.PropertyToID("AddWeight");

    [SerializeField] private string fieldName = "density";
    [SerializeField] private float weight = 0.6f;

    private FieldDescriptor fieldDescriptor;
    private int fieldReadId;

    [NonSerialized] private FieldRequest[] fieldReadsCache;

    public string FieldName
    {
        get => fieldName;
        set => fieldName = value;
    }

    public float Weight
    {
        get => weight;
        set => weight = value;
    }

    public override string DisplayName => "Add Normalized Gradient Field";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "AddNormalizedGradient";
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
        SetFloat(context, AddWeightId, weight);
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

/// <summary>
/// Touch/cursor paints Gray-Scott catalyst: raise V and erode U in the touch radius
/// (WriteInPlace Role A/B). Uses InputRouter TouchForce radius/strength; no pass-local brush params.
/// Place after GrayScottPass in the pipeline so touch overrides this frame's reaction.
/// </summary>
[Serializable]
public sealed class TouchInjectGrayScottPass : FieldKernelPass
{
    [SerializeField] private string fieldNameU = "U";
    [SerializeField] private string fieldNameV = "V";

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

    public override string DisplayName => "Touch Inject Gray-Scott";
    public override PassCategory Category => PassCategory.Emit;
    protected override string KernelName => "TouchInjectGrayScott";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Pair(
            ref fieldWritesCache,
            fieldNameU, FieldSlotRole.A, FieldAccess.WriteInPlace, FieldSemantic.Scalar, 1,
            fieldNameV, FieldSlotRole.B, FieldAccess.WriteInPlace, FieldSemantic.Scalar, 1);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        BindBuffer(context, SimShaderIds.Touches, context.TouchBuffer);
        SetInt(context, SimShaderIds.TouchCount, context.TouchCount);
    }
}

/// <summary>
/// Raise target scalar (default V) from agent presence: max(target, saturate(presence * gain)).
/// Read Role A + WriteInPlace Role B. Place after GrayScottPass.
/// </summary>
[Serializable]
public sealed class AgentBoostFieldPass : FieldKernelPass
{
    private static readonly int GainId = Shader.PropertyToID("Gain");

    [SerializeField] private string sourceFieldName = "agentPresence";
    [SerializeField] private string targetFieldName = "V";
    [SerializeField, Min(0f)] private float gain = 0.3f;

    [NonSerialized] private FieldRequest[] fieldReadsCache;
    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string SourceFieldName
    {
        get => sourceFieldName;
        set => sourceFieldName = value;
    }

    public string TargetFieldName
    {
        get => targetFieldName;
        set => targetFieldName = value;
    }

    public float Gain
    {
        get => gain;
        set => gain = value;
    }

    public override string DisplayName => "Agent Boost Field";
    public override PassCategory Category => PassCategory.Emit;
    protected override string KernelName => "AgentBoostField";

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, sourceFieldName,
            FieldAccess.Read, FieldSemantic.Scalar, 1, FieldSlotRole.A);

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, targetFieldName,
            FieldAccess.WriteInPlace, FieldSemantic.Scalar, 1, FieldSlotRole.B);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, GainId, gain);
    }
}

/// <summary>
/// Erode target scalar (default U) from agent presence: target *= (1 - saturate(presence * gain)).
/// Read Role A + WriteInPlace Role B. Place after GrayScottPass (typically after Boost).
/// </summary>
[Serializable]
public sealed class AgentErodeFieldPass : FieldKernelPass
{
    private static readonly int GainId = Shader.PropertyToID("Gain");

    [SerializeField] private string sourceFieldName = "agentPresence";
    [SerializeField] private string targetFieldName = "U";
    [SerializeField, Min(0f)] private float gain = 0.3f;

    [NonSerialized] private FieldRequest[] fieldReadsCache;
    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string SourceFieldName
    {
        get => sourceFieldName;
        set => sourceFieldName = value;
    }

    public string TargetFieldName
    {
        get => targetFieldName;
        set => targetFieldName = value;
    }

    public float Gain
    {
        get => gain;
        set => gain = value;
    }

    public override string DisplayName => "Agent Erode Field";
    public override PassCategory Category => PassCategory.Emit;
    protected override string KernelName => "AgentErodeField";

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, sourceFieldName,
            FieldAccess.Read, FieldSemantic.Scalar, 1, FieldSlotRole.A);

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, targetFieldName,
            FieldAccess.WriteInPlace, FieldSemantic.Scalar, 1, FieldSlotRole.B);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, GainId, gain);
    }
}

