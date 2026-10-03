using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Repeats the team-radius cell check during Play (ADR-038). One warning per new value.
/// A quiet frame allocates nothing.
/// </summary>
public sealed class TeamRadiusPlayCheck
{
    private struct Slot
    {
        public bool Has;
        public float Value;
    }

    private readonly Slot[] slots = new Slot[TeamProfile.MaxTeams * 3];
    private readonly int[] pending = new int[TeamProfile.MaxTeams * 3];

    /// <summary>
    /// Returns one warning for every newly out-of-range radius, or null when nothing new is wrong.
    /// <paramref name="minCellSide"/> is the smaller cell side; the grid epsilon is applied here.
    /// </summary>
    public string Check(IReadOnlyList<TeamProfile> teams, float minCellSide)
    {
        float limit = minCellSide * (1f + SpatialHashSet.GridEpsilon);
        int count = teams != null ? teams.Count : 0;
        if (count > TeamProfile.MaxTeams)
        {
            count = TeamProfile.MaxTeams;
        }

        for (int i = count * 3; i < slots.Length; i++)
        {
            slots[i].Has = false;
        }

        int pendingCount = 0;
        for (int team = 0; team < count; team++)
        {
            TeamProfile profile = teams[team];
            if (profile == null)
            {
                for (int radius = 0; radius < 3; radius++)
                {
                    slots[team * 3 + radius].Has = false;
                }

                continue;
            }

            pendingCount = Consider(team, 0, profile.SeparationRadius, limit, pendingCount);
            pendingCount = Consider(team, 1, profile.AlignmentRadius, limit, pendingCount);
            pendingCount = Consider(team, 2, profile.CohesionRadius, limit, pendingCount);
        }

        if (pendingCount == 0)
        {
            return null;
        }

        StringBuilder text = new StringBuilder();
        for (int i = 0; i < pendingCount; i++)
        {
            int slot = pending[i];
            int team = slot / 3;
            int radius = slot - team * 3;
            if (i > 0)
            {
                text.Append(' ');
            }

            text.Append("SimulationWorld: team ");
            text.Append(team);
            text.Append(' ');
            text.Append(RadiusName(radius));
            text.Append(' ');
            text.Append(slots[slot].Value);
            text.Append(" exceeds the hash cell limit ");
            text.Append(limit);
            text.Append('.');
        }

        return text.ToString();
    }

    public void Forget()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i].Has = false;
        }
    }

    private int Consider(int team, int radius, float value, float limit, int pendingCount)
    {
        int slot = team * 3 + radius;
        if (value > limit)
        {
            if (!slots[slot].Has || slots[slot].Value != value)
            {
                slots[slot].Has = true;
                slots[slot].Value = value;
                pending[pendingCount] = slot;
                pendingCount++;
            }

            return pendingCount;
        }

        slots[slot].Has = false;
        return pendingCount;
    }

    private static string RadiusName(int radius)
    {
        switch (radius)
        {
            case 0:
                return "separationRadius";
            case 1:
                return "alignmentRadius";
            default:
                return "cohesionRadius";
        }
    }
}
