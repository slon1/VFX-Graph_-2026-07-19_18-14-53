using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Pass classification is by role in the frame, not by data touched:
/// data is already machine-expressed via Reads/Writes / FieldReads/FieldWrites.
/// </summary>
public enum PassCategory
{
    Shape = 0,
    Force = 1,
    Dynamics = 2,
    Emit = 3,
    Transport = 4,
}

/// <summary>Shader property ids shared by all passes.</summary>
internal static class SimShaderIds
{
    public static readonly int ParticleCount = Shader.PropertyToID("ParticleCount");
    public static readonly int DeltaTime = Shader.PropertyToID("DeltaTime");
    public static readonly int SimTime = Shader.PropertyToID("SimTime");
    public static readonly int Touches = Shader.PropertyToID("Touches");
    public static readonly int TouchCount = Shader.PropertyToID("TouchCount");

    public static readonly int FieldResolution = Shader.PropertyToID("FieldResolution");
    public static readonly int FieldTexelSize = Shader.PropertyToID("FieldTexelSize");
    public static readonly int FieldOrigin = Shader.PropertyToID("FieldOrigin");
    public static readonly int FieldAxisU = Shader.PropertyToID("FieldAxisU");
    public static readonly int FieldAxisV = Shader.PropertyToID("FieldAxisV");
    public static readonly int FieldSize = Shader.PropertyToID("FieldSize");
}

/// <summary>
/// Pushes the shared FieldParams uniform block (plane basis + resolution) used by
/// FieldKernelPass and hybrid particle passes that sample fields.
/// </summary>
internal static class FieldShaderParams
{
    public static void Push(CommandBuffer cmd, ComputeShader shader, FieldDescriptor descriptor)
    {
        Vector2Int res = descriptor.Resolution;
        cmd.SetComputeIntParams(shader, SimShaderIds.FieldResolution, res.x, res.y, 0, 0);
        cmd.SetComputeVectorParam(
            shader,
            SimShaderIds.FieldTexelSize,
            new Vector4(1f / res.x, 1f / res.y, 0f, 0f));
        cmd.SetComputeVectorParam(shader, SimShaderIds.FieldOrigin, descriptor.Origin);
        cmd.SetComputeVectorParam(shader, SimShaderIds.FieldAxisU, descriptor.AxisU.normalized);
        cmd.SetComputeVectorParam(shader, SimShaderIds.FieldAxisV, descriptor.AxisV.normalized);
        cmd.SetComputeVectorParam(
            shader,
            SimShaderIds.FieldSize,
            new Vector4(descriptor.Size.x, descriptor.Size.y, 0f, 0f));
    }
}

/// <summary>Common Reads/Writes sets to avoid per-instance allocations.</summary>
internal static class AttrSets
{
    public static readonly AttributeId[] None = { };
    public static readonly AttributeId[] Position = { BuiltinAttributes.Position };
    public static readonly AttributeId[] Velocity = { BuiltinAttributes.Velocity };
    public static readonly AttributeId[] RestPosition = { BuiltinAttributes.RestPosition };
    public static readonly AttributeId[] PositionVelocity = { BuiltinAttributes.Position, BuiltinAttributes.Velocity };
    public static readonly AttributeId[] PositionRest = { BuiltinAttributes.Position, BuiltinAttributes.RestPosition };
}

/// <summary>
/// Caches single-entry FieldRequest arrays so FieldReads/FieldWrites don't allocate
/// on every access — World reads FieldWrites every frame for ping-pong swaps.
/// The cache is rebuilt when the serialized field name changes (inspector tweak).
/// </summary>
internal static class FieldRequestSets
{
    public static FieldRequest[] Single(
        ref FieldRequest[] cache,
        string fieldName,
        FieldAccess access,
        FieldSemantic semantic,
        int channels)
    {
        if (cache == null ||
            !string.Equals(cache[0].FieldName, fieldName, StringComparison.Ordinal))
        {
            cache = new[] { new FieldRequest(fieldName, access, semantic, channels) };
        }

        return cache;
    }
}

/// <summary>
/// Atomic unit of the simulation pipeline. Serialized polymorphically
/// ([SerializeReference]) inside EffectAsset.
/// </summary>
[Serializable]
public abstract class SimPass
{
    private static readonly FieldRequest[] EmptyFieldRequests = { };

    [SerializeField] private bool enabled = true;

    public bool Enabled
    {
        get => enabled;
        set => enabled = value;
    }

    public abstract string DisplayName { get; }
    public abstract PassCategory Category { get; }

    /// <summary>Particle attributes this pass reads. Used for validation and auto-registration.</summary>
    public abstract IReadOnlyList<AttributeId> Reads { get; }

    /// <summary>Particle attributes this pass writes. Used for validation and auto-registration.</summary>
    public abstract IReadOnlyList<AttributeId> Writes { get; }

    /// <summary>Field resources this pass reads. Default empty — override in field/hybrid passes.</summary>
    public virtual IReadOnlyList<FieldRequest> FieldReads => EmptyFieldRequests;

    /// <summary>Field resources this pass writes (InPlace or PingPong). Default empty.</summary>
    public virtual IReadOnlyList<FieldRequest> FieldWrites => EmptyFieldRequests;

    /// <summary>
    /// False when the last Execute early-outed without recording GPU work (e.g. kernel
    /// missing after a Play Mode edit). World skips ping-pong swaps for such passes —
    /// otherwise Current would flip to a stale texture. Defaults to true so custom
    /// passes that always record work need no extra code.
    /// </summary>
    public bool LastExecuteDispatched { get; protected set; } = true;

    public abstract void Initialize(SimContext context);
    public abstract void Execute(SimContext context, float deltaTime);
}

/// <summary>
/// Base for element-wise particle kernels: resolves the kernel by name,
/// auto-binds attribute buffers declared in Reads/Writes (HLSL buffer name ==
/// attribute name), sets ParticleCount and dispatches. Subclasses only push
/// their own uniforms in SetParams.
/// </summary>
[Serializable]
public abstract class ParticleKernelPass : SimPass
{
    protected const int ThreadGroupSize = 64; // must match THREADS in the .compute files

    private readonly List<(int propertyId, AttributeId attribute)> attributeBindings =
        new List<(int, AttributeId)>();

    private KernelHandle kernel;

    protected abstract string KernelName { get; }
    protected KernelHandle Kernel => kernel;

    public override void Initialize(SimContext context)
    {
        kernel = context.FindKernel(KernelName);

        attributeBindings.Clear();
        HashSet<AttributeId> seen = new HashSet<AttributeId>();
        CollectBindings(Reads, seen);
        CollectBindings(Writes, seen);
    }

    public sealed override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;

        if (!kernel.IsValid)
        {
            return; // pass added in Play Mode without Rebuild — skip silently
        }

        int count = context.Particles.Count;
        if (count == 0)
        {
            return;
        }

        for (int i = 0; i < attributeBindings.Count; i++)
        {
            (int propertyId, AttributeId attribute) = attributeBindings[i];
            context.Cmd.SetComputeBufferParam(
                kernel.Shader, kernel.Index, propertyId, context.Particles.Get(attribute));
        }

        context.Cmd.SetComputeIntParam(kernel.Shader, SimShaderIds.ParticleCount, count);
        SetParams(context, deltaTime);

        int threadGroups = (count + ThreadGroupSize - 1) / ThreadGroupSize;
        context.Cmd.DispatchCompute(kernel.Shader, kernel.Index, threadGroups, 1, 1);
        LastExecuteDispatched = true;
    }

    /// <summary>Push per-pass uniforms (and extra buffers) before the dispatch.</summary>
    protected virtual void SetParams(SimContext context, float deltaTime)
    {
    }

    protected void SetFloat(SimContext context, int propertyId, float value)
    {
        context.Cmd.SetComputeFloatParam(kernel.Shader, propertyId, value);
    }

    protected void SetInt(SimContext context, int propertyId, int value)
    {
        context.Cmd.SetComputeIntParam(kernel.Shader, propertyId, value);
    }

    protected void SetVector(SimContext context, int propertyId, Vector3 value)
    {
        context.Cmd.SetComputeVectorParam(kernel.Shader, propertyId, value);
    }

    protected void BindBuffer(SimContext context, int propertyId, GraphicsBuffer buffer)
    {
        context.Cmd.SetComputeBufferParam(kernel.Shader, kernel.Index, propertyId, buffer);
    }

    private void CollectBindings(IReadOnlyList<AttributeId> ids, HashSet<AttributeId> seen)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (seen.Add(ids[i]))
            {
                attributeBindings.Add((Shader.PropertyToID(ids[i].Name), ids[i]));
            }
        }
    }
}

/// <summary>
/// Base for field kernels: binds textures by naming convention, pushes FieldParams,
/// dispatches over the field resolution. Subclasses only override SetParams.
/// </summary>
[Serializable]
public abstract class FieldKernelPass : SimPass
{
    protected const int ThreadGroupSize = 8; // must match FIELD_THREADS in FieldPasses.compute

    private KernelHandle kernel;
    private readonly List<FieldBind> fieldBinds = new List<FieldBind>();
    private FieldDescriptor primaryDescriptor;

    private struct FieldBind
    {
        public string FieldName;
        public FieldAccess Access;
        public int ReadId;
        public int WriteId;
    }

    protected abstract string KernelName { get; }
    protected KernelHandle Kernel => kernel;

    /// <summary>Field whose plane/resolution drive FieldParams (first write, else first read).</summary>
    protected virtual string PrimaryFieldName
    {
        get
        {
            if (FieldWrites.Count > 0)
            {
                return FieldWrites[0].FieldName;
            }

            return FieldReads.Count > 0 ? FieldReads[0].FieldName : null;
        }
    }

    public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.None;

    public override void Initialize(SimContext context)
    {
        kernel = context.FindKernel(KernelName);
        fieldBinds.Clear();

        CollectFieldBinds(FieldReads);
        CollectFieldBinds(FieldWrites);
        ValidateAccessConflicts();

        string primary = PrimaryFieldName;
        if (string.IsNullOrEmpty(primary))
        {
            throw new InvalidOperationException(
                $"{DisplayName}: FieldKernelPass requires at least one field request.");
        }

        primaryDescriptor = context.Fields.Get(primary).Descriptor;
    }

    public sealed override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;

        if (!kernel.IsValid || primaryDescriptor == null)
        {
            return;
        }

        for (int i = 0; i < fieldBinds.Count; i++)
        {
            FieldBind bind = fieldBinds[i];
            SimField field = context.Fields.Get(bind.FieldName);

            switch (bind.Access)
            {
                case FieldAccess.Read:
                    context.Cmd.SetComputeTextureParam(
                        kernel.Shader, kernel.Index, bind.ReadId, field.Current);
                    break;
                case FieldAccess.WriteInPlace:
                    context.Cmd.SetComputeTextureParam(
                        kernel.Shader, kernel.Index, bind.WriteId, field.Current);
                    break;
                case FieldAccess.WritePingPong:
                    context.Cmd.SetComputeTextureParam(
                        kernel.Shader, kernel.Index, bind.ReadId, field.Current);
                    context.Cmd.SetComputeTextureParam(
                        kernel.Shader, kernel.Index, bind.WriteId, field.Next);
                    break;
            }
        }

        PushFieldParams(context, primaryDescriptor);
        SetParams(context, deltaTime);

        Vector2Int res = primaryDescriptor.Resolution;
        int groupsX = (res.x + ThreadGroupSize - 1) / ThreadGroupSize;
        int groupsY = (res.y + ThreadGroupSize - 1) / ThreadGroupSize;
        context.Cmd.DispatchCompute(kernel.Shader, kernel.Index, groupsX, groupsY, 1);
        LastExecuteDispatched = true;
    }

    protected virtual void SetParams(SimContext context, float deltaTime)
    {
    }

    protected void SetFloat(SimContext context, int propertyId, float value)
    {
        context.Cmd.SetComputeFloatParam(kernel.Shader, propertyId, value);
    }

    protected void SetInt(SimContext context, int propertyId, int value)
    {
        context.Cmd.SetComputeIntParam(kernel.Shader, propertyId, value);
    }

    protected void SetVector(SimContext context, int propertyId, Vector3 value)
    {
        context.Cmd.SetComputeVectorParam(kernel.Shader, propertyId, value);
    }

    protected void BindBuffer(SimContext context, int propertyId, GraphicsBuffer buffer)
    {
        context.Cmd.SetComputeBufferParam(kernel.Shader, kernel.Index, propertyId, buffer);
    }

    private void PushFieldParams(SimContext context, FieldDescriptor descriptor)
    {
        FieldShaderParams.Push(context.Cmd, kernel.Shader, descriptor);
    }

    private void CollectFieldBinds(IReadOnlyList<FieldRequest> requests)
    {
        for (int i = 0; i < requests.Count; i++)
        {
            FieldRequest request = requests[i];
            string name = request.FieldName;
            fieldBinds.Add(new FieldBind
            {
                FieldName = name,
                Access = request.Access,
                ReadId = Shader.PropertyToID(name + "Read"),
                WriteId = Shader.PropertyToID(name + "Write"),
            });
        }
    }

    private void ValidateAccessConflicts()
    {
        Dictionary<string, FieldAccess> seen = new Dictionary<string, FieldAccess>(StringComparer.Ordinal);
        for (int i = 0; i < fieldBinds.Count; i++)
        {
            FieldBind bind = fieldBinds[i];
            if (seen.TryGetValue(bind.FieldName, out FieldAccess existing))
            {
                if (existing != bind.Access)
                {
                    throw new InvalidOperationException(
                        $"{DisplayName}: conflicting FieldAccess on '{bind.FieldName}' " +
                        $"({existing} vs {bind.Access}).");
                }
            }
            else
            {
                seen.Add(bind.FieldName, bind.Access);
            }
        }
    }
}
