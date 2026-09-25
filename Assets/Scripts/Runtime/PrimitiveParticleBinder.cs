using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Present-step: camera-facing quads from ParticleSet.Position via Graphics.RenderPrimitives.
/// Optional LUT by ParticleSet.Value (ADR-031). Does not copy or read back buffers.
/// </summary>
public sealed class PrimitiveParticleBinder : IRenderBinder, IDisposable
{
    private const int LutWidth = 256;

    private static readonly int PositionsId = Shader.PropertyToID("_Positions");
    private static readonly int SizeId = Shader.PropertyToID("_Size");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int ValuesId = Shader.PropertyToID("_Values");
    private static readonly int LutTexId = Shader.PropertyToID("_LutTex");
    private static readonly int ScaleId = Shader.PropertyToID("_Scale");
    private static readonly int UseLutId = Shader.PropertyToID("_UseLut");

    private readonly float size;
    private readonly Color color;
    private readonly Gradient gradient;
    private readonly float valueScale;
    private Material material;
    private MaterialPropertyBlock props;
    private RenderParams renderParams;
    private Texture2D lutTexture;
    private GraphicsBuffer valuesBuffer;
    private bool hasValueAttribute;
    private int instanceCount;

    public PrimitiveParticleBinder(float size, Color color, Gradient gradient, float valueScale)
    {
        this.size = size;
        this.color = color;
        this.gradient = gradient;
        this.valueScale = valueScale;
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

        hasValueAttribute = context.Particles.TryGet(BuiltinAttributes.Value, out valuesBuffer);
        if (hasValueAttribute)
        {
            lutTexture = BakeLutTexture(gradient ?? DebugFieldQuadSlot.DefaultFireGradient());
        }

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

        if (hasValueAttribute)
        {
            props.SetBuffer(ValuesId, valuesBuffer);
            props.SetTexture(LutTexId, lutTexture);
            props.SetFloat(ScaleId, valueScale);
            props.SetFloat(UseLutId, 1f);
        }
        else
        {
            props.SetFloat(UseLutId, 0f);
        }

        Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, instanceCount);
    }

    public void Dispose()
    {
        DestroyObject(ref material);
        DestroyObject(ref lutTexture);
    }

    internal static Color[] BuildLutPixels(Gradient gradient, int width)
    {
        Color[] pixels = new Color[width];
        float inv = 1f / (width - 1);
        for (int i = 0; i < width; i++)
        {
            pixels[i] = gradient.Evaluate(i * inv);
        }

        return pixels;
    }

    private static Texture2D BakeLutTexture(Gradient gradient)
    {
        Texture2D texture = new Texture2D(LutWidth, 1, TextureFormat.RGBA32, false, true)
        {
            name = "M3D_ParticleBillboard_LUT",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };

        texture.SetPixels(BuildLutPixels(gradient, LutWidth));
        texture.Apply(false, true);
        return texture;
    }

    private static void DestroyObject<T>(ref T obj) where T : UnityEngine.Object
    {
        if (obj == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(obj);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(obj);
        }

        obj = null;
    }
}
