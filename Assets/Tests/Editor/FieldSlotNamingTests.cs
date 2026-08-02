using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class FieldSlotNamingTests
{
    /// <summary>
    /// Nested stub — same pattern as FieldAccumPassValidatorTests (hidden from EffectAsset Add Pass).
    /// </summary>
    private sealed class MultiFieldStubPass : FieldKernelPass
    {
        private readonly FieldRequest[] reads;
        private readonly FieldRequest[] writes;

        public MultiFieldStubPass()
        {
            reads = new[]
            {
                new FieldRequest("velocity", FieldAccess.Read, FieldSemantic.Velocity, 2),
            };
            writes = new[]
            {
                new FieldRequest("agentVelocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2),
            };
        }

        public override string DisplayName => "MultiFieldStub";
        public override PassCategory Category => PassCategory.Transport;
        protected override string KernelName => "UnusedKernel";
        public override IReadOnlyList<FieldRequest> FieldReads => reads;
        public override IReadOnlyList<FieldRequest> FieldWrites => writes;
    }

    private sealed class SingleFieldPingPongStubPass : FieldKernelPass
    {
        private readonly FieldRequest[] writes;

        public SingleFieldPingPongStubPass()
        {
            writes = new[]
            {
                new FieldRequest("agentVelocity", FieldAccess.WritePingPong, FieldSemantic.Velocity, 2),
            };
        }

        public override string DisplayName => "SingleFieldPingPongStub";
        public override PassCategory Category => PassCategory.Transport;
        protected override string KernelName => "UnusedKernel";
        public override IReadOnlyList<FieldRequest> FieldWrites => writes;
    }

    [Test]
    public void SimShaderIds_FieldSlots_MatchFixedPropertyNames()
    {
        Assert.AreEqual(Shader.PropertyToID("FieldRead"), SimShaderIds.FieldRead);
        Assert.AreEqual(Shader.PropertyToID("FieldWrite"), SimShaderIds.FieldWrite);
    }

    [Test]
    public void FieldKernelPass_MultiDistinctFieldNames_ThrowsBeforeFindKernel()
    {
        MultiFieldStubPass pass = new MultiFieldStubPass();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => pass.Initialize(null));
        StringAssert.Contains("exactly one distinct field name", ex.Message);
        StringAssert.Contains("M2c", ex.Message);
    }

    [Test]
    public void FieldKernelPass_WritePingPong_SingleName_PassesUniqueGuard()
    {
        // WritePingPong is one request (unique FieldName == 1). Guard must pass;
        // FindKernel then fails on null context — proves we got past unique-name check.
        SingleFieldPingPongStubPass pass = new SingleFieldPingPongStubPass();
        Assert.Throws<NullReferenceException>(() => pass.Initialize(null));
    }

    [Test]
    public void DecayFieldPass_KernelName_IsAlwaysDecayField()
    {
        DecayFieldPass pass = new DecayFieldPass();
        pass.FieldName = "agentVelocity";

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("DecayField", (string)kernelName.GetValue(pass));
    }
}
