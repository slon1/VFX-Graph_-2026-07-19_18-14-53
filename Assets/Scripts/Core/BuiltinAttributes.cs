// Explicitly agreed: BuiltinAttributes uses static readonly catalog entries.
public static class BuiltinAttributes
{
    public static readonly AttributeId Position = new AttributeId("position", AttributeType.Float3, true);
    public static readonly AttributeId Value = new AttributeId("value", AttributeType.Float1, true);
}
