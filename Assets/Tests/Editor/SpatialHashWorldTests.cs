using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

[TestFixture]
public class SpatialHashWorldTests
{
    private GameObject host;
    private EffectAsset effect;

    [TearDown]
    public void TearDown()
    {
        if (host != null)
        {
            SimulationWorldTestCleanup.DestroyHost(host);
            host = null;
        }

        if (effect != null)
        {
            Object.DestroyImmediate(effect);
            effect = null;
        }
    }

    [Test]
    public void Build_WithHash_SetsContextSpatialHash()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SimulationWorld world = CreateWorld(includeBounds: true);
        Assert.DoesNotThrow(() => world.Rebuild());
        Assert.IsTrue(world.enabled);

        FieldInfo contextField = typeof(SimulationWorld).GetField(
            "context", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(contextField);
        SimContext context = (SimContext)contextField.GetValue(world);
        Assert.IsNotNull(context.SpatialHash);
        Assert.AreEqual(64, context.SpatialHash.ParticleCount);
    }

    [Test]
    public void Build_WrapWithoutBounds_DisablesWorld()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SimulationWorld world = CreateWorld(includeBounds: false);
        LogAssert.Expect(LogType.Error, new Regex("phantom|BoxBounds"));
        world.Rebuild();
        Assert.IsFalse(world.enabled);
    }

    [Test]
    public void RenderParticles_DefaultTrue()
    {
        host = new GameObject("HashProbe_Default");
        host.SetActive(false);
        SimulationWorld world = host.AddComponent<SimulationWorld>();
        Assert.IsTrue(world.RenderParticles);
    }

    private SimulationWorld CreateWorld(bool includeBounds)
    {
        host = new GameObject("HashProbe_World");
        host.SetActive(false);

        BuildSpatialHashPass hash = new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = new Vector3(2f, 0f, 2f),
            MinCellSize = 1f,
            Wrap = true,
        };
        SimPass[] passes = includeBounds
            ? new SimPass[]
            {
                hash,
                new IntegratePass(),
                new BoxBoundsPass
                {
                    Center = Vector3.zero,
                    Extents = new Vector3(2f, 0f, 2f),
                    Behaviour = BoundsBehaviour.Wrap,
                },
            }
            : new SimPass[] { hash, new IntegratePass() };

        effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(DataSourceKind.Cube, 1f, passes, null);

        SerializedObject effectSo = new SerializedObject(effect);
        effectSo.FindProperty("cubeSource.resolution").intValue = 4;
        effectSo.FindProperty("cubeSource.cubeSize").floatValue = 4f;
        effectSo.ApplyModifiedPropertiesWithoutUndo();

        SimulationWorld world = host.AddComponent<SimulationWorld>();
        ComputeShader dynamics = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/Shaders/GPU/Passes/DynamicsPasses.compute");
        ComputeShader hashShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute");
        Assert.IsNotNull(dynamics);
        Assert.IsNotNull(hashShader);

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = effect;
        worldSo.FindProperty("visualEffect").objectReferenceValue = null;
        SerializedProperty library = worldSo.FindProperty("passLibrary");
        library.arraySize = 2;
        library.GetArrayElementAtIndex(0).objectReferenceValue = dynamics;
        library.GetArrayElementAtIndex(1).objectReferenceValue = hashShader;
        worldSo.ApplyModifiedPropertiesWithoutUndo();
        return world;
    }
}
