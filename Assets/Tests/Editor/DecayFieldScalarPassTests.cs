using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class DecayFieldScalarPassTests
{
    [Test]
    public void Contract_Transport_WritePingPong_ScalarDensity()
    {
        DecayFieldScalarPass pass = new DecayFieldScalarPass();

        Assert.AreEqual("Decay Field (Scalar)", pass.DisplayName);
        Assert.AreEqual(PassCategory.Transport, pass.Category);
        Assert.AreEqual(1.5f, pass.DecayRate);
        Assert.AreEqual("density", pass.FieldName);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("DecayFieldScalar", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldWrites.Count);
        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual("density", write.FieldName);
        Assert.AreEqual(FieldAccess.WritePingPong, write.Access);
        Assert.AreEqual(FieldSemantic.Scalar, write.RequiredSemantic);
        Assert.AreEqual(1, write.Channels);
    }
}
