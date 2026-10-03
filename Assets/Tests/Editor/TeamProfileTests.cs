using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class TeamProfileTests
{
    [Test]
    public void Upload_WritesTwoSlots_AndLeavesTheTailZero()
    {
        TeamProfile first = Profile(1.5f, 2.5f, 3.5f, 4f, 0.1f, 0.2f, 0.3f, 5f, 6f);
        TeamProfile second = Profile(8f, 7f, 6f, 1.25f, -1f, -2f, -3f, 9f, 10f);
        first.Color = DebugFieldQuadSlot.DefaultFireGradient();
        second.Color = DebugFieldQuadSlot.DefaultFireGradient();

        GraphicsBuffer buffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
        try
        {
            TeamParams.Upload(buffer, new[] { first, second });
            TeamParams[] data = new TeamParams[TeamProfile.MaxTeams];
            buffer.GetData(data);

            AssertSlot(data[0], first);
            AssertSlot(data[1], second);
            Assert.AreEqual(default(TeamParams), data[2]);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    public void Stride_Is48()
    {
        Assert.AreEqual(48, Marshal.SizeOf<TeamParams>());
        Assert.AreEqual(48, TeamParams.Stride);
    }

    [Test]
    public void NinthTeam_ThrowsOnEarlyExits()
    {
        EffectAsset effect = EffectWithTeams(9);
        try
        {
            Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(null, effect));
            Assert.Throws<InvalidOperationException>(
                () => SpatialHashValidator.Validate(Array.Empty<SimPass>(), effect));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(effect);
        }
    }

    [Test]
    public void EightTeams_WithoutBuilder_AllowsLargeRadius()
    {
        EffectAsset effect = EffectWithTeams(8, radius: 1000f);
        try
        {
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(Array.Empty<SimPass>(), effect));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(effect);
        }
    }

    [Test]
    public void SwarmTeamIndex_MustBeInsideTheList()
    {
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        try
        {
            effect.EditorConfigure(DataSourceKind.Swarm, 1f, Array.Empty<SimPass>(), null);
            SwarmSource swarm = (SwarmSource)effect.ResolveSource();
            effect.SetTeams(new[] { new TeamProfile(), new TeamProfile() });
            swarm.Spawns = new List<SwarmSource.Spawn>
            {
                Spawn(0),
                Spawn(0),
            };
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(Array.Empty<SimPass>(), effect));

            swarm.Spawns = new List<SwarmSource.Spawn> { Spawn(1) };
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(Array.Empty<SimPass>(), effect));

            swarm.Spawns = new List<SwarmSource.Spawn> { Spawn(2) };
            Assert.Throws<InvalidOperationException>(
                () => SpatialHashValidator.Validate(Array.Empty<SimPass>(), effect));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(effect);
        }
    }

    [Test]
    public void Cube_IgnoresTeamIndices()
    {
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        try
        {
            effect.EditorConfigure(DataSourceKind.Cube, 1f, Array.Empty<SimPass>(), null);
            effect.SetTeams(new[] { new TeamProfile() });
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(Array.Empty<SimPass>(), effect));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(effect);
        }
    }

    [Test]
    public void Radius_UsesLayoutCell_NotMinCellSize()
    {
        BuildSpatialHashPass builder = new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = new Vector3(10f, 0f, 10f),
            MinCellSize = 3f,
            Wrap = false,
        };
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(builder.Center, builder.Extents, builder.MinCellSize);
        float cell = Mathf.Min(layout.CellSize.x, layout.CellSize.y);
        Assert.Greater(Mathf.Abs(cell - builder.MinCellSize), 0.1f);

        float limit = cell * (1f + SpatialHashSet.GridEpsilon);
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        try
        {
            effect.EditorConfigure(DataSourceKind.Cube, 1f, new SimPass[] { builder }, null);
            TeamProfile profile = new TeamProfile { SeparationRadius = limit };
            effect.SetTeams(new[] { profile });
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(effect.Passes, effect));

            profile.SeparationRadius = cell * (1f + 1e-4f);
            Assert.Throws<InvalidOperationException>(() => SpatialHashValidator.Validate(effect.Passes, effect));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(effect);
        }
    }

    [Test]
    public void Multiplier_IsNotComparedToTheCell()
    {
        BuildSpatialHashPass builder = new BuildSpatialHashPass
        {
            Extents = new Vector3(10f, 0f, 10f),
            MinCellSize = 3f,
            Wrap = false,
        };
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        try
        {
            effect.EditorConfigure(DataSourceKind.Cube, 1f, new SimPass[] { builder }, null);
            effect.SetTeams(new[]
            {
                new TeamProfile
                {
                    SeparationRadius = 1f,
                    AlignmentRadius = 1f,
                    CohesionRadius = 1f,
                    InterGroupSeparationMultiplier = 1000f,
                },
            });
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(effect.Passes, effect));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(effect);
        }
    }

    [Test]
    public void NegativeRadiusAndMultiplier_ThrowFromSetterAndFromValidate()
    {
        TeamProfile profile = new TeamProfile();
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.SeparationRadius = -1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.InterGroupSeparationMultiplier = -1f);

        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        try
        {
            effect.EditorConfigure(DataSourceKind.Cube, 1f, Array.Empty<SimPass>(), null);
            effect.SetTeams(new[] { profile });
            SetPrivate(profile, "separationRadius", -0.5f);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SpatialHashValidator.Validate(Array.Empty<SimPass>(), effect));

            SetPrivate(profile, "separationRadius", 0f);
            SetPrivate(profile, "interGroupSeparationMultiplier", -2f);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SpatialHashValidator.Validate(Array.Empty<SimPass>(), effect));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(effect);
        }
    }

    [Test]
    public void EmptyTeams_AndSetTeamsNull_StayEmpty()
    {
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        try
        {
            effect.EditorConfigure(DataSourceKind.Cube, 1f, Array.Empty<SimPass>(), null);
            Assert.DoesNotThrow(() => SpatialHashValidator.Validate(effect.Passes, effect));
            effect.SetTeams(null);
            Assert.IsNotNull(effect.Teams);
            Assert.AreEqual(0, effect.Teams.Count);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(effect);
        }
    }

    private static void AssertSlot(TeamParams slot, TeamProfile profile)
    {
        Assert.AreEqual(profile.SeparationRadius, slot.Radii.x);
        Assert.AreEqual(profile.AlignmentRadius, slot.Radii.y);
        Assert.AreEqual(profile.CohesionRadius, slot.Radii.z);
        Assert.AreEqual(profile.InterGroupSeparationMultiplier, slot.Radii.w);
        Assert.AreEqual(profile.SeparationWeight, slot.Weights.x);
        Assert.AreEqual(profile.AlignmentWeight, slot.Weights.y);
        Assert.AreEqual(profile.CohesionWeight, slot.Weights.z);
        Assert.AreEqual(0f, slot.Weights.w);
        Assert.AreEqual(profile.Cruise, slot.Motion.x);
        Assert.AreEqual(profile.Turn, slot.Motion.y);
        Assert.AreEqual(0f, slot.Motion.z);
        Assert.AreEqual(0f, slot.Motion.w);
    }

    private static TeamProfile Profile(
        float sep,
        float align,
        float coh,
        float multiplier,
        float sepWeight,
        float alignWeight,
        float cohWeight,
        float cruise,
        float turn)
    {
        return new TeamProfile
        {
            SeparationRadius = sep,
            AlignmentRadius = align,
            CohesionRadius = coh,
            InterGroupSeparationMultiplier = multiplier,
            SeparationWeight = sepWeight,
            AlignmentWeight = alignWeight,
            CohesionWeight = cohWeight,
            Cruise = cruise,
            Turn = turn,
        };
    }

    private static EffectAsset EffectWithTeams(int count, float radius = 0f)
    {
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(DataSourceKind.Cube, 1f, Array.Empty<SimPass>(), null);
        TeamProfile[] profiles = new TeamProfile[count];
        for (int i = 0; i < count; i++)
        {
            profiles[i] = new TeamProfile { SeparationRadius = radius };
        }

        effect.SetTeams(profiles);
        return effect;
    }

    private static SwarmSource.Spawn Spawn(int team)
    {
        return new SwarmSource.Spawn
        {
            TeamIndex = team,
            Count = 1,
            Radius = 1f,
            InitialDirection = Vector2.right,
        };
    }

    private static void SetPrivate(TeamProfile profile, string fieldName, float value)
    {
        FieldInfo field = typeof(TeamProfile).GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        field.SetValue(profile, value);
    }
}

[TestFixture]
public class TeamProfileWorldTests
{
    private GameObject host;
    private EffectAsset effect;

    [TearDown]
    public void TearDown()
    {
        if (host != null)
        {
            UnityEngine.Object.DestroyImmediate(host);
            host = null;
        }

        if (effect != null)
        {
            UnityEngine.Object.DestroyImmediate(effect);
            effect = null;
        }
    }

    [Test]
    public void Rebuild_WithOneTeam_KeepsFourParticles()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        host = new GameObject("TeamProfile_World");
        host.SetActive(false);

        effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(DataSourceKind.Swarm, 1f, new SimPass[] { new IntegratePass() }, null);
        effect.SetTeams(new[] { new TeamProfile() });
        SwarmSource swarm = (SwarmSource)effect.ResolveSource();
        swarm.Spawns = new List<SwarmSource.Spawn>
        {
            new SwarmSource.Spawn
            {
                TeamIndex = 0,
                Count = 4,
                Radius = 1f,
                InitialDirection = Vector2.right,
            },
        };

        SimulationWorld world = host.AddComponent<SimulationWorld>();
        ComputeShader dynamics = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/Shaders/GPU/Passes/DynamicsPasses.compute");
        Assert.IsNotNull(dynamics);

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = effect;
        worldSo.FindProperty("visualEffect").objectReferenceValue = null;
        SerializedProperty library = worldSo.FindProperty("passLibrary");
        library.arraySize = 1;
        library.GetArrayElementAtIndex(0).objectReferenceValue = dynamics;
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        Assert.DoesNotThrow(() => world.Rebuild());
        Assert.IsTrue(world.enabled);

        FieldInfo particlesField = typeof(SimulationWorld).GetField(
            "particles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(particlesField);
        ParticleSet set = (ParticleSet)particlesField.GetValue(world);
        Assert.AreEqual(4, set.Count);
    }
}
