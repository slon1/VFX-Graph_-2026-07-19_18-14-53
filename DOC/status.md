# Status — M3D Framework (Milestone 2b.2.1)

**Дата:** 2026-08-04  
**Итерация:** 5.4 — Density P2G scatter (sum)  
**Проект:** Unity `6000.4.3f1` / URP / VFX Graph 17.x  
**Сцена:** `Assets/Scenes/Test1.unity`  
**Онбординг:** [`getting-started.md`](getting-started.md) · [`architecture.md`](architecture.md) · [`capabilities.md`](capabilities.md)  
**ADR / roadmap:** [`adr-001`](adr-001-field-resources-m2a.md) · [`ADR-002`](last/ADR-002-Generic-P2G-Scatter.md) · [`ADR-003`](last/ADR-003-Generic-Field-Slot-Naming.md) · [`ADR-004`](last/ADR-004-Gradient-Sample-Pass.md) · [`ADR-005`](last/ADR-005-Presence-Density-P2G-Scatter.md) · [`roadmap`](last/roadmap_m2a.md)

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
Sum decode (∝ count), Scalar/`density`. Replace: ClearField каждый кадр. ADR-005.  
Тест: `ScatterDensityFieldPassTests`.

Cohesion-локально: ClearField(density) → ClearAccum → ScatterDensity → NormalizeDensity → SampleGradient → …

---

## Файлы (ключевые)

```
Assets/Scripts/Passes/     FieldPasses.cs, P2GPasses.cs
Assets/Shaders/GPU/Passes/ FieldPasses, P2GPasses, GradientPasses, DensityPasses
Assets/Tests/Editor/       … SampleGradientFieldPassTests, ScatterDensityFieldPassTests
```

---

## Вне скоупа (далее)

M2b.3 Diffuse + Scalar Decay · M2c multi-field · Stable Fluids · spatial hash · AggregationMode enum
