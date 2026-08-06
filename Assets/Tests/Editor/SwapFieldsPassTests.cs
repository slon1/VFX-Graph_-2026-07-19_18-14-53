using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class SwapFieldsPassTests
{
    [Test]
    public void Contract_Transport_PairWritePingPong_RolesAB()
    {
        SwapFieldsPass pass = new SwapFieldsPass();

        Assert.AreEqual("Swap Fields", pass.DisplayName);
        Assert.AreEqual(PassCategory.Transport, pass.Category);
        Assert.AreEqual("fieldA", pass.FieldNameA);
        Assert.AreEqual("fieldB", pass.FieldNameB);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("SwapFields", (string)kernelName.GetValue(pass));

        Assert.AreEqual(0, pass.FieldReads.Count);
        Assert.AreEqual(2, pass.FieldWrites.Count);

        FieldRequest writeA = pass.FieldWrites[0];
        Assert.AreEqual("fieldA", writeA.FieldName);
        Assert.AreEqual(FieldSlotRole.A, writeA.Role);
        Assert.AreEqual(FieldAccess.WritePingPong, writeA.Access);
        Assert.AreEqual(FieldSemantic.Scalar, writeA.RequiredSemantic);
        Assert.AreEqual(1, writeA.Channels);

        FieldRequest writeB = pass.FieldWrites[1];
        Assert.AreEqual("fieldB", writeB.FieldName);
        Assert.AreEqual(FieldSlotRole.B, writeB.Role);
        Assert.AreEqual(FieldAccess.WritePingPong, writeB.Access);
        Assert.AreEqual(FieldSemantic.Scalar, writeB.RequiredSemantic);
        Assert.AreEqual(1, writeB.Channels);
    }
}
