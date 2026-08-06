using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class FieldSlotNamingTests
{
    /// <summary>
    /// Nested stub — same pattern as FieldAccumPassValidatorTests (hidden from EffectAsset Add Pass).
    /// Two distinct names under default Role=A → per-role guard must throw.
    /// </summary>
    private sealed class MultiFieldSameRoleStubPass : FieldKernelPass
    {
        private readonly FieldRequest[] reads;
        private readonly FieldRequest[] writes;

        public MultiFieldSameRoleStubPass()
        {
            reads = new[]
            {
                new FieldRequest("velocity", FieldAccess.Read, FieldSemantic.Velocity, 2, FieldSlotRole.A),
            };
            writes = new[]
            {
                new FieldRequest("agentVelocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2, FieldSlotRole.A),
            };
        }

        public override string DisplayName => "MultiFieldSameRoleStub";
        public override PassCategory Category => PassCategory.Transport;
        protected override string KernelName => "UnusedKernel";
        public override IReadOnlyList<FieldRequest> FieldReads => reads;
        public override IReadOnlyList<FieldRequest> FieldWrites => writes;
    }

    private sealed class MultiFieldDualRoleStubPass : FieldKernelPass
    {
        private readonly FieldRequest[] writes;

        public MultiFieldDualRoleStubPass(string nameA = "fieldA", string nameB = "fieldB")
        {
            writes = new[]
            {
                new FieldRequest(nameA, FieldAccess.WritePingPong, FieldSemantic.Scalar, 1, FieldSlotRole.A),
                new FieldRequest(nameB, FieldAccess.WritePingPong, FieldSemantic.Scalar, 1, FieldSlotRole.B),
            };
        }

        public override string DisplayName => "MultiFieldDualRoleStub";
        public override PassCategory Category => PassCategory.Transport;
        protected override string KernelName => "UnusedKernel";
        public override IReadOnlyList<FieldRequest> FieldWrites => writes;
    }

    private sealed class MultiFieldRoleBOnlyStubPass : FieldKernelPass
    {
        private readonly FieldRequest[] writes;

        public MultiFieldRoleBOnlyStubPass()
        {
            // Single distinct name under Role=B only — triggers {B}-without-{A} hard error.
            writes = new[]
            {
                new FieldRequest("fieldB", FieldAccess.WritePingPong, FieldSemantic.Scalar, 1, FieldSlotRole.B),
            };
        }

        public override string DisplayName => "MultiFieldRoleBOnlyStub";
        public override PassCategory Category => PassCategory.Transport;
        protected override string KernelName => "UnusedKernel";
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
        Assert.AreEqual(Shader.PropertyToID("FieldReadA"), SimShaderIds.FieldReadA);
        Assert.AreEqual(Shader.PropertyToID("FieldWriteA"), SimShaderIds.FieldWriteA);
        Assert.AreEqual(Shader.PropertyToID("FieldReadB"), SimShaderIds.FieldReadB);
        Assert.AreEqual(Shader.PropertyToID("FieldWriteB"), SimShaderIds.FieldWriteB);
    }

    [Test]
    public void FieldKernelPass_TwoNamesSameRole_ThrowsBeforeFindKernel()
    {
        MultiFieldSameRoleStubPass pass = new MultiFieldSameRoleStubPass();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => pass.Initialize(null));
        StringAssert.Contains("FieldSlotRole.A", ex.Message);
        StringAssert.Contains("ADR-008", ex.Message);
    }

    [Test]
    public void FieldKernelPass_RoleBWithoutA_Throws()
    {
        MultiFieldRoleBOnlyStubPass pass = new MultiFieldRoleBOnlyStubPass();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => pass.Initialize(null));
        StringAssert.Contains("{A, B}", ex.Message);
    }

    [Test]
    public void FieldKernelPass_DualRole_NullContext_ThrowsForMissingFields()
    {
        MultiFieldDualRoleStubPass pass = new MultiFieldDualRoleStubPass();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => pass.Initialize(null));
        StringAssert.Contains("requires a SimContext with Fields", ex.Message);
    }

    [Test]
    public void FieldKernelPass_WritePingPong_SingleName_PassesUniqueGuard()
    {
        // WritePingPong is one request (unique FieldName == 1). Guard must pass;
        // FindKernel then fails on null context — proves we got past role check.
        SingleFieldPingPongStubPass pass = new SingleFieldPingPongStubPass();
        Assert.Throws<NullReferenceException>(() => pass.Initialize(null));
    }

    [Test]
    public void FieldKernelPass_DualRole_MismatchedResolution_Throws()
    {
        FieldDescriptor a = FieldDescriptor.CreateDefault("fieldA", FieldSemantic.Scalar);
        FieldDescriptor b = FieldDescriptor.CreateDefault("fieldB", FieldSemantic.Scalar);
        SetPrivate(a, "resolution", new Vector2Int(32, 32));
        SetPrivate(b, "resolution", new Vector2Int(64, 64));

        using (FieldSet fields = AllocateFields(a, b))
        {
            SimContext context = new SimContext(null, fields, Array.Empty<ComputeShader>(), null);
            MultiFieldDualRoleStubPass pass = new MultiFieldDualRoleStubPass();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => pass.Initialize(context));
            StringAssert.Contains("matching Resolution and plane", ex.Message);
        }
    }

    [Test]
    public void FieldKernelPass_DualRole_MatchingGeometry_ReachesFindKernel()
    {
        FieldDescriptor a = FieldDescriptor.CreateDefault("fieldA", FieldSemantic.Scalar);
        FieldDescriptor b = FieldDescriptor.CreateDefault("fieldB", FieldSemantic.Scalar);
        SetPrivate(a, "resolution", new Vector2Int(32, 32));
        SetPrivate(b, "resolution", new Vector2Int(32, 32));

        using (FieldSet fields = AllocateFields(a, b))
        {
            SimContext context = new SimContext(null, fields, Array.Empty<ComputeShader>(), null);
            MultiFieldDualRoleStubPass pass = new MultiFieldDualRoleStubPass();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => pass.Initialize(context));
            StringAssert.Contains("UnusedKernel", ex.Message);
        }
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

    [Test]
    public void FieldRequest_Equals_IncludesRole()
    {
        FieldRequest a = new FieldRequest("density", FieldAccess.WritePingPong, FieldSemantic.Scalar, 1, FieldSlotRole.A);
        FieldRequest b = new FieldRequest("density", FieldAccess.WritePingPong, FieldSemantic.Scalar, 1, FieldSlotRole.B);
        Assert.IsFalse(a.Equals(b));
    }

    private static FieldSet AllocateFields(params FieldDescriptor[] descriptors)
    {
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer();
        try
        {
            fields.Allocate(descriptors, cmd);
        }
        finally
        {
            cmd.Release();
        }

        return fields;
    }

    private static void SetPrivate(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, fieldName);
        field.SetValue(target, value);
    }
}
