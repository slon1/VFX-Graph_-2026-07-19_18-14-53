public interface IDataSource
{
    string Name { get; }
    void Setup(ParticleSet particles);
    void Tick(ParticleSet particles);
}
