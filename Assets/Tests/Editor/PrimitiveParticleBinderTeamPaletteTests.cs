using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class PrimitiveParticleBinderTeamPaletteTests
{
    private const float ChannelTolerance = 1f / 255f;

    private ParticleSet particles;
    private FieldSet fields;

    [SetUp]
    public void SetUp()
    {
        particles = new ParticleSet();
        particles.EnsureCapacity(4);
        particles.RegisterAttribute(BuiltinAttributes.Position);
        fields = new FieldSet();
    }

    [TearDown]
    public void TearDown()
    {
        particles?.Dispose();
        particles = null;
        fields?.Dispose();
        fields = null;
    }

    [Test]
    public void TeamLut_RowsMatchGradientEnds()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        Gradient red = Flat(Color.red);
        Gradient blue = Flat(Color.blue);
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        particles.RegisterAttribute(BuiltinAttributes.Value);
        PrimitiveParticleBinder binder = NewBinder(new[]
        {
            new TeamProfile { Color = red },
            new TeamProfile { Color = blue },
        });

        try
        {
            binder.Initialize(Context());
            Texture2D lut = TeamLut(binder);
            Assert.AreEqual(256, lut.width);
            Assert.AreEqual(2, lut.height);
            AssertColors(red.Evaluate(0f), lut.GetPixel(0, 0));
            AssertColors(red.Evaluate(1f), lut.GetPixel(255, 0));
            AssertColors(blue.Evaluate(0f), lut.GetPixel(0, 1));
            AssertColors(blue.Evaluate(1f), lut.GetPixel(255, 1));
            Assert.Greater(ColorDistance(lut.GetPixel(0, 0), lut.GetPixel(0, 1)), ChannelTolerance);
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void TeamLut_ClampsHeightToMaxTeams()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        TeamProfile[] teams = new TeamProfile[9];
        for (int i = 0; i < teams.Length; i++)
        {
            teams[i] = new TeamProfile { Color = Flat(Color.white) };
        }

        PrimitiveParticleBinder binder = NewBinder(teams);
        try
        {
            binder.Initialize(Context());
            Assert.IsTrue(binder.UsesTeamPalette);
            Assert.AreEqual(TeamProfile.MaxTeams, TeamLut(binder).height);
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void NullTeamGradient_FallsBackToEffectThenFire()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        Gradient effectGradient = Flat(Color.green);
        PrimitiveParticleBinder fromEffect = new PrimitiveParticleBinder(
            0.05f,
            Color.white,
            effectGradient,
            1f,
            new[] { new TeamProfile { Color = null } });
        PrimitiveParticleBinder fromFire = new PrimitiveParticleBinder(
            0.05f,
            Color.white,
            null,
            1f,
            new[] { new TeamProfile { Color = null } });

        try
        {
            fromEffect.Initialize(Context());
            Texture2D effectLut = TeamLut(fromEffect);
            AssertColors(effectGradient.Evaluate(0f), effectLut.GetPixel(0, 0));
            AssertColors(effectGradient.Evaluate(1f), effectLut.GetPixel(255, 0));

            fromFire.Initialize(Context());
            Gradient fire = DebugFieldQuadSlot.DefaultFireGradient();
            Color[] firePixels = PrimitiveParticleBinder.BuildLutPixels(fire, 256);
            Texture2D fireLut = TeamLut(fromFire);
            AssertColors(firePixels[0], fireLut.GetPixel(0, 0));
            AssertColors(firePixels[255], fireLut.GetPixel(255, 0));
        }
        finally
        {
            fromEffect.Dispose();
            fromFire.Dispose();
        }
    }

    [Test]
    public void Constructors_WithoutTeams_LeavePaletteOff()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        PrimitiveParticleBinder four = new PrimitiveParticleBinder(0.05f, Color.white, null, 1f);
        PrimitiveParticleBinder missing = NewBinder(null);
        PrimitiveParticleBinder empty = NewBinder(new TeamProfile[0]);

        try
        {
            four.Initialize(Context());
            missing.Initialize(Context());
            empty.Initialize(Context());
            Assert.IsFalse(four.UsesTeamPalette);
            Assert.IsFalse(missing.UsesTeamPalette);
            Assert.IsFalse(empty.UsesTeamPalette);
        }
        finally
        {
            four.Dispose();
            missing.Dispose();
            empty.Dispose();
        }
    }

    [Test]
    public void MissingTeamId_KeepsTheSingleRowLut()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        particles.RegisterAttribute(BuiltinAttributes.Value);
        PrimitiveParticleBinder binder = NewBinder(new[] { new TeamProfile { Color = Flat(Color.red) } });

        try
        {
            binder.Initialize(Context());
            Assert.IsFalse(binder.UsesTeamPalette);
            Texture2D lut = GetField<Texture2D>(binder, "lutTexture");
            Assert.IsNotNull(lut);
            Assert.AreEqual("M3D_ParticleBillboard_LUT", lut.name);
            Assert.AreEqual(256, lut.width);
            Assert.AreEqual(1, lut.height);
            Assert.IsNull(GetField<Texture2D>(binder, "teamLut"));
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void TeamsWithoutValue_SampleTeamLutAtZero()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        PrimitiveParticleBinder binder = NewBinder(new[] { new TeamProfile { Color = Flat(Color.red) } });

        try
        {
            binder.Initialize(Context());
            Assert.IsTrue(binder.UsesTeamPalette);
            binder.Execute(Context());
            MaterialPropertyBlock props = GetProps(binder);
            Assert.AreEqual(0f, props.GetFloat("_UseLut"));
            Assert.AreEqual(1f, props.GetFloat("_UseTeams"));
            Assert.AreSame(TeamLut(binder), props.GetTexture("_LutTex"));
            Assert.IsNull(GetField<Texture2D>(binder, "dummyLut"));
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void Execute_WithTeamsAndValue_BindsBothFlags()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        particles.RegisterAttribute(BuiltinAttributes.Value);
        TeamProfile[] teams =
        {
            new TeamProfile { Color = Flat(Color.red) },
            new TeamProfile { Color = Flat(Color.blue) },
        };
        PrimitiveParticleBinder binder = NewBinder(teams);

        try
        {
            binder.Initialize(Context());
            binder.Execute(Context());
            MaterialPropertyBlock props = GetProps(binder);
            Assert.AreEqual(1f, props.GetFloat("_UseLut"));
            Assert.AreEqual(1f, props.GetFloat("_UseTeams"));
            Assert.AreEqual(teams.Length, props.GetFloat("_TeamCount"));
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void Dispose_ReleasesTeamLutAndAllowsASecondCall()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        particles.RegisterAttribute(BuiltinAttributes.Value);
        int teamLutsBefore = CountAlive<Texture2D>("M3D_ParticleBillboard_TeamLUT");
        int materialsBefore = CountAlive<Material>("M3D_ParticleBillboard");
        int dummyLutsBefore = CountAlive<Texture2D>("M3D_ParticleBillboard_DummyLut");
        PrimitiveParticleBinder binder = NewBinder(new[] { new TeamProfile { Color = Flat(Color.red) } });

        binder.Initialize(Context());
        binder.Dispose();
        Assert.DoesNotThrow(() => binder.Dispose());

        AssertSameCount(
            teamLutsBefore,
            CountAlive<Texture2D>("M3D_ParticleBillboard_TeamLUT"),
            "M3D_ParticleBillboard_TeamLUT");
        AssertSameCount(
            materialsBefore,
            CountAlive<Material>("M3D_ParticleBillboard"),
            "M3D_ParticleBillboard");
        AssertSameCount(
            dummyLutsBefore,
            CountAlive<Texture2D>("M3D_ParticleBillboard_DummyLut"),
            "M3D_ParticleBillboard_DummyLut");
    }

    private sealed class WorldFixture
    {
        public GameObject Host;
        public EffectAsset Effect;
    }

    [Test]
    public void World_Rebuild_EnablesTeamPalette()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        WorldFixture fixture = CreateWorld();
        try
        {
            SimulationWorld world = fixture.Host.GetComponent<SimulationWorld>();
            Assert.DoesNotThrow(() => world.Rebuild());
            Assert.IsTrue(world.enabled);

            FieldInfo bindersField = typeof(SimulationWorld).GetField(
                "binders", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(bindersField);
            var binders = (System.Collections.IList)bindersField.GetValue(world);
            PrimitiveParticleBinder binder = null;
            for (int i = 0; i < binders.Count; i++)
            {
                binder = binders[i] as PrimitiveParticleBinder;
                if (binder != null)
                {
                    break;
                }
            }

            Assert.IsNotNull(binder);
            Assert.IsTrue(binder.UsesTeamPalette);
        }
        finally
        {
            if (fixture.Host != null)
            {
                SimulationWorldTestCleanup.DestroyHost(fixture.Host);
            }

            if (fixture.Effect != null)
            {
                Object.DestroyImmediate(fixture.Effect);
            }
        }
    }

    private static WorldFixture CreateWorld()
    {
        GameObject host = new GameObject("TeamPalette_World");
        host.SetActive(false);
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(DataSourceKind.Swarm, 1f, new SimPass[] { new IntegratePass() }, null);
        SwarmSource swarm = (SwarmSource)effect.ResolveSource();
        swarm.Seed = 1;
        swarm.Spawns = new List<SwarmSource.Spawn>
        {
            new SwarmSource.Spawn
            {
                TeamIndex = 0,
                Count = 4,
                Center = Vector2.zero,
                Radius = 1f,
                InitialDirection = Vector2.right,
            },
        };
        effect.SetTeams(new[]
        {
            new TeamProfile { Name = "A", Color = Flat(Color.red) },
            new TeamProfile { Name = "B", Color = Flat(Color.blue) },
        });

        host.AddComponent<SimulationWorld>();
        ComputeShader dynamics = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/Shaders/GPU/Passes/DynamicsPasses.compute");
        Assert.IsNotNull(dynamics);
        SerializedObject soWorld = new SerializedObject(host.GetComponent<SimulationWorld>());
        soWorld.FindProperty("effect").objectReferenceValue = effect;
        soWorld.FindProperty("visualEffect").objectReferenceValue = null;
        SerializedProperty library = soWorld.FindProperty("passLibrary");
        library.arraySize = 1;
        library.GetArrayElementAtIndex(0).objectReferenceValue = dynamics;
        soWorld.ApplyModifiedPropertiesWithoutUndo();
        return new WorldFixture { Host = host, Effect = effect };
    }

    private PrimitiveParticleBinder NewBinder(IReadOnlyList<TeamProfile> teams)
    {
        return new PrimitiveParticleBinder(0.05f, Color.white, null, 1f, teams);
    }

    private SimContext Context()
    {
        return new SimContext(particles, fields, System.Array.Empty<ComputeShader>(), null);
    }

    private static Gradient Flat(Color color)
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
            new[] { new GradientAlphaKey(color.a, 0f), new GradientAlphaKey(color.a, 1f) });
        return gradient;
    }

    private static void AssertColors(Color expected, Color actual)
    {
        Assert.LessOrEqual(Mathf.Abs(expected.r - actual.r), ChannelTolerance, "r");
        Assert.LessOrEqual(Mathf.Abs(expected.g - actual.g), ChannelTolerance, "g");
        Assert.LessOrEqual(Mathf.Abs(expected.b - actual.b), ChannelTolerance, "b");
        Assert.LessOrEqual(Mathf.Abs(expected.a - actual.a), ChannelTolerance, "a");
    }

    private static float ColorDistance(Color a, Color b)
    {
        return Mathf.Max(
            Mathf.Abs(a.r - b.r),
            Mathf.Abs(a.g - b.g),
            Mathf.Abs(a.b - b.b));
    }

    private static Texture2D TeamLut(PrimitiveParticleBinder binder)
    {
        Texture2D lut = GetField<Texture2D>(binder, "teamLut");
        Assert.IsNotNull(lut);
        Assert.AreEqual("M3D_ParticleBillboard_TeamLUT", lut.name);
        return lut;
    }

    private static MaterialPropertyBlock GetProps(PrimitiveParticleBinder binder)
    {
        return GetField<MaterialPropertyBlock>(binder, "props");
    }

    private static int CountAlive<T>(string objectName) where T : Object
    {
        T[] found = Resources.FindObjectsOfTypeAll<T>();
        int count = 0;
        for (int i = 0; i < found.Length; i++)
        {
            if (found[i] != null && found[i].name == objectName)
            {
                count++;
            }
        }

        return count;
    }

    private static void AssertSameCount(int before, int after, string objectName)
    {
        Assert.AreEqual(before, after, objectName + ": было " + before + ", стало " + after + ".");
    }

    private static T GetField<T>(PrimitiveParticleBinder binder, string name)
    {
        FieldInfo field = typeof(PrimitiveParticleBinder).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, name);
        return (T)field.GetValue(binder);
    }
}
