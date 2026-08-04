using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class ScatterDensityFieldPassTests
{
    [Test]
    public void ScatterDensity_Contract_PositionOnly_Channels1()
    {
        ScatterDensityToFieldPass pass = new ScatterDensityToFieldPass();

        Assert.AreEqual("Scatter Density To Field", pass.DisplayName);
        Assert.AreEqual(PassCategory.Emit, pass.Category);
        Assert.AreEqual(AttrSets.Position, pass.Reads);
        Assert.AreEqual(AttrSets.None, pass.Writes);

        PropertyInfo kernelName = typeof(ParticleToFieldScatterPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("ScatterDensity", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldAccumWrites.Count);
        FieldAccumRequest write = pass.FieldAccumWrites[0];
        Assert.AreEqual("density", write.FieldName);
        Assert.AreEqual(1, write.Channels);
        Assert.AreEqual(4096f, write.Scale);
        Assert.AreEqual(0f, write.Bias);
    }

    [Test]
    public void NormalizeDensity_Contract_Scalar_Channels1()
    {
        NormalizeDensityAccumPass pass = new NormalizeDensityAccumPass();

        Assert.AreEqual("Normalize Density Accum", pass.DisplayName);
        Assert.AreEqual(PassCategory.Emit, pass.Category);

        PropertyInfo kernelName = typeof(NormalizeFieldAccumPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("NormalizeDensityAccum", (string)kernelName.GetValue(pass));

        Assert.AreEqual(1, pass.FieldAccumReads.Count);
        FieldAccumRequest read = pass.FieldAccumReads[0];
        Assert.AreEqual("density", read.FieldName);
        Assert.AreEqual(1, read.Channels);

        Assert.AreEqual(1, pass.FieldWrites.Count);
        FieldRequest fieldWrite = pass.FieldWrites[0];
        Assert.AreEqual("density", fieldWrite.FieldName);
        Assert.AreEqual(FieldAccess.WriteInPlace, fieldWrite.Access);
        Assert.AreEqual(FieldSemantic.Scalar, fieldWrite.RequiredSemantic);
        Assert.AreEqual(1, fieldWrite.Channels);
    }
}
