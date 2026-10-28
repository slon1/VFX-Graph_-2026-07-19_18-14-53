using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Build-time warnings for <see cref="SwarmPointForcePass"/> and <see cref="SwarmSpringForcePass"/> (ADR-049, ADR-055).
/// Warnings only: a throw in <see cref="SimulationWorld"/> teardown would stop the world.
/// </summary>
internal static class SwarmPointValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<SimPass> passes, EffectAsset effect = null)
    {
        List<string> warnings = new List<string>();
        if (passes == null)
        {
            WarnPointData(effect, warnings, wrap: false);
            return warnings;
        }

        int clearIndex = -1;
        int steerIndex = -1;
        string steerLabel = null;
        int pointPasses = 0;
        int pointIndex = -1;
        string pointLabel = null;
        bool springSeen = false;
        bool wrap = false;

        for (int i = 0; i < passes.Count; i++)
        {
            SimPass pass = passes[i];
            if (pass == null || !pass.Enabled)
            {
                continue;
            }

            if (pass is ClearVelocityPass && clearIndex < 0)
            {
                clearIndex = i;
            }

            if (pass is TeamHeadingSteerPass && steerIndex < 0)
            {
                steerIndex = i;
                steerLabel = "Team Heading Steer";
            }

            if (pass is TeamClassicSteerPass && steerIndex < 0)
            {
                steerIndex = i;
                steerLabel = "Team Classic Steer";
            }

            if (pass is BuildSpatialHashPass hash && hash.Wrap)
            {
                wrap = true;
            }

            if (pass is SwarmPointForcePass || pass is SwarmSpringForcePass)
            {
                pointPasses++;
                if (pointIndex < 0)
                {
                    pointIndex = i;
                    pointLabel = pass is SwarmSpringForcePass
                        ? "Swarm Spring Force"
                        : "Swarm Point Force";
                }

                if (pass is SwarmSpringForcePass)
                {
                    springSeen = true;
                }
            }
        }

        if (pointPasses > 1)
        {
            warnings.Add(springSeen
                ? "SimulationWorld: more than one swarm point pass (Swarm Point Force and Swarm Spring Force apply the same point list). " +
                  "Each pass applies every point; extra passes repeat the sum."
                : "SimulationWorld: more than one Swarm Point Force pass. " +
                  "One pass applies every point; extra passes repeat the same sum.");
        }

        if (pointIndex >= 0)
        {
            if (clearIndex < 0 || pointIndex < clearIndex)
            {
                warnings.Add(
                    "SimulationWorld: " + pointLabel + " stands before Clear Velocity. " +
                    "Clear Velocity wipes the point contribution before the heading reads it.");
            }

            if (steerIndex >= 0 && pointIndex > steerIndex)
            {
                warnings.Add(
                    "SimulationWorld: " + pointLabel + " stands after " + steerLabel + ". " +
                    "The heading will not turn toward the point.");
            }
        }

        WarnPointData(effect, warnings, wrap);
        return warnings;
    }

    private static void WarnPointData(EffectAsset effect, List<string> warnings, bool wrap)
    {
        if (effect == null)
        {
            return;
        }

        IReadOnlyList<SwarmPoint> points = effect.SwarmPoints;
        if (points.Count > SwarmPoint.MaxPoints)
        {
            warnings.Add(
                "SimulationWorld: swarmPoints has " + points.Count +
                " entries; Swarm Point Force keeps " + SwarmPoint.MaxPoints +
                " and drops the rest.");
        }

        if (!wrap)
        {
            return;
        }

        for (int i = 0; i < points.Count; i++)
        {
            SwarmPoint point = points[i];
            if (point != null && point.Weight < 0f && point.Radius == 0f)
            {
                warnings.Add(
                    "SimulationWorld: Swarm Point Force repulsion with radius 0 and hash wrap on " +
                    "gathers particles on the far side of the torus.");
                return;
            }
        }
    }
}
