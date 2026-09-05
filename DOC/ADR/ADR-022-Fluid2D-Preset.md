## ADR-022: Fluid2D пресет (сборка Stam-контура)

**Статус:** Принято (реализовано). С F1.7 пресет несёт dye (`SeedScalarDisk` + `AdvectScalarPass`) — [ADR-023](ADR-023-Advect-Scalar-Pass.md); этот ADR не переписывается.
**Дата:** 2026-08-25
**Контекст:** M3D Framework, фаза F1 (F1.6) — композиция уже закрытых кернелов в один `EffectAsset`
**Реализует:** формулы [ADR-016](ADR-016-Units-By-Pass-Family.md) §2; кернелы [ADR-017](ADR-017-Divergence-Pass-And-Square-Texel-Contract.md) / [ADR-018](ADR-018-Jacobi-Phi-Pass.md) (+ §5.1 ZeroMean) / [ADR-020](ADR-020-Subtract-Phi-Gradient-Pass.md) / [ADR-021](ADR-021-Solid-Wall-Velocity-Pass.md); self-advection [ADR-013](ADR-013-Sampler-Verification+Velocity-Field-Self-Advection.md); квадратный тексель [ADR-017](ADR-017-Divergence-Pass-And-Square-Texel-Contract.md)
**ТЗ:** [`todo-F1.6.md`](../last/todo-F1.6.md)

ADR-019 закрыт после F1.7: [ADR-019-Fluid2D-Solver.md](ADR-019-Fluid2D-Solver.md). Этот тикет — пресет и калибровка рычагов, не новый кернел и не постфактум-сводка сетки.

### Контекст

F1.1–F1.4 закрыты. Кернелы Stam-проекции, zero-mean `D`, free-slip рамка и `AdvectVelocityField` существуют по отдельности и покрыты харнесом. Пресетa нет: в demo нельзя потрогать поле пальцем и увидеть, что проекция + адвекция живут вместе.

Без композиции F1.6 нельзя калибровать два продуктовых рычага, которые сознательно оставили «предварительными»:

- `JacobiPhiPass.Iterations = 40` — оценка, не замер на широкополосном touch. Худший случай 2D-Jacobi — **осевые** моды `(k,0)/(0,k)`, `λ = (1+cos(kπ/N))/2`, не диагональ `(k,k)` ([Techdebt 8e](../last/Techdebt.md), errata [ADR-020 §3](ADR-020-Subtract-Phi-Gradient-Pass.md)).
- `ZeroMeanScalarPass` Bias = **256** (константа, не инспектор). Клип только в Accum. Сильный `TouchInject` теоретически занижает mean ([ADR-018 §5.1](ADR-018-Jacobi-Phi-Pass.md), [Techdebt 8d](../last/Techdebt.md)).

`λ^40` по Φ **не есть** отношение `max|D|` после проекции ([Techdebt 8f](../last/Techdebt.md)). Пресет **не** обязан повторить DoD цепочки 3.6 «на порядок» и не открывает MAC/Rhie–Chow. Триггер MAC — видимый odd-even на dye (F1.7 / ADR-016 §4), не этот тикет.

Desktop-first (план §0): бюджет 128² × Jacobi×40 на мобиле не оцениваем.

### Решение

#### 1. Это композиция, не новый пасс

Новых классов в `FluidPasses.cs` / новых кернелов в `FluidPasses.compute` нет. Существующие тесты пассов не переписывать (в том числе 3.6, ZeroMean, SolidWall). Production-формат боевого `velocity` остаётся `R16G16_SFloat`.

Имена полей — дефолты кернелов: `velocity`, `fluidD`, `fluidPhi`. Не `flockVel`, не `pressure`. Φ не выводить debug-quad'ом: heatmap скаляра читается как «давление», ADR-016 §2.2 это запрещает.

#### 2. Порядок за кадр

```
TouchInjectVelocityField(velocity)
Divergence
ZeroMeanScalar
JacobiPhi ×N
SubtractPhiGradient
SolidWallVelocity          // после проекции
AdvectVelocityField(velocity)
SolidWallVelocity          // второй экземпляр, после Advect
```

**Touch в начале.** Радиальный сплеш не zero-mean (`ΣD ≠ 0`). Без ZeroMean перед Jacobi warm-start `fluidPhi` снова поедет ([ADR-018 §5](ADR-018-Jacobi-Phi-Pass.md)). Touch обязан попасть в `u*` **до** Divergence того же кадра, иначе проекция отрабатывает прошлое поле, а свежий впрыск уезжает неспроецированным в Advect.

**Проекция, затем Advect** — уже зафиксировано в [`pass-catalog.md`](../pass-catalog.md). Не переставлять на Harris-порядок (Advect → force → project) в этом тикете. Замер: [ADR-024](ADR-024-Harris-Order-Experiment.md) §7 — ≥2× нет, production остаётся Project→Advect.

**Два экземпляра `SolidWallVelocityPass`**, не `RepeatCount = 2` на одном объекте: между ними должен стоять Advect. Advect (clamp UV) снова рождает нормаль на рамке ([ADR-021](ADR-021-Solid-Wall-Velocity-Pass.md)). Второй кернел не пишем.

**Второго прохода проекции нет.** Рамка `D` после стен снова грязная — [Techdebt 8g](../last/Techdebt.md), known limitation.

**ClearField нет** ни на одном из трёх полей. Divergence перезаписывает `fluidD` целиком. `fluidPhi` — warm-start (`WritePingPong`). `velocity` живёт между кадрами (Stam).

**DecayField / DiffuseVelocity нет.** Явная вязкость отклонена для v1 (план F2). Художественный рычаг — `AdvectVelocityFieldPass.DissipationRate` (ADR-013), дефолт пресета **0**.

#### 3. EffectAsset

| Кнопка | Значение |
| --- | --- |
| Путь | `Assets/Effects/Fluid2D.asset` |
| Source | `DataSourceKind.None` (field-only, как Gray-Scott) |
| `simulationSpeed` | `1` |
| Плоскость | XZ (`axisU = right`, `axisV = forward`, `origin = 0`), `InputRouter = GroundXZ` |
| `Size` | `(32, 32)` — квадратный тексель |
| `Resolution` | `128 × 128` у **всех трёх** полей (matching Resolution, ADR-017) |
| `velocity` | `Velocity`, `R16G16_SFloat`, clear 0 |
| `fluidD` | `Scalar`, **`R32_SFloat`**, clear 0 |
| `fluidPhi` | `Scalar`, **`R32_SFloat`**, clear 0 |
| Debug quad | только `velocity` (`VectorRg`). Не `fluidPhi`. `fluidD` не ставить в v1. **`colorScale = 0.125`**, не дефолт `Velocity()` (=2) |
| Jacobi `Iterations` | **40** (калибровка F1.6: живое, без пульса; 60/80 не понадобились) |
| Touch `MaxFieldSpeed` | **20** (дефолт пасса) |
| Advect `FieldName` | **`velocity`** (у пасса дефолт `flockVel` — ловушка) |
| Advect `DissipationRate` | **0** (поле не рвало half; DiffuseVelocity не ставили) |
| ZeroMean Bias | **256** (хватило на штатный сплеш MaxFieldSpeed=20 / удержание ~10 с; Inf/NaN нет) |

`FieldDescriptor.CreateDefault` для Scalar даёт `R16_SFloat`. `MaterializeMissingFields` тоже. Оба пути **запрещены** для `fluidD`/`fluidPhi`: форматы выставить явно через `SerializedProperty` после создания ассета (образец — правка `format` в `M3DDemoTools.CreateGrayScottBoidsEffect`).

`sourceKind = None` выставить **в том же методе Create** (расширить `CreateEffect` аргументом `kind` или патч сразу после `CreateAsset`). Не оставлять Cube: VFX с частицами без G2P выглядит как сломанный пресет.

`CreateEffect` удаляет существующий ассет. Калибровать **после** первого Create и коммитить; повторный Create сбрасывает `Iterations` / `DissipationRate`.

Меню: `Tools/M3D/Create Fluid2D Effect` и `Tools/M3D/Assign Fluid2D To Scene` (как HybridTouchField). Assign **не** обнуляет `visualEffect`: `Build` требует VFX даже при None (`SpawnCount = 0`). `Setup Open Scene` / `Create Demo Effects` по-прежнему без Fluid2D.

`FluidPasses.compute` уже в `PassLibraryPaths`. Путь не дублировать. `Assign` обязан прогнать `EnsurePassLibrary`.

**`colorScale` у VectorRg — gain, не MaxFieldSpeed.** Шейдер `FieldDebug`: `alpha = saturate(|v| · _Scale)`, chroma клип при `|v_comp| = 1/_Scale`. Дефолт `2` выжигает уже при `|u|≈0.5`. Ставить `20` (совпадение с MaxFieldSpeed) **усиливает** выжигание. Белая точка ≈ `1/colorScale`. Для Fluid2D: **`0.125`** (белая точка ≈ 8 — запас контраста ниже потолка 20, слабое скольжение на рамке ещё читается). `0.05` (`= 1/20`) не ставить: типичный сплеш сядет в серую кашу, а DoD как раз про рамку. Глобальный дефолт `DebugFieldQuadSlot.Velocity()` не менять — только слот пресета. HybridTouchField (`2`) не копировать: там картинка — частицы, не квад.

#### 4. Калибровка (продукт, не новый GPU-тест)

**Iterations.** Стартовать с 40. Ориентир — осевые моды / широкополосный touch, не диагональный сид 3.6 и не `λ^40` по Φ. Если после сплеша поле «дышит» крупными пятнами (низкочастотная дивергенция) — поднять к 60, затем к 80 (`[Range(1,80)]`). Если на 40 живое и не пульсирует — **оставить 40**. Не опускать ниже 40 «ради GPU» в этом тикете. Итог одной строкой в отчёте и в шапке ADR после закрытия.

Если на **80** крупные пятна всё ещё дышат: **стоп на 80**, числа и короткое видео/гиф в отчёт. `[Range]` не расширять, 120 не пробовать, MAC/Rhie–Chow не открывать. Не закрывать тикет самовольно пометкой «дышит — known limitation»: низкие частоты Jacobi так и задуманы ([Techdebt 8e](../last/Techdebt.md), ADR-018 §4), но итог пресета (40 vs 80 и приемлемость визуала) фиксирует архитектура по отчёту, не программист. 80→120 на фундаментальной моде 128² почти ничего не даёт (`λ≈1`), это не рычаг.

**Bias = 256.** Константа в `ZeroMeanScalarPass`, инспектор не заводить. Touch аддитивный, кламп `|u|≤20`: удержание 10 с у потолка — это кламп, не клип Accum. Клип Bias искать по Inf/NaN / коллапсу / глобальному дрейфу, не по «стало быстрее». При `Size = 32` сырое `D` штатного сплеша ожидаемо ≪ 256. Если клип виден — **остановиться и написать числа**, не поднимать Bias молча и не делать его `[SerializeField]` без нового решения.

Пресет **не** гейтится харнесом 3.6 (`max|D'| < max|D|/3` или `/10`). Visual + зелёные уже существующие тесты пассов.

**Один EditMode-тест закоммиченного ассета** (без GPU, без `Build` World): ловит ловушки композиции — `R16` у D/Φ, Advect на `flockVel`, один SolidWall, Source ≠ None, плоскость ≠ XZ. Не заменяет visual DoD и не вызывает меню Create. Состав assert — ТЗ шаг 3 (`colorScale`: `AreEqual(0.125f, …, 1e-4f)`).

#### 5. Visual DoD

1. Play `Test1`, Assign Fluid2D, `InputRouter.GroundXZ`.
2. Провести мышью по плоскости: на velocity-quad виден след, который **не** исчезает за 1–2 кадра (проекция не зануляет поле целиком) и **не** раздувается как источник массы (грубая сжимаемость).
3. У края квада поток скользит вдоль рамки, не вытекает «из квадрата» (второй SolidWall после Advect).
4. Rebuild после смены пассов/полей в Play — как у остальных пресетов.

### Последствия

- (+) Первый живой Stam на desktop: touch → несжимаемое поле → self-advection → непроницаемая рамка.
- (+) Калибровка 40 / Bias=256 перестаёт быть текстом ADR-018.
- (−) Без dye (F1.7) корректность «глазами» только по velocity-quad; odd-even collocated сетки на dye ещё не виден — и не ищется.
- (−) Рамка `D` после стен грязная; второго Poisson нет.
- (−) `velocity` в пресете — half; численный пол D на боевом формате хуже, чем в оракуле `R32G32`. Это production, не регресс 3.6.

**Вне скоупа:** `AdvectScalarPass` / dye на момент F1.6 (закрыто в F1.7); MAC / Rhie–Chow; явная вязкость; мобильный бюджет; второй проход проекции; `ClampFieldPass`; частицы / SampleVelocity на fluid; debug-quad Φ; смена Bias на SerializeField; смена production-формата `velocity`; Harris-порядок Advect-before-project.

### Альтернативы (отклонены)

**Touch после Advect / только один SolidWall.** Сплеш уедет неспроецированным; Advect снова испортит нормаль. Оба экземпляра стены и Touch-сначала уже согласованы с ADR-021 и catalog.

**Занять ADR-019 в F1.6.** Отклонено: номер — сводка после dye. Закрыт: [ADR-019](ADR-019-Fluid2D-Solver.md).

**Резолюция 64² как в харнесе.** Дешевле Jacobi, но demo читается как «клетка». Desktop-first → 128², тот же `Size = 32` (квадратный тексель).

**Source Cube + SampleVelocity.** Это HybridTouchField, не Fluid2D. Частицы в F1.6 не нужны.
