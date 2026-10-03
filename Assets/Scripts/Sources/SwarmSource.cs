using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns XZ disks with a shared heading and teamId (ADR-036). Does not write velocity.
/// </summary>
[Serializable]
public sealed class SwarmSource : IDataSource
{
    private const float ZeroDirectionSqr = 1e-6f;

    [SerializeField] private List<Spawn> spawns = new List<Spawn>();
    [SerializeField] private int seed = 1;
    [SerializeField] private float jitterDegrees;

    public string Name => "Swarm";

    public List<Spawn> Spawns
    {
        get => spawns;
        set => spawns = value;
    }

    public int Seed
    {
        get => seed;
        set => seed = value;
    }

    public float JitterDegrees
    {
        get => jitterDegrees;
        set
        {
            if (value < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "jitterDegrees must be >= 0.");
            }

            jitterDegrees = value;
        }
    }

    public void Setup(ParticleSet particles)
    {
        if (jitterDegrees < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jitterDegrees), jitterDegrees, "jitterDegrees must be >= 0.");
        }

        List<Spawn> list = spawns;
        int spawnCount = list != null ? list.Count : 0;
        long sum = 0;
        for (int i = 0; i < spawnCount; i++)
        {
            Spawn spawn = list[i];
            if (spawn == null)
            {
                throw new ArgumentNullException(nameof(spawns), "A swarm spawn entry is null.");
            }

            if (spawn.TeamIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spawn.TeamIndex), spawn.TeamIndex, "teamIndex must be >= 0.");
            }

            if (spawn.Count < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spawn.Count), spawn.Count, "count must be >= 0.");
            }

            if (spawn.Radius < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spawn.Radius), spawn.Radius, "radius must be >= 0.");
            }

            sum += spawn.Count;
        }

        if (sum > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spawns), sum, "Swarm particle count does not fit in int.");
        }

        int count = (int)sum;
        particles.EnsureCapacity(count);
        if (count == 0)
        {
            return;
        }

        Vector3[] rest = new Vector3[count];
        Vector3[] heading = new Vector3[count];
        uint[] teams = new uint[count];
        System.Random random = new System.Random(seed);
        double jitterRadians = jitterDegrees * Mathf.Deg2Rad;
        int cursor = 0;

        for (int i = 0; i < spawnCount; i++)
        {
            Spawn spawn = list[i];
            for (int n = 0; n < spawn.Count; n++)
            {
                double u = random.NextDouble();
                double v = random.NextDouble();
                double w = random.NextDouble();
                double radius = spawn.Radius * Math.Sqrt(u);
                double theta = v * 2.0 * Math.PI;

                rest[cursor] = new Vector3(
                    spawn.Center.x + (float)(Math.Cos(theta) * radius),
                    0f,
                    spawn.Center.y + (float)(Math.Sin(theta) * radius));

                double angle = HeadingAngle(spawn.InitialDirection, w, jitterRadians);
                heading[cursor] = new Vector3((float)Math.Cos(angle), 0f, (float)Math.Sin(angle));
                teams[cursor] = (uint)spawn.TeamIndex;
                cursor++;
            }
        }

        particles.RegisterAttribute(BuiltinAttributes.RestPosition).SetData(rest);
        particles.RegisterAttribute(BuiltinAttributes.Heading).SetData(heading);
        particles.RegisterAttribute(BuiltinAttributes.TeamId).SetData(teams);
    }

    public void Tick(ParticleSet particles)
    {
    }

    private static double HeadingAngle(Vector2 direction, double w, double jitterRadians)
    {
        if (direction.sqrMagnitude < ZeroDirectionSqr)
        {
            return w * 2.0 * Math.PI;
        }

        double baseAngle = Math.Atan2(direction.y, direction.x);
        return baseAngle + (w * 2.0 - 1.0) * jitterRadians;
    }

    /// <summary>One disk on the XZ plane. Vector2.y is world Z.</summary>
    [Serializable]
    public sealed class Spawn
    {
        [SerializeField] private int teamIndex;
        [SerializeField] private int count;
        [SerializeField] private Vector2 center;
        [SerializeField] private float radius;
        [SerializeField] private Vector2 initialDirection;

        public int TeamIndex
        {
            get => teamIndex;
            set => teamIndex = value;
        }

        public int Count
        {
            get => count;
            set => count = value;
        }

        public Vector2 Center
        {
            get => center;
            set => center = value;
        }

        public float Radius
        {
            get => radius;
            set => radius = value;
        }

        public Vector2 InitialDirection
        {
            get => initialDirection;
            set => initialDirection = value;
        }
    }
}
