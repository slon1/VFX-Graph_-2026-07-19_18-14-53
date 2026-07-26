using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.VFX;

/// <summary>
/// Runtime owner: simulation resources (ParticleSet, FieldSet), services (input, GPU),
/// frame loop, and render binders. Knows nothing about concrete effects —
/// everything comes from the EffectAsset. No domain branches (fluid/boids).
/// </summary>
public sealed class SimulationWorld : MonoBehaviour
{
    [SerializeField] private EffectAsset effect;
    [SerializeField] private ComputeShader[] passLibrary;
    [SerializeField] private VisualEffect visualEffect;
    [SerializeField] private InputRouter inputRouter;

    private ParticleSet particles;
    private FieldSet fields;
    private SimContext context;
    private CommandBuffer commandBuffer;
    private GraphicsBuffer touchBuffer;
    private IDataSource source;
    private readonly TouchForce[] touchScratch = new TouchForce[InputRouter.MaxTouches];
    private readonly Dictionary<SimPass, ProfilingSampler> samplers =
        new Dictionary<SimPass, ProfilingSampler>();
    private readonly List<IRenderBinder> binders = new List<IRenderBinder>();
    private FieldQuadBinder fieldQuadBinder;
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

    /// <summary>Rebuilds the whole world without restarting Play Mode.</summary>
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

            // Data-driven swap from FieldWrites declarations — not a domain branch.
            // Skipped when the pass early-outed without recording a dispatch,
            // otherwise Current would flip to a stale texture.
            if (pass.LastExecuteDispatched)
            {
                SwapPingPongFields(pass);
            }
        }

        Graphics.ExecuteCommandBuffer(commandBuffer);

        for (int i = 0; i < binders.Count; i++)
        {
            binders[i].Execute(context);
        }
    }

    private void SwapPingPongFields(SimPass pass)
    {
        IReadOnlyList<FieldRequest> writes = pass.FieldWrites;
        for (int i = 0; i < writes.Count; i++)
        {
            FieldRequest request = writes[i];
            if (request.Access == FieldAccess.WritePingPong)
            {
                fields.Swap(request.FieldName);
            }
        }
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

        commandBuffer = new CommandBuffer { name = "M3D Simulation" };
        fields = new FieldSet();

        try
        {
            fields.Allocate(effect.Fields, commandBuffer);
            Graphics.ExecuteCommandBuffer(commandBuffer);
            commandBuffer.Clear();
        }
        catch (Exception exception)
        {
            Debug.LogError($"SimulationWorld: field allocation failed: {exception.Message}", this);
            Teardown();
            enabled = false;
            return;
        }

        if (!ValidateFieldRequests())
        {
            Teardown();
            enabled = false;
            return;
        }

        AutoRegisterAttributes();
        InitializePositionFromRest();

        touchBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, InputRouter.MaxTouches, TouchForce.Stride);
        touchBuffer.SetData(new TouchForce[InputRouter.MaxTouches]);

        context = new SimContext(particles, fields, passLibrary, touchBuffer);
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

        SetupBinders();

        simulationTime = 0f;
        built = true;
        Debug.Log(
            $"SimulationWorld: effect '{effect.name}' ready ({particles.Count} points, " +
            $"{effect.Fields.Count} fields, source '{source.Name}', {passes.Count} passes).",
            this);
    }

    private bool ValidateFieldRequests()
    {
        IReadOnlyList<SimPass> passes = effect.Passes;
        for (int p = 0; p < passes.Count; p++)
        {
            SimPass pass = passes[p];
            if (pass == null)
            {
                continue;
            }

            if (!ValidateRequestList(pass, pass.FieldReads) ||
                !ValidateRequestList(pass, pass.FieldWrites))
            {
                return false;
            }

            // Same field must not mix WriteInPlace and WritePingPong on one pass.
            Dictionary<string, FieldAccess> writeAccess =
                new Dictionary<string, FieldAccess>(StringComparer.Ordinal);
            for (int i = 0; i < pass.FieldWrites.Count; i++)
            {
                FieldRequest request = pass.FieldWrites[i];
                if (writeAccess.TryGetValue(request.FieldName, out FieldAccess existing) &&
                    existing != request.Access)
                {
                    Debug.LogError(
                        $"SimulationWorld: pass '{pass.DisplayName}' has conflicting FieldAccess " +
                        $"on '{request.FieldName}' ({existing} vs {request.Access}).",
                        this);
                    return false;
                }

                writeAccess[request.FieldName] = request.Access;
            }
        }

        return true;
    }

    private bool ValidateRequestList(SimPass pass, IReadOnlyList<FieldRequest> requests)
    {
        for (int i = 0; i < requests.Count; i++)
        {
            FieldRequest request = requests[i];
            if (string.IsNullOrEmpty(request.FieldName))
            {
                Debug.LogError(
                    $"SimulationWorld: pass '{pass.DisplayName}' has an empty field name.",
                    this);
                return false;
            }

            if (!fields.TryGet(request.FieldName, out SimField field))
            {
                Debug.LogError(
                    $"SimulationWorld: pass '{pass.DisplayName}' references undeclared field " +
                    $"'{request.FieldName}'. Add it to EffectAsset.Fields or use Materialize missing fields.",
                    this);
                return false;
            }

            FieldDescriptor descriptor = field.Descriptor;
            if (descriptor.Semantic != request.RequiredSemantic &&
                request.RequiredSemantic != FieldSemantic.Custom)
            {
                Debug.LogError(
                    $"SimulationWorld: pass '{pass.DisplayName}' requires field '{request.FieldName}' " +
                    $"with semantic {request.RequiredSemantic}, but declaration is {descriptor.Semantic}.",
                    this);
                return false;
            }

            if (descriptor.ChannelCount < request.MinChannels)
            {
                Debug.LogError(
                    $"SimulationWorld: pass '{pass.DisplayName}' requires field '{request.FieldName}' " +
                    $"with >= {request.MinChannels} channels, but format has {descriptor.ChannelCount}.",
                    this);
                return false;
            }
        }

        return true;
    }

    private void SetupBinders()
    {
        binders.Clear();
        VfxParticleBinder vfxBinder = new VfxParticleBinder(visualEffect);
        vfxBinder.Initialize(context);
        binders.Add(vfxBinder);

        if (effect.ShowVelocityFieldQuad && fields.TryGet("velocity", out _))
        {
            fieldQuadBinder = new FieldQuadBinder("velocity", transform);
            fieldQuadBinder.Initialize(context);
            binders.Add(fieldQuadBinder);
        }
    }

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
        GraphicsBuffer buffer = particles.RegisterAttribute(id);
        buffer.SetData(new byte[buffer.count * buffer.stride]);
    }

    private void InitializePositionFromRest()
    {
        if (particles.TryGet(BuiltinAttributes.RestPosition, out GraphicsBuffer rest) &&
            particles.TryGet(BuiltinAttributes.Position, out GraphicsBuffer position))
        {
            Graphics.CopyBuffer(rest, position);
        }
    }

    private void Teardown()
    {
        built = false;

        fieldQuadBinder?.Dispose();
        fieldQuadBinder = null;
        binders.Clear();

        fields?.Dispose();
        fields = null;
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
