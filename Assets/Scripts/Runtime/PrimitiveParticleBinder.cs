using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Present-step: camera-facing quads from ParticleSet.Position via Graphics.RenderPrimitives.
/// Does not copy or read back the buffer (ADR-030).
/// </summary>
public sealed class PrimitiveParticleBinder : IRenderBinder, IDisposable
{
    private static readonly int PositionsId = Shader.PropertyToID("_Positions");
    private static readonly int SizeId = Shader.PropertyToID("_Size");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private readonly float size;
    private readonly Color color;
    private Material material;
    private MaterialPropertyBlock props;
    private RenderParams renderParams;
    private int instanceCount;

    public PrimitiveParticleBinder(float size, Color color)
    {
        this.size = size;
        this.color = color;
    }

    public void Initialize(SimContext context)
    {
        Shader shader = Shader.Find("M3D/ParticleBillboard");
        if (shader == null)
        {
            Debug.LogError("PrimitiveParticleBinder: shader 'M3D/ParticleBillboard' not found.");
            return;
        }

        material = new Material(shader) { name = "M3D_ParticleBillboard" };
        props = new MaterialPropertyBlock();
        instanceCount = context.Particles.Count;

        renderParams = new RenderParams(material)
        {
            matProps = props,
            worldBounds = new Bounds(Vector3.zero, Vector3.one * 1e5f),
            shadowCastingMode = ShadowCastingMode.Off,
        };
    }

    public void Execute(SimContext context)
    {
        if (material == null || instanceCount <= 0)
        {
            return;
        }

        GraphicsBuffer positions = context.Particles.Get(BuiltinAttributes.Position);
        props.SetBuffer(PositionsId, positions);
        props.SetFloat(SizeId, size);
        props.SetColor(ColorId, color);

        Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, instanceCount);
    }

    public void Dispose()
    {
        if (material == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(material);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(material);
        }

        material = null;
    }
}
