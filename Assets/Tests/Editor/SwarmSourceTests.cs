using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

[TestFixture]
public class SwarmSourceTests
{
    private ParticleSet particles;

    [SetUp]
    public void SetUp()
    {
        particles = new ParticleSet();
    }

    [TearDown]
    public void TearDown()
    {
        particles?.Dispose();
        particles = null;
    }

    [Test]
    public void KindNumbers_CubeNoneStayPut()
    {
        Assert.AreEqual(0, (int)DataSourceKind.Cube);
        Assert.AreEqual(3, (int)DataSourceKind.None);
        Assert.AreEqual(4, (int)DataSourceKind.Swarm);
    }

    [Test]
    public void TwoSpawns_TeamIdsFollowListOrder()
    {
        SwarmSource source = Source(
            Disk(team: 2, count: 3, center: Vector2.zero, radius: 1f, direction: Vector2.right),
            Disk(team: 7, count: 5, center: new Vector2(4f, 1f), radius: 1f, direction: Vector2.up));

        source.Setup(particles);

        Assert.AreEqual(8, particles.Count);
        uint[] teams = ReadTeams(8);
        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(2u, teams[i]);
        }

        for (int i = 3; i < 8; i++)
        {
            Assert.AreEqual(7u, teams[i]);
        }
    }

    [Test]
    public void Disk_PointsLieInsideRadiusOnXZ()
    {
        const float radius = 3f;
        Vector2 center = new Vector2(2f, -1f);
        SwarmSource source = Source(Disk(0, 24, center, radius, Vector2.right));
        source.Setup(particles);

        Vector3[] rest = ReadRest(24);
        for (int i = 0; i < rest.Length; i++)
        {
            Assert.AreEqual(0f, rest[i].y);
            float dx = rest[i].x - center.x;
            float dz = rest[i].z - center.y;
            Assert.LessOrEqual(Mathf.Sqrt(dx * dx + dz * dz), radius * (1f + 1e-4f));
        }
    }

    [Test]
    public void Heading_ZeroJitter_MatchesDirection()
    {
        SwarmSource source = Source(Disk(0, 6, Vector2.zero, 1f, new Vector2(0f, 1f)));
        source.JitterDegrees = 0f;
        source.Setup(particles);

        Vector3[] heading = ReadHeading(6);
        for (int i = 0; i < heading.Length; i++)
        {
            Assert.AreEqual(0f, heading[i].x, 1e-5f);
            Assert.AreEqual(0f, heading[i].y, 1e-5f);
            Assert.AreEqual(1f, heading[i].z, 1e-5f);
        }
    }

    [Test]
    public void Heading_OtherCases_StayUnitOnXZ()
    {
        SwarmSource source = Source(
            Disk(0, 8, Vector2.zero, 1f, new Vector2(1f, 0f)),
            Disk(1, 4, Vector2.one, 1f, Vector2.zero));
        source.JitterDegrees = 35f;
        source.Setup(particles);

        Vector3[] heading = ReadHeading(12);
        for (int i = 0; i < heading.Length; i++)
        {
            Assert.AreEqual(0f, heading[i].y, 1e-5f);
            Assert.AreEqual(1f, heading[i].magnitude, 1e-5f);
        }
    }

    [Test]
    public void SameSeed_BuffersMatchBitwise()
    {
        SwarmSource first = Filled(seed: 11);
        SwarmSource second = Filled(seed: 11);
        ParticleSet other = new ParticleSet();
        try
        {
            first.Setup(particles);
            second.Setup(other);
            AssertBits(ReadRest(8), ReadRest(other, 8));
            AssertBits(ReadHeading(8), ReadHeading(other, 8));
            Assert.AreEqual(ReadTeams(8), ReadTeams(other, 8));
        }
        finally
        {
            other.Dispose();
        }
    }

    [Test]
    public void DifferentSeed_MovesAtLeastOnePoint()
    {
        SwarmSource first = Filled(seed: 1);
        SwarmSource second = Filled(seed: 2);
        ParticleSet other = new ParticleSet();
        try
        {
            first.Setup(particles);
            second.Setup(other);
            Vector3[] a = ReadRest(8);
            Vector3[] b = ReadRest(other, 8);
            bool differs = false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].x != b[i].x || a[i].z != b[i].z)
                {
                    differs = true;
                    break;
                }
            }

            Assert.IsTrue(differs);
        }
        finally
        {
            other.Dispose();
        }
    }

    [Test]
    public void EmptyList_DoesNotRegisterRest()
    {
        new SwarmSource().Setup(particles);
        Assert.AreEqual(0, particles.Count);
        Assert.IsFalse(particles.TryGet(BuiltinAttributes.RestPosition, out _));
    }

    [Test]
    public void NullList_DoesNotRegisterRest()
    {
        SwarmSource source = new SwarmSource { Spawns = null };
        source.Setup(particles);
        Assert.AreEqual(0, particles.Count);
        Assert.IsFalse(particles.TryGet(BuiltinAttributes.RestPosition, out _));
    }

    [Test]
    public void NullSpawn_ThrowsBeforeCapacity()
    {
        SwarmSource source = new SwarmSource
        {
            Spawns = new List<SwarmSource.Spawn> { null },
        };
        Assert.Throws<ArgumentNullException>(() => source.Setup(particles));
        Assert.AreEqual(0, particles.Count);
        Assert.IsFalse(particles.TryGet(BuiltinAttributes.RestPosition, out _));
    }

    [Test]
    public void InvalidSpawn_ThrowsBeforeBuffers()
    {
        AssertInvalid(Disk(-1, 2, Vector2.zero, 1f, Vector2.right));
        AssertInvalid(Disk(0, -1, Vector2.zero, 1f, Vector2.right));
        AssertInvalid(Disk(0, 2, Vector2.zero, -0.1f, Vector2.right));
    }

    [Test]
    public void NegativeJitter_SetterAndSetupThrow()
    {
        SwarmSource source = Source(Disk(0, 2, Vector2.zero, 1f, Vector2.right));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.JitterDegrees = -1f);
        Assert.AreEqual(0, particles.Count);

        FieldInfo field = typeof(SwarmSource).GetField(
            "jitterDegrees", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        field.SetValue(source, -1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Setup(particles));
        Assert.AreEqual(0, particles.Count);
        Assert.IsFalse(particles.TryGet(BuiltinAttributes.RestPosition, out _));
    }

    [Test]
    public void SecondSetup_LargerCount_ThrowsFromParticleSet()
    {
        Source(Disk(0, 2, Vector2.zero, 1f, Vector2.right)).Setup(particles);
        SwarmSource larger = Source(Disk(0, 4, Vector2.zero, 1f, Vector2.right));
        Assert.Throws<InvalidOperationException>(() => larger.Setup(particles));
    }

    [Test]
    public void Setup_DoesNotRegisterVelocity()
    {
        Source(Disk(0, 3, Vector2.zero, 1f, Vector2.right)).Setup(particles);
        Assert.IsFalse(particles.TryGet(BuiltinAttributes.Velocity, out _));
    }

    private void AssertInvalid(SwarmSource.Spawn spawn)
    {
        ParticleSet set = new ParticleSet();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Source(spawn).Setup(set));
            Assert.AreEqual(0, set.Count);
            Assert.IsFalse(set.TryGet(BuiltinAttributes.RestPosition, out _));
        }
        finally
        {
            set.Dispose();
        }
    }

    private static SwarmSource Filled(int seed)
    {
        SwarmSource source = Source(Disk(3, 8, new Vector2(1f, 2f), 4f, new Vector2(0f, 1f)));
        source.Seed = seed;
        source.JitterDegrees = 12f;
        return source;
    }

    private static SwarmSource Source(params SwarmSource.Spawn[] spawns)
    {
        return new SwarmSource { Spawns = new List<SwarmSource.Spawn>(spawns) };
    }

    private static SwarmSource.Spawn Disk(int team, int count, Vector2 center, float radius, Vector2 direction)
    {
        return new SwarmSource.Spawn
        {
            TeamIndex = team,
            Count = count,
            Center = center,
            Radius = radius,
            InitialDirection = direction,
        };
    }

    private Vector3[] ReadRest(int count)
    {
        return ReadRest(particles, count);
    }

    private static Vector3[] ReadRest(ParticleSet set, int count)
    {
        Vector3[] data = new Vector3[count];
        set.Get(BuiltinAttributes.RestPosition).GetData(data);
        return data;
    }

    private Vector3[] ReadHeading(int count)
    {
        return ReadHeading(particles, count);
    }

    private static Vector3[] ReadHeading(ParticleSet set, int count)
    {
        Vector3[] data = new Vector3[count];
        set.Get(BuiltinAttributes.Heading).GetData(data);
        return data;
    }

    private uint[] ReadTeams(int count)
    {
        return ReadTeams(particles, count);
    }

    private static uint[] ReadTeams(ParticleSet set, int count)
    {
        uint[] data = new uint[count];
        set.Get(BuiltinAttributes.TeamId).GetData(data);
        return data;
    }

    private static void AssertBits(Vector3[] actual, Vector3[] expected)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            AssertBits(actual[i].x, expected[i].x);
            AssertBits(actual[i].y, expected[i].y);
            AssertBits(actual[i].z, expected[i].z);
        }
    }

    private static void AssertBits(float actual, float expected)
    {
        Assert.AreEqual(
            BitConverter.SingleToInt32Bits(expected),
            BitConverter.SingleToInt32Bits(actual));
    }
}

[TestFixture]
public class SwarmSourceWorldTests
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
    public void Rebuild_CopiesRestToPosition()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        host = new GameObject("SwarmSource_World");
        host.SetActive(false);

        effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(DataSourceKind.Swarm, 1f, new SimPass[] { new IntegratePass() }, null);
        SwarmSource swarm = (SwarmSource)effect.ResolveSource();
        swarm.Spawns = new List<SwarmSource.Spawn>
        {
            new SwarmSource.Spawn
            {
                TeamIndex = 0,
                Count = 4,
                Center = new Vector2(-2f, 0f),
                Radius = 1f,
                InitialDirection = Vector2.right,
            },
            new SwarmSource.Spawn
            {
                TeamIndex = 1,
                Count = 6,
                Center = new Vector2(2f, 0f),
                Radius = 1f,
                InitialDirection = Vector2.up,
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

        effect.SetTeams(new[]
        {
            new TeamProfile(),
            new TeamProfile(),
        });
        Assert.DoesNotThrow(() => world.Rebuild());
        Assert.IsTrue(world.enabled);

        FieldInfo particlesField = typeof(SimulationWorld).GetField(
            "particles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(particlesField);
        ParticleSet set = (ParticleSet)particlesField.GetValue(world);
        Assert.AreEqual(10, set.Count);

        Vector3[] rest = new Vector3[10];
        Vector3[] position = new Vector3[10];
        set.Get(BuiltinAttributes.RestPosition).GetData(rest);
        set.Get(BuiltinAttributes.Position).GetData(position);
        Assert.AreEqual(
            BitConverter.SingleToInt32Bits(rest[0].x),
            BitConverter.SingleToInt32Bits(position[0].x));
        Assert.AreEqual(
            BitConverter.SingleToInt32Bits(rest[0].y),
            BitConverter.SingleToInt32Bits(position[0].y));
        Assert.AreEqual(
            BitConverter.SingleToInt32Bits(rest[0].z),
            BitConverter.SingleToInt32Bits(position[0].z));
    }
}
