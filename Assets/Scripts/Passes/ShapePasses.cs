using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// First pass of a shape chain: rebuilds position from restPosition every frame,
/// so shape operators stay stateless (no feedback on their own output).
/// </summary>
[Serializable]
public sealed class CopyRestPass : ParticleKernelPass
{
    public override string DisplayName => "Copy Rest";
    public override PassCategory Category => PassCategory.Shape;
    protected override string KernelName => "CopyRest";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.RestPosition;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Position;
}

/// <summary>
/// Twist around Y. The phase accumulates strength * dt on the CPU, so the result
/// is deterministic for a given phase and reacts live to strength changes.
/// </summary>
[Serializable]
public sealed class TwistPass : ParticleKernelPass
{
    private static readonly int TwistPhaseId = Shader.PropertyToID("TwistPhase");

    [SerializeField] private float strength = 1f;

    private float phase;

    public float Strength
    {
        get => strength;
        set => strength = value;
    }

    public override string DisplayName => "Twist";
    public override PassCategory Category => PassCategory.Shape;
    protected override string KernelName => "Twist";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Position;

    public override void Initialize(SimContext context)
    {
        base.Initialize(context);
        phase = 0f;
    }

    protected override void SetParams(SimContext context, float deltaTime)
    {
        phase += strength * deltaTime;
        SetFloat(context, TwistPhaseId, phase);
    }
}

/// <summary>
/// Pulls particles back to their rest positions via a damped spring (writes velocity).
/// Base for morphing / reactive "return to shape" effects.
/// </summary>
[Serializable]
public sealed class SpringToRestPass : ParticleKernelPass
{
    private static readonly int SpringStiffnessId = Shader.PropertyToID("SpringStiffness");
    private static readonly int SpringDampingId = Shader.PropertyToID("SpringDamping");

    [SerializeField, Min(0f)] private float stiffness = 10f;
    [SerializeField, Min(0f)] private float damping = 2f;

    public float Stiffness
    {
        get => stiffness;
        set => stiffness = value;
    }

    public float Damping
    {
        get => damping;
        set => damping = value;
    }

    public override string DisplayName => "Spring To Rest";
    public override PassCategory Category => PassCategory.Shape;
    protected override string KernelName => "SpringToRest";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.PositionRest;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SpringStiffnessId, stiffness);
        SetFloat(context, SpringDampingId, damping);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}
