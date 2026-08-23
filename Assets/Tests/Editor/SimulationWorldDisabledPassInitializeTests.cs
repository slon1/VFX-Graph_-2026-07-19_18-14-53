using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.VFX;

/// <summary>
/// Regression: Build must skip Initialize for Enabled=false passes
/// (same guard as Update / accum allocation), so disabling a whole P2G block
/// does not throw when accum was never allocated.
/// </summary>
[TestFixture]
public class SimulationWorldDisabledPassInitializeTests
{
    private GameObject host;
    private EffectAsset effect;
    private ClearFieldAccumPass clearPass;
    private ScatterVelocityToFieldPass scatterPass;
    private NormalizeVelocityAccumPass normalizePass;

    [SetUp]
    public void SetUp()
    {
        host = new GameObject("SimWorld_DisabledPass_Test");
        host.SetActive(false);

        FieldDescriptor agentVelocity =
            FieldDescriptor.CreateDefault("agentVelocity", FieldSemantic.Velocity);

        clearPass = new ClearFieldAccumPass();
        scatterPass = new ScatterVelocityToFieldPass();
        normalizePass = new NormalizeVelocityAccumPass();

        effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(
            DataSourceKind.Cube,
            speed: 1f,
            passList: new SimPass[] { clearPass, scatterPass, normalizePass },
            fieldList: new[] { agentVelocity });

        SerializedObject soEffect = new SerializedObject(effect);
        soEffect.FindProperty("cubeSource.resolution").intValue = 4;
        soEffect.ApplyModifiedPropertiesWithoutUndo();

        VisualEffect vfx = host.AddComponent<VisualEffect>();
        SimulationWorld world = host.AddComponent<SimulationWorld>();

        ComputeShader p2g = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/Shaders/GPU/Passes/P2GPasses.compute");
        Assert.IsNotNull(p2g, "P2GPasses.compute must exist for Build.");

        SerializedObject soWorld = new SerializedObject(world);
        soWorld.FindProperty("effect").objectReferenceValue = effect;
        soWorld.FindProperty("visualEffect").objectReferenceValue = vfx;
        SerializedProperty library = soWorld.FindProperty("passLibrary");
        library.arraySize = 1;
        library.GetArrayElementAtIndex(0).objectReferenceValue = p2g;
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
    public void Build_AllP2GPassesDisabled_DoesNotFail()
    {
        clearPass.Enabled = false;
        scatterPass.Enabled = false;
        normalizePass.Enabled = false;

        AssertBuildSucceeds();
    }

    [Test]
    public void Build_AllP2GPassesEnabled_Succeeds()
    {
        clearPass.Enabled = true;
        scatterPass.Enabled = true;
        normalizePass.Enabled = true;

        AssertBuildSucceeds();
    }

    [Test]
    public void Build_NormalizeDisabled_ClearAndScatterEnabled_Succeeds()
    {
        clearPass.Enabled = true;
        scatterPass.Enabled = true;
        normalizePass.Enabled = false;

        AssertBuildSucceeds();
    }

    [Test]
    public void Build_OnlyClearEnabled_Succeeds()
    {
        clearPass.Enabled = true;
        scatterPass.Enabled = false;
        normalizePass.Enabled = false;

        AssertBuildSucceeds();
    }

    private void AssertBuildSucceeds()
    {
        SimulationWorld world = host.GetComponent<SimulationWorld>();
        Assert.IsNotNull(world);

        LogAssert.Expect(
            LogType.Warning,
            new Regex("PositionBuffer"));
        Assert.DoesNotThrow(() => world.Rebuild());
        Assert.IsTrue(world.enabled, "Build failure disables SimulationWorld.");
    }
}
