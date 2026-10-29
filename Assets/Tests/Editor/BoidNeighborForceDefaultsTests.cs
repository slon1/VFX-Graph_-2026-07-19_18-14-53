using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;

[TestFixture]
public class BoidNeighborForceDefaultsTests
{
    private static readonly string[] ExistingEffects =
    {
        "Boids_hash",
        "Boids_hash100k",
        "Boids_hash100k_x4",
        "Boids_hash200k",
        "Boids_hash200k_points",
        "Boids_hash30k",
        "Boids_hash400k",
        "Boids_hash400k_2teams",
        "Boids_hash600k",
        "Boids_hash800k",
        "Boids_hash_2teams",
        "Boids_hash_points",
    };

    [Test]
    public void NewPass_DefaultsTo128()
    {
        Assert.AreEqual(128, new BoidNeighborForcePass().MaxNeighbors);
    }

    [Test]
    public void ExistingEffects_StayAt48()
    {
        for (int i = 0; i < ExistingEffects.Length; i++)
        {
            string path = "Assets/Effects/" + ExistingEffects[i] + ".asset";
            EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
            if (asset == null)
            {
                Assert.Fail(path);
            }

            BoidNeighborForcePass force = null;
            IReadOnlyList<SimPass> passes = asset.Passes;
            for (int p = 0; p < passes.Count; p++)
            {
                if (passes[p] is BoidNeighborForcePass found)
                {
                    force = found;
                    break;
                }
            }

            if (force == null)
            {
                Assert.Fail(path);
            }

            Assert.AreEqual(48, force.MaxNeighbors, path);
        }
    }
}
