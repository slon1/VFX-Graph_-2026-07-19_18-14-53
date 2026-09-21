using System;
using NUnit.Framework;
using UnityEngine;

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
        PrimitiveParticleBinder binder = new PrimitiveParticleBinder(0.05f, Color.white);
        Assert.DoesNotThrow(() => binder.Initialize(context));
        Assert.DoesNotThrow(() => binder.Execute(context));
        binder.Dispose();

        Material[] leftover = Resources.FindObjectsOfTypeAll<Material>();
        for (int i = 0; i < leftover.Length; i++)
        {
            if (leftover[i] != null && leftover[i].name == "M3D_ParticleBillboard")
            {
                Assert.Fail("Dispose left M3D_ParticleBillboard material alive (expected DestroyImmediate).");
            }
        }
    }
}
