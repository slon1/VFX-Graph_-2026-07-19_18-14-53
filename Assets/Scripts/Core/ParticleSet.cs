using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// SoA particle storage: one GraphicsBuffer per attribute, schema kept in sync
/// via RegisterAttribute. Owned by SimulationWorld; sources fill it, passes transform it.
/// </summary>
public sealed class ParticleSet : IDisposable
{
    private readonly AttributeSchema schema = new AttributeSchema();
    private readonly Dictionary<AttributeId, GraphicsBuffer> buffers =
        new Dictionary<AttributeId, GraphicsBuffer>();

    public int Count { get; private set; }
    public int Capacity { get; private set; }

    /// <summary>Read-only view. Mutations only via RegisterAttribute / Dispose.</summary>
    public AttributeSchema Schema => schema;

    /// <summary>
    /// Sets Count and grows Capacity. Buffers are allocated with Capacity elements,
    /// so growing beyond Capacity after any buffer was created is an error:
    /// existing GraphicsBuffers cannot be resized in place (VFX/compute keep references).
    /// Dispose and rebuild the set instead.
    /// </summary>
    public void EnsureCapacity(int capacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (capacity > Capacity)
        {
            if (buffers.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Cannot grow capacity from {Capacity} to {capacity}: buffers already exist. " +
                    "Dispose the particle set and set it up again.");
            }

            Capacity = capacity;
        }

        Count = capacity;
    }

    public GraphicsBuffer Get(AttributeId id)
    {
        if (!buffers.TryGetValue(id, out GraphicsBuffer buffer))
        {
            throw new KeyNotFoundException($"Attribute '{id}' is not present in the particle set.");
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
    /// Validates Type/Target on re-registration. Never leaves schema/buffers half-updated.
    /// Buffers always get CopySource/CopyDestination so Graphics.CopyBuffer works
    /// (e.g. one-time restPosition -> position init).
    /// </summary>
    public GraphicsBuffer RegisterAttribute(
        AttributeId id,
        GraphicsBuffer.Target target = GraphicsBuffer.Target.Structured)
    {
        if (Capacity <= 0)
        {
            throw new InvalidOperationException("Call EnsureCapacity before RegisterAttribute.");
        }

        if (buffers.TryGetValue(id, out GraphicsBuffer existing))
        {
            schema.TryGet(id, out AttributeDescriptor existingDescriptor);

            if (existingDescriptor.Type != id.Type)
            {
                throw new InvalidOperationException(
                    $"Attribute '{id}' already exists with Type={existingDescriptor.Type}, requested {id.Type}.");
            }

            if (existingDescriptor.Target != target)
            {
                throw new InvalidOperationException(
                    $"Attribute '{id}' already exists with Target={existingDescriptor.Target}, requested {target}.");
            }

            return existing;
        }

        AttributeDescriptor descriptor = new AttributeDescriptor(id, target);
        GraphicsBuffer.Target bufferTarget = descriptor.Target |
            GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
        GraphicsBuffer buffer = new GraphicsBuffer(bufferTarget, Capacity, descriptor.Stride);

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
