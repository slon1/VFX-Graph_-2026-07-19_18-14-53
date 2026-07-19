using System.Collections.Generic;
using UnityEngine;

public sealed class TwistGPUOperator : IGPUOperator
{
    private const int ThreadGroupSize = 256;
    private const string KernelName = "CSMain";

    private readonly AttributeId[] requiredInputs = { BuiltinAttributes.Position };
    private readonly AttributeId[] outputs = { BuiltinAttributes.Position };

    private ComputeShader computeShader;
    private float twistStrength = 1f;
    private float simulationSpeed = 1f;
    private int kernelIndex;
    private int positionsPropertyId;
    private int particleCountPropertyId;
    private int operatorStrengthPropertyId;
    private bool initialized;

    public string Name => "Twist";
    public IReadOnlyList<AttributeId> RequiredInputs => requiredInputs;
    public IReadOnlyList<AttributeId> Outputs => outputs;

    public float TwistStrength
    {
        get => twistStrength;
        set => twistStrength = value;
    }

    public float SimulationSpeed
    {
        get => simulationSpeed;
        set => simulationSpeed = value;
    }

    public void Initialize(ComputeShader shader)
    {
        computeShader = shader;
        kernelIndex = computeShader.FindKernel(KernelName);
        positionsPropertyId = Shader.PropertyToID("Positions");
        particleCountPropertyId = Shader.PropertyToID("ParticleCount");
        operatorStrengthPropertyId = Shader.PropertyToID("OperatorStrength");
        initialized = true;
    }

    public void Execute(PointDataset dataset, float deltaTime)
    {
        if (!initialized || computeShader == null)
        {
            throw new System.InvalidOperationException("TwistGPUOperator is not initialized.");
        }

        GraphicsBuffer positions = dataset.Get(BuiltinAttributes.Position);
        int count = dataset.Count;
        int threadGroups = Mathf.CeilToInt(count / (float)ThreadGroupSize);
        float operatorStrength = twistStrength * simulationSpeed * deltaTime;

        computeShader.SetBuffer(kernelIndex, positionsPropertyId, positions);
        computeShader.SetInt(particleCountPropertyId, count);
        computeShader.SetFloat(operatorStrengthPropertyId, operatorStrength);
        computeShader.Dispatch(kernelIndex, threadGroups, 1, 1);
    }
}
