using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Writes the spatial-hash cell histogram into a scalar field (ADR-040 part A).
/// One texel per cell. The value is replaced, not accumulated.
/// </summary>
[System.Serializable]
public sealed class HashCountsToFieldPass : FieldKernelPass, ISpatialHashConsumer
{
    private static readonly int CellCountsId = Shader.PropertyToID("CellCounts");
    private const float SizeTolerance = 1e-3f;

    [SerializeField] private string fieldName = "hashCount";

    [System.NonSerialized] private FieldRequest[] fieldWritesCache;

    public string FieldName
    {
        get => fieldName;
        set => fieldName = value;
    }

    public override string DisplayName => "Hash Counts To Field";
    public override PassCategory Category => PassCategory.Emit;
    protected override string KernelName => "HashCountsToField";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, fieldName,
            FieldAccess.WriteInPlace, FieldSemantic.Scalar, 1);

    public override void Initialize(SimContext context)
    {
        if (context.SpatialHash == null)
        {
            throw new System.InvalidOperationException(
                "Hash Counts To Field requires a spatial hash.");
        }

        base.Initialize(context);

        FieldDescriptor descriptor = context.Fields.Get(fieldName).Descriptor;
        Vector2Int fieldRes = descriptor.Resolution;
        Vector2Int hashRes = context.SpatialHash.Layout.Resolution;
        if (fieldRes.x != hashRes.x || fieldRes.y != hashRes.y)
        {
            throw new System.InvalidOperationException(
                $"Hash Counts To Field: field '{fieldName}' resolution {fieldRes.x}x{fieldRes.y} " +
                $"does not match hash resolution {hashRes.x}x{hashRes.y}.");
        }

        Vector2 fieldSize = descriptor.Size;
        Vector2 hashSize = context.SpatialHash.Layout.Size;
        if (Mathf.Abs(fieldSize.x - hashSize.x) > SizeTolerance ||
            Mathf.Abs(fieldSize.y - hashSize.y) > SizeTolerance)
        {
            throw new System.InvalidOperationException(
                $"Hash Counts To Field: field '{fieldName}' resolution {fieldRes.x}x{fieldRes.y} " +
                $"size ({fieldSize.x}, {fieldSize.y}) does not match hash resolution {hashRes.x}x{hashRes.y} " +
                $"size ({hashSize.x}, {hashSize.y}).");
        }
    }

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SpatialHashSet hash = context.SpatialHash;
        BindBuffer(context, CellCountsId, hash.CellCounts);
        hash.PushParams(context.Cmd, Kernel.Shader);
    }
}
