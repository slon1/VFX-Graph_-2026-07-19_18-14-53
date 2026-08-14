using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class ClearVelocityPassTests
{
    [Test]
    public void Contract_DynamicsCategory_ClearVelocityKernel_WritesVelocityOnly()
    {
        ClearVelocityPass pass = new ClearVelocityPass();

        Assert.AreEqual(PassCategory.Dynamics, pass.Category);
        Assert.AreEqual("Clear Velocity", pass.DisplayName);
        Assert.AreEqual(AttrSets.None, pass.Reads);
        Assert.AreEqual(AttrSets.Velocity, pass.Writes);

        PropertyInfo kernelName = typeof(ParticleKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("ClearVelocity", (string)kernelName.GetValue(pass));
    }
}
