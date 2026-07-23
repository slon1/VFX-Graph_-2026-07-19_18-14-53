# Возможности проекта — M3D Framework

**Снимок:** 2026-07-23  
**Стек:** Unity 6 (`6000.4.3f1`) · URP · VFX Graph · UniTask (в проекте, в pipeline пока не используется)  
**Архитектура:** [`architecture.md`](architecture.md) · детали реализации: [`status.md`](status.md)

---

## Что это

GPU playground / фреймворк для интерактивных облаков точек на мобилке: источник заполняет SoA-буферы, пассы крутят их на compute через один CommandBuffer, VFX Graph рисует `position` без CPU readback.

```
Source → ParticleSet → SimPass pipeline → VFX Graph
```

Один эффект = один `EffectAsset` (пресет): источник + список пассов + параметры.

---

## Что умеет сейчас

### Источники (`IDataSource`)

| Источник   | Вход                 | Выход                    | Inspector                       |
| ---------- | -------------------- | ------------------------ | ------------------------------- |
| **Cube**   | resolution, cubeSize | `restPosition` сетка     | в EffectAsset                   |
| **Mesh**   | Mesh (Readable)      | вершины → `restPosition` | center / normalize / targetSize |
| **Bitmap** | Texture2D (Readable) | пиксель → XZ, Y=luma     | targetWidth, heightScale        |

### Dataset

- SoA: отдельный `GraphicsBuffer` на атрибут
- Builtins: `restPosition`, `position`, `velocity`, `value`
- Авторегистрация атрибутов по `Reads`/`Writes` пассов
- Custom: `AttributeId.Custom(name, type)` — API есть

### Pass library (~17)

| Категория    | Пассы                                                                                |
| ------------ | ------------------------------------------------------------------------------------ |
| **Shape**    | CopyRest, Twist, SpringToRest                                                        |
| **Force**    | Gravity, Drag, Vortex, Attractor, Repulsor, Noise, CurlNoise, Turbulence, TouchForce |
| **Dynamics** | Integrate, SpeedLimit, PlaneCollider, SphereCollider, BoxBounds                      |

Классификация — по роли в кадре (не по данным). Данные — в `Reads`/`Writes`.

Два режима композиции:

- shape-цепочка: `CopyRest → Twist`
- динамика: forces → Integrate → colliders (без CopyRest; стартовая поза копируется один раз при Build)

### Ввод

- `InputRouter`: мышь в Editor / touch на девайсе → `TouchBuffer`
- Плоскость взаимодействия: CameraFacing или GroundXZ
- `TouchForcePass`: drag (смазывание) + push (радиальное отталкивание)

### Визуализация

- VFX Graph читает `StructuredBuffer<float3>` (`ReadPositionBuffer.hlsl`)
- Exposed: `PositionBuffer`, `SpawnCount`
- Capacity ~1M

### Демо-пресеты

| Пресет           | Идея                                       |
| ---------------- | ------------------------------------------ |
| **TwistedCube**  | shape-паритет со старой сценой             |
| **GalaxySwirl**  | vortex + curl + touch smear + wrap bounds  |
| **ReactiveDust** | spring-to-rest + turbulence + finger repel |

---

## Ограничения (осознанные)

| Тема            | Сейчас                              |
| --------------- | ----------------------------------- |
| VFX capacity    | ~1M; большие mesh/bitmap обрезаются |
| Один source     | нет merge                           |
| Нет Fields      | fluid grid — Milestone 2            |
| Нет SpatialHash | boids/sand — Milestone 2            |
| Нет emitters    | lifetime/compaction — Milestone 2   |
| Async           | UniTask в проекте есть, Setup sync  |

---

## Быстрый старт

1. Открыть `Assets/Scenes/Test1.unity`
2. На `ParticleSimulation` → `SimulationWorld` → Effect (`TwistedCube` / `GalaxySwirl` / `ReactiveDust`)
3. Play Mode; для динамики — крутить мышью в Game View
4. Новые эффекты: Create → M3D → Effect Asset, Add Pass в инспекторе
5. Rebuild в Play Mode после смены списка пассов

Меню: `Tools/M3D/Create Demo Effects`, `Tools/M3D/Setup Open Scene`.
