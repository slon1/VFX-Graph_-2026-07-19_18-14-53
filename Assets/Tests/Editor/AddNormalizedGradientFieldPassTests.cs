using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class AddNormalizedGradientFieldPassTests
{
    [Test]
    public void Contract_ForceCategory_AddNormalizedGradientKernel_ScalarRead()
    {
        AddNormalizedGradientFieldPass pass = new AddNormalizedGradientFieldPass();

        Assert.AreEqual(PassCategory.Force, pass.Category);
        Assert.AreEqual("Add Normalized Gradient Field", pass.DisplayName);
        Assert.AreEqual("density", pass.FieldName);
        Assert.AreEqual(0.6f, pass.Weight);
        Assert.AreEqual(AttrSets.Position, pass.Reads);
        Assert.AreEqual(AttrSets.Velocity, pass.Writes);

        PropertyInfo kernelName = typeof(ParticleKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("AddNormalizedGradient", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldReads.Count);
        FieldRequest read = pass.FieldReads[0];
        Assert.AreEqual("density", read.FieldName);
        Assert.AreEqual(FieldAccess.Read, read.Access);
        Assert.AreEqual(FieldSemantic.Scalar, read.RequiredSemantic);
        Assert.AreEqual(1, read.Channels);
    }

    [Test]
    public void Weight_SignedAssignable()
    {
        AddNormalizedGradientFieldPass pass = new AddNormalizedGradientFieldPass();
        pass.Weight = -1.2f;
        Assert.AreEqual(-1.2f, pass.Weight);
    }
}
