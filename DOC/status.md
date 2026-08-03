# Status — M3D Framework (Milestone 2b.2)

**Дата:** 2026-08-04  
**Итерация:** 5.3 — Sample Gradient Field (G2P force)  
**Проект:** Unity `6000.4.3f1` / URP / VFX Graph 17.x  
**Сцена:** `Assets/Scenes/Test1.unity`  
**Онбординг:** [`getting-started.md`](getting-started.md) · [`architecture.md`](architecture.md) · [`capabilities.md`](capabilities.md)  
**ADR / roadmap:** [`adr-001`](adr-001-field-resources-m2a.md) · [`ADR-002`](last/ADR-002-Generic-P2G-Scatter.md) · [`ADR-003`](last/ADR-003-Generic-Field-Slot-Naming.md) · [`ADR-004`](last/ADR-004-Gradient-Sample-Pass.md) · [`roadmap`](last/roadmap_m2a.md)

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

### Resources

| Тип                            | Роль                                                              |
| ------------------------------ | ----------------------------------------------------------------- |
| `FieldDescriptor` / `FieldId`  | декларация на EffectAsset: format, resolution, plane basis, clear |
| `FieldRequest` + `FieldAccess` | `Read` / `WriteInPlace` / `WritePingPong`                         |
| `FieldSet` / `SimField`        | dual RenderTexture, `Current`/`Next`, `Swap`                      |
| Policy C                       | runtime **не** автосоздаёт поля; missing → Build error            |

### Passes (field)

| Pass                           | Access                   | Роль                                     |
| ------------------------------ | ------------------------ | ---------------------------------------- |
| `ClearFieldPass`               | WriteInPlace             | Current → `ClearValue`                   |
| `TouchInjectVelocityFieldPass` | WriteInPlace             | тач → splat; `MaxFieldSpeed`             |
| `DecayFieldPass`               | WritePingPong            | `* exp(-rate·dt)`                        |
| `SampleVelocityFieldPass`      | Read + particle Velocity | G2P hybrid transport                     |
| `SampleGradientFieldPass`      | Read Scalar + Velocity   | G2P force ∇ → `* Strength * dt`          |

---

## Milestone 2b.1 — P2G scatter (готово)

| Тип | Роль |
| --- | --- |
| `FieldAccumBuffer` | uint SoA: `[values…][count]`; `BufferCount = Channels+1` |
| `ClearFieldAccumPass` | zero accum |
| `ScatterVelocityToFieldPass` | InterlockedAdd + plane projection |
| `NormalizeVelocityAccumPass` | average decode → field Add |

Демо **AgentFieldEcho**. ADR-002 / M2b.1.1 slots (ADR-003).

---

## Milestone 2b.2 — Gradient sample (готово)

`SampleGradientFieldPass` + `GradientPasses.compute` (`Texture2D<float> FieldRead`).  
Central differences; `FieldUvGradientToWorld`; Force integration; Scalar/`density`.  
ADR-004. Тест: `SampleGradientFieldPassTests`.

---

## Файлы (ключевые)

```
Assets/Scripts/Core/       FieldAccumBuffer.cs, FieldSet.cs, FieldDescriptor.cs
Assets/Scripts/Runtime/    SimPass (+FieldAccum*), FieldAccumPassValidator, SimulationWorld
Assets/Scripts/Passes/     FieldPasses.cs, P2GPasses.cs
Assets/Shaders/GPU/Passes/ FieldPasses.compute, P2GPasses.compute, GradientPasses.compute
Assets/Shaders/GPU/Includes/ FieldSampling.hlsl
Assets/Effects/            HybridTouchField.asset, AgentFieldEcho.asset
Assets/Tests/Editor/       FieldRequestTests, FieldAccumPassValidatorTests, FieldSlotNamingTests, SampleGradientFieldPassTests
```

---

## Вне скоупа (далее)

M2b.3 Diffuse · M2c multi-field-per-kernel · AgentFieldDensity (Replace) · Stable Fluids · spatial hash · AggregationMode enum · runtime overflow guard
