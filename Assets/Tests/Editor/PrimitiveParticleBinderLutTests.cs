using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class PrimitiveParticleBinderLutTests
{
    [Test]
    public void BuildLutPixels_EndpointsMatchGradientEvaluate()
    {
        Gradient gradient = DebugFieldQuadSlot.DefaultFireGradient();
        Color[] pixels = PrimitiveParticleBinder.BuildLutPixels(gradient, 256);

        Assert.AreEqual(256, pixels.Length);
        Assert.AreEqual(gradient.Evaluate(0f), pixels[0]);
        Assert.AreEqual(gradient.Evaluate(1f), pixels[255]);
        Assert.AreNotEqual(pixels[0], pixels[255]);
    }
}
