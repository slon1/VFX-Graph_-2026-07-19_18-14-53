using UnityEngine;

[System.Serializable]
public sealed class CubeSource : IDataSource
{
    [SerializeField] private int resolution = 100;
    [SerializeField] private float cubeSize = 2f;

    public string Name => "Cube";

    public int Resolution
    {
        get => resolution;
        set => resolution = value;
    }

    public float CubeSize
    {
        get => cubeSize;
        set => cubeSize = value;
    }

    public void Setup(ParticleSet particles)
    {
        if (resolution < 1)
        {
            throw new System.ArgumentOutOfRangeException(nameof(resolution), "resolution must be >= 1.");
        }

        int count = resolution * resolution * resolution;
        particles.EnsureCapacity(count);

        GraphicsBuffer restPositions = particles.RegisterAttribute(BuiltinAttributes.RestPosition);
        restPositions.SetData(CreateGridPositions(resolution, cubeSize, count));
    }

    public void Tick(ParticleSet particles)
    {
    }

    private Vector3[] CreateGridPositions(int gridResolution, float size, int count)
    {
        Vector3[] positions = new Vector3[count];
        float halfExtent = size * 0.5f;
        float step = gridResolution > 1 ? size / (gridResolution - 1) : 0f;
        int index = 0;

        for (int z = 0; z < gridResolution; z++)
        {
            for (int y = 0; y < gridResolution; y++)
            {
                for (int x = 0; x < gridResolution; x++)
                {
                    positions[index] = new Vector3(
                        -halfExtent + x * step,
                        -halfExtent + y * step,
                        -halfExtent + z * step);
                    index++;
                }
            }
        }

        return positions;
    }
}
