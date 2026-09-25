# Возможности проекта — M3D Framework

**Снимок:** 2026-09-16  
**Стек:** Unity 6 · URP · VFX Graph · UniTask  
**Онбординг:** [`getting-started.md`](getting-started.md) · [`pass-catalog.md`](pass-catalog.md) · [`architecture.md`](architecture.md) · [`status.md`](status.md) · [`roadmap`](last/roadmap_m2a.md)

---

## Что это

GPU playground / фреймворк: источники → SoA particles + grid fields → compute passes → VFX / debug quad.

```
Source → ParticleSet + FieldSet → SimPass pipeline → Binders
```

---

## Что умеет сейчас

### Particles / источники

| `DataSourceKind` | Поведение |
| --- | --- |
| Cube / Mesh / Bitmap | Заполняют `restPosition`, задают `ParticleSet` capacity |
| **None** | 0 частиц (`NoneSource`); field-only эффекты; particle-пассы no-op; VFX `SpawnCount=0` |

Builtins: `restPosition`, `position`, `velocity`, **`heading`**, `value`.  
Авторегистрация атрибутов по Reads/Writes — **пропускается** при Capacity=0 (None).

### Fields (M2a)

- Декларация на EffectAsset (`FieldDescriptor`: format, resolution, plane basis).
- `FieldAccess`: Read / WriteInPlace / WritePingPong (World-owned Swap, только после реального dispatch).
- Пассы: ClearField, TouchInjectVelocity, DecayField / **DecayFieldScalar**, SampleVelocityField, **SteerToVelocityField**, **AddNormalizedVelocityField**, **AddNormalizedGradientField**, **SampleGradientField**, **DiffuseField**, **DiffuseVelocityField**, **AdvectVelocityField**, **AdvectScalarPass**, **DivergenceFieldPass**, **JacobiPhiPass**, **ZeroMeanScalarPass**, **SubtractPhiGradientPass**, **SolidWallVelocityPass**, **VorticityConfinementPass**, **ClearVelocity**, **HeadingSteer**.
- Texture slots: `FieldRead` / `FieldWrite` (single-field); multi-field: `FieldReadA/B` + `FieldWriteA/B` (ADR-008 / M2c).
- `RepeatCount` (ADR-015): World повторяет `Execute + Swap` N раз за кадр (итерации решателя, не субшаги `dt`). Default 1; `JacobiPhiPass` переопределяет (дефолт 40).
- Единицы по семействам (ADR-016): RD/boids-диффузия — **texel** Laplacian без `/h²`; G2P-градиент — **UV** без `/Size`; fluid — **world**. Существующие texel/UV не меняются. `RequiresSquareTexel` проверяется на Build (`SquareTexelValidator`, ADR-017).
- Debug: `FieldDebugQuadsBinder` + `M3D/FieldDebug` — слоты (`VectorRg` / `ScalarHeatmap` + Gradient LUT + hdrIntensity), layout по AxisU. Desktop post-FX: global `M3D Volume` + `M3DVolumeProfile` (Bloom + ACES); на мобилке Volume выключает `M3DVolumeMobileGate` (ADR-025).

### Multi-field kernel (M2c)

- `FieldSlotRole` A/B на `FieldRequest`; `FieldRequestSets.Pair`.
- Multi-role: plane always matches (hard error). Resolution — у write и у `Load`; UV-read по контракту может отличаться (ADR-001 §8 / поправка ADR-008). `ValidateMatchingFieldGeometry` по умолчанию всё ещё требует matching Resolution у всех ролей; снятие для UV-read — **точечный opt-in-флаг** `AllowResolutionMismatch` на конкретном запросе (не общее правило для всех Read-ролей — сломало бы изолированные тесты `JacobiPhiPass`/`SubtractPhiGradientPass`). Флаг стоит только на `AdvectScalarPass.velocity` — F0.5, закрыт через [ADR-029](ADR/ADR-029-Cross-Resolution-Dye.md) (F3.0). `RequiresSquareTexel` (ADR-017) оставляет matching для fluid-`Load`. Proof `SwapFieldsPass`.

### Gray-Scott (M2c.1)

- `GrayScottPass` — dual Scalar U/V, WritePingPong; `GrayScottPasses.compute`.
- `SeedScalarDiskPass` — one-shot disk (ShouldDispatch / hasFired, reset на Initialize).
- Рекомендация: N=1–4 GrayScottPass за кадр при Speed=1 (калибровать эмпирически). ADR-009.
- Пресет: **`Assets/Effects/Gray-Scott.asset`** — `Source Kind = None`, поля **XZ**, `TouchInjectGrayScott` после React, debug quads U/V.
- Гибрид: **`Assets/Effects/Gray-Scott-Boids.asset`** — boids + `agentPresence` Replace P2G → `AgentBoost`/`AgentErode` (gain 0.3); U/V/presence res 128 / size 50.
- One-way: **`Assets/Effects/Gray-Scott-Agents.asset`** — движение частиц + presence → GS; **без** flock-полей и SampleVelocity/Gradient.

### P2G (M2b.1)

- `FieldAccumBuffer` + ClearAccum / ScatterVelocity / NormalizeVelocity (average per texel).
- Композиция: accumulate-onto-decaying vs Replace (ClearField + ClearAccum + Scatter + Normalize).
- Демо: **AgentFieldEcho**.
- Normalize пишет в `FieldWrite` (generic slot).

### Density P2G (M2b.2.1)

- `ScatterDensity` / `NormalizeDensityAccum` — sum (∝ count), Scalar field; `DensityPasses.compute`.
- **Replace:** ClearField(density) каждый кадр.  
- **Accumulate-onto-decaying:** без ClearField; после Normalize — `DecayFieldScalar` (M2b.3.1 / ADR-007).

### G2P gradient (M2b.2)

- `SampleGradientFieldPass` — Force: `velocity += ∇φ * Strength * dt` (Scalar field, ADR-004).
- Kernel в `GradientPasses.compute` (`Texture2D<float> FieldRead`).

### Diffuse (M2b.3) + velocity blur (ADR-011)

- `DiffuseFieldPass` — 5-point explicit Laplacian, WritePingPong, Scalar; `DiffusePasses.compute`.
- `DiffuseVelocityFieldPass` — тот же Laplacian на `float2` Velocity (`FieldPasses.compute`); для blur `flockVel` перед alignment.
- Рекомендация: `NormalizeDensity → несколько мягких Diffuse подряд в кадре → SampleGradient` (не «больше кадров ожидания»). Дальнодействие = rate × число Diffuse/кадр × размер текселя; вялость между далёкими кластерами — ожидаемая сходимость, не баг. ADR-006 / [`status.md`](status.md).

### Kinematic heading boids (ADR-012)

- `ClearVelocityPass` — GPU zero `velocity` (force accumulator reset).
- `AddNormalizedVelocityFieldPass` / `AddNormalizedGradientFieldPass` — unit direction × weight, **без dt**.
- `HeadingSteerPass` — nlerp `heading`, flatten Y, snap `velocity = heading * CruiseSpeed`.
- `Boids_mk1`: P2G → Clear → AddNormalized* → HeadingSteer → Integrate → Wrap (Speed=20) → `HeadingToValue` (present, без dt). **Не** Newton (Curl/Drag/Limit/Steer/SampleGradient).

### Alignment G2P (ADR-011)

- `SteerToVelocityFieldPass` — Force: Reynolds `v += (fieldVel − v) * strength * dt`. Gray-Scott-Boids / legacy.
- `DiffuseVelocityFieldPass` — blur `flockVel` перед G2P.

### Scalar Decay (M2b.3.1)

- `DecayFieldScalarPass` — `value * exp(-rate·dt)`, WritePingPong, Scalar; `DecayPasses.compute` (Load).
- Пайплайн памяти: `ClearAccum → ScatterDensity → NormalizeDensity → DecayFieldScalar → [Diffuse…] → SampleGradient`.

### Pass library

| Категория | Примеры |
| --- | --- |
| Shape / Force / Dynamics | CopyRest, Twist, Gravity, Vortex, **SampleGradient**, **AddNormalizedGradient**, **SteerToVelocityField**, **ClearVelocity**, **HeadingSteer**, Integrate, Bounds, … |
| Emit / Transport | ClearField, **SeedScalarDisk**, TouchInject, Decay / **DecayScalar**, **Diffuse** / **DiffuseVelocity**, **AdvectVelocity**, **AdvectScalar**, **Divergence** / **ZeroMeanScalar** / **Jacobi** / **SubtractPhiGradient** / **SolidWallVelocity** / **VorticityConfinement**, **SwapFields**, **GrayScott**, SampleVelocity, ClearAccum, ScatterVelocity/Density, Normalize |

### Демо-пресеты

| Пресет | Идея |
| --- | --- |
| TwistedCube | shape |
| GalaxySwirl / ReactiveDust | dynamics + touch на частицах |
| **HybridTouchField** | touch → velocity field → particles |
| **AgentFieldEcho** | particles → agentVelocity field (P2G) |
| **Gray-Scott** | field-only RD (`Source Kind = None`, XZ + touch inject) |
| **Boids_mk1** | kinematic heading + fields (ADR-012) + `HeadingToValue` и fire-LUT (ADR-031, EditMode; Play-цвета не закрыты). Primitive безусловный |
| **Gray-Scott-Boids** | boids → agentPresence → Boost/Erode U/V (+ field→boids) |
| **Gray-Scott-Agents** | agents → GS only (no field feedback) |
| **Fluid2D** | Stam: Touch → Seed(dye) → Divergence → ZeroMean → Jacobi×40 → Subtract → SolidWall → Advect → SolidWall → AdvectScalar (None, GroundXZ, velocity+dye quads) |
| **Fluid2D_Vorticity** | эксперимент: Advect → VC → Project → Wall → dye (`ε_vc=1`, `borderMargin=2`); не production; look интерьера не взят |
| **Fluid2D_MacCormackDye** | эксперимент F2.2: VC-цепочка + limited MacCormack dye (`dyeMacScratch`); не production; look не взят (клубы как Vorticity) |
| **Fluid2D_HighResDye** | эксперимент F3.0: Stam как Fluid2D, `dye` 512² при `velocity` 128² (тот же Size 32); visual: острее, тот же гриб; не production |

---

### Fluid projection (Stam)

- Кернелы проекции: Divergence / **ZeroMeanScalar** (`fluidD` zero-mean перед Jacobi) / Jacobi / SubtractPhiGradient / **SolidWallVelocity** (free-slip `u·n=0` на рамке). **VorticityConfinement** — look-тикет F2.1 на `Fluid2D_Vorticity.asset` (Harris + VC до проекции), не в production.
- Пресет `Fluid2D` есть (`Assets/Effects/Fluid2D.asset`, меню Create/Assign): Touch → Seed(dye) → project → wall → advect(velocity) → wall → **AdvectScalar**; quads velocity+dye. Сводка Stam: [ADR-019](ADR/ADR-019-Fluid2D-Solver.md). Порядок project→advect измерен ([ADR-024](ADR/ADR-024-Harris-Order-Experiment.md) §7) и подтверждён visual F2.3 — Harris на λ=8 чуть чище по D, look-нить не взят; production не меняли. Эталон: `Fluid2D_HarrisOrder.asset`. F2 закрыта: [ADR-026](ADR/ADR-026-F2-Small-Scale-Structure.md). VC — [ADR-027](ADR/ADR-027-Vorticity-Confinement-Pass.md). F2.2 — [ADR-028](ADR/ADR-028-Limited-MacCormack-Dye.md). Odd-even интерьера на dye **не виден** — MAC не открывали.
- **F3.0 (готово):** `Fluid2D_HighResDye.asset` — тот же Stam-порядок, `dye` 512² при `velocity`/`fluidD`/`fluidPhi` 128² (F0.5 cross-res dye, [ADR-029](ADR/ADR-029-Cross-Resolution-Dye.md)). Visual заметно острее `Fluid2D` на том же жесте, макро-силуэт (гриб) не меняется — меньше bilinear-смаз tracer, не новая топология velocity. Production `Fluid2D.asset` не менялся.

---

## Ограничения

| Тема | Сейчас |
| --- | --- |
| Stable Fluids (Stam-minimum + F2/F3 скоуп) | F1 закрыта ([ADR-019](ADR/ADR-019-Fluid2D-Solver.md)). F2.0–F2.3 закрыты ([ADR-026](ADR/ADR-026-F2-Small-Scale-Structure.md)): look (тонкая нить) не взят VC/MacCormack, production остаётся `Fluid2D` Project→Advect. F3.0 закрыт ([ADR-029](ADR/ADR-029-Cross-Resolution-Dye.md)): cross-res dye (F0.5) численно и визуально острее на `Fluid2D_HighResDye.asset`, эксперимент, не production. MAC / viscosity вне фазы |
| Texel / UV Laplacian и градиент | Параметры Diffuse / GrayScott / SampleGradient зависят от разрешения и `Size` (ADR-016); не «исправлять» `/h²` |
| Нет emitters | lifetime/compaction позже |
| Нет SpatialHash | boids/sand позже |
| Fields только 2D | R16 / RG16 |
| Policy C для fields | нет runtime autogen |
| P2G average only | Sum/Max — позже; overflow суммы — док, не guard |
| Multi-field kernel ≤2 | Role A/B; Gray-Scott = M2c.1 |
