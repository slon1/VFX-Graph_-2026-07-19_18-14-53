using System.Collections.Generic;

public sealed class AttributeSchema
{
    private readonly List<AttributeDescriptor> attributes = new List<AttributeDescriptor>();
    private readonly Dictionary<AttributeId, AttributeDescriptor> byId =
        new Dictionary<AttributeId, AttributeDescriptor>();

    public IReadOnlyList<AttributeDescriptor> Attributes => attributes;

    public bool Has(AttributeId id)
    {
        return byId.ContainsKey(id);
    }

    public bool TryGet(AttributeId id, out AttributeDescriptor descriptor)
    {
        return byId.TryGetValue(id, out descriptor);
    }

    internal void Add(AttributeDescriptor descriptor)
    {
        if (byId.ContainsKey(descriptor.Id))
        {
            throw new System.InvalidOperationException($"Attribute '{descriptor.Id}' already exists in schema.");
        }

        attributes.Add(descriptor);
        byId.Add(descriptor.Id, descriptor);
    }

    internal void Clear()
    {
        attributes.Clear();
        byId.Clear();
    }
}
