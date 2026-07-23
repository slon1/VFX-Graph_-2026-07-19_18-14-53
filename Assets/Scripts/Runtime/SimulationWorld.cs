using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.VFX;

/// <summary>
/// Runtime owner of the simulation: resources (ParticleSet, TouchBuffer),
/// frame loop (one CommandBuffer per frame), pass initialization/validation
/// and render binding (VFX Graph). Knows nothing about concrete effects —
/// everything comes from the EffectAsset.
/// </summary>
public sealed class SimulationWorld : MonoBehaviour
{
    private const string PositionBufferPropertyName = "PositionBuffer";
    private const string SpawnCountPropertyName = "SpawnCount";
    private const int VfxCapacity = 1_000_000; // must match CreateParticleBufferVFX.Capacity

    [SerializeField] private EffectAsset effect;
    [SerializeField] private ComputeShader[] passLibrary;
    [SerializeField] private VisualEffect visualEffect;
    [SerializeField] private InputRouter inputRouter;

    private ParticleSet particles;
    private SimContext context;
    private CommandBuffer commandBuffer;
    private GraphicsBuffer touchBuffer;
    private IDataSource source;
    private readonly TouchForce[] touchScratch = new TouchForce[InputRouter.MaxTouches];
    private readonly Dictionary<SimPass, ProfilingSampler> samplers =
        new Dictionary<SimPass, ProfilingSampler>();
    private float simulationTime;
    private bool built;

    private void OnEnable()
    {
        Build();
    }

    private void OnDisable()
    {
        Teardown();
    }

    private void OnDestroy()
    {
        Teardown();
    }

    /// <summary>Rebuilds the whole world (new source data, re-init passes) without restarting Play Mode.</summary>
    public void Rebuild()
    {
        Teardown();
        Build();
    }

    private void Update()
    {
        if (!built)
        {
            return;
        }

        float deltaTime = Time.deltaTime * effect.SimulationSpeed;
        simulationTime += deltaTime;
        context.Time = simulationTime;

        int touchCount = 0;
        if (inputRouter != null)
        {
            touchCount = inputRouter.Sample(touchScratch);
            if (touchCount > 0)
            {
                touchBuffer.SetData(touchScratch, 0, 0, touchCount);
            }
        }

        context.TouchCount = touchCount;

        source.Tick(particles);

        commandBuffer.Clear();
        IReadOnlyList<SimPass> passes = effect.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            SimPass pass = passes[i];
            if (pass == null || !pass.Enabled)
            {
                continue;
            }

            if (!samplers.TryGetValue(pass, out ProfilingSampler sampler))
            {
                sampler = new ProfilingSampler(pass.DisplayName);
                samplers.Add(pass, sampler);
            }

            using (new ProfilingScope(commandBuffer, sampler))
            {
                pass.Execute(context, deltaTime);
            }
        }

        Graphics.ExecuteCommandBuffer(commandBuffer);
    }

    private void Build()
    {
        if (built)
        {
            return;
        }

        if (visualEffect == null)
        {
            visualEffect = GetComponent<VisualEffect>();
        }

        if (effect == null || visualEffect == null || passLibrary == null || passLibrary.Length == 0)
        {
            Debug.LogError(
                "SimulationWorld: EffectAsset, VisualEffect and pass library compute shaders must be assigned.",
                this);
            enabled = false;
            return;
        }

        source = effect.ResolveSource();
        particles = new ParticleSet();
        source.Setup(particles);

        AutoRegisterAttributes();
        InitializePositionFromRest();

        touchBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, InputRouter.MaxTouches, TouchForce.Stride);
        touchBuffer.SetData(new TouchForce[InputRouter.MaxTouches]);

        context = new SimContext(particles, passLibrary, touchBuffer);
        commandBuffer = new CommandBuffer { name = "M3D Simulation" };
        context.Cmd = commandBuffer;

        IReadOnlyList<SimPass> passes = effect.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            SimPass pass = passes[i];
            if (pass == null)
            {
                continue;
            }

            try
            {
                pass.Initialize(context);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"SimulationWorld: failed to initialize pass '{pass.DisplayName}': {exception.Message}",
                    this);
                Teardown();
                enabled = false;
                return;
            }
        }

        BindVisualEffect();

        simulationTime = 0f;
        built = true;
        Debug.Log(
            $"SimulationWorld: effect '{effect.name}' ready ({particles.Count} points, source '{source.Name}', {passes.Count} passes).",
            this);
    }

    /// <summary>
    /// Registers (zero-filled) every attribute any pass reads or writes and is not
    /// already provided by the source. Position is always present — VFX reads it.
    /// </summary>
    private void AutoRegisterAttributes()
    {
        IReadOnlyList<SimPass> passes = effect.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            SimPass pass = passes[i];
            if (pass == null)
            {
                continue;
            }

            RegisterMissing(pass.Reads);
            RegisterMissing(pass.Writes);
        }

        if (!particles.Schema.Has(BuiltinAttributes.Position))
        {
            RegisterZeroed(BuiltinAttributes.Position);
        }
    }

    private void RegisterMissing(IReadOnlyList<AttributeId> ids)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (!particles.Schema.Has(ids[i]))
            {
                RegisterZeroed(ids[i]);
            }
        }
    }

    private void RegisterZeroed(AttributeId id)
    {
        // GraphicsBuffer contents are undefined after creation — zero-fill once at build.
        GraphicsBuffer buffer = particles.RegisterAttribute(id);
        buffer.SetData(new byte[buffer.count * buffer.stride]);
    }

    /// <summary>
    /// One-time position = restPosition so dynamics-only chains (no CopyRestPass)
    /// start from the source shape instead of the origin.
    /// </summary>
    private void InitializePositionFromRest()
    {
        if (particles.TryGet(BuiltinAttributes.RestPosition, out GraphicsBuffer rest) &&
            particles.TryGet(BuiltinAttributes.Position, out GraphicsBuffer position))
        {
            Graphics.CopyBuffer(rest, position);
        }
    }

    private void BindVisualEffect()
    {
        GraphicsBuffer positions = particles.Get(BuiltinAttributes.Position);
        visualEffect.SetGraphicsBuffer(PositionBufferPropertyName, positions);

        if (visualEffect.HasFloat(SpawnCountPropertyName))
        {
            visualEffect.SetFloat(SpawnCountPropertyName, particles.Count);
        }

        if (particles.Count > VfxCapacity)
        {
            Debug.LogWarning(
                $"SimulationWorld: {particles.Count} points exceed VFX capacity {VfxCapacity}; extra points will not be rendered.",
                this);
        }

        visualEffect.Reinit();
    }

    private void Teardown()
    {
        built = false;
        particles?.Dispose();
        particles = null;
        touchBuffer?.Dispose();
        touchBuffer = null;
        commandBuffer?.Release();
        commandBuffer = null;
        samplers.Clear();
        context = null;
        source = null;
    }
}
