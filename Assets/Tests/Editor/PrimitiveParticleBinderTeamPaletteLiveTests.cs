using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

[TestFixture]
public class PrimitiveParticleBinderTeamPaletteLiveTests
{
    private const float ChannelTolerance = 1f / 255f;
    private const string TeamCountWarning =
        "PrimitiveParticleBinder: team count changed after Initialize. Rebuild to resize the team palette.";

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
    public void InPlaceGradientEdit_RebakeRowAfterInterval()
    {
        AssertShader();
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        TeamProfile keep = new TeamProfile { Color = Flat(Color.red) };
        TeamProfile edited = new TeamProfile { Color = Flat(Color.blue) };
        PrimitiveParticleBinder binder = NewBinder(new List<TeamProfile> { keep, edited });

        try
        {
            binder.Initialize(Context());
            edited.Color.SetKeys(
                new[] { new GradientColorKey(Color.green, 0f), new GradientColorKey(Color.green, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            ExecuteTimes(binder, PrimitiveParticleBinder.LiveLutCheckInterval);

            Texture2D lut = TeamLut(binder);
            AssertRow(lut, 0, keep.Color);
            AssertRow(lut, 1, edited.Color);
            Assert.AreEqual(1, binder.LutRefreshCount);
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void ReplacedGradient_RebakeRowAfterInterval()
    {
        AssertShader();
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        TeamProfile keep = new TeamProfile { Color = Flat(Color.red) };
        TeamProfile edited = new TeamProfile { Color = Flat(Color.blue) };
        PrimitiveParticleBinder binder = NewBinder(new List<TeamProfile> { keep, edited });

        try
        {
            binder.Initialize(Context());
            edited.Color = Flat(Color.green);
            ExecuteTimes(binder, PrimitiveParticleBinder.LiveLutCheckInterval);

            Texture2D lut = TeamLut(binder);
            AssertRow(lut, 0, keep.Color);
            AssertRow(lut, 1, edited.Color);
            Assert.AreEqual(1, binder.LutRefreshCount);
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void UnchangedGradients_CheckWithoutRefresh()
    {
        AssertShader();
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        PrimitiveParticleBinder binder = NewBinder(new List<TeamProfile>
        {
            new TeamProfile { Color = Flat(Color.red) },
            new TeamProfile { Color = Flat(Color.blue) },
        });

        try
        {
            binder.Initialize(Context());
            ExecuteTimes(binder, 3 * PrimitiveParticleBinder.LiveLutCheckInterval);
            Assert.AreEqual(3, binder.LutCheckCount);
            Assert.AreEqual(0, binder.LutRefreshCount);
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void Refresh_KeepsTheSameTeamLutTexture()
    {
        AssertShader();
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        TeamProfile edited = new TeamProfile { Color = Flat(Color.blue) };
        PrimitiveParticleBinder binder = NewBinder(new List<TeamProfile>
        {
            new TeamProfile { Color = Flat(Color.red) },
            edited,
        });

        try
        {
            binder.Initialize(Context());
            Texture2D lut = TeamLut(binder);
            int before = CountAlive("M3D_ParticleBillboard_TeamLUT");
            edited.Color = Flat(Color.green);
            ExecuteTimes(binder, PrimitiveParticleBinder.LiveLutCheckInterval);
            Assert.AreSame(lut, TeamLut(binder));
            Assert.AreEqual(before, CountAlive("M3D_ParticleBillboard_TeamLUT"));
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void NullColor_UsesFallback_LaterGradientIsSeen()
    {
        AssertShader();
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        Gradient fallback = Flat(Color.green);
        TeamProfile profile = new TeamProfile { Color = null };
        PrimitiveParticleBinder binder = new PrimitiveParticleBinder(
            0.05f, Color.white, fallback, 1f, new List<TeamProfile> { profile });

        try
        {
            binder.Initialize(Context());
            AssertRow(TeamLut(binder), 0, fallback);
            profile.Color = Flat(Color.blue);
            ExecuteTimes(binder, PrimitiveParticleBinder.LiveLutCheckInterval);
            AssertRow(TeamLut(binder), 0, profile.Color);
            Assert.AreEqual(1, binder.LutRefreshCount);
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void ShorterList_MissingRowUsesFallback_WarnsOnce()
    {
        AssertShader();
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        Gradient fallback = Flat(Color.green);
        var teams = new List<TeamProfile>
        {
            new TeamProfile { Color = Flat(Color.red) },
            new TeamProfile { Color = Flat(Color.blue) },
        };
        PrimitiveParticleBinder binder = new PrimitiveParticleBinder(
            0.05f, Color.white, fallback, 1f, teams);

        try
        {
            binder.Initialize(Context());
            teams.RemoveAt(1);
            LogAssert.Expect(LogType.Warning, TeamCountWarning);
            ExecuteTimes(binder, PrimitiveParticleBinder.LiveLutCheckInterval);

            Texture2D lut = TeamLut(binder);
            Assert.AreEqual(2, lut.height);
            AssertRow(lut, 0, teams[0].Color);
            AssertRow(lut, 1, fallback);
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void LongerList_IgnoresExtraTeam_WarnsOnce()
    {
        AssertShader();
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        Gradient row0 = Flat(Color.red);
        Gradient row1 = Flat(Color.blue);
        var teams = new List<TeamProfile>
        {
            new TeamProfile { Color = row0 },
            new TeamProfile { Color = row1 },
        };
        PrimitiveParticleBinder binder = NewBinder(teams);

        try
        {
            binder.Initialize(Context());
            teams.Add(new TeamProfile { Color = Flat(Color.yellow) });
            LogAssert.Expect(LogType.Warning, TeamCountWarning);
            ExecuteTimes(binder, PrimitiveParticleBinder.LiveLutCheckInterval);

            Texture2D lut = TeamLut(binder);
            Assert.AreEqual(2, lut.height);
            AssertRow(lut, 0, row0);
            AssertRow(lut, 1, row1);
            Assert.AreEqual(0, binder.LutRefreshCount);
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void Dispose_ClearsTeamsAndTeamLut()
    {
        AssertShader();
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        PrimitiveParticleBinder binder = NewBinder(new List<TeamProfile>
        {
            new TeamProfile { Color = Flat(Color.red) },
        });

        binder.Initialize(Context());
        binder.Dispose();
        Assert.IsNull(GetField<IReadOnlyList<TeamProfile>>(binder, "teams"));
        Assert.IsNull(GetField<Texture2D>(binder, "teamLut"));
    }

    [Test]
    public void WithoutTeams_DoesNotCheck()
    {
        AssertShader();
        PrimitiveParticleBinder binder = new PrimitiveParticleBinder(0.05f, Color.white, null, 1f);

        try
        {
            binder.Initialize(Context());
            Assert.IsNull(GetField<IReadOnlyList<TeamProfile>>(binder, "teams"));
            ExecuteTimes(binder, PrimitiveParticleBinder.LiveLutCheckInterval * 2);
            Assert.AreEqual(0, binder.LutCheckCount);
        }
        finally
        {
            binder.Dispose();
        }
    }

    private void ExecuteTimes(PrimitiveParticleBinder binder, int count)
    {
        SimContext context = Context();
        for (int i = 0; i < count; i++)
        {
            binder.Execute(context);
        }
    }

    private PrimitiveParticleBinder NewBinder(IReadOnlyList<TeamProfile> teams)
    {
        return new PrimitiveParticleBinder(0.05f, Color.white, null, 1f, teams);
    }

    private SimContext Context()
    {
        return new SimContext(particles, fields, System.Array.Empty<ComputeShader>(), null);
    }

    private static void AssertShader()
    {
        Assert.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");
    }

    private static Gradient Flat(Color color)
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
            new[] { new GradientAlphaKey(color.a, 0f), new GradientAlphaKey(color.a, 1f) });
        return gradient;
    }

    private static void AssertRow(Texture2D lut, int row, Gradient gradient)
    {
        Color[] expected = PrimitiveParticleBinder.BuildLutPixels(gradient, lut.width);
        for (int x = 0; x < expected.Length; x++)
        {
            Color actual = lut.GetPixel(x, row);
            Assert.LessOrEqual(Mathf.Abs(expected[x].r - actual.r), ChannelTolerance, "row " + row + " x " + x + " r");
            Assert.LessOrEqual(Mathf.Abs(expected[x].g - actual.g), ChannelTolerance, "row " + row + " x " + x + " g");
            Assert.LessOrEqual(Mathf.Abs(expected[x].b - actual.b), ChannelTolerance, "row " + row + " x " + x + " b");
            Assert.LessOrEqual(Mathf.Abs(expected[x].a - actual.a), ChannelTolerance, "row " + row + " x " + x + " a");
        }
    }

    private static int CountAlive(string objectName)
    {
        Texture2D[] found = Resources.FindObjectsOfTypeAll<Texture2D>();
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

    private static Texture2D TeamLut(PrimitiveParticleBinder binder)
    {
        Texture2D lut = GetField<Texture2D>(binder, "teamLut");
        Assert.IsNotNull(lut);
        Assert.AreEqual("M3D_ParticleBillboard_TeamLUT", lut.name);
        return lut;
    }

    private static T GetField<T>(PrimitiveParticleBinder binder, string name)
    {
        FieldInfo field = typeof(PrimitiveParticleBinder).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, name);
        return (T)field.GetValue(binder);
    }
}
