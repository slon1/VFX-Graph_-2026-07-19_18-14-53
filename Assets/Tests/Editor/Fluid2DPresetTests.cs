using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class Fluid2DPresetTests
{
    private const string AssetPath = "Assets/Effects/Fluid2D.asset";

    [Test]
    public void Preset_MatchesFluid2DComposition()
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        Assert.IsNotNull(
            asset,
            "run Tools/M3D/Create Fluid2D Effect");

        Assert.IsInstanceOf<NoneSource>(asset.ResolveSource());
        Assert.AreEqual(4, asset.Fields.Count);
        Assert.AreEqual("velocity", asset.Fields[0].Name);
        Assert.AreEqual("fluidD", asset.Fields[1].Name);
        Assert.AreEqual("fluidPhi", asset.Fields[2].Name);
        Assert.AreEqual("dye", asset.Fields[3].Name);

        Assert.AreEqual(GraphicsFormat.R16G16_SFloat, asset.Fields[0].Format);
        Assert.AreEqual(GraphicsFormat.R32_SFloat, asset.Fields[1].Format);
        Assert.AreEqual(GraphicsFormat.R32_SFloat, asset.Fields[2].Format);
        Assert.AreEqual(GraphicsFormat.R16_SFloat, asset.Fields[3].Format);
        Assert.AreNotEqual(GraphicsFormat.R32_SFloat, asset.Fields[3].Format);
        Assert.AreNotEqual(GraphicsFormat.R16_SFloat, asset.Fields[1].Format);
        Assert.AreNotEqual(GraphicsFormat.R16_SFloat, asset.Fields[2].Format);

        Vector2Int res = new Vector2Int(128, 128);
        Vector2 size = new Vector2(32f, 32f);
        for (int i = 0; i < asset.Fields.Count; i++)
        {
            FieldDescriptor field = asset.Fields[i];
            Assert.AreEqual(res, field.Resolution, field.Name);
            Assert.AreEqual(size, field.Size, field.Name);
            Assert.AreEqual(Vector3.zero, field.Origin, field.Name);
            Assert.AreEqual(Vector3.right, field.AxisU, field.Name);
            Assert.AreEqual(Vector3.forward, field.AxisV, field.Name);
        }

        Assert.AreEqual(10, asset.Passes.Count);
        Assert.IsInstanceOf<TouchInjectVelocityFieldPass>(asset.Passes[0]);
        Assert.IsInstanceOf<SeedScalarDiskPass>(asset.Passes[1]);
        Assert.IsInstanceOf<DivergenceFieldPass>(asset.Passes[2]);
        Assert.IsInstanceOf<ZeroMeanScalarPass>(asset.Passes[3]);
        Assert.IsInstanceOf<JacobiPhiPass>(asset.Passes[4]);
        Assert.IsInstanceOf<SubtractPhiGradientPass>(asset.Passes[5]);
        Assert.IsInstanceOf<SolidWallVelocityPass>(asset.Passes[6]);
        Assert.IsInstanceOf<AdvectVelocityFieldPass>(asset.Passes[7]);
        Assert.IsInstanceOf<SolidWallVelocityPass>(asset.Passes[8]);
        Assert.IsInstanceOf<AdvectScalarPass>(asset.Passes[9]);

        int wallCount = 0;
        for (int i = 0; i < asset.Passes.Count; i++)
        {
            if (asset.Passes[i] is SolidWallVelocityPass)
            {
                wallCount++;
            }
        }

        Assert.AreEqual(2, wallCount);

        SeedScalarDiskPass seed = (SeedScalarDiskPass)asset.Passes[1];
        Assert.AreEqual("dye", seed.FieldName);

        AdvectVelocityFieldPass advectVelocity = (AdvectVelocityFieldPass)asset.Passes[7];
        Assert.AreEqual("velocity", advectVelocity.FieldName);

        AdvectScalarPass advectScalar = (AdvectScalarPass)asset.Passes[9];
        Assert.AreEqual("dye", advectScalar.ScalarField);
        Assert.AreEqual("velocity", advectScalar.VelocityField);

        Assert.AreEqual(2, asset.DebugFieldQuads.Count);
        Assert.AreEqual("velocity", asset.DebugFieldQuads[0].fieldName);
        Assert.AreEqual(0.125f, asset.DebugFieldQuads[0].colorScale, 1e-4f);
        Assert.AreEqual("dye", asset.DebugFieldQuads[1].fieldName);

        JacobiPhiPass jacobi = (JacobiPhiPass)asset.Passes[4];
        Assert.That(jacobi.RepeatCount, Is.InRange(40, 80));
    }
}
