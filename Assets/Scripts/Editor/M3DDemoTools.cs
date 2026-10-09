using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
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
    private const string GrayScottBoidsPath = EffectsFolder + "/Gray-Scott-Boids.asset";
    private const string Fluid2DPath = EffectsFolder + "/Fluid2D.asset";
    private const string Fluid2DHarrisOrderPath = EffectsFolder + "/Fluid2D_HarrisOrder.asset";
    private const string Fluid2DVorticityPath = EffectsFolder + "/Fluid2D_Vorticity.asset";
    private const string Fluid2DMacCormackDyePath = EffectsFolder + "/Fluid2D_MacCormackDye.asset";
    private const string Fluid2DHighResDyePath = EffectsFolder + "/Fluid2D_HighResDye.asset";
    private const string PhysarumPath = EffectsFolder + "/Physarum.asset";
    private const string PhysarumFluidPath = EffectsFolder + "/Physarum_Fluid.asset";
    private const string HashProbe30kPath = EffectsFolder + "/HashProbe_30k.asset";
    private const string HashProbe100kPath = EffectsFolder + "/HashProbe_100k.asset";
    private const string HashProbe30kScene = "Assets/Scenes/HashProbe_30k.unity";
    private const string HashProbe100kScene = "Assets/Scenes/HashProbe_100k.unity";
    private const string BoidsHashPath = EffectsFolder + "/Boids_hash.asset";
    private const string BoidsHashScene = "Assets/Scenes/Boids_Hash.unity";
    private const string BoidsHashTwoTeamsPath = EffectsFolder + "/Boids_hash_2teams.asset";
    private const string BoidsHashTwoTeamsScene = "Assets/Scenes/Boids_Hash_2Teams.unity";

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
        "Assets/Shaders/GPU/Passes/PhysarumPasses.compute",
        "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute",
        "Assets/Shaders/GPU/Passes/HashDebugPasses.compute",
        "Assets/Shaders/GPU/Passes/BoidsPasses.compute",
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

    [MenuItem("Tools/M3D/Create Gray-Scott-Boids Effect")]
    public static void CreateGrayScottBoidsEffect()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        Vector2 planeSize = new Vector2(50f, 50f);
        Vector2Int res128 = new Vector2Int(128, 128);
        Vector2Int res32 = new Vector2Int(32, 32);

        FieldDescriptor flockVel = FieldDescriptor.CreateDefault("flockVel", FieldSemantic.Velocity);
        FieldDescriptor cohesionDensity = FieldDescriptor.CreateDefault("cohesionDensity", FieldSemantic.Scalar);
        FieldDescriptor separationDensity = FieldDescriptor.CreateDefault("separationDensity", FieldSemantic.Scalar);
        FieldDescriptor agentPresence = FieldDescriptor.CreateDefault("agentPresence", FieldSemantic.Scalar);
        FieldDescriptor fieldU = FieldDescriptor.CreateDefault("U", FieldSemantic.Scalar);
        FieldDescriptor fieldV = FieldDescriptor.CreateDefault("V", FieldSemantic.Scalar);

        ClearFieldPass clearPresence = new ClearFieldPass();
        ClearFieldAccumPass clearPresenceAccum = new ClearFieldAccumPass();
        ScatterDensityToFieldPass scatterPresence = new ScatterDensityToFieldPass();
        NormalizeDensityAccumPass normalizePresence = new NormalizeDensityAccumPass();
        SetPrivate(clearPresence, "fieldName", "agentPresence");
        SetPrivate(clearPresence, "requiredSemantic", FieldSemantic.Scalar);
        SetPrivate(clearPresence, "channels", 1);
        SetPrivate(clearPresenceAccum, "fieldName", "agentPresence");
        SetPrivate(clearPresenceAccum, "channels", 1);
        SetPrivate(scatterPresence, "targetFieldName", "agentPresence");
        SetPrivate(normalizePresence, "fieldName", "agentPresence");

        ClearFieldAccumPass clearFlockAccum = new ClearFieldAccumPass();
        ScatterVelocityToFieldPass scatterFlock = new ScatterVelocityToFieldPass();
        NormalizeVelocityAccumPass normalizeFlock = new NormalizeVelocityAccumPass();
        SetPrivate(clearFlockAccum, "fieldName", "flockVel");
        SetPrivate(clearFlockAccum, "channels", 2);
        SetPrivate(scatterFlock, "targetFieldName", "flockVel");
        SetPrivate(normalizeFlock, "fieldName", "flockVel");

        ClearFieldAccumPass clearCohesionAccum = new ClearFieldAccumPass();
        ScatterDensityToFieldPass scatterCohesion = new ScatterDensityToFieldPass();
        NormalizeDensityAccumPass normalizeCohesion = new NormalizeDensityAccumPass();
        SetPrivate(clearCohesionAccum, "fieldName", "cohesionDensity");
        SetPrivate(clearCohesionAccum, "channels", 1);
        SetPrivate(scatterCohesion, "targetFieldName", "cohesionDensity");
        SetPrivate(normalizeCohesion, "fieldName", "cohesionDensity");

        ClearFieldAccumPass clearSepAccum = new ClearFieldAccumPass();
        ScatterDensityToFieldPass scatterSep = new ScatterDensityToFieldPass();
        NormalizeDensityAccumPass normalizeSep = new NormalizeDensityAccumPass();
        SetPrivate(clearSepAccum, "fieldName", "separationDensity");
        SetPrivate(clearSepAccum, "channels", 1);
        SetPrivate(scatterSep, "targetFieldName", "separationDensity");
        SetPrivate(normalizeSep, "fieldName", "separationDensity");

        SteerToVelocityFieldPass sampleFlock = new SteerToVelocityFieldPass { Strength = 1f };
        SampleGradientFieldPass sampleCohesion = new SampleGradientFieldPass { Strength = 1f };
        SampleGradientFieldPass sampleSeparation = new SampleGradientFieldPass { Strength = -0.9f };
        sampleFlock.VelocityFieldName = "flockVel";
        SetPrivate(sampleCohesion, "fieldName", "cohesionDensity");
        SetPrivate(sampleSeparation, "fieldName", "separationDensity");

        CreateEffect(
            GrayScottBoidsPath,
            cubeResolution: 25,
            simulationSpeed: 50f,
            fields: new[]
            {
                flockVel, cohesionDensity, separationDensity, agentPresence, fieldU, fieldV,
            },
            debugQuads: new[]
            {
                DebugFieldQuadSlot.Density("U"),
                DebugFieldQuadSlot.Density("V"),
                DebugFieldQuadSlot.Density("agentPresence"),
            },
            new CurlNoisePass { Frequency = 0.8f, Amplitude = 0.04f, Speed = 0.3f },
            new DragPass { Drag = 1.75f },
            new SpeedLimitPass { MaxSpeed = 4f },
            new IntegratePass(),
            new BoxBoundsPass
            {
                Extents = new Vector3(50f, 50f, 50f),
                Behaviour = BoundsBehaviour.Bounce,
                Bounce = 0.6f,
            },
            clearFlockAccum,
            scatterFlock,
            normalizeFlock,
            new DecayFieldPass { FieldName = "flockVel", DecayRate = 2f },
            new DiffuseVelocityFieldPass { FieldName = "flockVel", DiffusionRate = 0.15f },
            new DiffuseVelocityFieldPass { FieldName = "flockVel", DiffusionRate = 0.15f },
            new DiffuseVelocityFieldPass { FieldName = "flockVel", DiffusionRate = 0.15f },
            new DiffuseVelocityFieldPass { FieldName = "flockVel", DiffusionRate = 0.15f },
            new DiffuseVelocityFieldPass { FieldName = "flockVel", DiffusionRate = 0.15f },
            new DiffuseVelocityFieldPass { FieldName = "flockVel", DiffusionRate = 0.15f },
            clearCohesionAccum,
            scatterCohesion,
            normalizeCohesion,
            new DecayFieldScalarPass { FieldName = "cohesionDensity", DecayRate = 2f },
            new DiffuseFieldPass { FieldName = "cohesionDensity", DiffusionRate = 0.18f },
            new DiffuseFieldPass { FieldName = "cohesionDensity", DiffusionRate = 0.18f },
            new DiffuseFieldPass { FieldName = "cohesionDensity", DiffusionRate = 0.18f },
            new DiffuseFieldPass { FieldName = "cohesionDensity", DiffusionRate = 0.18f },
            new DiffuseFieldPass { FieldName = "cohesionDensity", DiffusionRate = 0.18f },
            new DiffuseFieldPass { FieldName = "cohesionDensity", DiffusionRate = 0.18f },
            clearSepAccum,
            scatterSep,
            normalizeSep,
            new DecayFieldScalarPass { FieldName = "separationDensity", DecayRate = 2f },
            sampleFlock,
            sampleCohesion,
            sampleSeparation,
            clearPresence,
            clearPresenceAccum,
            scatterPresence,
            normalizePresence,
            new SeedScalarDiskPass
            {
                FieldName = "V",
                CenterUV = new Vector2(0.5f, 0.5f),
                RadiusUV = 0.06f,
                Value = 1f,
            },
            new GrayScottPass(),
            new GrayScottPass(),
            new AgentBoostFieldPass(),
            new AgentErodeFieldPass(),
            new TouchInjectGrayScottPass());

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(GrayScottBoidsPath);
        SerializedObject so = new SerializedObject(asset);
        SerializedProperty fieldsProp = so.FindProperty("fields");
        for (int i = 0; i < fieldsProp.arraySize; i++)
        {
            SerializedProperty f = fieldsProp.GetArrayElementAtIndex(i);
            string name = f.FindPropertyRelative("id.name").stringValue;
            SerializedProperty res = f.FindPropertyRelative("resolution");
            SerializedProperty size = f.FindPropertyRelative("size");
            SerializedProperty clear = f.FindPropertyRelative("clearValue");
            size.vector2Value = planeSize;
            f.FindPropertyRelative("origin").vector3Value = Vector3.zero;
            f.FindPropertyRelative("axisU").vector3Value = Vector3.right;
            f.FindPropertyRelative("axisV").vector3Value = Vector3.forward;

            if (name == "cohesionDensity")
            {
                res.vector2IntValue = res32;
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16_SFloat;
            }
            else if (name == "flockVel")
            {
                res.vector2IntValue = new Vector2Int(64, 64);
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16G16_SFloat;
            }
            else
            {
                res.vector2IntValue = res128;
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16_SFloat;
            }

            if (name == "U")
            {
                clear.colorValue = Color.white;
            }
            else
            {
                clear.colorValue = Color.clear;
            }
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        Debug.Log("M3D: created Gray-Scott-Boids (boids + agentPresence → GS feedback).");
    }

    /// <summary>
    /// One-way agents → Gray-Scott: particles move (curl/drag) and paint U/V via presence.
    /// No flock fields, no SampleVelocity/Gradient (field does not steer particles).
    /// </summary>
    [MenuItem("Tools/M3D/Create Gray-Scott-Agents Effect")]
    public static void CreateGrayScottAgentsEffect()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        const string path = EffectsFolder + "/Gray-Scott-Agents.asset";
        Vector2 planeSize = new Vector2(50f, 50f);
        Vector2Int res128 = new Vector2Int(128, 128);

        FieldDescriptor agentPresence = FieldDescriptor.CreateDefault("agentPresence", FieldSemantic.Scalar);
        FieldDescriptor fieldU = FieldDescriptor.CreateDefault("U", FieldSemantic.Scalar);
        FieldDescriptor fieldV = FieldDescriptor.CreateDefault("V", FieldSemantic.Scalar);

        ClearFieldPass clearPresence = new ClearFieldPass();
        ClearFieldAccumPass clearPresenceAccum = new ClearFieldAccumPass();
        ScatterDensityToFieldPass scatterPresence = new ScatterDensityToFieldPass();
        NormalizeDensityAccumPass normalizePresence = new NormalizeDensityAccumPass();
        SetPrivate(clearPresence, "fieldName", "agentPresence");
        SetPrivate(clearPresence, "requiredSemantic", FieldSemantic.Scalar);
        SetPrivate(clearPresence, "channels", 1);
        SetPrivate(clearPresenceAccum, "fieldName", "agentPresence");
        SetPrivate(clearPresenceAccum, "channels", 1);
        SetPrivate(scatterPresence, "targetFieldName", "agentPresence");
        SetPrivate(normalizePresence, "fieldName", "agentPresence");

        CreateEffect(
            path,
            cubeResolution: 25,
            simulationSpeed: 20f,
            fields: new[] { agentPresence, fieldU, fieldV },
            debugQuads: new[]
            {
                DebugFieldQuadSlot.Density("U"),
                DebugFieldQuadSlot.Density("V"),
                DebugFieldQuadSlot.Density("agentPresence"),
            },
            new CurlNoisePass { Frequency = 0.8f, Amplitude = 0.04f, Speed = 0.3f },
            new DragPass { Drag = 1.75f },
            new SpeedLimitPass { MaxSpeed = 4f },
            new IntegratePass(),
            new BoxBoundsPass
            {
                Extents = new Vector3(50f, 50f, 50f),
                Behaviour = BoundsBehaviour.Bounce,
                Bounce = 0.6f,
            },
            clearPresence,
            clearPresenceAccum,
            scatterPresence,
            normalizePresence,
            new SeedScalarDiskPass
            {
                FieldName = "V",
                CenterUV = new Vector2(0.5f, 0.5f),
                RadiusUV = 0.06f,
                Value = 1f,
            },
            new GrayScottPass(),
            new GrayScottPass(),
            new AgentBoostFieldPass(),
            new AgentErodeFieldPass(),
            new TouchInjectGrayScottPass());

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        SerializedObject so = new SerializedObject(asset);
        SerializedProperty fieldsProp = so.FindProperty("fields");
        for (int i = 0; i < fieldsProp.arraySize; i++)
        {
            SerializedProperty f = fieldsProp.GetArrayElementAtIndex(i);
            string name = f.FindPropertyRelative("id.name").stringValue;
            f.FindPropertyRelative("resolution").vector2IntValue = res128;
            f.FindPropertyRelative("size").vector2Value = planeSize;
            f.FindPropertyRelative("origin").vector3Value = Vector3.zero;
            f.FindPropertyRelative("axisU").vector3Value = Vector3.right;
            f.FindPropertyRelative("axisV").vector3Value = Vector3.forward;
            f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16_SFloat;
            f.FindPropertyRelative("clearValue").colorValue =
                name == "U" ? Color.white : Color.clear;
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        Debug.Log("M3D: created Gray-Scott-Agents (one-way agents → GS, no field→particle feedback).");
    }

    private static void SetPrivate(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new System.InvalidOperationException(
                $"M3D: field '{fieldName}' not found on {target.GetType().Name}.");
        }

        field.SetValue(target, value);
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

    [MenuItem("Tools/M3D/Create Fluid2D Effect")]
    public static void CreateFluid2DEffect()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        Vector2Int res128 = new Vector2Int(128, 128);
        Vector2 planeSize = new Vector2(32f, 32f);

        FieldDescriptor velocity = FieldDescriptor.CreateDefault("velocity", FieldSemantic.Velocity);
        FieldDescriptor fluidD = FieldDescriptor.CreateDefault("fluidD", FieldSemantic.Scalar);
        FieldDescriptor fluidPhi = FieldDescriptor.CreateDefault("fluidPhi", FieldSemantic.Scalar);
        FieldDescriptor dye = FieldDescriptor.CreateDefault("dye", FieldSemantic.Scalar);

        DebugFieldQuadSlot velocityQuad = DebugFieldQuadSlot.Velocity();
        velocityQuad.colorScale = 0.125f;

        CreateEffect(
            Fluid2DPath,
            cubeResolution: 1,
            simulationSpeed: 1f,
            kind: DataSourceKind.None,
            fields: new[] { velocity, fluidD, fluidPhi, dye },
            debugQuads: new[] { velocityQuad, DebugFieldQuadSlot.Density("dye") },
            new TouchInjectVelocityFieldPass(),
            new SeedScalarDiskPass
            {
                FieldName = "dye",
                CenterUV = new Vector2(0.5f, 0.5f),
                RadiusUV = 0.08f,
                Value = 1f,
            },
            new DivergenceFieldPass(),
            new ZeroMeanScalarPass(),
            new JacobiPhiPass { Iterations = 40 },
            new SubtractPhiGradientPass(),
            new SolidWallVelocityPass(),
            new AdvectVelocityFieldPass
            {
                FieldName = "velocity",
                DissipationRate = 0f,
            },
            new SolidWallVelocityPass(),
            new AdvectScalarPass
            {
                ScalarField = "dye",
                VelocityField = "velocity",
                DissipationRate = 0f,
            });

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(Fluid2DPath);
        SerializedObject so = new SerializedObject(asset);
        SerializedProperty fieldsProp = so.FindProperty("fields");
        for (int i = 0; i < fieldsProp.arraySize; i++)
        {
            SerializedProperty f = fieldsProp.GetArrayElementAtIndex(i);
            string name = f.FindPropertyRelative("id.name").stringValue;
            f.FindPropertyRelative("resolution").vector2IntValue = res128;
            f.FindPropertyRelative("size").vector2Value = planeSize;
            f.FindPropertyRelative("origin").vector3Value = Vector3.zero;
            f.FindPropertyRelative("axisU").vector3Value = Vector3.right;
            f.FindPropertyRelative("axisV").vector3Value = Vector3.forward;
            f.FindPropertyRelative("clearValue").colorValue = Color.clear;
            if (name == "velocity")
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16G16_SFloat;
            }
            else if (name == "dye")
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16_SFloat;
            }
            else
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R32_SFloat;
            }
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();

        System.Text.StringBuilder log = new System.Text.StringBuilder();
        log.Append("M3D: created Fluid2D. Passes:");
        for (int i = 0; i < asset.Passes.Count; i++)
        {
            log.Append(' ');
            log.Append(asset.Passes[i].GetType().Name);
            if (i < asset.Passes.Count - 1)
            {
                log.Append(',');
            }
        }

        log.Append(" Formats:");
        for (int i = 0; i < asset.Fields.Count; i++)
        {
            log.Append(' ');
            log.Append(asset.Fields[i].Name);
            log.Append('=');
            log.Append(asset.Fields[i].Format);
        }

        Debug.Log(log.ToString());
    }

    [MenuItem("Tools/M3D/Assign Fluid2D To Scene")]
    public static void AssignFluid2DToScene()
    {
        SimulationWorld world = Object.FindAnyObjectByType<SimulationWorld>();
        EffectAsset fluid = AssetDatabase.LoadAssetAtPath<EffectAsset>(Fluid2DPath);
        if (world == null || fluid == null)
        {
            Debug.LogError("M3D: SimulationWorld or Fluid2D.asset missing — run Tools/M3D/Create Fluid2D Effect.");
            return;
        }

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = fluid;
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        InputRouter router = world.GetComponent<InputRouter>();
        if (router != null)
        {
            SerializedObject routerSo = new SerializedObject(router);
            routerSo.FindProperty("planeMode").enumValueIndex = (int)InteractionPlaneMode.GroundXZ;
            routerSo.ApplyModifiedPropertiesWithoutUndo();
        }

        SerializedProperty library = worldSo.FindProperty("passLibrary");
        EnsurePassLibrary(library);
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("M3D: scene assigned Fluid2D (GroundXZ). visualEffect left in place.");
    }

    [MenuItem("Tools/M3D/Create Fluid2D HarrisOrder Experiment")]
    public static void CreateFluid2DHarrisOrderExperiment()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        Vector2Int res128 = new Vector2Int(128, 128);
        Vector2 planeSize = new Vector2(32f, 32f);

        FieldDescriptor velocity = FieldDescriptor.CreateDefault("velocity", FieldSemantic.Velocity);
        FieldDescriptor fluidD = FieldDescriptor.CreateDefault("fluidD", FieldSemantic.Scalar);
        FieldDescriptor fluidPhi = FieldDescriptor.CreateDefault("fluidPhi", FieldSemantic.Scalar);
        FieldDescriptor dye = FieldDescriptor.CreateDefault("dye", FieldSemantic.Scalar);

        DebugFieldQuadSlot velocityQuad = DebugFieldQuadSlot.Velocity();
        velocityQuad.colorScale = 0.125f;

        CreateEffect(
            Fluid2DHarrisOrderPath,
            cubeResolution: 1,
            simulationSpeed: 1f,
            kind: DataSourceKind.None,
            fields: new[] { velocity, fluidD, fluidPhi, dye },
            debugQuads: new[] { velocityQuad, DebugFieldQuadSlot.Density("dye") },
            new TouchInjectVelocityFieldPass(),
            new SeedScalarDiskPass
            {
                FieldName = "dye",
                CenterUV = new Vector2(0.5f, 0.5f),
                RadiusUV = 0.08f,
                Value = 1f,
            },
            new AdvectVelocityFieldPass
            {
                FieldName = "velocity",
                DissipationRate = 0f,
            },
            new DivergenceFieldPass(),
            new ZeroMeanScalarPass(),
            new JacobiPhiPass { Iterations = 40 },
            new SubtractPhiGradientPass(),
            new SolidWallVelocityPass(),
            new AdvectScalarPass
            {
                ScalarField = "dye",
                VelocityField = "velocity",
                DissipationRate = 0f,
            });

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(Fluid2DHarrisOrderPath);
        SerializedObject so = new SerializedObject(asset);
        SerializedProperty fieldsProp = so.FindProperty("fields");
        for (int i = 0; i < fieldsProp.arraySize; i++)
        {
            SerializedProperty f = fieldsProp.GetArrayElementAtIndex(i);
            string name = f.FindPropertyRelative("id.name").stringValue;
            f.FindPropertyRelative("resolution").vector2IntValue = res128;
            f.FindPropertyRelative("size").vector2Value = planeSize;
            f.FindPropertyRelative("origin").vector3Value = Vector3.zero;
            f.FindPropertyRelative("axisU").vector3Value = Vector3.right;
            f.FindPropertyRelative("axisV").vector3Value = Vector3.forward;
            f.FindPropertyRelative("clearValue").colorValue = Color.clear;
            if (name == "velocity")
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16G16_SFloat;
            }
            else if (name == "dye")
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16_SFloat;
            }
            else
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R32_SFloat;
            }
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();

        System.Text.StringBuilder log = new System.Text.StringBuilder();
        log.Append("M3D: created Fluid2D HarrisOrder. Passes:");
        for (int i = 0; i < asset.Passes.Count; i++)
        {
            log.Append(' ');
            log.Append(asset.Passes[i].GetType().Name);
            if (i < asset.Passes.Count - 1)
            {
                log.Append(',');
            }
        }

        log.Append(" Formats:");
        for (int i = 0; i < asset.Fields.Count; i++)
        {
            log.Append(' ');
            log.Append(asset.Fields[i].Name);
            log.Append('=');
            log.Append(asset.Fields[i].Format);
        }

        Debug.Log(log.ToString());
    }

    [MenuItem("Tools/M3D/Assign Fluid2D HarrisOrder Experiment To Scene")]
    public static void AssignFluid2DHarrisOrderToScene()
    {
        SimulationWorld world = Object.FindAnyObjectByType<SimulationWorld>();
        EffectAsset fluid = AssetDatabase.LoadAssetAtPath<EffectAsset>(Fluid2DHarrisOrderPath);
        if (world == null || fluid == null)
        {
            Debug.LogError(
                "M3D: SimulationWorld or Fluid2D_HarrisOrder.asset missing — " +
                "run Tools/M3D/Create Fluid2D HarrisOrder Experiment.");
            return;
        }

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = fluid;
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        InputRouter router = world.GetComponent<InputRouter>();
        if (router != null)
        {
            SerializedObject routerSo = new SerializedObject(router);
            routerSo.FindProperty("planeMode").enumValueIndex = (int)InteractionPlaneMode.GroundXZ;
            routerSo.ApplyModifiedPropertiesWithoutUndo();
        }

        SerializedProperty library = worldSo.FindProperty("passLibrary");
        EnsurePassLibrary(library);
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("M3D: scene assigned Fluid2D HarrisOrder (GroundXZ). visualEffect left in place.");
    }

    [MenuItem("Tools/M3D/Create Fluid2D Vorticity Experiment")]
    public static void CreateFluid2DVorticityExperiment()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        Vector2Int res128 = new Vector2Int(128, 128);
        Vector2 planeSize = new Vector2(32f, 32f);

        FieldDescriptor velocity = FieldDescriptor.CreateDefault("velocity", FieldSemantic.Velocity);
        FieldDescriptor fluidD = FieldDescriptor.CreateDefault("fluidD", FieldSemantic.Scalar);
        FieldDescriptor fluidPhi = FieldDescriptor.CreateDefault("fluidPhi", FieldSemantic.Scalar);
        FieldDescriptor dye = FieldDescriptor.CreateDefault("dye", FieldSemantic.Scalar);

        DebugFieldQuadSlot velocityQuad = DebugFieldQuadSlot.Velocity();
        velocityQuad.colorScale = 0.125f;

        CreateEffect(
            Fluid2DVorticityPath,
            cubeResolution: 1,
            simulationSpeed: 1f,
            kind: DataSourceKind.None,
            fields: new[] { velocity, fluidD, fluidPhi, dye },
            debugQuads: new[] { velocityQuad, DebugFieldQuadSlot.Density("dye") },
            new TouchInjectVelocityFieldPass(),
            new SeedScalarDiskPass
            {
                FieldName = "dye",
                CenterUV = new Vector2(0.5f, 0.5f),
                RadiusUV = 0.08f,
                Value = 1f,
            },
            new AdvectVelocityFieldPass
            {
                FieldName = "velocity",
                DissipationRate = 0f,
            },
            new VorticityConfinementPass { EpsilonVc = 1f, BorderMargin = 2 },
            new DivergenceFieldPass(),
            new ZeroMeanScalarPass(),
            new JacobiPhiPass { Iterations = 40 },
            new SubtractPhiGradientPass(),
            new SolidWallVelocityPass(),
            new AdvectScalarPass
            {
                ScalarField = "dye",
                VelocityField = "velocity",
                DissipationRate = 0f,
            });

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(Fluid2DVorticityPath);
        SerializedObject so = new SerializedObject(asset);
        SerializedProperty fieldsProp = so.FindProperty("fields");
        for (int i = 0; i < fieldsProp.arraySize; i++)
        {
            SerializedProperty f = fieldsProp.GetArrayElementAtIndex(i);
            string name = f.FindPropertyRelative("id.name").stringValue;
            f.FindPropertyRelative("resolution").vector2IntValue = res128;
            f.FindPropertyRelative("size").vector2Value = planeSize;
            f.FindPropertyRelative("origin").vector3Value = Vector3.zero;
            f.FindPropertyRelative("axisU").vector3Value = Vector3.right;
            f.FindPropertyRelative("axisV").vector3Value = Vector3.forward;
            f.FindPropertyRelative("clearValue").colorValue = Color.clear;
            if (name == "velocity")
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16G16_SFloat;
            }
            else if (name == "dye")
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16_SFloat;
            }
            else
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R32_SFloat;
            }
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();

        System.Text.StringBuilder log = new System.Text.StringBuilder();
        log.Append("M3D: created Fluid2D Vorticity. Passes:");
        for (int i = 0; i < asset.Passes.Count; i++)
        {
            log.Append(' ');
            log.Append(asset.Passes[i].GetType().Name);
            if (i < asset.Passes.Count - 1)
            {
                log.Append(',');
            }
        }

        log.Append(" Formats:");
        for (int i = 0; i < asset.Fields.Count; i++)
        {
            log.Append(' ');
            log.Append(asset.Fields[i].Name);
            log.Append('=');
            log.Append(asset.Fields[i].Format);
        }

        Debug.Log(log.ToString());
    }

    [MenuItem("Tools/M3D/Assign Fluid2D Vorticity Experiment To Scene")]
    public static void AssignFluid2DVorticityToScene()
    {
        SimulationWorld world = Object.FindAnyObjectByType<SimulationWorld>();
        EffectAsset fluid = AssetDatabase.LoadAssetAtPath<EffectAsset>(Fluid2DVorticityPath);
        if (world == null || fluid == null)
        {
            Debug.LogError(
                "M3D: SimulationWorld or Fluid2D_Vorticity.asset missing — " +
                "run Tools/M3D/Create Fluid2D Vorticity Experiment.");
            return;
        }

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = fluid;
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        InputRouter router = world.GetComponent<InputRouter>();
        if (router != null)
        {
            SerializedObject routerSo = new SerializedObject(router);
            routerSo.FindProperty("planeMode").enumValueIndex = (int)InteractionPlaneMode.GroundXZ;
            routerSo.ApplyModifiedPropertiesWithoutUndo();
        }

        SerializedProperty library = worldSo.FindProperty("passLibrary");
        EnsurePassLibrary(library);
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("M3D: scene assigned Fluid2D Vorticity (GroundXZ). visualEffect left in place.");
    }

    [MenuItem("Tools/M3D/Create Fluid2D MacCormackDye Experiment")]
    public static void CreateFluid2DMacCormackDyeExperiment()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        Vector2Int res128 = new Vector2Int(128, 128);
        Vector2 planeSize = new Vector2(32f, 32f);

        FieldDescriptor velocity = FieldDescriptor.CreateDefault("velocity", FieldSemantic.Velocity);
        FieldDescriptor fluidD = FieldDescriptor.CreateDefault("fluidD", FieldSemantic.Scalar);
        FieldDescriptor fluidPhi = FieldDescriptor.CreateDefault("fluidPhi", FieldSemantic.Scalar);
        FieldDescriptor dye = FieldDescriptor.CreateDefault("dye", FieldSemantic.Scalar);
        FieldDescriptor dyeMacScratch = FieldDescriptor.CreateDefault("dyeMacScratch", FieldSemantic.Scalar);

        DebugFieldQuadSlot velocityQuad = DebugFieldQuadSlot.Velocity();
        velocityQuad.colorScale = 0.125f;

        CreateEffect(
            Fluid2DMacCormackDyePath,
            cubeResolution: 1,
            simulationSpeed: 1f,
            kind: DataSourceKind.None,
            fields: new[] { velocity, fluidD, fluidPhi, dye, dyeMacScratch },
            debugQuads: new[] { velocityQuad, DebugFieldQuadSlot.Density("dye") },
            new TouchInjectVelocityFieldPass(),
            new SeedScalarDiskPass
            {
                FieldName = "dye",
                CenterUV = new Vector2(0.5f, 0.5f),
                RadiusUV = 0.08f,
                Value = 1f,
            },
            new AdvectVelocityFieldPass
            {
                FieldName = "velocity",
                DissipationRate = 0f,
            },
            new VorticityConfinementPass { EpsilonVc = 1f, BorderMargin = 2 },
            new DivergenceFieldPass(),
            new ZeroMeanScalarPass(),
            new JacobiPhiPass { Iterations = 40 },
            new SubtractPhiGradientPass(),
            new SolidWallVelocityPass(),
            new CopyScalarPass
            {
                ScalarField = "dye",
                ScratchField = "dyeMacScratch",
            },
            new AdvectScalarPass
            {
                ScalarField = "dye",
                VelocityField = "velocity",
                DissipationRate = 0f,
                Reverse = false,
            },
            new AdvectScalarPass
            {
                ScalarField = "dye",
                VelocityField = "velocity",
                DissipationRate = 0f,
                Reverse = true,
            },
            new LimitedMacCormackCombinePass
            {
                ScalarField = "dye",
                ScratchField = "dyeMacScratch",
            });

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(Fluid2DMacCormackDyePath);
        SerializedObject so = new SerializedObject(asset);
        SerializedProperty fieldsProp = so.FindProperty("fields");
        for (int i = 0; i < fieldsProp.arraySize; i++)
        {
            SerializedProperty f = fieldsProp.GetArrayElementAtIndex(i);
            string name = f.FindPropertyRelative("id.name").stringValue;
            f.FindPropertyRelative("resolution").vector2IntValue = res128;
            f.FindPropertyRelative("size").vector2Value = planeSize;
            f.FindPropertyRelative("origin").vector3Value = Vector3.zero;
            f.FindPropertyRelative("axisU").vector3Value = Vector3.right;
            f.FindPropertyRelative("axisV").vector3Value = Vector3.forward;
            f.FindPropertyRelative("clearValue").colorValue = Color.clear;
            if (name == "velocity")
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16G16_SFloat;
            }
            else if (name == "dye" || name == "dyeMacScratch")
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16_SFloat;
            }
            else
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R32_SFloat;
            }
        }

        so.ApplyModifiedPropertiesWithoutUndo();

        for (int i = 0; i < asset.Passes.Count; i++)
        {
            if (asset.Passes[i] is SeedScalarDiskPass seed)
            {
                seed.RadiusUV = 0.16f;
                break;
            }
        }

        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();

        System.Text.StringBuilder log = new System.Text.StringBuilder();
        log.Append("M3D: created Fluid2D MacCormackDye. Passes:");
        for (int i = 0; i < asset.Passes.Count; i++)
        {
            log.Append(' ');
            log.Append(asset.Passes[i].GetType().Name);
            if (i < asset.Passes.Count - 1)
            {
                log.Append(',');
            }
        }

        log.Append(" Formats:");
        for (int i = 0; i < asset.Fields.Count; i++)
        {
            log.Append(' ');
            log.Append(asset.Fields[i].Name);
            log.Append('=');
            log.Append(asset.Fields[i].Format);
        }

        Debug.Log(log.ToString());
    }

    [MenuItem("Tools/M3D/Assign Fluid2D MacCormackDye Experiment To Scene")]
    public static void AssignFluid2DMacCormackDyeToScene()
    {
        SimulationWorld world = Object.FindAnyObjectByType<SimulationWorld>();
        EffectAsset fluid = AssetDatabase.LoadAssetAtPath<EffectAsset>(Fluid2DMacCormackDyePath);
        if (world == null || fluid == null)
        {
            Debug.LogError(
                "M3D: SimulationWorld or Fluid2D_MacCormackDye.asset missing — " +
                "run Tools/M3D/Create Fluid2D MacCormackDye Experiment.");
            return;
        }

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = fluid;
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        InputRouter router = world.GetComponent<InputRouter>();
        if (router != null)
        {
            SerializedObject routerSo = new SerializedObject(router);
            routerSo.FindProperty("planeMode").enumValueIndex = (int)InteractionPlaneMode.GroundXZ;
            routerSo.ApplyModifiedPropertiesWithoutUndo();
        }

        SerializedProperty library = worldSo.FindProperty("passLibrary");
        EnsurePassLibrary(library);
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("M3D: scene assigned Fluid2D MacCormackDye (GroundXZ). visualEffect left in place.");
    }

    [MenuItem("Tools/M3D/Create Fluid2D HighResDye Experiment")]
    public static void CreateFluid2DHighResDyeExperiment()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        Vector2Int res128 = new Vector2Int(128, 128);
        Vector2Int res512 = new Vector2Int(512, 512);
        Vector2 planeSize = new Vector2(32f, 32f);

        FieldDescriptor velocity = FieldDescriptor.CreateDefault("velocity", FieldSemantic.Velocity);
        FieldDescriptor fluidD = FieldDescriptor.CreateDefault("fluidD", FieldSemantic.Scalar);
        FieldDescriptor fluidPhi = FieldDescriptor.CreateDefault("fluidPhi", FieldSemantic.Scalar);
        FieldDescriptor dye = FieldDescriptor.CreateDefault("dye", FieldSemantic.Scalar);

        DebugFieldQuadSlot velocityQuad = DebugFieldQuadSlot.Velocity();
        velocityQuad.colorScale = 0.125f;

        CreateEffect(
            Fluid2DHighResDyePath,
            cubeResolution: 1,
            simulationSpeed: 1f,
            kind: DataSourceKind.None,
            fields: new[] { velocity, fluidD, fluidPhi, dye },
            debugQuads: new[] { velocityQuad, DebugFieldQuadSlot.Density("dye") },
            new TouchInjectVelocityFieldPass(),
            new SeedScalarDiskPass
            {
                FieldName = "dye",
                CenterUV = new Vector2(0.5f, 0.5f),
                RadiusUV = 0.08f,
                Value = 1f,
            },
            new DivergenceFieldPass(),
            new ZeroMeanScalarPass(),
            new JacobiPhiPass { Iterations = 40 },
            new SubtractPhiGradientPass(),
            new SolidWallVelocityPass(),
            new AdvectVelocityFieldPass
            {
                FieldName = "velocity",
                DissipationRate = 0f,
            },
            new SolidWallVelocityPass(),
            new AdvectScalarPass
            {
                ScalarField = "dye",
                VelocityField = "velocity",
                DissipationRate = 0f,
            });

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(Fluid2DHighResDyePath);
        SerializedObject so = new SerializedObject(asset);
        SerializedProperty fieldsProp = so.FindProperty("fields");
        for (int i = 0; i < fieldsProp.arraySize; i++)
        {
            SerializedProperty f = fieldsProp.GetArrayElementAtIndex(i);
            string name = f.FindPropertyRelative("id.name").stringValue;
            f.FindPropertyRelative("resolution").vector2IntValue =
                name == "dye" ? res512 : res128;
            f.FindPropertyRelative("size").vector2Value = planeSize;
            f.FindPropertyRelative("origin").vector3Value = Vector3.zero;
            f.FindPropertyRelative("axisU").vector3Value = Vector3.right;
            f.FindPropertyRelative("axisV").vector3Value = Vector3.forward;
            f.FindPropertyRelative("clearValue").colorValue = Color.clear;
            if (name == "velocity")
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16G16_SFloat;
            }
            else if (name == "dye")
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R16_SFloat;
            }
            else
            {
                f.FindPropertyRelative("format").intValue = (int)GraphicsFormat.R32_SFloat;
            }
        }

        so.ApplyModifiedPropertiesWithoutUndo();

        for (int i = 0; i < asset.Passes.Count; i++)
        {
            if (asset.Passes[i] is SeedScalarDiskPass seed)
            {
                seed.RadiusUV = 0.16f;
                break;
            }
        }

        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();

        System.Text.StringBuilder log = new System.Text.StringBuilder();
        log.Append("M3D: created Fluid2D HighResDye. Passes:");
        for (int i = 0; i < asset.Passes.Count; i++)
        {
            log.Append(' ');
            log.Append(asset.Passes[i].GetType().Name);
            if (i < asset.Passes.Count - 1)
            {
                log.Append(',');
            }
        }

        log.Append(" Formats:");
        for (int i = 0; i < asset.Fields.Count; i++)
        {
            log.Append(' ');
            log.Append(asset.Fields[i].Name);
            log.Append('=');
            log.Append(asset.Fields[i].Format);
            log.Append('@');
            log.Append(asset.Fields[i].Resolution.x);
        }

        Debug.Log(log.ToString());
    }

    [MenuItem("Tools/M3D/Assign Fluid2D HighResDye Experiment To Scene")]
    public static void AssignFluid2DHighResDyeToScene()
    {
        SimulationWorld world = Object.FindAnyObjectByType<SimulationWorld>();
        EffectAsset fluid = AssetDatabase.LoadAssetAtPath<EffectAsset>(Fluid2DHighResDyePath);
        if (world == null || fluid == null)
        {
            Debug.LogError(
                "M3D: SimulationWorld or Fluid2D_HighResDye.asset missing — " +
                "run Tools/M3D/Create Fluid2D HighResDye Experiment.");
            return;
        }

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = fluid;
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        InputRouter router = world.GetComponent<InputRouter>();
        if (router != null)
        {
            SerializedObject routerSo = new SerializedObject(router);
            routerSo.FindProperty("planeMode").enumValueIndex = (int)InteractionPlaneMode.GroundXZ;
            routerSo.ApplyModifiedPropertiesWithoutUndo();
        }

        SerializedProperty library = worldSo.FindProperty("passLibrary");
        EnsurePassLibrary(library);
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("M3D: scene assigned Fluid2D HighResDye (GroundXZ). visualEffect left in place.");
    }

    [MenuItem("Tools/M3D/Add F2.3 Scripted Stroke To Scene")]
    public static void AddF23ScriptedStrokeToScene()
    {
        SimulationWorld world = Object.FindAnyObjectByType<SimulationWorld>();
        InputRouter router = Object.FindAnyObjectByType<InputRouter>();
        if (world == null)
        {
            Debug.LogError("M3D: SimulationWorld missing — open Test1 and Setup Open Scene.");
            return;
        }

        GameObject host = router != null ? router.gameObject : world.gameObject;
        if (router == null)
        {
            router = host.GetComponent<InputRouter>();
            if (router == null)
            {
                router = host.AddComponent<InputRouter>();
            }
        }

        ScriptedTouchStroke stroke = host.GetComponent<ScriptedTouchStroke>();
        if (stroke == null)
        {
            stroke = host.AddComponent<ScriptedTouchStroke>();
        }

        stroke.StartUV = new Vector2(0.5f, 0.25f);
        stroke.EndUV = new Vector2(0.5f, 0.75f);
        stroke.Duration = 0.5f;
        stroke.enabled = true;

        SerializedObject worldSo = new SerializedObject(world);
        SerializedProperty routerProp = worldSo.FindProperty("inputRouter");
        if (routerProp.objectReferenceValue == null)
        {
            routerProp.objectReferenceValue = router;
            worldSo.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorUtility.SetDirty(host);
        EditorSceneManager.MarkSceneDirty(host.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log(
            "M3D: ScriptedTouchStroke on '" + host.name +
            "'. After F2.3 visual disable the component, or the next Play will run the script again.");
    }

    [MenuItem("Tools/M3D/Create Physarum Effect")]
    public static void CreatePhysarumEffect()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        FieldDescriptor trail = FieldDescriptor.CreateDefault("trail", FieldSemantic.Scalar);
        DebugFieldQuadSlot trailQuad = DebugFieldQuadSlot.Density("trail");
        trailQuad.colorScale = 0.03f;

        ClearFieldAccumPass clear = new ClearFieldAccumPass();
        SetPrivate(clear, "fieldName", "trail");
        SetPrivate(clear, "channels", 1);

        ScatterDensityToFieldPass scatter = new ScatterDensityToFieldPass();
        SetPrivate(scatter, "targetFieldName", "trail");
        SetPrivate(scatter, "valueScale", 4096f);
        SetPrivate(scatter, "valueBias", 0f);

        NormalizeDensityAccumPass normalize = new NormalizeDensityAccumPass();
        SetPrivate(normalize, "fieldName", "trail");
        SetPrivate(normalize, "valueScale", 4096f);
        SetPrivate(normalize, "valueBias", 0f);

        CreateEffect(
            PhysarumPath,
            cubeResolution: 32,
            simulationSpeed: 1f,
            fields: new[] { trail },
            debugQuads: new[] { trailQuad },
            clear,
            scatter,
            normalize,
            new DecayFieldScalarPass { FieldName = "trail", DecayRate = 0.8f },
            new DiffuseFieldPass { FieldName = "trail", DiffusionRate = 0.15f },
            new DiffuseFieldPass { FieldName = "trail", DiffusionRate = 0.15f },
            new PhysarumSteerPass(),
            new IntegratePass(),
            new BoxBoundsPass
            {
                Center = Vector3.zero,
                Extents = new Vector3(16f, 0f, 16f),
                Behaviour = BoundsBehaviour.Wrap,
            },
            new HeadingToValuePass());

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(PhysarumPath);
        SerializedObject so = new SerializedObject(asset);
        so.FindProperty("cubeSource.cubeSize").floatValue = 32f;
        SerializedProperty field = so.FindProperty("fields").GetArrayElementAtIndex(0);
        field.FindPropertyRelative("resolution").vector2IntValue = new Vector2Int(128, 128);
        field.FindPropertyRelative("size").vector2Value = new Vector2(32f, 32f);
        so.FindProperty("particleSize").floatValue = 0.2f;
        so.FindProperty("particleValueScale").floatValue = 1f;
        so.FindProperty("particleGradient").gradientValue = DebugFieldQuadSlot.DefaultFireGradient();
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();

        SimulationWorld world = Object.FindAnyObjectByType<SimulationWorld>();
        if (world == null)
        {
            Debug.LogError("M3D: Physarum.asset created, but no SimulationWorld in the open scene.");
            return;
        }

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = asset;
        SerializedProperty library = worldSo.FindProperty("passLibrary");
        EnsurePassLibrary(library);
        worldSo.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("M3D: created Physarum and assigned it to the open scene.");
    }

    [MenuItem("Tools/M3D/Create Physarum Fluid Effect")]
    public static void CreatePhysarumFluidEffect()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        FieldDescriptor trail = FieldDescriptor.CreateDefault("trail", FieldSemantic.Scalar);
        FieldDescriptor velocity = FieldDescriptor.CreateDefault("velocity", FieldSemantic.Velocity);
        DebugFieldQuadSlot trailQuad = DebugFieldQuadSlot.Density("trail");
        trailQuad.colorScale = 0.03f;
        DebugFieldQuadSlot velocityQuad = DebugFieldQuadSlot.Velocity("velocity");
        velocityQuad.colorScale = 0.125f;

        ClearFieldAccumPass clear = new ClearFieldAccumPass();
        SetPrivate(clear, "fieldName", "trail");
        SetPrivate(clear, "channels", 1);

        ScatterDensityToFieldPass scatter = new ScatterDensityToFieldPass();
        SetPrivate(scatter, "targetFieldName", "trail");
        SetPrivate(scatter, "valueScale", 4096f);
        SetPrivate(scatter, "valueBias", 0f);

        NormalizeDensityAccumPass normalize = new NormalizeDensityAccumPass();
        SetPrivate(normalize, "fieldName", "trail");
        SetPrivate(normalize, "valueScale", 4096f);
        SetPrivate(normalize, "valueBias", 0f);

        CreateEffect(
            PhysarumFluidPath,
            cubeResolution: 32,
            simulationSpeed: 1f,
            fields: new[] { trail, velocity },
            debugQuads: new[] { trailQuad, velocityQuad },
            new TouchInjectVelocityFieldPass(),
            new DecayFieldPass { FieldName = "velocity", DecayRate = 0.4f },
            clear,
            scatter,
            normalize,
            new AdvectScalarPass
            {
                ScalarField = "trail",
                VelocityField = "velocity",
                DissipationRate = 0f,
                Reverse = false,
                WrapUv = true,
            },
            new DecayFieldScalarPass { FieldName = "trail", DecayRate = 0.8f },
            new DiffuseFieldPass { FieldName = "trail", DiffusionRate = 0.15f },
            new DiffuseFieldPass { FieldName = "trail", DiffusionRate = 0.15f },
            new PhysarumSteerPass(),
            new IntegratePass(),
            new BoxBoundsPass
            {
                Center = Vector3.zero,
                Extents = new Vector3(16f, 0f, 16f),
                Behaviour = BoundsBehaviour.Wrap,
            },
            new HeadingToValuePass());

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(PhysarumFluidPath);
        SerializedObject so = new SerializedObject(asset);
        so.FindProperty("cubeSource.cubeSize").floatValue = 32f;
        SerializedProperty fields = so.FindProperty("fields");
        for (int i = 0; i < fields.arraySize; i++)
        {
            SerializedProperty field = fields.GetArrayElementAtIndex(i);
            field.FindPropertyRelative("resolution").vector2IntValue = new Vector2Int(128, 128);
            field.FindPropertyRelative("size").vector2Value = new Vector2(32f, 32f);
        }

        so.FindProperty("particleSize").floatValue = 0.2f;
        so.FindProperty("particleValueScale").floatValue = 1f;
        so.FindProperty("particleGradient").gradientValue = DebugFieldQuadSlot.DefaultFireGradient();
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);

        SimulationWorld world = Object.FindAnyObjectByType<SimulationWorld>();
        if (world == null)
        {
            Debug.LogError("M3D: Physarum_Fluid.asset created, but no SimulationWorld in the open scene.");
            return;
        }

        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = asset;
        SerializedProperty library = worldSo.FindProperty("passLibrary");
        EnsurePassLibrary(library);
        worldSo.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("M3D: created Physarum_Fluid and assigned it to the open scene.");
    }

    [MenuItem("Tools/M3D/Create Spatial Hash Probe")]
    public static void CreateSpatialHashProbe()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        CreateHashProbeAsset(HashProbe30kPath, 31);
        CreateHashProbeAsset(HashProbe100kPath, 46);

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogError("M3D: spatial hash probe scenes cancelled; assets were still written.");
            return;
        }

        CreateHashProbeScene(HashProbe30kScene, HashProbe30kPath, "30k");
        CreateHashProbeScene(HashProbe100kScene, HashProbe100kPath, "100k");
        Debug.Log("M3D: created HashProbe_30k / HashProbe_100k assets and scenes.");
    }

    private static void CreateHashProbeAsset(string path, int cubeResolution)
    {
        CreateEffect(
            path,
            cubeResolution,
            simulationSpeed: 1f,
            fields: null,
            debugQuads: null,
            new BuildSpatialHashPass(),
            new ClearVelocityPass(),
            new HeadingSteerPass(),
            new IntegratePass(),
            new BoxBoundsPass
            {
                Center = Vector3.zero,
                Extents = new Vector3(16f, 0f, 16f),
                Behaviour = BoundsBehaviour.Wrap,
            });

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        SerializedObject so = new SerializedObject(asset);
        so.FindProperty("cubeSource.cubeSize").floatValue = 32f;
        so.FindProperty("particleSize").floatValue = 0.2f;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);
    }

    private static void CreateHashProbeScene(string scenePath, string assetPath, string label)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        Camera camera = Camera.main;
        if (camera != null)
        {
            camera.transform.SetPositionAndRotation(new Vector3(0f, 40f, 0f), Quaternion.Euler(90f, 0f, 0f));
        }

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(assetPath);
        GameObject host = new GameObject("M3D Probe");
        SimulationWorld world = host.AddComponent<SimulationWorld>();
        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = asset;
        worldSo.FindProperty("visualEffect").objectReferenceValue = null;
        worldSo.FindProperty("inputRouter").objectReferenceValue = null;
        EnsurePassLibrary(worldSo.FindProperty("passLibrary"));
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        HashProbeControls controls = host.AddComponent<HashProbeControls>();
        SerializedObject controlsSo = new SerializedObject(controls);
        controlsSo.FindProperty("world").objectReferenceValue = world;
        controlsSo.FindProperty("label").stringValue = label;
        controlsSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene, scenePath);
    }

    [MenuItem("Tools/M3D/Create Boids Hash Effect")]
    public static void CreateBoidsHashEffect()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        CreateBoidsHashAsset(BoidsHashPath);

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogError("M3D: Boids Hash scene cancelled; the asset was still written.");
            return;
        }

        CreateBoidsHashScene(BoidsHashScene, BoidsHashPath);
        Debug.Log("M3D: created Boids_hash asset and Boids_Hash scene.");
    }

    private static void CreateBoidsHashAsset(string path)
    {
        EffectAsset existing = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        if (existing != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        FieldDescriptor hashCount = FieldDescriptor.CreateDefault("hashCount", FieldSemantic.Scalar);
        SetField(hashCount, "resolution", new Vector2Int(30, 30));
        SetField(hashCount, "size", new Vector2(90f, 90f));
        SetField(hashCount, "origin", new Vector3(0f, -1f, 0f));

        DebugFieldQuadSlot hashQuad = DebugFieldQuadSlot.Density("hashCount");
        hashQuad.colorScale = 0.05f;

        EffectAsset asset = ScriptableObject.CreateInstance<EffectAsset>();
        asset.EditorConfigure(
            DataSourceKind.Swarm,
            1f,
            new SimPass[]
            {
                new BuildSpatialHashPass
                {
                    Center = Vector3.zero,
                    Extents = new Vector3(45f, 0f, 45f),
                    MinCellSize = 3f,
                    Wrap = true,
                },
                new HashCountsToFieldPass { FieldName = "hashCount" },
                new ClearVelocityPass(),
                new BoidNeighborForcePass { MaxNeighbors = 48, CountCapHits = false },
                new TeamHeadingSteerPass(),
                new IntegratePass(),
                new BoxBoundsPass
                {
                    Center = Vector3.zero,
                    Extents = new Vector3(45f, 0f, 45f),
                    Behaviour = BoundsBehaviour.Wrap,
                },
                new HeadingToValuePass(),
            },
            new[] { hashCount },
            new[] { hashQuad });

        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        swarm.Seed = 1;
        swarm.Spawns = new System.Collections.Generic.List<SwarmSource.Spawn>
        {
            new SwarmSource.Spawn
            {
                TeamIndex = 0,
                Count = 3000,
                Center = Vector2.zero,
                Radius = 44f,
                InitialDirection = Vector2.zero,
            },
        };
        asset.SetTeams(new[]
        {
            new TeamProfile
            {
                Name = "Swarm",
                SeparationRadius = 3f,
                AlignmentRadius = 3f,
                CohesionRadius = 3f,
                InterGroupSeparationMultiplier = 4f,
                SeparationWeight = 1.2f,
                AlignmentWeight = 0.8f,
                CohesionWeight = 0.6f,
                Cruise = 6f,
                Turn = 4f,
                Color = DebugFieldQuadSlot.DefaultFireGradient(),
            },
        });

        SerializedObject so = new SerializedObject(asset);
        so.FindProperty("particleSize").floatValue = 0.5f;
        so.FindProperty("particleValueScale").floatValue = 1f;
        so.ApplyModifiedPropertiesWithoutUndo();
        SetField(asset, "particleGradient", DebugFieldQuadSlot.DefaultFireGradient());

        AssetDatabase.CreateAsset(asset, path);
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);
    }

    private static void CreateBoidsHashScene(string scenePath, string assetPath)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        Camera camera = Camera.main;
        if (camera != null)
        {
            camera.orthographic = true;
            camera.orthographicSize = 46f;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 40f, 0f), Quaternion.Euler(90f, 0f, 0f));
        }

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(assetPath);
        GameObject host = new GameObject("M3D Boids Hash");
        SimulationWorld world = host.AddComponent<SimulationWorld>();
        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = asset;
        worldSo.FindProperty("visualEffect").objectReferenceValue = null;
        worldSo.FindProperty("inputRouter").objectReferenceValue = null;
        EnsurePassLibrary(worldSo.FindProperty("passLibrary"));
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene, scenePath);
    }

    [MenuItem("Tools/M3D/Create Boids Hash 2 Teams Effect")]
    public static void CreateBoidsHashTwoTeamsEffect()
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "Effects");
        }

        CreateBoidsHashTwoTeamsAsset(BoidsHashTwoTeamsPath);

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogError("M3D: Boids Hash 2 Teams scene cancelled; the asset was still written.");
            return;
        }

        CreateBoidsHashTwoTeamsScene(BoidsHashTwoTeamsScene, BoidsHashTwoTeamsPath);
        Debug.Log("M3D: created Boids_hash_2teams asset and Boids_Hash_2Teams scene.");
    }

    private static void CreateBoidsHashTwoTeamsAsset(string path)
    {
        EffectAsset existing = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        if (existing != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        FieldDescriptor hashCount = FieldDescriptor.CreateDefault("hashCount", FieldSemantic.Scalar);
        SetField(hashCount, "resolution", new Vector2Int(30, 30));
        SetField(hashCount, "size", new Vector2(90f, 90f));
        SetField(hashCount, "origin", new Vector3(0f, -1f, 0f));

        DebugFieldQuadSlot hashQuad = DebugFieldQuadSlot.Density("hashCount");
        hashQuad.colorScale = 0.05f;

        EffectAsset asset = ScriptableObject.CreateInstance<EffectAsset>();
        asset.EditorConfigure(
            DataSourceKind.Swarm,
            1f,
            new SimPass[]
            {
                new BuildSpatialHashPass
                {
                    Center = Vector3.zero,
                    Extents = new Vector3(45f, 0f, 45f),
                    MinCellSize = 3f,
                    Wrap = true,
                },
                new HashCountsToFieldPass { FieldName = "hashCount" },
                new ClearVelocityPass(),
                new BoidNeighborForcePass { MaxNeighbors = 48, CountCapHits = false },
                new TeamHeadingSteerPass(),
                new IntegratePass(),
                new BoxBoundsPass
                {
                    Center = Vector3.zero,
                    Extents = new Vector3(45f, 0f, 45f),
                    Behaviour = BoundsBehaviour.Wrap,
                },
                new HeadingToValuePass(),
            },
            new[] { hashCount },
            new[] { hashQuad });

        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        swarm.Seed = 1;
        swarm.JitterDegrees = 0f;
        swarm.Spawns = new System.Collections.Generic.List<SwarmSource.Spawn>
        {
            new SwarmSource.Spawn
            {
                TeamIndex = 0,
                Count = 1500,
                Center = new Vector2(-22f, 0f),
                Radius = 12f,
                InitialDirection = new Vector2(1f, 0f),
            },
            new SwarmSource.Spawn
            {
                TeamIndex = 1,
                Count = 1500,
                Center = new Vector2(22f, 0f),
                Radius = 12f,
                InitialDirection = new Vector2(-1f, 0f),
            },
        };
        asset.SetTeams(new[]
        {
            new TeamProfile
            {
                Name = "Fire",
                SeparationRadius = 3f,
                AlignmentRadius = 3f,
                CohesionRadius = 3f,
                InterGroupSeparationMultiplier = 4f,
                SeparationWeight = 1.2f,
                AlignmentWeight = 0.8f,
                CohesionWeight = 0.6f,
                Cruise = 6f,
                Turn = 4f,
                Color = FireCyclicGradient(),
            },
            new TeamProfile
            {
                Name = "Ice",
                SeparationRadius = 3f,
                AlignmentRadius = 3f,
                CohesionRadius = 3f,
                InterGroupSeparationMultiplier = 4f,
                SeparationWeight = 1.2f,
                AlignmentWeight = 0.8f,
                CohesionWeight = 0.6f,
                Cruise = 6f,
                Turn = 4f,
                Color = IceGradient(),
            },
        });

        SerializedObject so = new SerializedObject(asset);
        so.FindProperty("particleSize").floatValue = 0.5f;
        so.FindProperty("particleValueScale").floatValue = 1f;
        so.ApplyModifiedPropertiesWithoutUndo();
        SetField(asset, "particleGradient", DebugFieldQuadSlot.DefaultFireGradient());

        AssetDatabase.CreateAsset(asset, path);
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);
    }

    private static Gradient FireCyclicGradient()
    {
        return CyclicTeamGradient(
            new Color(0.55f, 0.08f, 0.00f),
            new Color(0.85f, 0.30f, 0.00f),
            new Color(1.00f, 0.70f, 0.10f));
    }

    private static Gradient IceGradient()
    {
        return CyclicTeamGradient(
            new Color(0.30f, 0.80f, 1.00f),
            new Color(0.15f, 0.50f, 0.95f),
            new Color(0.05f, 0.15f, 0.55f));
    }

    private static Gradient CyclicTeamGradient(Color ends, Color quarter, Color middle)
    {
        Gradient gradient = new Gradient();
        gradient.mode = GradientMode.Blend;
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(ends, 0f),
                new GradientColorKey(quarter, 0.25f),
                new GradientColorKey(middle, 0.5f),
                new GradientColorKey(quarter, 0.75f),
                new GradientColorKey(ends, 1f),
            },
            new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f),
            });
        return gradient;
    }

    private static void CreateBoidsHashTwoTeamsScene(string scenePath, string assetPath)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        Camera camera = Camera.main;
        if (camera != null)
        {
            camera.orthographic = true;
            camera.orthographicSize = 46f;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 40f, 0f), Quaternion.Euler(90f, 0f, 0f));
        }

        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(assetPath);
        GameObject host = new GameObject("M3D Boids Hash 2 Teams");
        SimulationWorld world = host.AddComponent<SimulationWorld>();
        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = asset;
        worldSo.FindProperty("visualEffect").objectReferenceValue = null;
        worldSo.FindProperty("inputRouter").objectReferenceValue = null;
        EnsurePassLibrary(worldSo.FindProperty("passLibrary"));
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.SaveScene(scene, scenePath);
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new System.InvalidOperationException("M3D: field '" + name + "' was not found.");
        }

        field.SetValue(target, value);
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
        CreateEffect(
            path, cubeResolution, simulationSpeed, DataSourceKind.Cube, fields, debugQuads, passes);
    }

    private static void CreateEffect(
        string path,
        int cubeResolution,
        float simulationSpeed,
        DataSourceKind kind,
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
        asset.EditorConfigure(kind, simulationSpeed, passes, fields, debugQuads);

        SerializedObject so = new SerializedObject(asset);
        so.FindProperty("cubeSource.resolution").intValue = cubeResolution;
        so.ApplyModifiedPropertiesWithoutUndo();

        AssetDatabase.CreateAsset(asset, path);
        EditorUtility.SetDirty(asset);
    }
}
