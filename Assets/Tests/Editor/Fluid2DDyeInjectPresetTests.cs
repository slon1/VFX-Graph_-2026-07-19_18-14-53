using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class Fluid2DDyeInjectPresetTests
{
    private const string AssetPath = "Assets/Effects/Fluid2D_DyeInject.asset";

    [Test]
    public void Preset_MatchesDyeInjectComposition()
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        Assert.IsNotNull(asset, "run Tools/M3D/Create Fluid2D DyeInject Experiment");

        Assert.AreEqual(4, asset.Fields.Count);
        Assert.AreEqual("velocity", asset.Fields[0].Name);
        Assert.AreEqual("fluidD", asset.Fields[1].Name);
        Assert.AreEqual("fluidPhi", asset.Fields[2].Name);
        Assert.AreEqual("dye", asset.Fields[3].Name);

        Assert.AreEqual(GraphicsFormat.R16G16_SFloat, asset.Fields[0].Format);
        Assert.AreEqual(GraphicsFormat.R32_SFloat, asset.Fields[1].Format);
        Assert.AreEqual(GraphicsFormat.R32_SFloat, asset.Fields[2].Format);
        Assert.AreEqual(GraphicsFormat.R16_SFloat, asset.Fields[3].Format);

        Vector2Int res128 = new Vector2Int(128, 128);
        Vector2Int res512 = new Vector2Int(512, 512);
        Vector2 size = new Vector2(32f, 32f);
        for (int i = 0; i < asset.Fields.Count; i++)
        {
            FieldDescriptor field = asset.Fields[i];
            Vector2Int expectedRes = field.Name == "dye" ? res512 : res128;
            Assert.AreEqual(expectedRes, field.Resolution, field.Name);
            Assert.AreEqual(size, field.Size, field.Name);
        }

        Assert.AreEqual(11, asset.Passes.Count);
        Assert.IsInstanceOf<TouchInjectVelocityFieldPass>(asset.Passes[0]);
        Assert.IsInstanceOf<SeedScalarDiskPass>(asset.Passes[1]);
        Assert.IsInstanceOf<TouchInjectScalarFieldPass>(asset.Passes[2]);
        Assert.IsInstanceOf<DivergenceFieldPass>(asset.Passes[3]);
        Assert.IsInstanceOf<ZeroMeanScalarPass>(asset.Passes[4]);
        Assert.IsInstanceOf<JacobiPhiPass>(asset.Passes[5]);
        Assert.IsInstanceOf<SubtractPhiGradientPass>(asset.Passes[6]);
        Assert.IsInstanceOf<SolidWallVelocityPass>(asset.Passes[7]);
        Assert.IsInstanceOf<AdvectVelocityFieldPass>(asset.Passes[8]);
        Assert.IsInstanceOf<SolidWallVelocityPass>(asset.Passes[9]);
        Assert.IsInstanceOf<AdvectScalarPass>(asset.Passes[10]);

        int wallCount = 0;
        int seedCount = 0;
        int injectCount = 0;
        for (int i = 0; i < asset.Passes.Count; i++)
        {
            SimPass pass = asset.Passes[i];
            if (pass is SolidWallVelocityPass)
            {
                wallCount++;
            }

            if (pass is SeedScalarDiskPass)
            {
                seedCount++;
            }

            if (pass is TouchInjectScalarFieldPass)
            {
                injectCount++;
            }

            Assert.IsNotInstanceOf<VorticityConfinementPass>(pass);
            Assert.IsNotInstanceOf<CopyScalarPass>(pass);
            Assert.IsNotInstanceOf<LimitedMacCormackCombinePass>(pass);
        }

        Assert.AreEqual(2, wallCount);
        Assert.AreEqual(1, seedCount);
        Assert.AreEqual(1, injectCount);

        SeedScalarDiskPass seed = (SeedScalarDiskPass)asset.Passes[1];
        Assert.AreEqual("dye", seed.FieldName);
        Assert.AreEqual(0.04f, seed.RadiusUV, 1e-6f);
        Assert.AreEqual(1f, seed.Value, 1e-6f);

        TouchInjectScalarFieldPass inject = (TouchInjectScalarFieldPass)asset.Passes[2];
        Assert.AreEqual("dye", inject.FieldName);
        Assert.AreEqual(60f, inject.InjectRate, 1e-6f);
        Assert.AreEqual(1f, inject.MaxValue, 1e-6f);
        Assert.AreEqual(1f, inject.RadiusScale, 1e-6f);
        Assert.IsInstanceOf<DivergenceFieldPass>(asset.Passes[3]);

        JacobiPhiPass jacobi = (JacobiPhiPass)asset.Passes[5];
        Assert.AreEqual(40, jacobi.Iterations);

        AdvectScalarPass advect = (AdvectScalarPass)asset.Passes[10];
        Assert.AreEqual(0f, advect.DissipationRate);
    }
}
