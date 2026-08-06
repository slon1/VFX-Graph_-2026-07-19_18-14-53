using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.VFX;

/// <summary>
/// Deterministic setup helpers: create demo EffectAssets and wire the open scene.
/// </summary>
public static class M3DDemoTools
{
    private const string EffectsFolder = "Assets/Effects";
    private const string TwistedCubePath = EffectsFolder + "/TwistedCube.asset";
    private const string GalaxySwirlPath = EffectsFolder + "/GalaxySwirl.asset";
    private const string ReactiveDustPath = EffectsFolder + "/ReactiveDust.asset";
    private const string HybridTouchFieldPath = EffectsFolder + "/HybridTouchField.asset";
    private const string AgentFieldEchoPath = EffectsFolder + "/AgentFieldEcho.asset";

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
    };

    [MenuItem("Tools/M3D/Create Demo Effects")]
    public static void CreateDemoEffects()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        CreateEffect(TwistedCubePath, cubeResolution: 100, simulationSpeed: 1f,
            fields: null,
            debugQuads: null,
            new CopyRestPass(),
            new TwistPass { Strength = 1f });

        CreateEffect(GalaxySwirlPath, cubeResolution: 100, simulationSpeed: 1f,
            fields: null,
            debugQuads: null,
            new VortexPass { Strength = 8f, Radius = 4f, Axis = Vector3.up },
            new CurlNoisePass { Frequency = 0.6f, Amplitude = 1.5f },
            new DragPass { Drag = 0.8f },
            new TouchForcePass { DragStrength = 3f, PushStrength = 0f },
            new IntegratePass(),
            new BoxBoundsPass { Extents = new Vector3(4f, 4f, 4f), Behaviour = BoundsBehaviour.Wrap });

        CreateEffect(ReactiveDustPath, cubeResolution: 100, simulationSpeed: 1f,
            fields: null,
            debugQuads: null,
            new SpringToRestPass { Stiffness = 12f, Damping = 3f },
            new TurbulencePass { Amplitude = 0.6f, Frequency = 1.2f, Octaves = 3 },
            new TouchForcePass { DragStrength = 0f, PushStrength = 25f },
            new DragPass { Drag = 2f },
            new IntegratePass());

        // M2a hybrid DoD: Touch → velocity field → particles → render.
        FieldDescriptor velocityField = FieldDescriptor.CreateDefault("velocity", FieldSemantic.Velocity);
        CreateEffect(HybridTouchFieldPath, cubeResolution: 64, simulationSpeed: 1f,
            fields: new[] { velocityField },
            debugQuads: new[] { DebugFieldQuadSlot.Velocity() },
            new TouchInjectVelocityFieldPass(),
            new DecayFieldPass { DecayRate = 1.5f },
            new SampleVelocityFieldPass { Strength = 1f },
            new DragPass { Drag = 0.5f },
            new IntegratePass(),
            new BoxBoundsPass
            {
                Extents = new Vector3(5f, 0.5f, 5f),
                Behaviour = BoundsBehaviour.Bounce,
                Bounce = 0.2f,
            });

        // M2b.1 P2G round-trip: particles → agentVelocity field (accumulate-onto-decaying).
        // No ClearFieldPass: field remembers recent motion via Decay. Replace semantics =
        // ClearFieldPass → ClearFieldAccum → Scatter → Normalize (same passes, different order).
        FieldDescriptor agentVelocity = FieldDescriptor.CreateDefault("agentVelocity", FieldSemantic.Velocity);
        CreateEffect(AgentFieldEchoPath, cubeResolution: 64, simulationSpeed: 1f,
            fields: new[] { agentVelocity },
            debugQuads: new[] { DebugFieldQuadSlot.Velocity("agentVelocity") },
            new CopyRestPass(),
            new CurlNoisePass { Frequency = 0.5f, Amplitude = 1.2f },
            new DragPass { Drag = 0.8f },
            new SpeedLimitPass { MaxSpeed = 16f },
            new IntegratePass(),
            new BoxBoundsPass
            {
                Extents = new Vector3(5f, 0.5f, 5f),
                Behaviour = BoundsBehaviour.Wrap,
            },
            new ClearFieldAccumPass(),
            new ScatterVelocityToFieldPass(),
            new NormalizeVelocityAccumPass(),
            new DecayFieldPass { FieldName = "agentVelocity", DecayRate = 1.5f });

        AssetDatabase.SaveAssets();
        Debug.Log(
            "M3D: created demo effects: TwistedCube, GalaxySwirl, ReactiveDust, HybridTouchField, AgentFieldEcho.");
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
        routerSo.FindProperty("planeMode").enumValueIndex = (int)InteractionPlaneMode.GroundXZ;
        routerSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(host.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log($"M3D: scene wired — '{host.name}' now runs '{defaultEffect.name}'.");
    }

    [MenuItem("Tools/M3D/Assign HybridTouchField To Scene")]
    public static void AssignHybridToScene()
    {
        SimulationWorld world = Object.FindAnyObjectByType<SimulationWorld>();
        EffectAsset hybrid = AssetDatabase.LoadAssetAtPath<EffectAsset>(HybridTouchFieldPath);
        if (world == null || hybrid == null)
        {
            Debug.LogError("M3D: SimulationWorld or HybridTouchField.asset missing.");
            return;
        }

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = hybrid;
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        InputRouter router = world.GetComponent<InputRouter>();
        if (router != null)
        {
            SerializedObject routerSo = new SerializedObject(router);
            routerSo.FindProperty("planeMode").enumValueIndex = (int)InteractionPlaneMode.GroundXZ;
            routerSo.ApplyModifiedPropertiesWithoutUndo();
        }

        // Ensure FieldPasses.compute is in the library.
        SerializedProperty library = worldSo.FindProperty("passLibrary");
        EnsurePassLibrary(library);
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("M3D: scene assigned HybridTouchField (GroundXZ).");
    }

    [MenuItem("Tools/M3D/Assign AgentFieldEcho To Scene")]
    public static void AssignAgentFieldEchoToScene()
    {
        SimulationWorld world = Object.FindAnyObjectByType<SimulationWorld>();
        EffectAsset echo = AssetDatabase.LoadAssetAtPath<EffectAsset>(AgentFieldEchoPath);
        if (world == null || echo == null)
        {
            Debug.LogError("M3D: SimulationWorld or AgentFieldEcho.asset missing — run Create Demo Effects.");
            return;
        }

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = echo;
        SerializedProperty library = worldSo.FindProperty("passLibrary");
        EnsurePassLibrary(library);
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        InputRouter router = world.GetComponent<InputRouter>();
        if (router != null)
        {
            SerializedObject routerSo = new SerializedObject(router);
            routerSo.FindProperty("planeMode").enumValueIndex = (int)InteractionPlaneMode.GroundXZ;
            routerSo.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("M3D: scene assigned AgentFieldEcho (P2G accumulate-onto-decaying).");
    }

    private static void EnsurePassLibrary(SerializedProperty library)
    {
        library.arraySize = PassLibraryPaths.Length;
        for (int i = 0; i < PassLibraryPaths.Length; i++)
        {
            library.GetArrayElementAtIndex(i).objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<ComputeShader>(PassLibraryPaths[i]);
        }
    }

    private static void CreateEffect(
        string path,
        int cubeResolution,
        float simulationSpeed,
        FieldDescriptor[] fields,
        DebugFieldQuadSlot[] debugQuads,
        params SimPass[] passes)
    {
        EffectAsset existing = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        if (existing != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        EffectAsset asset = ScriptableObject.CreateInstance<EffectAsset>();
        asset.EditorConfigure(DataSourceKind.Cube, simulationSpeed, passes, fields, debugQuads);

        SerializedObject so = new SerializedObject(asset);
        so.FindProperty("cubeSource.resolution").intValue = cubeResolution;
        so.ApplyModifiedPropertiesWithoutUndo();

        AssetDatabase.CreateAsset(asset, path);
        EditorUtility.SetDirty(asset);
    }
}
