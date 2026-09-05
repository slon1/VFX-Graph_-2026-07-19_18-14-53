using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Idempotent HDR + Bloom + ACES volume setup for the open scene (ADR-025).
/// </summary>
public static class PostProcessingSetup
{
    private const string VolumeObjectName = "M3D Volume";
    private const string ProfilePath = "Assets/Settings/M3DVolumeProfile.asset";
    private const string PcRpAssetPath = "Assets/Settings/PC_RPAsset.asset";
    private const string MobileRpAssetPath = "Assets/Settings/Mobile_RPAsset.asset";

    private const float BloomThreshold = 0.8f;
    private const float BloomIntensity = 0.4f;
    private const float BloomScatter = 0.65f;

    [MenuItem("Tools/M3D/Setup Post-Processing (HDR + Bloom + ACES)")]
    public static void SetupPostProcessing()
    {
        LogHdrState();
        DetachQualityVolumeProfiles();
        EnsureCameraPostProcessing();
        Volume volume = EnsureVolumeObject();
        VolumeProfile profile = EnsureVolumeProfile(volume);
        EnsureBloom(profile);
        EnsureTonemapping(profile);
        EnsureColorAdjustments(profile);
        EnsureMobileGate(volume.gameObject);

        EditorUtility.SetDirty(profile);
        EditorUtility.SetDirty(volume);
        Scene scene = volume.gameObject.scene;
        if (scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(scene);
        }

        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("M3D: post-processing setup done (or already present, idempotent).");
    }

    private static void LogHdrState()
    {
        LogUrpHdr(PcRpAssetPath);
        LogUrpHdr(MobileRpAssetPath);

        Camera[] cameras = Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (cameras.Length == 0)
        {
            Debug.LogWarning("M3D: no Camera in the open scene to log HDR / post-processing flags.");
            return;
        }

        for (int i = 0; i < cameras.Length; i++)
        {
            Camera camera = cameras[i];
            bool postProcessing = false;
            if (camera.TryGetComponent(out UniversalAdditionalCameraData cameraData))
            {
                postProcessing = cameraData.renderPostProcessing;
            }

            Debug.Log(
                $"M3D: camera '{camera.name}' allowHDR={camera.allowHDR} renderPostProcessing={postProcessing}.");
        }
    }

    private static void LogUrpHdr(string assetPath)
    {
        UniversalRenderPipelineAsset asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assetPath);
        if (asset == null)
        {
            Debug.LogError($"M3D: URP asset not found at '{assetPath}'.");
            return;
        }

        Debug.Log($"M3D: {assetPath} supportsHDR={ReadSupportsHdr(asset)} (read-only, not written).");
    }

    private static bool ReadSupportsHdr(UniversalRenderPipelineAsset asset)
    {
        System.Reflection.PropertyInfo property = typeof(UniversalRenderPipelineAsset).GetProperty("supportsHDR");
        if (property != null && property.PropertyType == typeof(bool) && property.GetGetMethod() != null)
        {
            return (bool)property.GetValue(asset);
        }

        SerializedObject so = new SerializedObject(asset);
        SerializedProperty hdr = so.FindProperty("m_SupportsHDR");
        if (hdr == null)
        {
            Debug.LogWarning($"M3D: could not read HDR flag on '{asset.name}'.");
            return false;
        }

        return hdr.boolValue;
    }

    private static void DetachQualityVolumeProfiles()
    {
        DetachQualityVolumeProfile(PcRpAssetPath);
        DetachQualityVolumeProfile(MobileRpAssetPath);
    }

    private static void DetachQualityVolumeProfile(string assetPath)
    {
        UniversalRenderPipelineAsset asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assetPath);
        if (asset == null)
        {
            Debug.LogError($"M3D: URP asset not found at '{assetPath}'.");
            return;
        }

        SerializedObject so = new SerializedObject(asset);
        SerializedProperty profileProp = so.FindProperty("m_VolumeProfile");
        if (profileProp == null)
        {
            Debug.LogError($"M3D: '{assetPath}' has no m_VolumeProfile property.");
            return;
        }

        if (profileProp.objectReferenceValue == null)
        {
            Debug.Log($"M3D: {assetPath} m_VolumeProfile already None.");
            return;
        }

        string previous = profileProp.objectReferenceValue.name;
        profileProp.objectReferenceValue = null;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        Debug.Log($"M3D: detached m_VolumeProfile '{previous}' from {assetPath} (HDR flags untouched).");
    }

    private static void EnsureCameraPostProcessing()
    {
        UniversalAdditionalCameraData[] cameras = Object.FindObjectsByType<UniversalAdditionalCameraData>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (cameras.Length == 0)
        {
            Debug.LogWarning("M3D: no UniversalAdditionalCameraData in the open scene.");
            return;
        }

        for (int i = 0; i < cameras.Length; i++)
        {
            UniversalAdditionalCameraData cameraData = cameras[i];
            if (cameraData.renderPostProcessing)
            {
                Debug.Log($"M3D: camera '{cameraData.gameObject.name}' renderPostProcessing already enabled.");
                continue;
            }

            cameraData.renderPostProcessing = true;
            EditorUtility.SetDirty(cameraData);
            Debug.Log($"M3D: enabled renderPostProcessing on '{cameraData.gameObject.name}'.");
        }
    }

    private static Volume EnsureVolumeObject()
    {
        Volume[] volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < volumes.Length; i++)
        {
            Volume existing = volumes[i];
            if (existing == null || existing.gameObject.name != VolumeObjectName)
            {
                continue;
            }

            if (!existing.isGlobal)
            {
                existing.isGlobal = true;
                EditorUtility.SetDirty(existing);
                Debug.Log("M3D: reused 'M3D Volume' and set isGlobal=true.");
            }
            else
            {
                Debug.Log("M3D: reusing existing GameObject 'M3D Volume'.");
            }

            return existing;
        }

        GameObject go = new GameObject(VolumeObjectName);
        Volume volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        Debug.Log("M3D: created GameObject 'M3D Volume' (isGlobal=true).");
        return volume;
    }

    private static VolumeProfile EnsureVolumeProfile(Volume volume)
    {
        VolumeProfile desired = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (desired == null)
        {
            desired = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(desired, ProfilePath);
            Debug.Log($"M3D: created volume profile at '{ProfilePath}'.");
        }
        else
        {
            StripMissingComponents(desired);
            Debug.Log($"M3D: reusing volume profile at '{ProfilePath}'.");
        }

        if (volume.sharedProfile != desired)
        {
            if (volume.sharedProfile != null)
            {
                Debug.LogWarning(
                    $"M3D: replacing Volume sharedProfile '{volume.sharedProfile.name}' with '{desired.name}'.");
            }

            volume.sharedProfile = desired;
            EditorUtility.SetDirty(volume);
        }

        return desired;
    }

    private static void StripMissingComponents(VolumeProfile profile)
    {
        int removed = 0;
        for (int i = profile.components.Count - 1; i >= 0; i--)
        {
            if (profile.components[i] == null)
            {
                profile.components.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            EditorUtility.SetDirty(profile);
            Debug.LogWarning($"M3D: stripped {removed} missing VolumeComponent reference(s) from '{profile.name}'.");
        }
    }

    private static void PersistComponent(VolumeComponent component, VolumeProfile profile)
    {
        component.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
        if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(component)))
        {
            AssetDatabase.AddObjectToAsset(component, profile);
        }

        EditorUtility.SetDirty(component);
        EditorUtility.SetDirty(profile);
    }

    private static void EnsureBloom(VolumeProfile profile)
    {
        if (profile.TryGet(out Bloom bloom))
        {
            Debug.Log("M3D: Bloom already present, leaving calibrated values untouched.");
            return;
        }

        bloom = profile.Add<Bloom>(overrides: true);
        bloom.threshold.Override(BloomThreshold);
        bloom.intensity.Override(BloomIntensity);
        bloom.scatter.Override(BloomScatter);
        PersistComponent(bloom, profile);
        Debug.Log(
            $"M3D: added Bloom threshold={BloomThreshold} intensity={BloomIntensity} scatter={BloomScatter}.");
    }

    private static void EnsureTonemapping(VolumeProfile profile)
    {
        if (profile.TryGet(out Tonemapping tonemapping))
        {
            Debug.Log("M3D: Tonemapping already present, leaving calibrated values untouched.");
            return;
        }

        tonemapping = profile.Add<Tonemapping>(overrides: true);
        tonemapping.mode.Override(TonemappingMode.ACES);
        PersistComponent(tonemapping, profile);
        Debug.Log("M3D: added Tonemapping mode=ACES.");
    }

    private static void EnsureColorAdjustments(VolumeProfile profile)
    {
        if (profile.TryGet(out ColorAdjustments colorAdjustments))
        {
            Debug.Log("M3D: ColorAdjustments already present, leaving calibrated values untouched.");
            return;
        }

        colorAdjustments = profile.Add<ColorAdjustments>(overrides: true);
        colorAdjustments.postExposure.Override(0f);
        colorAdjustments.contrast.Override(0f);
        colorAdjustments.saturation.Override(0f);
        PersistComponent(colorAdjustments, profile);
        Debug.Log("M3D: added ColorAdjustments postExposure/contrast/saturation=0 (stub).");
    }

    private static void EnsureMobileGate(GameObject volumeGo)
    {
        if (volumeGo.GetComponent<M3DVolumeMobileGate>() != null)
        {
            Debug.Log("M3D: M3DVolumeMobileGate already present.");
            return;
        }

        volumeGo.AddComponent<M3DVolumeMobileGate>();
        EditorUtility.SetDirty(volumeGo);
        Debug.Log("M3D: added M3DVolumeMobileGate.");
    }
}
