using System;
using System.Collections.Generic;
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
    private static readonly int TeamIdsId = Shader.PropertyToID("_TeamIds");
    private static readonly int UseTeamsId = Shader.PropertyToID("_UseTeams");
    private static readonly int TeamCountId = Shader.PropertyToID("_TeamCount");

    private readonly float size;
    private readonly Color color;
    private readonly Gradient gradient;
    private readonly float valueScale;
    private IReadOnlyList<TeamProfile> teams;
    private Material material;
    private MaterialPropertyBlock props;
    private RenderParams renderParams;
    private Texture2D lutTexture;
    private Texture2D teamLut;
    private GraphicsBuffer valuesBuffer;
    private GraphicsBuffer teamBuffer;
    private Texture2D dummyLut;
    private GraphicsBuffer dummyValues;
    private GraphicsBuffer dummyTeamIds;
    private bool hasValueAttribute;
    private int teamCount;
    private int instanceCount;

    internal bool UsesTeamPalette { get; private set; }

    public PrimitiveParticleBinder(float size, Color color, Gradient gradient, float valueScale)
        : this(size, color, gradient, valueScale, null)
    {
    }

    public PrimitiveParticleBinder(
        float size, Color color, Gradient gradient, float valueScale, IReadOnlyList<TeamProfile> teams)
    {
        this.size = size;
        this.color = color;
        this.gradient = gradient;
        this.valueScale = valueScale;
        this.teams = teams;
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
        UsesTeamPalette = teams != null
            && teams.Count > 0
            && context.Particles.TryGet(BuiltinAttributes.TeamId, out teamBuffer);
        if (UsesTeamPalette)
        {
            teamCount = teams.Count < TeamProfile.MaxTeams ? teams.Count : TeamProfile.MaxTeams;
            Gradient[] rows = new Gradient[teamCount];
            for (int i = 0; i < teamCount; i++)
            {
                TeamProfile profile = teams[i];
                Gradient row = profile != null ? profile.Color : null;
                rows[i] = row ?? gradient ?? DebugFieldQuadSlot.DefaultFireGradient();
            }

            teamLut = BakeTeamLut(rows);
        }

        teams = null;

        if (UsesTeamPalette)
        {
            if (!hasValueAttribute)
            {
                dummyValues = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float));
            }
        }
        else if (hasValueAttribute)
        {
            lutTexture = BakeLutTexture(gradient ?? DebugFieldQuadSlot.DefaultFireGradient());
        }
        else
        {
            dummyValues = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float));
            dummyLut = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = "M3D_ParticleBillboard_DummyLut",
                hideFlags = HideFlags.HideAndDontSave,
            };
            dummyLut.Apply(false, true);
        }

        if (!UsesTeamPalette)
        {
            dummyTeamIds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
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

        if (UsesTeamPalette)
        {
            props.SetBuffer(TeamIdsId, teamBuffer);
            props.SetFloat(UseTeamsId, 1f);
            props.SetFloat(TeamCountId, teamCount);
            props.SetTexture(LutTexId, teamLut);
            if (hasValueAttribute)
            {
                props.SetBuffer(ValuesId, valuesBuffer);
                props.SetFloat(ScaleId, valueScale);
                props.SetFloat(UseLutId, 1f);
            }
            else
            {
                props.SetBuffer(ValuesId, dummyValues);
                props.SetFloat(UseLutId, 0f);
            }
        }
        else if (hasValueAttribute)
        {
            props.SetBuffer(ValuesId, valuesBuffer);
            props.SetTexture(LutTexId, lutTexture);
            props.SetFloat(ScaleId, valueScale);
            props.SetFloat(UseLutId, 1f);
            props.SetBuffer(TeamIdsId, dummyTeamIds);
            props.SetFloat(UseTeamsId, 0f);
            props.SetFloat(TeamCountId, 1f);
        }
        else
        {
            props.SetBuffer(ValuesId, dummyValues);
            props.SetTexture(LutTexId, dummyLut);
            props.SetFloat(UseLutId, 0f);
            props.SetBuffer(TeamIdsId, dummyTeamIds);
            props.SetFloat(UseTeamsId, 0f);
            props.SetFloat(TeamCountId, 1f);
        }

        Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, instanceCount);
    }

    public void Dispose()
    {
        DestroyObject(ref material);
        DestroyObject(ref lutTexture);
        DestroyObject(ref teamLut);
        DestroyObject(ref dummyLut);
        if (dummyValues != null)
        {
            dummyValues.Release();
            dummyValues = null;
        }

        if (dummyTeamIds != null)
        {
            dummyTeamIds.Release();
            dummyTeamIds = null;
        }
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

    private static Texture2D BakeTeamLut(Gradient[] rows)
    {
        Texture2D texture = new Texture2D(LutWidth, rows.Length, TextureFormat.RGBA32, false, true)
        {
            name = "M3D_ParticleBillboard_TeamLUT",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };

        Color[] pixels = new Color[LutWidth * rows.Length];
        for (int row = 0; row < rows.Length; row++)
        {
            Color[] baked = BuildLutPixels(rows[row], LutWidth);
            for (int x = 0; x < LutWidth; x++)
            {
                pixels[row * LutWidth + x] = baked[x];
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
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
