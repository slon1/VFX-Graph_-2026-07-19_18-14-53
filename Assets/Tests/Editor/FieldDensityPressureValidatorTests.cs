using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class FieldDensityPressureValidatorTests
{
    [Test]
    public void MissingClearVelocityBefore_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(new SimPass[]
            {
                Pressure(),
                new TeamHeadingSteerPass(),
            }, null));
        StringAssert.Contains("Clear Velocity", exception.Message);
    }

    [Test]
    public void MissingTeamHeadingSteerAfter_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(new SimPass[]
            {
                new ClearVelocityPass(),
                Pressure(),
            }, null));
        StringAssert.Contains("Team Heading Steer", exception.Message);
    }

    [Test]
    public void SteerBeforePressure_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(new SimPass[]
            {
                new ClearVelocityPass(),
                new TeamHeadingSteerPass(),
                Pressure(),
            }, null));
        StringAssert.Contains("Team Heading Steer", exception.Message);
    }

    [Test]
    public void ClearAfterPressure_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(new SimPass[]
            {
                Pressure(),
                new ClearVelocityPass(),
                new TeamHeadingSteerPass(),
            }, null));
        StringAssert.Contains("Clear Velocity", exception.Message);
    }

    [Test]
    public void PressureTexelNonPositive_Throws()
    {
        FieldDensityPressurePass pressure = Pressure();
        pressure.PressureTexel = 0f;
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(PressureOnly(pressure), null));
        StringAssert.Contains("PressureTexel", exception.Message);
    }

    [Test]
    public void TexelThatDoesNotDivideTheWorld_Throws()
    {
        FieldDensityPressurePass pressure = Pressure(45f, 0.35f);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(PressureOnly(pressure), null));
        StringAssert.Contains("does not divide", exception.Message);
    }

    [Test]
    public void GridAboveMaxCells_Throws()
    {
        FieldDensityPressurePass pressure = Pressure(256.125f, 0.25f);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(PressureOnly(pressure), null));
        StringAssert.Contains("exceeds", exception.Message);
    }

    [Test]
    public void SideBelowThree_Throws()
    {
        FieldDensityPressurePass pressure = Pressure(0.25f, 0.25f);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(PressureOnly(pressure), null));
        StringAssert.Contains("at least 3 cells", exception.Message);
    }

    [Test]
    public void WeightNegative_Throws()
    {
        FieldDensityPressurePass pressure = Pressure();
        pressure.Weight = -0.1f;
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(PressureOnly(pressure), null));
        StringAssert.Contains("Weight", exception.Message);
    }

    [Test]
    public void G0NonPositive_Throws()
    {
        FieldDensityPressurePass pressure = Pressure();
        pressure.G0 = 0f;
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(PressureOnly(pressure), null));
        StringAssert.Contains("G0", exception.Message);
    }

    [Test]
    public void CenterExtentsDifferFromCollect_Throws()
    {
        FieldDensityPressurePass pressure = Pressure();
        pressure.Center = new Vector3(1f, 0f, 0f);
        EffectAsset effect = Effect();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(FieldWithPressure(pressure), effect));
        StringAssert.Contains("does not match", exception.Message);
        UnityEngine.Object.DestroyImmediate(effect);
    }

    [Test]
    public void DisabledPressure_DoesNotViolate()
    {
        FieldDensityPressurePass pressure = Pressure();
        pressure.Enabled = false;
        pressure.Weight = -1f;
        pressure.G0 = 0f;
        EffectAsset effect = Effect();
        IReadOnlyList<string> warnings = SpatialHashValidator.Validate(
            new SimPass[]
            {
                Collect(),
                new FieldNeighborConvolvePass(),
                new ClearVelocityPass(),
                Force(),
                pressure,
                new TeamHeadingSteerPass(),
                new IntegratePass(),
                Bounds(),
                new HeadingToValuePass(),
            },
            effect);
        Assert.IsEmpty(warnings);
        UnityEngine.Object.DestroyImmediate(effect);
    }

    [Test]
    public void FieldPlusPressure_DoesNotWarn()
    {
        EffectAsset effect = Effect();
        IReadOnlyList<string> warnings = SpatialHashValidator.Validate(FieldWithPressure(Pressure()), effect);
        Assert.IsEmpty(warnings);
        UnityEngine.Object.DestroyImmediate(effect);
    }

    [Test]
    public void PressureOnly_DoesNotWarn()
    {
        EffectAsset effect = Effect();
        IReadOnlyList<string> warnings = SpatialHashValidator.Validate(PressureOnly(Pressure()), effect);
        Assert.IsEmpty(warnings);
        UnityEngine.Object.DestroyImmediate(effect);
    }

    [Test]
    public void TexelPointTwoFiveOnWorldNinety_IsAccepted()
    {
        EffectAsset effect = Effect();
        Assert.DoesNotThrow(
            () => SpatialHashValidator.Validate(PressureOnly(Pressure(45f, 0.25f)), effect));
        UnityEngine.Object.DestroyImmediate(effect);
    }

    private static SimPass[] PressureOnly(FieldDensityPressurePass pressure)
    {
        return new SimPass[]
        {
            new ClearVelocityPass(),
            pressure,
            new TeamHeadingSteerPass(),
        };
    }

    private static SimPass[] FieldWithPressure(FieldDensityPressurePass pressure)
    {
        return new SimPass[]
        {
            Collect(),
            new FieldNeighborConvolvePass(),
            new ClearVelocityPass(),
            Force(),
            pressure,
            new TeamHeadingSteerPass(),
            new IntegratePass(),
            Bounds(),
            new HeadingToValuePass(),
        };
    }

    private static FieldDensityPressurePass Pressure(float extent = 45f, float texel = 0.25f)
    {
        return new FieldDensityPressurePass
        {
            Center = Vector3.zero,
            Extents = new Vector3(extent, 0f, extent),
            PressureTexel = texel,
            Weight = 0.8f,
            G0 = 40f,
        };
    }

    private static FieldNeighborCollectPass Collect()
    {
        return new FieldNeighborCollectPass
        {
            Mode = FieldCollectMode.Scatter,
            Center = Vector3.zero,
            Extents = new Vector3(45f, 0f, 45f),
            TexelSize = 1f,
            RefreshInterval = 3,
        };
    }

    private static FieldNeighborForcePass Force()
    {
        return new FieldNeighborForcePass
        {
            Center = Vector3.zero,
            Extents = new Vector3(45f, 0f, 45f),
            TexelSize = 1f,
        };
    }

    private static BoxBoundsPass Bounds()
    {
        return new BoxBoundsPass
        {
            Center = Vector3.zero,
            Extents = new Vector3(45f, 0f, 45f),
            Behaviour = BoundsBehaviour.Wrap,
        };
    }

    private static EffectAsset Effect()
    {
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.SetTeams(new[]
        {
            new TeamProfile { SeparationRadius = 3f, AlignmentRadius = 3f, CohesionRadius = 3f },
            new TeamProfile { SeparationRadius = 3f, AlignmentRadius = 3f, CohesionRadius = 3f },
        });
        return effect;
    }
}
