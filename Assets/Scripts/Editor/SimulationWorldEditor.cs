using System.Collections.Generic;
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

    [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
    private static void DrawSwarmPoints(SimulationWorld world, GizmoType gizmoType)
    {
        if (world == null || (gizmoType & (GizmoType.Selected | GizmoType.Active)) == 0)
        {
            return;
        }

        EffectAsset effect = world.Effect;
        if (effect == null)
        {
            return;
        }

        IReadOnlyList<SwarmPoint> points = effect.SwarmPoints;
        for (int i = 0; i < points.Count; i++)
        {
            SwarmPoint point = points[i];
            if (point == null)
            {
                continue;
            }

            Vector2 xz = point.PositionXZ;
            Vector3 center = new Vector3(xz.x, 0f, xz.y);
            Color color = point.Weight < 0f ? Color.red : Color.green;
            Gizmos.color = color;
            float marker = HandleUtility.GetHandleSize(center) * 0.08f;
            Gizmos.DrawSphere(center, marker);
            if (point.Radius <= 0f)
            {
                continue;
            }

            Handles.color = color;
            Handles.DrawWireDisc(center, Vector3.up, point.Radius);
            float plateau = point.Radius * (1f - point.Edge);
            if (plateau > 0f && plateau < point.Radius)
            {
                Handles.DrawWireDisc(center, Vector3.up, plateau);
            }
        }
    }
}
