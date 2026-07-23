// Explicitly agreed: BuiltinAttributes uses static readonly catalog entries.
public static class BuiltinAttributes
{
    /// <summary>Source data: canonical (rest) positions. Written by IDataSource, read-only for operators.</summary>
    public static readonly AttributeId RestPosition = new AttributeId("restPosition", AttributeType.Float3, true);

    /// <summary>Simulation output: written by the pass pipeline. Read by VFX.</summary>
    public static readonly AttributeId Position = new AttributeId("position", AttributeType.Float3, true);

    /// <summary>Particle velocity, integrated into Position by IntegratePass.</summary>
    public static readonly AttributeId Velocity = new AttributeId("velocity", AttributeType.Float3, true);

    public static readonly AttributeId Value = new AttributeId("value", AttributeType.Float1, true);
}
