using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

/// <summary>
/// ADR-031: Build does not require a VisualEffect component.
/// In-memory cube, resolution 2 — not the 125k Boids asset.
/// </summary>
[TestFixture]
public class SimulationWorldWithoutVisualEffectTests
{
    private GameObject host;
    private EffectAsset effect;

    [SetUp]
    public void SetUp()
    {
        host = new GameObject("SimWorld_NoVisualEffect");
        host.SetActive(false);

        effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(
            DataSourceKind.Cube,
            speed: 1f,
            passList: new SimPass[] { new IntegratePass() },
            fieldList: null);

        SerializedObject soEffect = new SerializedObject(effect);
        soEffect.FindProperty("cubeSource.resolution").intValue = 2;
        soEffect.ApplyModifiedPropertiesWithoutUndo();

        host.AddComponent<SimulationWorld>();

        ComputeShader dynamics = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/Shaders/GPU/Passes/DynamicsPasses.compute");
        Assert.IsNotNull(dynamics);

        SerializedObject soWorld = new SerializedObject(host.GetComponent<SimulationWorld>());
        soWorld.FindProperty("effect").objectReferenceValue = effect;
        soWorld.FindProperty("visualEffect").objectReferenceValue = null;
        SerializedProperty library = soWorld.FindProperty("passLibrary");
        library.arraySize = 1;
        library.GetArrayElementAtIndex(0).objectReferenceValue = dynamics;
        soWorld.ApplyModifiedPropertiesWithoutUndo();
    }

    [TearDown]
    public void TearDown()
    {
        if (host != null)
        {
            Object.DestroyImmediate(host);
            host = null;
        }

        if (effect != null)
        {
            Object.DestroyImmediate(effect);
            effect = null;
        }
    }

    [Test]
    public void Build_WithoutVisualEffect_Succeeds()
    {
        SimulationWorld world = host.GetComponent<SimulationWorld>();
        Assert.IsNull(host.GetComponent<UnityEngine.VFX.VisualEffect>());
        Assert.DoesNotThrow(() => world.Rebuild());
        Assert.IsTrue(world.enabled, "Build failure disables SimulationWorld.");

        FieldInfo particlesField = typeof(SimulationWorld).GetField(
            "particles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(particlesField);
        ParticleSet particles = (ParticleSet)particlesField.GetValue(world);
        Assert.IsNotNull(particles);
        Assert.Greater(particles.Count, 0);
    }
}
