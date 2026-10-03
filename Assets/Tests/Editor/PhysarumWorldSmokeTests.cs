using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

/// <summary>
/// ADR-032: Rebuild Physarum.asset without a VisualEffect. Uses the 32³ preset, not a tiny copy.
/// </summary>
[TestFixture]
public class PhysarumWorldSmokeTests
{
    private const string AssetPath = "Assets/Effects/Physarum.asset";

    private static readonly string[] PassLibraryPaths =
    {
        "Assets/Shaders/GPU/Passes/P2GPasses.compute",
        "Assets/Shaders/GPU/Passes/DensityPasses.compute",
        "Assets/Shaders/GPU/Passes/DecayPasses.compute",
        "Assets/Shaders/GPU/Passes/DiffusePasses.compute",
        "Assets/Shaders/GPU/Passes/DynamicsPasses.compute",
        "Assets/Shaders/GPU/Passes/PhysarumPasses.compute",
    };

    private GameObject host;

    [SetUp]
    public void SetUp()
    {
        host = new GameObject("SimWorld_Physarum_Smoke");
        host.SetActive(false);
        host.AddComponent<SimulationWorld>();

        EffectAsset effect = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        Assert.IsNotNull(effect, "missing Assets/Effects/Physarum.asset — run Tools/M3D/Create Physarum Effect");

        SerializedObject soWorld = new SerializedObject(host.GetComponent<SimulationWorld>());
        soWorld.FindProperty("effect").objectReferenceValue = effect;
        soWorld.FindProperty("visualEffect").objectReferenceValue = null;
        SerializedProperty library = soWorld.FindProperty("passLibrary");
        library.arraySize = PassLibraryPaths.Length;
        for (int i = 0; i < PassLibraryPaths.Length; i++)
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(PassLibraryPaths[i]);
            Assert.IsNotNull(shader, PassLibraryPaths[i]);
            library.GetArrayElementAtIndex(i).objectReferenceValue = shader;
        }

        soWorld.ApplyModifiedPropertiesWithoutUndo();
    }

    [TearDown]
    public void TearDown()
    {
        if (host != null)
        {
            SimulationWorldTestCleanup.DestroyHost(host);
            host = null;
        }
    }

    [Test]
    public void Rebuild_WithoutVisualEffect_Succeeds()
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
        Assert.AreEqual(32768, particles.Count);
    }
}
