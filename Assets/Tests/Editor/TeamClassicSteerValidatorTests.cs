using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class TeamClassicSteerValidatorTests
{
    private const string HeadingAfter =
        "SimulationWorld: Swarm Point Force stands after Team Heading Steer. " +
        "The heading will not turn toward the point.";

    private const string ClassicAfter =
        "SimulationWorld: Swarm Point Force stands after Team Classic Steer. " +
        "The heading will not turn toward the point.";

    [Test]
    public void NegativeClassicFields_ThrowFromTheValidator()
    {
        string[] fields = { "minSpeed", "maxSpeed", "maxForce", "forceGain" };
        for (int i = 0; i < fields.Length; i++)
        {
            TeamProfile profile = new TeamProfile();
            SetPrivate(profile, fields[i], -0.5f);
            EffectAsset effect = EffectWith(profile, new TeamClassicSteerPass());
            try
            {
                ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                    () => SpatialHashValidator.Validate(effect.Passes, effect));
                StringAssert.Contains(fields[i], exception.ParamName);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(effect);
            }
        }
    }

    [Test]
    public void NegativeClassicFields_StillThrowWhenThePassIsDisabled()
    {
        TeamProfile profile = new TeamProfile();
        SetPrivate(profile, "forceGain", -1f);
        EffectAsset effect = EffectWith(profile, new TeamClassicSteerPass { Enabled = false });
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SpatialHashValidator.Validate(effect.Passes, effect));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(effect);
        }
    }

    [Test]
    public void MaxSpeedZero_ThrowsOnlyWhenThePassIsEnabled()
    {
        TeamProfile profile = new TeamProfile { MaxSpeed = 0f };
        EffectAsset enabled = EffectWith(profile, new TeamClassicSteerPass());
        EffectAsset disabled = EffectWith(profile, new TeamClassicSteerPass { Enabled = false });
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => SpatialHashValidator.Validate(enabled.Passes, enabled));
            StringAssert.Contains("team 0", exception.Message);
            StringAssert.Contains("maxSpeed", exception.Message);
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(disabled.Passes, disabled));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(enabled);
            UnityEngine.Object.DestroyImmediate(disabled);
        }
    }

    [Test]
    public void MinSpeedAboveMax_ThrowsOnlyWhenThePassIsEnabled()
    {
        TeamProfile profile = new TeamProfile { MinSpeed = 10f, MaxSpeed = 4f };
        EffectAsset enabled = EffectWith(profile, new TeamClassicSteerPass());
        EffectAsset disabled = EffectWith(profile, new TeamClassicSteerPass { Enabled = false });
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => SpatialHashValidator.Validate(enabled.Passes, enabled));
            StringAssert.Contains("team 0", exception.Message);
            StringAssert.Contains("minSpeed", exception.Message);
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(disabled.Passes, disabled));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(enabled);
            UnityEngine.Object.DestroyImmediate(disabled);
        }
    }

    [Test]
    public void EmptyTeamList_ThrowsOnlyWhenThePassIsEnabled()
    {
        EffectAsset enabled = EffectWith(null, new TeamClassicSteerPass());
        EffectAsset disabled = EffectWith(null, new TeamClassicSteerPass { Enabled = false });
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => SpatialHashValidator.Validate(enabled.Passes, enabled));
            StringAssert.Contains("Team Classic Steer", exception.Message);
            StringAssert.Contains("team list", exception.Message);
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(disabled.Passes, disabled));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(enabled);
            UnityEngine.Object.DestroyImmediate(disabled);
        }
    }

    [Test]
    public void BothSteers_ThrowWhenBothAreEnabled()
    {
        TeamProfile profile = new TeamProfile();
        EffectAsset both = EffectWith(
            profile, new TeamHeadingSteerPass(), new TeamClassicSteerPass());
        EffectAsset classicOff = EffectWith(
            profile, new TeamHeadingSteerPass(), new TeamClassicSteerPass { Enabled = false });
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => SpatialHashValidator.Validate(both.Passes, both));
            StringAssert.Contains("Team Heading Steer", exception.Message);
            StringAssert.Contains("Team Classic Steer", exception.Message);
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(classicOff.Passes, classicOff));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(both);
            UnityEngine.Object.DestroyImmediate(classicOff);
        }
    }

    [Test]
    public void SwarmPoint_WarnsAfterClassic_AndKeepsTheHeadingText()
    {
        IReadOnlyList<string> quiet = SwarmPointValidator.Validate(new SimPass[]
        {
            new ClearVelocityPass(),
            new SwarmPointForcePass(),
            new TeamClassicSteerPass(),
        });
        Assert.AreEqual(0, quiet.Count);

        IReadOnlyList<string> late = SwarmPointValidator.Validate(new SimPass[]
        {
            new ClearVelocityPass(),
            new TeamClassicSteerPass(),
            new SwarmPointForcePass(),
        });
        Assert.AreEqual(1, late.Count);
        Assert.AreEqual(ClassicAfter, late[0]);

        IReadOnlyList<string> heading = SwarmPointValidator.Validate(new SimPass[]
        {
            new ClearVelocityPass(),
            new TeamHeadingSteerPass(),
            new SwarmPointForcePass(),
        });
        Assert.AreEqual(1, heading.Count);
        Assert.AreEqual(HeadingAfter, heading[0]);
    }

    private static EffectAsset EffectWith(TeamProfile profile, params SimPass[] passes)
    {
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(DataSourceKind.Cube, 1f, passes, null);
        if (profile != null)
        {
            effect.SetTeams(new[] { profile });
        }

        return effect;
    }

    private static void SetPrivate(TeamProfile profile, string fieldName, float value)
    {
        FieldInfo field = typeof(TeamProfile).GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, fieldName);
        field.SetValue(profile, value);
    }
}
