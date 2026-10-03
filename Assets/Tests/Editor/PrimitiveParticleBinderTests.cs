using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class PrimitiveParticleBinderTests
{
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
    public void InitializeExecuteDispose_DoesNotThrowAndDestroysMaterial()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        SimContext context = new SimContext(particles, fields, Array.Empty<ComputeShader>(), null);
        PrimitiveParticleBinder binder = new PrimitiveParticleBinder(0.05f, Color.white, null, 1f);
        Assert.DoesNotThrow(() => binder.Initialize(context));
        Assert.DoesNotThrow(() => binder.Execute(context));

        MaterialPropertyBlock props = GetProps(binder);
        Assert.AreEqual(0f, props.GetFloat("_UseLut"));
        Texture2D dummyLut = GetField<Texture2D>(binder, "dummyLut");
        GraphicsBuffer dummyValues = GetField<GraphicsBuffer>(binder, "dummyValues");
        Assert.IsNotNull(dummyLut);
        Assert.AreEqual(1, dummyLut.width);
        Assert.AreEqual(1, dummyLut.height);
        Assert.IsNotNull(dummyValues);
        Assert.AreEqual(1, dummyValues.count);
        binder.Dispose();
        Assert.DoesNotThrow(() => binder.Dispose());

        Material[] leftover = Resources.FindObjectsOfTypeAll<Material>();
        for (int i = 0; i < leftover.Length; i++)
        {
            if (leftover[i] != null && leftover[i].name == "M3D_ParticleBillboard")
            {
                Assert.Fail("Dispose left M3D_ParticleBillboard material alive (expected DestroyImmediate).");
            }
        }

        Texture2D[] leftoverTextures = Resources.FindObjectsOfTypeAll<Texture2D>();
        for (int i = 0; i < leftoverTextures.Length; i++)
        {
            if (leftoverTextures[i] != null && leftoverTextures[i].name == "M3D_ParticleBillboard_DummyLut")
            {
                Assert.Fail("Dispose left M3D_ParticleBillboard_DummyLut alive.");
            }
        }
    }

    [Test]
    public void Execute_WithValueAttribute_EnablesLutAndKeepsTexture()
    {
        Assume.That(Shader.Find("M3D/ParticleBillboard") != null, "shader M3D/ParticleBillboard must be imported");

        particles.RegisterAttribute(BuiltinAttributes.Value);
        SimContext context = new SimContext(particles, fields, Array.Empty<ComputeShader>(), null);
        PrimitiveParticleBinder binder = new PrimitiveParticleBinder(
            0.05f,
            Color.white,
            DebugFieldQuadSlot.DefaultFireGradient(),
            1f);

        binder.Initialize(context);
        Texture2D baked = GetLut(binder);
        Assert.IsNotNull(baked);

        binder.Execute(context);
        Assert.AreEqual(1f, GetProps(binder).GetFloat("_UseLut"));
        Assert.AreSame(baked, GetLut(binder));

        binder.Execute(context);
        Assert.AreSame(baked, GetLut(binder));
        Assert.IsNull(GetField<Texture2D>(binder, "dummyLut"));
        Assert.IsNull(GetField<GraphicsBuffer>(binder, "dummyValues"));
        binder.Dispose();

        Texture2D[] leftover = Resources.FindObjectsOfTypeAll<Texture2D>();
        for (int i = 0; i < leftover.Length; i++)
        {
            if (leftover[i] != null && leftover[i].name == "M3D_ParticleBillboard_LUT")
            {
                Assert.Fail("Dispose left M3D_ParticleBillboard_LUT alive.");
            }
        }
    }

    private static MaterialPropertyBlock GetProps(PrimitiveParticleBinder binder)
    {
        return GetField<MaterialPropertyBlock>(binder, "props");
    }

    private static Texture2D GetLut(PrimitiveParticleBinder binder)
    {
        return GetField<Texture2D>(binder, "lutTexture");
    }

    private static T GetField<T>(PrimitiveParticleBinder binder, string name)
    {
        FieldInfo field = typeof(PrimitiveParticleBinder).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(binder);
    }
}
