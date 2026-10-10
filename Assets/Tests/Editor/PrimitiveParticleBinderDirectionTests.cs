using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class PrimitiveParticleBinderDirectionTests
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
    public void Direction_WithVelocity_SetsFlagsAndScale()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null);
        GraphicsBuffer velocity = particles.RegisterAttribute(BuiltinAttributes.Velocity);
        Assert.AreEqual(12, velocity.stride, "velocity stride");

        PrimitiveParticleBinder binder = DirectionBinder(null, new Vector3(0f, 0f, 2f));
        try
        {
            binder.Initialize(Context());
            binder.Execute(Context());
            Assert.IsTrue(binder.UsesDirectionColor);
            MaterialPropertyBlock props = Props(binder);
            Assert.AreEqual(1f, props.GetFloat("_UseDirection"));
            Assert.AreEqual(1f, props.GetFloat("_UseLut"));
            Assert.AreEqual(1f, props.GetFloat("_Scale"));
            Vector4 axis = props.GetVector("_DirAxis");
            Assert.AreEqual(0f, axis.x, 1e-5f);
            Assert.AreEqual(0f, axis.y, 1e-5f);
            Assert.AreEqual(1f, axis.z, 1e-5f);
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void Direction_WithoutVelocity_FallsBackToValue()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null);
        particles.RegisterAttribute(BuiltinAttributes.Value);
        PrimitiveParticleBinder binder = DirectionBinder(null, new Vector3(0f, 0f, 1f));
        try
        {
            binder.Initialize(Context());
            binder.Execute(Context());
            Assert.IsFalse(binder.UsesDirectionColor);
            MaterialPropertyBlock props = Props(binder);
            Assert.AreEqual(0f, props.GetFloat("_UseDirection"));
            Assert.AreEqual(1f, props.GetFloat("_UseLut"));
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void OldConstructors_StayOnValue()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null);
        particles.RegisterAttribute(BuiltinAttributes.Velocity);
        PrimitiveParticleBinder four = new PrimitiveParticleBinder(0.05f, Color.white, null, 1f);
        PrimitiveParticleBinder five = new PrimitiveParticleBinder(0.05f, Color.white, null, 1f, null);
        try
        {
            four.Initialize(Context());
            five.Initialize(Context());
            four.Execute(Context());
            five.Execute(Context());
            Assert.IsFalse(four.UsesDirectionColor);
            Assert.IsFalse(five.UsesDirectionColor);
            Assert.AreEqual(0f, Props(four).GetFloat("_UseDirection"));
            Assert.AreEqual(0f, Props(five).GetFloat("_UseDirection"));
        }
        finally
        {
            four.Dispose();
            five.Dispose();
        }
    }

    [Test]
    public void ZeroAxis_BecomesWorldUp()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null);
        particles.RegisterAttribute(BuiltinAttributes.Velocity);
        PrimitiveParticleBinder binder = DirectionBinder(null, Vector3.zero);
        try
        {
            binder.Initialize(Context());
            binder.Execute(Context());
            Vector4 axis = Props(binder).GetVector("_DirAxis");
            Assert.AreEqual(0f, axis.x, 1e-5f);
            Assert.AreEqual(0f, axis.y, 1e-5f);
            Assert.AreEqual(1f, axis.z, 1e-5f);
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void Direction_WithTeams_RebakeLutInEditor()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null);
        particles.RegisterAttribute(BuiltinAttributes.Velocity);
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        TeamProfile edited = new TeamProfile { Color = Flat(Color.blue) };
        var teams = new List<TeamProfile> { new TeamProfile { Color = Flat(Color.red) }, edited };
        PrimitiveParticleBinder binder = DirectionBinder(teams, new Vector3(0f, 0f, 1f));
        try
        {
            binder.Initialize(Context());
            edited.Color = Flat(Color.green);
            for (int i = 0; i < PrimitiveParticleBinder.LiveLutCheckInterval; i++)
            {
                binder.Execute(Context());
            }

            Texture2D lut = TeamLut(binder);
            AssertColors(Color.green, lut.GetPixel(0, 1));
            Assert.AreEqual(1, binder.LutRefreshCount);
            Assert.AreEqual(1f, Props(binder).GetFloat("_UseLut"));
            Assert.AreEqual(1f, Props(binder).GetFloat("_Scale"));
        }
        finally
        {
            binder.Dispose();
        }
    }

    [Test]
    public void Direction_WithoutTeamsOrValue_UsesGradientLutNotFlatColor()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null);
        particles.RegisterAttribute(BuiltinAttributes.Velocity);
        Gradient gradient = Flat(new Color(0.2f, 0.4f, 0.6f, 1f));
        PrimitiveParticleBinder binder = new PrimitiveParticleBinder(
            0.05f,
            Color.red,
            gradient,
            4f,
            null,
            ParticleColorSource.Direction,
            new Vector3(0f, 0f, 1f));
        try
        {
            binder.Initialize(Context());
            binder.Execute(Context());
            Assert.IsTrue(binder.UsesDirectionColor);
            MaterialPropertyBlock props = Props(binder);
            Assert.AreEqual(1f, props.GetFloat("_UseLut"));
            Assert.AreEqual(0f, props.GetFloat("_UseTeams"));
            Assert.AreEqual(1f, props.GetFloat("_Scale"));
            Texture lut = props.GetTexture("_LutTex");
            Assert.IsNotNull(lut);
            Assert.AreEqual("M3D_ParticleBillboard_LUT", lut.name);
            Assert.AreEqual(256, lut.width);
            Assert.AreEqual(1, lut.height);
        }
        finally
        {
            binder.Dispose();
        }
    }

    private PrimitiveParticleBinder DirectionBinder(IReadOnlyList<TeamProfile> teams, Vector3 axis)
    {
        return new PrimitiveParticleBinder(
            0.05f,
            Color.white,
            Flat(Color.white),
            4f,
            teams,
            ParticleColorSource.Direction,
            axis);
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
    }

    private static MaterialPropertyBlock Props(PrimitiveParticleBinder binder)
    {
        return Get<MaterialPropertyBlock>(binder, "props");
    }

    private static Texture2D TeamLut(PrimitiveParticleBinder binder)
    {
        Texture2D lut = Get<Texture2D>(binder, "teamLut");
        Assert.IsNotNull(lut);
        return lut;
    }

    private static T Get<T>(PrimitiveParticleBinder binder, string name)
    {
        FieldInfo field = typeof(PrimitiveParticleBinder).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, name);
        return (T)field.GetValue(binder);
    }
}
