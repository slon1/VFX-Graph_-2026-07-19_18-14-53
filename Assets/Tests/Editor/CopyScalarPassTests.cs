using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class CopyScalarPassTests
{
    private const string Dye = "dye";
    private const string Scratch = "dyeMacScratch";
    private const string FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute";
    private const int Res = 64;
    private static readonly Vector2 Size = new Vector2(Res, Res);

    [Test]
    public void Contract_ScratchWriteInPlaceA_DyeReadB()
    {
        CopyScalarPass pass = new CopyScalarPass();

        Assert.AreEqual("Copy Scalar", pass.DisplayName);
        Assert.AreEqual(PassCategory.Transport, pass.Category);
        Assert.IsFalse(pass.RequiresSquareTexel);
        Assert.AreEqual(Scratch, pass.ScratchField);
        Assert.AreEqual(Dye, pass.ScalarField);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("CopyScalar", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldWrites.Count);
        FieldRequest write = pass.FieldWrites[0];
        Assert.AreEqual(Scratch, write.FieldName);
        Assert.AreEqual(FieldAccess.WriteInPlace, write.Access);
        Assert.AreEqual(FieldSemantic.Scalar, write.RequiredSemantic);
        Assert.AreEqual(1, write.Channels);
        Assert.AreEqual(FieldSlotRole.A, write.Role);

        Assert.AreEqual(1, pass.FieldReads.Count);
        FieldRequest read = pass.FieldReads[0];
        Assert.AreEqual(Dye, read.FieldName);
        Assert.AreEqual(FieldAccess.Read, read.Access);
        Assert.AreEqual(FieldSemantic.Scalar, read.RequiredSemantic);
        Assert.AreEqual(1, read.Channels);
        Assert.AreEqual(FieldSlotRole.B, read.Role);
    }

    [Test]
    [Category("GPU")]
    public void Copy_DyeToScratch_BitwiseAndDyeUnchanged()
    {
        float[] dyeSeed = UniqueScalar();
        float[] scratchSeed = FillScalar(999f);
        using (FieldTestHarness harness = CreateHarness())
        {
            harness.SeedScalar(Dye, dyeSeed);
            harness.SeedScalar(Scratch, scratchSeed);

            CopyScalarPass pass = new CopyScalarPass
            {
                ScalarField = Dye,
                ScratchField = Scratch,
            };
            pass.Initialize(harness.Context);
            harness.RunPass(pass, 1f);

            float[] dyeAfter = harness.ReadScalar(Dye);
            float[] scratchAfter = harness.ReadScalar(Scratch);
            AssertBitwiseEqual(dyeSeed, dyeAfter, "dye unchanged");
            AssertBitwiseEqual(dyeSeed, scratchAfter, "scratch equals dye");
        }
    }

    private static FieldTestHarness CreateHarness()
    {
        FieldDescriptor dye = FieldTestHarness.Descriptor(
            Dye, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            new Vector2Int(Res, Res), Size, Color.clear);
        FieldDescriptor scratch = FieldTestHarness.Descriptor(
            Scratch, FieldSemantic.Scalar, GraphicsFormat.R32_SFloat,
            new Vector2Int(Res, Res), Size, Color.clear);
        return new FieldTestHarness(new[] { dye, scratch }, FieldCompute);
    }

    private static float[] FillScalar(float value)
    {
        float[] field = new float[Res * Res];
        for (int i = 0; i < field.Length; i++)
        {
            field[i] = value;
        }

        return field;
    }

    private static float[] UniqueScalar()
    {
        float[] field = new float[Res * Res];
        for (int i = 0; i < field.Length; i++)
        {
            field[i] = i + 0.125f;
        }

        return field;
    }

    private static void AssertBitwiseEqual(float[] expected, float[] obtained, string label)
    {
        Assert.AreEqual(expected.Length, obtained.Length, $"{label}: length");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(
                BitConverter.SingleToInt32Bits(expected[i]),
                BitConverter.SingleToInt32Bits(obtained[i]),
                $"{label} [{i}] expected={expected[i]:G9} obtained={obtained[i]:G9}");
        }
    }
}
