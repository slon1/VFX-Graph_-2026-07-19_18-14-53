# Возможности проекта — M3D Framework

**Снимок:** 2026-07-26  
**Стек:** Unity 6 · URP · VFX Graph · UniTask  
**Онбординг:** [`getting-started.md`](getting-started.md) · архитектура: [`architecture.md`](architecture.md) · статус: [`status.md`](status.md)

---

## Что это

GPU playground / фреймворк: источники → SoA particles + grid fields → compute passes → VFX / debug quad.

```
Source → ParticleSet + FieldSet → SimPass pipeline → Binders
```

---

## Что умеет сейчас

### Particles

Источники Cube / Mesh / Bitmap → `restPosition`.  
Builtins: `restPosition`, `position`, `velocity`, `value`.  
Авторегистрация атрибутов по Reads/Writes пассов.

### Fields (M2a)

- Декларация на EffectAsset (`FieldDescriptor`: format, resolution, plane basis).
- `FieldAccess`: Read / WriteInPlace / WritePingPong (World-owned Swap, только после реального dispatch).
- Пассы: TouchInjectVelocity, DecayField, SampleVelocityField.
- Debug: `FieldQuadBinder` + shader `M3D/FieldDebug`.

### Pass library

| Категория | Примеры |
| --- | --- |
| Shape / Force / Dynamics | CopyRest, Twist, Gravity, Vortex, Integrate, Bounds, … |
| Emit / Transport | TouchInjectVelocityField, DecayField, SampleVelocityField |

### Демо-пресеты

| Пресет | Идея |
| --- | --- |
| TwistedCube | shape |
| GalaxySwirl / ReactiveDust | dynamics + touch на частицах |
| **HybridTouchField** | touch → velocity field → particles |

---

## Ограничения

| Тема | Сейчас |
| --- | --- |
| Нет Stable Fluids | advect/pressure — M2b |
| Нет emitters | lifetime/compaction позже |
| Нет SpatialHash | boids/sand позже |
| Fields только 2D | R16 / RG16 |
| Policy C для fields | нет runtime autogen |

---

## Быстрый старт

См. [`getting-started.md`](getting-started.md). Hybrid: Effect = HybridTouchField, InputRouter = GroundXZ, Play, водить мышью.
