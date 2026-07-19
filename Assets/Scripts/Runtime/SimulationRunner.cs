using UnityEngine;
using UnityEngine.VFX;

public class SimulationRunner : MonoBehaviour
{
    private const string PositionBufferPropertyName = "PositionBuffer";
    private const string SpawnCountPropertyName = "SpawnCount";

    [SerializeField] private CubeSource cubeSource = new CubeSource();
    [SerializeField] private float twistStrength = 1f;
    [SerializeField] private float simulationSpeed = 1f;
    [SerializeField] private ComputeShader computeShader;
    [SerializeField] private VisualEffect visualEffect;

    private PointDataset dataset;
    private TwistGPUOperator twistOperator;
    private IDataSource activeSource;

    private void OnEnable()
    {
        if (visualEffect == null)
        {
            visualEffect = GetComponent<VisualEffect>();
        }

        if (computeShader == null || visualEffect == null)
        {
            Debug.LogError("SimulationRunner: ComputeShader and VisualEffect must be assigned.", this);
            enabled = false;
            return;
        }

        activeSource = cubeSource;
        dataset = new PointDataset();
        activeSource.Setup(dataset);

        twistOperator = new TwistGPUOperator();
        twistOperator.Initialize(computeShader);
        twistOperator.TwistStrength = twistStrength;
        twistOperator.SimulationSpeed = simulationSpeed;

        GraphicsBuffer positions = dataset.Get(BuiltinAttributes.Position);
        visualEffect.SetGraphicsBuffer(PositionBufferPropertyName, positions);
        if (visualEffect.HasFloat(SpawnCountPropertyName))
        {
            visualEffect.SetFloat(SpawnCountPropertyName, dataset.Count);
        }

        visualEffect.Reinit();
        Debug.Log(
            $"SimulationRunner: dataset ready ({dataset.Count} points, source '{activeSource.Name}').",
            this);
    }

    private void Update()
    {
        twistOperator.TwistStrength = twistStrength;
        twistOperator.SimulationSpeed = simulationSpeed;

        activeSource.Tick(dataset);
        twistOperator.Execute(dataset, Time.deltaTime);
    }

    private void OnDisable()
    {
        ReleaseDataset();
    }

    private void OnDestroy()
    {
        ReleaseDataset();
    }

    private void ReleaseDataset()
    {
        if (dataset == null)
        {
            return;
        }

        dataset.Dispose();
        dataset = null;
    }
}
