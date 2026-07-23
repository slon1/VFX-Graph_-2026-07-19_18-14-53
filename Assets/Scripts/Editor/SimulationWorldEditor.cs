using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SimulationWorld))]
public sealed class SimulationWorldEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            if (GUILayout.Button("Rebuild"))
            {
                ((SimulationWorld)target).Rebuild();
            }
        }

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Rebuild is available in Play Mode: re-runs the source and re-initializes passes " +
                "after changing the effect or adding passes.",
                MessageType.Info);
        }
    }
}
