using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One effect = one asset: data source config + ordered pass list + exposed settings.
/// The pass list order IS the execution order. Copy the asset to make a preset.
/// </summary>
[CreateAssetMenu(fileName = "NewEffect", menuName = "M3D/Effect Asset")]
public sealed class EffectAsset : ScriptableObject
{
    [SerializeField] private DataSourceKind sourceKind = DataSourceKind.Cube;
    [SerializeField] private CubeSource cubeSource = new CubeSource();
    [SerializeField] private MeshSource meshSource = new MeshSource();
    [SerializeField] private BitmapSource bitmapSource = new BitmapSource();
    [SerializeField, Min(0f)] private float simulationSpeed = 1f;
    [SerializeReference] private List<SimPass> passes = new List<SimPass>();

    public float SimulationSpeed => simulationSpeed;
    public IReadOnlyList<SimPass> Passes => passes;

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

    /// <summary>Editor seeding only — used by M3DDemoTools before AssetDatabase.CreateAsset.</summary>
    public void EditorConfigure(DataSourceKind kind, float speed, SimPass[] passList)
    {
        sourceKind = kind;
        simulationSpeed = speed;
        passes = new List<SimPass>(passList);
    }
}
