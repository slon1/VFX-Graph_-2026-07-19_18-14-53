# Возможности проекта — M3D Framework

**Снимок:** 2026-08-02  
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
- Пассы: ClearField, TouchInjectVelocity, DecayField, SampleVelocityField.
- Texture slots: `FieldRead` / `FieldWrite` (не `{fieldName}…`; ADR-003 / M2b.1.1).
- Debug: `FieldQuadBinder` + shader `M3D/FieldDebug`.

### P2G (M2b.1)

- `FieldAccumBuffer` + ClearAccum / ScatterVelocity / NormalizeVelocity (average per texel).
- Композиция: accumulate-onto-decaying vs Replace (ClearField + ClearAccum + Scatter + Normalize).
- Демо: **AgentFieldEcho**.
- Normalize пишет в `FieldWrite` (generic slot).

### Pass library

| Категория | Примеры |
| --- | --- |
| Shape / Force / Dynamics | CopyRest, Twist, Gravity, Vortex, Integrate, Bounds, … |
| Emit / Transport | ClearField, TouchInject, Decay, SampleVelocity, ClearAccum, Scatter, Normalize |

### Демо-пресеты

| Пресет | Идея |
| --- | --- |
| TwistedCube | shape |
| GalaxySwirl / ReactiveDust | dynamics + touch на частицах |
| **HybridTouchField** | touch → velocity field → particles |
| **AgentFieldEcho** | particles → agentVelocity field (P2G) |

---

## Ограничения

| Тема | Сейчас |
| --- | --- |
| Нет Stable Fluids | advect/pressure — позже |
| Нет emitters | lifetime/compaction позже |
| Нет SpatialHash | boids/sand позже |
| Fields только 2D | R16 / RG16 |
| Policy C для fields | нет runtime autogen |
| P2G average only | Sum/Max — позже; overflow суммы — док, не guard |
