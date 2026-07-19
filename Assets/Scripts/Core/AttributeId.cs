using System;

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
    Float1 = 4,
    Float3 = 12,
    Float4 = 16,
    UInt = 4
}
