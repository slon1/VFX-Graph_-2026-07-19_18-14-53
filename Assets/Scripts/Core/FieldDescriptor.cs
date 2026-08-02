using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

/// <summary>Semantic role of a field — used for Materialize defaults and minimal compatibility checks.</summary>
public enum FieldSemantic
{
    Velocity = 0,
    Dye = 1,
    Scalar = 2,
    Custom = 3,
}

/// <summary>How a pass accesses a field. Declared per FieldRequest.</summary>
public enum FieldAccess
{
    /// <summary>Sample Current as SRV. No swap.</summary>
    Read = 0,

    /// <summary>Read-modify-write Current as UAV. No swap (splat / accumulate).</summary>
    WriteInPlace = 1,

    /// <summary>Read Current (SRV), write Next (UAV). World swaps after the pass.</summary>
    WritePingPong = 2,
}

/// <summary>Identity of a declared field on EffectAsset. Registry key is Name.</summary>
[Serializable]
public struct FieldId : IEquatable<FieldId>
{
    [SerializeField] private string name;
    [SerializeField] private FieldSemantic semantic;

    public string Name => name;
    public FieldSemantic Semantic => semantic;

    public FieldId(string name, FieldSemantic semantic)
    {
        this.name = name;
        this.semantic = semantic;
    }

    public bool Equals(FieldId other) =>
        string.Equals(name, other.name, StringComparison.Ordinal);

    public override bool Equals(object obj) => obj is FieldId other && Equals(other);

    public override int GetHashCode() =>
        name != null ? StringComparer.Ordinal.GetHashCode(name) : 0;

    public override string ToString() => name;
}

/// <summary>
/// EffectAsset-owned declaration of a simulation field resource.
/// Plane basis maps world positions to UV; inject kernels project touches onto it.
/// </summary>
[Serializable]
public sealed class FieldDescriptor
{
    [SerializeField] private FieldId id = new FieldId("velocity", FieldSemantic.Velocity);
    [SerializeField] private GraphicsFormat format = GraphicsFormat.R16G16_SFloat;
    [SerializeField] private Vector2Int resolution = new Vector2Int(256, 256);
    [SerializeField] private Color clearValue = Color.clear;
    [SerializeField] private Vector3 origin = Vector3.zero;
    [SerializeField] private Vector3 axisU = Vector3.right;
    [SerializeField] private Vector3 axisV = Vector3.forward;
    [SerializeField] private Vector2 size = new Vector2(10f, 10f);

    public FieldId Id => id;
    public string Name => id.Name;
    public FieldSemantic Semantic => id.Semantic;
    public GraphicsFormat Format => format;
    public Vector2Int Resolution => resolution;
    public Color ClearValue => clearValue;
    public Vector3 Origin => origin;
    public Vector3 AxisU => axisU;
    public Vector3 AxisV => axisV;
    public Vector2 Size => size;

    public int ChannelCount => GetChannelCount(format);

    public static int GetChannelCount(GraphicsFormat graphicsFormat)
    {
        switch (graphicsFormat)
        {
            case GraphicsFormat.R16_SFloat:
            case GraphicsFormat.R32_SFloat:
            case GraphicsFormat.R8_UNorm:
                return 1;
            case GraphicsFormat.R16G16_SFloat:
            case GraphicsFormat.R32G32_SFloat:
                return 2;
            case GraphicsFormat.R16G16B16A16_SFloat:
            case GraphicsFormat.R32G32B32A32_SFloat:
                return 4;
            default:
                return 4;
        }
    }

    public static FieldDescriptor CreateDefault(string name, FieldSemantic semantic)
    {
        GraphicsFormat fmt = semantic == FieldSemantic.Velocity
            ? GraphicsFormat.R16G16_SFloat
            : GraphicsFormat.R16_SFloat;

        return new FieldDescriptor
        {
            id = new FieldId(name, semantic),
            format = fmt,
            resolution = new Vector2Int(256, 256),
            clearValue = Color.clear,
            origin = Vector3.zero,
            axisU = Vector3.right,
            axisV = Vector3.forward,
            size = new Vector2(10f, 10f),
        };
    }
}

/// <summary>
/// Pass declaration of a field dependency. Compatibility: semantic + channel count
/// (resolution/precision are quality knobs on EffectAsset).
/// For writes the field format must have exactly <see cref="Channels"/> channels
/// (UAV layout must match the kernel declaration); for reads this is a minimum
/// (extra channels are legal to sample).
/// </summary>
[Serializable]
public struct FieldRequest : IEquatable<FieldRequest>
{
    [SerializeField] private string fieldName;
    [SerializeField] private FieldAccess access;
    [SerializeField] private FieldSemantic requiredSemantic;
    [SerializeField] private int channels;

    public string FieldName => fieldName;
    public FieldAccess Access => access;
    public FieldSemantic RequiredSemantic => requiredSemantic;
    public int Channels => channels;

    public FieldRequest(
        string fieldName,
        FieldAccess access,
        FieldSemantic requiredSemantic,
        int channels)
    {
        this.fieldName = fieldName;
        this.access = access;
        this.requiredSemantic = requiredSemantic;
        this.channels = channels;
    }

    /// <summary>
    /// Write UAV layouts need exact channel count; Read may sample fewer channels
    /// from a wider format (e.g. RG from RGBA).
    /// </summary>
    public static bool ChannelsCompatible(FieldAccess access, int requestChannels, int descriptorChannels)
    {
        return access == FieldAccess.Read
            ? descriptorChannels >= requestChannels
            : descriptorChannels == requestChannels;
    }

    public bool Equals(FieldRequest other) =>
        string.Equals(fieldName, other.fieldName, StringComparison.Ordinal) &&
        access == other.access &&
        requiredSemantic == other.requiredSemantic &&
        channels == other.channels;

    public override bool Equals(object obj) => obj is FieldRequest other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            fieldName != null ? StringComparer.Ordinal.GetHashCode(fieldName) : 0,
            (int)access,
            (int)requiredSemantic,
            channels);
}
