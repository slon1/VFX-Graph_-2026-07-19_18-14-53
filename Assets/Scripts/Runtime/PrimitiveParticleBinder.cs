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

#if UNITY_EDITOR
    private Gradient fallbackGradient;
    private Color[] uploadedPixels;
    private Color[] scratchPixels;
    private int executeCount;
    private bool teamCountWarningIssued;
#endif

    internal bool UsesTeamPalette { get; private set; }

    internal const int LiveLutCheckInterval = 8;

    internal int LutCheckCount { get; private set; }

    internal int LutRefreshCount { get; private set; }

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
#if UNITY_EDITOR
            fallbackGradient = gradient ?? DebugFieldQuadSlot.DefaultFireGradient();
            uploadedPixels = new Color[LutWidth * teamCount];
            scratchPixels = new Color[LutWidth * teamCount];
            for (int row = 0; row < teamCount; row++)
            {
                BuildLutPixels(RowGradient(teams[row], fallbackGradient), LutWidth, uploadedPixels, row * LutWidth);
            }

            teamLut = CreateTeamLut(teamCount, uploadedPixels);
#else
            Gradient playerFallback = gradient ?? DebugFieldQuadSlot.DefaultFireGradient();
            Gradient[] rows = new Gradient[teamCount];
            for (int i = 0; i < teamCount; i++)
            {
                rows[i] = RowGradient(teams[i], playerFallback);
            }

            teamLut = BakeTeamLut(rows);
            teams = null;
#endif
        }
        else
        {
            teams = null;
        }

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
        if (material == null)
        {
            return;
        }

#if UNITY_EDITOR
        // Play tuning of team colors stays in the Editor. A player build keeps the Initialize LUT.
        if (UsesTeamPalette && teams != null)
        {
            executeCount++;
            if (executeCount % LiveLutCheckInterval == 0)
            {
                CheckLiveTeamLut();
            }
        }
#endif

        if (instanceCount <= 0)
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

        teams = null;
    }

    internal static void BuildLutPixels(Gradient gradient, int width, Color[] destination, int offset)
    {
        float inv = 1f / (width - 1);
        for (int i = 0; i < width; i++)
        {
            destination[offset + i] = gradient.Evaluate(i * inv);
        }
    }

    internal static Color[] BuildLutPixels(Gradient gradient, int width)
    {
        Color[] pixels = new Color[width];
        BuildLutPixels(gradient, width, pixels, 0);
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

#if !UNITY_EDITOR
    private static Texture2D BakeTeamLut(Gradient[] rows)
    {
        Color[] pixels = new Color[LutWidth * rows.Length];
        for (int row = 0; row < rows.Length; row++)
        {
            BuildLutPixels(rows[row], LutWidth, pixels, row * LutWidth);
        }

        return CreateTeamLut(rows.Length, pixels);
    }
#endif

    private static Texture2D CreateTeamLut(int height, Color[] pixels)
    {
        Texture2D texture = new Texture2D(LutWidth, height, TextureFormat.RGBA32, false, true)
        {
            name = "M3D_ParticleBillboard_TeamLUT",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static Gradient RowGradient(TeamProfile profile, Gradient fallback)
    {
        Gradient row = profile != null ? profile.Color : null;
        return row ?? fallback;
    }

#if UNITY_EDITOR
    private void CheckLiveTeamLut()
    {
        LutCheckCount++;
        int liveCount = teams.Count < TeamProfile.MaxTeams ? teams.Count : TeamProfile.MaxTeams;
        if (liveCount != teamCount && !teamCountWarningIssued)
        {
            teamCountWarningIssued = true;
            Debug.LogWarning(
                "PrimitiveParticleBinder: team count changed after Initialize. Rebuild to resize the team palette.");
        }

        for (int row = 0; row < teamCount; row++)
        {
            TeamProfile profile = row < teams.Count ? teams[row] : null;
            BuildLutPixels(RowGradient(profile, fallbackGradient), LutWidth, scratchPixels, row * LutWidth);
        }

        if (!PixelsDiffer(uploadedPixels, scratchPixels))
        {
            return;
        }

        Array.Copy(scratchPixels, uploadedPixels, uploadedPixels.Length);
        teamLut.SetPixels(uploadedPixels);
        teamLut.Apply(false, false);
        LutRefreshCount++;
    }

    private static bool PixelsDiffer(Color[] stored, Color[] fresh)
    {
        for (int i = 0; i < stored.Length; i++)
        {
            Color left = stored[i];
            Color right = fresh[i];
            if (left.r != right.r || left.g != right.g || left.b != right.b || left.a != right.a)
            {
                return true;
            }
        }

        return false;
    }
#endif

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
