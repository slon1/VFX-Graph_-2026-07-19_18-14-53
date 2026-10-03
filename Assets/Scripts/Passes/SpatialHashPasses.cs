using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Marks passes that read SimContext.SpatialHash; validated to sit after the builder (ADR-034 §1).</summary>
internal interface ISpatialHashConsumer
{
}

/// <summary>
/// Builds the XZ spatial hash and a cell-sorted snapshot of position/heading/teamId (ADR-034).
/// Owns the SpatialHashSet; six kernels per Execute.
/// </summary>
[Serializable]
public sealed class BuildSpatialHashPass : SimPass, IDisposable
{
    private const int ThreadGroupSize = 64;

    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField] private Vector3 extents = new Vector3(16f, 0f, 16f);
    [SerializeField, Min(1e-3f)] private float minCellSize = 2f;
    [SerializeField] private bool wrap = true;

    [NonSerialized] private SpatialHashSet hash;
    [NonSerialized] private KernelHandle clearKernel;
    [NonSerialized] private KernelHandle countKernel;
    [NonSerialized] private KernelHandle scanBlocksKernel;
    [NonSerialized] private KernelHandle scanBlockSumsKernel;
    [NonSerialized] private KernelHandle addOffsetsKernel;
    [NonSerialized] private KernelHandle scatterKernel;

    public Vector3 Center
    {
        get => center;
        set => center = value;
    }

    public Vector3 Extents
    {
        get => extents;
        set => extents = value;
    }

    public float MinCellSize
    {
        get => minCellSize;
        set => minCellSize = value;
    }

    public bool Wrap
    {
        get => wrap;
        set => wrap = value;
    }

    internal SpatialHashSet Hash => hash;

    public override string DisplayName => "Build Spatial Hash";
    public override PassCategory Category => PassCategory.Emit;
    public override System.Collections.Generic.IReadOnlyList<AttributeId> Reads => AttrSets.PositionHeadingTeam;
    public override System.Collections.Generic.IReadOnlyList<AttributeId> Writes => AttrSets.None;

    public override void Initialize(SimContext context)
    {
        if (context.SpatialHash != null)
        {
            throw new InvalidOperationException(
                "SimulationWorld: only one enabled 'Build Spatial Hash' pass per effect.");
        }

        clearKernel = context.FindKernel("HashClear");
        countKernel = context.FindKernel("HashCount");
        scanBlocksKernel = context.FindKernel("HashScanBlocks");
        scanBlockSumsKernel = context.FindKernel("HashScanBlockSums");
        addOffsetsKernel = context.FindKernel("HashAddOffsets");
        scatterKernel = context.FindKernel("HashScatter");

        ComputeShader shader = clearKernel.Shader;
        if (countKernel.Shader != shader ||
            scanBlocksKernel.Shader != shader ||
            scanBlockSumsKernel.Shader != shader ||
            addOffsetsKernel.Shader != shader ||
            scatterKernel.Shader != shader)
        {
            throw new InvalidOperationException(
                "Build Spatial Hash kernels must share one compute shader; uniforms are set once.");
        }

        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(center, extents, minCellSize);
        SpatialHashSet.ValidateLayout(layout, wrap);

        int count = context.Particles != null ? context.Particles.Count : 0;
        if (count > 0)
        {
            RequireAttribute(context, BuiltinAttributes.Position);
            RequireAttribute(context, BuiltinAttributes.Heading);
            RequireAttribute(context, BuiltinAttributes.TeamId);
        }

        Dispose();
        hash = new SpatialHashSet(layout, wrap, count);
        context.SpatialHash = hash;
    }

    public override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;
        if (hash == null || hash.ParticleCount == 0 || !KernelsValid())
        {
            return;
        }

        ComputeShader shader = clearKernel.Shader;
        CommandBuffer cmd = context.Cmd;
        hash.PushParams(cmd, shader);
        cmd.SetComputeIntParam(shader, SimShaderIds.ParticleCount, hash.ParticleCount);

        GraphicsBuffer positions = context.Particles.Get(BuiltinAttributes.Position);
        GraphicsBuffer headings = context.Particles.Get(BuiltinAttributes.Heading);
        GraphicsBuffer teams = context.Particles.Get(BuiltinAttributes.TeamId);

        Bind(cmd, clearKernel, "CellCounts", hash.CellCounts);

        Bind(cmd, countKernel, "position", positions);
        Bind(cmd, countKernel, "ParticleCells", hash.ParticleCells);
        Bind(cmd, countKernel, "CellCounts", hash.CellCounts);

        Bind(cmd, scanBlocksKernel, "CellCounts", hash.CellCounts);
        Bind(cmd, scanBlocksKernel, "CellStarts", hash.CellStarts);
        Bind(cmd, scanBlocksKernel, "BlockSums", hash.BlockSums);

        Bind(cmd, scanBlockSumsKernel, "BlockSums", hash.BlockSums);
        Bind(cmd, scanBlockSumsKernel, "BlockOffsets", hash.BlockOffsets);

        Bind(cmd, addOffsetsKernel, "CellStarts", hash.CellStarts);
        Bind(cmd, addOffsetsKernel, "CellCursors", hash.CellCursors);
        Bind(cmd, addOffsetsKernel, "BlockOffsets", hash.BlockOffsets);

        Bind(cmd, scatterKernel, "position", positions);
        Bind(cmd, scatterKernel, "heading", headings);
        Bind(cmd, scatterKernel, "teamId", teams);
        Bind(cmd, scatterKernel, "ParticleCells", hash.ParticleCells);
        Bind(cmd, scatterKernel, "CellCursors", hash.CellCursors);
        Bind(cmd, scatterKernel, "SortedIndices", hash.SortedIndices);
        Bind(cmd, scatterKernel, "SortedPositions", hash.SortedPositions);
        Bind(cmd, scatterKernel, "SortedDirections", hash.SortedDirections);
        Bind(cmd, scatterKernel, "SortedTeams", hash.SortedTeams);

        int cellGroups = (hash.Layout.CellCount + ThreadGroupSize - 1) / ThreadGroupSize;
        int particleGroups = (hash.ParticleCount + ThreadGroupSize - 1) / ThreadGroupSize;
        int blocks = hash.Layout.BlockCount;

        cmd.DispatchCompute(clearKernel.Shader, clearKernel.Index, cellGroups, 1, 1);
        cmd.DispatchCompute(countKernel.Shader, countKernel.Index, particleGroups, 1, 1);
        cmd.DispatchCompute(scanBlocksKernel.Shader, scanBlocksKernel.Index, blocks, 1, 1);
        cmd.DispatchCompute(scanBlockSumsKernel.Shader, scanBlockSumsKernel.Index, 1, 1, 1);
        cmd.DispatchCompute(addOffsetsKernel.Shader, addOffsetsKernel.Index, blocks, 1, 1);
        cmd.DispatchCompute(scatterKernel.Shader, scatterKernel.Index, particleGroups, 1, 1);
        LastExecuteDispatched = true;
    }

    public void Dispose()
    {
        hash?.Dispose();
        hash = null;
    }

    private bool KernelsValid()
    {
        return clearKernel.IsValid &&
            countKernel.IsValid &&
            scanBlocksKernel.IsValid &&
            scanBlockSumsKernel.IsValid &&
            addOffsetsKernel.IsValid &&
            scatterKernel.IsValid;
    }

    private static void RequireAttribute(SimContext context, AttributeId id)
    {
        if (!context.Particles.TryGet(id, out _))
        {
            throw new InvalidOperationException(
                $"Build Spatial Hash requires attribute '{id.Name}'.");
        }
    }

    private static void Bind(CommandBuffer cmd, KernelHandle kernel, string name, GraphicsBuffer buffer)
    {
        cmd.SetComputeBufferParam(kernel.Shader, kernel.Index, name, buffer);
    }
}
