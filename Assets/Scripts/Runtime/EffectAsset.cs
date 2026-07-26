using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One effect = one asset: data source config + field declarations + ordered pass list.
/// Field resources are declaration-owned (policy C): runtime never auto-creates them.
/// </summary>
[CreateAssetMenu(fileName = "NewEffect", menuName = "M3D/Effect Asset")]
public sealed class EffectAsset : ScriptableObject
{
    [SerializeField] private DataSourceKind sourceKind = DataSourceKind.Cube;
    [SerializeField] private CubeSource cubeSource = new CubeSource();
    [SerializeField] private MeshSource meshSource = new MeshSource();
    [SerializeField] private BitmapSource bitmapSource = new BitmapSource();
    [SerializeField, Min(0f)] private float simulationSpeed = 1f;
    [SerializeField] private List<FieldDescriptor> fields = new List<FieldDescriptor>();
    [SerializeReference] private List<SimPass> passes = new List<SimPass>();
    [SerializeField] private bool showVelocityFieldQuad;

    public float SimulationSpeed => simulationSpeed;
    public IReadOnlyList<FieldDescriptor> Fields => fields;
    public IReadOnlyList<SimPass> Passes => passes;
    public bool ShowVelocityFieldQuad => showVelocityFieldQuad;

    public IDataSource ResolveSource()
    {
        switch (sourceKind)
        {
            case DataSourceKind.Cube:
                return cubeSource;
            case DataSourceKind.Mesh:
                return meshSource;
            case DataSourceKind.Bitmap:
                return bitmapSource;
            default:
                throw new System.ArgumentOutOfRangeException(
                    nameof(sourceKind), sourceKind, "Unknown data source kind.");
        }
    }

    /// <summary>Editor seeding — used by M3DDemoTools before AssetDatabase.CreateAsset.</summary>
    public void EditorConfigure(
        DataSourceKind kind,
        float speed,
        SimPass[] passList,
        FieldDescriptor[] fieldList = null,
        bool velocityQuad = false)
    {
        sourceKind = kind;
        simulationSpeed = speed;
        passes = new List<SimPass>(passList);
        fields = fieldList != null
            ? new List<FieldDescriptor>(fieldList)
            : new List<FieldDescriptor>();
        showVelocityFieldQuad = velocityQuad;
    }

#if UNITY_EDITOR
    /// <summary>
    /// Appends default FieldDescriptors for any FieldReads/FieldWrites names
    /// not already declared. Does not overwrite existing entries.
    /// </summary>
    public int MaterializeMissingFields()
    {
        HashSet<string> existing = new HashSet<string>(System.StringComparer.Ordinal);
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i] != null && !string.IsNullOrEmpty(fields[i].Name))
            {
                existing.Add(fields[i].Name);
            }
        }

        int added = 0;
        for (int p = 0; p < passes.Count; p++)
        {
            SimPass pass = passes[p];
            if (pass == null)
            {
                continue;
            }

            added += MaterializeFromRequests(pass.FieldReads, existing);
            added += MaterializeFromRequests(pass.FieldWrites, existing);
        }

        return added;
    }

    private int MaterializeFromRequests(IReadOnlyList<FieldRequest> requests, HashSet<string> existing)
    {
        int added = 0;
        for (int i = 0; i < requests.Count; i++)
        {
            FieldRequest request = requests[i];
            if (string.IsNullOrEmpty(request.FieldName) || existing.Contains(request.FieldName))
            {
                continue;
            }

            fields.Add(FieldDescriptor.CreateDefault(request.FieldName, request.RequiredSemantic));
            existing.Add(request.FieldName);
            added++;
        }

        return added;
    }
#endif
}
