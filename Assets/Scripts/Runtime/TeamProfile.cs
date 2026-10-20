using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One swarm team on an effect (ADR-037). Color is stored for a later palette and is not uploaded.
/// </summary>
[Serializable]
public sealed class TeamProfile
{
    public const int MaxTeams = 8;

    [SerializeField] private string name = "";
    [SerializeField] private float separationRadius;
    [SerializeField] private float alignmentRadius;
    [SerializeField] private float cohesionRadius;
    [SerializeField] private float interGroupSeparationMultiplier = 1f;
    [SerializeField] private float separationWeight;
    [SerializeField] private float alignmentWeight;
    [SerializeField] private float cohesionWeight;
    [SerializeField] private float cruise;
    [SerializeField] private float turn;
    [SerializeField] private Gradient color = new Gradient();

    public string Name
    {
        get => name;
        set => name = value;
    }

    public float SeparationRadius
    {
        get => separationRadius;
        set => separationRadius = NonNegative(value, nameof(value));
    }

    public float AlignmentRadius
    {
        get => alignmentRadius;
        set => alignmentRadius = NonNegative(value, nameof(value));
    }

    public float CohesionRadius
    {
        get => cohesionRadius;
        set => cohesionRadius = NonNegative(value, nameof(value));
    }

    public float InterGroupSeparationMultiplier
    {
        get => interGroupSeparationMultiplier;
        set => interGroupSeparationMultiplier = NonNegative(value, nameof(value));
    }

    public float SeparationWeight
    {
        get => separationWeight;
        set => separationWeight = value;
    }

    public float AlignmentWeight
    {
        get => alignmentWeight;
        set => alignmentWeight = value;
    }

    public float CohesionWeight
    {
        get => cohesionWeight;
        set => cohesionWeight = value;
    }

    public float Cruise
    {
        get => cruise;
        set => cruise = NonNegative(value, nameof(value));
    }

    public float Turn
    {
        get => turn;
        set => turn = NonNegative(value, nameof(value));
    }

    public Gradient Color
    {
        get => color;
        set => color = value;
    }

    private static float NonNegative(float value, string paramName)
    {
        if (value < 0f)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be >= 0.");
        }

        return value;
    }
}

/// <summary>
/// One editor-placed attraction or repulsion point (ADR-049). Position is XZ; Y is unused.
/// </summary>
[Serializable]
public sealed class SwarmPoint
{
    public const int MaxPoints = 16;

    [SerializeField, Tooltip("Позиция в плоскости XZ; Y не используется")]
    private Vector2 positionXZ;

    [SerializeField, Range(-10f, 10f)] private float weight = 0.6f;
    [SerializeField, Min(0f)] private float radius = 10f;
    [SerializeField, Range(0f, 1f)] private float edge = 0.25f;

    public Vector2 PositionXZ
    {
        get => positionXZ;
        set => positionXZ = value;
    }

    public float Weight
    {
        get => weight;
        set => weight = Mathf.Clamp(value, -10f, 10f);
    }

    public float Radius
    {
        get => radius;
        set => radius = NonNegative(value);
    }

    public float Edge
    {
        get => edge;
        set => edge = Mathf.Clamp01(value);
    }

    private static float NonNegative(float value)
    {
        if (value < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value must be >= 0.");
        }

        return value;
    }
}

/// <summary>
/// GPU slot for one team. Three float4s, 48 bytes. Name and color stay on <see cref="TeamProfile"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TeamParams
{
    public Vector4 Radii;
    public Vector4 Weights;
    public Vector4 Motion;

    public const int Stride = 48;

    public static void Upload(GraphicsBuffer buffer, IReadOnlyList<TeamProfile> teams)
    {
        Upload(buffer, teams, new TeamParams[TeamProfile.MaxTeams]);
    }

    internal static void Upload(GraphicsBuffer buffer, IReadOnlyList<TeamProfile> teams, TeamParams[] scratch)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (buffer.count != TeamProfile.MaxTeams)
        {
            throw new ArgumentException(
                "Team buffer length must be " + TeamProfile.MaxTeams + ", got " + buffer.count + ".",
                nameof(buffer));
        }

        if (scratch == null || scratch.Length != TeamProfile.MaxTeams)
        {
            throw new ArgumentException(
                "Team upload scratch must hold " + TeamProfile.MaxTeams + " slots.",
                nameof(scratch));
        }

        for (int i = 0; i < TeamProfile.MaxTeams; i++)
        {
            scratch[i] = default;
        }

        int count = teams != null ? teams.Count : 0;
        int live = count < TeamProfile.MaxTeams ? count : TeamProfile.MaxTeams;
        for (int i = 0; i < live; i++)
        {
            TeamProfile profile = teams[i];
            if (profile == null)
            {
                continue;
            }

            scratch[i] = new TeamParams
            {
                Radii = new Vector4(
                    profile.SeparationRadius,
                    profile.AlignmentRadius,
                    profile.CohesionRadius,
                    profile.InterGroupSeparationMultiplier),
                Weights = new Vector4(
                    profile.SeparationWeight,
                    profile.AlignmentWeight,
                    profile.CohesionWeight,
                    0f),
                Motion = new Vector4(profile.Cruise, profile.Turn, 0f, 0f),
            };
        }

        buffer.SetData(scratch);
    }
}

/// <summary>
/// One swarm point on the GPU: two float4s, 32 bytes.
/// The buffer itself is float4 elements (2 per point) so the kernel can index 2k and 2k+1.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SwarmPointParams
{
    public Vector4 Packed;
    public Vector4 Edge;

    public const int Stride = 32;
    public const int Float4Count = SwarmPoint.MaxPoints * 2;

    public static void Upload(GraphicsBuffer buffer, IReadOnlyList<SwarmPoint> points)
    {
        Upload(buffer, points, new Vector4[Float4Count]);
    }

    internal static void Upload(GraphicsBuffer buffer, IReadOnlyList<SwarmPoint> points, Vector4[] scratch)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (buffer.count != Float4Count || buffer.stride != sizeof(float) * 4)
        {
            throw new ArgumentException(
                "Swarm point buffer must hold " + Float4Count + " float4 slots, got count " +
                buffer.count + " stride " + buffer.stride + ".",
                nameof(buffer));
        }

        if (scratch == null || scratch.Length != Float4Count)
        {
            throw new ArgumentException(
                "Swarm point upload scratch must hold " + Float4Count + " float4 slots.",
                nameof(scratch));
        }

        for (int i = 0; i < scratch.Length; i++)
        {
            scratch[i] = default;
        }

        int count = points != null ? points.Count : 0;
        int live = count < SwarmPoint.MaxPoints ? count : SwarmPoint.MaxPoints;
        for (int i = 0; i < live; i++)
        {
            SwarmPoint point = points[i];
            if (point == null)
            {
                continue;
            }

            Vector2 xz = point.PositionXZ;
            scratch[2 * i] = new Vector4(xz.x, xz.y, point.Weight, point.Radius);
            scratch[2 * i + 1] = new Vector4(point.Edge, 0f, 0f, 0f);
        }

        buffer.SetData(scratch);
    }
}
