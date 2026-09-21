using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;

[TestFixture]
public class BoidsMk1PrimitiveWorldSmokeTests
{
    private const string AssetPath = "Assets/Effects/Boids_mk1.asset";

    private static readonly string[] PassLibraryPaths =
    {
        "Assets/Shaders/GPU/Passes/ShapePasses.compute",
        "Assets/Shaders/GPU/Passes/ForcePasses.compute",
        "Assets/Shaders/GPU/Passes/DynamicsPasses.compute",
        "Assets/Shaders/GPU/Passes/FieldPasses.compute",
        "Assets/Shaders/GPU/Passes/P2GPasses.compute",
        "Assets/Shaders/GPU/Passes/GradientPasses.compute",
        "Assets/Shaders/GPU/Passes/DensityPasses.compute",
        "Assets/Shaders/GPU/Passes/DiffusePasses.compute",
        "Assets/Shaders/GPU/Passes/DecayPasses.compute",
        "Assets/Shaders/GPU/Passes/MultiFieldTestPasses.compute",
        "Assets/Shaders/GPU/Passes/GrayScottPasses.compute",
        "Assets/Shaders/GPU/Passes/FluidPasses.compute",
        "Assets/Shaders/GPU/Passes/TouchGrayScottPasses.compute",
        "Assets/Shaders/GPU/Passes/AgentFieldFeedbackPasses.compute",
    };

    private GameObject host;
    private EffectAsset copy;

    [SetUp]
    public void SetUp()
    {
        host = new GameObject("SimWorld_BoidsMk1_Primitive_Smoke");
        host.SetActive(false);

        EffectAsset source = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        Assert.IsNotNull(source, "missing Assets/Effects/Boids_mk1.asset");
        copy = Object.Instantiate(source);

        SerializedObject soAsset = new SerializedObject(copy);
        soAsset.FindProperty("particleRenderMode").enumValueIndex = (int)ParticleRenderMode.Primitive;
        soAsset.ApplyModifiedPropertiesWithoutUndo();
        Assert.AreEqual(ParticleRenderMode.Primitive, copy.ParticleRenderMode);
        Assert.AreEqual(ParticleRenderMode.Vfx, source.ParticleRenderMode);

        VisualEffect vfx = host.AddComponent<VisualEffect>();
        SimulationWorld world = host.AddComponent<SimulationWorld>();

        SerializedObject soWorld = new SerializedObject(world);
        soWorld.FindProperty("effect").objectReferenceValue = copy;
        soWorld.FindProperty("visualEffect").objectReferenceValue = vfx;
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
            Object.DestroyImmediate(host);
            host = null;
        }

        if (copy != null)
        {
            Object.DestroyImmediate(copy);
            copy = null;
        }
    }

    [Test]
    public void RebuildAndOneUpdate_PrimitiveCopy_DoesNotThrow()
    {
        SimulationWorld world = host.GetComponent<SimulationWorld>();
        Assert.IsNotNull(world);
        Assert.DoesNotThrow(() => world.Rebuild());
        Assert.IsTrue(world.enabled, "Build failure disables SimulationWorld.");

        MethodInfo update = typeof(SimulationWorld).GetMethod(
            "Update", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(update);
        Assert.DoesNotThrow(() => update.Invoke(world, null));
    }
}
