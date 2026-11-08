using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Near-field density pressure on a fine count grid (ADR-062 §2.10).
/// Runs every frame. Diagnostic repeats 2..N of <see cref="FieldRepeatStage.All"/> write scratch
/// buffers. <see cref="SimPass.RepeatCount"/> stays 1. The stage token <c>press</c> is wired later.
/// </summary>
[Serializable]
public sealed class FieldDensityPressurePass : SimPass, IDisposable
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

    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField] private Vector3 extents = new Vector3(45f, 0f, 45f);
    [SerializeField, Min(1e-4f)] private float pressureTexel = 0.25f;
    [SerializeField, Min(0f)] private float weight = 0.8f;
    [SerializeField, Min(1e-4f)] private float g0 = 40f;
    [SerializeField] private bool jitter = true;
    [SerializeField] private int jitterSeed = 1;

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

        clearKernel = context.FindKernel("PressureClear");
        scatterKernel = context.FindKernel("PressureScatter");
        gradientKernel = context.FindKernel("PressureGradient");
        forceKernel = context.FindKernel("PressureForce");
    }

    public override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;
        try
        {
            SpatialHashLayout layout = SpatialHashSet.ComputeLayout(center, extents, pressureTexel);
            RequireOwnLimit(layout);
            Layout = layout;
            LastShift = ShiftOf(frameIndex, jitterSeed, layout.CellSize, jitter);
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
            liveVelocity = context.Particles.Get(BuiltinAttributes.Velocity);
            CommandBuffer cmd = context.Cmd;
            Vector2 origin = layout.Origin + LastShift;
            SetUniforms(cmd, clearKernel.Shader, count, layout, origin);
            DispatchChain(cmd, count, layout.CellCount, density, grad, liveVelocity, context);
            RepeatIntoScratch(cmd, count, layout, context);
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
        liveVelocity = null;
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

    private bool RepeatsThisStage =>
        fieldRepeatCount > 1 && FieldStage == FieldRepeatStage.All;

    private bool KernelsReady =>
        clearKernel.IsValid && scatterKernel.IsValid && gradientKernel.IsValid && forceKernel.IsValid;

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
    }

    private void DispatchChain(
        CommandBuffer cmd,
        int count,
        int cells,
        GraphicsBuffer densityBuffer,
        GraphicsBuffer gradBuffer,
        GraphicsBuffer velocityBuffer,
        SimContext context)
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
        cmd.DispatchCompute(clearKernel.Shader, clearKernel.Index, cellGroups, 1, 1);
        cmd.DispatchCompute(scatterKernel.Shader, scatterKernel.Index, particleGroups, 1, 1);
        cmd.DispatchCompute(gradientKernel.Shader, gradientKernel.Index, cellGroups, 1, 1);
        cmd.DispatchCompute(forceKernel.Shader, forceKernel.Index, particleGroups, 1, 1);
    }

    private void RepeatIntoScratch(CommandBuffer cmd, int count, SpatialHashLayout layout, SimContext context)
    {
        if (!RepeatsThisStage)
        {
            return;
        }

        int slots = layout.CellCount * 2;
        if (scratchDensity == null || scratchDensity.count != slots)
        {
            scratchDensity?.Release();
            scratchDensity = new GraphicsBuffer(GraphicsBuffer.Target.Structured, slots, sizeof(uint));
        }

        if (scratchGrad == null || scratchGrad.count != slots)
        {
            scratchGrad?.Release();
            scratchGrad = new GraphicsBuffer(GraphicsBuffer.Target.Structured, slots, sizeof(float) * 2);
        }

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

        for (int repeat = 2; repeat <= fieldRepeatCount; repeat++)
        {
            cmd.SetBufferData(scratchVelocity, scratchZeros);
            DispatchChain(cmd, count, layout.CellCount, scratchDensity, scratchGrad, scratchVelocity, context);
        }

        Bind(cmd, forceKernel, "velocity", liveVelocity);
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
