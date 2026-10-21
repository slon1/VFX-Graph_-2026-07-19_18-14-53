using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class TouchInjectScalarPassTests
{
    [Test]
    public void Contract_Emit_SingleWriteInPlace_CachedDefaults()
    {
        TouchInjectScalarFieldPass pass = new TouchInjectScalarFieldPass();

        Assert.AreEqual("Touch Inject Scalar Field", pass.DisplayName);
        Assert.AreEqual(PassCategory.Emit, pass.Category);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("TouchInjectScalar", (string)kernelName.GetValue(pass));

        Assert.AreEqual(0, pass.FieldReads.Count);
        Assert.AreEqual(1, pass.FieldWrites.Count);

        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual("dye", write.FieldName);
        Assert.AreEqual(FieldAccess.WriteInPlace, write.Access);
        Assert.AreEqual(FieldSemantic.Scalar, write.RequiredSemantic);
        Assert.AreEqual(1, write.Channels);

        Assert.AreSame(pass.FieldWrites, pass.FieldWrites);

        Assert.AreEqual(60f, pass.InjectRate);
        Assert.AreEqual(1f, pass.MaxValue);
        Assert.AreEqual(1f, pass.RadiusScale);

        pass.FieldName = "ink";
        Assert.AreEqual("ink", pass.FieldWrites[0].FieldName);
    }
}
