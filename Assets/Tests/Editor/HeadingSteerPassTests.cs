using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class HeadingSteerPassTests
{
    [Test]
    public void Contract_DynamicsCategory_HeadingSteerKernel_HeadingVelocityRw()
    {
        HeadingSteerPass pass = new HeadingSteerPass();

        Assert.AreEqual(PassCategory.Dynamics, pass.Category);
        Assert.AreEqual("Heading Steer", pass.DisplayName);
        Assert.AreEqual(0.15f, pass.TurnSpeed);
        Assert.AreEqual(4f, pass.CruiseSpeed);
        Assert.AreEqual(AttrSets.HeadingVelocity, pass.Reads);
        Assert.AreEqual(AttrSets.HeadingVelocity, pass.Writes);

        PropertyInfo kernelName = typeof(ParticleKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("HeadingSteer", (string)kernelName.GetValue(pass));
    }

    [Test]
    public void TurnAndCruiseSpeed_AreAssignable()
    {
        HeadingSteerPass pass = new HeadingSteerPass();
        pass.TurnSpeed = 0.2f;
        pass.CruiseSpeed = 5f;
        Assert.AreEqual(0.2f, pass.TurnSpeed);
        Assert.AreEqual(5f, pass.CruiseSpeed);
    }
}
