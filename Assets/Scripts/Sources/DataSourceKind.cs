public enum DataSourceKind
{
    Cube = 0,
    Mesh = 1,
    Bitmap = 2,
    /// <summary>No particles — field-only effects (e.g. Gray-Scott).</summary>
    None = 3,
    /// <summary>XZ disks with teamId and heading (ADR-036). No neighbour force.</summary>
    Swarm = 4,
}
