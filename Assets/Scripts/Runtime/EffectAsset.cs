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
    [SerializeField] private NoneSource noneSource = new NoneSource();
    [SerializeField, Min(0f)] private float simulationSpeed = 1f;
    [SerializeField] private List<FieldDescriptor> fields = new List<FieldDescriptor>();
    [SerializeReference] private List<SimPass> passes = new List<SimPass>();
    [SerializeField] private List<DebugFieldQuadSlot> debugFieldQuads = new List<DebugFieldQuadSlot>();

    // Legacy flags — migrated into debugFieldQuads via OnValidate, then cleared.
    [SerializeField, HideInInspector] private bool showVelocityFieldQuad;
    [SerializeField, HideInInspector] private bool showDensityFieldQuad;

    public float SimulationSpeed => simulationSpeed;
    public IReadOnlyList<FieldDescriptor> Fields => fields;
    public IReadOnlyList<SimPass> Passes => passes;
    public IReadOnlyList<DebugFieldQuadSlot> DebugFieldQuads => debugFieldQuads;

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
            case DataSourceKind.None:
                return noneSource;
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
        DebugFieldQuadSlot[] debugQuads = null)
    {
        sourceKind = kind;
        simulationSpeed = speed;
        passes = new List<SimPass>(passList);
        fields = fieldList != null
            ? new List<FieldDescriptor>(fieldList)
            : new List<FieldDescriptor>();
        debugFieldQuads = debugQuads != null
            ? new List<DebugFieldQuadSlot>(debugQuads)
            : new List<DebugFieldQuadSlot>();
        showVelocityFieldQuad = false;
        showDensityFieldQuad = false;
    }

#if UNITY_EDITOR
    /// <summary>Inspector entry-point so migration runs even if OnValidate did not fire yet.</summary>
    /// <returns>True if legacy flags were converted into slots.</returns>
    public bool EditorEnsureDebugQuadMigration()
    {
        return MigrateLegacyDebugQuads();
    }

    private void OnValidate()
    {
        MigrateLegacyDebugQuads();
    }

    /// <summary>
    /// One-shot: ShowVelocity/ShowDensity bools → debugFieldQuads entries, then clear flags.
    /// </summary>
    private bool MigrateLegacyDebugQuads()
    {
        if (!showVelocityFieldQuad && !showDensityFieldQuad)
        {
            return false;
        }

        if (debugFieldQuads == null)
        {
            debugFieldQuads = new List<DebugFieldQuadSlot>();
        }

        if (showVelocityFieldQuad)
        {
            string velocityName = ResolveLegacyVelocityFieldName();
            if (!string.IsNullOrEmpty(velocityName) && !HasDebugSlot(velocityName))
            {
                debugFieldQuads.Add(DebugFieldQuadSlot.Velocity(velocityName));
            }

            showVelocityFieldQuad = false;
        }

        if (showDensityFieldQuad)
        {
            string densityName = ResolveLegacyDensityFieldName();
            if (!string.IsNullOrEmpty(densityName) && !HasDebugSlot(densityName))
            {
                debugFieldQuads.Add(DebugFieldQuadSlot.Density(densityName));
            }

            showDensityFieldQuad = false;
        }

        return true;
    }

    private bool HasDebugSlot(string fieldName)
    {
        for (int i = 0; i < debugFieldQuads.Count; i++)
        {
            if (string.Equals(debugFieldQuads[i].fieldName, fieldName, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private string ResolveLegacyVelocityFieldName()
    {
        if (FindFieldName("velocity") != null)
        {
            return "velocity";
        }

        if (FindFieldName("agentVelocity") != null)
        {
            return "agentVelocity";
        }

        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i] != null && fields[i].Semantic == FieldSemantic.Velocity)
            {
                return fields[i].Name;
            }
        }

        return "velocity";
    }

    private string ResolveLegacyDensityFieldName()
    {
        if (FindFieldName("density") != null)
        {
            return "density";
        }

        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i] != null && fields[i].Semantic == FieldSemantic.Scalar)
            {
                return fields[i].Name;
            }
        }

        return "density";
    }

    private string FindFieldName(string name)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i] != null && string.Equals(fields[i].Name, name, System.StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }

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
