# Getting Started — для новых программистов

Краткий онбординг. Детали — [`capabilities.md`](capabilities.md), архитектура — [`architecture.md`](architecture.md), статус — [`status.md`](status.md).  
**Каталог пассов** (назначение, dt, Pass Library): [`pass-catalog.md`](pass-catalog.md).  
Решения: [`adr-001`](adr-001-field-resources-m2a.md), [`ADR-002`](last/ADR-002-Generic-P2G-Scatter.md), [`ADR-003`](last/ADR-003-Generic-Field-Slot-Naming.md), [`ADR-004`](last/ADR-004-Gradient-Sample-Pass.md), [`ADR-005`](last/ADR-005-Presence-Density-P2G-Scatter.md), [`ADR-006`](last/ADR-006-Diffuse-Field-Pass.md), [`ADR-007`](last/ADR-007-Scalar-Field-Decay.md), [`ADR-008`](last/ADR-008-Multi-Field-Per-Kernel-Binding.md), [`ADR-009`](last/ADR-009-Gray-Scott-Reaction-Diffusion.md), [`ADR-011`](last/ADR-011-Boids-Alignment-DeltaTime-And-Blur.md), [`ADR-012`](last/ADR-012-Kinematic-Heading-Boids.md), [`ADR-013`](ADR/ADR-013-Sampler-Verification+Velocity-Field-Self-Advection.md), [`ADR-014`](ADR/ADR-014-GPU-Numeric-Test-Harness.md)–[`ADR-025`](ADR/ADR-025-PostFX-HDR-Bloom-ACES.md). План фазы: [`plan-stable-fluid.md`](plan-stable-fluid.md) · [`last/roadmap_m2a.md`](last/roadmap_m2a.md).

---

## Что это

GPU-фреймворк интерактивных симуляций (Unity 6 + Compute + VFX Graph):

```
EffectAsset → ParticleSet + FieldSet → SimPass pipeline → Render binders
```

- **Источник** заполняет `restPosition` (куб / mesh / bitmap), либо **None** — без частиц (field-only).
- **Пассы** меняют particles и/или fields на GPU.
- **Binders** показывают результат (VFX Graph, debug field quad).

Один эффект = один `EffectAsset`: источник + **декларации полей** + список пассов.

Есть: particle passes, field foundation, **P2G velocity + density**, **G2P gradient**, **Diffuse** / **DiffuseVelocity**, **AdvectVelocityField** (self-advection, ADR-013), **AdvectScalar** (пассивный dye, ADR-023), **SteerToVelocityField** (Reynolds alignment), **AddNormalized*** + **HeadingSteer** (kinematic boids), **Scalar Decay**, **multi-field Role A/B**, **Gray-Scott** (+ SeedScalarDisk), **Source Kind = None**, hybrid touch demo, тач/мышь, **кернелы Stam-проекции** (Divergence / ZeroMean / Jacobi / SubtractPhiGradient / SolidWallVelocity) и **пресет Fluid2D** (меню Create/Assign, InputRouter = GroundXZ, quads velocity+dye).  
Пока нет: trail/persistence buffer, spatial hash / emitters с lifetime.

---

## Как пользоваться

### Демо

1. `Assets/Scenes/Test1.unity` → `SimulationWorld.Effect`.
2. Пресеты в `Assets/Effects/`:
   - **TwistedCube** — shape-цепочка.
   - **GalaxySwirl** / **ReactiveDust** — dynamics + touch.
   - **HybridTouchField** — Touch → velocity field → particles (+ velocity quad).
   - **AgentFieldEcho** — CurlNoise → P2G scatter velocity → field quad (без тача).
   - **Gray-Scott** — field-only RD (`Source Kind = None`, поля на **XZ**, quads U/V; тач после React).
   - **Gray-Scott-Boids** — boids + `agentPresence` P2G → Boost/Erode в U/V (plane 50×50; `flockVel` 64 + Steer/DiffuseVelocity).
   - **Boids_mk1** — kinematic field-flocking (ADR-012: AddNormalized* + HeadingSteer; Speed≈20).
   - **Gray-Scott-Agents** — то же one-way: частицы красят GS, поле их не рулит.
   - **Fluid2D** — Stam: Touch → Seed(dye) → project → wall → advect(`velocity`) → wall → AdvectScalar (None, XZ, velocity+dye quads). Порядок project→advect оставлен после [ADR-024](ADR/ADR-024-Harris-Order-Experiment.md). Эталон Harris: `Fluid2D_HarrisOrder.asset` (Assign, не Demo Effects).
3. Play. Для hybrid / Gray-Scott / **Fluid2D**: InputRouter = **GroundXZ**.

Меню: `Tools/M3D/Create Demo Effects`, `Create Gray-Scott-Boids Effect`, `Create Gray-Scott-Agents Effect`, **`Create Fluid2D Effect`**, **`Create Fluid2D HarrisOrder Experiment`**, **`ADR-012 Reconfigure Boids_mk1`**, `Setup Open Scene`, `Assign HybridTouchField To Scene`, `Assign AgentFieldEcho To Scene`, **`Assign Fluid2D To Scene`**, **`Assign Fluid2D HarrisOrder Experiment To Scene`**, **`Setup Post-Processing (HDR + Bloom + ACES)`**.  
После смены пассов/полей в Play — **Rebuild** на SimulationWorld.  
Pass Library: GS — `GrayScottPasses` + `TouchGrayScottPasses` + `AgentFieldFeedbackPasses`.  
Пост-обработка (desktop, ADR-025): в `Test1` уже есть global `M3D Volume` + `M3DVolumeProfile` (Bloom + ACES). Повторный Setup идемпотентен. На мобилке Volume выключает `M3DVolumeMobileGate`. Не править `DefaultVolumeProfile.asset` (тестовый ассет пакета URP).

### Field-only (без частиц)

1. `Source Kind = None` на EffectAsset (не Cube с малым resolution).
2. Дескрипторы полей на **XZ** (`axisV = Z`), как Hybrid — чтобы совпасть с GroundXZ.
3. Цепочка: `SeedScalarDisk(V)` → `GrayScottPass` × N → **`TouchInjectGrayScott`**; U clear=1, V clear=0; debug quads на U и V.
4. Пресет: `Assets/Effects/Gray-Scott.asset`. Для мягкой кисти: `InputRouter.touchStrength ≈ 1` (дефолт 10 ≈ жёсткий диск). Каталог: [`pass-catalog.md`](pass-catalog.md).

### Fluid2D (Stam)

1. Пресет: `Assets/Effects/Fluid2D.asset` (меню `Create Fluid2D Effect` → сразу `Assign Fluid2D To Scene`; InputRouter = GroundXZ).
2. Цепочка: Touch → Seed(dye) → Divergence → ZeroMean → Jacobi×40 → Subtract → SolidWall → Advect(velocity) → SolidWall → AdvectScalar.
3. Debug quads: velocity (`colorScale=0.125`) и dye (heatmap). Play, тач по плоскости XZ. После Create guid часто новый — без Assign слот сцены смотрит в старый ассет.

### Boids → Gray-Scott

1. Пресет: `Assets/Effects/Gray-Scott-Boids.asset` (меню `Tools/M3D/Create Gray-Scott-Boids Effect`).
2. Alignment: `ClearAccum → ScatterVelocity → Normalize → Decay → DiffuseVelocity×6 → … → SteerToVelocityField` (`flockVel` 64×64); не `SampleVelocityField` (тот — Hybrid/Echo).
3. Presence Replace: `ClearField(agentPresence)` → ClearAccum → ScatterDensity → Normalize → … → React → `AgentBoost` / `AgentErode` (`gain`≈0.3).
4. U/V/`agentPresence` обязаны совпасть по Resolution+plane (M2c); Size 50 как boids, presence/U/V res 128.
5. One-way без обратной связи: `Assets/Effects/Gray-Scott-Agents.asset` — только Curl/Drag/… + presence→GS (нет flockVel / Sample*/Steer).
6. Чистые boids: `Assets/Effects/Boids_mk1.asset` (Speed≈20). Порядок: P2G → ClearVelocity → AddNormalized* → HeadingSteer → Integrate → Wrap. Reconfigure: `Tools/M3D/ADR-012 Reconfigure Boids_mk1`.
### Свой эффект с полями

1. `Create → M3D → Effect Asset`.
2. Добавить пассы (Emit/Transport для полей).
3. **Materialize missing fields from passes** — или вручную заполнить Fields.
4. Runtime **не** создаст поле сам: опечатка в имени → ошибка Build с именем пасса и поля.
5. Debug-quadы: список **Debug Field Quads** (имя, mode, colorScale, **LUT** Gradient, **hdrIntensity**). Убрать слот = скрыть. Несколько слотов → quads рядом по AxisU.
   - ScalarHeatmap: `colorScale` нормализует значение→UV LUT + альфу; `hdrIntensity` — множитель цвета после LUT (для Bloom, не влияет на альфу). Stops градиента — LDR `[0,1]`.
   - Правка LUT/hdrIntensity применяется на **Rebuild** (печётся в Setup биндера, не live в Play). `hdrIntensity > 1` на heatmap даёт реальный Bloom через `M3D Volume` (ADR-025).

Типичный hybrid:

`TouchInjectVelocityField → DecayField → SampleVelocityField → Integrate`

Типичный P2G (память поля, velocity):

`ClearFieldAccum → ScatterVelocity → NormalizeVelocity → DecayField`  
(Replace: вставить `ClearFieldPass` перед ClearAccum.)

Типичный density Accumulate (после M2b.3.1):

`ClearAccum → ScatterDensity → NormalizeDensity → DecayFieldScalar → [Diffuse…] → SampleGradient`  
(Replace: `ClearField(density)` каждый кадр вместо DecayScalar.)

---

## Как добавить пасс

### Particle (как раньше)

Kernel в `Shape/Force/DynamicsPasses.compute` + класс `: ParticleKernelPass`.  
Буферы = имена атрибутов (`position`, `velocity`, …).

### Field

1. Kernel в `Assets/Shaders/GPU/Passes/FieldPasses.compute` (`numthreads(8,8,1)`).
2. Имена текстур: single-field — `FieldRead` / `FieldWrite`; multi-field — `FieldReadA/B` + `FieldWriteA/B` (`FieldSlotRole`, ADR-008). Multi-role требует одинаковые Resolution + plane.
3. Класс `: FieldKernelPass`, объявить `FieldWrites` / `FieldReads` с `FieldAccess`:
   - **WriteInPlace** — splat в Current, без swap.
   - **WritePingPong** — Current→Next, World сделает Swap (только если пасс записал dispatch).
   - **Read** — sample Current.
4. Декларации возвращать через `FieldRequestSets.Single(ref cache, ...)` с `[NonSerialized]`-полем кэша — World читает `FieldWrites` каждый кадр, `new[] {...}` в свойстве даст мусор в каждом кадре (образец — `FieldPasses.cs`).
5. Совместимость: semantic + каналы (`FieldRequest.Channels`: для write — exact match UAV layout; для read — minimum). Precision/resolution — quality knobs.
6. Один пасс = один plane (origin/axisU/axisV/size) у всех полей; write-поля обязаны иметь одинаковое resolution (диспатч сайзится по primary). Read-поля могут отличаться по resolution (normalized UV).

Per-frame обнуление **текстуры** поля: `ClearFieldPass` — `SimField.ClearCurrent` → `ClearValue`.

### P2G (частица → поле)

1. Velocity: kernels в `P2GPasses.compute` (average decode).
2. Density: kernels в `DensityPasses.compute` (sum decode, ∝ count).
   - **Replace:** `ClearField(density)` каждый кадр.
   - **Accumulate-onto-decaying:** без ClearField; после Normalize — `DecayFieldScalar` (`DecayPasses.compute`, Load).
3. Diffuse: kernel в `DiffusePasses.compute` (5-point Load Laplacian); несколько мягких шагов лучше одного большого rate.
4. `: ParticleToFieldScatterPass` / `: NormalizeFieldAccumPass` (+ `ClearFieldAccumPass`).
5. Списки: `FieldAccumClears` / `FieldAccumWrites` / `FieldAccumReads` (не путать с текстурными FieldWrites).
6. `Channels` = value-каналы; count всегда последний в accum (`BufferCount = Channels+1`).
7. Build проверяет Channels↔descriptor, Scale/Bias, state machine (см. `FieldAccumPassValidator`).
8. Sampling/plane: `FieldSampling.hlsl` + `FieldShaderParams.Push`.

Hybrid (field + particles):
- значение: `SampleVelocityFieldPass` — Transport, `ParticleKernelPass` + `FieldReads` (Hybrid/Echo; **без** dt);
- alignment (Reynolds): `SteerToVelocityFieldPass` — Force, `v += (fieldVel−v)*strength*dt` (`saturate`), ADR-011; Gray-Scott-Boids;
- alignment (kinematic): `AddNormalizedVelocityFieldPass` — unit dir × weight, **без dt**, ADR-012 `Boids_mk1`;
- градиент (Newton): `SampleGradientFieldPass` — Force, `∇ * Strength * dt`, kernel в `GradientPasses.compute`;
- cohesion/separation (kinematic): `AddNormalizedGradientFieldPass` — unit ∇ × weight, **без dt**, ADR-012;
- kinematic integrate: `ClearVelocityPass` → AddNormalized* → `HeadingSteerPass` (snap cruise speed) → Integrate;
- сглаживание scalar: `DiffuseFieldPass` — Transport, WritePingPong; CFL `rate·dt ≲ 0.2–0.25`;
- сглаживание velocity: `DiffuseVelocityFieldPass` — тот же Laplacian на `float2` (`FieldPasses.compute`); 6× на `flockVel` 64×64;
- self-advection velocity: `AdvectVelocityFieldPass` — Transport, WritePingPong, semi-Lagrangian; `dissipationRate` → `exp(-rate·dt)` на CPU, 0=выкл; ADR-013;
- пассивный dye: `AdvectScalarPass` — Transport, dye WritePingPong A + velocity Read B; backtrace `uv − u·dt/Size`; ADR-023;
- Stam projection: `DivergenceFieldPass` → `ZeroMeanScalarPass` → `JacobiPhiPass` → `SubtractPhiGradientPass` → `SolidWallVelocityPass` (`FluidPasses.compute`);
- scalar decay: `DecayFieldScalarPass` — Transport; rate default 1.5;
- Gray-Scott: `GrayScottPass` + Seed + TouchInject; boids-гибрид: presence P2G → `AgentBoost`/`AgentErode` (`gain`); N=1–4 React; ADR-009;
- cohesion Replace: ClearField(density) → Scatter → Normalize → Diffuse×mild → SampleGradient;
- cohesion Accumulate: ClearAccum → Scatter → Normalize → **DecayFieldScalar** → [Diffuse…] → SampleGradient;
- дальнодействие: вялое притяжение далёких кластеров — ожидаемо (скорость сходимости Diffuse); лечится числом Diffuse за кадр, грубее resolution или rate≲0.25 — не «просто больше кадров». См. [`status.md`](status.md).

Добавить `.compute` в `SimulationWorld.Pass Library`, если новый файл.

---

## Куда смотреть

| Задача | Путь |
| --- | --- |
| Цикл / Swap / валидация полей | `Runtime/SimulationWorld.cs` |
| Источники (Cube/Mesh/Bitmap/**None**) | `Sources/DataSourceKind.cs`, `Sources/NoneSource.cs` |
| P2G SM / Channels validation | `Runtime/FieldAccumPassValidator.cs` |
| Field descriptor / requests | `Core/FieldDescriptor.cs`, `Core/FieldSet.cs`, `Core/FieldAccumBuffer.cs` |
| Контракт пасса | `Runtime/SimPass.cs` |
| Field / P2G / Gradient / Fluid kernels | `Passes/FieldPasses.cs`, `Passes/FluidPasses.cs`, `Passes/P2GPasses.cs`, `Shaders/GPU/Passes/` |
| Binders | `VfxParticleBinder.cs`, `FieldDebugQuadsBinder.cs` |
