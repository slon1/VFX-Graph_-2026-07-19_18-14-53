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

    /// <summary>Fixed texture slots for single-field kernels (not {fieldName}Read/Write).</summary>
    public static readonly int FieldRead = Shader.PropertyToID("FieldRead");
    public static readonly int FieldWrite = Shader.PropertyToID("FieldWrite");

    /// <summary>Role slots for multi-field-per-kernel passes (ADR-008). Single-field keeps FieldRead/Write.</summary>
    public static readonly int FieldReadA = Shader.PropertyToID("FieldReadA");
    public static readonly int FieldWriteA = Shader.PropertyToID("FieldWriteA");
    public static readonly int FieldReadB = Shader.PropertyToID("FieldReadB");
    public static readonly int FieldWriteB = Shader.PropertyToID("FieldWriteB");

    public static readonly int DiffusionRate = Shader.PropertyToID("DiffusionRate");
    public static readonly int Dissipation = Shader.PropertyToID("Dissipation");
    public static readonly int DecayFactor = Shader.PropertyToID("DecayFactor");
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
    public static readonly AttributeId[] Heading = { BuiltinAttributes.Heading };
    public static readonly AttributeId[] HeadingVelocity = { BuiltinAttributes.Heading, BuiltinAttributes.Velocity };
}

/// <summary>
/// Caches single-entry FieldRequest arrays so FieldReads/FieldWrites don't allocate
/// on every access — World reads FieldWrites every frame for ping-pong swaps.
/// Rebuild uses <see cref="FieldRequest"/> value equality (not fieldName alone).
/// </summary>
internal static class FieldRequestSets
{
    public static FieldRequest[] Single(
        ref FieldRequest[] cache,
        string fieldName,
        FieldAccess access,
        FieldSemantic requiredSemantic,
        int channels,
        FieldSlotRole role = FieldSlotRole.A)
    {
        FieldRequest current = new FieldRequest(fieldName, access, requiredSemantic, channels, role);

        if (cache == null || cache.Length != 1 || !cache[0].Equals(current))
        {
            cache = new[] { current };
        }

        return cache;
    }

    public static FieldRequest[] Pair(
        ref FieldRequest[] cache,
        string fieldNameA,
        FieldSlotRole roleA,
        FieldAccess accessA,
        FieldSemantic semanticA,
        int channelsA,
        string fieldNameB,
        FieldSlotRole roleB,
        FieldAccess accessB,
        FieldSemantic semanticB,
        int channelsB)
    {
        FieldRequest a = new FieldRequest(fieldNameA, accessA, semanticA, channelsA, roleA);
        FieldRequest b = new FieldRequest(fieldNameB, accessB, semanticB, channelsB, roleB);

        if (cache == null ||
            cache.Length != 2 ||
            !cache[0].Equals(a) ||
            !cache[1].Equals(b))
        {
            cache = new[] { a, b };
        }

        return cache;
    }
}

/// <summary>
/// Clear of a P2G uint accum buffer. Channels = value channels (count is BufferCount-1 plumbing).
/// </summary>
public readonly struct FieldAccumClearRequest : IEquatable<FieldAccumClearRequest>
{
    public string FieldName { get; }
    public int Channels { get; }

    public FieldAccumClearRequest(string fieldName, int channels)
    {
        FieldName = fieldName;
        Channels = channels;
    }

    public bool Equals(FieldAccumClearRequest other) =>
        string.Equals(FieldName, other.FieldName, StringComparison.Ordinal) &&
        Channels == other.Channels;

    public override bool Equals(object obj) => obj is FieldAccumClearRequest other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            FieldName != null ? StringComparer.Ordinal.GetHashCode(FieldName) : 0,
            Channels);
}

/// <summary>
/// Scatter (write) / Normalize (read) request for a P2G accum buffer.
/// Scale/Bias must match between Scatter and Normalize for the same field (Build hard-error).
/// </summary>
public readonly struct FieldAccumRequest : IEquatable<FieldAccumRequest>
{
    public string FieldName { get; }
    public int Channels { get; }
    public float Scale { get; }
    public float Bias { get; }

    public FieldAccumRequest(string fieldName, int channels, float scale, float bias)
    {
        FieldName = fieldName;
        Channels = channels;
        Scale = scale;
        Bias = bias;
    }

    public bool Equals(FieldAccumRequest other) =>
        string.Equals(FieldName, other.FieldName, StringComparison.Ordinal) &&
        Channels == other.Channels &&
        Scale.Equals(other.Scale) &&
        Bias.Equals(other.Bias);

    public override bool Equals(object obj) => obj is FieldAccumRequest other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            FieldName != null ? StringComparer.Ordinal.GetHashCode(FieldName) : 0,
            Channels,
            Scale,
            Bias);
}

internal static class FieldAccumClearRequestSets
{
    public static FieldAccumClearRequest[] Single(
        ref FieldAccumClearRequest[] cache,
        string fieldName,
        int channels)
    {
        FieldAccumClearRequest current = new FieldAccumClearRequest(fieldName, channels);
        if (cache == null || cache.Length != 1 || !cache[0].Equals(current))
        {
            cache = new[] { current };
        }

        return cache;
    }
}

internal static class FieldAccumRequestSets
{
    public static FieldAccumRequest[] Single(
        ref FieldAccumRequest[] cache,
        string fieldName,
        int channels,
        float scale,
        float bias)
    {
        FieldAccumRequest current = new FieldAccumRequest(fieldName, channels, scale, bias);
        if (cache == null || cache.Length != 1 || !cache[0].Equals(current))
        {
            cache = new[] { current };
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
    private static readonly FieldAccumClearRequest[] EmptyAccumClears = { };
    private static readonly FieldAccumRequest[] EmptyAccumRequests = { };

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

    /// <summary>P2G accum clears (ClearFieldAccumPass).</summary>
    public virtual IReadOnlyList<FieldAccumClearRequest> FieldAccumClears => EmptyAccumClears;

    /// <summary>P2G accum scatter writes (ParticleToFieldScatterPass).</summary>
    public virtual IReadOnlyList<FieldAccumRequest> FieldAccumWrites => EmptyAccumRequests;

    /// <summary>P2G accum normalize reads (NormalizeFieldAccumPass).</summary>
    public virtual IReadOnlyList<FieldAccumRequest> FieldAccumReads => EmptyAccumRequests;

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
/// Base for field kernels: binds textures via fixed FieldRead/FieldWrite slots, pushes FieldParams,
/// dispatches over the field resolution. Subclasses only override SetParams.
/// Single distinct field name per pass (multi-field-per-kernel is M2c).
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
        public FieldSlotRole Role;
        public int ReadId;
        public int WriteId;
    }

    protected abstract string KernelName { get; }
    protected KernelHandle Kernel => kernel;

    /// <summary>
    /// When false, sealed Execute early-outs without DispatchCompute (LastExecuteDispatched stays false).
    /// Used by one-shot passes (e.g. SeedScalarDiskPass) without breaking the sealed Execute path.
    /// </summary>
    protected virtual bool ShouldDispatch => true;

    private bool multiRoleBindings;

    /// <summary>
    /// Field whose plane/resolution drive FieldParams.
    /// Multi-role: Role=A. Single-role: first write, else first read.
    /// </summary>
    protected virtual string PrimaryFieldName
    {
        get
        {
            if (multiRoleBindings)
            {
                for (int i = 0; i < FieldWrites.Count; i++)
                {
                    if (FieldWrites[i].Role == FieldSlotRole.A)
                    {
                        return FieldWrites[i].FieldName;
                    }
                }

                for (int i = 0; i < FieldReads.Count; i++)
                {
                    if (FieldReads[i].Role == FieldSlotRole.A)
                    {
                        return FieldReads[i].FieldName;
                    }
                }
            }

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
        fieldBinds.Clear();
        multiRoleBindings = false;
        CollectFieldBinds(FieldReads);
        CollectFieldBinds(FieldWrites);
        AssignSlotIdsAndValidateRoles();
        ValidateAccessConflicts();

        string primary = PrimaryFieldName;
        if (string.IsNullOrEmpty(primary))
        {
            throw new InvalidOperationException(
                $"{DisplayName}: FieldKernelPass requires at least one field request.");
        }

        // Role guard runs before Fields/FindKernel so EditMode stubs can use null context.
        if (multiRoleBindings)
        {
            if (context == null || context.Fields == null)
            {
                throw new InvalidOperationException(
                    $"{DisplayName}: multi-field-per-kernel pass requires a SimContext with Fields.");
            }

            ValidateMatchingFieldGeometry(context);
        }

        // After unique-name / role guards so EditMode multi-field tests need no compute library.
        kernel = context.FindKernel(KernelName);
        primaryDescriptor = context.Fields.Get(primary).Descriptor;
    }

    public sealed override void Execute(SimContext context, float deltaTime)
    {
        LastExecuteDispatched = false;

        if (!kernel.IsValid || primaryDescriptor == null || !ShouldDispatch)
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

    /// <summary>
    /// Collects binds with roles; slot property IDs assigned in <see cref="AssignSlotIdsAndValidateRoles"/>.
    /// </summary>
    private void CollectFieldBinds(IReadOnlyList<FieldRequest> requests)
    {
        for (int i = 0; i < requests.Count; i++)
        {
            FieldRequest request = requests[i];
            fieldBinds.Add(new FieldBind
            {
                FieldName = request.FieldName,
                Access = request.Access,
                Role = request.Role,
                ReadId = 0,
                WriteId = 0,
            });
        }
    }

    /// <summary>
    /// Guard matrix (ADR-008 / M2c): per-role unique FieldName; roles exactly {A} or {A,B};
    /// single-role → legacy FieldRead/FieldWrite; multi-role → *A/*B.
    /// </summary>
    private void AssignSlotIdsAndValidateRoles()
    {
        if (fieldBinds.Count == 0)
        {
            multiRoleBindings = false;
            return;
        }

        Dictionary<FieldSlotRole, string> roleToName =
            new Dictionary<FieldSlotRole, string>();
        Dictionary<string, FieldSlotRole> nameToRole =
            new Dictionary<string, FieldSlotRole>(StringComparer.Ordinal);

        for (int i = 0; i < fieldBinds.Count; i++)
        {
            FieldBind bind = fieldBinds[i];
            if (roleToName.TryGetValue(bind.Role, out string existingName))
            {
                if (!string.Equals(existingName, bind.FieldName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{DisplayName}: FieldSlotRole.{bind.Role} is bound to both " +
                        $"'{existingName}' and '{bind.FieldName}'. " +
                        "At most one distinct field name is allowed per role (ADR-008).");
                }
            }
            else
            {
                roleToName.Add(bind.Role, bind.FieldName);
            }

            if (nameToRole.TryGetValue(bind.FieldName, out FieldSlotRole existingRole))
            {
                if (existingRole != bind.Role)
                {
                    throw new InvalidOperationException(
                        $"{DisplayName}: field '{bind.FieldName}' cannot use both " +
                        $"FieldSlotRole.{existingRole} and FieldSlotRole.{bind.Role}.");
                }
            }
            else
            {
                nameToRole.Add(bind.FieldName, bind.Role);
            }
        }

        bool hasA = roleToName.ContainsKey(FieldSlotRole.A);
        bool hasB = roleToName.ContainsKey(FieldSlotRole.B);
        if (hasA && hasB)
        {
            multiRoleBindings = true;
        }
        else if (hasA && !hasB)
        {
            multiRoleBindings = false;
        }
        else
        {
            throw new InvalidOperationException(
                $"{DisplayName}: field slot roles must be exactly {{A}} (single-field) or " +
                "{A, B} (multi-field). Role set {B} without A is not allowed (ADR-008).");
        }

        for (int i = 0; i < fieldBinds.Count; i++)
        {
            FieldBind bind = fieldBinds[i];
            if (multiRoleBindings)
            {
                if (bind.Role == FieldSlotRole.A)
                {
                    bind.ReadId = SimShaderIds.FieldReadA;
                    bind.WriteId = SimShaderIds.FieldWriteA;
                }
                else
                {
                    bind.ReadId = SimShaderIds.FieldReadB;
                    bind.WriteId = SimShaderIds.FieldWriteB;
                }
            }
            else
            {
                bind.ReadId = SimShaderIds.FieldRead;
                bind.WriteId = SimShaderIds.FieldWrite;
            }

            fieldBinds[i] = bind;
        }
    }

    private void ValidateMatchingFieldGeometry(SimContext context)
    {
        FieldDescriptor reference = null;
        string referenceName = null;

        for (int i = 0; i < fieldBinds.Count; i++)
        {
            string name = fieldBinds[i].FieldName;
            FieldDescriptor descriptor = context.Fields.Get(name).Descriptor;
            if (reference == null)
            {
                reference = descriptor;
                referenceName = name;
                continue;
            }

            if (descriptor.Resolution != reference.Resolution ||
                descriptor.Origin != reference.Origin ||
                descriptor.AxisU != reference.AxisU ||
                descriptor.AxisV != reference.AxisV ||
                descriptor.Size != reference.Size)
            {
                throw new InvalidOperationException(
                    $"{DisplayName}: multi-field-per-kernel requires matching Resolution and plane " +
                    $"(Origin, AxisU, AxisV, Size) for all roles. " +
                    $"'{referenceName}' and '{name}' differ (ADR-008 / M2c).");
            }
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
