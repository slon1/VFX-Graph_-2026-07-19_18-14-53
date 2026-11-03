using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class FieldNeighborValidatorTests
{
    [Test]
    public void GatherChain_RadiusThreeCellOne_DoesNotThrow()
    {
        EffectAsset effect = Effect(2, 3f);
        Assert.DoesNotThrow(() => SpatialHashValidator.Validate(Gather(), effect));
        UnityEngine.Object.DestroyImmediate(effect);
    }

    [Test]
    public void ChainWithoutField_RadiusThreeCellOne_Throws()
    {
        EffectAsset effect = Effect(1, 3f);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(new SimPass[] { Hash(), Bounds() }, effect));
        StringAssert.Contains("exceeds the hash cell limit", exception.Message);
        UnityEngine.Object.DestroyImmediate(effect);
    }

    [Test]
    public void ThreeTeams_Throws()
    {
        EffectAsset effect = Effect(3, 3f);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(Gather(), effect));
        StringAssert.Contains("exactly two", exception.Message);
        UnityEngine.Object.DestroyImmediate(effect);
    }

    [Test]
    public void UnequalRadii_Throws()
    {
        EffectAsset effect = Effect(2, 3f);
        effect.Teams[1].AlignmentRadius = 4f;
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(Gather(), effect));
        StringAssert.Contains("alignmentRadius", exception.Message);
        UnityEngine.Object.DestroyImmediate(effect);
    }

    [Test]
    public void RadiusOverTexelNotThree_Throws()
    {
        EffectAsset effect = Effect(2, 4f);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(Gather(), effect));
        StringAssert.Contains("R/t", exception.Message);
        UnityEngine.Object.DestroyImmediate(effect);
    }

    [Test]
    public void WrapOff_Throws()
    {
        SimPass[] passes = Gather();
        ((BuildSpatialHashPass)passes[0]).Wrap = false;
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(passes, null));
        StringAssert.Contains("wrap", exception.Message);
    }

    [Test]
    public void GridAboveMaxCells_Throws()
    {
        SimPass[] passes = Gather(extent: 200f);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(passes, null));
        StringAssert.Contains("exceeds", exception.Message);
    }

    [Test]
    public void CollectBeforeHash_Throws()
    {
        SimPass[] passes =
        {
            Collect(FieldCollectMode.Gather),
            new FieldNeighborConvolvePass(),
            Hash(),
            Bounds(),
        };
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(passes, null));
        StringAssert.Contains("before", exception.Message);
    }

    [Test]
    public void ConvolveBeforeCollect_Throws()
    {
        SimPass[] passes =
        {
            Hash(),
            Bounds(),
            new FieldNeighborConvolvePass(),
            Collect(FieldCollectMode.Gather),
        };
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(passes, null));
        StringAssert.Contains("before", exception.Message);
    }

    [Test]
    public void HashLayoutDiffersFromField_Throws()
    {
        SimPass[] passes = Gather();
        ((BuildSpatialHashPass)passes[0]).MinCellSize = 2f;
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(passes, null));
        StringAssert.Contains("layout", exception.Message);
    }

    [Test]
    public void RefreshIntervalMismatch_Throws()
    {
        SimPass[] passes = Gather();
        ((BuildSpatialHashPass)passes[0]).RefreshInterval = 3;
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(passes, null));
        StringAssert.Contains("RefreshInterval", exception.Message);
    }

    [Test]
    public void ScatterWithHash_Throws()
    {
        SimPass[] passes =
        {
            Hash(),
            Bounds(),
            Collect(FieldCollectMode.Scatter),
            new FieldNeighborConvolvePass(),
        };
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(passes, null));
        StringAssert.Contains("Scatter", exception.Message);
    }

    [Test]
    public void ScatterChain_DoesNotThrow()
    {
        EffectAsset effect = Effect(2, 3f);
        Assert.DoesNotThrow(() => SpatialHashValidator.Validate(new SimPass[]
        {
            Bounds(),
            Collect(FieldCollectMode.Scatter),
            new FieldNeighborConvolvePass(),
        }, effect));
        UnityEngine.Object.DestroyImmediate(effect);
    }

    [Test]
    public void NeighborForceWithField_Throws()
    {
        SimPass[] passes =
        {
            Hash(),
            Bounds(),
            Collect(FieldCollectMode.Gather),
            new FieldNeighborConvolvePass(),
            new BoidNeighborForcePass(),
        };
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(passes, null));
        StringAssert.Contains("Boid Neighbor Force", exception.Message);
    }

    [Test]
    public void HashRefreshAboveOneWithoutField_Throws()
    {
        BuildSpatialHashPass hash = Hash();
        hash.RefreshInterval = 3;
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(new SimPass[] { hash, Bounds() }, null));
        StringAssert.Contains("RefreshInterval", exception.Message);
    }

    [Test]
    public void PlayRadiusCheck_StaysQuietWithoutNeighborForce()
    {
        TeamProfile profile = new TeamProfile
        {
            SeparationRadius = 3f,
            AlignmentRadius = 3f,
            CohesionRadius = 3f,
        };
        string warning = new TeamRadiusPlayCheck().Check(new[] { profile, profile }, 1f);
        Assert.IsNotNull(warning);
        Assert.IsFalse(HasEnabledForce(Gather()));
    }

    private static bool HasEnabledForce(IReadOnlyList<SimPass> passes)
    {
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is BoidNeighborForcePass force && force.Enabled)
            {
                return true;
            }
        }

        return false;
    }

    private static SimPass[] Gather(float extent = 8f)
    {
        return new SimPass[]
        {
            Hash(extent),
            Bounds(extent),
            Collect(FieldCollectMode.Gather, extent),
            new FieldNeighborConvolvePass(),
        };
    }

    private static BuildSpatialHashPass Hash(float extent = 8f)
    {
        return new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = new Vector3(extent, 0f, extent),
            MinCellSize = 1f,
            Wrap = true,
        };
    }

    private static FieldNeighborCollectPass Collect(FieldCollectMode mode, float extent = 8f)
    {
        return new FieldNeighborCollectPass
        {
            Mode = mode,
            Center = Vector3.zero,
            Extents = new Vector3(extent, 0f, extent),
            TexelSize = 1f,
        };
    }

    private static BoxBoundsPass Bounds(float extent = 8f)
    {
        return new BoxBoundsPass
        {
            Center = Vector3.zero,
            Extents = new Vector3(extent, 0f, extent),
            Behaviour = BoundsBehaviour.Wrap,
        };
    }

    private static EffectAsset Effect(int teamCount, float radius)
    {
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        TeamProfile[] teams = new TeamProfile[teamCount];
        for (int i = 0; i < teamCount; i++)
        {
            teams[i] = new TeamProfile
            {
                SeparationRadius = radius,
                AlignmentRadius = radius,
                CohesionRadius = radius,
            };
        }

        effect.SetTeams(teams);
        return effect;
    }
}
