using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

[TestFixture]
public class SeedScalarDiskPassTests
{
    [Test]
    public void Contract_Emit_WriteInPlace_ScalarV()
    {
        SeedScalarDiskPass pass = new SeedScalarDiskPass();

        Assert.AreEqual("Seed Scalar Disk", pass.DisplayName);
        Assert.AreEqual(PassCategory.Emit, pass.Category);
        Assert.AreEqual("V", pass.FieldName);
        Assert.AreEqual(new Vector2(0.5f, 0.5f), pass.CenterUV);
        Assert.AreEqual(0.06f, pass.RadiusUV);
        Assert.AreEqual(1f, pass.Value);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("SeedScalarDisk", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldWrites.Count);
        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual("V", write.FieldName);
        Assert.AreEqual(FieldAccess.WriteInPlace, write.Access);
        Assert.AreEqual(FieldSemantic.Scalar, write.RequiredSemantic);
        Assert.AreEqual(1, write.Channels);
        Assert.AreEqual(FieldSlotRole.A, write.Role);
    }

    [Test]
    public void Initialize_ResetsHasFired_AcrossRebuild()
    {
        FieldDescriptor desc = FieldDescriptor.CreateDefault("V", FieldSemantic.Scalar);
        SetPrivate(desc, "resolution", new Vector2Int(8, 8));
        SetPrivate(desc, "format", GraphicsFormat.R32_SFloat);

        using (FieldSet fields = AllocateFields(desc))
        {
            ComputeShader shader = AssetDatabaseLoadGrayScott();
            Assume.That(shader != null, "GrayScottPasses.compute must be imported");
            Assume.That(shader.HasKernel("SeedScalarDisk"));

            SimContext context = new SimContext(null, fields, new[] { shader }, null);
            SeedScalarDiskPass pass = new SeedScalarDiskPass();

            pass.Initialize(context);
            Assert.IsFalse(GetHasFired(pass));

            SetHasFired(pass, true);
            Assert.IsTrue(GetHasFired(pass));

            pass.Initialize(context);
            Assert.IsFalse(GetHasFired(pass), "Rebuild/Initialize must clear hasFired for one-shot seed");
        }
    }

    [Test]
    public void ShouldDispatch_False_AfterHasFired()
    {
        SeedScalarDiskPass pass = new SeedScalarDiskPass();
        PropertyInfo shouldDispatch = typeof(FieldKernelPass).GetProperty(
            "ShouldDispatch", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(shouldDispatch);

        SetHasFired(pass, false);
        Assert.IsTrue((bool)shouldDispatch.GetValue(pass));

        SetHasFired(pass, true);
        Assert.IsFalse((bool)shouldDispatch.GetValue(pass));
    }

    private static ComputeShader AssetDatabaseLoadGrayScott()
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/Shaders/GPU/Passes/GrayScottPasses.compute");
#else
        return null;
#endif
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

    private static bool GetHasFired(SeedScalarDiskPass pass)
    {
        FieldInfo field = typeof(SeedScalarDiskPass).GetField(
            "hasFired", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (bool)field.GetValue(pass);
    }

    private static void SetHasFired(SeedScalarDiskPass pass, bool value)
    {
        FieldInfo field = typeof(SeedScalarDiskPass).GetField(
            "hasFired", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        field.SetValue(pass, value);
    }
}
