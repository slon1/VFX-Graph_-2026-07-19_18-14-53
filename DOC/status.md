# Status — M3D Framework (Milestone 2c.1)

**Дата:** 2026-08-08  
**Итерация:** 5.8 — Gray-Scott Reaction-Diffusion (+ `DataSourceKind.None`)  
**Проект:** Unity `6000.4.3f1` / URP / VFX Graph 17.x  
**Сцена:** `Assets/Scenes/Test1.unity`  
**Онбординг:** [`getting-started.md`](getting-started.md) · [`pass-catalog.md`](pass-catalog.md) · [`architecture.md`](architecture.md) · [`capabilities.md`](capabilities.md)  
**ADR / roadmap:** [`adr-001`](adr-001-field-resources-m2a.md) · [`ADR-002`](last/ADR-002-Generic-P2G-Scatter.md) · [`ADR-003`](last/ADR-003-Generic-Field-Slot-Naming.md) · [`ADR-004`](last/ADR-004-Gradient-Sample-Pass.md) · [`ADR-005`](last/ADR-005-Presence-Density-P2G-Scatter.md) · [`ADR-006`](last/ADR-006-Diffuse-Field-Pass.md) · [`ADR-007`](last/ADR-007-Scalar-Field-Decay.md) · [`ADR-008`](last/ADR-008-Multi-Field-Per-Kernel-Binding.md) · [`ADR-009`](last/ADR-009-Gray-Scott-Reaction-Diffusion.md) · [`roadmap`](last/roadmap_m2a.md)

---

## Цель

```
EffectAsset (Fields + Passes)
    → ParticleSet + FieldSet (+ FieldAccumBuffer)
    → SimPass pipeline (World-owned ping-pong swap)
    → Render binders (VFX / FieldQuad)
```

Доменные симуляции = композиции Pass, не подсистемы.  
Simulation Resources (`ParticleSet`, `FieldSet`) ≠ services (Input, GPU, binders).

---

## Milestone 2a — foundation (готово)

FieldSet / FieldAccess / ClearField / TouchInject / Decay / SampleVelocity. Policy C.

---

## Milestone 2b.1 / 2b.1.1 — P2G velocity + generic slots (готово)

`FieldAccumBuffer`, Scatter/Normalize **velocity** (average), `FieldRead`/`FieldWrite`. Демо AgentFieldEcho.

---

## Milestone 2b.2 — Gradient sample (готово)

`SampleGradientFieldPass` + `GradientPasses.compute`. Force: `∇ * Strength * dt`. ADR-004.

---

## Milestone 2b.2.1 — Density P2G (готово)

`ScatterDensityToFieldPass` / `NormalizeDensityAccumPass` + `DensityPasses.compute`.  
Sum decode (∝ count), Scalar/`density`. ADR-005.  
Тест: `ScatterDensityFieldPassTests`.

---

## Milestone 2b.3 — Diffuse Field (готово)

`DiffuseFieldPass` + `DiffusePasses.compute`. Explicit 5-point Laplacian, WritePingPong, Scalar.  
CFL: `rate·dt ≲ 0.2–0.25`. ADR-006. Тест: `DiffuseFieldPassTests`.

### Рекомендация: дальнодействие Diffuse-cohesion

Дальнодействие cohesion через Diffuse — компромисс между **rate**, **числом `DiffuseFieldPass` за кадр** и **разрешением поля**.

«Несколько мягких Diffuse лучше одного большого rate» значит именно **несколько пассов подряд в одном EffectAsset / одном кадре**, а не «подождать больше кадров симуляции». Чем крупнее тексель, тем быстрее (в числе текселей) фронт добегает до соседа.

MCP mid-smoke (res=64, rate=0.15, dt=1, пики на texel 10 и 50): mid `grad` до Diffuse ≈0; после N≈40–80 шагов `grad.x` к большему пику, но \|g\| растёт медленно. Если в итоговой cohesion-демке притяжение между далёкими кластерами вялое — это не баг, а следствие этой сходимости. Лечится: несколько последовательных `DiffuseFieldPass` в кадре, более грубое поле, или выше rate в пределах CFL (≲0.25).

---

## Milestone 2b.3.1 — Scalar Decay (готово)

`DecayFieldScalarPass` + `DecayPasses.compute` (Load). `SimShaderIds.DecayFactor` общий с velocity Decay.  
Default rate 1.5. ADR-007. Тест: `DecayFieldScalarPassTests`.

**Replace:** ClearField(density) каждый кадр → ClearAccum → Scatter → Normalize → …  
**Accumulate-onto-decaying:** ClearAccum → Scatter → Normalize → **DecayFieldScalar** → [Diffuse…] → SampleGradient (без ClearField).

---

## Milestone 2c — Multi-field-per-kernel (готово)

`FieldSlotRole` A/B, `FieldRequestSets.Pair`, слоты `FieldReadA/B`/`FieldWriteA/B`.  
Single-role пассы остаются на `FieldRead`/`FieldWrite`. Multi-role: geometry hard error (Resolution + plane).  
Proof: `SwapFieldsPass` + `MultiFieldTestPasses.compute`. ADR-008.  
Тесты: `FieldSlotNamingTests`, `SwapFieldsPassTests`.

---

## Milestone 2c.1 — Gray-Scott (готово)

`GrayScottPass` (U+V multi-role WritePingPong) + `SeedScalarDiskPass` (one-shot via `ShouldDispatch`/`hasFired`).  
`GrayScottPasses.compute`. Defaults Du/Dv/F/k калибровочные. `saturate` на выходе — clamp, не замена CFL. ADR-009.  
Рекомендация: **N=1–4 `GrayScottPass` подряд за кадр** — компромисс скорость реакции vs стабильность; калибровать при Speed=1.  
Тесты: `GrayScottPassTests`, `SeedScalarDiskPassTests`.  
Пресет: `Assets/Effects/Gray-Scott.asset`.

### Field-only: `DataSourceKind.None`

`NoneSource` → `ParticleSet` capacity 0. Авторегистрация particle-атрибутов пропускается; particle-пассы no-op (`Count==0`); VFX `SpawnCount=0`.  
Для RD / grid-only эффектов (Gray-Scott) — **Source Kind = None**, не «куб с 1 частицей».

---

## Файлы (ключевые)

```
Assets/Scripts/Passes/     FieldPasses.cs (GrayScott, Seed, Swap, …), P2GPasses.cs
Assets/Scripts/Runtime/    SimPass.cs (ShouldDispatch, roles), SimulationWorld.cs
Assets/Shaders/GPU/Passes/ GrayScottPasses, MultiFieldTestPasses, DiffusePasses, …
Assets/Tests/Editor/       GrayScottPassTests, SeedScalarDiskPassTests, …
```

---

## Вне скоупа (далее)

LUT/trail (M2d) · Stable Fluids · spatial hash · AggregationMode enum
