using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>One-shot ADR-012 preset wiring for Boids_mk1 (Inspector-safe, no YAML rid edits).</summary>
public static class Adr012BoidsMk1Setup
{
    private const string AssetPath = "Assets/Effects/Boids_mk1.asset";

    [MenuItem("Tools/M3D/ADR-012 Reconfigure Boids_mk1")]
    public static void ReconfigureBoidsMk1()
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        if (asset == null)
        {
            Debug.LogError($"ADR-012: missing {AssetPath}");
            return;
        }

        List<SimPass> passes = BuildPassList();
        SerializedObject so = new SerializedObject(asset);
        SerializedProperty passesProp = so.FindProperty("passes");
        passesProp.ClearArray();

        for (int i = 0; i < passes.Count; i++)
        {
            passesProp.InsertArrayElementAtIndex(i);
            passesProp.GetArrayElementAtIndex(i).managedReferenceValue = passes[i];
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        Debug.Log($"ADR-012: reconfigured {AssetPath} with {passes.Count} passes.");
    }

    private static List<SimPass> BuildPassList()
    {
        var list = new List<SimPass>();

        // 1. P2G flockVel + 6x DiffuseVelocity
        list.Add(MakeClearAccum("flockVel", 2));
        list.Add(MakeScatterVelocity("flockVel", 4096f, 32f));
        list.Add(MakeNormalizeVelocity("flockVel", 4096f, 32f));
        list.Add(MakeDecayField("flockVel", 2f));
        for (int i = 0; i < 6; i++)
        {
            list.Add(new DiffuseVelocityFieldPass { FieldName = "flockVel", DiffusionRate = 0.15f });
        }

        // 2. P2G cohesion + 6x Diffuse
        list.Add(MakeClearAccum("cohesionDensity", 1));
        list.Add(MakeScatterDensity("cohesionDensity", 4096f, 0f));
        list.Add(MakeNormalizeDensity("cohesionDensity", 4096f, 0f));
        list.Add(MakeDecayScalar("cohesionDensity", 2f));
        for (int i = 0; i < 6; i++)
        {
            list.Add(new DiffuseFieldPass { FieldName = "cohesionDensity", DiffusionRate = 0.18f });
        }

        // 3. P2G separation
        list.Add(MakeClearAccum("separationDensity", 1));
        list.Add(MakeScatterDensity("separationDensity", 4096f, 0f));
        list.Add(MakeNormalizeDensity("separationDensity", 4096f, 0f));
        list.Add(MakeDecayScalar("separationDensity", 2f));

        // 4. Kinematic heading window
        list.Add(new ClearVelocityPass());
        list.Add(new AddNormalizedVelocityFieldPass { VelocityFieldName = "flockVel", Weight = 0.8f });
        list.Add(new AddNormalizedGradientFieldPass { FieldName = "cohesionDensity", Weight = 0.6f });
        list.Add(new AddNormalizedGradientFieldPass { FieldName = "separationDensity", Weight = -1.2f });
        list.Add(new HeadingSteerPass { TurnSpeed = 0.15f, CruiseSpeed = 4f });
        list.Add(new IntegratePass());
        list.Add(new BoxBoundsPass
        {
            Center = Vector3.zero,
            Extents = new Vector3(50f, 50f, 50f),
            Behaviour = BoundsBehaviour.Wrap,
            Bounce = 0.6f,
        });

        return list;
    }

    private static ClearFieldAccumPass MakeClearAccum(string fieldName, int channels)
    {
        var p = new ClearFieldAccumPass();
        SetPrivate(p, "fieldName", fieldName);
        SetPrivate(p, "channels", channels);
        return p;
    }

    private static ScatterVelocityToFieldPass MakeScatterVelocity(string target, float scale, float bias)
    {
        var p = new ScatterVelocityToFieldPass();
        SetPrivate(p, "targetFieldName", target);
        SetPrivate(p, "valueScale", scale);
        SetPrivate(p, "valueBias", bias);
        return p;
    }

    private static NormalizeVelocityAccumPass MakeNormalizeVelocity(string fieldName, float scale, float bias)
    {
        var p = new NormalizeVelocityAccumPass();
        SetPrivate(p, "fieldName", fieldName);
        SetPrivate(p, "valueScale", scale);
        SetPrivate(p, "valueBias", bias);
        return p;
    }

    private static DecayFieldPass MakeDecayField(string fieldName, float rate)
    {
        var p = new DecayFieldPass();
        SetPrivate(p, "fieldName", fieldName);
        SetPrivate(p, "decayRate", rate);
        return p;
    }

    private static ScatterDensityToFieldPass MakeScatterDensity(string target, float scale, float bias)
    {
        var p = new ScatterDensityToFieldPass();
        SetPrivate(p, "targetFieldName", target);
        SetPrivate(p, "valueScale", scale);
        SetPrivate(p, "valueBias", bias);
        return p;
    }

    private static NormalizeDensityAccumPass MakeNormalizeDensity(string fieldName, float scale, float bias)
    {
        var p = new NormalizeDensityAccumPass();
        SetPrivate(p, "fieldName", fieldName);
        SetPrivate(p, "valueScale", scale);
        SetPrivate(p, "valueBias", bias);
        return p;
    }

    private static DecayFieldScalarPass MakeDecayScalar(string fieldName, float rate)
    {
        var p = new DecayFieldScalarPass();
        SetPrivate(p, "fieldName", fieldName);
        SetPrivate(p, "decayRate", rate);
        return p;
    }

    private static void SetPrivate(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(target, value);
    }
}
