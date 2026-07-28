# Getting Started — для новых программистов

Краткий онбординг. Детали — [`capabilities.md`](capabilities.md), архитектура — [`architecture.md`](architecture.md), статус — [`status.md`](status.md).

---

## Что это

GPU-фреймворк интерактивных симуляций (Unity 6 + Compute + VFX Graph):

```
EffectAsset → ParticleSet + FieldSet → SimPass pipeline → Render binders
```

- **Источник** заполняет `restPosition` (куб / mesh / bitmap).
- **Пассы** меняют particles и/или fields на GPU.
- **Binders** показывают результат (VFX Graph, debug field quad).

Один эффект = один `EffectAsset`: источник + **декларации полей** + список пассов.

Есть: particle passes, field foundation (inject/decay/sample), hybrid demo, тач/мышь.  
Пока нет: Stable Fluids, spatial hash / boids, particle emitters с lifetime.

---

## Как пользоваться

### Демо

1. `Assets/Scenes/Test1.unity` → `SimulationWorld.Effect`.
2. Пресеты в `Assets/Effects/`:
   - **TwistedCube** — shape-цепочка.
   - **GalaxySwirl** / **ReactiveDust** — dynamics + touch.
   - **HybridTouchField** — Touch → velocity field → particles (+ velocity quad).
3. Play. Для hybrid: InputRouter = **GroundXZ**, водить мышью по плоскости XZ.

Меню: `Tools/M3D/Create Demo Effects`, `Setup Open Scene`, `Assign HybridTouchField To Scene`.  
После смены пассов/полей в Play — **Rebuild** на SimulationWorld.

### Свой эффект с полями

1. `Create → M3D → Effect Asset`.
2. Добавить пассы (Emit/Transport для полей).
3. **Materialize missing fields from passes** — или вручную заполнить Fields.
4. Runtime **не** создаст поле сам: опечатка в имени → ошибка Build с именем пасса и поля.
5. Включить `Show Velocity Field Quad` для debug RG-вида.

Типичный hybrid:

`TouchInjectVelocityField → DecayField → SampleVelocityField → Integrate`

---

## Как добавить пасс

### Particle (как раньше)

Kernel в `Shape/Force/DynamicsPasses.compute` + класс `: ParticleKernelPass`.  
Буферы = имена атрибутов (`position`, `velocity`, …).

### Field

1. Kernel в `Assets/Shaders/GPU/Passes/FieldPasses.compute` (`numthreads(8,8,1)`).
2. Имена текстур: `{fieldName}Read`, `{fieldName}Write`.
3. Класс `: FieldKernelPass`, объявить `FieldWrites` / `FieldReads` с `FieldAccess`:
   - **WriteInPlace** — splat в Current, без swap.
   - **WritePingPong** — Current→Next, World сделает Swap (только если пасс записал dispatch).
   - **Read** — sample Current.
4. Декларации возвращать через `FieldRequestSets.Single(ref cache, ...)` с `[NonSerialized]`-полем кэша — World читает `FieldWrites` каждый кадр, `new[] {...}` в свойстве даст мусор в каждом кадре (образец — `FieldPasses.cs`).
5. Совместимость: semantic + каналы (`FieldRequest.Channels`: для write — exact match UAV layout; для read — minimum). Precision/resolution — quality knobs.
6. Один пасс = один plane (origin/axisU/axisV/size) у всех полей; write-поля обязаны иметь одинаковое resolution (диспатч сайзится по primary). Read-поля могут отличаться по resolution (normalized UV).

Hybrid (field + particles): как `SampleVelocityFieldPass` — `ParticleKernelPass` + `FieldReads`.

Добавить `.compute` в `SimulationWorld.Pass Library`, если новый файл.

---

## Куда смотреть

| Задача | Путь |
| --- | --- |
| Цикл / Swap / валидация полей | `Runtime/SimulationWorld.cs` |
| Field descriptor / requests | `Core/FieldDescriptor.cs`, `Core/FieldSet.cs` |
| Контракт пасса | `Runtime/SimPass.cs` |
| Field kernels | `Passes/FieldPasses.cs`, `Shaders/GPU/Passes/FieldPasses.compute` |
| Binders | `VfxParticleBinder.cs`, `FieldQuadBinder.cs` |
