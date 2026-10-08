using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

[TestFixture]
public class PrimitiveParticleBinderTeamPaletteShaderTests
{
    private const string ComputePath = "Assets/Tests/Editor/ParticlePaletteAddressing.compute";
    private const float ChannelTolerance = 1f / 255f;

    private ParticleSet particles;
    private FieldSet fields;
    private ComputeShader shader;

    [SetUp]
    public void SetUp()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        Assume.That(shader != null, "ParticlePaletteAddressing.compute must be imported.");
        Assume.That(shader.HasKernel("PaletteAddressing"));

        particles = new ParticleSet();
        particles.EnsureCapacity(4);
        particles.RegisterAttribute(BuiltinAttributes.Position);
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        fields = new FieldSet();
    }

    [TearDown]
    public void TearDown()
    {
        particles?.Dispose();
        particles = null;
        fields?.Dispose();
        fields = null;
        shader = null;
    }

    [Test]
    public void Rows_EndsMatchTexels_MiddleMatchesNeighborAverage()
    {
        Texture2D lut = Bake(new[]
        {
            Ramp(Color.black, Color.white),
            Ramp(Color.blue, Color.green),
        });
        try
        {
            Vector4[] got = Sample(lut, new[]
            {
                Input(0f, 0, 2),
                Input(1f, 0, 2),
                Input(0.5f, 0, 2),
                Input(0f, 1, 2),
                Input(1f, 1, 2),
                Input(0.5f, 1, 2),
            });

            AssertColor(lut.GetPixel(0, 0), got[0], ChannelTolerance, "row0 value0");
            AssertColor(lut.GetPixel(255, 0), got[1], ChannelTolerance, "row0 value1");
            AssertColor(Middle(lut, 0), got[2], ChannelTolerance, "row0 value0.5");
            AssertColor(lut.GetPixel(0, 1), got[3], ChannelTolerance, "row1 value0");
            AssertColor(lut.GetPixel(255, 1), got[4], ChannelTolerance, "row1 value1");
            AssertColor(Middle(lut, 1), got[5], ChannelTolerance, "row1 value0.5");
        }
        finally
        {
            DisposeBaked();
        }
    }

    [Test]
    public void TeamId_AtCountAndFarPast_TakesLastRow()
    {
        Texture2D lut = Bake(new[] { Flat(Color.red), Flat(Color.blue) });
        try
        {
            Vector4[] got = Sample(lut, new[]
            {
                Input(1f, 2, 2),
                Input(1f, 9, 2),
            });
            AssertColor(lut.GetPixel(255, 1), got[0], ChannelTolerance, "teamId == teamCount");
            AssertColor(lut.GetPixel(255, 1), got[1], ChannelTolerance, "teamId 9");
        }
        finally
        {
            DisposeBaked();
        }
    }

    [Test]
    public void UseLutOff_WithTeams_SamplesX0()
    {
        Texture2D lut = Bake(new[]
        {
            Ramp(Color.red, Color.yellow),
            Ramp(Color.blue, Color.cyan),
        });
        try
        {
            Vector4[] got = Sample(lut, new[]
            {
                Input(1f, 0, 2, useLut: 0f),
                Input(1f, 1, 2, useLut: 0f),
            });
            AssertColor(lut.GetPixel(0, 0), got[0], ChannelTolerance, "team0 x0");
            AssertColor(lut.GetPixel(0, 1), got[1], ChannelTolerance, "team1 x0");
        }
        finally
        {
            DisposeBaked();
        }
    }

    [Test]
    public void NoTeams_ValueSamplesMidRow_AlphaMultiplies()
    {
        Color texel = new Color(0.2f, 0.8f, 0.4f, 0.5f);
        Texture2D lut = Bake(new[] { Flat(texel) });
        try
        {
            Vector4 color = new Vector4(1f, 1f, 1f, 0.4f);
            Vector4[] got = Sample(lut, new[] { Input(1f, 0, 1, useTeams: 0f, color: color) });
            Color stored = lut.GetPixel(255, 0);
            Color expected = stored;
            expected.a *= color.w;
            AssertColor(expected, got[0], ChannelTolerance, "no teams alpha");
            Assert.Greater(Mathf.Abs(got[0].w - 1f), ChannelTolerance, "alpha is not 1");
            Assert.Greater(Mathf.Abs(got[0].w - stored.a), ChannelTolerance, "alpha is not the raw lut alpha");
        }
        finally
        {
            DisposeBaked();
        }
    }

    [Test]
    public void BothFlagsOff_ReturnsColor()
    {
        Texture2D lut = Bake(new[] { Flat(Color.red) });
        try
        {
            Vector4 color = new Vector4(0.2f, 0.3f, 0.4f, 0.8f);
            Vector4[] got = Sample(lut, new[]
            {
                Input(1f, 3, 1, useLut: 0f, useTeams: 0f, color: color),
            });
            AssertColor(color, got[0], ChannelTolerance, "flat color");
        }
        finally
        {
            DisposeBaked();
        }
    }

    [Test]
    public void SolidRows_ValueHalf_DoesNotBleed()
    {
        Texture2D lut = Bake(new[] { Flat(Color.red), Flat(Color.blue) });
        try
        {
            Vector4[] got = Sample(lut, new[]
            {
                Input(0.5f, 0, 2),
                Input(0.5f, 1, 2),
            });
            AssertColor(Color.red, got[0], ChannelTolerance, "red row");
            AssertColor(Color.blue, got[1], ChannelTolerance, "blue row");
        }
        finally
        {
            DisposeBaked();
        }
    }

    [Test]
    public void ThreeSolidRows_EachCenterStaysOnItsRow()
    {
        Color[] rows = { Color.red, Color.green, Color.blue };
        Texture2D lut = Bake(Flats(rows));
        try
        {
            Vector4[] got = Sample(lut, InputsAtHalf(3));
            for (int i = 0; i < rows.Length; i++)
                AssertColor(rows[i], got[i], ChannelTolerance, "team " + i);
        }
        finally
        {
            DisposeBaked();
        }
    }

    [Test]
    public void MaxTeams_EachSolidRowStaysPut()
    {
        Color[] rows =
        {
            Color.red, Color.green, Color.blue, Color.yellow,
            Color.cyan, Color.magenta, Color.white, new Color(0.2f, 0.4f, 0.8f, 1f),
        };
        Assert.AreEqual(TeamProfile.MaxTeams, rows.Length);
        Texture2D lut = Bake(Flats(rows));
        try
        {
            Assert.AreEqual(TeamProfile.MaxTeams, lut.height);
            Vector4[] got = Sample(lut, InputsAtHalf(rows.Length));
            for (int i = 0; i < rows.Length; i++)
                AssertColor(rows[i], got[i], ChannelTolerance, "team " + i);
        }
        finally
        {
            DisposeBaked();
        }
    }

    [Test]
    public void Scale_SaturatesAboveOne_HalfScaleHitsMiddle_NegativeClampsToZero()
    {
        Texture2D lut = Bake(new[] { Ramp(Color.red, Color.blue) });
        try
        {
            Vector4[] got = Sample(lut, new[]
            {
                Input(0.75f, 0, 1, scale: 2f),
                Input(1f, 0, 1, scale: 0.5f),
                Input(-1f, 0, 1, scale: 1f),
            });
            AssertColor(lut.GetPixel(255, 0), got[0], ChannelTolerance, "scale 2");
            AssertColor(Middle(lut, 0), got[1], ChannelTolerance, "scale 0.5");
            AssertColor(lut.GetPixel(0, 0), got[2], ChannelTolerance, "value < 0");
        }
        finally
        {
            DisposeBaked();
        }
    }

    private PrimitiveParticleBinder binder;

    private Texture2D Bake(Gradient[] rows)
    {
        TeamProfile[] teams = new TeamProfile[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            teams[i] = new TeamProfile { Color = rows[i] };

        binder = new PrimitiveParticleBinder(0.05f, Color.white, null, 1f, teams);
        binder.Initialize(new SimContext(particles, fields, System.Array.Empty<ComputeShader>(), null));
        Texture2D lut = (Texture2D)typeof(PrimitiveParticleBinder).GetField(
            "teamLut", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(binder);
        Assert.IsNotNull(lut);
        Assert.AreEqual("M3D_ParticleBillboard_TeamLUT", lut.name);
        return lut;
    }

    private void DisposeBaked()
    {
        binder?.Dispose();
        binder = null;
    }

    private struct PaletteInput
    {
        public float Value;
        public uint Team;
        public float UseLut;
        public float UseTeams;
        public float TeamCount;
        public float Scale;
        public Vector4 Color;
    }

    private static PaletteInput Input(
        float value,
        uint team,
        int teamCount,
        float useLut = 1f,
        float useTeams = 1f,
        float scale = 1f,
        Vector4? color = null)
    {
        return new PaletteInput
        {
            Value = value,
            Team = team,
            UseLut = useLut,
            UseTeams = useTeams,
            TeamCount = teamCount,
            Scale = scale,
            Color = color ?? new Vector4(1f, 1f, 1f, 1f),
        };
    }

    private static PaletteInput[] InputsAtHalf(int teamCount)
    {
        PaletteInput[] inputs = new PaletteInput[teamCount];
        for (int i = 0; i < teamCount; i++)
            inputs[i] = Input(0.5f, (uint)i, teamCount);
        return inputs;
    }

    private Vector4[] Sample(Texture2D lut, PaletteInput[] inputs)
    {
        int count = inputs.Length;
        int kernel = shader.FindKernel("PaletteAddressing");
        var values = new float[count];
        var teams = new uint[count];
        var useLut = new float[count];
        var useTeams = new float[count];
        var teamCount = new float[count];
        var scale = new float[count];
        var color = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = inputs[i].Value;
            teams[i] = inputs[i].Team;
            useLut[i] = inputs[i].UseLut;
            useTeams[i] = inputs[i].UseTeams;
            teamCount[i] = inputs[i].TeamCount;
            scale[i] = inputs[i].Scale;
            color[i] = inputs[i].Color;
        }

        GraphicsBuffer valueBuf = null;
        GraphicsBuffer teamBuf = null;
        GraphicsBuffer useLutBuf = null;
        GraphicsBuffer useTeamsBuf = null;
        GraphicsBuffer teamCountBuf = null;
        GraphicsBuffer scaleBuf = null;
        GraphicsBuffer colorBuf = null;
        GraphicsBuffer outBuf = null;
        try
        {
            valueBuf = Structured(count, 4, values);
            teamBuf = Structured(count, 4, teams);
            useLutBuf = Structured(count, 4, useLut);
            useTeamsBuf = Structured(count, 4, useTeams);
            teamCountBuf = Structured(count, 4, teamCount);
            scaleBuf = Structured(count, 4, scale);
            colorBuf = Structured(count, 16, color);
            outBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16);

            shader.SetTexture(kernel, "_LutTex", lut);
            shader.SetBuffer(kernel, "_InValue", valueBuf);
            shader.SetBuffer(kernel, "_InTeam", teamBuf);
            shader.SetBuffer(kernel, "_InUseLut", useLutBuf);
            shader.SetBuffer(kernel, "_InUseTeams", useTeamsBuf);
            shader.SetBuffer(kernel, "_InTeamCount", teamCountBuf);
            shader.SetBuffer(kernel, "_InScale", scaleBuf);
            shader.SetBuffer(kernel, "_InColor", colorBuf);
            shader.SetBuffer(kernel, "_OutColor", outBuf);
            shader.SetInt("_CaseCount", count);
            shader.Dispatch(kernel, 1, 1, 1);

            var output = new Vector4[count];
            outBuf.GetData(output);
            return output;
        }
        finally
        {
            Release(valueBuf);
            Release(teamBuf);
            Release(useLutBuf);
            Release(useTeamsBuf);
            Release(teamCountBuf);
            Release(scaleBuf);
            Release(colorBuf);
            Release(outBuf);
        }
    }

    private static GraphicsBuffer Structured<T>(int count, int stride, T[] data) where T : struct
    {
        var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
        buffer.SetData(data);
        return buffer;
    }

    private static void Release(GraphicsBuffer buffer)
    {
        buffer?.Release();
    }

    private static Gradient Ramp(Color a, Color b)
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 1f) },
            new[] { new GradientAlphaKey(a.a, 0f), new GradientAlphaKey(b.a, 1f) });
        return gradient;
    }

    private static Gradient Flat(Color color)
    {
        return Ramp(color, color);
    }

    private static Gradient[] Flats(Color[] colors)
    {
        Gradient[] rows = new Gradient[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            rows[i] = Flat(colors[i]);
        return rows;
    }

    private static Color Middle(Texture2D lut, int row)
    {
        return (lut.GetPixel(127, row) + lut.GetPixel(128, row)) * 0.5f;
    }

    private static void AssertColor(Color expected, Vector4 actual, float tolerance, string label)
    {
        Assert.LessOrEqual(Mathf.Abs(expected.r - actual.x), tolerance, label + " r");
        Assert.LessOrEqual(Mathf.Abs(expected.g - actual.y), tolerance, label + " g");
        Assert.LessOrEqual(Mathf.Abs(expected.b - actual.z), tolerance, label + " b");
        Assert.LessOrEqual(Mathf.Abs(expected.a - actual.w), tolerance, label + " a");
    }

    private static void AssertColor(Vector4 expected, Vector4 actual, float tolerance, string label)
    {
        AssertColor(new Color(expected.x, expected.y, expected.z, expected.w), actual, tolerance, label);
    }
}
