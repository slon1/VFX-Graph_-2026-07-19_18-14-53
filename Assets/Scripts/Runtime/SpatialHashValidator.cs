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
            ValidateTeams(effect, null, null, false);
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

        bool fieldChain = HasEnabledField(passes);
        if (fieldChain)
        {
            ValidateFieldChain(passes, effect, builder);
        }
        else if (builder != null && builder.RefreshInterval > 1)
        {
            throw new InvalidOperationException(
                "SimulationWorld: Build Spatial Hash RefreshInterval above 1 is only valid in a field-neighbor chain.");
        }

        if (builder == null)
        {
            if (consumerIndices.Count > 0)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: a spatial hash consumer requires an enabled 'Build Spatial Hash' pass before it.");
            }

            ValidateTeams(effect, null, passes, fieldChain);
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

        ValidateTeams(effect, builder, passes, fieldChain);
        return warnings;
    }

    private static void ValidateFieldChain(
        IReadOnlyList<SimPass> passes,
        EffectAsset effect,
        BuildSpatialHashPass builder)
    {
        FieldNeighborCollectPass collect = null;
        int collectIndex = -1;
        int convolveIndex = -1;
        int hashIndex = -1;
        for (int i = 0; i < passes.Count; i++)
        {
            SimPass pass = passes[i];
            if (pass == null || !pass.Enabled)
            {
                continue;
            }

            if (pass is BuildSpatialHashPass)
            {
                hashIndex = i;
            }

            if (pass is BoidNeighborForcePass)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: Boid Neighbor Force cannot sit in a field-neighbor chain.");
            }

            if (pass is FieldNeighborCollectPass foundCollect)
            {
                if (collect != null)
                {
                    throw new InvalidOperationException(
                        "SimulationWorld: only one enabled field-neighbor collect pass per effect.");
                }

                collect = foundCollect;
                collectIndex = i;
            }

            if (pass is FieldNeighborConvolvePass)
            {
                if (convolveIndex >= 0)
                {
                    throw new InvalidOperationException(
                        "SimulationWorld: only one enabled field-neighbor convolve pass per effect.");
                }

                convolveIndex = i;
            }
        }

        if (collect == null || convolveIndex < 0)
        {
            throw new InvalidOperationException(
                "SimulationWorld: a field-neighbor chain requires Field Neighbor Collect before Field Neighbor Convolve.");
        }

        if (convolveIndex >= 0 && convolveIndex < collectIndex)
        {
            throw new InvalidOperationException(
                "SimulationWorld: Field Neighbor Convolve stands before Field Neighbor Collect.");
        }

        if (collect.Mode == FieldCollectMode.Gather)
        {
            if (builder == null || hashIndex > collectIndex)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: Field Neighbor Collect Gather requires Build Spatial Hash before it.");
            }

            if (!builder.Wrap)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: a field-neighbor chain requires wrap.");
            }

            if (builder.RefreshInterval != collect.RefreshInterval)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: field-neighbor Gather RefreshInterval " + collect.RefreshInterval +
                    " does not match the spatial hash RefreshInterval " + builder.RefreshInterval + ".");
            }

            SpatialHashLayout hashLayout = SpatialHashSet.ComputeLayout(
                builder.Center, builder.Extents, builder.MinCellSize);
            SpatialHashLayout fieldLayout = SpatialHashSet.ComputeLayout(
                collect.Center, collect.Extents, collect.TexelSize);
            if (hashLayout.Resolution != fieldLayout.Resolution ||
                (hashLayout.Origin - fieldLayout.Origin).sqrMagnitude > 1e-8f ||
                (hashLayout.CellSize - fieldLayout.CellSize).sqrMagnitude > 1e-8f)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: field-neighbor layout does not match the spatial hash " +
                    $"(field {fieldLayout.Resolution.x}x{fieldLayout.Resolution.y}, " +
                    $"hash {hashLayout.Resolution.x}x{hashLayout.Resolution.y}).");
            }
        }
        else if (builder != null)
        {
            throw new InvalidOperationException(
                "SimulationWorld: Field Neighbor Collect Scatter cannot sit with Build Spatial Hash.");
        }
        else if (!HasWrapBounds(passes))
        {
            throw new InvalidOperationException(
                "SimulationWorld: a field-neighbor chain requires an enabled BoxBounds Wrap pass.");
        }

        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(
            collect.Center, collect.Extents, collect.TexelSize);
        SpatialHashSet.ValidateLayout(layout, wrap: true);
        if (effect == null)
        {
            return;
        }

        IReadOnlyList<TeamProfile> teams = effect.Teams;
        if (teams.Count != 2)
        {
            throw new InvalidOperationException(
                "SimulationWorld: a field-neighbor chain needs exactly two teams, found " + teams.Count + ".");
        }

        float radius = teams[0] != null ? teams[0].SeparationRadius : 0f;
        for (int i = 0; i < teams.Count; i++)
        {
            TeamProfile profile = teams[i];
            if (profile == null)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: team list entry " + i + " is null.");
            }

            RequireEqualRadius(profile.SeparationRadius, radius, "separationRadius", i);
            RequireEqualRadius(profile.AlignmentRadius, radius, "alignmentRadius", i);
            RequireEqualRadius(profile.CohesionRadius, radius, "cohesionRadius", i);
        }

        float texel = collect.TexelSize;
        if (!(texel > 0f) || Mathf.Abs(radius / texel - 3f) > 1e-4f)
        {
            throw new InvalidOperationException(
                "SimulationWorld: field-neighbor R/t must be 3, found " + radius + " / " + texel + ".");
        }
    }

    private static bool HasWrapBounds(IReadOnlyList<SimPass> passes)
    {
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is BoxBoundsPass box && box.Enabled && box.Behaviour == BoundsBehaviour.Wrap)
            {
                return true;
            }
        }

        return false;
    }

    private static void RequireEqualRadius(float value, float radius, string name, int team)
    {
        if (Mathf.Abs(value - radius) > 1e-4f)
        {
            throw new InvalidOperationException(
                "SimulationWorld: team " + team + " " + name + " " + value +
                " does not equal the field radius " + radius + ".");
        }
    }

    private static void ValidateTeams(
        EffectAsset effect,
        BuildSpatialHashPass builder,
        IReadOnlyList<SimPass> passes,
        bool skipWithinCell)
    {
        if (effect == null)
        {
            return;
        }

        IReadOnlyList<TeamProfile> teams = effect.Teams;
        bool classicSteer = HasEnabled<TeamClassicSteerPass>(passes);
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
            RequireNonNegative(profile.Cruise, "cruise");
            RequireNonNegative(profile.Turn, "turn");
            RequireNonNegative(profile.MinSpeed, "minSpeed");
            RequireNonNegative(profile.MaxSpeed, "maxSpeed");
            RequireNonNegative(profile.MaxForce, "maxForce");
            RequireNonNegative(profile.ForceGain, "forceGain");
            if (classicSteer)
            {
                if (!(profile.MaxSpeed > 0f))
                {
                    throw new InvalidOperationException(
                        "SimulationWorld: team " + i + " maxSpeed must be > 0.");
                }

                if (profile.MinSpeed > profile.MaxSpeed)
                {
                    throw new InvalidOperationException(
                        "SimulationWorld: team " + i + " minSpeed exceeds maxSpeed.");
                }
            }
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

        if (teams.Count > 0 && builder != null && !skipWithinCell)
        {
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

        if (HasEnabled<TeamHeadingSteerPass>(passes) && classicSteer)
        {
            throw new InvalidOperationException(
                "SimulationWorld: Team Heading Steer and Team Classic Steer are both enabled. " +
                "Both write velocity and heading.");
        }

        if (passes != null)
        {
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i] is BoidNeighborForcePass force && force.Enabled && force.AlignFromVelocity)
                {
                    if (!classicSteer)
                    {
                        throw new InvalidOperationException(
                            "SimulationWorld: Boid Neighbor Force AlignFromVelocity requires an enabled Team Classic Steer pass.");
                    }

                    if (force.WalkMode != 1)
                    {
                        throw new InvalidOperationException(
                            "SimulationWorld: Boid Neighbor Force AlignFromVelocity requires WalkMode 1.");
                    }
                }
            }
        }

        if (passes == null || teams.Count > 0)
        {
            return;
        }

        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is BoidNeighborForcePass force && force.Enabled)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: Boid Neighbor Force requires a non-empty team list.");
            }

            if (passes[i] is TeamHeadingSteerPass steer && steer.Enabled)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: Team Heading Steer requires a non-empty team list.");
            }

            if (passes[i] is TeamClassicSteerPass classic && classic.Enabled)
            {
                throw new InvalidOperationException(
                    "SimulationWorld: Team Classic Steer requires a non-empty team list.");
            }
        }
    }

    private static bool HasEnabledField(IReadOnlyList<SimPass> passes)
    {
        if (passes == null)
        {
            return false;
        }

        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is IFieldNeighborPass && passes[i].Enabled)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEnabled<T>(IReadOnlyList<SimPass> passes) where T : SimPass
    {
        if (passes == null)
        {
            return false;
        }

        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is T candidate && candidate.Enabled)
            {
                return true;
            }
        }

        return false;
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
