using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

[TestFixture]
public class BoidsHashPresetsWalkDefaultTests
{
    [Test]
    public void EveryNeighborForcePreset_IsWalk1_AndFitsTheCell()
    {
        string[] guids = AssetDatabase.FindAssets("t:EffectAsset");
        int found = 0;
        for (int g = 0; g < guids.Length; g++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[g]);
            EffectAsset effect = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
            if (effect == null)
            {
                continue;
            }

            IReadOnlyList<SimPass> passes = effect.Passes;
            int forces = 0;
            BuildSpatialHashPass hash = null;
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i] is BoidNeighborForcePass foundForce)
                {
                    forces++;
                    Assert.AreEqual(1, foundForce.WalkMode, path);
                }

                if (hash == null && passes[i] is BuildSpatialHashPass foundHash)
                {
                    hash = foundHash;
                }
            }

            if (forces == 0)
            {
                continue;
            }

            found++;
            Assert.IsNotNull(hash, path + " has no Build Spatial Hash pass.");
            SpatialHashLayout layout = SpatialHashSet.ComputeLayout(hash.Center, hash.Extents, hash.MinCellSize);
            float minSide = Mathf.Min(layout.CellSize.x, layout.CellSize.y);
            string warning = new TeamRadiusPlayCheck().Check(effect.Teams, minSide);
            Assert.IsNull(warning, path + ": " + warning);
        }

        Assert.Greater(found, 0, "no effect contains BoidNeighborForcePass");
        TestContext.WriteLine("neighbor-force effects " + found);
    }
}
