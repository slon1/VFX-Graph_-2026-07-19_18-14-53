using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Inspector for EffectAsset: shows only the active source config and a
/// reorderable polymorphic pass list with an "Add Pass" menu grouped by category.
/// </summary>
[CustomEditor(typeof(EffectAsset))]
public sealed class EffectAssetEditor : Editor
{
    private SerializedProperty sourceKindProperty;
    private SerializedProperty cubeSourceProperty;
    private SerializedProperty meshSourceProperty;
    private SerializedProperty bitmapSourceProperty;
    private SerializedProperty simulationSpeedProperty;
    private SerializedProperty passesProperty;
    private ReorderableList passList;

    private void OnEnable()
    {
        sourceKindProperty = serializedObject.FindProperty("sourceKind");
        cubeSourceProperty = serializedObject.FindProperty("cubeSource");
        meshSourceProperty = serializedObject.FindProperty("meshSource");
        bitmapSourceProperty = serializedObject.FindProperty("bitmapSource");
        simulationSpeedProperty = serializedObject.FindProperty("simulationSpeed");
        passesProperty = serializedObject.FindProperty("passes");

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

        foreach (Type type in TypeCache.GetTypesDerivedFrom<SimPass>())
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
