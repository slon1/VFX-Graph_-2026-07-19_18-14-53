#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.VFX;
using UnityEditor.VFX.Block;
using UnityEngine;
using UnityEngine.VFX;

public static class CreateParticleBufferVFX
{
    private const string AssetPath = "Assets/Vfx/ParticleBufferVFX.vfx";
    private const uint Capacity = 1_000_000u;

    [MenuItem("Tools/PoC/Create Particle Buffer VFX")]
    public static void Create()
    {
        EnsureEmptyAsset(AssetPath);

        VisualEffectAsset vfx = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(AssetPath);
        VisualEffectResource resource = vfx.GetOrCreateResource();
        VFXGraph graph = resource.GetOrCreateGraph();
        graph.RemoveAllChildren();

        float k = 400f;

        VFXBasicSpawner spawner = ScriptableObject.CreateInstance<VFXBasicSpawner>();
        spawner.position = new Vector2(0f, 0f);

        VFXSpawnerBurst burst = ScriptableObject.CreateInstance<VFXSpawnerBurst>();
        burst.SetSettingValue("repeat", VFXSpawnerBurst.RepeatMode.Single);
        burst.SetSettingValue("spawnMode", VFXSpawnerBurst.RandomMode.Constant);
        spawner.AddChild(burst);

        VFXParameter spawnCount = ScriptableObject.CreateInstance<VFXParameter>();
        spawnCount.Init(typeof(float));
        spawnCount.SetSettingValue("m_ExposedName", "SpawnCount");
        spawnCount.SetSettingValue("m_Exposed", true);
        spawnCount.value = (float)Capacity;
        graph.AddChild(spawnCount);
        spawnCount.AddNode(new Vector2(-400f, 0f));
        spawnCount.GetOutputSlot(0).Link(burst.GetInputSlot(0));

        VFXBasicInitialize init = ScriptableObject.CreateInstance<VFXBasicInitialize>();
        init.SetSettingValue("capacity", Capacity);
        init.position = new Vector2(0f, k);

        SetAttribute setLifetime = ScriptableObject.CreateInstance<SetAttribute>();
        setLifetime.SetSettingValue("attribute", VFXAttribute.Lifetime.name);
        setLifetime.GetInputSlot(0).value = 1e9f;
        init.AddChild(setLifetime);

        SetAttribute setColor = ScriptableObject.CreateInstance<SetAttribute>();
        setColor.SetSettingValue("attribute", VFXAttribute.Color.name);
        setColor.GetInputSlot(0).value = Vector3.one;
        init.AddChild(setColor);

        SetAttribute setAlpha = ScriptableObject.CreateInstance<SetAttribute>();
        setAlpha.SetSettingValue("attribute", VFXAttribute.Alpha.name);
        setAlpha.GetInputSlot(0).value = 1f;
        init.AddChild(setAlpha);

        init.GetInputSlot(0).value = new AABox
        {
            center = Vector3.zero,
            size = new Vector3(4f, 4f, 4f)
        };

        VFXBasicUpdate update = ScriptableObject.CreateInstance<VFXBasicUpdate>();
        update.position = new Vector2(0f, k * 2f);
        update.SetSettingValue("integration", VFXBasicUpdate.VFXIntegrationMode.None);
        update.SetSettingValue("angularIntegration", VFXBasicUpdate.VFXIntegrationMode.None);
        update.SetSettingValue("ageParticles", false);
        update.SetSettingValue("reapParticles", false);

        CustomHLSL customHlsl = ScriptableObject.CreateInstance<CustomHLSL>();
        ShaderInclude positionHLSL = AssetDatabase.LoadAssetAtPath<ShaderInclude>(
            "Assets/Shaders/GPU/Vfx/ReadPositionBuffer.hlsl");
        if (positionHLSL == null)
        {
            Debug.LogError("Missing Assets/Shaders/GPU/Vfx/ReadPositionBuffer.hlsl (ShaderInclude).");
            return;
        }

        customHlsl.SetSettingValue("m_ShaderFile", positionHLSL);
        customHlsl.SetSettingValue("m_BlockName", "Read Position Buffer");
        update.AddChild(customHlsl);

        VFXPointOutput output = ScriptableObject.CreateInstance<VFXPointOutput>();
        output.position = new Vector2(0f, k * 3f);

        spawner.LinkTo(init);
        init.LinkTo(update);
        update.LinkTo(output);

        graph.AddChild(spawner);
        graph.AddChild(init);
        graph.AddChild(update);
        graph.AddChild(output);

        // Expose GraphicsBuffer by ensuring the CustomHLSL input becomes an exposed parameter.
        ExposeGraphicsBufferParameter(graph, customHlsl);

        graph.UpdateSubAssets();
        resource.WriteAsset();
        AssetDatabase.ImportAsset(AssetPath);
        AssetDatabase.SaveAssets();

        Debug.Log($"Created {AssetPath}. CustomHLSL input slots: {customHlsl.GetNbInputSlots()}");
    }

    private static void EnsureEmptyAsset(string path)
    {
        if (AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(path) != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        string emptyAsset =
            "%YAML 1.1\n" +
            "%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!114 &114350483966674976\n" +
            "MonoBehaviour:\n" +
            "  m_Script: {fileID: 11500000, guid: 7d4c867f6b72b714dbb5fd1780afe208, type: 3}\n" +
            "--- !u!2058629511 &1\n" +
            "VisualEffectResource:\n" +
            "  m_Graph: {fileID: 114350483966674976}\n";

        System.IO.File.WriteAllText(path, emptyAsset);
        AssetDatabase.ImportAsset(path);
    }

    private static void ExposeGraphicsBufferParameter(VFXGraph graph, CustomHLSL customHlsl)
    {
        customHlsl.Invalidate(VFXModel.InvalidationCause.kSettingChanged);

        if (customHlsl.GetNbInputSlots() == 0)
        {
            Debug.LogWarning("CustomHLSL has no input slots after setting HLSL code.");
            return;
        }

        VFXSlot bufferSlot = customHlsl.GetInputSlot(0);
        VFXParameter parameter = ScriptableObject.CreateInstance<VFXParameter>();
        parameter.Init(typeof(GraphicsBuffer));
        parameter.SetSettingValue("m_ExposedName", "PositionBuffer");
        parameter.SetSettingValue("m_Exposed", true);
        graph.AddChild(parameter);
        parameter.AddNode(new Vector2(-400f, 800f));

        if (parameter.GetNbOutputSlots() > 0)
        {
            parameter.GetOutputSlot(0).Link(bufferSlot);
        }
        else
        {
            Debug.LogWarning("Could not link PositionBuffer parameter to CustomHLSL. Link manually in VFX Graph.");
        }
    }
}
#endif
