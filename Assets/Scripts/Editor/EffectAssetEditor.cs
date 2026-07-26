using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Inspector for EffectAsset: source, fields list, Materialize button, polymorphic passes.
/// </summary>
[CustomEditor(typeof(EffectAsset))]
public sealed class EffectAssetEditor : Editor
{
    private SerializedProperty sourceKindProperty;
    private SerializedProperty cubeSourceProperty;
    private SerializedProperty meshSourceProperty;
    private SerializedProperty bitmapSourceProperty;
    private SerializedProperty simulationSpeedProperty;
    private SerializedProperty fieldsProperty;
    private SerializedProperty passesProperty;
    private SerializedProperty showVelocityFieldQuadProperty;
    private ReorderableList passList;

    private void OnEnable()
    {
        sourceKindProperty = serializedObject.FindProperty("sourceKind");
        cubeSourceProperty = serializedObject.FindProperty("cubeSource");
        meshSourceProperty = serializedObject.FindProperty("meshSource");
        bitmapSourceProperty = serializedObject.FindProperty("bitmapSource");
        simulationSpeedProperty = serializedObject.FindProperty("simulationSpeed");
        fieldsProperty = serializedObject.FindProperty("fields");
        passesProperty = serializedObject.FindProperty("passes");
        showVelocityFieldQuadProperty = serializedObject.FindProperty("showVelocityFieldQuad");

        passList = new ReorderableList(serializedObject, passesProperty, true, true, true, true)
        {
            drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Passes (execution order)"),
            elementHeightCallback = GetElementHeight,
            drawElementCallback = DrawElement,
            onAddDropdownCallback = ShowAddMenu,
        };
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(sourceKindProperty);
        switch ((DataSourceKind)sourceKindProperty.intValue)
        {
            case DataSourceKind.Cube:
                EditorGUILayout.PropertyField(cubeSourceProperty, true);
                break;
            case DataSourceKind.Mesh:
                EditorGUILayout.PropertyField(meshSourceProperty, true);
                break;
            case DataSourceKind.Bitmap:
                EditorGUILayout.PropertyField(bitmapSourceProperty, true);
                break;
        }

        EditorGUILayout.PropertyField(simulationSpeedProperty);
        EditorGUILayout.PropertyField(showVelocityFieldQuadProperty);

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(fieldsProperty, new GUIContent("Fields"), true);

        if (GUILayout.Button("Materialize missing fields from passes"))
        {
            EffectAsset asset = (EffectAsset)target;
            Undo.RecordObject(asset, "Materialize Fields");
            int added = asset.MaterializeMissingFields();
            EditorUtility.SetDirty(asset);
            serializedObject.Update();
            Debug.Log(added > 0
                ? $"Materialized {added} field descriptor(s)."
                : "No missing fields — declarations already cover all pass requests.");
        }

        EditorGUILayout.Space();
        passList.DoLayoutList();

        serializedObject.ApplyModifiedProperties();
    }

    private float GetElementHeight(int index)
    {
        SerializedProperty element = passesProperty.GetArrayElementAtIndex(index);
        return EditorGUI.GetPropertyHeight(element, true) + 6f;
    }

    private void DrawElement(Rect rect, int index, bool isActive, bool isFocused)
    {
        SerializedProperty element = passesProperty.GetArrayElementAtIndex(index);
        string label = element.managedReferenceValue is SimPass pass
            ? $"{pass.DisplayName}  [{pass.Category}]"
            : "(missing pass)";

        rect.xMin += 10f;
        rect.yMin += 2f;
        rect.yMax -= 2f;
        EditorGUI.PropertyField(rect, element, new GUIContent(label), true);
    }

    private void ShowAddMenu(Rect buttonRect, ReorderableList list)
    {
        GenericMenu menu = new GenericMenu();
        List<Type> types = new List<Type>(TypeCache.GetTypesDerivedFrom<SimPass>());
        types.Sort((a, b) =>
        {
            SimPass sa = (SimPass)Activator.CreateInstance(a);
            SimPass sb = (SimPass)Activator.CreateInstance(b);
            int cat = sa.Category.CompareTo(sb.Category);
            return cat != 0 ? cat : string.CompareOrdinal(sa.DisplayName, sb.DisplayName);
        });

        foreach (Type type in types)
        {
            if (type.IsAbstract)
            {
                continue;
            }

            SimPass sample = (SimPass)Activator.CreateInstance(type);
            Type captured = type;
            menu.AddItem(
                new GUIContent($"{sample.Category}/{sample.DisplayName}"),
                false,
                () => AddPass(captured));
        }

        menu.DropDown(buttonRect);
    }

    private void AddPass(Type passType)
    {
        serializedObject.Update();
        int index = passesProperty.arraySize;
        passesProperty.arraySize++;
        passesProperty.GetArrayElementAtIndex(index).managedReferenceValue =
            Activator.CreateInstance(passType);
        serializedObject.ApplyModifiedProperties();
    }
}
