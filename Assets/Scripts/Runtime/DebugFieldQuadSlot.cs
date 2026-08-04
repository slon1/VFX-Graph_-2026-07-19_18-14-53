using System;
using UnityEngine;

/// <summary>How a debug field quad maps texels to color (shader _VisualMode).</summary>
public enum FieldQuadVisualMode
{
    /// <summary>2ch RG → chroma; |v| → alpha. Shader mode 0.</summary>
    VectorRg = 0,
    /// <summary>1ch R → warm heatmap. Shader mode 1.</summary>
    ScalarHeatmap = 1,
}

/// <summary>
/// One enabled debug visualization slot on an EffectAsset.
/// Presence in the list = enabled (no separate toggle).
/// </summary>
[Serializable]
public struct DebugFieldQuadSlot
{
    public string fieldName;
    public FieldQuadVisualMode mode;
    [Min(0f)] public float colorScale;

    public static float DefaultScale(FieldQuadVisualMode mode) =>
        mode == FieldQuadVisualMode.ScalarHeatmap ? 1f : 2f;

    public static FieldQuadVisualMode DefaultModeForChannelCount(int channelCount)
    {
        return channelCount == 1
            ? FieldQuadVisualMode.ScalarHeatmap
            : FieldQuadVisualMode.VectorRg;
    }

    public static DebugFieldQuadSlot Create(string fieldName, FieldQuadVisualMode mode)
    {
        return new DebugFieldQuadSlot
        {
            fieldName = fieldName,
            mode = mode,
            colorScale = DefaultScale(mode),
        };
    }

    public static DebugFieldQuadSlot ForDescriptor(FieldDescriptor descriptor)
    {
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        FieldQuadVisualMode mode = DefaultModeForChannelCount(descriptor.ChannelCount);
        return Create(descriptor.Name, mode);
    }

    public static DebugFieldQuadSlot Velocity(string fieldName = "velocity") =>
        Create(fieldName, FieldQuadVisualMode.VectorRg);

    public static DebugFieldQuadSlot Density(string fieldName = "density") =>
        Create(fieldName, FieldQuadVisualMode.ScalarHeatmap);
}
