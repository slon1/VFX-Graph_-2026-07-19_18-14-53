using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class AgentBoostFieldPassTests
{
    [Test]
    public void Contract_Emit_ReadA_WriteInPlaceB_Defaults()
    {
        AgentBoostFieldPass pass = new AgentBoostFieldPass();

        Assert.AreEqual("Agent Boost Field", pass.DisplayName);
        Assert.AreEqual(PassCategory.Emit, pass.Category);
        Assert.AreEqual("agentPresence", pass.SourceFieldName);
        Assert.AreEqual("V", pass.TargetFieldName);
        Assert.AreEqual(0.3f, pass.Gain);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("AgentBoostField", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldReads.Count);
        Assert.AreEqual(1, pass.FieldWrites.Count);

        FieldRequest read = pass.FieldReads[0];
        Assert.AreEqual("agentPresence", read.FieldName);
        Assert.AreEqual(FieldSlotRole.A, read.Role);
        Assert.AreEqual(FieldAccess.Read, read.Access);
        Assert.AreEqual(FieldSemantic.Scalar, read.RequiredSemantic);
        Assert.AreEqual(1, read.Channels);

        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual("V", write.FieldName);
        Assert.AreEqual(FieldSlotRole.B, write.Role);
        Assert.AreEqual(FieldAccess.WriteInPlace, write.Access);
        Assert.AreEqual(FieldSemantic.Scalar, write.RequiredSemantic);
        Assert.AreEqual(1, write.Channels);
    }
}
