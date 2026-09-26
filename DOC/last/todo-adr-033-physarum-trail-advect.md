# ТЗ: ADR-033 Перенос следа Physarum полем скорости

**Статус:** EditMode зелёный (2026-09-26). Play пальцем не закрыт.
**Исполнитель:** один тикет. Play не калибровать. Вихрь и проекцию не добавлять.

Прочитать [`ADR-033-Physarum-Trail-Advect.md`](../ADR/ADR-033-Physarum-Trail-Advect.md) перед кодом. Раздел 5 [`plan-physarum.md`](../plan-physarum.md) (полный Stam) не исполнять.

**Не трогать:** `Assets/Effects/Physarum.asset`. Все `Fluid2D*.asset`. Кернел `AdvectVelocityField` и его `saturate`. `PhysarumSteer` / `PhysarumPasses.compute`. Формулу `exp(-dissipationRate · dt)`. Divergence, Jacobi, SubtractPhiGradient, SolidWall, AdvectVelocity в новом пресете.

---

## 1. Кернел `AdvectScalar`

Файл `Assets/Shaders/GPU/Passes/FieldPasses.compute`, только блок `#ifdef KERNEL_ADVECTSCALAR`.

Рядом с существующим `SamplerState sampler_linear_clamp` кернел должен видеть `SamplerState sampler_linear_repeat`. Объявить его внутри этого `#ifdef`, не выносить в общее тело файла.

Униформа `int AdvectWrap`. Её выставляет только `AdvectScalarPass`.

Тело заменить на ветку. При `AdvectWrap == 0` код обязан совпасть с нынешним:

```
vel = FieldReadB.SampleLevel(sampler_linear_clamp, uv, 0);
backUv = saturate(uv - (vel * DeltaTime) / FieldSize);
sampled = FieldReadA.SampleLevel(sampler_linear_clamp, backUv, 0);
```

При `AdvectWrap != 0`:

- и `FieldReadB`, и `FieldReadA` сэмплируются `sampler_linear_repeat`;
- `backUv` не прогонять через `saturate` и не через `frac`.

`FieldWriteA = sampled * Dissipation` в обеих ветках.

`AdvectVelocityField` оставить со своим `saturate`.

## 2. Класс `AdvectScalarPass`

Файл `Assets/Scripts/Passes/FieldPasses.cs`.

```csharp
[SerializeField] private bool wrapUv;
```

Дефолт `false`. Публичные get/set, как у `Reverse`.

В `SetParams` после уже существующих `DeltaTime` / `Dissipation`:

```csharp
SetInt(context, AdvectWrapId, wrapUv ? 1 : 0);
```

`Shader.PropertyToID("AdvectWrap")`. `reverse` по-прежнему ставит `Dissipation = 1` и отрицательный `dt`. Флаг wrap при этом всё равно отправляется. На Fluid2D его не включать: у старых ассетов поля в YAML нет, десериализация даёт `false`.

Имена полей и `AllowResolutionMismatch` на velocity не менять.

## 3. Меню и ассет

`Tools/M3D/Create Physarum Fluid Effect` в `M3DDemoTools`, по образцу `Create Physarum Effect`.

Путь: `Assets/Effects/Physarum_Fluid.asset`. Повторный вызов удаляет прежний ассет. `Physarum.asset` не удалять и не переписывать.

Поля:

- `trail` — `FieldDescriptor.CreateDefault("trail", FieldSemantic.Scalar)`, затем resolution `(128, 128)`, size `(32, 32)`.
- `velocity` — `CreateDefault("velocity", FieldSemantic.Velocity)`, те же resolution и size. Ось и origin не трогать.

Куб: resolution `32`, `cubeSize` `32`, `simulationSpeed` `1`. `particleSize` `0.2`, `particleValueScale` `1`, `particleGradient` = `DebugFieldQuadSlot.DefaultFireGradient()`.

Квады: `DebugFieldQuadSlot.Density("trail")` с `colorScale = 0.03f`; `DebugFieldQuadSlot.Velocity("velocity")` с `colorScale = 0.125f`.

Порядок пассов:

1. `TouchInjectVelocityFieldPass` — имя поля `velocity` (дефолт класса уже такой).
2. `DecayFieldPass` — `FieldName = "velocity"`, `DecayRate = 0.4f`. Это не `DecayFieldScalarPass`.
3. `ClearFieldAccumPass` — `trail`, **`channels = 1`** через `SetPrivate`.
4. `ScatterDensityToFieldPass` — `trail`, scale 4096, bias 0, через `SetPrivate`.
5. `NormalizeDensityAccumPass` — то же.
6. `AdvectScalarPass` — `ScalarField = "trail"`, `VelocityField = "velocity"`, `DissipationRate = 0`, `Reverse = false`, `WrapUv = true`.
7. `DecayFieldScalarPass` — `trail`, rate `0.8`.
8. `DiffuseFieldPass` — `trail`, `0.15`.
9. `DiffuseFieldPass` — `trail`, `0.15`.
10. `PhysarumSteerPass` — дефолты.
11. `IntegratePass`.
12. `BoxBoundsPass` — center 0, extents `(16, 0, 16)`, Wrap.
13. `HeadingToValuePass`.

В списке нет Divergence, Jacobi, SubtractPhiGradient, SolidWall, AdvectVelocity, ClearField, CopyRest.

Меню назначает ассет на `SimulationWorld` открытой сцены, переписывает Pass Library из `PassLibraryPaths` и сохраняет сцену. Нет мира — ассет всё равно создать, в лог ошибку, без исключения. InputRouter не трогать. В Play он должен быть GroundXZ; это строка в `getting-started`, не код меню.

Новый `.compute` не добавлять. `FieldPasses.compute` уже в `PassLibraryPaths`.

## 4. Тесты

### Контракт в `AdvectScalarPassTests`

`new AdvectScalarPass().WrapUv` равен `false`. Существующий `PassiveGaussian_ComMovesEightTexelsWithoutSelfAdvectionOvershoot` не переключать на wrap и порог `dCOM_x` не менять. Гаусс из центра 20.5 на 64² за 8 шагов край не пересекает, этим тестом wrap не доказывается.

### `AdvectScalarWrapTests` — один шаг через край

`Assume.That(SystemInfo.supportsComputeShaders)`.

`FieldTestHarness`: скаляр `trail` `R32_SFloat`, скорость `velocity` `R32G32_SFloat`, оба 8×8, size `(8, 8)`. `dt = 1`. Один `RunPass`.

Скорость — константа `(1, 0)` на всех текселях. Скаляр — `1` в индексе `4 * 8 + 7` (тексель `(7, 4)`), остальные `0`.

`AdvectScalarPass`: scalar `trail`, velocity `velocity`, `DissipationRate = 0`, `WrapUv = true`.

После шага значение индекса `4 * 8 + 0` (тексель `(0, 4)`) > `0.9`, индекс `4 * 8 + 7` < `0.1`. Сумму текселей не сравнивать.

При `Size == Resolution` и `dt = 1` сдвиг ровно на один тексель: `backUv` центра текселя 0 равен `-0.0625`, repeat-сэмплер читает центр текселя 7. Если порог `0.9` красный из-за шва — чинить ветку сэмплера, не ослаблять порог и не вставлять `frac`.

### `PhysarumFluidPresetTests`

Загрузить `Assets/Effects/Physarum_Fluid.asset`. Не вызывать Create из теста.

Проверить куб 32 / `cubeSize` 32, `SimulationSpeed == 1`, `particleSize == 0.2`. Два поля: `trail` Scalar `R16_SFloat` 128² size 32, `velocity` Velocity `R16G16_SFloat` 128² size 32. Тринадцать пассов в порядке §3. У ClearAccum `channels == 1`. У `DecayField` rate `0.4` и имя `velocity`. У `AdvectScalar` имена `trail` / `velocity`, `DissipationRate == 0`, `WrapUv == true`, `Reverse == false`. В списке нет `JacobiPhiPass`, `DivergenceFieldPass`, `SolidWallVelocityPass`, `AdvectVelocityFieldPass`, `ClearFieldPass`, `CopyRestPass`.

Отдельным утверждением загрузить `Assets/Effects/Physarum.asset`: одного поля `trail` достаточно, `AdvectScalarPass` в его списке нет.

### `PhysarumFluidWorldSmokeTests`

Как `PhysarumWorldSmokeTests`: объект без `VisualEffect`, эффект — сам `Physarum_Fluid.asset`. В библиотеке обязаны быть `FieldPasses.compute` (TouchInject, DecayField, AdvectScalar), `P2GPasses`, `DensityPasses`, `DecayPasses`, `DiffusePasses`, `DynamicsPasses`, `PhysarumPasses`.

`Rebuild()` не бросает, `enabled == true`, `particles.Count == 32768`.

## 5. Документы

Коротко:

- `DOC/pass-catalog.md` — у `Advect Scalar` opt-in `wrapUv`: repeat без `saturate` и без `frac`; дефолт clamp. Строка пресета `Physarum_Fluid`.
- `DOC/status.md` — ADR-033 EditMode; Play пальцем не закрыт.
- `DOC/capabilities.md` — строка пресета.
- `DOC/getting-started.md` — меню и GroundXZ.

`DOC/ADR/ADR-023-Advect-Scalar-Pass.md` и `DOC/ADR/ADR-033-Physarum-Trail-Advect.md` не переписывать.

## 6. Готово, когда

- Старый оракул `dCOM_x ≈ 8` зелёный.
- Wrap-тест через правый край зелёный.
- `Physarum.asset` не изменился.
- `Rebuild` `Physarum_Fluid` без `VisualEffect` собирает мир.
- В Play палец может дать слабый или короткий толчок. `decayRate` 0.4 и `colorScale` квадов из-за этого не подкручивать.
