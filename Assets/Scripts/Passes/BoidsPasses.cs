using System;
using System.Collections.Generic;
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
    private static readonly int SortedTeamsId = Shader.PropertyToID("SortedTeams");
    private static readonly int CellStartsId = Shader.PropertyToID("CellStarts");
    private static readonly int CellCountsId = Shader.PropertyToID("CellCounts");
    private static readonly int MaxNeighborsId = Shader.PropertyToID("MaxNeighbors");
    private static readonly int CountCapHitsId = Shader.PropertyToID("CountCapHits");
    private static readonly int CapHitsId = Shader.PropertyToID("CapHits");

    [SerializeField] private int maxNeighbors = 48;
    [SerializeField] private bool countCapHits;

    private readonly uint[] capClear = new uint[1];
    private readonly List<float> capWindow = new List<float>(CapWindow);

    private GraphicsBuffer capCounter;
    private int generation;
    private int inflight;
    private bool disposed;

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
        BindBuffer(context, SortedPositionsId, hash.SortedPositions);
        BindBuffer(context, SortedDirectionsId, hash.SortedDirections);
        BindBuffer(context, SortedTeamsId, hash.SortedTeams);
        BindBuffer(context, CellStartsId, hash.CellStarts);
        BindBuffer(context, CellCountsId, hash.CellCounts);
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
