using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pass classification is by role in the frame, not by data touched:
/// data is already machine-expressed via Reads/Writes.
/// </summary>
public enum PassCategory
{
    Shape = 0,
    Force = 1,
    Dynamics = 2,
}

/// <summary>Shader property ids shared by all passes.</summary>
internal static class SimShaderIds
{
    public static readonly int ParticleCount = Shader.PropertyToID("ParticleCount");
    public static readonly int DeltaTime = Shader.PropertyToID("DeltaTime");
    public static readonly int SimTime = Shader.PropertyToID("SimTime");
    public static readonly int Touches = Shader.PropertyToID("Touches");
    public static readonly int TouchCount = Shader.PropertyToID("TouchCount");
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
/// Atomic unit of the simulation pipeline. Serialized polymorphically
/// ([SerializeReference]) inside EffectAsset.
/// </summary>
[Serializable]
public abstract class SimPass
{
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
