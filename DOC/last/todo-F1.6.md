## ТЗ для программиста — F1.6 (Fluid2D пресет)

Роль этого документа: собрать уже закрытые пассы в один `EffectAsset` по [ADR-022](../ADR/ADR-022-Fluid2D-Preset.md). Новых кернелов нет. Не пересматривать порядок, имена полей, формат `fluidD`/`fluidPhi`, отказ от ADR-019.

Прочитать ADR-022 целиком **до кода**. Без этого легко собрать ложный пресет (Touch не туда, Advect на `flockVel`, Scalar как `R16`, один SolidWall, Cube вместо None).

Зафиксировано — не начинать, пока это не ясно:

1. **`ZeroMeanScalarPass` обязателен.** Touch не zero-mean. Цепочка без него возвращает дрейф `mean(Φ)` (ADR-018 §5). Стоит **между** Divergence и Jacobi.
2. **Два экземпляра `SolidWallVelocityPass`:** после Subtract и после Advect. Не `RepeatCount = 2` на одном объекте.
3. **Калибровка Jacobi — по осевым модам / пальцу, не по диагонали 3.6.** `λ^40` по Φ ≠ `max|D|`. Порог «на порядок» не является DoD пресета.
4. **Bias = 256 остаётся константой.** Не делать инспектор. Если клип виден на штатном сплеше — стоп и числа, не молчаливый бамп.
5. **ADR-019 не трогать.** Итоговый solver после F1.7. Этот тикет — ADR-022.

Референсы по месту:

- `Assets/Scripts/Editor/M3DDemoTools.cs` — образец меню + `EnsurePassLibrary` + правка `format`/`size` через `SerializedObject` (`CreateGrayScottBoidsEffect`, `AssignHybridToScene`).
- `EffectAsset.EditorConfigure` — умеет `DataSourceKind`. Fluid2D обязан получить **None в том же методе Create**, не «потом в инспекторе».
- `AdvectVelocityFieldPass` — дефолт `fieldName = "flockVel"`. В пресете **обязательно** `FieldName = "velocity"`.
- `TouchInjectVelocityFieldPass` — публичного `FieldName` нет, дефолт уже `"velocity"`. **Не** `SetPrivate`. Ловушка имени только у Advect.
- `FieldDescriptor.CreateDefault(Scalar)` → `R16_SFloat`. Для `fluidD`/`fluidPhi` этого мало; `MaterializeMissingFields` — та же ловушка.
- `SimulationWorld.Teardown` уже диспозит `IDisposable` (ZeroMean). Второй буфер ZeroMean не создавать.
- Существующие тесты: `DivergenceFieldPassTests`, `JacobiPhiPassTests`, `ZeroMeanScalarPassTests`, `SubtractPhiGradientPassTests`, `SolidWallVelocityPassTests` — **не менять**.

Код кернелов не писать. Документацию из шага 4 — да, в том же тикете. F1.7 / dye / MAC / второй Poisson — вне скоупа.

---

### Шаг 1 — меню создания пресета

В `M3DDemoTools`:

- константа пути `EffectsFolder + "/Fluid2D.asset"`;
- `[MenuItem("Tools/M3D/Create Fluid2D Effect")]`;
- `[MenuItem("Tools/M3D/Assign Fluid2D To Scene")]`.

`Create Demo Effects` / `Setup Open Scene` **не** переключать на Fluid2D (дефолт сцены остаётся TwistedCube).

Цепочка пассов **дословно** (новые объекты, дефолтные имена полей у fluid-пассов):

```csharp
new TouchInjectVelocityFieldPass(),          // MaxFieldSpeed = 20
new DivergenceFieldPass(),
new ZeroMeanScalarPass(),
new JacobiPhiPass { Iterations = 40 },
new SubtractPhiGradientPass(),
new SolidWallVelocityPass(),
new AdvectVelocityFieldPass
{
    FieldName = "velocity",                  // не flockVel
    DissipationRate = 0f,
},
new SolidWallVelocityPass(),                 // второй экземпляр
```

Поля: три дескриптора `velocity` / `fluidD` / `fluidPhi`. После `CreateAsset` через `SerializedObject`:

| Поле | semantic | format | res | size | plane |
| --- | --- | --- | --- | --- | --- |
| `velocity` | Velocity | `R16G16_SFloat` | 128² | (32, 32) | origin 0, U=right, V=forward |
| `fluidD` | Scalar | **`R32_SFloat`** | 128² | (32, 32) | то же |
| `fluidPhi` | Scalar | **`R32_SFloat`** | 128² | (32, 32) | то же |

`clearValue` — `Color.clear` у всех. `sourceKind` — `DataSourceKind.None`. `simulationSpeed` — `1`.

Debug quad — не голый `DebugFieldQuadSlot.Velocity()` (там `colorScale = 2`):

```csharp
DebugFieldQuadSlot velocityQuad = DebugFieldQuadSlot.Velocity();
velocityQuad.colorScale = 0.125f; // белая точка ≈ |u|=8; не 2 и не 20
```

Шейдер множит: `saturate(length(v) * _Scale)`. `colorScale = 20` выжжет сильнее, не слабее. Дефолт `Velocity()` / HybridTouchField не копировать.

`sourceKind = None` — **обязательно в том же методе Create**, до `SaveAssets`. Предпочтительно расширить `CreateEffect` аргументом `DataSourceKind kind = DataSourceKind.Cube` и для Fluid2D передать `None` в `EditorConfigure` (существующие вызовы не ломать). Если `CreateEffect` не трогают — патч `sourceKind` через `SerializedObject` сразу после `CreateAsset`. Мягкое «если удобнее» не допускается: забытый Cube = частицы без G2P.

После патча форматов — `Debug.Log` типов восьми пассов и `format` трёх полей (sanity в Console для отчёта).

`CreateEffect` делает `DeleteAsset` и собирает заново. **Create один раз → калибровать инспектором → коммитить ассет.** Повторный Create после калибровки сбросит `Iterations` / `DissipationRate`. Не гонять меню второй раз «для чистоты».

`Assign Fluid2D To Scene`: повесить ассет на `SimulationWorld.effect`, `InputRouter.planeMode = GroundXZ`, `EnsurePassLibrary`. **`visualEffect` со сцены не снимать и не обнулять.** `Build` падает без VFX даже при Source None (`SimulationWorld` ~строка 142); None уже ставит `SpawnCount = 0`. Образец — `AssignHybridToScene`. Не дублировать путь `FluidPasses.compute` в `PassLibraryPaths` — он уже есть.

---

### Шаг 2 — калибровка (ручная, не новый TestFixture)

Стартовые значения из шага 1. Play, Assign, GroundXZ.

**Iterations.** Провести пальцем/мышью. Крупные пятна «дышат» / поле пульсирует целиком → 60, затем 80. На 40 живое и без пульса — оставить 40. Ниже 40 не опускать. Итог — в отчёте и в шапке ADR-022 (заменить «к реализации» на «реализовано», дописать выбранное N).

Если **80 всё ещё дышит:** стоп, числа + видео/гиф в отчёт. `[Range(1,80)]` не расширять, 120 не ставить (в коде тоже), MAC не открывать. Не закрывать с пометкой «known limitation» без ответа архитектуры.

**Touch.** Inject аддитивный, кламп `|u| ≤ 20` (`MaxFieldSpeed`). Удержание ~10 с **не** разгоняет поле сверх потолка — это кламп, не симптом Bias. Bias-клип искать по Inf/NaN / коллапсу в константу / глобальному дрейфу при живом сплеше, не по «стало быстрее».

**Bias.** Удерживать касание ~10 с при `MaxFieldSpeed = 20`. Inf/NaN, коллапс в константу или явный глобальный дрейф при живом сплеше → стоп, числа в отчёте, Bias не трогать. Если штатно держится — Bias остаётся 256, инспектор не заводить.

**DissipationRate.** Оставить 0, пока поле не взрывается. Если без dissipation визуально разносит half-velocity — можно поставить малый rate (порядка `0.1`) как художественный рычаг ADR-013, **не** вешать `DiffuseVelocityFieldPass`.

Не гонять 3.6 как критерий закрытия. Не открывать MAC из «не на порядок».

---

### Шаг 3 — тесты

Новый **GPU**-тест пресета **не писать.** Существующие GPU-тесты fluid-семейства прогнать (регрессий быть не должно: кернелы не менялись).

**Один EditMode-тест ассета — да, в этом тикете.** `Assets/Tests/Editor/Fluid2DPresetTests.cs`. Без `[Category("GPU")]`. `AssetDatabase.LoadAssetAtPath<EffectAsset>("Assets/Effects/Fluid2D.asset")`; если null — падать с текстом «run Tools/M3D/Create Fluid2D Effect», не вызывать Create из теста.

Не `SimulationWorld.Build`, не `FindKernel`, не харнес, не 3.6.

Assert (публичные свойства; `sourceKind` — через `ResolveSource() is NoneSource`):

1. Source — `NoneSource`. Поля ровно три: `velocity`, `fluidD`, `fluidPhi`.
2. Форматы: `velocity` = `R16G16_SFloat`; `fluidD` и `fluidPhi` = `R32_SFloat` (не `R16_SFloat`).
3. У всех трёх: `Resolution = (128,128)`, `Size = (32,32)`, `Origin = 0`, `AxisU = Vector3.right`, `AxisV = Vector3.forward` (XY-плоскость пройдёт res/size и сломает GroundXZ).
4. `Passes.Count == 8`, типы по порядку: TouchInject → Divergence → ZeroMeanScalar → JacobiPhi → SubtractPhiGradient → SolidWallVelocity → AdvectVelocityField → SolidWallVelocity. Ровно два `SolidWallVelocityPass`.
5. `(AdvectVelocityFieldPass)Passes[6]`: `FieldName == "velocity"` (не `flockVel`).
6. Debug quads: один слот, имя `velocity`, `Assert.AreEqual(0.125f, slot.colorScale, 1e-4f)` (YAML; не дефолт 2).
7. `JacobiPhiPass.RepeatCount` в `[40, 80]` — не прибивать ровно 40 (калибровка). `DissipationRate` не ассертить (можно 0 или малый rate из шага 2).

Ручной инспектор после Create — sanity до зелёного теста, не замена.

---

### Шаг 4 — документация (часть тикета)

- `DOC/plan-stable-fluid.md`: F1.6 → **Готово**, ссылка на ADR-022; статус-колонка не «Открыто».
- `DOC/pass-catalog.md`: цепочка Fluid2D — с Touch в начале и двумя SolidWall; снять «пресета ещё нет».
- `DOC/getting-started.md`: пресет в списке демо, меню Create/Assign, `InputRouter = GroundXZ`; «пресета Fluid2D нет» убрать.
- `DOC/capabilities.md`: пресет есть; dye по-прежнему F1.7.
- `DOC/status.md`: секция F1.6 по образцу F1.4; итерацию сдвинуть; во «Вне скоупа» F1.6 убрать, оставить F1.7 / ADR-019.
- `DOC/ADR/ADR-014-GPU-Numeric-Test-Harness.md`: строка `ADR-022 | F1.6 | …` уже вписана вместе с ADR; не дублировать. ADR-019 не переписывать.
- `DOC/ADR/ADR-022-Fluid2D-Preset.md`: статус **Принято (реализовано)** + фактические `Iterations` / `DissipationRate`, если калибровка сдвинула дефолт.
- `DOC/last/Techdebt.md`: 8d — калибровка Bias в F1.6 закрыта (факт: 256 хватило / не хватило — как есть). 8e — `iterations` пресета зафиксирован.

---

### Отчёт

1. Diff: `M3DDemoTools.cs`, `Assets/Effects/Fluid2D.asset` (+ `.meta`), `Fluid2DPresetTests.cs`, документация из шага 4. Явно подтвердить: `Create Demo Effects` не трогали; `PassLibraryPaths` и `FluidPasses.cs` / `.compute` / валидаторы / формат production `velocity` — без изменений.
2. Калибровка: итоговые `Iterations`, Bias (остался 256 или стоп с числами), `DissipationRate`.
3. Visual: сплеш на quad, скольжение вдоль края, удержание ~10 с.
4. Существующие GPU-тесты fluid: зелёные.

Если Build падает на `SquareTexelValidator` / matching Resolution — не ослаблять валидатор; выровнять дескрипторы.

Если Advect «молчит» — первым делом имя поля (`flockVel` vs `velocity`), не формулу backtrace.
