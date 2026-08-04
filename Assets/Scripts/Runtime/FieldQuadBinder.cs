using UnityEngine;

/// <summary>
/// Debug/presentation quad that samples a declared field every frame.
/// Must rebind Current after ping-pong swaps — Execute runs every frame.
/// </summary>
public sealed class FieldQuadBinder : IRenderBinder
{
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int FieldOriginId = Shader.PropertyToID("_FieldOrigin");
    private static readonly int FieldAxisUId = Shader.PropertyToID("_FieldAxisU");
    private static readonly int FieldAxisVId = Shader.PropertyToID("_FieldAxisV");
    private static readonly int FieldSizeId = Shader.PropertyToID("_FieldSize");

    private readonly string fieldName;
    private readonly Transform host;
    private GameObject quadObject;
    private Material material;
    private SimField field;

    public FieldQuadBinder(string fieldName, Transform host)
    {
        this.fieldName = fieldName;
        this.host = host;
    }

    public void Initialize(SimContext context)
    {
        if (!context.Fields.TryGet(fieldName, out field))
        {
            Debug.LogError($"FieldQuadBinder: field '{fieldName}' not found.");
            return;
        }

        FieldDescriptor descriptor = field.Descriptor;
        Shader shader = Shader.Find("M3D/FieldDebug");
        if (shader == null)
        {
            Debug.LogError("FieldQuadBinder: shader 'M3D/FieldDebug' not found.");
            return;
        }

        material = new Material(shader) { name = $"M3D_FieldDebug_{fieldName}" };
        material.SetTexture(MainTexId, field.Current);
        material.SetVector(FieldOriginId, descriptor.Origin);
        material.SetVector(FieldAxisUId, descriptor.AxisU.normalized);
        material.SetVector(FieldAxisVId, descriptor.AxisV.normalized);
        material.SetVector(FieldSizeId, new Vector4(descriptor.Size.x, descriptor.Size.y, 0f, 0f));

        quadObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quadObject.name = $"FieldQuad_{fieldName}";
        quadObject.transform.SetParent(host, false);

        // Orient quad to field plane: local XY maps to axisU/axisV.
        Vector3 n = Vector3.Cross(descriptor.AxisU.normalized, descriptor.AxisV.normalized).normalized;
        if (n.sqrMagnitude < 1e-6f)
        {
            n = Vector3.up;
        }

        quadObject.transform.position = descriptor.Origin;
        //quadObject.transform.rotation = Quaternion.LookRotation(n, descriptor.AxisV.normalized);
        quadObject.transform.localScale = new Vector3(descriptor.Size.x, descriptor.Size.y, 1f);

        MeshRenderer renderer = quadObject.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;

        Collider collider = quadObject.GetComponent<Collider>();
        if (collider != null)
        {
            Object.Destroy(collider);
        }
    }

    public void Execute(SimContext context)
    {
        if (material == null || field == null)
        {
            return;
        }

        // Ping-pong swaps change which texture is Current — rebind every frame.
        material.SetTexture(MainTexId, field.Current);
    }

    public void Dispose()
    {
        if (quadObject != null)
        {
            Object.Destroy(quadObject);
            quadObject = null;
        }

        if (material != null)
        {
            Object.Destroy(material);
            material = null;
        }

        field = null;
    }
}
