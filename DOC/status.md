# Status — M3D Framework (Milestone 2b.1)

**Дата:** 2026-08-02  
**Итерация:** 5.1 — Generic P2G scatter (atomic accum → average)  
**Проект:** Unity `6000.4.3f1` / URP / VFX Graph 17.x  
**Сцена:** `Assets/Scenes/Test1.unity`  
**Онбординг:** [`getting-started.md`](getting-started.md) · архитектура: [`architecture.md`](architecture.md) · ADR: [`last/ADR-002-Generic-P2G-Scatter.md`](last/ADR-002-Generic-P2G-Scatter.md)

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
| `SampleVelocityFieldPass`      | Read + particle Velocity | G2P hybrid                               |

---

## Milestone 2b.1 — P2G scatter (готово)

| Тип | Роль |
| --- | --- |
| `FieldAccumBuffer` | uint SoA: `[values…][count]`; `BufferCount = Channels+1` |
| `ClearFieldAccumPass` | zero accum |
| `ScatterVelocityToFieldPass` | InterlockedAdd + plane projection |
| `NormalizeVelocityAccumPass` | average decode → field Add |

Build: Channels↔descriptor; Scale/Bias Scatter↔Normalize; SM **Normalize→Unclear** (enabled-only).  
Encode: NaN-guard, затем `max(0,·)`. Нет runtime overflow-guard на сумму.

Демо **AgentFieldEcho**: CurlNoise→Drag→SpeedLimit→Integrate→ClearAccum→Scatter→Normalize→Decay (accumulate-onto-decaying; без ClearFieldPass). Replace = добавить ClearFieldPass перед ClearAccum.

Меню: `Create Demo Effects`, `Assign AgentFieldEcho To Scene`.  
Тесты: `FieldAccumPassValidatorTests`, `FieldRequestTests`.

---

## Файлы (ключевые)

```
Assets/Scripts/Core/       FieldAccumBuffer.cs, FieldSet.cs, FieldDescriptor.cs
Assets/Scripts/Runtime/    SimPass (+FieldAccum*), FieldAccumPassValidator, SimulationWorld
Assets/Scripts/Passes/     FieldPasses.cs, P2GPasses.cs
Assets/Shaders/GPU/Passes/ FieldPasses.compute, P2GPasses.compute
Assets/Shaders/GPU/Includes/ FieldSampling.hlsl
Assets/Effects/            HybridTouchField.asset, AgentFieldEcho.asset
Assets/Tests/Editor/       FieldRequestTests.cs, FieldAccumPassValidatorTests.cs
```

---

## Вне скоупа (далее)

M2b.2 Gradient sample · M2b.3 Diffuse · AgentFieldDensity (Replace) · Stable Fluids · spatial hash · AggregationMode enum · runtime overflow guard
