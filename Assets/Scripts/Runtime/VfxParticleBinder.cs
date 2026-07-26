using UnityEngine;
using UnityEngine.VFX;

/// <summary>
/// Binds ParticleSet.Position to VFX Graph once at Initialize. Execute is a no-op —
/// particle buffers are stable for the world's lifetime.
/// </summary>
public sealed class VfxParticleBinder : IRenderBinder
{
    private const string PositionBufferPropertyName = "PositionBuffer";
    private const string SpawnCountPropertyName = "SpawnCount";
    private const int VfxCapacity = 1_000_000;

    private readonly VisualEffect visualEffect;

    public VfxParticleBinder(VisualEffect visualEffect)
    {
        this.visualEffect = visualEffect;
    }

    public void Initialize(SimContext context)
    {
        GraphicsBuffer positions = context.Particles.Get(BuiltinAttributes.Position);
        visualEffect.SetGraphicsBuffer(PositionBufferPropertyName, positions);

        if (visualEffect.HasFloat(SpawnCountPropertyName))
        {
            visualEffect.SetFloat(SpawnCountPropertyName, context.Particles.Count);
        }

        if (context.Particles.Count > VfxCapacity)
        {
            Debug.LogWarning(
                $"VfxParticleBinder: {context.Particles.Count} points exceed VFX capacity {VfxCapacity}; " +
                "extra points will not be rendered.");
        }

        visualEffect.Reinit();
    }

    public void Execute(SimContext context)
    {
        // Particle GraphicsBuffers are stable until Teardown — nothing to rebind per frame.
    }
}
