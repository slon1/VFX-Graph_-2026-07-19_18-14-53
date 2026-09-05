## ТЗ для программиста — F1.8 (эксперимент: Project→Advect vs Harris)

**Закрыто 2026-09-02.** Замер λ=4 в коде; вывод под confound — ADR-024 §5. Новый тикет: [`todo-F1.8b-harris-lambda8.md`](todo-F1.8b-harris-lambda8.md). Это ТЗ не переоткрывать.

Роль этого документа: измерить, а не переписать. По [ADR-024](../ADR/ADR-024-Harris-Order-Experiment.md). Прочитать ADR-024 целиком до кода — там разбор, почему аргумент в ADR-022 §2 против Harris-порядка не обосновывает вывод, и почему сид для численного раздела **обязан** быть бездивергентным, а не сплешем и не сидом из 3.6.

Зафиксировано — не пересматривать:

1. **`Assets/Effects/Fluid2D.asset` не трогать.** Ни порядок пассов, ни поля, ни калибровку. Это ADR-022/ADR-019, закрыты.
2. **Ни один существующий GPU-тест не менять.** `DivergenceFieldPassTests`, `JacobiPhiPassTests`, `ZeroMeanScalarPassTests`, `SubtractPhiGradientPassTests`, `SolidWallVelocityPassTests`, `AdvectScalarPassTests`, `Fluid2DPresetTests` — без изменений.
3. **Новых кернелов и новых классов пассов нет.** Только новая композиция уже существующих `DivergenceFieldPass`, `ZeroMeanScalarPass`, `JacobiPhiPass`, `SubtractPhiGradientPass`, `SolidWallVelocityPass`, `AdvectVelocityFieldPass`, `AdvectScalarPass`.
4. **Раздел A (численный) обязателен и решает вопрос.** Раздел B (визуальный) — best-effort, не гейтит закрытие тикета.
5. **Итог — числа в отчёте, не решение о смене production-порядка.** Если данные подтвердят гипотезу — следующий тикет (отдельный ADR) поменяет `Fluid2D.asset`; в этом тикете этого не делать.
6. **Геометрия раздела A — не геометрия 3.6.** `Size = Resolution = 64` (`h = 1`), `dt = 1`, как в `HarnessAdvectTests`/ADR-013, не `Size=32/Res=64` из 3.6 — там `Advect` не участвует, здесь участвует.
7. **Два `.compute`-пути в харнесе, две точки замера на кадр (`afterAdvect`/`afterChain`).** См. шаг 1 — без этого тест либо не соберётся, либо ассерт «сид живой» ложно потребует того же от порядка B, где по гипотезе итог должен быть малым.

Референс по устройству GPU-харнеса — `Assets/Tests/Editor/SubtractPhiGradientPassTests.cs`, тест `ProjectionChain_HarmonicK8_ReducesInteriorMaxAbsDivergence` (3.6): та же `FieldTestHarness`, тот же `MaxAbsInterior`, тот же способ строить `FieldDescriptor`/`FieldTestHarness` без `EffectAsset`/`SimulationWorld.Build`. Не изобретать новый способ гонять пассы — **прямые вызовы** `pass.Initialize(harness.Context)` + `harness.RunPass(pass, dt)`, как там.

---

### Шаг 1 — численный тест (Раздел A ADR-024)

Новый файл `Assets/Tests/Editor/HarrisOrderExperimentTests.cs`, `[Category("GPU")]`.

**Геометрия и форматы — не геометрия 3.6.** 3.6 не гоняет `Advect`, `dt` там не участвует; здесь `Advect` — часть замера, поэтому берётся калибровка `HarnessAdvectTests`/ADR-013, не 3.6: `Size = 64`, `Resolution = 64` (`h = 1`), `DeltaTime = 1` (`dt/h = 1`). `velocity` — `R32G32_SFloat`, `fluidD`/`fluidPhi` — `R32_SFloat`. `Jacobi.Iterations = 40` в обоих порядках (то же число, что в production — не давать Harris-порядку скрытое преимущество через больше итераций).

**Харнес обязан грузить обе библиотеки кернелов**, не одну: `AdvectVelocityField` живёт в `FieldPasses.compute`, остальные пассы — в `FluidPasses.compute`.

```csharp
new FieldTestHarness(descriptors, FluidCompute, FieldCompute);
// FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute"
// FieldCompute = "Assets/Shaders/GPU/Passes/FieldPasses.compute"
```

Без второго пути `FindKernel("AdvectVelocityField")` упадёт — легко пропустить, если копировать конструктор харнеса из 3.6 не глядя.

**Сид** — бездивергентный (Taylor-Green-подобный), длина волны **4 текселя** (не «один период на весь домен» — на `h=1` это даёт слабую кривизну и рискует не дать сигнала от self-advection):

```csharp
float k = 2f * Mathf.PI / 4f; // длина волны 4 текселя при h=1
u.x =  k * Mathf.Sin(k * plane.x) * Mathf.Cos(k * plane.y);
u.y = -k * Mathf.Cos(k * plane.x) * Mathf.Sin(k * plane.y);
```

`plane` — та же функция `PlanePosition`, что в `SubtractPhiGradientPassTests` (центр текселя в мировых координатах поля; при `Size=Resolution=64` мировые координаты совпадают с текселями).

**Дискретная `D` этого сида на стенсиле `uE.x−uW.x+uN.y−uS.y` равна нулю алгебраически** (см. ADR-024, раздел 2) — не просто «мало», а до float-шума на любых `k`/`h`. Перед первым прогоном любого пасса прогнать диагностический `Divergence` на сиде и залогировать `maxAbsInterior(seed)`; **абсолютный** ассерт `< 1e-3` (не относительный к чему-либо другому — запас на накопление float32 в `sin/cos` по 64² точкам). Если ассерт не проходит — проблема в сиде/дескрипторе, не в пассах, останавливаться здесь.

**Нужны три `FieldDescriptor`** (`velocity`, `fluidD`, `fluidPhi`) + **отдельный четвёртый** скалярный дескриптор для диагностики, `fluidD_diag` (`R32_SFloat`, та же геометрия) — используется только для замеров, чтобы не портить боевой `fluidD`, которым внутри цепочки пользуются `ZeroMean`/`Jacobi` следующего кадра. Диагностический `DivergenceFieldPass` строить с `DivergenceField = "fluidD_diag"`.

**Порядок A** (по одному экземпляру пассов, вызывать `RunPass` в этой последовательности; диагностический замер **afterAdvect** — сразу после первого `AdvectVelocityFieldPass`, до второго Wall):

```
DivergenceFieldPass          // пишет fluidD
ZeroMeanScalarPass           // fluidD ← fluidD − mean
JacobiPhiPass { Iterations = 40 }
SubtractPhiGradientPass
SolidWallVelocityPass
AdvectVelocityFieldPass { FieldName = "velocity", DissipationRate = 0 }
// ← диагностика afterAdvect здесь (Divergence → fluidD_diag, чтение, лог)
SolidWallVelocityPass        // второй экземпляр — как в production
// ← диагностика afterChain здесь
```

**Порядок B (Harris)** — свой набор экземпляров пассов, свой набор полей (два независимых `FieldTestHarness`, оба стартуют с одного и того же сида в этот кадр; диагностика **afterAdvect** — сразу после единственного `AdvectVelocityFieldPass`, до `Divergence` самой цепочки):

```
AdvectVelocityFieldPass { FieldName = "velocity", DissipationRate = 0 }
// ← диагностика afterAdvect здесь
DivergenceFieldPass
ZeroMeanScalarPass
JacobiPhiPass { Iterations = 40 }
SubtractPhiGradientPass
SolidWallVelocityPass        // один экземпляр, не два
// ← диагностика afterChain здесь
```

**Диагностика (обе точки, оба порядка)** — прогнать диагностический `DivergenceFieldPass` (`fluidD_diag`, не трогает боевой `fluidD`) на текущем `velocity`, считать:

- `maxAbsInterior` — как `MaxAbsInterior` в 3.6 (исключает рамку `x∈{0,N-1}` / `y∈{0,N-1}`);
- `maxAbsBorder` — максимум `|D|` **только** по рамке (новая маленькая функция, зеркало `MaxAbsInterior`, скопировать логику, не менять 3.6).

**8 кадров подряд**, каждый порядок отдельно: результат кадра N (итоговый `velocity` после **afterChain**, включая Wall) — сид кадра N+1 **того же порядка**, без повторной инициализации `FieldTestHarness` и без нового сида. `ZeroMeanScalarPass` — `IDisposable`, использовать `using`, пересоздавать вместе с харнесом одного порядка, не переиспользовать между A и B.

**Проверки на NaN/Inf** — на обеих точках диагностики (afterAdvect, afterChain), каждый кадр, оба порядка: проход по `ReadVelocity`/`ReadScalar(fluidD_diag)`, `float.IsNaN`/`float.IsInfinity`. Любое совпадение — `Assert.Fail` с номером кадра, порядком и точкой замера, дальше не продолжать молча.

**Отчёт в тесте** — `TestContext.WriteLine`/`Debug.Log` таблицей по всем 8 кадрам и обеим точкам: `frame, order, point(afterAdvect|afterChain), maxAbsInterior, maxAbsBorder`. Не сворачивать в одно число.

**Assert-ы (гейт «эксперимент прогнан и не сломан», не гейт «Harris лучше»):**

1. `maxAbsInterior(seed)` (до любого пасса) `< 1e-3` — сид действительно почти бездивергентен, иначе стоп раньше пассов.
2. Обе цепочки без NaN/Inf на всех 8 кадрах, обеих точках замера.
3. **`maxAbsInterior_afterAdvect`** кадра 1 **обоих** порядков заметно (например, `> 10×`) больше `maxAbsInterior(seed)` — подтверждает, что именно `Advect` рождает дивергенцию, у обоих порядков одинаково. Этот ассерт — **только** на `afterAdvect`, не на `afterChain`: у B к моменту `afterChain` проекция могла успешно убрать бо́льшую часть этой дивергенции — это не баг теста, это и есть проверяемая гипотеза.
4. Финальный `Assert.Pass(report)` с многострочным отчётом (обе точки, оба порядка, 8 кадров) — интерпретация числа `afterChain(A)` vs `afterChain(B)` — в шаге 3 этого ТЗ, не в самом ассерте.

Не пытаться закодировать вывод «Harris лучше»/«текущий порядок лучше» как проходящий/падающий тест — на этом этапе неизвестно, что покажут числа.

---

### Шаг 2 — визуальный эксперимент (Раздел B ADR-024, best-effort)

В `M3DDemoTools.cs`:

- Путь `Assets/Effects/Fluid2D_HarrisOrder.asset`.
- `[MenuItem("Tools/M3D/Create Fluid2D HarrisOrder Experiment")]`, `[MenuItem("Tools/M3D/Assign Fluid2D HarrisOrder Experiment To Scene")]`.
- Копия конфигурации `Fluid2D.asset` (поля `velocity`/`fluidD`/`fluidPhi`/`dye`, форматы, `Resolution = 128`, `Size = (32,32)`, `Source = None`, plane GroundXZ, **два** debug quad — `velocity` (`colorScale = 0.125`) **и** `dye`, как в production `Fluid2DPresetTests`, не только velocity), но цепочка пассов:

```csharp
new TouchInjectVelocityFieldPass(),           // MaxFieldSpeed = 20
new SeedScalarDiskPass { FieldName = "dye" },  // дефолт "V" — ловушка Gray-Scott, не production dye
new AdvectVelocityFieldPass
{
    FieldName = "velocity",
    DissipationRate = 0f,
},
new DivergenceFieldPass(),
new ZeroMeanScalarPass(),
new JacobiPhiPass { Iterations = 40 },
new SubtractPhiGradientPass(),
new SolidWallVelocityPass(),                   // один экземпляр
new AdvectScalarPass(),                         // dye ← финальный, спроецированный, стенный velocity
```

Не подключать к `Create Demo Effects` / `Setup Open Scene`. Не переиспользовать код `CreateFluid2DEffect` через флаг-параметр, если это ломает читаемость существующего метода — разрешено скопировать и держать отдельно, это временный экспериментальный ассет, не постоянный продукт.

**Ручная проверка:** Play, `Assign Fluid2D HarrisOrder Experiment To Scene`, `InputRouter.GroundXZ`, провести мышью, подержать касание ~10 с (как в F1.6). Сравнить (не одновременно — по одному `SimulationWorld` за раз, переключая ассет) с production `Fluid2D`: след на velocity-quad, поведение у рамки, поведение dye. Искать не только известную рамочную грязь (`Techdebt 8g`) — любое новое поведение, которого не было в production-варианте (взрыв, залипание, визуально более резкий/более смазанный след от touch).

Скриншот или короткое видео/гиф обоих вариантов в отчёт.

---

### Шаг 3 — отчёт и интерпретация

1. Таблица чисел из шага 1: `maxAbsInterior`/`maxAbsBorder` по 8 кадрам, оба порядка.
2. Явный вывод по интерьеру: `maxAbsInterior(B)` заметно (≥2×) меньше `maxAbsInterior(A)` на большинстве кадров, или нет — написать прямо, каким бы ни был результат.
3. Явный вывод по рамке: `maxAbsBorder(A)` и `maxAbsBorder(B)` сопоставимы (подтверждает, что `8g` не зависит от порядка) или нет.
4. Визуал из шага 2: любое новое поведение у Harris-варианта, если было замечено.
5. Diff: только новый тестовый файл, только новые методы/меню в `M3DDemoTools.cs`, только новый `.asset` (+`.meta`). Явно подтвердить: `Fluid2D.asset`, `FluidPasses.cs/.compute`, `FieldPasses.cs/.compute`, существующие GPU-тесты — без изменений.

**Не делать в этом тикете:** менять production `Fluid2D.asset`; переписывать ADR-022/ADR-019; открывать MAC/Rhie–Chow; чинить рамку `Techdebt 8g` (вторым Poisson-проходом или иначе) независимо от результата — если данные покажут, что порядок стоит менять, это отдельный следующий тикет с собственным ADR.
