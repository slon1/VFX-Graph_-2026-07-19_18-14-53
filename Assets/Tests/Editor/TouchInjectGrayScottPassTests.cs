using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class TouchInjectGrayScottPassTests
{
    [Test]
    public void Contract_Emit_PairWriteInPlace_UV_RolesAB()
    {
        TouchInjectGrayScottPass pass = new TouchInjectGrayScottPass();

        Assert.AreEqual("Touch Inject Gray-Scott", pass.DisplayName);
        Assert.AreEqual(PassCategory.Emit, pass.Category);
        Assert.AreEqual("U", pass.FieldNameU);
        Assert.AreEqual("V", pass.FieldNameV);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("TouchInjectGrayScott", (string)kernelName.GetValue(pass));

        Assert.AreEqual(0, pass.FieldReads.Count);
        Assert.AreEqual(2, pass.FieldWrites.Count);

        FieldRequest writeU = pass.FieldWrites[0];
        Assert.AreEqual("U", writeU.FieldName);
        Assert.AreEqual(FieldSlotRole.A, writeU.Role);
        Assert.AreEqual(FieldAccess.WriteInPlace, writeU.Access);
        Assert.AreEqual(FieldSemantic.Scalar, writeU.RequiredSemantic);
        Assert.AreEqual(1, writeU.Channels);

        FieldRequest writeV = pass.FieldWrites[1];
        Assert.AreEqual("V", writeV.FieldName);
        Assert.AreEqual(FieldSlotRole.B, writeV.Role);
        Assert.AreEqual(FieldAccess.WriteInPlace, writeV.Access);
        Assert.AreEqual(FieldSemantic.Scalar, writeV.RequiredSemantic);
        Assert.AreEqual(1, writeV.Channels);
    }
}
