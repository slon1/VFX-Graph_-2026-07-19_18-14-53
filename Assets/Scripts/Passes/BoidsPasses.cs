using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Adds separation, alignment and cohesion from the spatial-hash snapshot (ADR-038).
/// Does not read live heading. Does not steer.
/// </summary>
[Serializable]
public sealed class BoidNeighborForcePass : ParticleKernelPass, ISpatialHashConsumer, IDisposable
{
    public const string ReadTeamParamsKernelName = "BoidReadTeamParams";
    private const int CapWindow = 60;
    private const int MaxInflight = 4;

    private static readonly AttributeId[] ReadPositionTeam =
        { BuiltinAttributes.Position, BuiltinAttributes.TeamId };

    private static readonly int TeamsId = Shader.PropertyToID("Teams");
    private static readonly int SortedPositionsId = Shader.PropertyToID("SortedPositions");
    private static readonly int SortedDirectionsId = Shader.PropertyToID("SortedDirections");
    private static readonly int SortedPackedId = Shader.PropertyToID("SortedPacked");
    private static readonly int SortedTeamsId = Shader.PropertyToID("SortedTeams");
    private static readonly int SortedIndicesId = Shader.PropertyToID("SortedIndices");
    private static readonly int CellStartsId = Shader.PropertyToID("CellStarts");
    private static readonly int CellCountsId = Shader.PropertyToID("CellCounts");
    private static readonly int MaxNeighborsId = Shader.PropertyToID("MaxNeighbors");
    private static readonly int CountCapHitsId = Shader.PropertyToID("CountCapHits");
    private static readonly int CapHitsId = Shader.PropertyToID("CapHits");

    [SerializeField] private int maxNeighbors = 48;
    [SerializeField] private bool countCapHits;

    // Default 1. Modes 0 and 2 are frozen (ADR-048).
    [SerializeField] private int walkMode = 1;

    private readonly uint[] capClear = new uint[1];
    private readonly List<float> capWindow = new List<float>(CapWindow);

    private GraphicsBuffer capCounter;
    private KernelHandle sortedKernel;
    private KernelHandle packedKernel;
    private SpatialHashSet spatialHash;
    private int generation;
    private int inflight;
    private bool disposed;
    private int packedMissingStreak;
    private bool packedMissingWarned;
    private bool packedKernelMissingWarned;
    private int effectiveWalkMode;
    private bool radiusWarned;
    private float warnedRadius;

    public int MaxNeighbors
    {
        get => maxNeighbors;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "maxNeighbors must be >= 0.");
            }

            maxNeighbors = value;
        }
    }

    // Requested walk. Default 1. Modes 0 and 2 are frozen (ADR-048).
    public int WalkMode
    {
        get => walkMode;
        set
        {
            if (value < 0 || value > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "walkMode must be 0, 1, or 2.");
            }

            walkMode = value;
            if (spatialHash != null)
            {
                spatialHash.PackedRequested = value == 2;
            }
        }
    }

    /// <summary>Walk that the last <see cref="SelectKernel"/> actually ran: 0, 1, or 2.</summary>
    public int EffectiveWalkMode => effectiveWalkMode;

    public bool CountCapHits
    {
        get => countCapHits;
        set => countCapHits = value;
    }

    /// <summary>Cap-hit counter. Tests write it directly. Null before Initialize and after Dispose.</summary>
    internal GraphicsBuffer CapCounter => capCounter;

    /// <summary>Mean of the last received cap-hit fractions. -1 when the flag is off or no sample has landed.</summary>
    public float LastCapHitFraction
    {
        get
        {
            if (!countCapHits || capWindow.Count == 0)
            {
                return -1f;
            }

            float sum = 0f;
            for (int i = 0; i < capWindow.Count; i++)
            {
                sum += capWindow[i];
            }

            return sum / capWindow.Count;
        }
    }

    public override string DisplayName => "Boid Neighbor Force";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "BoidNeighborForce";
    public override IReadOnlyList<AttributeId> Reads => ReadPositionTeam;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    public override void Initialize(SimContext context)
    {
        if (context.SpatialHash == null)
        {
            throw new InvalidOperationException(
                "SimulationWorld: Boid Neighbor Force requires an enabled 'Build Spatial Hash' pass.");
        }

        if (context.Teams == null)
        {
            throw new InvalidOperationException(
                "SimulationWorld: Boid Neighbor Force requires a non-empty team list.");
        }

        base.Initialize(context);
        spatialHash = context.SpatialHash;
        spatialHash.PackedRequested = walkMode == 2;
        sortedKernel = context.FindKernel("BoidNeighborForceSorted");
        packedKernel = FindPackedKernel(context);
        if (capCounter != null)
        {
            capCounter.Release();
            capCounter = null;
        }

        capCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
        disposed = false;
        generation++;
    }

    public void ResetCapHitWindow()
    {
        capWindow.Clear();
        generation++;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        generation++;
        if (inflight > 0)
        {
            AsyncGPUReadback.WaitAllRequests();
        }

        if (capCounter != null)
        {
            capCounter.Release();
            capCounter = null;
        }
    }

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SpatialHashSet hash = context.SpatialHash;
        BindBuffer(context, TeamsId, context.Teams);
        BindBuffer(context, CellStartsId, hash.CellStarts);
        BindBuffer(context, CellCountsId, hash.CellCounts);
        bool packed = packedKernel.IsValid && Kernel.Shader == packedKernel.Shader && Kernel.Index == packedKernel.Index;
        if (packed)
        {
            BindBuffer(context, SortedPackedId, hash.SortedPacked);
            BindBuffer(context, SortedIndicesId, hash.SortedIndices);
        }
        else
        {
            BindBuffer(context, SortedPositionsId, hash.SortedPositions);
            BindBuffer(context, SortedDirectionsId, hash.SortedDirections);
            BindBuffer(context, SortedTeamsId, hash.SortedTeams);
            if (Kernel.Shader == sortedKernel.Shader && Kernel.Index == sortedKernel.Index)
            {
                BindBuffer(context, SortedIndicesId, hash.SortedIndices);
            }
        }

        hash.PushParams(context.Cmd, Kernel.Shader);
        SetInt(context, MaxNeighborsId, maxNeighbors);
        BindBuffer(context, CapHitsId, capCounter);
        SetInt(context, CountCapHitsId, countCapHits ? 1 : 0);
        if (countCapHits)
        {
            capClear[0] = 0u;
            context.Cmd.SetBufferData(capCounter, capClear);
        }
    }

    protected override KernelHandle SelectKernel(SimContext context)
    {
        bool radiusBlocks = RadiusBlocksSortedWalk(context);
        bool packedWritten = spatialHash != null && spatialHash.PackedWritten;
        NotePackedFallback(packedKernel.IsValid, radiusBlocks, packedWritten);
        if (walkMode == 2 && packedKernel.IsValid && !radiusBlocks && packedWritten)
        {
            effectiveWalkMode = 2;
            return packedKernel;
        }

        if (walkMode >= 1 && sortedKernel.IsValid && !radiusBlocks)
        {
            effectiveWalkMode = 1;
            return sortedKernel;
        }

        effectiveWalkMode = 0;
        return base.SelectKernel(context);
    }

    private void NotePackedFallback(bool packedKernelOk, bool radiusBlocks, bool packedWritten)
    {
        if (walkMode != 2)
        {
            packedMissingStreak = 0;
            return;
        }

        if (!packedKernelOk)
        {
            if (!packedKernelMissingWarned)
            {
                packedKernelMissingWarned = true;
                Debug.LogWarning(
                    "BoidNeighborForce: walk 2: кернел обхода не найден, выполняется walk 1.");
            }

            return;
        }

        if (radiusBlocks)
        {
            return;
        }

        if (!packedWritten)
        {
            packedMissingStreak++;
            if (packedMissingStreak >= 3 && !packedMissingWarned)
            {
                packedMissingWarned = true;
                Debug.LogWarning(
                    "BoidNeighborForce: walk 2: пакет не пишется, выполняется walk 1.");
            }

            return;
        }

        packedMissingStreak = 0;
        packedMissingWarned = false;
    }

    private static KernelHandle FindPackedKernel(SimContext context)
    {
        try
        {
            return context.FindKernel("BoidNeighborForceSortedPacked");
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }

    private bool RadiusBlocksSortedWalk(SimContext context)
    {
        if (context == null || context.TeamMaxRadius <= 0f || context.SpatialHash == null)
        {
            return false;
        }

        Vector2 cell = context.SpatialHash.Layout.CellSize;
        float limit = Mathf.Min(cell.x, cell.y) * (1f + SpatialHashSet.GridEpsilon);
        float radius = context.TeamMaxRadius;
        if (radius <= limit)
        {
            radiusWarned = false;
            return false;
        }

        if (!radiusWarned || warnedRadius != radius)
        {
            radiusWarned = true;
            warnedRadius = radius;
            Debug.LogWarning(
                "BoidNeighborForce: max radius "
                + radius.ToString(CultureInfo.InvariantCulture)
                + " exceeds the hash cell limit "
                + limit.ToString(CultureInfo.InvariantCulture)
                + ", executing walk 0.");
        }

        return true;
    }

    protected override void OnDispatched(SimContext context)
    {
        if (!countCapHits || capCounter == null || disposed || inflight >= MaxInflight)
        {
            return;
        }

        int generationAtRequest = generation;
        int countAtRequest = context.Particles.Count;
        inflight++;
        context.Cmd.RequestAsyncReadback(
            capCounter,
            request => OnCapReadback(request, generationAtRequest, countAtRequest));
    }

    private void OnCapReadback(AsyncGPUReadbackRequest request, int generationAtRequest, int countAtRequest)
    {
        if (disposed || generationAtRequest != generation)
        {
            inflight--;
            return;
        }

        inflight--;
        if (request.hasError || countAtRequest <= 0)
        {
            return;
        }

        float fraction = request.GetData<uint>()[0] / (float)countAtRequest;
        capWindow.Add(fraction);
        if (capWindow.Count > CapWindow)
        {
            capWindow.RemoveAt(0);
        }
    }
}

/// <summary>
/// HeadingSteer with per-team cruise and turn from TeamParams.Motion (ADR-039).
/// Does not read the spatial hash.
/// </summary>
[Serializable]
public sealed class TeamHeadingSteerPass : ParticleKernelPass
{
    private static readonly AttributeId[] ReadHeadingVelocityTeam =
    {
        BuiltinAttributes.Heading,
        BuiltinAttributes.Velocity,
        BuiltinAttributes.TeamId,
    };

    private static readonly AttributeId[] WriteHeadingVelocity =
    {
        BuiltinAttributes.Heading,
        BuiltinAttributes.Velocity,
    };

    private static readonly int TeamsId = Shader.PropertyToID("Teams");

    public override string DisplayName => "Team Heading Steer";
    public override PassCategory Category => PassCategory.Dynamics;
    protected override string KernelName => "TeamHeadingSteer";
    public override IReadOnlyList<AttributeId> Reads => ReadHeadingVelocityTeam;
    public override IReadOnlyList<AttributeId> Writes => WriteHeadingVelocity;

    public override void Initialize(SimContext context)
    {
        if (context.Teams == null)
        {
            throw new InvalidOperationException(
                "SimulationWorld: Team Heading Steer requires a non-empty team list.");
        }

        base.Initialize(context);
    }

    protected override void SetParams(SimContext context, float deltaTime)
    {
        BindBuffer(context, TeamsId, context.Teams);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}
