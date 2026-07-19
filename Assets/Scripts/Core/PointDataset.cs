using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class PointDataset : IDisposable
{
    private readonly AttributeSchema schema = new AttributeSchema();
    private readonly Dictionary<AttributeId, GraphicsBuffer> buffers =
        new Dictionary<AttributeId, GraphicsBuffer>();

    public int Count { get; private set; }
    public int Capacity { get; private set; }

    /// <summary>Read-only view. Mutations only via RegisterAttribute / Dispose.</summary>
    public AttributeSchema Schema => schema;

    public void EnsureCapacity(int capacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (capacity > Capacity)
        {
            Capacity = capacity;
        }

        Count = capacity;
    }

    public GraphicsBuffer Get(AttributeId id)
    {
        if (!buffers.TryGetValue(id, out GraphicsBuffer buffer))
        {
            throw new KeyNotFoundException($"Attribute '{id}' is not present in the dataset.");
        }

        return buffer;
    }

    public bool TryGet(AttributeId id, out GraphicsBuffer buffer)
    {
        return buffers.TryGetValue(id, out buffer);
    }

    public bool TryGetDescriptor(AttributeId id, out AttributeDescriptor descriptor)
    {
        return schema.TryGet(id, out descriptor);
    }

    /// <summary>
    /// Atomically registers descriptor + GraphicsBuffer, or returns the existing buffer.
    /// Never leaves schema/buffers half-updated.
    /// </summary>
    public GraphicsBuffer RegisterAttribute(
        AttributeId id,
        GraphicsBuffer.Target target = GraphicsBuffer.Target.Structured)
    {
        if (Count <= 0)
        {
            throw new InvalidOperationException("Call EnsureCapacity before RegisterAttribute.");
        }

        if (buffers.TryGetValue(id, out GraphicsBuffer existing))
        {
            if (schema.TryGet(id, out AttributeDescriptor existingDescriptor) &&
                existingDescriptor.Target != target)
            {
                throw new InvalidOperationException(
                    $"Attribute '{id}' already exists with Target={existingDescriptor.Target}, requested {target}.");
            }

            return existing;
        }

        AttributeDescriptor descriptor = new AttributeDescriptor(id, target);
        GraphicsBuffer buffer = new GraphicsBuffer(descriptor.Target, Count, descriptor.Stride);

        schema.Add(descriptor);
        buffers.Add(id, buffer);
        return buffer;
    }

    public void Dispose()
    {
        foreach (KeyValuePair<AttributeId, GraphicsBuffer> pair in buffers)
        {
            pair.Value.Release();
        }

        buffers.Clear();
        schema.Clear();
        Count = 0;
        Capacity = 0;
    }
}
