using System;
using UnityEngine;

/// <summary>How a debug field quad maps texels to color (shader _VisualMode).</summary>
public enum FieldQuadVisualMode
{
    /// <summary>2ch RG → chroma; |v| → alpha. Shader mode 0.</summary>
    VectorRg = 0,
    /// <summary>1ch R → LUT heatmap. Shader mode 1.</summary>
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
    /// <summary>ScalarHeatmap palette (LDR stops). Null on old assets → runtime DefaultFireGradient.</summary>
    public Gradient lut;
    /// <summary>HDR multiply after LUT sample; does not affect alpha. 1 = no extra boost.</summary>
    [Min(0f)] public float hdrIntensity;

    public static float DefaultScale(FieldQuadVisualMode mode) =>
        mode == FieldQuadVisualMode.ScalarHeatmap ? 1f : 2f;

    public static FieldQuadVisualMode DefaultModeForChannelCount(int channelCount)
    {
        return channelCount == 1
            ? FieldQuadVisualMode.ScalarHeatmap
            : FieldQuadVisualMode.VectorRg;
    }

    /// <summary>
    /// Fire-like palette: black → dark red → orange → yellow-white.
    /// Always returns a <b>new</b> Gradient (mutable; never cache a shared instance).
    /// </summary>
    public static Gradient DefaultFireGradient()
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(Color.black, 0f),
                new GradientColorKey(new Color(0.45f, 0.02f, 0f), 0.3f),
                new GradientColorKey(new Color(1f, 0.35f, 0f), 0.6f),
                new GradientColorKey(new Color(1f, 0.95f, 0.55f), 1f),
            },
            new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f),
            });
        return gradient;
    }

    public static DebugFieldQuadSlot Create(string fieldName, FieldQuadVisualMode mode)
    {
        return new DebugFieldQuadSlot
        {
            fieldName = fieldName,
            mode = mode,
            colorScale = DefaultScale(mode),
            lut = DefaultFireGradient(),
            hdrIntensity = 1f,
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
