---
name: Milestone 2 Field Resources
overview: "Milestone 2a: resource-oriented foundation — FieldSet + World-owned ping-pong, FieldRequest declarations (Read / WriteInPlace / WritePingPong), EffectAsset-owned field descriptors with plane basis, Inject/Decay/Sample + render binders. Acceptance: hybrid Touch → velocity field → particles → render without special cases in SimulationWorld. No Stable Fluids."
todos:
  - id: resource-model
    content: FieldId/FieldDescriptor (plane basis)/FieldRequest with FieldAccess; extend SimPass + PassCategory
    status: completed
  - id: fieldset-pingpong
    content: FieldSet dual RT + World-owned Swap; clear on allocate; format validation
    status: completed
  - id: context-world
    content: SimContext Fields registry; World allocate/validate from EffectAsset only; auto-swap after WritePingPong passes
    status: completed
  - id: effectasset-editor
    content: EffectAsset.Fields + Materialize missing fields; pass field refs
    status: completed
  - id: field-passes
    content: TouchInjectVelocityField + DecayField + SampleVelocityField kernels/C#
    status: completed
  - id: render-binders
    content: "IRenderBinder: VfxParticleBinder + FieldQuadBinder; World present step"
    status: completed
  - id: hybrid-demo
    content: HybridTouchField EffectAsset + regression particle demos
    status: completed
  - id: docs-verify
    content: Update DOC (RFC principles) + Unity MCP smoke test
    status: completed
isProject: false
---

# Milestone 2a — Simulation Resources + Field foundation

## Decisions (locked)

- **Scope:** foundation + thin hybrid slice only. No Stable Fluids / Jacobi / pressure.
- **Field ownership (C):** `[EffectAsset](Assets/Scripts/Runtime/EffectAsset.cs)` declares field resources. Passes only **reference by name** and declare **compatibility requirements**. Runtime **never** auto-creates undescribed fields (missing → Build error). Editor button **Materialize missing fields** adds default descriptors from pass requests.
- **Resources vs services:** Simulation Resources = `ParticleSet`, `FieldSet`. Services = Input (`TouchBuffer`), GPU (`CommandBuffer`, kernel library), Render binders. Services are not in the resource registry.
- **DoD:** hybrid EffectAsset runs as a normal pass list; `[SimulationWorld](Assets/Scripts/Runtime/SimulationWorld.cs)` has no `if (hasFields)` / fluid branches.
- **Field write semantics — two kinds, declared per request.** `FieldAccess`: `Read` / `WriteInPlace` / `WritePingPong`.
  - `WriteInPlace` — additive splat / read-modify-write of own texel via RWTexture, **no swap** (swapping would lose accumulated content). Used by `TouchInjectVelocityField`.
  - `WritePingPong` — pass reads `Current` (SRV), writes `Next` (UAV); field is swapped afterwards. Required for any neighbor-reading op; also usable for per-texel ops when we want to exercise the mechanism.
  - The earlier phrasing "field write implies ping-pong" is **replaced** by this explicit flag.
- **Swap ownership — World, not passes.** `SimulationWorld` inspects each pass's field requests while recording the frame `CommandBuffer` and calls `FieldSet.Swap(fieldId)` right after every `WritePingPong` pass. Passes never call Swap and don't know ping-pong exists. CPU-side swap between recorded dispatches is correct because texture params are bound per-pass at record time (same pattern as buffer binding in `ParticleKernelPass.Execute`).
- **Field plane is owned by the FieldDescriptor, not InputRouter.** `InputRouter`'s `CameraFacing` mode moves the interaction plane with the camera — a world-space field cannot be mapped to it. The descriptor carries an explicit plane basis (origin + right/up axes + size, see Phase A.1); inject kernels project touch world positions onto that basis themselves. For the M2a demo the plane is static (XZ or XY at a fixed transform); InputRouter should use a matching mode (`GroundXZ`) but nothing in the field pipeline depends on it.

```mermaid
flowchart LR
  Touch[Touch service]
  Inject[InjectVelocityField]
  Vel[velocity Field]
  Sample[SampleVelocityToParticles]
  Parts[ParticleSet]
  Int[Integrate]
  Bind[Render binders]
  Touch --> Inject --> Vel --> Sample --> Parts --> Int --> Bind
```



## Phase A — Resource model

**1. Resource identity & requests**

- `FieldId` (name + semantic: Velocity / Dye / Scalar / Custom).
- `FieldDescriptor`: id, `GraphicsFormat` (start: `R16_SFloat`, `R16G16_SFloat`), resolution (e.g. 256²), clear value, **plane basis**: `origin` (float3), `axisU` / `axisV` (float3, normalized), `size` (float2, world units). worldPos → UV = `((p - origin)·axisU / size.x + 0.5, (p - origin)·axisV / size.y + 0.5)`. Same struct is pushed to kernels as uniforms (see `FieldParams`, Phase B).
- `FieldRequest`: field name (string ref) + `FieldAccess` (`Read` / `WriteInPlace` / `WritePingPong`, see Decisions) + compatibility requirement.
  - **Compatibility requirements are minimal on purpose:** semantic + minimum channel count (e.g. `SampleVelocityField` requires semantic `Velocity`, ≥2 channels). Do **not** require exact format/resolution — field resolution is a quality knob the user tunes on the EffectAsset, and Materialize defaults must not fight validation.
- Extend `[SimPass](Assets/Scripts/Runtime/SimPass.cs)`: keep `Reads`/`Writes` (`AttributeId`) for particles **unchanged**; add `FieldReads` / `FieldWrites` (`IReadOnlyList<FieldRequest>`, default empty arrays on the base class so all 17 existing passes compile untouched). **Decision: separate lists, not a unified `Resources` abstraction** — unify later (M3+) when a third resource kind (spatial hash) shows what the real abstraction is. `AutoRegisterAttributes` keeps working as-is. This asymmetry (particles auto-registered, fields declaration-owned) is intentional — see Migration note.
- Extend `PassCategory` with `Emit`, `Transport` (Shape/Force/Dynamics stay). Update the grouped **Add Pass** menu in `EffectAssetEditor` to include the new categories.

**2. FieldSet + ping-pong**

- New `FieldSet` / `SimField`: dual **plain `RenderTexture`** (`enableRandomWrite = true`), `Current`/`Next`, `Swap(fieldId)`. **Not RTHandle** — the URP RTHandle system is for camera-scaled targets and only adds coupling here.
- Allocate from EffectAsset field declarations only.
- **Clear on allocate:** RenderTexture contents are undefined after creation (same issue as `RegisterZeroed` for GraphicsBuffers) — clear **both** textures of each field to the descriptor's clear value at Build (`cmd.SetRenderTarget` + `cmd.ClearRenderTarget`, or a tiny clear kernel).
- **Format validation at Build:** `SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.LoadStore)` — fail loudly with the field name and format in the message (policy C: no silent fallbacks).
- `FieldSet` implements `IDisposable`; `SimulationWorld.Teardown` releases it alongside particles/touchBuffer/commandBuffer so **Rebuild** keeps working.
- World-space mapping comes from the descriptor's plane basis (see A.1), not from `[InputRouter](Assets/Scripts/Runtime/InputRouter.cs)` — the demo scene just configures InputRouter to a matching static plane.

**3. SimContext / World**

- `[SimContext](Assets/Scripts/Runtime/SimContext.cs)`: `Particles` + `Fields` (registry by name) + services (`TouchBuffer`, `Cmd`, `Time`, `FindKernel`).
- `[SimulationWorld](Assets/Scripts/Runtime/SimulationWorld.cs)` Build:
  1. source → `ParticleSet` (as now);
  2. allocate `FieldSet` from **EffectAsset.Fields** (validate format support, clear both textures — see A.2);
  3. validate every pass field request: field exists + semantic/channel-count compatible (minimal requirements, A.1). Unknown name or incompatible request → `LogError` with pass name + field name, abort Build (same pattern as pass Initialize failure today);
  4. auto-register **particle** attrs from Reads/Writes (unchanged);
  5. init passes → binders.
- Frame loop stays: sample input → execute passes → execute binders. No field-specific branch. The only field-aware step in the generic loop: after recording a pass whose `FieldWrites` contains a `WritePingPong` request, World calls `Swap` for that field (see Decisions — World owns Swap; this is data-driven from declarations, not a domain branch).

**4. EffectAsset + editor**

- Serialize `List<FieldDescriptor> fields` on EffectAsset.
- EffectAsset inspector: Fields list + existing passes; button **Materialize missing fields from passes** (scan `FieldReads`/`FieldWrites`, append defaults, do not overwrite existing). Defaults per semantic: Velocity → `R16G16_SFloat`, Scalar/Dye → `R16_SFloat`, 256², clear = 0, XZ plane 10×10 at origin. Because pass requirements are minimal (semantic + channels), the user can freely retune format/resolution afterwards without breaking validation.
- Pass inspector: field name dropdown / string ref to declared fields (fail loudly if typo at Build). Dropdown enumerates `EffectAsset.Fields` of the asset that owns the pass (passes are `[SerializeReference]` inside the same asset, so the editor has access).

## Phase B — Passes & kernels (resource-specific implementations)

HLSL: `Assets/Shaders/GPU/Passes/FieldPasses.compute` (+ small includes if needed). Kernels resource-specific; naming by **operation**.

**`FieldKernelPass` base class** (mirror of `ParticleKernelPass`, keeps "new op = 2 files by convention"):

- resolves kernel via `context.FindKernel`;
- auto-binds field textures from `FieldReads`/`FieldWrites` by naming convention (below);
- pushes `FieldParams` uniforms; dispatches `ceil(resolution / 8)²` groups (`numthreads(8,8,1)`);
- subclasses only override `SetParams` (same contract as particle passes).

**Binding conventions (fixed now, all field kernels follow them):**

- `Read` / `WritePingPong` source → `Texture2D {fieldName}Read` (SRV, sample with `SamplerState sampler_linear_clamp` + `SampleLevel`);
- `WritePingPong` target → `RWTexture2D {fieldName}Write` (UAV, this is `Next`);
- `WriteInPlace` → `RWTexture2D {fieldName}Write` bound to `Current` (single texture, no swap);
- shared cbuffer `FieldParams`: `FieldResolution` (int2), `FieldTexelSize` (float2), plane basis `FieldOrigin` / `FieldAxisU` / `FieldAxisV` / `FieldSize` (from the descriptor, A.1).


| Pass                           | Role                | Access                          | In → Out                                                    |
| ------------------------------ | ------------------- | ------------------------------- | ------------------------------------------------------------ |
| `TouchInjectVelocityFieldPass` | Emit                | `velocity`: **WriteInPlace**    | Touch service → additive splat into `velocity` (project touch world pos onto field plane basis; falloff × touch delta) |
| `DecayFieldPass`               | thin field op       | `velocity`: **WritePingPong**   | `next = current * exp(-decayRate * dt)` → World swaps         |
| `SampleVelocityFieldPass`      | Transport G2P       | `velocity`: **Read** + particle `Velocity` write | bilinear-sample velocity field at particle position → add to `BuiltinAttributes.Velocity` |


Particle `IntegratePass` reused as-is after Sample.

Implementation notes:

- **Decay is per-texel and doesn't strictly need ping-pong** (it could run in-place). We deliberately route it through `WritePingPong` anyway — it's the M2a vehicle for proving the Swap machinery end-to-end before advection (a true neighbor-reading op) arrives in M2b. Document this in the pass comment so the choice doesn't look accidental.
- Decay (not per-frame clear, not blur) is the thin op: cheaper than blur, and exponential decay toward zero prevents inject from accumulating forever. R16F flush-to-zero on repeated multiply is desired behavior here (field genuinely settles to 0) — worth a kernel comment.
- `SampleVelocityFieldPass` is the hybrid pass: it declares **both** a particle write (`Velocity`) and a field read — this is exactly what the dual Reads/Writes + FieldReads/FieldWrites split on SimPass must support. Bilinear `SampleLevel` requires the source to be SRV-bound; ping-pong/`Read` access guarantees no simultaneous UAV binding.
- Kernels never see `Current`/`Next` or swap logic — only `{fieldName}Read` / `{fieldName}Write`.

## Phase C — Render binders (services, not resources)

Extract present-step out of World’s hard-coded VFX block:

- `IRenderBinder`: `Initialize(SimContext)`, `Execute(SimContext)` after sim each frame.
- `VfxParticleBinder` — current PositionBuffer / SpawnCount / Reinit logic. One-time `SetGraphicsBuffer` in `Initialize` (particle buffers are stable for the world's lifetime); `Execute` is a no-op.
- `FieldQuadBinder` — world quad material sampling a declared field (debug RG velocity). **Must rebind in `Execute` every frame** (`material.SetTexture(..., field.Current)`) — ping-pong swaps change which texture is `Current`. Optional toggle on EffectAsset / World.
- This Initialize-once vs Execute-every-frame asymmetry between the two binders is expected — don't "fix" it.
- Binder `Execute` runs on the CPU after `Graphics.ExecuteCommandBuffer` (it only sets material/VFX bindings, no GPU work to record).

World iterates binders the same way as passes — still no domain branches.

## Phase D — Acceptance demo + docs

**EffectAsset** `Assets/Effects/HybridTouchField.asset`:

1. Declare field `velocity` (RG16F, 256², static plane basis; InputRouter set to the matching static mode, e.g. `GroundXZ`).
2. Passes: `TouchInjectVelocityField` → `DecayField` → `SampleVelocityField` → `Integrate` (+ light `Drag` / `BoxBounds` if needed for stability). Note: Decay after Inject means this frame's injection also decays slightly — expected and stable, mention in docs.
3. Binders: VFX particles + optional velocity quad.

Particle-only demos (`TwistedCube`, etc.) must keep working unchanged (empty Fields list).

**Acceptance also includes the negative path:** a pass referencing a misspelled/undeclared field name must produce a clear Build error naming the pass and the field (policy C is claimed — prove it), and **Materialize missing fields** must fix it.

Update `[DOC/architecture.md](DOC/architecture.md)` (resource-oriented RFC, resources vs services, WriteInPlace vs WritePingPong semantics, World-owned Swap, DoD), `[DOC/status.md](DOC/status.md)`, `[DOC/capabilities.md](DOC/capabilities.md)`, `[DOC/getting-started.md](DOC/getting-started.md)` (how to declare fields, materialize, hybrid demo).

Smoke via Unity MCP: compile, Play HybridTouchField (touch drag moves particles, velocity quad shows decaying trail), Play TwistedCube regression, Rebuild button in Play Mode (FieldSet must survive teardown/rebuild).

## Explicitly out of scope

Stable Fluids (advect/project/Jacobi), particle emitters/lifetime, spatial hash/boids, 3D/voxels, autogenesis of fields at runtime, unifying kernels across Particle/Field.

## Migration note

Particle attribute auto-registration stays. Only **fields** use declaration-owned policy (C). That asymmetry is intentional for M2a and documented.