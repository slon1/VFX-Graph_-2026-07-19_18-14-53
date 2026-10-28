using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Build-time warnings for <see cref="SwarmPointForcePass"/> (ADR-049).
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

            if (pass is SwarmPointForcePass)
            {
                pointPasses++;
                if (pointIndex < 0)
                {
                    pointIndex = i;
                }
            }
        }

        if (pointPasses > 1)
        {
            warnings.Add(
                "SimulationWorld: more than one Swarm Point Force pass. " +
                "One pass applies every point; extra passes repeat the same sum.");
        }

        if (pointIndex >= 0)
        {
            if (clearIndex < 0 || pointIndex < clearIndex)
            {
                warnings.Add(
                    "SimulationWorld: Swarm Point Force stands before Clear Velocity. " +
                    "Clear Velocity wipes the point contribution before the heading reads it.");
            }

            if (steerIndex >= 0 && pointIndex > steerIndex)
            {
                warnings.Add(
                    "SimulationWorld: Swarm Point Force stands after " + steerLabel + ". " +
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
