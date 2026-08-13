using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class SteerToVelocityFieldPassTests
{
    [Test]
    public void Contract_ForceCategory_SteerKernel_VelocityFieldRead()
    {
        SteerToVelocityFieldPass pass = new SteerToVelocityFieldPass();

        Assert.AreEqual(PassCategory.Force, pass.Category);
        Assert.AreEqual("Steer To Velocity Field", pass.DisplayName);
        Assert.AreEqual("flockVel", pass.VelocityFieldName);
        Assert.AreEqual(1f, pass.Strength);
        Assert.AreEqual(AttrSets.Position, pass.Reads);
        Assert.AreEqual(AttrSets.Velocity, pass.Writes);

        PropertyInfo kernelName = typeof(ParticleKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("SteerToVelocityField", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldReads.Count);
        FieldRequest read = pass.FieldReads[0];
        Assert.AreEqual("flockVel", read.FieldName);
        Assert.AreEqual(FieldAccess.Read, read.Access);
        Assert.AreEqual(FieldSemantic.Velocity, read.RequiredSemantic);
        Assert.AreEqual(2, read.Channels);
    }

    [Test]
    public void Strength_DefaultsToOne_AndIsAssignable()
    {
        SteerToVelocityFieldPass pass = new SteerToVelocityFieldPass();
        Assert.AreEqual(1f, pass.Strength);
        pass.Strength = 2.5f;
        Assert.AreEqual(2.5f, pass.Strength);
    }
}
