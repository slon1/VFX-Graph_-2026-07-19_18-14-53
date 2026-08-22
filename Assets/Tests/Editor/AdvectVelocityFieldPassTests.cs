using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class AdvectVelocityFieldPassTests
{
    [Test]
    public void Contract_Transport_WritePingPong_VelocityFlockVel()
    {
        AdvectVelocityFieldPass pass = new AdvectVelocityFieldPass();

        Assert.AreEqual("Advect Velocity Field", pass.DisplayName);
        Assert.AreEqual(PassCategory.Transport, pass.Category);
        Assert.AreEqual(0f, pass.Dissipation);
        Assert.AreEqual("flockVel", pass.FieldName);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("AdvectVelocityField", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldWrites.Count);
        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual("flockVel", write.FieldName);
        Assert.AreEqual(FieldAccess.WritePingPong, write.Access);
        Assert.AreEqual(FieldSemantic.Velocity, write.RequiredSemantic);
        Assert.AreEqual(2, write.Channels);
    }
}
