using System.Collections.Generic;

public interface IGPUOperator
{
    string Name { get; }
    IReadOnlyList<AttributeId> RequiredInputs { get; }
    IReadOnlyList<AttributeId> Outputs { get; }
    void Execute(PointDataset dataset, float deltaTime);
}
