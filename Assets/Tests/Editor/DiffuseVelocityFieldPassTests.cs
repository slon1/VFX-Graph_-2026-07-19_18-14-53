using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class DiffuseVelocityFieldPassTests
{
    [Test]
    public void Contract_Transport_WritePingPong_VelocityFlockVel()
    {
        DiffuseVelocityFieldPass pass = new DiffuseVelocityFieldPass();

        Assert.AreEqual("Diffuse Velocity Field", pass.DisplayName);
        Assert.AreEqual(PassCategory.Transport, pass.Category);
        Assert.AreEqual(0.15f, pass.DiffusionRate);
        Assert.AreEqual("flockVel", pass.FieldName);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("DiffuseVelocityField", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldWrites.Count);
        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual("flockVel", write.FieldName);
        Assert.AreEqual(FieldAccess.WritePingPong, write.Access);
        Assert.AreEqual(FieldSemantic.Velocity, write.RequiredSemantic);
        Assert.AreEqual(2, write.Channels);
    }
}
