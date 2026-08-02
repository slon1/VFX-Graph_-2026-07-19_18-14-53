using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>One allocated field: dual RenderTextures with Current/Next ping-pong.</summary>
public sealed class SimField : IDisposable
{
    private RenderTexture textureA;
    private RenderTexture textureB;
    private bool currentIsA = true;

    public FieldDescriptor Descriptor { get; }
    public string Name => Descriptor.Name;

    public RenderTexture Current => currentIsA ? textureA : textureB;
    public RenderTexture Next => currentIsA ? textureB : textureA;

    public SimField(FieldDescriptor descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));

        GraphicsFormat format = descriptor.Format;
        if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.LoadStore))
        {
            throw new InvalidOperationException(
                $"Field '{descriptor.Name}': format {format} is not supported for LoadStore on this device.");
        }

        Vector2Int res = descriptor.Resolution;
        if (res.x < 1 || res.y < 1)
        {
            throw new InvalidOperationException(
                $"Field '{descriptor.Name}': resolution must be >= 1, got {res}.");
        }

        textureA = CreateRt(descriptor, "A");
        textureB = CreateRt(descriptor, "B");
    }

    public void Swap()
    {
        currentIsA = !currentIsA;
    }

    /// <summary>Clear both ping-pong textures to the descriptor clear value.</summary>
    public void ClearBoth(CommandBuffer cmd)
    {
        Color clear = Descriptor.ClearValue;
        ClearOne(cmd, textureA, clear);
        ClearOne(cmd, textureB, clear);
    }

    /// <summary>Clear Current to the descriptor clear value (per-frame accumulator reset).</summary>
    public void ClearCurrent(CommandBuffer cmd)
    {
        ClearOne(cmd, Current, Descriptor.ClearValue);
    }

    public void Dispose()
    {
        Release(ref textureA);
        Release(ref textureB);
    }

    private static RenderTexture CreateRt(FieldDescriptor descriptor, string suffix)
    {
        RenderTexture rt = new RenderTexture(
            descriptor.Resolution.x,
            descriptor.Resolution.y,
            0,
            descriptor.Format)
        {
            name = $"M3D_{descriptor.Name}_{suffix}",
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false,
        };
        rt.Create();
        return rt;
    }

    private static void ClearOne(CommandBuffer cmd, RenderTexture rt, Color clear)
    {
        cmd.SetRenderTarget(rt);
        cmd.ClearRenderTarget(false, true, clear);
    }

    private static void Release(ref RenderTexture rt)
    {
        if (rt == null)
        {
            return;
        }

        rt.Release();
        UnityEngine.Object.Destroy(rt);
        rt = null;
    }
}

/// <summary>
/// Registry of allocated fields for one SimulationWorld lifetime.
/// Allocated only from EffectAsset field declarations.
/// </summary>
public sealed class FieldSet : IDisposable
{
    private readonly Dictionary<string, SimField> fields =
        new Dictionary<string, SimField>(StringComparer.Ordinal);
    private readonly Dictionary<string, FieldAccumBuffer> accumBuffers =
        new Dictionary<string, FieldAccumBuffer>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, SimField> Fields => fields;

    public void Allocate(IReadOnlyList<FieldDescriptor> descriptors, CommandBuffer clearCmd)
    {
        if (descriptors == null)
        {
            return;
        }

        for (int i = 0; i < descriptors.Count; i++)
        {
            FieldDescriptor descriptor = descriptors[i];
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Name))
            {
                throw new InvalidOperationException(
                    $"FieldSet: descriptor at index {i} has empty name.");
            }

            if (fields.ContainsKey(descriptor.Name))
            {
                throw new InvalidOperationException(
                    $"FieldSet: duplicate field name '{descriptor.Name}'.");
            }

            SimField field = new SimField(descriptor);
            field.ClearBoth(clearCmd);
            fields.Add(descriptor.Name, field);
        }
    }

    public SimField Get(string name)
    {
        if (!fields.TryGetValue(name, out SimField field))
        {
            throw new KeyNotFoundException($"Field '{name}' is not present in the FieldSet.");
        }

        return field;
    }

    public bool TryGet(string name, out SimField field) => fields.TryGetValue(name, out field);

    public void Swap(string name) => Get(name).Swap();

    /// <summary>
    /// Allocates or returns the P2G accum buffer for a declared field.
    /// valueChannels must match the field descriptor (validated by Build before call).
    /// </summary>
    public FieldAccumBuffer GetOrCreateAccumBuffer(FieldDescriptor descriptor, int valueChannels)
    {
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        if (!fields.ContainsKey(descriptor.Name))
        {
            throw new InvalidOperationException(
                $"FieldSet: cannot create accum buffer for undeclared field '{descriptor.Name}'.");
        }

        if (accumBuffers.TryGetValue(descriptor.Name, out FieldAccumBuffer existing))
        {
            if (existing.Channels != valueChannels)
            {
                throw new InvalidOperationException(
                    $"FieldSet: accum buffer for '{descriptor.Name}' already exists with " +
                    $"Channels={existing.Channels}, requested {valueChannels}.");
            }

            return existing;
        }

        FieldAccumBuffer created = new FieldAccumBuffer(descriptor.Resolution, valueChannels);
        accumBuffers.Add(descriptor.Name, created);
        return created;
    }

    public FieldAccumBuffer GetAccumBuffer(string name)
    {
        if (!accumBuffers.TryGetValue(name, out FieldAccumBuffer buffer))
        {
            throw new KeyNotFoundException(
                $"Field accum buffer '{name}' is not present. Ensure a P2G Clear/Scatter/Normalize pass declares it.");
        }

        return buffer;
    }

    public bool TryGetAccumBuffer(string name, out FieldAccumBuffer buffer) =>
        accumBuffers.TryGetValue(name, out buffer);

    public void Dispose()
    {
        foreach (KeyValuePair<string, FieldAccumBuffer> pair in accumBuffers)
        {
            pair.Value.Dispose();
        }

        accumBuffers.Clear();

        foreach (KeyValuePair<string, SimField> pair in fields)
        {
            pair.Value.Dispose();
        }

        fields.Clear();
    }
}
