using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class AgentErodeFieldPassTests
{
    [Test]
    public void Contract_Emit_ReadA_WriteInPlaceB_Defaults()
    {
        AgentErodeFieldPass pass = new AgentErodeFieldPass();

        Assert.AreEqual("Agent Erode Field", pass.DisplayName);
        Assert.AreEqual(PassCategory.Emit, pass.Category);
        Assert.AreEqual("agentPresence", pass.SourceFieldName);
        Assert.AreEqual("U", pass.TargetFieldName);
        Assert.AreEqual(0.3f, pass.Gain);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("AgentErodeField", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldReads.Count);
        Assert.AreEqual(1, pass.FieldWrites.Count);

        FieldRequest read = pass.FieldReads[0];
        Assert.AreEqual("agentPresence", read.FieldName);
        Assert.AreEqual(FieldSlotRole.A, read.Role);
        Assert.AreEqual(FieldAccess.Read, read.Access);
        Assert.AreEqual(FieldSemantic.Scalar, read.RequiredSemantic);
        Assert.AreEqual(1, read.Channels);

        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual("U", write.FieldName);
        Assert.AreEqual(FieldSlotRole.B, write.Role);
        Assert.AreEqual(FieldAccess.WriteInPlace, write.Access);
        Assert.AreEqual(FieldSemantic.Scalar, write.RequiredSemantic);
        Assert.AreEqual(1, write.Channels);
    }
}
