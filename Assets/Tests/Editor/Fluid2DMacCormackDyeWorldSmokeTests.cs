using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;

[TestFixture]
public class Fluid2DMacCormackDyeWorldSmokeTests
{
    private const string AssetPath = "Assets/Effects/Fluid2D_MacCormackDye.asset";

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

    [SetUp]
    public void SetUp()
    {
        host = new GameObject("SimWorld_Fluid2D_MacCormackDye_Smoke");
        host.SetActive(false);

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        Assert.IsNotNull(asset, "run Tools/M3D/Create Fluid2D MacCormackDye Experiment");

        VisualEffect vfx = host.AddComponent<VisualEffect>();
        SimulationWorld world = host.AddComponent<SimulationWorld>();

        SerializedObject soWorld = new SerializedObject(world);
        soWorld.FindProperty("effect").objectReferenceValue = asset;
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
    }

    [Test]
    public void Rebuild_MacCormackDyeAsset_DoesNotThrow()
    {
        SimulationWorld world = host.GetComponent<SimulationWorld>();
        Assert.IsNotNull(world);
        Assert.DoesNotThrow(() => world.Rebuild());
        Assert.IsTrue(world.enabled, "Build failure disables SimulationWorld.");
    }
}
