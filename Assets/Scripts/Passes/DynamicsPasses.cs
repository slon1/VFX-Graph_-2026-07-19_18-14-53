using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Semi-implicit Euler: position += velocity * dt. Put after all force passes.</summary>
[Serializable]
public sealed class IntegratePass : ParticleKernelPass
{
    public override string DisplayName => "Integrate";
    public override PassCategory Category => PassCategory.Dynamics;
    protected override string KernelName => "Integrate";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Velocity;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Position;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}

[Serializable]
public sealed class SpeedLimitPass : ParticleKernelPass
{
    private static readonly int MaxSpeedId = Shader.PropertyToID("MaxSpeed");

    [SerializeField, Min(0f)] private float maxSpeed = 10f;

    public float MaxSpeed
    {
        get => maxSpeed;
        set => maxSpeed = value;
    }

    public override string DisplayName => "Speed Limit";
    public override PassCategory Category => PassCategory.Dynamics;
    protected override string KernelName => "SpeedLimit";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, MaxSpeedId, maxSpeed);
    }
}

[Serializable]
public sealed class PlaneColliderPass : ParticleKernelPass
{
    private static readonly int PlanePointId = Shader.PropertyToID("PlanePoint");
    private static readonly int PlaneNormalId = Shader.PropertyToID("PlaneNormal");
    private static readonly int PlaneBounceId = Shader.PropertyToID("PlaneBounce");
    private static readonly int PlaneFrictionId = Shader.PropertyToID("PlaneFriction");

    [SerializeField] private Vector3 point = new Vector3(0f, -1f, 0f);
    [SerializeField] private Vector3 normal = Vector3.up;
    [SerializeField, Range(0f, 1f)] private float bounce = 0.4f;
    [SerializeField, Range(0f, 1f)] private float friction = 0.2f;

    public Vector3 Point
    {
        get => point;
        set => point = value;
    }

    public Vector3 Normal
    {
        get => normal;
        set => normal = value;
    }

    public float Bounce
    {
        get => bounce;
        set => bounce = value;
    }

    public float Friction
    {
        get => friction;
        set => friction = value;
    }

    public override string DisplayName => "Plane Collider";
    public override PassCategory Category => PassCategory.Dynamics;
    protected override string KernelName => "PlaneCollider";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.PositionVelocity;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.PositionVelocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetVector(context, PlanePointId, point);
        SetVector(context, PlaneNormalId, normal);
        SetFloat(context, PlaneBounceId, bounce);
        SetFloat(context, PlaneFrictionId, friction);
    }
}

[Serializable]
public sealed class SphereColliderPass : ParticleKernelPass
{
    private static readonly int SphereCenterId = Shader.PropertyToID("SphereCenter");
    private static readonly int SphereRadiusId = Shader.PropertyToID("SphereRadius");
    private static readonly int SphereBounceId = Shader.PropertyToID("SphereBounce");
    private static readonly int SphereFrictionId = Shader.PropertyToID("SphereFriction");

    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField, Min(0f)] private float radius = 1f;
    [SerializeField, Range(0f, 1f)] private float bounce = 0.5f;
    [SerializeField, Range(0f, 1f)] private float friction = 0.1f;

    public Vector3 Center
    {
        get => center;
        set => center = value;
    }

    public float Radius
    {
        get => radius;
        set => radius = value;
    }

    public float Bounce
    {
        get => bounce;
        set => bounce = value;
    }

    public float Friction
    {
        get => friction;
        set => friction = value;
    }

    public override string DisplayName => "Sphere Collider";
    public override PassCategory Category => PassCategory.Dynamics;
    protected override string KernelName => "SphereCollider";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.PositionVelocity;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.PositionVelocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetVector(context, SphereCenterId, center);
        SetFloat(context, SphereRadiusId, radius);
        SetFloat(context, SphereBounceId, bounce);
        SetFloat(context, SphereFrictionId, friction);
    }
}

public enum BoundsBehaviour
{
    Bounce = 0,
    Wrap = 1,
}

[Serializable]
public sealed class BoxBoundsPass : ParticleKernelPass
{
    private static readonly int BoundsCenterId = Shader.PropertyToID("BoundsCenter");
    private static readonly int BoundsExtentsId = Shader.PropertyToID("BoundsExtents");
    private static readonly int BoundsBounceId = Shader.PropertyToID("BoundsBounce");
    private static readonly int BoundsModeId = Shader.PropertyToID("BoundsMode");

    [SerializeField] private Vector3 center = Vector3.zero;
    [SerializeField] private Vector3 extents = new Vector3(2f, 2f, 2f);
    [SerializeField] private BoundsBehaviour behaviour = BoundsBehaviour.Bounce;
    [SerializeField, Range(0f, 1f)] private float bounce = 0.6f;

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

    public BoundsBehaviour Behaviour
    {
        get => behaviour;
        set => behaviour = value;
    }

    public float Bounce
    {
        get => bounce;
        set => bounce = value;
    }

    public override string DisplayName => "Box Bounds";
    public override PassCategory Category => PassCategory.Dynamics;
    protected override string KernelName => "BoxBounds";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.PositionVelocity;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.PositionVelocity;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetVector(context, BoundsCenterId, center);
        SetVector(context, BoundsExtentsId, extents);
        SetFloat(context, BoundsBounceId, bounce);
        SetInt(context, BoundsModeId, (int)behaviour);
    }
}
