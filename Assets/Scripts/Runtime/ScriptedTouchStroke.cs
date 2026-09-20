using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// F2.3 scripted vertical stroke in field UV (not a camera ray).
/// Clock starts on the first InputRouter.Sample after World is built.
/// </summary>
public sealed class ScriptedTouchStroke : MonoBehaviour
{
    [SerializeField] private Vector2 startUV = new Vector2(0.5f, 0.25f);
    [SerializeField] private Vector2 endUV = new Vector2(0.5f, 0.75f);
    [SerializeField, Min(0.01f)] private float duration = 0.5f;

    private SimulationWorld world;
    private bool started;
    private float elapsed;
    private bool hasPrevious;
    private Vector3 previousWorld;

    public Vector2 StartUV
    {
        get => startUV;
        set => startUV = value;
    }

    public Vector2 EndUV
    {
        get => endUV;
        set => endUV = value;
    }

    public float Duration
    {
        get => duration;
        set => duration = value;
    }

    private void OnEnable()
    {
        world = GetComponent<SimulationWorld>();
        started = false;
        elapsed = 0f;
        hasPrevious = false;
    }

    /// <summary>
    /// Field UV → world, same formula as TouchInjectVelocity.
    /// </summary>
    public static Vector3 UvToWorld(
        Vector2 uv, Vector3 origin, Vector3 axisU, Vector3 axisV, Vector2 size)
    {
        return origin
            + axisU * ((uv.x - 0.5f) * size.x)
            + axisV * ((uv.y - 0.5f) * size.y);
    }

    /// <summary>
    /// Time-parameterized sample. Position depends only on t, not on frame count.
    /// Delta is zero at t=0; callers that need a motion delta pass previous themselves.
    /// </summary>
    public static ScriptedTouchSample Evaluate(
        float elapsedSeconds, float durationSeconds, Vector3 startWorld, Vector3 endWorld)
    {
        if (durationSeconds <= 0f || elapsedSeconds >= durationSeconds)
        {
            return new ScriptedTouchSample(0, endWorld, Vector3.zero);
        }

        float u = elapsedSeconds / durationSeconds;
        Vector3 position = Vector3.Lerp(startWorld, endWorld, u);
        Vector3 delta = elapsedSeconds <= 0f ? Vector3.zero : position - startWorld;
        return new ScriptedTouchSample(1, position, delta);
    }

    /// <summary>
    /// Called from InputRouter.Sample. First call is t=0 / Delta=0; then t += Time.deltaTime.
    /// Returns 0 after Duration; mouse stays ignored while this component is enabled.
    /// </summary>
    public int Sample(out Vector3 position, out Vector3 delta)
    {
        position = Vector3.zero;
        delta = Vector3.zero;

        if (!TryGetPlane(out Vector3 origin, out Vector3 axisU, out Vector3 axisV, out Vector2 size))
        {
            return 0;
        }

        Vector3 startWorld = UvToWorld(startUV, origin, axisU, axisV, size);
        Vector3 endWorld = UvToWorld(endUV, origin, axisU, axisV, size);

        if (!started)
        {
            started = true;
            elapsed = 0f;
        }
        else
        {
            elapsed += Time.deltaTime;
        }

        ScriptedTouchSample sample = Evaluate(elapsed, duration, startWorld, endWorld);
        if (sample.Count == 0)
        {
            return 0;
        }

        position = sample.Position;
        delta = hasPrevious ? position - previousWorld : Vector3.zero;
        previousWorld = position;
        hasPrevious = true;
        return 1;
    }

    private bool TryGetPlane(
        out Vector3 origin, out Vector3 axisU, out Vector3 axisV, out Vector2 size)
    {
        origin = Vector3.zero;
        axisU = Vector3.right;
        axisV = Vector3.forward;
        size = Vector2.zero;

        if (world == null)
        {
            world = GetComponent<SimulationWorld>();
        }

        EffectAsset effect = world != null ? world.Effect : null;
        if (effect == null)
        {
            return false;
        }

        FieldDescriptor plane = FindPlane(effect, "dye");
        if (plane == null)
        {
            plane = FindPlane(effect, "velocity");
        }

        if (plane == null)
        {
            return false;
        }

        origin = plane.Origin;
        axisU = plane.AxisU;
        axisV = plane.AxisV;
        size = plane.Size;
        return true;
    }

    private static FieldDescriptor FindPlane(EffectAsset effect, string name)
    {
        IReadOnlyList<FieldDescriptor> fields = effect.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            FieldDescriptor field = fields[i];
            if (field != null && field.Name == name)
            {
                return field;
            }
        }

        return null;
    }
}

public readonly struct ScriptedTouchSample
{
    public readonly int Count;
    public readonly Vector3 Position;
    public readonly Vector3 Delta;

    public ScriptedTouchSample(int count, Vector3 position, Vector3 delta)
    {
        Count = count;
        Position = position;
        Delta = delta;
    }
}
