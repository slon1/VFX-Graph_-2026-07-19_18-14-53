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
