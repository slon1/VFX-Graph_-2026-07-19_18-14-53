using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class AddNormalizedVelocityFieldPassTests
{
    [Test]
    public void Contract_ForceCategory_AddNormalizedVelocityKernel_FlockVelRead()
    {
        AddNormalizedVelocityFieldPass pass = new AddNormalizedVelocityFieldPass();

        Assert.AreEqual(PassCategory.Force, pass.Category);
        Assert.AreEqual("Add Normalized Velocity Field", pass.DisplayName);
        Assert.AreEqual("flockVel", pass.VelocityFieldName);
        Assert.AreEqual(0.8f, pass.Weight);
        Assert.AreEqual(AttrSets.Position, pass.Reads);
        Assert.AreEqual(AttrSets.Velocity, pass.Writes);

        PropertyInfo kernelName = typeof(ParticleKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("AddNormalizedVelocityField", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldReads.Count);
        FieldRequest read = pass.FieldReads[0];
        Assert.AreEqual("flockVel", read.FieldName);
        Assert.AreEqual(FieldAccess.Read, read.Access);
        Assert.AreEqual(FieldSemantic.Velocity, read.RequiredSemantic);
        Assert.AreEqual(2, read.Channels);
    }

    [Test]
    public void Weight_DefaultsAndIsAssignable()
    {
        AddNormalizedVelocityFieldPass pass = new AddNormalizedVelocityFieldPass();
        Assert.AreEqual(0.8f, pass.Weight);
        pass.Weight = 1.2f;
        Assert.AreEqual(1.2f, pass.Weight);
    }
}
