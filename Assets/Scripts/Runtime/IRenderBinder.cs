using UnityEngine;

/// <summary>
/// Present-step service: binds simulation resources to renderers after the sim CommandBuffer.
/// Not a simulation resource — lives outside the resource registry.
/// </summary>
public interface IRenderBinder
{
    void Initialize(SimContext context);
    void Execute(SimContext context);
}
