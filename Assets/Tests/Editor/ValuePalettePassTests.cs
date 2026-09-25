using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class ValuePalettePassTests
{
    [Test]
    public void SpeedToValue_Contract()
    {
        SpeedToValuePass pass = new SpeedToValuePass();

        Assert.AreEqual(PassCategory.Dynamics, pass.Category);
        Assert.AreEqual("Speed To Value", pass.DisplayName);
        Assert.AreEqual(1f, pass.SpeedRef);
        Assert.AreEqual(AttrSets.Velocity, pass.Reads);
        Assert.AreEqual(AttrSets.Value, pass.Writes);
        Assert.AreEqual("SpeedToValue", KernelName(pass));
    }

    [Test]
    public void HeadingToValue_Contract()
    {
        HeadingToValuePass pass = new HeadingToValuePass();

        Assert.AreEqual(PassCategory.Dynamics, pass.Category);
        Assert.AreEqual("Heading To Value", pass.DisplayName);
        Assert.AreEqual(AttrSets.Heading, pass.Reads);
        Assert.AreEqual(AttrSets.Value, pass.Writes);
        Assert.AreEqual("HeadingToValue", KernelName(pass));
    }

    [Test]
    public void TeamToValue_Contract_DoesNotInitialize()
    {
        TeamToValuePass pass = new TeamToValuePass();

        Assert.AreEqual(PassCategory.Dynamics, pass.Category);
        Assert.AreEqual("Team To Value", pass.DisplayName);
        Assert.AreEqual(AttrSets.None, pass.Reads);
        Assert.AreEqual(AttrSets.Value, pass.Writes);
        Assert.AreEqual("TeamToValue", KernelName(pass));
    }

    [Test]
    public void TeamToValue_InitializeWithoutKernel_Throws()
    {
        TeamToValuePass pass = new TeamToValuePass();
        FieldSet fields = new FieldSet();
        try
        {
            SimContext context = new SimContext(null, fields, Array.Empty<ComputeShader>(), null);
            Assert.Throws<InvalidOperationException>(() => pass.Initialize(context));
        }
        finally
        {
            fields.Dispose();
        }
    }

    private static string KernelName(ParticleKernelPass pass)
    {
        PropertyInfo kernelName = typeof(ParticleKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        return (string)kernelName.GetValue(pass);
    }
}
