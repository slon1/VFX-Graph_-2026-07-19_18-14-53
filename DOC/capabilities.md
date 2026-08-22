# Возможности проекта — M3D Framework

**Снимок:** 2026-08-08  
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
- Пассы: ClearField, TouchInjectVelocity, DecayField / **DecayFieldScalar**, SampleVelocityField, **SteerToVelocityField**, **AddNormalizedVelocityField**, **AddNormalizedGradientField**, **SampleGradientField**, **DiffuseField**, **DiffuseVelocityField**, **AdvectVelocityField**, **ClearVelocity**, **HeadingSteer**.
- Texture slots: `FieldRead` / `FieldWrite` (single-field); multi-field: `FieldReadA/B` + `FieldWriteA/B` (ADR-008 / M2c).
- Debug: `FieldDebugQuadsBinder` + `M3D/FieldDebug` — слоты (`VectorRg` / `ScalarHeatmap` + Gradient LUT + hdrIntensity), layout по AxisU.

### Multi-field kernel (M2c)

- `FieldSlotRole` A/B на `FieldRequest`; `FieldRequestSets.Pair`.
- Multi-role: matching Resolution + plane (hard error); proof `SwapFieldsPass`.

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
- `Boids_mk1`: P2G → Clear → AddNormalized* → HeadingSteer → Integrate → Wrap (Speed=20). **Не** Newton (Curl/Drag/Limit/Steer/SampleGradient).

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
| Emit / Transport | ClearField, **SeedScalarDisk**, TouchInject, Decay / **DecayScalar**, **Diffuse** / **DiffuseVelocity**, **AdvectVelocity**, **SwapFields**, **GrayScott**, SampleVelocity, ClearAccum, ScatterVelocity/Density, Normalize |

### Демо-пресеты

| Пресет | Идея |
| --- | --- |
| TwistedCube | shape |
| GalaxySwirl / ReactiveDust | dynamics + touch на частицах |
| **HybridTouchField** | touch → velocity field → particles |
| **AgentFieldEcho** | particles → agentVelocity field (P2G) |
| **Gray-Scott** | field-only RD (`Source Kind = None`, XZ + touch inject) |
| **Boids_mk1** | kinematic heading + fields (ADR-012): AddNormalized* + HeadingSteer, DiffuseVelocity |
| **Gray-Scott-Boids** | boids → agentPresence → Boost/Erode U/V (+ field→boids) |
| **Gray-Scott-Agents** | agents → GS only (no field feedback) |

---

## Ограничения

| Тема | Сейчас |
| --- | --- |
| Stable Fluids неполный | velocity self-advection есть (ADR-013); dye/pressure/vorticity — позже |
| Нет emitters | lifetime/compaction позже |
| Нет SpatialHash | boids/sand позже |
| Fields только 2D | R16 / RG16 |
| Policy C для fields | нет runtime autogen |
| P2G average only | Sum/Max — позже; overflow суммы — док, не guard |
| Multi-field kernel ≤2 | Role A/B; Gray-Scott = M2c.1 |
