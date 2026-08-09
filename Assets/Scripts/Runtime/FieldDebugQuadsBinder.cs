using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Debug panel: one quad (+ optional name label) per enabled <see cref="DebugFieldQuadSlot"/>.
/// Layout spreads along the field plane AxisU so multiple slots sit side-by-side.
/// </summary>
public sealed class FieldDebugQuadsBinder : IRenderBinder
{
    private const float GapFactor = 0.15f;
    private const int LutWidth = 256;

    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int LutTexId = Shader.PropertyToID("_LutTex");
    private static readonly int ScaleId = Shader.PropertyToID("_Scale");
    private static readonly int HdrIntensityId = Shader.PropertyToID("_HdrIntensity");
    private static readonly int VisualModeId = Shader.PropertyToID("_VisualMode");

    private readonly IReadOnlyList<DebugFieldQuadSlot> slots;
    private readonly Transform host;
    private readonly List<QuadEntry> quads = new List<QuadEntry>();
    private GameObject root;

    private struct QuadEntry
    {
        public SimField Field;
        public Material Material;
        public Texture2D LutTexture;
        public GameObject QuadObject;
    }

    public FieldDebugQuadsBinder(IReadOnlyList<DebugFieldQuadSlot> slots, Transform host)
    {
        this.slots = slots ?? System.Array.Empty<DebugFieldQuadSlot>();
        this.host = host;
    }

    public void Initialize(SimContext context)
    {
        Shader shader = Shader.Find("M3D/FieldDebug");
        if (shader == null)
        {
            Debug.LogError("FieldDebugQuadsBinder: shader 'M3D/FieldDebug' not found.");
            return;
        }

        List<DebugFieldQuadSlot> active = new List<DebugFieldQuadSlot>();
        List<SimField> resolved = new List<SimField>();

        for (int i = 0; i < slots.Count; i++)
        {
            DebugFieldQuadSlot slot = slots[i];
            if (string.IsNullOrEmpty(slot.fieldName))
            {
                continue;
            }

            if (!context.Fields.TryGet(slot.fieldName, out SimField field))
            {
                Debug.LogError(
                    $"FieldDebugQuadsBinder: debug slot field '{slot.fieldName}' not found in runtime Fields.");
                continue;
            }

            int channels = field.Descriptor.ChannelCount;
            if (channels != 1 && channels != 2)
            {
                Debug.LogWarning(
                    $"FieldDebugQuadsBinder: skip '{slot.fieldName}' — {channels} channel(s); " +
                    "only 1 (ScalarHeatmap) or 2 (VectorRg) are supported.");
                continue;
            }

            if (slot.mode == FieldQuadVisualMode.ScalarHeatmap && channels != 1)
            {
                Debug.LogWarning(
                    $"FieldDebugQuadsBinder: skip '{slot.fieldName}' — ScalarHeatmap requires 1 channel, got {channels}.");
                continue;
            }

            if (slot.mode == FieldQuadVisualMode.VectorRg && channels != 2)
            {
                Debug.LogWarning(
                    $"FieldDebugQuadsBinder: skip '{slot.fieldName}' — VectorRg requires 2 channels, got {channels}.");
                continue;
            }

            active.Add(slot);
            resolved.Add(field);
        }

        if (active.Count == 0)
        {
            return;
        }

        root = new GameObject("FieldDebugQuads");
        root.transform.SetParent(host, false);

        // Shared plane from first slot (Policy: one plane per effect writes).
        FieldDescriptor plane = resolved[0].Descriptor;
        Vector3 axisU = plane.AxisU.normalized;
        Vector3 axisV = plane.AxisV.normalized;
        Vector3 n = Vector3.Cross(axisU, axisV).normalized;
        if (n.sqrMagnitude < 1e-6f)
        {
            n = Vector3.up;
        }

        float cellWidth = plane.Size.x * (1f + GapFactor);
        float originShift = (active.Count - 1) * 0.5f * cellWidth;

        for (int i = 0; i < active.Count; i++)
        {
            DebugFieldQuadSlot slot = active[i];
            SimField field = resolved[i];
            FieldDescriptor descriptor = field.Descriptor;

            Material material = new Material(shader) { name = $"M3D_FieldDebug_{slot.fieldName}" };
            float scale = slot.colorScale > 0f ? slot.colorScale : DebugFieldQuadSlot.DefaultScale(slot.mode);
            float hdr = slot.hdrIntensity > 0f ? slot.hdrIntensity : 1f;
            Texture2D lutTexture = BakeLutTexture(slot.lut ?? DebugFieldQuadSlot.DefaultFireGradient());

            material.SetTexture(MainTexId, field.Current);
            material.SetTexture(LutTexId, lutTexture);
            material.SetFloat(ScaleId, scale);
            material.SetFloat(HdrIntensityId, hdr);
            material.SetFloat(VisualModeId, (float)slot.mode);

            GameObject quadObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadObject.name = $"FieldQuad_{slot.fieldName}";
            quadObject.transform.SetParent(root.transform, false);

            Vector3 center = descriptor.Origin + axisU * (i * cellWidth - originShift);
            quadObject.transform.position = center;
            quadObject.transform.rotation = Quaternion.LookRotation(n, axisV);
            quadObject.transform.localScale = new Vector3(descriptor.Size.x, descriptor.Size.y, 1f);

            MeshRenderer renderer = quadObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;

            Collider collider = quadObject.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }

            CreateLabel(quadObject.transform, slot.fieldName, descriptor.Size);

            quads.Add(new QuadEntry
            {
                Field = field,
                Material = material,
                LutTexture = lutTexture,
                QuadObject = quadObject,
            });
        }
    }

    public void Execute(SimContext context)
    {
        for (int i = 0; i < quads.Count; i++)
        {
            QuadEntry entry = quads[i];
            if (entry.Material == null || entry.Field == null)
            {
                continue;
            }

            entry.Material.SetTexture(MainTexId, entry.Field.Current);
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < quads.Count; i++)
        {
            if (quads[i].LutTexture != null)
            {
                Object.Destroy(quads[i].LutTexture);
            }

            if (quads[i].Material != null)
            {
                Object.Destroy(quads[i].Material);
            }
        }

        quads.Clear();

        if (root != null)
        {
            Object.Destroy(root);
            root = null;
        }
    }

    /// <summary>
    /// Bake Gradient to a 256×1 LUT. Caller owns the texture (Destroy with Material).
    /// Wrap Clamp + Bilinear — avoids edge seams and banding.
    /// </summary>
    private static Texture2D BakeLutTexture(Gradient gradient)
    {
        Texture2D texture = new Texture2D(LutWidth, 1, TextureFormat.RGBA32, false, true)
        {
            name = "M3D_FieldDebug_LUT",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };

        Color[] pixels = new Color[LutWidth];
        float inv = 1f / (LutWidth - 1);
        for (int i = 0; i < LutWidth; i++)
        {
            pixels[i] = gradient.Evaluate(i * inv);
        }

        texture.SetPixels(pixels);
        texture.Apply(false, true);
        return texture;
    }

    private static void CreateLabel(Transform quad, string fieldName, Vector2 fieldSize)
    {
        GameObject labelGo = new GameObject($"Label_{fieldName}");
        labelGo.transform.SetParent(quad, false);
        // Above the quad center along AxisV (local +Y of Unity Quad maps to AxisV after LookRotation).
        labelGo.transform.localPosition = new Vector3(0f, 0.58f, -0.02f);
        labelGo.transform.localRotation = Quaternion.identity;
        float sx = Mathf.Max(quad.localScale.x, 1e-3f);
        float sy = Mathf.Max(quad.localScale.y, 1e-3f);
        labelGo.transform.localScale = new Vector3(1f / sx, 1f / sy, 1f);

        TextMesh text = labelGo.AddComponent<TextMesh>();
        text.text = fieldName;
        text.fontSize = 64;
        text.characterSize = 0.05f * Mathf.Max(fieldSize.x, fieldSize.y);
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.color = Color.white;
    }
}
