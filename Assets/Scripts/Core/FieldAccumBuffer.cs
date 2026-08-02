using System;
using UnityEngine;

/// <summary>
/// Companion uint accumulation buffer for atomic P2G scatter into a field.
/// Layout per texel: [value0..valueChannels-1][count] — count is always last.
/// BufferCount = Channels + 1. Lazily allocated by FieldSet; not declared on EffectAsset.
/// </summary>
public sealed class FieldAccumBuffer : IDisposable
{
    public GraphicsBuffer Buffer { get; }
    public int Channels { get; }
    public int BufferCount => Channels + 1;
    public Vector2Int Resolution { get; }
    public int ElementCount { get; }

    public FieldAccumBuffer(Vector2Int resolution, int valueChannels)
    {
        if (resolution.x < 1 || resolution.y < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Resolution must be >= 1.");
        }

        if (valueChannels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(valueChannels), valueChannels, "Channels must be >= 1.");
        }

        Resolution = resolution;
        Channels = valueChannels;
        ElementCount = resolution.x * resolution.y * BufferCount;
        Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, ElementCount, sizeof(uint));
    }

    public void Dispose()
    {
        Buffer?.Release();
    }
}
