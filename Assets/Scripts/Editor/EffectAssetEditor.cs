using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Inspector for EffectAsset: source, fields list, Materialize button, polymorphic passes,
/// debug field quads (dropdown field names).
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
    private SerializedProperty debugFieldQuadsProperty;
    private ReorderableList passList;
    private ReorderableList debugQuadList;
    private string[] fieldNameOptions = Array.Empty<string>();

    private void OnEnable()
    {
        sourceKindProperty = serializedObject.FindProperty("sourceKind");
        cubeSourceProperty = serializedObject.FindProperty("cubeSource");
        meshSourceProperty = serializedObject.FindProperty("meshSource");
        bitmapSourceProperty = serializedObject.FindProperty("bitmapSource");
        simulationSpeedProperty = serializedObject.FindProperty("simulationSpeed");
        fieldsProperty = serializedObject.FindProperty("fields");
        passesProperty = serializedObject.FindProperty("passes");
        debugFieldQuadsProperty = serializedObject.FindProperty("debugFieldQuads");

        EffectAsset asset = (EffectAsset)target;
        bool migrated = asset.EditorEnsureDebugQuadMigration();
        if (migrated)
        {
            EditorUtility.SetDirty(asset);
        }
        serializedObject.Update();

        passList = new ReorderableList(serializedObject, passesProperty, true, true, true, true)
        {
            drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Passes (execution order)"),
            elementHeightCallback = GetPassElementHeight,
            drawElementCallback = DrawPassElement,
            onAddDropdownCallback = ShowAddPassMenu,
        };

        debugQuadList = new ReorderableList(serializedObject, debugFieldQuadsProperty, true, true, true, true)
        {
            drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Debug Field Quads"),
            elementHeight = EditorGUIUtility.singleLineHeight * 3f + 10f,
            drawElementCallback = DrawDebugQuadElement,
            onAddCallback = AddDebugQuad,
        };

        RefreshFieldNameOptions();
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
            case DataSourceKind.None:
                EditorGUILayout.HelpBox(
                    "None — no particles (field-only). Particle passes are no-ops; use fields + field passes (e.g. Gray-Scott).",
                    MessageType.Info);
                break;
        }

        EditorGUILayout.PropertyField(simulationSpeedProperty);

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(fieldsProperty, new GUIContent("Fields"), true);

        if (GUILayout.Button("Materialize missing fields from passes"))
        {
            EffectAsset asset = (EffectAsset)target;
            Undo.RecordObject(asset, "Materialize Fields");
            int added = asset.MaterializeMissingFields();
            EditorUtility.SetDirty(asset);
            serializedObject.Update();
            RefreshFieldNameOptions();
            Debug.Log(added > 0
                ? $"Materialized {added} field descriptor(s)."
                : "No missing fields — declarations already cover all pass requests.");
        }

        EditorGUILayout.Space();
        DrawDebugQuadsSection();

        EditorGUILayout.Space();
        passList.DoLayoutList();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawDebugQuadsSection()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Debug Field Quads", EditorStyles.boldLabel);
        if (GUILayout.Button("Refresh", GUILayout.Width(70f)))
        {
            RefreshFieldNameOptions();
        }

        EditorGUILayout.EndHorizontal();

        if (fieldNameOptions.Length == 0)
        {
            EditorGUILayout.HelpBox(
                "No fields declared. Add FieldDescriptors (or Materialize), then Refresh to pick names.",
                MessageType.Info);
        }

        debugQuadList.DoLayoutList();
        EditorGUILayout.HelpBox(
            "Each list entry = one visible debug quad. Remove the entry to hide. " +
            "Mode defaults from channel count; colorScale is per-slot.",
            MessageType.None);
    }

    private void RefreshFieldNameOptions()
    {
        EffectAsset asset = target as EffectAsset;
        if (asset == null || asset.Fields == null)
        {
            fieldNameOptions = Array.Empty<string>();
            return;
        }

        List<string> names = new List<string>();
        for (int i = 0; i < asset.Fields.Count; i++)
        {
            FieldDescriptor descriptor = asset.Fields[i];
            if (descriptor != null && !string.IsNullOrEmpty(descriptor.Name))
            {
                names.Add(descriptor.Name);
            }
        }

        fieldNameOptions = names.ToArray();
    }

    private void DrawDebugQuadElement(Rect rect, int index, bool isActive, bool isFocused)
    {
        SerializedProperty element = debugFieldQuadsProperty.GetArrayElementAtIndex(index);
        SerializedProperty nameProp = element.FindPropertyRelative("fieldName");
        SerializedProperty modeProp = element.FindPropertyRelative("mode");
        SerializedProperty scaleProp = element.FindPropertyRelative("colorScale");

        float line = EditorGUIUtility.singleLineHeight;
        float pad = 2f;
        Rect row1 = new Rect(rect.x, rect.y + pad, rect.width, line);
        Rect row2 = new Rect(rect.x, row1.yMax + pad, rect.width, line);
        Rect row3 = new Rect(rect.x, row2.yMax + pad, rect.width, line);

        DrawFieldNameDropdown(row1, nameProp);
        EditorGUI.PropertyField(row2, modeProp);
        EditorGUI.PropertyField(row3, scaleProp);
    }

    private void DrawFieldNameDropdown(Rect rect, SerializedProperty nameProp)
    {
        if (fieldNameOptions.Length == 0)
        {
            EditorGUI.PropertyField(rect, nameProp, new GUIContent("Field"));
            return;
        }

        string currentName = nameProp.stringValue;
        int current = Array.IndexOf(fieldNameOptions, currentName);
        string[] options = fieldNameOptions;
        if (current < 0 && !string.IsNullOrEmpty(currentName))
        {
            // Keep orphan name visible until Refresh / user picks a declared field.
            options = new string[fieldNameOptions.Length + 1];
            options[0] = currentName + " (missing)";
            Array.Copy(fieldNameOptions, 0, options, 1, fieldNameOptions.Length);
            current = 0;
        }
        else if (current < 0)
        {
            current = 0;
        }

        int next = EditorGUI.Popup(rect, "Field", current, options);
        if (options == fieldNameOptions)
        {
            if (next >= 0 && next < fieldNameOptions.Length)
            {
                ApplyFieldSelection(nameProp, fieldNameOptions[next]);
            }
        }
        else if (next > 0 && next < options.Length)
        {
            // Skip index 0 orphan label — pick a real field.
            ApplyFieldSelection(nameProp, fieldNameOptions[next - 1]);
        }
    }

    private void ApplyFieldSelection(SerializedProperty nameProp, string selected)
    {
        if (string.Equals(nameProp.stringValue, selected, StringComparison.Ordinal))
        {
            return;
        }

        nameProp.stringValue = selected;
        SerializedProperty element = nameProp.serializedObject.FindProperty(
            nameProp.propertyPath.Substring(0, nameProp.propertyPath.LastIndexOf('.')));
        if (element == null)
        {
            return;
        }

        FieldDescriptor descriptor = FindDescriptor((EffectAsset)target, selected);
        if (descriptor == null || (descriptor.ChannelCount != 1 && descriptor.ChannelCount != 2))
        {
            return;
        }

        FieldQuadVisualMode mode = DebugFieldQuadSlot.DefaultModeForChannelCount(descriptor.ChannelCount);
        element.FindPropertyRelative("mode").enumValueIndex = (int)mode;
        element.FindPropertyRelative("colorScale").floatValue = DebugFieldQuadSlot.DefaultScale(mode);
    }

    private static FieldDescriptor FindDescriptor(EffectAsset asset, string name)
    {
        if (asset == null || string.IsNullOrEmpty(name))
        {
            return null;
        }

        for (int i = 0; i < asset.Fields.Count; i++)
        {
            FieldDescriptor descriptor = asset.Fields[i];
            if (descriptor != null && string.Equals(descriptor.Name, name, StringComparison.Ordinal))
            {
                return descriptor;
            }
        }

        return null;
    }

    private void AddDebugQuad(ReorderableList list)
    {
        RefreshFieldNameOptions();
        int index = debugFieldQuadsProperty.arraySize;
        debugFieldQuadsProperty.arraySize++;
        SerializedProperty element = debugFieldQuadsProperty.GetArrayElementAtIndex(index);
        SerializedProperty nameProp = element.FindPropertyRelative("fieldName");
        SerializedProperty modeProp = element.FindPropertyRelative("mode");
        SerializedProperty scaleProp = element.FindPropertyRelative("colorScale");

        string name = fieldNameOptions.Length > 0 ? fieldNameOptions[0] : string.Empty;
        nameProp.stringValue = name;

        FieldDescriptor descriptor = FindDescriptor((EffectAsset)target, name);
        FieldQuadVisualMode mode = descriptor != null && (descriptor.ChannelCount == 1 || descriptor.ChannelCount == 2)
            ? DebugFieldQuadSlot.DefaultModeForChannelCount(descriptor.ChannelCount)
            : FieldQuadVisualMode.VectorRg;
        modeProp.enumValueIndex = (int)mode;
        scaleProp.floatValue = DebugFieldQuadSlot.DefaultScale(mode);
    }

    private float GetPassElementHeight(int index)
    {
        SerializedProperty element = passesProperty.GetArrayElementAtIndex(index);
        return EditorGUI.GetPropertyHeight(element, true) + 6f;
    }

    private void DrawPassElement(Rect rect, int index, bool isActive, bool isFocused)
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

    private void ShowAddPassMenu(Rect buttonRect, ReorderableList list)
    {
        GenericMenu menu = new GenericMenu();
        List<Type> types = new List<Type>();
        foreach (Type type in TypeCache.GetTypesDerivedFrom<SimPass>())
        {
            if (!IsAddablePassType(type))
            {
                continue;
            }

            types.Add(type);
        }

        types.Sort((a, b) =>
        {
            SimPass sa = (SimPass)Activator.CreateInstance(a);
            SimPass sb = (SimPass)Activator.CreateInstance(b);
            int cat = sa.Category.CompareTo(sb.Category);
            return cat != 0 ? cat : string.CompareOrdinal(sa.DisplayName, sb.DisplayName);
        });

        foreach (Type type in types)
        {
            SimPass sample = (SimPass)Activator.CreateInstance(type);
            Type captured = type;
            menu.AddItem(
                new GUIContent($"{sample.Category}/{sample.DisplayName}"),
                false,
                () => AddPass(captured));
        }

        menu.DropDown(buttonRect);
    }

    private static bool IsAddablePassType(Type type)
    {
        if (type == null || type.IsAbstract || type.IsGenericTypeDefinition || type.IsNested)
        {
            return false;
        }

        if (type.Assembly != typeof(SimPass).Assembly)
        {
            return false;
        }

        return type.GetConstructor(Type.EmptyTypes) != null;
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
