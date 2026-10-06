# Getting Started — для новых программистов

Краткий онбординг. Детали — [`capabilities.md`](capabilities.md), архитектура — [`architecture.md`](architecture.md), статус — [`status.md`](status.md).  
**Каталог пассов** (назначение, dt, Pass Library): [`pass-catalog.md`](pass-catalog.md).  
Решения: [`adr-001`](adr-001-field-resources-m2a.md), [`ADR-002`](last/ADR-002-Generic-P2G-Scatter.md), [`ADR-003`](last/ADR-003-Generic-Field-Slot-Naming.md), [`ADR-004`](last/ADR-004-Gradient-Sample-Pass.md), [`ADR-005`](last/ADR-005-Presence-Density-P2G-Scatter.md), [`ADR-006`](last/ADR-006-Diffuse-Field-Pass.md), [`ADR-007`](last/ADR-007-Scalar-Field-Decay.md), [`ADR-008`](last/ADR-008-Multi-Field-Per-Kernel-Binding.md), [`ADR-009`](last/ADR-009-Gray-Scott-Reaction-Diffusion.md), [`ADR-011`](last/ADR-011-Boids-Alignment-DeltaTime-And-Blur.md), [`ADR-012`](last/ADR-012-Kinematic-Heading-Boids.md), [`ADR-013`](ADR/ADR-013-Sampler-Verification+Velocity-Field-Self-Advection.md), [`ADR-014`](ADR/ADR-014-GPU-Numeric-Test-Harness.md)–[`ADR-028`](ADR/ADR-028-Limited-MacCormack-Dye.md). План фазы: [`plan-stable-fluid.md`](plan-stable-fluid.md) · [`last/roadmap_m2a.md`](last/roadmap_m2a.md).

---

## Что это

GPU-фреймворк интерактивных симуляций (Unity 6 + Compute + VFX Graph):

```
EffectAsset → ParticleSet + FieldSet → SimPass pipeline → Render binders
```

- **Источник** заполняет `restPosition` (куб / mesh / bitmap), либо **None** — без частиц (field-only).
- **Пассы** меняют particles и/или fields на GPU.
- **Binders** показывают результат (Primitive-квады частиц, debug field quad). `Particle Render Mode` на эффект не влияет.

Один эффект = один `EffectAsset`: источник + **декларации полей** + список пассов.

Есть: particle passes, field foundation, **P2G velocity + density**, **G2P gradient**, **Diffuse** / **DiffuseVelocity**, **AdvectVelocityField** (self-advection, ADR-013), **AdvectScalar** (пассивный dye, ADR-023), **SteerToVelocityField** (Reynolds alignment), **AddNormalized*** + **HeadingSteer** (kinematic boids), **Scalar Decay**, **multi-field Role A/B**, **Gray-Scott** (+ SeedScalarDisk), **Source Kind = None**, hybrid touch demo, тач/мышь, **кернелы Stam-проекции** (Divergence / ZeroMean / Jacobi / SubtractPhiGradient / SolidWallVelocity) и **пресет Fluid2D** (меню Create/Assign, InputRouter = GroundXZ, quads velocity+dye). Эксперимент VC: **VorticityConfinement** + `Fluid2D_Vorticity.asset` (`BorderMargin=2`, не production; look интерьера не взят, торнадо рамки сняты).  
Пока нет: trail/persistence buffer, emitters с lifetime. Spatial hash P1 — пробник ([ADR-034](ADR/ADR-034-Spatial-Hash-And-Teams.md)). Замер S10 записан в [status.md](status.md). Источник **Swarm** выбирается в инспекторе EffectAsset ([ADR-036](ADR/ADR-036-Swarm-Source.md)): диски с `teamId` и курсом. У эффекта есть список **teams**, максимум 8 ([ADR-037](ADR/ADR-037-Team-Profile.md)): цвет хранится и не используется, буфер читают сила соседей и курс. Пресет **Boids_hash** собирает хэш, гистограмму, силу и курс ([ADR-040](ADR/ADR-040-Boids-Hash-Preset-And-Play-Gate.md)). Меню `Tools/M3D/Create Boids Hash Effect`, сцена `Assets/Scenes/Boids_Hash.unity`.

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
   - **Boids_mk1** — kinematic field-flocking (ADR-012: AddNormalized* + HeadingSteer; Speed≈20) и хвост `HeadingToValue` с fire-LUT ([ADR-031](ADR/ADR-031-Primitive-Only-And-Value-Palette.md)). Частицы всегда рисуются Primitive-квадами; `VisualEffect` для сборки мира не нужен. Меню Enable/Disable Primitive только пишут `Particle Render Mode` в ассет, картинку не меняют. **Particle Size** — мировые единицы квада (0.05 на весь кадр мигает: квад меньше пикселя); правка применяется после выхода из Play и новой сборки.
   - **Physarum** — след `trail` и сенсорный поворот ([ADR-032](ADR/ADR-032-Physarum-Steer.md)). Меню `Tools/M3D/Create Physarum Effect` создаёт ассет и, если в сцене есть `SimulationWorld`, назначает его и Pass Library. Картинка сети в Play не калибровалась.
   - **Physarum_Fluid** — тот же след, снос полем `velocity` ([ADR-033](ADR/ADR-033-Physarum-Trail-Advect.md)). Меню `Tools/M3D/Create Physarum Fluid Effect`. В `Test1` сейчас назначен этот эффект. В Play InputRouter = **GroundXZ**. Толчок пальцем не калибровался.
   - **HashProbe_30k / HashProbe_100k** — замер spatial hash, не демо. Сцены `Assets/Scenes/HashProbe_30k.unity` и `HashProbe_100k.unity`. Меню `Tools/M3D/Create Spatial Hash Probe` создаёт оба ассета и обе сцены и не трогает `Test1`. В Play кнопки Hash и Render переключают билдер и рисование частиц без Rebuild. Замер: четыре режима по 10 с (хэш вкл/выкл × рендер вкл/выкл). Если все четыре упёрлись в 60 FPS, смотреть `100k`.
   - **Boids_hash** — Swarm, хэш, поле `hashCount`, сила соседей и курс из команды. Сцена `Assets/Scenes/Boids_Hash.unity`. Меню `Tools/M3D/Create Boids Hash Effect` создаёт ассет и сцену. 5c закрыт, порог доли cap снят ([`play-5c-boids-hash.md`](last/play-5c-boids-hash.md)). Замер на S10: `Assets/Scripts/FPSDisplay.cs` на любом объекте сцены; окно 30 с прогрева + 30 с замера, кнопки `MaxN`, сила, рендер; результат из `adb logcat -s Unity | findstr M3D_PERF`, строка с `phase=DONE`. Мерить по времени кадра, а не по fps (экран 60 Гц); числа и ограничения: [`status.md`](status.md), ADR-040 errata.
   - **Gray-Scott-Agents** — то же one-way: частицы красят GS, поле их не рулит.
   - **Fluid2D** — Stam: Touch → Seed(dye) → project → wall → advect(`velocity`) → wall → AdvectScalar (None, XZ, velocity+dye quads). Порядок project→advect оставлен после [ADR-024](ADR/ADR-024-Harris-Order-Experiment.md). Эталон Harris: `Fluid2D_HarrisOrder.asset` (Assign, не Demo Effects). Эксперимент VC: `Fluid2D_Vorticity.asset`. Эксперимент F2.2: `Fluid2D_MacCormackDye.asset` (не production; look не взят).
3. Play. Для hybrid / Gray-Scott / **Fluid2D** / **Physarum_Fluid**: InputRouter = **GroundXZ**.

Меню: `Tools/M3D/Create Demo Effects`, `Create Gray-Scott-Boids Effect`, `Create Gray-Scott-Agents Effect`, **`Create Physarum Effect`**, **`Create Physarum Fluid Effect`**, **`Create Spatial Hash Probe`**, **`Create Boids Hash Effect`**, **`Create Fluid2D Effect`**, **`Create Fluid2D HarrisOrder Experiment`**, **`Create Fluid2D Vorticity Experiment`**, **`Create Fluid2D MacCormackDye Experiment`**, **`Add F2.3 Scripted Stroke To Scene`**, **`ADR-012 Reconfigure Boids_mk1`**, **`Enable/Disable Primitive Render On Boids_mk1`**, **`Register ParticleBillboard Shader`**, `Setup Open Scene`, `Assign HybridTouchField To Scene`, `Assign AgentFieldEcho To Scene`, **`Assign Fluid2D To Scene`**, **`Assign Fluid2D HarrisOrder Experiment To Scene`**, **`Assign Fluid2D Vorticity Experiment To Scene`**, **`Assign Fluid2D MacCormackDye Experiment To Scene`**, **`Setup Post-Processing (HDR + Bloom + ACES)`**.  
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
4. Эксперимент vorticity (не production): `Tools/M3D/Assign Fluid2D Vorticity Experiment To Scene`. Цепочка Harris + VC до проекции, `ε_vc=1`, `BorderMargin=2`. Visual F2.1b закрыт — [`play-F2.1b-touch.md`](last/play-F2.1b-touch.md). **Create Vorticity не вызывать** (`radiusUV` на диске 0.16). F2.2 закрыт: `Fluid2D_MacCormackDye.asset` — [`play-F2.2-touch.md`](last/play-F2.2-touch.md). F2.3 закрыт: скриптованный жест, production остаётся Fluid2D — [`play-F2.3-touch.md`](last/play-F2.3-touch.md). Create Fluid2D / Harris / Vorticity / MacCormackDye не жать (factory `radiusUV=0.08` сотрёт look). После F2.3 visual выключить `ScriptedTouchStroke`. Правка пасса в инспекторе меняет `.asset` сразу (ScriptableObject).

### Boids → Gray-Scott

1. Пресет: `Assets/Effects/Gray-Scott-Boids.asset` (меню `Tools/M3D/Create Gray-Scott-Boids Effect`).
2. Alignment: `ClearAccum → ScatterVelocity → Normalize → Decay → DiffuseVelocity×6 → … → SteerToVelocityField` (`flockVel` 64×64); не `SampleVelocityField` (тот — Hybrid/Echo).
3. Presence Replace: `ClearField(agentPresence)` → ClearAccum → ScatterDensity → Normalize → … → React → `AgentBoost` / `AgentErode` (`gain`≈0.3).
4. U/V/`agentPresence` обязаны совпасть по Resolution+plane (M2c); Size 50 как boids, presence/U/V res 128.
5. One-way без обратной связи: `Assets/Effects/Gray-Scott-Agents.asset` — только Curl/Drag/… + presence→GS (нет flockVel / Sample*/Steer).
6. Чистые boids: `Assets/Effects/Boids_mk1.asset` (Speed≈20). Порядок: P2G → ClearVelocity → AddNormalized* → HeadingSteer → Integrate → Wrap → `HeadingToValue`. Reconfigure: `Tools/M3D/ADR-012 Reconfigure Boids_mk1` (список пассов он собирает сам и хвост `HeadingToValue` сохраняет).
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
- vorticity confinement (эксперимент): `VorticityConfinementPass` — Transport, WritePingPong, `ε_vc`, `borderMargin=2` на ассете, `Fluid2D_Vorticity.asset`, не Fluid2D; ADR-027;
- limited MacCormack dye (эксперимент F2.2, закрыт, look не взят): Copy + ±AdvectScalar + combine, scratch `dyeMacScratch`, не Role C; `Fluid2D_MacCormackDye.asset`; ADR-028;
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
| Источники (Cube/Mesh/Bitmap/**None**/**Swarm**) | `Sources/DataSourceKind.cs`, `Sources/SwarmSource.cs`, `Sources/NoneSource.cs` |
| P2G SM / Channels validation | `Runtime/FieldAccumPassValidator.cs` |
| Field descriptor / requests | `Core/FieldDescriptor.cs`, `Core/FieldSet.cs`, `Core/FieldAccumBuffer.cs` |
| Контракт пасса | `Runtime/SimPass.cs` |
| Field / P2G / Gradient / Fluid kernels | `Passes/FieldPasses.cs`, `Passes/FluidPasses.cs`, `Passes/P2GPasses.cs`, `Shaders/GPU/Passes/` |
| Binders | `PrimitiveParticleBinder.cs` (частицы), `FieldDebugQuadsBinder.cs` (поля). `VfxParticleBinder.cs` мир не создаёт |
