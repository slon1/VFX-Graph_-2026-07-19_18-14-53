using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>One-shot ADR-030 wiring: Primitive render opt-in on Boids_mk1 and shader include.</summary>
public static class Adr030PrimitiveRenderSetup
{
    private const string AssetPath = "Assets/Effects/Boids_mk1.asset";
    private const string ShaderName = "M3D/ParticleBillboard";

    [MenuItem("Tools/M3D/Register ParticleBillboard Shader")]
    public static void RegisterParticleBillboardShader()
    {
        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogError($"M3D: shader '{ShaderName}' not found.");
            return;
        }

        SerializedObject so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
        SerializedProperty shaders = so.FindProperty("m_AlwaysIncludedShaders");
        if (shaders == null || !shaders.isArray)
        {
            Debug.LogError("M3D: GraphicsSettings.m_AlwaysIncludedShaders not found.");
            return;
        }

        for (int i = 0; i < shaders.arraySize; i++)
        {
            Shader existing = shaders.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
            if (existing == shader)
            {
                Debug.Log($"M3D: '{ShaderName}' already in Always Included Shaders.");
                return;
            }
        }

        int index = shaders.arraySize;
        shaders.arraySize = index + 1;
        shaders.GetArrayElementAtIndex(index).objectReferenceValue = shader;
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Debug.Log($"M3D: registered '{ShaderName}' in Always Included Shaders.");
    }

    [MenuItem("Tools/M3D/Enable Primitive Render On Boids_mk1")]
    public static void EnablePrimitiveRenderOnBoidsMk1()
    {
        SetBoidsMode(ParticleRenderMode.Primitive, "Primitive");
    }

    [MenuItem("Tools/M3D/Disable Primitive Render On Boids_mk1")]
    public static void DisablePrimitiveRenderOnBoidsMk1()
    {
        SetBoidsMode(ParticleRenderMode.Vfx, "Vfx");
    }

    private static void SetBoidsMode(ParticleRenderMode mode, string label)
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        if (asset == null)
        {
            Debug.LogError($"M3D: missing {AssetPath}");
            return;
        }

        SerializedObject so = new SerializedObject(asset);
        so.FindProperty("particleRenderMode").enumValueIndex = (int)mode;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        Debug.Log($"M3D: {AssetPath} particleRenderMode={label}.");
    }
}
