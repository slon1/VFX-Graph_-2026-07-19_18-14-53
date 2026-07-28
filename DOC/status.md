# Status — M3D Framework (Milestone 2a)

**Дата:** 2026-07-26  
**Итерация:** 5.0 — FieldSet + resource-oriented hybrid pipeline  
**Проект:** Unity `6000.4.3f1` / URP / VFX Graph 17.x  
**Сцена:** `Assets/Scenes/Test1.unity`  
**Онбординг:** [`getting-started.md`](getting-started.md) · архитектура: [`architecture.md`](architecture.md)

---

## Цель

```
EffectAsset (Fields + Passes)
    → ParticleSet + FieldSet
    → SimPass pipeline (World-owned ping-pong swap)
    → Render binders (VFX / FieldQuad)
```

Доменные симуляции = композиции Pass, не подсистемы.  
Simulation Resources (`ParticleSet`, `FieldSet`) ≠ services (Input, GPU, binders).

---

## Milestone 2a — что сделано

### Resources

| Тип                            | Роль                                                              |
| ------------------------------ | ----------------------------------------------------------------- |
| `FieldDescriptor` / `FieldId`  | декларация на EffectAsset: format, resolution, plane basis, clear |
| `FieldRequest` + `FieldAccess` | `Read` / `WriteInPlace` / `WritePingPong`                         |
| `FieldSet` / `SimField`        | dual RenderTexture, `Current`/`Next`, `Swap`                      |
| Policy C                       | runtime **не** автосоздаёт поля; missing → Build error            |

Plane basis на дескрипторе (`origin`, `axisU`/`axisV`, `size`) — не на InputRouter.

### Passes (новые)

| Pass                           | Access                   | Роль                                     |
| ------------------------------ | ------------------------ | ---------------------------------------- |
| `TouchInjectVelocityFieldPass` | WriteInPlace             | тач → splat в velocity field; `MaxFieldSpeed` clamp |
| `DecayFieldPass`               | WritePingPong            | `* exp(-rate·dt)`; доказывает World Swap |
| `SampleVelocityFieldPass`      | Read + particle Velocity | G2P hybrid                               |

`PassCategory`: +`Emit`, +`Transport`.  
`FieldKernelPass` — зеркало `ParticleKernelPass` для полей.

### World / binders

- После каждого пасса с `WritePingPong` → `FieldSet.Swap` (data-driven). Swap пропускается, если пасс не записал dispatch (`SimPass.LastExecuteDispatched`) — иначе `Current` перещёлкнулся бы на устаревшую текстуру.
- `IRenderBinder`: `VfxParticleBinder` (bind once), `FieldQuadBinder` (rebind Current каждый кадр).
- Валидация: имя поля, semantic; каналы — exact для write / `>=` для read; конфликт InPlace vs PingPong на одном пассе; одинаковый plane у всех полей пасса; одинаковый resolution у write-полей.
- Ноль аллокаций в кадре: декларации `FieldReads`/`FieldWrites` кэшируются через `FieldRequestSets.Single` (кэш пересобирается при смене имени поля в инспекторе).
- `TouchInjectVelocityFieldPass.MaxFieldSpeed` (дефолт 20, `<= 0` = без лимита) — clamp magnitude после splat.

### Демо

| Asset                                    | Идея                                                                       |
| ---------------------------------------- | -------------------------------------------------------------------------- |
| `HybridTouchField`                       | Touch → Inject → Decay → Sample → Integrate (+ Drag/Bounds), velocity quad |
| TwistedCube / GalaxySwirl / ReactiveDust | без изменений (пустой Fields)                                              |

Меню: `Tools/M3D/Create Demo Effects`, `Assign HybridTouchField To Scene`.

---

## Файлы (новые / ключевые)

```
Assets/Scripts/Core/       FieldDescriptor.cs, FieldSet.cs
Assets/Scripts/Runtime/    SimPass (+Field*), SimContext, SimulationWorld,
                           IRenderBinder, VfxParticleBinder, FieldQuadBinder
Assets/Scripts/Passes/     FieldPasses.cs
Assets/Shaders/GPU/Passes/ FieldPasses.compute
Assets/Shaders/GPU/        FieldDebug.shader
Assets/Effects/            HybridTouchField.asset
```

---

## Вне скоупа (M2b+)

Stable Fluids (advect/project/Jacobi), particle emitters/lifetime, spatial hash/boids, unified Resources abstraction, 3D/voxels.
