using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class PhysarumPresetTests
{
    private const string AssetPath = "Assets/Effects/Physarum.asset";

    [Test]
    public void Asset_MatchesAdr032Preset()
    {
        EffectAsset effect = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        Assert.IsNotNull(effect, "missing Assets/Effects/Physarum.asset — run Tools/M3D/Create Physarum Effect");

        SerializedObject so = new SerializedObject(effect);
        Assert.AreEqual((int)DataSourceKind.Cube, so.FindProperty("sourceKind").intValue);
        Assert.AreEqual(32, so.FindProperty("cubeSource.resolution").intValue);
        Assert.AreEqual(32f, so.FindProperty("cubeSource.cubeSize").floatValue);
        Assert.AreEqual(1f, effect.SimulationSpeed);
        Assert.AreEqual(0.2f, effect.ParticleSize);

        Assert.AreEqual(1, effect.Fields.Count);
        FieldDescriptor trail = effect.Fields[0];
        Assert.AreEqual("trail", trail.Name);
        Assert.AreEqual(new Vector2Int(128, 128), trail.Resolution);
        Assert.AreEqual(new Vector2(32f, 32f), trail.Size);
        Assert.AreEqual(GraphicsFormat.R16_SFloat, trail.Format);

        Assert.AreEqual(10, effect.Passes.Count);
        System.Type[] expected =
        {
            typeof(ClearFieldAccumPass),
            typeof(ScatterDensityToFieldPass),
            typeof(NormalizeDensityAccumPass),
            typeof(DecayFieldScalarPass),
            typeof(DiffuseFieldPass),
            typeof(DiffuseFieldPass),
            typeof(PhysarumSteerPass),
            typeof(IntegratePass),
            typeof(BoxBoundsPass),
            typeof(HeadingToValuePass),
        };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i], effect.Passes[i].GetType(), "pass " + i);
            Assert.AreNotEqual(typeof(ClearFieldPass), effect.Passes[i].GetType());
            Assert.AreNotEqual(typeof(CopyRestPass), effect.Passes[i].GetType());
        }

        FieldInfo channels = typeof(ClearFieldAccumPass).GetField(
            "channels", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(channels);
        Assert.AreEqual(1, (int)channels.GetValue(effect.Passes[0]));

        BoxBoundsPass bounds = (BoxBoundsPass)effect.Passes[8];
        Assert.AreEqual(BoundsBehaviour.Wrap, bounds.Behaviour);
        Assert.AreEqual(new Vector3(16f, 0f, 16f), bounds.Extents);
    }
}
