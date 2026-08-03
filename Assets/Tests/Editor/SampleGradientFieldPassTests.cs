using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class SampleGradientFieldPassTests
{
    [Test]
    public void Contract_ForceCategory_SampleGradientKernel_ScalarDensityRead()
    {
        SampleGradientFieldPass pass = new SampleGradientFieldPass();

        Assert.AreEqual(PassCategory.Force, pass.Category);
        Assert.AreEqual("Sample Gradient Field", pass.DisplayName);
        Assert.AreEqual(AttrSets.Position, pass.Reads);
        Assert.AreEqual(AttrSets.Velocity, pass.Writes);

        PropertyInfo kernelName = typeof(ParticleKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("SampleGradient", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldReads.Count);
        FieldRequest read = pass.FieldReads[0];
        Assert.AreEqual("density", read.FieldName);
        Assert.AreEqual(FieldAccess.Read, read.Access);
        Assert.AreEqual(FieldSemantic.Scalar, read.RequiredSemantic);
        Assert.AreEqual(1, read.Channels);
    }

    [Test]
    public void Strength_DefaultsToOne_AndIsAssignable()
    {
        SampleGradientFieldPass pass = new SampleGradientFieldPass();
        Assert.AreEqual(1f, pass.Strength);
        pass.Strength = -2.5f;
        Assert.AreEqual(-2.5f, pass.Strength);
    }
}
