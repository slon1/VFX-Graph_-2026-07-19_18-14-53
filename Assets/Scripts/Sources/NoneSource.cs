using System;

/// <summary>
/// Field-only source: zero particles. Use for Gray-Scott and other grid simulations
/// that do not need a ParticleSet.
/// </summary>
[Serializable]
public sealed class NoneSource : IDataSource
{
    public string Name => "None";

    public void Setup(ParticleSet particles)
    {
        particles.EnsureCapacity(0);
    }

    public void Tick(ParticleSet particles)
    {
    }
}
