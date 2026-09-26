# ТЗ: ADR-032 Physarum — сенсорный пасс и пресет

**Статус:** EditMode зелёный (2026-09-26). Play-сеть не закрыта.
**Исполнитель:** один тикет, P1+P2. Play-картинку сети не закрывать и не подгонять числа под неё.

Прочитать [`ADR-032-Physarum-Steer.md`](../ADR/ADR-032-Physarum-Steer.md) перед кодом. Черновик [`plan-physarum.md`](../plan-physarum.md) — идея; где он спорит с ADR, делать по ADR.

**Не трогать:** ядра и классы `DecayFieldScalar`, `DiffuseField`, `ScatterDensity`, `NormalizeDensityAccum`, `Integrate`, `BoxBounds`, `HeadingToValue`. `Boids_mk1` и все Fluid*.asset. `ParticleBillboard.shader`. `FieldTestHarness` (не добавлять в него частицы). Spatial hash. Гибрид с Fluid2D. Новые источники частиц.

---

## 1. Кернел `PhysarumPasses.compute`

Новый файл `Assets/Shaders/GPU/Passes/PhysarumPasses.compute`.

- `#pragma kernel PhysarumSteer`
- `#include "Assets/Shaders/GPU/Includes/FieldSampling.hlsl"`
- `numthreads`, как у прочих частичных кернелов: 64 по X.
- Буферы: `RWStructuredBuffer<float3> position` (только чтение), `heading`, `velocity`. `uint ParticleCount`.
- `Texture2D<float> FieldRead`.
- `SamplerState sampler_linear_repeat`. Не `sampler_linear_clamp`. UV с `WorldToFieldUV` не зажимать через `saturate`.
- Униформы: `SensorAngle`, `SensorDistance`, `TurnAngle`, `MoveSpeed`, `RandomWiggle` — все углы уже в радианах. `uint StepSalt`.

Ранний выход: `id.x >= ParticleCount`.

Курс. Если `dot(h, h) < 1e-4`, заменить `h` единичным вектором в XZ: угол `2π * (Hash(id) / 4294967295)`. Иначе обнулить Y и нормализовать `(h.x, 0, h.z)`.

Сенсоры от этой уже плоской `h`. `left = (-h.z, 0, h.x)` — базис, не позиция сенсора.

- вперёд: `h * SensorDistance`
- влево: `(h * cos(SensorAngle) + left * sin(SensorAngle)) * SensorDistance`
- вправо: `(h * cos(SensorAngle) - left * sin(SensorAngle)) * SensorDistance`

При `SensorAngle = π/2` левый сенсор совпадает с `left`. При `22.5°` пресета он смотрит вперёд-влево. Голый перпендикуляр на пресете неверен.

Три `SampleLevel(..., uv, 0)`.

Поворот по ADR-032 §2. `+θ` — формула

```
x' = x cos θ − z sin θ
z' = x sin θ + z cos θ
```

Хеш — один `uint Hash(uint x)` в этом файле (xor-shift + два умножения). Отдельный include не заводить. Случайный знак ветки «оба боковых выше» — младший бит `Hash(id ^ StepSalt)`. Шум — `Hash(id + StepSalt * 0x9E3779B9)` в диапазон `[-RandomWiggle, +RandomWiggle]`. При `RandomWiggle == 0` шум равен нулю при любом соли.

Запись:

```
heading[id.x] = float3(x', 0, z');
velocity[id.x] = heading[id.x] * MoveSpeed;
```

`position` не писать. `DeltaTime` не читать.

## 2. Класс `PhysarumSteerPass`

Файл `Assets/Scripts/Passes/DynamicsPasses.cs`.

Образец обвязки поля — `SampleVelocityFieldPass`: `Initialize` берёт дескриптор и `SimShaderIds.FieldRead`, `SetParams` биндит `field.Current` и зовёт `FieldShaderParams.Push`. Семантика чтения — `FieldSemantic.Scalar`, каналы `1`, имя по умолчанию `"trail"`. Не копировать у `SampleVelocityField` семантику Velocity и clamp-сэмплер.

В `Assets/Scripts/Runtime/SimPass.cs` добавить

```csharp
public static readonly AttributeId[] PositionHeading = { BuiltinAttributes.Position, BuiltinAttributes.Heading };
```

`Reads` → этот набор. `Writes` → `AttrSets.HeadingVelocity`. Геттер не выделяет массив.

Поля, градусы, с публичными get/set (тесты ими пользуются):

| Поле | Дефолт |
| --- | --- |
| `sensorAngle` | 22.5 |
| `sensorDistance` | 1 |
| `turnAngle` | 45 |
| `moveSpeed` | 15 |
| `randomWiggle` | 10 |

`SetParams` переводит три угла в радианы (`* Mathf.Deg2Rad`) и шлёт соль. Счётчик соли — `[NonSerialized] int stepSalt`, каждый `SetParams` делает `++stepSalt`. В `[SerializeField]` его не класть.

`KernelName` → `"PhysarumSteer"`. `DisplayName` → `"Physarum Steer"`. `Category` → `Dynamics`.

`deltaTime` в `SetParams` не использовать.

## 3. Библиотека шейдеров

В `M3DDemoTools.PassLibraryPaths` добавить путь к `PhysarumPasses.compute`.

Любой тестовый массив Pass Library, который собирает мир с этим пассом, тоже должен содержать этот шейдер. Иначе `FindKernel` бросает `InvalidOperationException`, `SimulationWorld.Build` ловит её, пишет ошибку и ставит `enabled = false`.

## 4. Меню и ассет

`Tools/M3D/Create Physarum Effect` в `M3DDemoTools`, по образцу `Create Fluid2D Effect` / `CreateEffect`.

Путь: `Assets/Effects/Physarum.asset`. Повторный вызов удаляет старый ассет и пишет заново (так уже делает `CreateEffect`).

Порядок пассов — ровно этот:

1. `ClearFieldAccumPass` — имя `trail`, **`channels = 1`**. Дефолт класса — 2. Оставить 2 нельзя.
2. `ScatterDensityToFieldPass` — имя `trail`, scale 4096, bias 0.
3. `NormalizeDensityAccumPass` — имя `trail`, scale 4096, bias 0.
4. `DecayFieldScalarPass` — `trail`, rate `0.8`.
5. `DiffuseFieldPass` — `trail`, rate `0.15`.
6. `DiffuseFieldPass` — `trail`, rate `0.15`. Второй экземпляр, не `RepeatCount`.
7. `PhysarumSteerPass` — дефолты из §2.
8. `IntegratePass`.
9. `BoxBoundsPass` — center 0, extents `(16, 0, 16)`, `BoundsBehaviour.Wrap`.
10. `HeadingToValuePass`.

Имена приватных полей у scatter/normalize/clear выставлять тем же `SetPrivate`, которым Gray-Scott-Boids ставит `targetFieldName`. Публичных сеттеров у этих классов нет — не добавлять их ради одного меню.

В списке нет `ClearFieldPass` и нет `CopyRestPass`.

Источник: Cube, `resolution = 32`, `cubeSize = 32`. `CreateEffect` пишет только resolution. `cubeSize` дописать на тот же `SerializedObject` (`cubeSource.cubeSize`). `simulationSpeed = 1`.

Поле одно: `FieldDescriptor.CreateDefault("trail", FieldSemantic.Scalar)`, затем на сериализованном ассете resolution `(128, 128)`, size `(32, 32)`. Ось и origin не трогать (дефолт XZ, origin 0). Clear остаётся 0.

Квад: `DebugFieldQuadSlot.Density("trail")`, затем `colorScale = 0.03f`. `hdrIntensity` оставить как у `Create` (1). LUT — fire, его ставит `Density()`.

Частицы на ассете через `SerializedObject`: `particleSize = 0.2`, `particleValueScale = 1`, `particleGradient` = `DebugFieldQuadSlot.DefaultFireGradient()`. `particleRenderMode` не писать: мир его не читает.

Меню назначает ассет на `SimulationWorld` открытой сцены, переписывает `passLibrary` из `PassLibraryPaths` и сохраняет сцену (`EditorSceneManager.SaveOpenScenes`), как соседние Assign-меню. Нет мира в открытой сцене — ассет всё равно создать, в лог ошибку про сцену, не падать.

## 5. Тесты

### `PhysarumSteerPassTests` — контракт, без GPU

Как `HeadingSteerPassTests`: категория, имя, кернел через reflection, reads/writes, дефолты пяти ручек, один `FieldReads` (`trail`, Read, Scalar, 1).

### `PhysarumSteerGpuTests` — один шаг, readback `heading`

`Assume.That(SystemInfo.supportsComputeShaders)`.

Поле залить `FieldTestHarness.SeedScalar`. Дескриптор: имя `trail`, Scalar, `R32_SFloat` (в тесте проще допуск; в пресете остаётся R16), resolution 8×8, size 8×8.

Свой `ParticleSet` на 1 агента. Зарегистрировать `position`, `heading`, `velocity`, записать старт. Свой `CommandBuffer`, присвоить его в `ctx.Cmd` (новый `SimContext` буфер команд не наследует, у харнеса `ParticleSet == null`). Свой `SimContext(particles, harness.Context.Fields, new[] { physarumShader }, null)`.

`pass.Initialize(ctx)` затем `pass.Execute(ctx, deltaTime: 1f)`. `dt = 1` специально: угол не имеет права от него зависеть. `Graphics.ExecuteCommandBuffer`, `headingBuffer.GetData`.

Общие ручки прогона: `sensorAngle = 90`, `sensorDistance = 1`, `turnAngle = 90`, `randomWiggle = 0`, `moveSpeed` любой. Старт: position `(0.5, 0, 0.5)`, heading `(0, 0, 1)`. Это центр текселя `(4, 4)`. Дистанция `1` — ровно один тексель при size 8 и resolution 8. Агент в нуле и дистанция `1.5` попадают на рёбра текселей, так не ставить.

`uv * 8` у сенсоров ниже равен `n + 0.5`. Индексы заливки `y * 8 + x`, остальные тексели 0:

| Тест | Единица в текселе | Ожидание |
| --- | --- | --- |
| влево | мир `(-0.5, 0, 0.5)`, тексель `(3, 4)` → индекс 35 | `heading.x < -0.9`, `abs(z) < 0.1`, `abs(y) < 1e-4` |
| вправо | мир `(1.5, 0, 0.5)`, тексель `(5, 4)` → индекс 37 | `heading.x > 0.9`, те же пределы на z и y |
| прямо | мир `(0.5, 0, 1.5)`, тексель `(4, 5)` → индекс 44 | `heading.z > 0.9`, `abs(x) < 0.1` |
| оба боковых | `(3, 4)` и `(5, 4)` = 1, передний 0 | `abs(heading.x) > 0.9`, `abs(z) < 0.1`. Знак не проверять |
| нулевой курс | поле нулевое, heading 0 | длина курса в `[0.99, 1.01]`, `abs(y) < 1e-4` |

Если сенсор попал на границу текселя и assert красный — чинить геометрию теста, не ослаблять порог `0.9`.

В `TearDown` снять command buffer, particle set, harness.

### `PhysarumPresetTests`

Загрузить `Assets/Effects/Physarum.asset`. Не вызывать Create из теста.

Проверить: один источник Cube (через `SerializedObject`: resolution 32, cubeSize 32), `SimulationSpeed == 1`, одно поле `trail` 128² size 32 format `R16_SFloat`, десять пассов в порядке §4, у первого `channels == 1`, у `BoxBounds` режим Wrap и extents `(16,0,16)`, в списке нет типа `ClearFieldPass` и нет `CopyRestPass`. `particleSize == 0.2`.

Имена типов пассов читать с `effect.Passes[i].GetType()`.

### `PhysarumWorldSmokeTests`

Как `SimulationWorldWithoutVisualEffectTests`: объект без `VisualEffect`, библиотека из шейдеров, которые реально нужны цепочке (`P2GPasses`, `DensityPasses`, `DecayPasses`, `DiffusePasses`, `DynamicsPasses`, `PhysarumPasses`). Эффект — сам `Physarum.asset`, не копия с resolution 2.

`Rebuild()` не бросает. `world.enabled == true`. `particles.Count == 32768`.

## 6. Документы

Коротко, без пересказа ADR:

- `DOC/pass-catalog.md` — пасс `PhysarumSteer` (нет `dt`, угол за шаг, поле `trail`) и цепочка пресета.
- `DOC/status.md` — ADR-032 EditMode; Play-сеть не закрыта.
- `DOC/capabilities.md` — строка пресета `Physarum`.
- `DOC/getting-started.md` — пункт демо и имя меню.

`DOC/ADR/ADR-032-Physarum-Steer.md` не переписывать. `plan-physarum.md` не расширять фазами P3/P4.

## 7. Готово, когда

- EditMode-тесты из §5 зелёные.
- В свежей сцене после меню мир собирается с `Physarum.asset` и в консоли нет `Kernel 'PhysarumSteer' not found`.
- Play не является гейтом. Сеть может быть квадратным пятном, столбиком по Y или ещё не паутиной — числа пресета не подкручивать по скриншоту.
