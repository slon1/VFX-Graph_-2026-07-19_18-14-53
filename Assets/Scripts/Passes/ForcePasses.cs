using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public sealed class GravityPass : ParticleKernelPass
{
    private static readonly int GravityVectorId = Shader.PropertyToID("GravityVector");

    [SerializeField] private Vector3 gravity = new Vector3(0f, -9.81f, 0f);

    public Vector3 Gravity
    {
        get => gravity;
        set => gravity = value;
    }

    public override string DisplayName => "Gravity";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "Gravity";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetVector(context, GravityVectorId, gravity);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}

/// <summary>Exponential velocity damping. Factor is precomputed on CPU for stability.</summary>
[Serializable]
public sealed class DragPass : ParticleKernelPass
{
    private static readonly int DragFactorId = Shader.PropertyToID("DragFactor");

    [SerializeField, Min(0f)] private float drag = 1f;

    public float Drag
    {
        get => drag;
        set => drag = value;
    }

    public override string DisplayName => "Drag";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "Drag";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, DragFactorId, Mathf.Exp(-drag * deltaTime));
    }
}

[Serializable]
public sealed class VortexPass : ParticleKernelPass
{
    private static readonly int VortexCenterId = Shader.PropertyToID("VortexCenter");
    private static readonly int VortexAxisId = Shader.PropertyToID("VortexAxis");
    private static readonly int VortexStrengthId = Shader.PropertyToID("VortexStrength");
    private static readonly int VortexRadiusId = Shader.PropertyToID("VortexRadius");

    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField] private Vector3 axis = Vector3.up;
    [SerializeField] private float strength = 5f;
    [SerializeField, Min(0.01f)] private float radius = 5f;

    public Vector3 Center
    {
        get => center;
        set => center = value;
    }

    public Vector3 Axis
    {
        get => axis;
        set => axis = value;
    }

    public float Strength
    {
        get => strength;
        set => strength = value;
    }

    public float Radius
    {
        get => radius;
        set => radius = value;
    }

    public override string DisplayName => "Vortex";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "Vortex";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetVector(context, VortexCenterId, center);
        SetVector(context, VortexAxisId, axis);
        SetFloat(context, VortexStrengthId, strength);
        SetFloat(context, VortexRadiusId, radius);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}

/// <summary>Shared base for Attractor/Repulsor — one PointForce kernel, signed strength.</summary>
[Serializable]
public abstract class PointForcePassBase : ParticleKernelPass
{
    private static readonly int PointForceCenterId = Shader.PropertyToID("PointForceCenter");
    private static readonly int PointForceStrengthId = Shader.PropertyToID("PointForceStrength");
    private static readonly int PointForceRadiusId = Shader.PropertyToID("PointForceRadius");

    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField, Min(0f)] private float strength = 5f;
    [SerializeField, Min(0.01f)] private float radius = 5f;

    public Vector3 Center
    {
        get => center;
        set => center = value;
    }

    public float Strength
    {
        get => strength;
        set => strength = value;
    }

    public float Radius
    {
        get => radius;
        set => radius = value;
    }

    protected abstract float Sign { get; }

    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "PointForce";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetVector(context, PointForceCenterId, center);
        SetFloat(context, PointForceStrengthId, strength * Sign);
        SetFloat(context, PointForceRadiusId, radius);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}

[Serializable]
public sealed class AttractorPass : PointForcePassBase
{
    public override string DisplayName => "Attractor";
    protected override float Sign => 1f;
}

[Serializable]
public sealed class RepulsorPass : PointForcePassBase
{
    public override string DisplayName => "Repulsor";
    protected override float Sign => -1f;
}

[Serializable]
public sealed class NoiseForcePass : ParticleKernelPass
{
    private static readonly int NoiseFrequencyId = Shader.PropertyToID("NoiseFrequency");
    private static readonly int NoiseAmplitudeId = Shader.PropertyToID("NoiseAmplitude");
    private static readonly int NoiseSpeedId = Shader.PropertyToID("NoiseSpeed");

    [SerializeField, Min(0f)] private float frequency = 1f;
    [SerializeField] private float amplitude = 2f;
    [SerializeField] private float speed = 0.5f;

    public float Frequency
    {
        get => frequency;
        set => frequency = value;
    }

    public float Amplitude
    {
        get => amplitude;
        set => amplitude = value;
    }

    public float Speed
    {
        get => speed;
        set => speed = value;
    }

    public override string DisplayName => "Noise Force";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "NoiseForce";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, NoiseFrequencyId, frequency);
        SetFloat(context, NoiseAmplitudeId, amplitude);
        SetFloat(context, NoiseSpeedId, speed);
        SetFloat(context, SimShaderIds.SimTime, context.Time);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}

/// <summary>Divergence-free swirling noise. The most expensive force (6 noise evals per particle).</summary>
[Serializable]
public sealed class CurlNoisePass : ParticleKernelPass
{
    private static readonly int CurlFrequencyId = Shader.PropertyToID("CurlFrequency");
    private static readonly int CurlAmplitudeId = Shader.PropertyToID("CurlAmplitude");
    private static readonly int CurlSpeedId = Shader.PropertyToID("CurlSpeed");

    [SerializeField, Min(0f)] private float frequency = 0.8f;
    [SerializeField] private float amplitude = 2f;
    [SerializeField] private float speed = 0.3f;

    public float Frequency
    {
        get => frequency;
        set => frequency = value;
    }

    public float Amplitude
    {
        get => amplitude;
        set => amplitude = value;
    }

    public float Speed
    {
        get => speed;
        set => speed = value;
    }

    public override string DisplayName => "Curl Noise";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "CurlNoiseForce";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, CurlFrequencyId, frequency);
        SetFloat(context, CurlAmplitudeId, amplitude);
        SetFloat(context, CurlSpeedId, speed);
        SetFloat(context, SimShaderIds.SimTime, context.Time);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}

[Serializable]
public sealed class TurbulencePass : ParticleKernelPass
{
    private static readonly int TurbFrequencyId = Shader.PropertyToID("TurbFrequency");
    private static readonly int TurbAmplitudeId = Shader.PropertyToID("TurbAmplitude");
    private static readonly int TurbOctavesId = Shader.PropertyToID("TurbOctaves");
    private static readonly int TurbSpeedId = Shader.PropertyToID("TurbSpeed");

    [SerializeField, Min(0f)] private float frequency = 1f;
    [SerializeField] private float amplitude = 2f;
    [SerializeField, Range(1, 6)] private int octaves = 3;
    [SerializeField] private float speed = 0.5f;

    public float Frequency
    {
        get => frequency;
        set => frequency = value;
    }

    public float Amplitude
    {
        get => amplitude;
        set => amplitude = value;
    }

    public int Octaves
    {
        get => octaves;
        set => octaves = value;
    }

    public float Speed
    {
        get => speed;
        set => speed = value;
    }

    public override string DisplayName => "Turbulence";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "TurbulenceForce";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, TurbFrequencyId, frequency);
        SetFloat(context, TurbAmplitudeId, amplitude);
        SetInt(context, TurbOctavesId, octaves);
        SetFloat(context, TurbSpeedId, speed);
        SetFloat(context, SimShaderIds.SimTime, context.Time);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}

/// <summary>
/// Finger interaction: drag term smears particles along finger movement,
/// push term repels them radially from the touch point.
/// </summary>
[Serializable]
public sealed class TouchForcePass : ParticleKernelPass
{
    private static readonly int TouchDragStrengthId = Shader.PropertyToID("TouchDragStrength");
    private static readonly int TouchPushStrengthId = Shader.PropertyToID("TouchPushStrength");

    [SerializeField, Min(0f)] private float dragStrength = 1f;
    [SerializeField] private float pushStrength = 0f;

    public float DragStrength
    {
        get => dragStrength;
        set => dragStrength = value;
    }

    public float PushStrength
    {
        get => pushStrength;
        set => pushStrength = value;
    }

    public override string DisplayName => "Touch Force";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "TouchImpulse";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        BindBuffer(context, SimShaderIds.Touches, context.TouchBuffer);
        SetInt(context, SimShaderIds.TouchCount, context.TouchCount);
        SetFloat(context, TouchDragStrengthId, dragStrength);
        SetFloat(context, TouchPushStrengthId, pushStrength);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}
