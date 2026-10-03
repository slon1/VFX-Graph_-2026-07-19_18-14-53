using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Build-time checks for one spatial-hash builder, its consumers, and BoxBounds (ADR-034).
/// </summary>
internal static class SpatialHashValidator
{
    private const float BoundsTolerance = 1e-3f;

    /// <summary>Throws InvalidOperationException on errors; returns warnings for the caller to log.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<SimPass> passes, EffectAsset effect = null)
    {
        List<string> warnings = new List<string>();
        if (passes == null)
        {
            ValidateTeams(effect, null);
            return warnings;
        }

        BuildSpatialHashPass builder = null;
        int builderIndex = -1;
        List<int> consumerIndices = new List<int>();
        List<BoxBoundsPass> bounds = new List<BoxBoundsPass>();

        for (int i = 0; i < passes.Count; i++)
        {
            SimPass pass = passes[i];
            if (pass == null || !pass.Enabled)
            {
                continue;
            }

            if (pass is BuildSpatialHashPass hashPass)
            {
                if (builder != null)
                {
                    throw new InvalidOperationException(
                        "SimulationWorld: only one enabled 'Build Spatial Hash' pass per effect.");
                }

                builder = hashPass;
                builderIndex = i;
            }

            if (pass is ISpatialHashConsumer)
            {
                consumerIndices.Add(i);
            }

            if (pass is BoxBoundsPass box)
            {
                bounds.Add(box);
            }
        }

        if (builder == null)
        {
            if (consumerIndices.Count > 0)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: a spatial hash consumer requires an enabled 'Build Spatial Hash' pass before it.");
            }

            ValidateTeams(effect, null);
            return warnings;
        }

        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(builder.Center, builder.Extents, builder.MinCellSize);
        SpatialHashSet.ValidateLayout(layout, builder.Wrap);

        for (int i = 0; i < consumerIndices.Count; i++)
        {
            if (consumerIndices[i] < builderIndex)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: a spatial hash consumer stands before 'Build Spatial Hash'.");
            }
        }

        if (builder.Wrap)
        {
            bool hasWrap = false;
            for (int i = 0; i < bounds.Count; i++)
            {
                if (bounds[i].Behaviour == BoundsBehaviour.Bounce)
                {
                    throw new InvalidOperationException(
                        "SimulationWorld: wrap spatial hash cannot sit with a Bounce BoxBounds pass. " +
                        "Phantom neighbours would appear across the seam.");
                }

                if (bounds[i].Behaviour == BoundsBehaviour.Wrap)
                {
                    hasWrap = true;
                }
            }

            if (!hasWrap)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: wrap spatial hash needs an enabled BoxBounds Wrap pass. " +
                    "Without it the hash wraps particles the world does not, and phantom neighbours appear across the seam.");
            }
        }
        else
        {
            for (int i = 0; i < bounds.Count; i++)
            {
                if (bounds[i].Behaviour == BoundsBehaviour.Wrap)
                {
                    throw new InvalidOperationException(
                        "SimulationWorld: a non-wrapping spatial hash cannot sit with a Wrap BoxBounds pass.");
                }
            }

            if (bounds.Count == 0)
            {
                warnings.Add(
                    "Spatial hash wrap is off and there is no BoxBounds pass: " +
                    "particles may leave the hash grid and pile up in edge cells.");
            }
        }

        for (int i = 0; i < bounds.Count; i++)
        {
            BoxBoundsPass box = bounds[i];
            if (Mathf.Abs(builder.Center.x - box.Center.x) > BoundsTolerance ||
                Mathf.Abs(builder.Center.z - box.Center.z) > BoundsTolerance ||
                Mathf.Abs(builder.Extents.x - box.Extents.x) > BoundsTolerance ||
                Mathf.Abs(builder.Extents.z - box.Extents.z) > BoundsTolerance)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: spatial hash bounds " +
                    $"(center {builder.Center}, extents {builder.Extents}) do not match BoxBounds " +
                    $"(center {box.Center}, extents {box.Extents}).");
            }
        }

        ValidateTeams(effect, builder);
        return warnings;
    }

    private static void ValidateTeams(EffectAsset effect, BuildSpatialHashPass builder)
    {
        if (effect == null)
        {
            return;
        }

        IReadOnlyList<TeamProfile> teams = effect.Teams;
        if (teams.Count > TeamProfile.MaxTeams)
        {
            throw new InvalidOperationException(
                "SimulationWorld: team list has " + teams.Count + " entries; the maximum is " +
                TeamProfile.MaxTeams + ".");
        }

        for (int i = 0; i < teams.Count; i++)
        {
            TeamProfile profile = teams[i];
            if (profile == null)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: team list entry " + i + " is null.");
            }

            RequireNonNegative(profile.SeparationRadius, "separationRadius");
            RequireNonNegative(profile.AlignmentRadius, "alignmentRadius");
            RequireNonNegative(profile.CohesionRadius, "cohesionRadius");
            RequireNonNegative(profile.InterGroupSeparationMultiplier, "interGroupSeparationMultiplier");
        }

        if (effect.ResolveSource() is SwarmSource swarm && swarm.Spawns != null)
        {
            IReadOnlyList<SwarmSource.Spawn> spawns = swarm.Spawns;
            for (int i = 0; i < spawns.Count; i++)
            {
                SwarmSource.Spawn spawn = spawns[i];
                if (spawn == null)
                {
                    throw new InvalidOperationException(
                        "SimulationWorld: swarm spawn " + i + " is null.");
                }

                if (spawn.TeamIndex >= teams.Count)
                {
                    throw new InvalidOperationException(
                        "SimulationWorld: swarm spawn " + i + " teamIndex " + spawn.TeamIndex +
                        " is outside the team list of length " + teams.Count + ".");
                }
            }
        }

        if (teams.Count == 0 || builder == null)
        {
            return;
        }

        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(
            builder.Center, builder.Extents, builder.MinCellSize);
        float limit = Mathf.Min(layout.CellSize.x, layout.CellSize.y) * (1f + SpatialHashSet.GridEpsilon);
        for (int i = 0; i < teams.Count; i++)
        {
            TeamProfile profile = teams[i];
            RequireWithinCell(profile.SeparationRadius, "separationRadius", limit);
            RequireWithinCell(profile.AlignmentRadius, "alignmentRadius", limit);
            RequireWithinCell(profile.CohesionRadius, "cohesionRadius", limit);
        }
    }

    private static void RequireNonNegative(float value, string name)
    {
        if (value < 0f)
        {
            throw new ArgumentOutOfRangeException(name, value, name + " must be >= 0.");
        }
    }

    private static void RequireWithinCell(float value, string name, float limit)
    {
        if (value > limit)
        {
            throw new InvalidOperationException(
                "SimulationWorld: team " + name + " " + value + " exceeds the hash cell limit " + limit + ".");
        }
    }
}
