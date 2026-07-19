public interface IDataSource
{
    string Name { get; }
    void Setup(PointDataset dataset);
    void Tick(PointDataset dataset);
}
