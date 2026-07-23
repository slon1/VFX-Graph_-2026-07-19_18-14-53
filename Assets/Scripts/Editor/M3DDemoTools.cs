using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.VFX;

/// <summary>
/// Deterministic setup helpers: create the three demo EffectAssets and wire the
/// currently open scene (SimulationWorld + InputRouter on the VFX GameObject).
/// </summary>
public static class M3DDemoTools
{
    private const string EffectsFolder = "Assets/Effects";
    private const string TwistedCubePath = EffectsFolder + "/TwistedCube.asset";
    private const string GalaxySwirlPath = EffectsFolder + "/GalaxySwirl.asset";
    private const string ReactiveDustPath = EffectsFolder + "/ReactiveDust.asset";

    private static readonly string[] PassLibraryPaths =
    {
        "Assets/Shaders/GPU/Passes/ShapePasses.compute",
        "Assets/Shaders/GPU/Passes/ForcePasses.compute",
        "Assets/Shaders/GPU/Passes/DynamicsPasses.compute",
    };

    [MenuItem("Tools/M3D/Create Demo Effects")]
    public static void CreateDemoEffects()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        // Parity with the old scene: shape chain rebuilt from rest every frame.
        CreateEffect(TwistedCubePath, cubeResolution: 100, simulationSpeed: 1f,
            new CopyRestPass(),
            new TwistPass { Strength = 1f });

        // Dynamics chain: swirl + curl, finger smears the cloud, wrap bounds keep it alive.
        CreateEffect(GalaxySwirlPath, cubeResolution: 100, simulationSpeed: 1f,
            new VortexPass { Strength = 8f, Radius = 4f, Axis = Vector3.up },
            new CurlNoisePass { Frequency = 0.6f, Amplitude = 1.5f },
            new DragPass { Drag = 0.8f },
            new TouchForcePass { DragStrength = 3f, PushStrength = 0f },
            new IntegratePass(),
            new BoxBoundsPass { Extents = new Vector3(4f, 4f, 4f), Behaviour = BoundsBehaviour.Wrap });

        // Reactive: particles spring back to the shape, finger repels them.
        CreateEffect(ReactiveDustPath, cubeResolution: 100, simulationSpeed: 1f,
            new SpringToRestPass { Stiffness = 12f, Damping = 3f },
            new TurbulencePass { Amplitude = 0.6f, Frequency = 1.2f, Octaves = 3 },
            new TouchForcePass { DragStrength = 0f, PushStrength = 25f },
            new DragPass { Drag = 2f },
            new IntegratePass());

        AssetDatabase.SaveAssets();
        Debug.Log("M3D: created demo effects: TwistedCube, GalaxySwirl, ReactiveDust.");
    }

    [MenuItem("Tools/M3D/Setup Open Scene")]
    public static void SetupOpenScene()
    {
        VisualEffect vfx = Object.FindAnyObjectByType<VisualEffect>();
        if (vfx == null)
        {
            Debug.LogError("M3D: no VisualEffect found in the open scene.");
            return;
        }

        GameObject host = vfx.gameObject;
        int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(host);
        if (removed > 0)
        {
            Debug.Log($"M3D: removed {removed} missing script(s) from '{host.name}'.");
        }

        SimulationWorld world = host.GetComponent<SimulationWorld>();
        if (world == null)
        {
            world = host.AddComponent<SimulationWorld>();
        }

        InputRouter router = host.GetComponent<InputRouter>();
        if (router == null)
        {
            router = host.AddComponent<InputRouter>();
        }

        EffectAsset defaultEffect = AssetDatabase.LoadAssetAtPath<EffectAsset>(TwistedCubePath);
        if (defaultEffect == null)
        {
            Debug.LogError("M3D: demo effects not found — run Tools/M3D/Create Demo Effects first.");
            return;
        }

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = defaultEffect;
        worldSo.FindProperty("visualEffect").objectReferenceValue = vfx;
        worldSo.FindProperty("inputRouter").objectReferenceValue = router;

        SerializedProperty library = worldSo.FindProperty("passLibrary");
        library.arraySize = PassLibraryPaths.Length;
        for (int i = 0; i < PassLibraryPaths.Length; i++)
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(PassLibraryPaths[i]);
            if (shader == null)
            {
                Debug.LogError($"M3D: compute shader not found at '{PassLibraryPaths[i]}'.");
                return;
            }

            library.GetArrayElementAtIndex(i).objectReferenceValue = shader;
        }

        worldSo.ApplyModifiedPropertiesWithoutUndo();

        SerializedObject routerSo = new SerializedObject(router);
        routerSo.FindProperty("targetCamera").objectReferenceValue = Camera.main;
        routerSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(host.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log($"M3D: scene wired — '{host.name}' now runs '{defaultEffect.name}'.");
    }

    private static void CreateEffect(
        string path, int cubeResolution, float simulationSpeed, params SimPass[] passes)
    {
        EffectAsset existing = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        if (existing != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        // Configure the instance BEFORE CreateAsset so SerializeReference persists.
        EffectAsset asset = ScriptableObject.CreateInstance<EffectAsset>();
        asset.EditorConfigure(DataSourceKind.Cube, simulationSpeed, passes);

        SerializedObject so = new SerializedObject(asset);
        so.FindProperty("cubeSource.resolution").intValue = cubeResolution;
        so.ApplyModifiedPropertiesWithoutUndo();

        AssetDatabase.CreateAsset(asset, path);
        EditorUtility.SetDirty(asset);
    }
}
