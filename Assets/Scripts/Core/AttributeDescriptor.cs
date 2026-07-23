using UnityEngine;

public sealed class AttributeDescriptor
{
    public AttributeId Id { get; }
    public AttributeType Type { get; }
    public int Stride { get; }
    public GraphicsBuffer.Target Target { get; }

    public AttributeDescriptor(
        AttributeId id,
        GraphicsBuffer.Target target = GraphicsBuffer.Target.Structured)
    {
        Id = id;
        Type = id.Type;
        Stride = id.Type.GetStride();
        Target = target;
    }

    public string Name => Id.Name;
}
