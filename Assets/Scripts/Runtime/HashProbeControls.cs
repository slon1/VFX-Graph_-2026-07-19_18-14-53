using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Play-mode FPS probe for the spatial hash (ADR-034 P1). Not a simulation pass.
/// </summary>
public sealed class HashProbeControls : MonoBehaviour
{
    [SerializeField] private SimulationWorld world;
    [SerializeField] private string label = "30k";

    private readonly List<BuildSpatialHashPass> disabledByProbe = new List<BuildSpatialHashPass>();
    private float ema = 1f / 60f;
    private bool emaReady;

    private void OnEnable()
    {
        Application.targetFrameRate = 120;
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;
        ema = emaReady ? Mathf.Lerp(ema, dt, 0.05f) : dt;
        emaReady = true;
    }

    private void OnGUI()
    {
        GUI.matrix = Matrix4x4.Scale(Vector3.one * (Screen.height / 360f));
        bool hashOn = HashEnabled();
        string line = string.Format(
            "{0}  {1:F1} FPS  {2:F2} ms  hash:{3}  render:{4}",
            label,
            ema > 1e-8f ? 1f / ema : 0f,
            ema * 1000f,
            hashOn ? "on" : "off",
            world != null && world.RenderParticles ? "on" : "off");
        GUI.Label(new Rect(16f, 16f, 680f, 28f), line);
        if (GUI.Button(new Rect(16f, 48f, 120f, 32f), "Hash"))
        {
            ToggleHash();
        }

        if (GUI.Button(new Rect(148f, 48f, 120f, 32f), "Render") && world != null)
        {
            world.RenderParticles = !world.RenderParticles;
        }
    }

    private void OnDisable()
    {
        for (int i = 0; i < disabledByProbe.Count; i++)
        {
            if (disabledByProbe[i] != null)
            {
                disabledByProbe[i].Enabled = true;
            }
        }

        disabledByProbe.Clear();
    }

    private bool HashEnabled()
    {
        if (world == null || world.Effect == null)
        {
            return false;
        }

        IReadOnlyList<SimPass> passes = world.Effect.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is BuildSpatialHashPass builder && builder.Enabled)
            {
                return true;
            }
        }

        return false;
    }

    private void ToggleHash()
    {
        if (world == null || world.Effect == null)
        {
            return;
        }

        IReadOnlyList<SimPass> passes = world.Effect.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is not BuildSpatialHashPass builder)
            {
                continue;
            }

            builder.Enabled = !builder.Enabled;
            if (!builder.Enabled)
            {
                if (!disabledByProbe.Contains(builder))
                {
                    disabledByProbe.Add(builder);
                }
            }
            else
            {
                disabledByProbe.Remove(builder);
            }
        }
    }
}
