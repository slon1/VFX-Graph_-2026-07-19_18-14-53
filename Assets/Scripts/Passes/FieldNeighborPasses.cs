using System;
using UnityEngine;
using UnityEngine.Rendering;

public enum FieldCollectMode
{
    Gather = 0,
    Scatter = 1,
}

/// <summary>Which field kernels a diagnostic repeat measures (ADR-061). All is the running chain.</summary>
public enum FieldRepeatStage
{
    All = 0,
    Collect = 1,
    Conv = 2,
    Grad = 3,
    Off = 4,
    Force = 5,
}

public static class FieldRepeatStages
{
    public static bool TryParse(string value, out FieldRepeatStage stage)
    {
        switch (value)
        {
            case "all":
                stage = FieldRepeatStage.All;
                return true;
            case "collect":
                stage = FieldRepeatStage.Collect;
                return true;
            case "conv":
                stage = FieldRepeatStage.Conv;
                return true;
            case "grad":
                stage = FieldRepeatStage.Grad;
                return true;
            case "force":
                stage = FieldRepeatStage.Force;
                return true;
            case "off":
                stage = FieldRepeatStage.Off;
                return true;
            default:
                stage = FieldRepeatStage.All;
                return false;
        }
    }

    public static string Name(FieldRepeatStage stage)
    {
        switch (stage)
        {
            case FieldRepeatStage.Collect:
                return "collect";
            case FieldRepeatStage.Conv:
                return "conv";
            case FieldRepeatStage.Grad:
                return "grad";
            case FieldRepeatStage.Force:
                return "force";
            case FieldRepeatStage.Off:
                return "off";
            default:
                return "all";
        }
    }
}

/// <summary>
/// Deposits neighbor counts and headings into <see cref="FieldNeighborSet.Raw"/> (ADR-061 §2.3).
/// Gather reads the spatial hash. Scatter deposits with fixed-point atomics and does not use the hash.
/// </summary>
[Serializable]
public sealed class FieldNeighborCollectPass : SimPass, IFieldNeighborPass, IDisposable
{
    private const int ThreadGroupSize = 64;

    [SerializeField] private FieldCollectMode mode = FieldCollectMode.Gather;
    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField] private Vector3 extents = new Vector3(16f, 0f, 16f);
    [SerializeField, Min(1e-4f)] private float texelSize = 1f;
    [SerializeField, Min(1)] private int refreshInterval = 1;

    [NonSerialized] private FieldNeighborSet field;
    [NonSerialized] private KernelHandle gatherKernel;
    [NonSerialized] private KernelHandle clearKernel;
    [NonSerialized] private KernelHandle scatterKernel;
    [NonSerialized] private int executeIndex;
    [NonSerialized] private int fieldRepeatCount = 1;
    [NonSerialized] private GraphicsBuffer scratchRaw;
    [NonSerialized] private GraphicsBuffer scratchAccum;

    public FieldCollectMode Mode
    {
        get => mode;
        set => mode = value;
    }

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

    public float TexelSize
    {
        get => texelSize;
        set => texelSize = value;
    }

    public int RefreshInterval
    {
        get => refreshInterval;
        set => refreshInterval = value < 1 ? 1 : value;
    }

    /// <summary>Diagnostic stage. Does not change <see cref="SimPass.RepeatCount"/>.</summary>
    public FieldRepeatStage FieldStage { get; set; } = FieldRepeatStage.All;

    /// <summary>Extra dispatches of the collect stage. 1 keeps the historical single dispatch and allocates no scratch.</summary>
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

    /// <summary>Scratch written by repeats 2..R. Null while <see cref="FieldRepeatCount"/> is 1.</summary>
    internal GraphicsBuffer FieldScratch => mode == FieldCollectMode.Scatter ? scratchAccum : scratchRaw;

    internal FieldNeighborSet Field => field;

    public override string DisplayName => mode == FieldCollectMode.Scatter
        ? "Field Neighbor Collect Scatter"
        : "Field Neighbor Collect Gather";

    public override PassCategory Category => PassCategory.Force;
    public override System.Collections.Generic.IReadOnlyList<AttributeId> Reads => AttrSets.PositionHeadingTeam;
    public override System.Collections.Generic.IReadOnlyList<AttributeId> Writes => AttrSets.None;

    public override void Initialize(SimContext context)
    {
        if (context.FieldNeighbor != null)
        {
            throw new InvalidOperationException(
                "SimulationWorld: only one enabled field-neighbor collect pass per effect.");
        }

        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(center, extents, texelSize);
        SpatialHashSet.ValidateLayout(layout, wrap: true);
        if (mode == FieldCollectMode.Gather)
        {
            RequireMatchingHash(context, layout);
            gatherKernel = context.FindKernel("FieldGather");
        }
        else
        {
            clearKernel = context.FindKernel("FieldAccumClear");
            scatterKernel = context.FindKernel("FieldScatter");
        }

        int count = context.Particles != null ? context.Particles.Count : 0;
        Dispose();
        field = new FieldNeighborSet(layout, count, mode == FieldCollectMode.Scatter);
        context.FieldNeighbor = field;
        executeIndex = 0;
    }

    public override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;
        if (field != null)
        {
            field.RefreshedThisFrame = false;
        }

        if (FieldStage == FieldRepeatStage.Off || field == null || field.ParticleCount == 0)
        {
            return;
        }

        if (mode == FieldCollectMode.Gather)
        {
            executeIndex++;
            if (context.SpatialHash == null || !context.SpatialHash.BuiltThisFrame)
            {
                return;
            }
        }
        else
        {
            int interval = refreshInterval < 1 ? 1 : refreshInterval;
            bool due = interval <= 1 || (executeIndex % interval) == 0;
            executeIndex++;
            if (!due)
            {
                return;
            }
        }

        ComputeShader shader = mode == FieldCollectMode.Gather ? gatherKernel.Shader : clearKernel.Shader;
        CommandBuffer cmd = context.Cmd;
        field.PushParams(cmd, shader, field.RadiusTexels(context.Teams), field.TeamRadius(context.Teams));
        cmd.SetComputeIntParam(shader, SimShaderIds.ParticleCount, field.ParticleCount);

        if (mode == FieldCollectMode.Gather)
        {
            SpatialHashSet hash = context.SpatialHash;
            hash.PushParams(cmd, shader);
            Bind(cmd, gatherKernel, "CellCounts", hash.CellCounts);
            Bind(cmd, gatherKernel, "CellStarts", hash.CellStarts);
            Bind(cmd, gatherKernel, "SortedPositions", hash.SortedPositions);
            Bind(cmd, gatherKernel, "SortedDirections", hash.SortedDirections);
            Bind(cmd, gatherKernel, "SortedTeams", hash.SortedTeams);
            Bind(cmd, gatherKernel, "Raw", field.Raw);
            int groups = (field.Layout.CellCount + ThreadGroupSize - 1) / ThreadGroupSize;
            cmd.DispatchCompute(gatherKernel.Shader, gatherKernel.Index, groups, 1, 1);
            RepeatGather(cmd, groups);
        }
        else
        {
            Bind(cmd, clearKernel, "Accum", field.Accum);
            Bind(cmd, scatterKernel, "position", context.Particles.Get(BuiltinAttributes.Position));
            Bind(cmd, scatterKernel, "heading", context.Particles.Get(BuiltinAttributes.Heading));
            Bind(cmd, scatterKernel, "teamId", context.Particles.Get(BuiltinAttributes.TeamId));
            Bind(cmd, scatterKernel, "Accum", field.Accum);
            int accumCount = field.Layout.CellCount * FieldNeighborSet.ChannelsPerTexel;
            int clearGroups = (accumCount + ThreadGroupSize - 1) / ThreadGroupSize;
            int particleGroups = (field.ParticleCount + ThreadGroupSize - 1) / ThreadGroupSize;
            cmd.DispatchCompute(clearKernel.Shader, clearKernel.Index, clearGroups, 1, 1);
            cmd.DispatchCompute(scatterKernel.Shader, scatterKernel.Index, particleGroups, 1, 1);
            RepeatScatter(cmd, clearGroups, particleGroups);
        }

        field.RefreshedThisFrame = true;
        field.Frame = executeIndex - 1;
        LastExecuteDispatched = true;
    }

    public void Dispose()
    {
        ReleaseScratch();
        field?.Dispose();
        field = null;
    }

    private bool RepeatsCollect =>
        fieldRepeatCount > 1 && (FieldStage == FieldRepeatStage.All || FieldStage == FieldRepeatStage.Collect);

    private void RepeatGather(CommandBuffer cmd, int groups)
    {
        if (!RepeatsCollect)
        {
            return;
        }

        scratchRaw = Ensure(scratchRaw, field.Layout.CellCount * FieldNeighborSet.ChannelsPerTexel, 4);
        Bind(cmd, gatherKernel, "Raw", scratchRaw);
        for (int repeat = 2; repeat <= fieldRepeatCount; repeat++)
        {
            cmd.DispatchCompute(gatherKernel.Shader, gatherKernel.Index, groups, 1, 1);
        }

        Bind(cmd, gatherKernel, "Raw", field.Raw);
    }

    private void RepeatScatter(CommandBuffer cmd, int clearGroups, int particleGroups)
    {
        if (!RepeatsCollect)
        {
            return;
        }

        scratchAccum = Ensure(scratchAccum, field.Layout.CellCount * FieldNeighborSet.ChannelsPerTexel, 4);
        Bind(cmd, clearKernel, "Accum", scratchAccum);
        Bind(cmd, scatterKernel, "Accum", scratchAccum);
        for (int repeat = 2; repeat <= fieldRepeatCount; repeat++)
        {
            cmd.DispatchCompute(clearKernel.Shader, clearKernel.Index, clearGroups, 1, 1);
            cmd.DispatchCompute(scatterKernel.Shader, scatterKernel.Index, particleGroups, 1, 1);
        }

        Bind(cmd, clearKernel, "Accum", field.Accum);
        Bind(cmd, scatterKernel, "Accum", field.Accum);
    }

    private static GraphicsBuffer Ensure(GraphicsBuffer buffer, int count, int stride)
    {
        if (buffer != null && buffer.count == count)
        {
            return buffer;
        }

        buffer?.Release();
        return new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
    }

    private void ReleaseScratch()
    {
        scratchRaw?.Release();
        scratchRaw = null;
        scratchAccum?.Release();
        scratchAccum = null;
    }

    private static void RequireMatchingHash(SimContext context, SpatialHashLayout layout)
    {
        SpatialHashSet hash = context.SpatialHash;
        if (hash == null)
        {
            throw new InvalidOperationException(
                "Field Neighbor Collect Gather requires Build Spatial Hash with the same layout.");
        }

        SpatialHashLayout hashLayout = hash.Layout;
        if (hashLayout.Resolution != layout.Resolution ||
            (hashLayout.Origin - layout.Origin).sqrMagnitude > 1e-8f ||
            (hashLayout.CellSize - layout.CellSize).sqrMagnitude > 1e-8f)
        {
            throw new InvalidOperationException(
                "Field Neighbor Collect Gather layout does not match the spatial hash " +
                $"(field {layout.Resolution.x}x{layout.Resolution.y}, hash {hashLayout.Resolution.x}x{hashLayout.Resolution.y}).");
        }
    }

    private static void Bind(CommandBuffer cmd, KernelHandle kernel, string name, GraphicsBuffer buffer)
    {
        cmd.SetComputeBufferParam(kernel.Shader, kernel.Index, name, buffer);
    }
}

/// <summary>
/// Disk convolution, potential and central-difference gradient (ADR-061 §2.4).
/// Scatter accumulation is resolved here; there is no separate resolve pass.
/// </summary>
[Serializable]
public sealed class FieldNeighborConvolvePass : SimPass, IFieldNeighborPass, IDisposable
{
    private const int ThreadGroupSize = 64;

    [NonSerialized] private KernelHandle potentialRawKernel;
    [NonSerialized] private KernelHandle potentialAccumKernel;
    [NonSerialized] private KernelHandle gradientKernel;
    [NonSerialized] private int fieldRepeatCount = 1;
    [NonSerialized] private GraphicsBuffer scratchPhi;
    [NonSerialized] private GraphicsBuffer scratchField;

    public override string DisplayName => "Field Neighbor Convolve";
    public override PassCategory Category => PassCategory.Force;
    public override System.Collections.Generic.IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override System.Collections.Generic.IReadOnlyList<AttributeId> Writes => AttrSets.None;

    /// <summary>Diagnostic stage. Does not change <see cref="SimPass.RepeatCount"/>.</summary>
    public FieldRepeatStage FieldStage { get; set; } = FieldRepeatStage.All;

    /// <summary>Extra dispatches of the selected convolve stage. 1 allocates no scratch.</summary>
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

    /// <summary>Scratch field written by repeats 2..R. Null while <see cref="FieldRepeatCount"/> is 1.</summary>
    internal GraphicsBuffer FieldScratch => scratchField;

    public override void Initialize(SimContext context)
    {
        if (context.FieldNeighbor == null)
        {
            throw new InvalidOperationException(
                "Field Neighbor Convolve requires Field Neighbor Collect earlier in the chain.");
        }

        if (context.Teams == null)
        {
            throw new InvalidOperationException("Field Neighbor Convolve requires a team buffer.");
        }

        potentialRawKernel = context.FindKernel("FieldPotentialRaw");
        potentialAccumKernel = context.FindKernel("FieldPotentialAccum");
        gradientKernel = context.FindKernel("FieldGradient");
    }

    public override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;
        if (FieldStage == FieldRepeatStage.Off)
        {
            return;
        }

        FieldNeighborSet field = context.FieldNeighbor;
        if (field == null || !field.RefreshedThisFrame || field.ParticleCount == 0 || context.Teams == null)
        {
            return;
        }

        KernelHandle potential = field.Scatter ? potentialAccumKernel : potentialRawKernel;
        ComputeShader shader = potential.Shader;
        CommandBuffer cmd = context.Cmd;
        field.PushParams(cmd, shader, field.RadiusTexels(context.Teams), field.TeamRadius(context.Teams));
        Bind(cmd, potential, "Teams", context.Teams);
        Bind(cmd, potential, "Phi", field.Phi);
        Bind(cmd, potential, "FieldSlots", field.Field);
        if (field.Scatter)
        {
            Bind(cmd, potential, "Accum", field.Accum);
        }
        else
        {
            Bind(cmd, potential, "Raw", field.Raw);
        }

        Bind(cmd, gradientKernel, "Phi", field.Phi);
        Bind(cmd, gradientKernel, "FieldSlots", field.Field);
        int groups = (field.Layout.CellCount + ThreadGroupSize - 1) / ThreadGroupSize;
        cmd.DispatchCompute(potential.Shader, potential.Index, groups, 1, 1);
        cmd.DispatchCompute(gradientKernel.Shader, gradientKernel.Index, groups, 1, 1);
        RepeatPotential(cmd, potential, field, groups);
        RepeatGradient(cmd, field, groups);
        LastExecuteDispatched = true;
    }

    public void Dispose()
    {
        ReleaseScratch();
    }

    private void RepeatPotential(CommandBuffer cmd, KernelHandle potential, FieldNeighborSet field, int groups)
    {
        if (fieldRepeatCount <= 1 ||
            (FieldStage != FieldRepeatStage.All && FieldStage != FieldRepeatStage.Conv))
        {
            return;
        }

        EnsureConvolveScratch(field, phi: true);
        Bind(cmd, potential, "Phi", scratchPhi);
        Bind(cmd, potential, "FieldSlots", scratchField);
        for (int repeat = 2; repeat <= fieldRepeatCount; repeat++)
        {
            cmd.DispatchCompute(potential.Shader, potential.Index, groups, 1, 1);
        }

        Bind(cmd, potential, "Phi", field.Phi);
        Bind(cmd, potential, "FieldSlots", field.Field);
    }

    private void RepeatGradient(CommandBuffer cmd, FieldNeighborSet field, int groups)
    {
        if (fieldRepeatCount <= 1 ||
            (FieldStage != FieldRepeatStage.All && FieldStage != FieldRepeatStage.Grad))
        {
            return;
        }

        EnsureConvolveScratch(field, phi: false);
        Bind(cmd, gradientKernel, "FieldSlots", scratchField);
        for (int repeat = 2; repeat <= fieldRepeatCount; repeat++)
        {
            cmd.DispatchCompute(gradientKernel.Shader, gradientKernel.Index, groups, 1, 1);
        }

        Bind(cmd, gradientKernel, "FieldSlots", field.Field);
    }

    private void EnsureConvolveScratch(FieldNeighborSet field, bool phi)
    {
        int cells = field.Layout.CellCount;
        if (scratchField == null || scratchField.count != cells * 4)
        {
            scratchField?.Release();
            scratchField = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cells * 4, 16);
        }

        if (!phi)
        {
            return;
        }

        if (scratchPhi == null || scratchPhi.count != cells)
        {
            scratchPhi?.Release();
            scratchPhi = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cells, 8);
        }
    }

    private void ReleaseScratch()
    {
        scratchPhi?.Release();
        scratchPhi = null;
        scratchField?.Release();
        scratchField = null;
    }

    private static void Bind(CommandBuffer cmd, KernelHandle kernel, string name, GraphicsBuffer buffer)
    {
        cmd.SetComputeBufferParam(kernel.Shader, kernel.Index, name, buffer);
    }
}

/// <summary>
/// Reads the neighbor field and adds separation, alignment and cohesion (ADR-062 §2.2).
/// Diagnostic repeats 2..N write a scratch velocity. <see cref="SimPass.RepeatCount"/> stays 1.
/// </summary>
[Serializable]
public sealed class FieldNeighborForcePass : SimPass, IFieldNeighborPass, IDisposable
{
    private const int ThreadGroupSize = 64;

    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField] private Vector3 extents = new Vector3(16f, 0f, 16f);
    [SerializeField, Min(1e-4f)] private float texelSize = 1f;

    [NonSerialized] private KernelHandle kernel;
    [NonSerialized] private int fieldRepeatCount = 1;
    [NonSerialized] private GraphicsBuffer scratchVelocity;
    [NonSerialized] private Vector3[] scratchZeros;
    [NonSerialized] private GraphicsBuffer liveVelocity;

    public override string DisplayName => "Field Neighbor Force";
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

    public float TexelSize
    {
        get => texelSize;
        set => texelSize = value;
    }

    /// <summary>Diagnostic stage. Does not change <see cref="SimPass.RepeatCount"/>.</summary>
    public FieldRepeatStage FieldStage { get; set; } = FieldRepeatStage.All;

    /// <summary>Extra dispatches of the force. 1 writes velocity and allocates no scratch.</summary>
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

    /// <summary>Scratch velocity written by repeats 2..R. Null while <see cref="FieldRepeatCount"/> is 1.</summary>
    internal GraphicsBuffer ForceScratch => scratchVelocity;

    public override void Initialize(SimContext context)
    {
        if (context.FieldNeighbor == null)
        {
            throw new InvalidOperationException(
                "Field Neighbor Force requires Field Neighbor Collect earlier in the chain.");
        }

        if (context.Teams == null)
        {
            throw new InvalidOperationException("Field Neighbor Force requires a team buffer.");
        }

        kernel = context.FindKernel("FieldNeighborForce");
    }

    public override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;
        if (FieldStage == FieldRepeatStage.Off || !kernel.IsValid)
        {
            return;
        }

        FieldNeighborSet field = context.FieldNeighbor;
        if (field == null || field.ParticleCount == 0 || context.Teams == null || context.Particles == null)
        {
            return;
        }

        int count = context.Particles.Count;
        if (count == 0)
        {
            return;
        }

        liveVelocity = context.Particles.Get(BuiltinAttributes.Velocity);
        CommandBuffer cmd = context.Cmd;
        ComputeShader shader = kernel.Shader;
        field.PushParams(cmd, shader, field.RadiusTexels(context.Teams), field.TeamRadius(context.Teams));
        cmd.SetComputeIntParam(shader, SimShaderIds.ParticleCount, count);
        Bind(cmd, "position", context.Particles.Get(BuiltinAttributes.Position));
        Bind(cmd, "heading", context.Particles.Get(BuiltinAttributes.Heading));
        Bind(cmd, "teamId", context.Particles.Get(BuiltinAttributes.TeamId));
        Bind(cmd, "velocity", liveVelocity);
        Bind(cmd, "Teams", context.Teams);
        Bind(cmd, "FieldSlots", field.Field);
        int groups = (count + ThreadGroupSize - 1) / ThreadGroupSize;
        cmd.DispatchCompute(shader, kernel.Index, groups, 1, 1);
        RepeatForce(cmd, count, groups);
        LastExecuteDispatched = true;
    }

    public void Dispose()
    {
        ReleaseScratch();
        liveVelocity = null;
    }

    private bool RepeatsForce =>
        fieldRepeatCount > 1 &&
        (FieldStage == FieldRepeatStage.All || FieldStage == FieldRepeatStage.Force);

    private void RepeatForce(CommandBuffer cmd, int count, int groups)
    {
        if (!RepeatsForce)
        {
            return;
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

        cmd.SetBufferData(scratchVelocity, scratchZeros);
        Bind(cmd, "velocity", scratchVelocity);
        for (int repeat = 2; repeat <= fieldRepeatCount; repeat++)
        {
            cmd.DispatchCompute(kernel.Shader, kernel.Index, groups, 1, 1);
        }

        Bind(cmd, "velocity", liveVelocity);
    }

    private void Bind(CommandBuffer cmd, string name, GraphicsBuffer buffer)
    {
        cmd.SetComputeBufferParam(kernel.Shader, kernel.Index, name, buffer);
    }

    private void ReleaseScratch()
    {
        scratchVelocity?.Release();
        scratchVelocity = null;
        scratchZeros = null;
    }
}
