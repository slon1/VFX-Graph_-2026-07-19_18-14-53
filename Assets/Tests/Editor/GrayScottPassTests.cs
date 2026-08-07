using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class GrayScottPassTests
{
    [Test]
    public void Contract_Transport_PairWritePingPong_UV_RolesAB()
    {
        GrayScottPass pass = new GrayScottPass();

        Assert.AreEqual("Gray-Scott Reaction-Diffusion", pass.DisplayName);
        Assert.AreEqual(PassCategory.Transport, pass.Category);
        Assert.AreEqual("U", pass.FieldNameU);
        Assert.AreEqual("V", pass.FieldNameV);
        Assert.AreEqual(0.16f, pass.DiffusionRateU);
        Assert.AreEqual(0.08f, pass.DiffusionRateV);
        Assert.AreEqual(0.035f, pass.FeedRate);
        Assert.AreEqual(0.06f, pass.KillRate);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("GrayScottReact", (string)kernelName.GetValue(pass));

        Assert.AreEqual(0, pass.FieldReads.Count);
        Assert.AreEqual(2, pass.FieldWrites.Count);

        FieldRequest writeU = pass.FieldWrites[0];
        Assert.AreEqual("U", writeU.FieldName);
        Assert.AreEqual(FieldSlotRole.A, writeU.Role);
        Assert.AreEqual(FieldAccess.WritePingPong, writeU.Access);
        Assert.AreEqual(FieldSemantic.Scalar, writeU.RequiredSemantic);
        Assert.AreEqual(1, writeU.Channels);

        FieldRequest writeV = pass.FieldWrites[1];
        Assert.AreEqual("V", writeV.FieldName);
        Assert.AreEqual(FieldSlotRole.B, writeV.Role);
        Assert.AreEqual(FieldAccess.WritePingPong, writeV.Access);
        Assert.AreEqual(FieldSemantic.Scalar, writeV.RequiredSemantic);
        Assert.AreEqual(1, writeV.Channels);
    }
}
