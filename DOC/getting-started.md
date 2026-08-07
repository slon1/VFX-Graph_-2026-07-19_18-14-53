# Getting Started — для новых программистов

Краткий онбординг. Детали — [`capabilities.md`](capabilities.md), архитектура — [`architecture.md`](architecture.md), статус — [`status.md`](status.md).  
**Каталог пассов** (назначение, dt, Pass Library): [`pass-catalog.md`](pass-catalog.md).  
Решения: [`adr-001`](adr-001-field-resources-m2a.md), [`ADR-002`](last/ADR-002-Generic-P2G-Scatter.md), [`ADR-003`](last/ADR-003-Generic-Field-Slot-Naming.md), [`ADR-004`](last/ADR-004-Gradient-Sample-Pass.md), [`ADR-005`](last/ADR-005-Presence-Density-P2G-Scatter.md), [`ADR-006`](last/ADR-006-Diffuse-Field-Pass.md), [`ADR-007`](last/ADR-007-Scalar-Field-Decay.md), [`ADR-008`](last/ADR-008-Multi-Field-Per-Kernel-Binding.md), [`ADR-009`](last/ADR-009-Gray-Scott-Reaction-Diffusion.md). План фазы: [`last/roadmap_m2a.md`](last/roadmap_m2a.md).

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

Есть: particle passes, field foundation, **P2G velocity + density**, **G2P gradient**, **Diffuse**, **Scalar Decay**, **multi-field Role A/B**, **Gray-Scott** (+ SeedScalarDisk), **Source Kind = None**, hybrid touch demo, тач/мышь.  
Пока нет: LUT/trail render, Stable Fluids, spatial hash / boids, particle emitters с lifetime.

---

## Как пользоваться

### Демо

1. `Assets/Scenes/Test1.unity` → `SimulationWorld.Effect`.
2. Пресеты в `Assets/Effects/`:
   - **TwistedCube** — shape-цепочка.
   - **GalaxySwirl** / **ReactiveDust** — dynamics + touch.
   - **HybridTouchField** — Touch → velocity field → particles (+ velocity quad).
   - **AgentFieldEcho** — CurlNoise → P2G scatter velocity → field quad (без тача).
   - **Gray-Scott** — field-only RD (`Source Kind = None`, quads U/V).
3. Play. Для hybrid: InputRouter = **GroundXZ**.

Меню: `Tools/M3D/Create Demo Effects`, `Setup Open Scene`, `Assign HybridTouchField To Scene`, `Assign AgentFieldEcho To Scene`.  
После смены пассов/полей в Play — **Rebuild** на SimulationWorld.  
Pass Library должен включать нужные `.compute` (для GS — `GrayScottPasses.compute`).

### Field-only (без частиц)

1. `Source Kind = None` на EffectAsset (не Cube с малым resolution).
2. Дескрипторы полей + field-пассы; particle-пассы можно не добавлять.
3. Пример: `SeedScalarDisk(V)` → `GrayScottPass` × N; U clear=1, V clear=0; debug quads на U и V.
4. Пресет: `Assets/Effects/Gray-Scott.asset`. Каталог: [`pass-catalog.md`](pass-catalog.md).

### Свой эффект с полями

1. `Create → M3D → Effect Asset`.
2. Добавить пассы (Emit/Transport для полей).
3. **Materialize missing fields from passes** — или вручную заполнить Fields.
4. Runtime **не** создаст поле сам: опечатка в имени → ошибка Build с именем пасса и поля.
5. Debug-quadы: список **Debug Field Quads** на EffectAsset (имя поля из dropdown + mode + colorScale). Убрать = скрыть. Несколько слотов → quads рядом по AxisU с подписью имени.

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
- значение: `SampleVelocityFieldPass` — Transport, `ParticleKernelPass` + `FieldReads`;
- градиент: `SampleGradientFieldPass` — Force, `∇ * Strength * dt`, kernel в `GradientPasses.compute`;
- сглаживание: `DiffuseFieldPass` — Transport, WritePingPong; CFL `rate·dt ≲ 0.2–0.25`;
- scalar decay: `DecayFieldScalarPass` — Transport; rate default 1.5;
- Gray-Scott: `GrayScottPass` (U+V) + `SeedScalarDiskPass` (one-shot); N=1–4 React за кадр при Speed=1; ADR-009;
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
| Field / P2G / Gradient kernels | `Passes/FieldPasses.cs`, `Passes/P2GPasses.cs`, `Shaders/GPU/Passes/` |
| Binders | `VfxParticleBinder.cs`, `FieldDebugQuadsBinder.cs` |
