using System;
using UnityEngine;
using UnityEngine.Rendering;

public readonly struct SpatialHashLayout
{
    public readonly Vector2 Origin;
    public readonly Vector2 Size;
    public readonly Vector2 CellSize;
    public readonly Vector2Int Resolution;

    public SpatialHashLayout(Vector2 origin, Vector2 size, Vector2 cellSize, Vector2Int resolution)
    {
        Origin = origin;
        Size = size;
        CellSize = cellSize;
        Resolution = resolution;
    }

    public int CellCount => Resolution.x * Resolution.y;

    public int BlockCount => (CellCount + SpatialHashSet.ScanBlockSize - 1) / SpatialHashSet.ScanBlockSize;
}

/// <summary>
/// XZ spatial hash owned by <see cref="BuildSpatialHashPass"/> (ADR-034). Not a world resource.
/// </summary>
public sealed class SpatialHashSet : IDisposable
{
    public const int ScanBlockSize = 256;
    public const int MaxCells = ScanBlockSize * ScanBlockSize;
    public const float GridEpsilon = 1e-5f;

    private static readonly int HashOriginId = Shader.PropertyToID("HashOrigin");
    private static readonly int HashSizeId = Shader.PropertyToID("HashSize");
    private static readonly int HashCellSizeId = Shader.PropertyToID("HashCellSize");
    private static readonly int HashResId = Shader.PropertyToID("HashRes");
    private static readonly int HashWrapId = Shader.PropertyToID("HashWrap");
    private static readonly int HashCellCountId = Shader.PropertyToID("HashCellCount");
    private static readonly int HashBlockCountId = Shader.PropertyToID("HashBlockCount");

    public SpatialHashLayout Layout { get; }
    public bool Wrap { get; }
    public int ParticleCount { get; }

    internal GraphicsBuffer CellCounts { get; private set; }
    internal GraphicsBuffer CellStarts { get; private set; }
    internal GraphicsBuffer CellCursors { get; private set; }
    internal GraphicsBuffer BlockSums { get; private set; }
    internal GraphicsBuffer BlockOffsets { get; private set; }
    internal GraphicsBuffer ParticleCells { get; private set; }
    internal GraphicsBuffer SortedIndices { get; private set; }
    internal GraphicsBuffer SortedPositions { get; private set; }
    internal GraphicsBuffer SortedDirections { get; private set; }
    internal GraphicsBuffer SortedTeams { get; private set; }

    public SpatialHashSet(SpatialHashLayout layout, bool wrap, int particleCount)
    {
        if (particleCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(particleCount), particleCount, "particleCount must be >= 0.");
        }

        Layout = layout;
        Wrap = wrap;
        ParticleCount = particleCount;

        int cells = layout.CellCount;
        CellCounts = NewUint(cells);
        CellStarts = NewUint(cells);
        CellCursors = NewUint(cells);
        BlockSums = NewUint(ScanBlockSize);
        BlockOffsets = NewUint(ScanBlockSize);

        if (particleCount > 0)
        {
            ParticleCells = NewUint(particleCount);
            SortedIndices = NewUint(particleCount);
            SortedPositions = NewFloat2(particleCount);
            SortedDirections = NewFloat2(particleCount);
            SortedTeams = NewUint(particleCount);
        }
    }

    public static SpatialHashLayout ComputeLayout(Vector3 center, Vector3 extents, float minCellSize)
    {
        if (!IsPositiveFinite(minCellSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minCellSize), minCellSize, "minCellSize must be finite and > 0.");
        }

        if (!IsPositiveFinite(extents.x) || !IsPositiveFinite(extents.z))
        {
            throw new ArgumentOutOfRangeException(
                nameof(extents), extents, "extents.x and extents.z must be finite and > 0.");
        }

        double sizeX = 2.0 * extents.x;
        double sizeZ = 2.0 * extents.z;
        int resX = AxisResolution(sizeX, minCellSize);
        int resZ = AxisResolution(sizeZ, minCellSize);
        return new SpatialHashLayout(
            new Vector2(center.x - extents.x, center.z - extents.z),
            new Vector2((float)sizeX, (float)sizeZ),
            new Vector2((float)(sizeX / resX), (float)(sizeZ / resZ)),
            new Vector2Int(resX, resZ));
    }

    public static void ValidateLayout(SpatialHashLayout layout, bool wrap)
    {
        long cells = (long)layout.Resolution.x * layout.Resolution.y;
        if (cells > MaxCells)
        {
            throw new InvalidOperationException(
                $"Spatial hash {layout.Resolution.x}x{layout.Resolution.y} = {cells} cells exceeds {MaxCells}.");
        }

        if (wrap && (layout.Resolution.x < 3 || layout.Resolution.y < 3))
        {
            throw new InvalidOperationException(
                "wrap needs at least 3 cells per axis; the wrapped neighbour cells would repeat " +
                $"(res {layout.Resolution.x}x{layout.Resolution.y}).");
        }
    }

    internal void PushParams(CommandBuffer cmd, ComputeShader shader)
    {
        SpatialHashLayout layout = Layout;
        cmd.SetComputeVectorParam(shader, HashOriginId, new Vector4(layout.Origin.x, layout.Origin.y, 0f, 0f));
        cmd.SetComputeVectorParam(shader, HashSizeId, new Vector4(layout.Size.x, layout.Size.y, 0f, 0f));
        cmd.SetComputeVectorParam(shader, HashCellSizeId, new Vector4(layout.CellSize.x, layout.CellSize.y, 0f, 0f));
        cmd.SetComputeIntParams(shader, HashResId, layout.Resolution.x, layout.Resolution.y, 0, 0);
        cmd.SetComputeIntParam(shader, HashWrapId, Wrap ? 1 : 0);
        cmd.SetComputeIntParam(shader, HashCellCountId, layout.CellCount);
        cmd.SetComputeIntParam(shader, HashBlockCountId, layout.BlockCount);
    }

    public void Dispose()
    {
        CellCounts = Release(CellCounts);
        CellStarts = Release(CellStarts);
        CellCursors = Release(CellCursors);
        BlockSums = Release(BlockSums);
        BlockOffsets = Release(BlockOffsets);
        ParticleCells = Release(ParticleCells);
        SortedIndices = Release(SortedIndices);
        SortedPositions = Release(SortedPositions);
        SortedDirections = Release(SortedDirections);
        SortedTeams = Release(SortedTeams);
    }

    private static int AxisResolution(double size, float minCellSize)
    {
        double raw = Math.Floor(size / minCellSize * (1.0 + GridEpsilon));
        if (raw < 1.0)
        {
            raw = 1.0;
        }

        if (raw > MaxCells + 1)
        {
            raw = MaxCells + 1;
        }

        return (int)raw;
    }

    private static bool IsPositiveFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }

    private static GraphicsBuffer NewUint(int count)
    {
        return new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
    }

    private static GraphicsBuffer NewFloat2(int count)
    {
        return new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 2);
    }

    private static GraphicsBuffer Release(GraphicsBuffer buffer)
    {
        buffer?.Release();
        return null;
    }
}
