using System;

/// <summary>
/// Identity is the Name only (one attribute name = one buffer).
/// Type consistency is enforced by ParticleSet.RegisterAttribute.
/// </summary>
public readonly struct AttributeId : IEquatable<AttributeId>
{
    public string Name { get; }
    public AttributeType Type { get; }
    public bool IsBuiltin { get; }

    public AttributeId(string name, AttributeType type, bool isBuiltin)
    {
        Name = name;
        Type = type;
        IsBuiltin = isBuiltin;
    }

    public static AttributeId Custom(string name, AttributeType type)
    {
        return new AttributeId(name, type, false);
    }

    public bool Equals(AttributeId other)
    {
        return string.Equals(Name, other.Name, StringComparison.Ordinal);
    }

    public override bool Equals(object obj)
    {
        return obj is AttributeId other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Name != null ? StringComparer.Ordinal.GetHashCode(Name) : 0;
    }

    public override string ToString()
    {
        return Name;
    }
}

public enum AttributeType
{
    Float1 = 0,
    Float3 = 1,
    Float4 = 2,
    UInt = 3,
}

public static class AttributeTypeExtensions
{
    public static int GetStride(this AttributeType type)
    {
        switch (type)
        {
            case AttributeType.Float1:
                return sizeof(float);
            case AttributeType.Float3:
                return sizeof(float) * 3;
            case AttributeType.Float4:
                return sizeof(float) * 4;
            case AttributeType.UInt:
                return sizeof(uint);
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown attribute type.");
        }
    }
}
