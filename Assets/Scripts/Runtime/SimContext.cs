using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One active touch/pointer converted to world space. Layout must match Touch.hlsl.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TouchForce
{
    public Vector3 Position;
    public Vector3 Delta;
    public float Radius;
    public float Strength;

    public const int Stride = 8 * sizeof(float);
}

public readonly struct KernelHandle
{
    public readonly ComputeShader Shader;
    public readonly int Index;

    public KernelHandle(ComputeShader shader, int index)
    {
        Shader = shader;
        Index = index;
    }

    public bool IsValid => Shader != null;
}

/// <summary>
/// Everything a SimPass needs at runtime: particle data, kernel lookup,
/// touch input and the frame CommandBuffer. Owned by SimulationWorld.
/// </summary>
public sealed class SimContext
{
    private readonly ComputeShader[] shaders;

    public ParticleSet Particles { get; }
    public GraphicsBuffer TouchBuffer { get; }
    public int TouchCount { get; internal set; }
    public CommandBuffer Cmd { get; internal set; }

    /// <summary>Accumulated simulation time (scaled by EffectAsset.SimulationSpeed).</summary>
    public float Time { get; internal set; }

    public SimContext(ParticleSet particles, ComputeShader[] shaders, GraphicsBuffer touchBuffer)
    {
        Particles = particles;
        this.shaders = shaders;
        TouchBuffer = touchBuffer;
    }

    /// <summary>Finds a kernel by name across the whole pass library.</summary>
    public KernelHandle FindKernel(string kernelName)
    {
        for (int i = 0; i < shaders.Length; i++)
        {
            ComputeShader shader = shaders[i];
            if (shader != null && shader.HasKernel(kernelName))
            {
                return new KernelHandle(shader, shader.FindKernel(kernelName));
            }
        }

        throw new InvalidOperationException(
            $"Kernel '{kernelName}' not found in any compute shader of the pass library.");
    }
}
