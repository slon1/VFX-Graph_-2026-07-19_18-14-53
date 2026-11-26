using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Near-field density pressure on a fine count grid (ADR-062 §2.10).
/// Runs every frame. Diagnostic repeats 2..N of <see cref="FieldRepeatStage.All"/> or
/// <see cref="FieldRepeatStage.Press"/> write scratch buffers. <see cref="SimPass.RepeatCount"/> stays 1.
/// </summary>
[Serializable]
public sealed class FieldDensityPressurePass : SimPass, IDisposable, ISerializationCallbackReceiver
{
    public const int MaxCells = 4194304;
    private const int ThreadGroupSize = 64;
    private const float FixedScale = 16384f;

    private static readonly int ParticleCountId = UnityEngine.Shader.PropertyToID("ParticleCount");
    private static readonly int CellsId = UnityEngine.Shader.PropertyToID("PressureCells");
    private static readonly int OriginId = UnityEngine.Shader.PropertyToID("PressureOrigin");
    private static readonly int CellSizeId = UnityEngine.Shader.PropertyToID("PressureCellSize");
    private static readonly int ResId = UnityEngine.Shader.PropertyToID("PressureRes");
    private static readonly int WeightId = UnityEngine.Shader.PropertyToID("PressureWeight");
    private static readonly int FloorId = UnityEngine.Shader.PropertyToID("PressureG0");
    private static readonly int SortedOrderId = UnityEngine.Shader.PropertyToID("SortedOrder");
    private static readonly int FrameIndexId = UnityEngine.Shader.PropertyToID("FrameIndex");
    private static readonly int ForceInterleaveId = UnityEngine.Shader.PropertyToID("ForceInterleave");
    private static readonly int WriteStoredId = UnityEngine.Shader.PropertyToID("WriteStored");

    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField] private Vector3 extents = new Vector3(45f, 0f, 45f);
    [SerializeField, Min(1e-4f)] private float pressureTexel = 0.25f;
    [SerializeField, Min(0f)] private float weight = 0.8f;
    [SerializeField, Min(1e-4f)] private float g0 = 40f;
    [SerializeField] private bool jitter = true;
    [SerializeField] private int jitterSeed = 1;
    [SerializeField] private bool sortedOrder;
    [SerializeField] private int gridUpdateInterval = 1;
    [SerializeField] private int forceInterleave = 1;

    [NonSerialized] private KernelHandle clearKernel;
    [NonSerialized] private KernelHandle scatterKernel;
    [NonSerialized] private KernelHandle gradientKernel;
    [NonSerialized] private KernelHandle forceKernel;
    [NonSerialized] private int fieldRepeatCount = 1;
    [NonSerialized] private int frameIndex;
    [NonSerialized] private GraphicsBuffer density;
    [NonSerialized] private GraphicsBuffer grad;
    [NonSerialized] private GraphicsBuffer scratchDensity;
    [NonSerialized] private GraphicsBuffer scratchGrad;
    [NonSerialized] private GraphicsBuffer scratchVelocity;
    [NonSerialized] private Vector3[] scratchZeros;
    [NonSerialized] private GraphicsBuffer liveVelocity;
    [NonSerialized] private GraphicsBuffer indexDummy;
    [NonSerialized] private GraphicsBuffer storedForce;
    [NonSerialized] private GraphicsBuffer storedDummy;
    [NonSerialized] private GraphicsBuffer fresh;
    [NonSerialized] private GraphicsBuffer freshDummy;
    [NonSerialized] private uint[] freshZeros;

    public override string DisplayName => "Field Density Pressure";
    public override PassCategory Category => PassCategory.Force;
    public override System.Collections.Generic.IReadOnlyList<AttributeId> Reads => AttrSets.PositionHeadingTeam;
    public override System.Collections.Generic.IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

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

    public float PressureTexel
    {
        get => pressureTexel;
        set => pressureTexel = value;
    }

    public float Weight
    {
        get => weight;
        set => weight = value;
    }

    public float G0
    {
        get => g0;
        set => g0 = value;
    }

    public bool Jitter
    {
        get => jitter;
        set => jitter = value;
    }

    public int JitterSeed
    {
        get => jitterSeed;
        set => jitterSeed = value;
    }

    public bool SortedOrder
    {
        get => sortedOrder;
        set => sortedOrder = value;
    }

    /// <summary>Rebuild the density grid every Nth frame. 1 rebuilds every frame.</summary>
    public int GridUpdateInterval
    {
        get => gridUpdateInterval;
        set => gridUpdateInterval = value;
    }

    /// <summary>Recompute pressure force for each particle every Nth frame. 1 recomputes every frame.</summary>
    public int ForceInterleave
    {
        get => forceInterleave;
        set => forceInterleave = value;
    }

    /// <summary>Diagnostic stage. Does not change <see cref="SimPass.RepeatCount"/>.</summary>
    public FieldRepeatStage FieldStage { get; set; } = FieldRepeatStage.All;

    /// <summary>Extra dispatches. 1 writes the live buffers and allocates no scratch.</summary>
    public int FieldRepeatCount
    {
        get => fieldRepeatCount;
        set
        {
            fieldRepeatCount = Mathf.Clamp(value, 1, 64);
            if (fieldRepeatCount <= 1)
            {
                ReleaseScratch();
            }
        }
    }

    internal SpatialHashLayout Layout { get; private set; }

    internal Vector2 LastShift { get; private set; }

    /// <summary>Next frame index. The execute that just finished used this value minus one.</summary>
    internal int FrameIndex => frameIndex;

    internal GraphicsBuffer Density => density;

    internal GraphicsBuffer Grad => grad;

    internal GraphicsBuffer ScratchVelocity => scratchVelocity;

    internal ComputeShader PressureShader => clearKernel.Shader;

    internal const float FixedPointScale = FixedScale;

    public override void Initialize(SimContext context)
    {
        if (context.Teams == null)
        {
            throw new InvalidOperationException("Field Density Pressure requires a team buffer.");
        }

        frameIndex = 0;
        GridBuilds = 0;
        clearKernel = context.FindKernel("PressureClear");
        scatterKernel = context.FindKernel("PressureScatter");
        gradientKernel = context.FindKernel("PressureGradient");
        forceKernel = context.FindKernel("PressureForce");
    }

    public override void Execute(SimContext context, float deltaTime)
    {
            LastExecuteDispatched = false;
            RepeatClearDispatches = 0;
            RepeatScatterDispatches = 0;
            RepeatGradientDispatches = 0;
            RepeatForceDispatches = 0;
            try
            {
            RequireInterval(gridUpdateInterval, "GridUpdateInterval");
            RequireInterval(forceInterleave, "ForceInterleave");
            SpatialHashLayout layout = SpatialHashSet.ComputeLayout(center, extents, pressureTexel);
            RequireOwnLimit(layout);
            Layout = layout;
            bool buildGrid = frameIndex % gridUpdateInterval == 0;
            if (buildGrid)
            {
                LastShift = ShiftOf(frameIndex, jitterSeed, layout.CellSize, jitter);
            }
            if (FieldStage == FieldRepeatStage.Off || !KernelsReady || context.Particles == null || context.Teams == null)
            {
                return;
            }

            int count = context.Particles.Count;
            if (count == 0)
            {
                return;
            }

            EnsureLive(layout);
            EnsureStored(count);
            liveVelocity = context.Particles.Get(BuiltinAttributes.Velocity);
            CommandBuffer cmd = context.Cmd;
            Vector2 origin = layout.Origin + LastShift;
            SetUniforms(cmd, clearKernel.Shader, count, layout, origin);
            BindSorted(cmd, context);
            DispatchChain(cmd, count, layout.CellCount, density, grad, liveVelocity, context, buildGrid);
            RepeatIntoScratch(cmd, count, layout, context, buildGrid);
            LastExecuteDispatched = true;
        }
        finally
        {
            frameIndex++;
        }
    }

    public void Dispose()
    {
        density?.Release();
        density = null;
        grad?.Release();
        grad = null;
        ReleaseScratch();
        indexDummy?.Release();
        indexDummy = null;
        storedForce?.Release();
        storedForce = null;
        storedDummy?.Release();
        storedDummy = null;
        fresh?.Release();
        fresh = null;
        freshDummy?.Release();
        freshDummy = null;
        freshZeros = null;
        liveVelocity = null;
    }

    internal void BindIndexBuffer(CommandBuffer cmd, int kernel)
    {
        cmd.SetComputeBufferParam(clearKernel.Shader, kernel, "SortedIndices", IndexBuffer(null));
        cmd.SetComputeBufferParam(clearKernel.Shader, kernel, "PressureStored", StoredBuffer());
        cmd.SetComputeBufferParam(clearKernel.Shader, kernel, "PressureFresh", FreshBuffer());
    }

    private void BindSorted(CommandBuffer cmd, SimContext context)
    {
        GraphicsBuffer indices = IndexBuffer(context);
        cmd.SetComputeIntParam(clearKernel.Shader, SortedOrderId, sortedOrder ? 1 : 0);
        GraphicsBuffer stored = StoredBuffer();
        GraphicsBuffer freshBuffer = FreshBuffer();
        BindIndex(cmd, clearKernel.Index, indices);
        BindIndex(cmd, scatterKernel.Index, indices);
        BindIndex(cmd, gradientKernel.Index, indices);
        BindIndex(cmd, forceKernel.Index, indices);
        BindStored(cmd, clearKernel.Index, stored, freshBuffer);
        BindStored(cmd, scatterKernel.Index, stored, freshBuffer);
        BindStored(cmd, gradientKernel.Index, stored, freshBuffer);
        BindStored(cmd, forceKernel.Index, stored, freshBuffer);
    }

    private GraphicsBuffer IndexBuffer(SimContext context)
    {
        if (sortedOrder)
        {
            if (context == null || context.SpatialHash == null || context.SpatialHash.SortedIndices == null)
            {
                throw new InvalidOperationException(
                    "Field Density Pressure SortedOrder requires Build Spatial Hash earlier in the chain.");
            }

            return context.SpatialHash.SortedIndices;
        }

        if (indexDummy == null)
        {
            indexDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
        }

        return indexDummy;
    }

    private void BindIndex(CommandBuffer cmd, int kernel, GraphicsBuffer indices)
    {
        cmd.SetComputeBufferParam(clearKernel.Shader, kernel, "SortedIndices", indices);
    }

    private void BindStored(CommandBuffer cmd, int kernel, GraphicsBuffer stored, GraphicsBuffer freshBuffer)
    {
        cmd.SetComputeBufferParam(clearKernel.Shader, kernel, "PressureStored", stored);
        cmd.SetComputeBufferParam(clearKernel.Shader, kernel, "PressureFresh", freshBuffer);
    }

    private GraphicsBuffer StoredBuffer()
    {
        if (forceInterleave > 1 && storedForce != null)
        {
            return storedForce;
        }

        if (storedDummy == null)
        {
            storedDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 2);
        }

        return storedDummy;
    }

    private GraphicsBuffer FreshBuffer()
    {
        if (forceInterleave > 1 && fresh != null)
        {
            return fresh;
        }

        if (freshDummy == null)
        {
            freshDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
        }

        return freshDummy;
    }

    internal static Vector2 ShiftOf(int frame, int seed, Vector2 cellSize, bool enabled)
    {
        if (!enabled)
        {
            return Vector2.zero;
        }

        uint mixed = Pcg((uint)frame * 747796405u ^ (uint)seed * 2891336453u);
        float s1 = (Pcg(mixed) & 0x00FFFFFFu) / 16777216f;
        float s2 = (Pcg(mixed ^ 0x9E3779B9u) & 0x00FFFFFFu) / 16777216f;
        return new Vector2(s1 * cellSize.x, s2 * cellSize.y);
    }

    internal int RepeatClearDispatches { get; private set; }

    internal int RepeatScatterDispatches { get; private set; }

    internal int RepeatGradientDispatches { get; private set; }

    internal int RepeatForceDispatches { get; private set; }

    /// <summary>Live grid builds since the last <see cref="Initialize"/>.</summary>
    internal int GridBuilds { get; private set; }

    internal GraphicsBuffer StoredForce => storedForce;

    internal void ResetSchedule()
    {
        frameIndex = 0;
        GridBuilds = 0;
        if (fresh != null && freshZeros != null)
        {
            Array.Clear(freshZeros, 0, freshZeros.Length);
            fresh.SetData(freshZeros);
        }
    }

    public void OnBeforeSerialize()
    {
    }

    public void OnAfterDeserialize()
    {
        if (gridUpdateInterval == 0)
        {
            gridUpdateInterval = 1;
        }

        if (forceInterleave == 0)
        {
            forceInterleave = 1;
        }
    }

    private bool RepeatsThisStage =>
        fieldRepeatCount > 1 &&
        (FieldStage == FieldRepeatStage.All ||
         FieldStage == FieldRepeatStage.Press ||
         FieldStage == FieldRepeatStage.PClear ||
         FieldStage == FieldRepeatStage.PScatter ||
         FieldStage == FieldRepeatStage.PGrad ||
         FieldStage == FieldRepeatStage.PForce);

    private bool KernelsReady =>
        clearKernel.IsValid && scatterKernel.IsValid && gradientKernel.IsValid && forceKernel.IsValid;

    private void EnsureStored(int count)
    {
        if (forceInterleave <= 1)
        {
            storedForce?.Release();
            storedForce = null;
            fresh?.Release();
            fresh = null;
            freshZeros = null;
            return;
        }

        if (storedForce != null && storedForce.count == count && fresh != null && fresh.count == count)
        {
            return;
        }

        storedForce?.Release();
        fresh?.Release();
        storedForce = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 2);
        fresh = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
        if (freshZeros == null || freshZeros.Length != count)
        {
            freshZeros = new uint[count];
        }
        else
        {
            Array.Clear(freshZeros, 0, freshZeros.Length);
        }

        fresh.SetData(freshZeros);
    }

    private static void RequireInterval(int value, string name)
    {
        if (value < 1 || value > 4)
        {
            throw new InvalidOperationException(
                "Field Density Pressure " + name + " must be in 1..4, found " + value + ".");
        }
    }

    private static void RequireOwnLimit(SpatialHashLayout layout)
    {
        long cells = (long)layout.Resolution.x * layout.Resolution.y;
        if (layout.Resolution.x < 3 || layout.Resolution.y < 3 || cells > MaxCells)
        {
            throw new InvalidOperationException(
                "Density pressure grid " + layout.Resolution.x + "x" + layout.Resolution.y +
                " is outside 3.." + MaxCells + " cells.");
        }
    }

    private void EnsureLive(SpatialHashLayout layout)
    {
        int slots = layout.CellCount * 2;
        if (density == null || density.count != slots)
        {
            density?.Release();
            density = new GraphicsBuffer(GraphicsBuffer.Target.Structured, slots, sizeof(uint));
        }

        if (grad == null || grad.count != slots)
        {
            grad?.Release();
            grad = new GraphicsBuffer(GraphicsBuffer.Target.Structured, slots, sizeof(float) * 2);
        }
    }

    private void SetUniforms(CommandBuffer cmd, ComputeShader shader, int count, SpatialHashLayout layout, Vector2 origin)
    {
        cmd.SetComputeIntParam(shader, ParticleCountId, count);
        cmd.SetComputeIntParam(shader, CellsId, layout.CellCount);
        cmd.SetComputeVectorParam(shader, OriginId, new Vector4(origin.x, origin.y, 0f, 0f));
        cmd.SetComputeVectorParam(shader, CellSizeId, new Vector4(layout.CellSize.x, layout.CellSize.y, 0f, 0f));
        cmd.SetComputeIntParams(shader, ResId, layout.Resolution.x, layout.Resolution.y);
        cmd.SetComputeFloatParam(shader, WeightId, weight);
        cmd.SetComputeFloatParam(shader, FloorId, g0);
        cmd.SetComputeIntParam(shader, FrameIndexId, frameIndex);
        cmd.SetComputeIntParam(shader, ForceInterleaveId, forceInterleave);
    }

    private void DispatchChain(
        CommandBuffer cmd,
        int count,
        int cells,
        GraphicsBuffer densityBuffer,
        GraphicsBuffer gradBuffer,
        GraphicsBuffer velocityBuffer,
        SimContext context,
        bool buildGrid)
    {
        Bind(cmd, clearKernel, "Density", densityBuffer);
        Bind(cmd, scatterKernel, "Density", densityBuffer);
        Bind(cmd, scatterKernel, "position", context.Particles.Get(BuiltinAttributes.Position));
        Bind(cmd, scatterKernel, "teamId", context.Particles.Get(BuiltinAttributes.TeamId));
        Bind(cmd, gradientKernel, "Density", densityBuffer);
        Bind(cmd, gradientKernel, "Grad", gradBuffer);
        Bind(cmd, gradientKernel, "Teams", context.Teams);
        Bind(cmd, forceKernel, "Grad", gradBuffer);
        Bind(cmd, forceKernel, "position", context.Particles.Get(BuiltinAttributes.Position));
        Bind(cmd, forceKernel, "teamId", context.Particles.Get(BuiltinAttributes.TeamId));
        Bind(cmd, forceKernel, "velocity", velocityBuffer);
        int cellGroups = (cells + ThreadGroupSize - 1) / ThreadGroupSize;
        int particleGroups = (count + ThreadGroupSize - 1) / ThreadGroupSize;
        if (buildGrid)
        {
            cmd.DispatchCompute(clearKernel.Shader, clearKernel.Index, cellGroups, 1, 1);
            cmd.DispatchCompute(scatterKernel.Shader, scatterKernel.Index, particleGroups, 1, 1);
            cmd.DispatchCompute(gradientKernel.Shader, gradientKernel.Index, cellGroups, 1, 1);
            GridBuilds++;
        }

        cmd.SetComputeIntParam(forceKernel.Shader, WriteStoredId, 1);
        cmd.DispatchCompute(forceKernel.Shader, forceKernel.Index, particleGroups, 1, 1);
    }

    private void RepeatIntoScratch(CommandBuffer cmd, int count, SpatialHashLayout layout, SimContext context, bool buildGrid)
    {
        if (!RepeatsThisStage)
        {
            return;
        }

        int slots = layout.CellCount * 2;
        bool chain = FieldStage == FieldRepeatStage.All || FieldStage == FieldRepeatStage.Press;
        bool gridStage = chain ||
            FieldStage == FieldRepeatStage.PClear ||
            FieldStage == FieldRepeatStage.PScatter ||
            FieldStage == FieldRepeatStage.PGrad;
        bool runGrid = buildGrid && gridStage;
        bool writesDensity = runGrid &&
            (chain || FieldStage == FieldRepeatStage.PClear || FieldStage == FieldRepeatStage.PScatter);
        bool writesGrad = runGrid && (chain || FieldStage == FieldRepeatStage.PGrad);
        bool writesVelocity = chain || FieldStage == FieldRepeatStage.PForce;
        cmd.SetComputeIntParam(forceKernel.Shader, WriteStoredId, 0);
        if (writesDensity)
        {
            EnsureScratchDensity(slots);
        }

        if (writesGrad)
        {
            EnsureScratchGrad(slots);
        }

        if (writesVelocity)
        {
            EnsureScratchVelocity(count);
            cmd.SetBufferData(scratchVelocity, scratchZeros);
        }

        int cellGroups = (layout.CellCount + ThreadGroupSize - 1) / ThreadGroupSize;
        int particleGroups = (count + ThreadGroupSize - 1) / ThreadGroupSize;
        for (int repeat = 2; repeat <= fieldRepeatCount; repeat++)
        {
            if (chain)
            {
                if (buildGrid)
                {
                    Bind(cmd, clearKernel, "Density", scratchDensity);
                    cmd.DispatchCompute(clearKernel.Shader, clearKernel.Index, cellGroups, 1, 1);
                    Bind(cmd, scatterKernel, "Density", scratchDensity);
                    Bind(cmd, scatterKernel, "position", context.Particles.Get(BuiltinAttributes.Position));
                    Bind(cmd, scatterKernel, "teamId", context.Particles.Get(BuiltinAttributes.TeamId));
                    cmd.DispatchCompute(scatterKernel.Shader, scatterKernel.Index, particleGroups, 1, 1);
                    Bind(cmd, gradientKernel, "Density", scratchDensity);
                    Bind(cmd, gradientKernel, "Grad", scratchGrad);
                    Bind(cmd, gradientKernel, "Teams", context.Teams);
                    cmd.DispatchCompute(gradientKernel.Shader, gradientKernel.Index, cellGroups, 1, 1);
                    RepeatClearDispatches++;
                    RepeatScatterDispatches++;
                    RepeatGradientDispatches++;
                }

                Bind(cmd, forceKernel, "Grad", buildGrid ? scratchGrad : grad);
                Bind(cmd, forceKernel, "position", context.Particles.Get(BuiltinAttributes.Position));
                Bind(cmd, forceKernel, "teamId", context.Particles.Get(BuiltinAttributes.TeamId));
                Bind(cmd, forceKernel, "velocity", scratchVelocity);
                cmd.DispatchCompute(forceKernel.Shader, forceKernel.Index, particleGroups, 1, 1);
                RepeatForceDispatches++;
                continue;
            }

            if (!buildGrid && FieldStage != FieldRepeatStage.PForce)
            {
                continue;
            }

            if (FieldStage == FieldRepeatStage.PClear)
            {
                Bind(cmd, clearKernel, "Density", scratchDensity);
                cmd.DispatchCompute(clearKernel.Shader, clearKernel.Index, cellGroups, 1, 1);
                RepeatClearDispatches++;
            }
            else if (FieldStage == FieldRepeatStage.PScatter)
            {
                Bind(cmd, scatterKernel, "Density", scratchDensity);
                Bind(cmd, scatterKernel, "position", context.Particles.Get(BuiltinAttributes.Position));
                Bind(cmd, scatterKernel, "teamId", context.Particles.Get(BuiltinAttributes.TeamId));
                cmd.DispatchCompute(scatterKernel.Shader, scatterKernel.Index, particleGroups, 1, 1);
                RepeatScatterDispatches++;
            }
            else if (FieldStage == FieldRepeatStage.PGrad)
            {
                Bind(cmd, gradientKernel, "Density", density);
                Bind(cmd, gradientKernel, "Grad", scratchGrad);
                Bind(cmd, gradientKernel, "Teams", context.Teams);
                cmd.DispatchCompute(gradientKernel.Shader, gradientKernel.Index, cellGroups, 1, 1);
                RepeatGradientDispatches++;
            }
            else if (FieldStage == FieldRepeatStage.PForce)
            {
                Bind(cmd, forceKernel, "Grad", grad);
                Bind(cmd, forceKernel, "position", context.Particles.Get(BuiltinAttributes.Position));
                Bind(cmd, forceKernel, "teamId", context.Particles.Get(BuiltinAttributes.TeamId));
                Bind(cmd, forceKernel, "velocity", scratchVelocity);
                cmd.DispatchCompute(forceKernel.Shader, forceKernel.Index, particleGroups, 1, 1);
                RepeatForceDispatches++;
            }
        }

        if (writesVelocity)
        {
            Bind(cmd, forceKernel, "velocity", liveVelocity);
        }
    }

    private void EnsureScratchDensity(int slots)
    {
        if (scratchDensity == null || scratchDensity.count != slots)
        {
            scratchDensity?.Release();
            scratchDensity = new GraphicsBuffer(GraphicsBuffer.Target.Structured, slots, sizeof(uint));
        }
    }

    private void EnsureScratchGrad(int slots)
    {
        if (scratchGrad == null || scratchGrad.count != slots)
        {
            scratchGrad?.Release();
            scratchGrad = new GraphicsBuffer(GraphicsBuffer.Target.Structured, slots, sizeof(float) * 2);
        }
    }

    private void EnsureScratchVelocity(int count)
    {
        if (scratchVelocity == null || scratchVelocity.count != count)
        {
            scratchVelocity?.Release();
            scratchVelocity = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
        }

        if (scratchZeros == null || scratchZeros.Length != count)
        {
            scratchZeros = new Vector3[count];
        }
        else
        {
            Array.Clear(scratchZeros, 0, scratchZeros.Length);
        }
    }

    private static void Bind(CommandBuffer cmd, KernelHandle kernel, string name, GraphicsBuffer buffer)
    {
        cmd.SetComputeBufferParam(kernel.Shader, kernel.Index, name, buffer);
    }

    private void ReleaseScratch()
    {
        scratchDensity?.Release();
        scratchDensity = null;
        scratchGrad?.Release();
        scratchGrad = null;
        scratchVelocity?.Release();
        scratchVelocity = null;
        scratchZeros = null;
    }

    private static uint Pcg(uint value)
    {
        uint state = value * 747796405u + 2891336453u;
        uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
        return (word >> 22) ^ word;
    }
}
