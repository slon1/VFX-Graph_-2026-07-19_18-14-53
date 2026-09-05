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
