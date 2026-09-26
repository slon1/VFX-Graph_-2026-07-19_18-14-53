using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class PhysarumFluidPresetTests
{
    private const string FluidPath = "Assets/Effects/Physarum_Fluid.asset";
    private const string PhysarumPath = "Assets/Effects/Physarum.asset";

    [Test]
    public void Asset_MatchesAdr033Preset()
    {
        EffectAsset effect = AssetDatabase.LoadAssetAtPath<EffectAsset>(FluidPath);
        Assert.IsNotNull(effect, "missing Assets/Effects/Physarum_Fluid.asset — run Tools/M3D/Create Physarum Fluid Effect");

        SerializedObject so = new SerializedObject(effect);
        Assert.AreEqual((int)DataSourceKind.Cube, so.FindProperty("sourceKind").intValue);
        Assert.AreEqual(32, so.FindProperty("cubeSource.resolution").intValue);
        Assert.AreEqual(32f, so.FindProperty("cubeSource.cubeSize").floatValue);
        Assert.AreEqual(1f, effect.SimulationSpeed);
        Assert.AreEqual(0.2f, effect.ParticleSize);

        Assert.AreEqual(2, effect.Fields.Count);
        FieldDescriptor trail = effect.Fields[0];
        Assert.AreEqual("trail", trail.Name);
        Assert.AreEqual(FieldSemantic.Scalar, trail.Semantic);
        Assert.AreEqual(GraphicsFormat.R16_SFloat, trail.Format);
        Assert.AreEqual(new Vector2Int(128, 128), trail.Resolution);
        Assert.AreEqual(new Vector2(32f, 32f), trail.Size);

        FieldDescriptor velocity = effect.Fields[1];
        Assert.AreEqual("velocity", velocity.Name);
        Assert.AreEqual(FieldSemantic.Velocity, velocity.Semantic);
        Assert.AreEqual(GraphicsFormat.R16G16_SFloat, velocity.Format);
        Assert.AreEqual(new Vector2Int(128, 128), velocity.Resolution);
        Assert.AreEqual(new Vector2(32f, 32f), velocity.Size);

        System.Type[] expected =
        {
            typeof(TouchInjectVelocityFieldPass),
            typeof(DecayFieldPass),
            typeof(ClearFieldAccumPass),
            typeof(ScatterDensityToFieldPass),
            typeof(NormalizeDensityAccumPass),
            typeof(AdvectScalarPass),
            typeof(DecayFieldScalarPass),
            typeof(DiffuseFieldPass),
            typeof(DiffuseFieldPass),
            typeof(PhysarumSteerPass),
            typeof(IntegratePass),
            typeof(BoxBoundsPass),
            typeof(HeadingToValuePass),
        };
        Assert.AreEqual(expected.Length, effect.Passes.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i], effect.Passes[i].GetType(), "pass " + i);
        }

        FieldInfo channels = typeof(ClearFieldAccumPass).GetField(
            "channels", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(channels);
        Assert.AreEqual(1, (int)channels.GetValue(effect.Passes[2]));

        DecayFieldPass decay = (DecayFieldPass)effect.Passes[1];
        Assert.AreEqual("velocity", decay.FieldName);
        Assert.AreEqual(0.4f, decay.DecayRate);

        AdvectScalarPass advect = (AdvectScalarPass)effect.Passes[5];
        Assert.AreEqual("trail", advect.ScalarField);
        Assert.AreEqual("velocity", advect.VelocityField);
        Assert.AreEqual(0f, advect.DissipationRate);
        Assert.IsTrue(advect.WrapUv);
        Assert.IsFalse(advect.Reverse);

        for (int i = 0; i < effect.Passes.Count; i++)
        {
            System.Type type = effect.Passes[i].GetType();
            Assert.AreNotEqual(typeof(JacobiPhiPass), type);
            Assert.AreNotEqual(typeof(DivergenceFieldPass), type);
            Assert.AreNotEqual(typeof(SolidWallVelocityPass), type);
            Assert.AreNotEqual(typeof(AdvectVelocityFieldPass), type);
            Assert.AreNotEqual(typeof(ClearFieldPass), type);
            Assert.AreNotEqual(typeof(CopyRestPass), type);
        }
    }

    [Test]
    public void PhysarumAsset_StaysTrailOnlyWithoutAdvect()
    {
        EffectAsset effect = AssetDatabase.LoadAssetAtPath<EffectAsset>(PhysarumPath);
        Assert.IsNotNull(effect, "missing Assets/Effects/Physarum.asset");
        Assert.AreEqual(1, effect.Fields.Count);
        Assert.AreEqual("trail", effect.Fields[0].Name);
        for (int i = 0; i < effect.Passes.Count; i++)
        {
            Assert.AreNotEqual(typeof(AdvectScalarPass), effect.Passes[i].GetType());
        }
    }
}
