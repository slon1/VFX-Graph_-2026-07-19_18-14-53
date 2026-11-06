using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Neighbor-field buffers for one world (ADR-061 §2.1–2.4). Not a <see cref="FieldSet"/>.
/// Owned by <see cref="FieldNeighborCollectPass"/>.
/// </summary>
public sealed class FieldNeighborSet : IDisposable
{
    public const int FixedPointScale = 1 << 14;
    public const int ChannelsPerTexel = 6;
    public const int FieldFloatsPerTexel = 16;

    private static readonly int HashOriginId = Shader.PropertyToID("HashOrigin");
    private static readonly int HashSizeId = Shader.PropertyToID("HashSize");
    private static readonly int HashCellSizeId = Shader.PropertyToID("HashCellSize");
    private static readonly int HashResId = Shader.PropertyToID("HashRes");
    private static readonly int HashWrapId = Shader.PropertyToID("HashWrap");
    private static readonly int HashCellCountId = Shader.PropertyToID("HashCellCount");
    private static readonly int FieldRtId = Shader.PropertyToID("FieldRt");
    private static readonly int FieldRId = Shader.PropertyToID("FieldR");
    private static readonly int FieldEpsId = Shader.PropertyToID("FieldEps");

    public SpatialHashLayout Layout { get; }
    public int ParticleCount { get; }
    public bool Scatter { get; }

    /// <summary>6 floats per texel: team 0 (N, Hx, Hz), team 1 (N, Hx, Hz).</summary>
    internal GraphicsBuffer Raw { get; private set; }

    /// <summary>6 uints per texel, fixed point 2^14. Null unless <see cref="Scatter"/>.</summary>
    internal GraphicsBuffer Accum { get; private set; }

    /// <summary>Phi of the two teams, one float2 per texel.</summary>
    internal GraphicsBuffer Phi { get; private set; }

    /// <summary>Four float4 per texel, 64 bytes (ADR-061 §2.4).</summary>
    internal GraphicsBuffer Field { get; private set; }

    public bool RefreshedThisFrame { get; internal set; }

    /// <summary>Execute index of the last refresh. 0 before the first one.</summary>
    public int Frame { get; internal set; }

    public FieldNeighborSet(SpatialHashLayout layout, int particleCount, bool scatter)
    {
        if (particleCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(particleCount), particleCount, "particleCount must be >= 0.");
        }

        SpatialHashSet.ValidateLayout(layout, wrap: true);
        Layout = layout;
        ParticleCount = particleCount;
        Scatter = scatter;

        int cells = layout.CellCount;
        Raw = NewFloat(cells * ChannelsPerTexel);
        Phi = NewFloat2(cells);
        Field = NewFloat4(cells * 4);
        if (scatter)
        {
            Accum = NewUint(cells * ChannelsPerTexel);
        }
    }

    private float teamRadius;

    /// <summary>Teams[0].Radii.x, read once. The play path uploads that slot before Execute.</summary>
    internal float TeamRadius(GraphicsBuffer teams)
    {
        if (teamRadius > 0f)
        {
            return teamRadius;
        }

        if (teams == null)
        {
            throw new InvalidOperationException("Field neighbor radius needs a team buffer.");
        }

        var slot = new TeamParams[1];
        teams.GetData(slot, 0, 0, 1);
        teamRadius = slot[0].Radii.x;
        if (!(teamRadius > 0f) || float.IsNaN(teamRadius) || float.IsInfinity(teamRadius))
        {
            throw new InvalidOperationException(
                "Field neighbor radius needs Teams[0].Radii.x > 0, found " + teamRadius + ".");
        }

        return teamRadius;
    }

    /// <summary>R divided by the real layout cell, not the nominal TexelSize.</summary>
    internal float RadiusTexels(GraphicsBuffer teams)
    {
        float cell = Layout.CellSize.x;
        float radius = TeamRadius(teams);
        if (!(cell > 0f))
        {
            throw new InvalidOperationException(
                "Field neighbor radius needs a positive cell size, found " + cell + ".");
        }

        return radius / cell;
    }

    internal void PushParams(CommandBuffer cmd, ComputeShader shader, float radiusTexels, float radius)
    {
        SpatialHashLayout layout = Layout;
        cmd.SetComputeVectorParam(shader, HashOriginId, new Vector4(layout.Origin.x, layout.Origin.y, 0f, 0f));
        cmd.SetComputeVectorParam(shader, HashSizeId, new Vector4(layout.Size.x, layout.Size.y, 0f, 0f));
        cmd.SetComputeVectorParam(shader, HashCellSizeId, new Vector4(layout.CellSize.x, layout.CellSize.y, 0f, 0f));
        cmd.SetComputeIntParams(shader, HashResId, layout.Resolution.x, layout.Resolution.y, 0, 0);
        cmd.SetComputeIntParam(shader, HashWrapId, 1);
        cmd.SetComputeIntParam(shader, HashCellCountId, layout.CellCount);
        cmd.SetComputeFloatParam(shader, FieldRtId, radiusTexels);
        cmd.SetComputeFloatParam(shader, FieldRId, radius);
        cmd.SetComputeFloatParam(shader, FieldEpsId, 0.001f);
    }

    public void Dispose()
    {
        Raw = Release(Raw);
        Accum = Release(Accum);
        Phi = Release(Phi);
        Field = Release(Field);
    }

    private static GraphicsBuffer NewFloat(int count)
    {
        return New(count, 4, GraphicsBuffer.Target.Structured);
    }

    private static GraphicsBuffer NewFloat2(int count)
    {
        return New(count, 8, GraphicsBuffer.Target.Structured);
    }

    private static GraphicsBuffer NewFloat4(int count)
    {
        return New(count, 16, GraphicsBuffer.Target.Structured);
    }

    private static GraphicsBuffer NewUint(int count)
    {
        return New(count, 4, GraphicsBuffer.Target.Structured);
    }

    private static GraphicsBuffer New(int count, int stride, GraphicsBuffer.Target target)
    {
        if (count <= 0)
        {
            return null;
        }

        return new GraphicsBuffer(target, count, stride);
    }

    private static GraphicsBuffer Release(GraphicsBuffer buffer)
    {
        buffer?.Release();
        return null;
    }
}
