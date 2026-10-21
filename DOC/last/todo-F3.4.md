## ТЗ для программиста — F3.4 (Vorticity Confinement поверх DyeInject, вариант E1)

**Закрыто 2026-10-21.** ADR-051 принят. На пресете `EpsilonVc = 25`. Гейты ниже гонялись при `ε=1`. Production остаётся `Fluid2D.asset`.

**Открыто 2026-10-21.** Статус в плане был **Не начат → В работе** после Шага 0. ADR: [ADR-051](../ADR/ADR-051-Vorticity-On-Dye-Inject.md) — прочитать **до кода**, целиком. Протокол visual: `play-F3.4-touch.md`.

Роль: проверить гипотезу F3.4 — если добавить VC в принятую цепочку `Fluid2D_DyeInject` (один пасс, перед проекцией), то на тонкой нити dye 512² появятся вихревые завитки, и без возвращения торнадо у рамки. **Нового кернела нет**, нового пасса нет. Один пресет, три тестовых файла, доки.

Опора: F3.1 ([ADR-050](../ADR/ADR-050-Continuous-Dye-Injection.md), [`todo-F3.1.md`](todo-F3.1.md), принят) и F2.1/F2.1b ([ADR-027](../ADR/ADR-027-Vorticity-Confinement-Pass.md)). F3.1 не переоткрывается.

Зафиксировано — не пересматривать:

1. **Не трогать:** все существующие `Fluid2D*.asset` (`Fluid2D`, `Fluid2D_HarrisOrder`, `Fluid2D_Vorticity`, `Fluid2D_MacCormackDye`, `Fluid2D_HighResDye`, `Fluid2D_DyeInject`). Не запускать их Create. Новый путь — `Assets/Effects/Fluid2D_DyeInjectVC.asset`.
2. **Кернелы и классы не менять:** никакие. В частности `VorticityConfinementPass` и кернел `VorticityConfinement`, маску `BorderMargin`, `TouchInjectScalarFieldPass`, `AdvectScalarPass`, `SeedScalarDiskPass`, `FieldTestHarness`, `SimContext`. Валидаторы — без правок.
3. **Состав — ровно как `Fluid2D_DyeInject`, с одним отличием:** добавлен `VorticityConfinementPass { VelocityField = "velocity", EpsilonVc = 1, BorderMargin = 2 }` **сразу после `TouchInjectScalarFieldPass` и перед `DivergenceFieldPass`**. Итого **12** пассов: `TouchInjectVelocity, SeedScalarDisk, TouchInjectScalar, VorticityConfinement, Divergence, ZeroMean, Jacobi×40, Subtract, SolidWall, AdvectVelocity, SolidWall, AdvectScalar`. Без `CopyScalar`/`LimitedMacCormackCombine`.
4. **Параметры клонируются из фабрики `CreateFluid2DDyeInjectExperiment`**, а не из значений, которые владелец покрутил в инспекторе `Fluid2D_DyeInject.asset`: `SeedScalarDisk.RadiusUV = 0.04`, `Value = 1`; `TouchInjectScalar`: `injectRate = 60`, `maxValue = 1`, `radiusScale = 1`; `AdvectScalarPass.DissipationRate = 0`; `Jacobi = 40`.
5. **Поля как в `Fluid2D_DyeInject`:** `velocity` 128² `R16G16_SFloat`, `fluidD` 128² `R32_SFloat`, `fluidPhi` 128² `R32_SFloat`, `dye` 512² `R16_SFloat`; `Size = (32, 32)`; `clearValue = Color.clear`.
6. **Меню:** `Tools/M3D/Create Fluid2D DyeInjectVC Experiment` / `Tools/M3D/Assign Fluid2D DyeInjectVC Experiment To Scene`. `Assign` берёт `SimulationWorld` **активной** сцены (как у F3.1, `SceneManager.GetActiveScene()`); нет в активной сцене → ошибка, ничего не менять.
7. **Assign в сцену выполняет оператор.** Grok сцену не назначает и **не коммитит изменения `.unity`** без решения владельца.
7а. **Git-коммит — только по отдельной просьбе владельца.** До неё результаты остаются в рабочем дереве; если Create пересериализовал принятый `Fluid2D_DyeInject.asset`, вернуть его с диска (`git checkout`).
8. **Статусы.** До слов владельца статус — «Предложено» (ADR-051) и «В работе (EditMode готов)» (план). Слова «Принято», «Готово», «Visual принят» — только по явному сообщению владельца. Не выводить статус из зелёных тестов.
9. **GPU ms — запись, не порог** ([Techdebt 8k](Techdebt.md)).
10. **Красный гейт → стоп.** Числа/сообщение в чат; не крутить допуски молча.
11. **Existing VC-тесты и все тесты F3.x — зелёные без правок.**

Референсы: `M3DDemoTools.CreateFluid2DDyeInjectExperiment` и `AssignFluid2DDyeInjectToScene` (клонировать **их**); `M3DDemoTools.CreateFluid2DVorticityExperiment` (как строится `VorticityConfinementPass`); `Fluid2DDyeInjectPresetTests`, `Fluid2DDyeInjectWorldSmokeTests`, `Fluid2DDyeInjectLiveTests` (стили composition / world-smoke / live-GPU); `Fluid2DVorticityPresetTests`, `Fluid2DVorticityWorldSmokeTests` (список compute-файлов для VC и порядок проверки).

Имена: ассет `Fluid2D_DyeInjectVC.asset`; тесты `Fluid2DDyeInjectVcPresetTests.cs`, `Fluid2DDyeInjectVcWorldSmokeTests.cs`, `Fluid2DDyeInjectVcLiveTests.cs` (последний `[Category("GPU")]`).

---

### Шаг 0 — закрыт (ответы Grok, 2026-10-21)

Проверено кодом, допущения ADR-051 подтверждены:

1. Кернел `VorticityConfinement` лежит в `Assets/Shaders/GPU/Passes/FluidPasses.compute`. Файл есть в `passLibrary` и у `Fluid2DVorticityWorldSmokeTests`, и у `Fluid2DDyeInjectWorldSmokeTests`; списки совпадают, включая `TouchGrayScottPasses.compute`. Для P2 список Vorticity-теста копируется целиком, добавлять ничего не нужно.
2. `VorticityConfinementPass` пишет `velocity` как `WritePingPong` (`FluidPasses.cs`, строки 300–303). `SimulationWorld` меняет буферы местами сразу после `Execute`, если пасс реально диспатчился (`SimulationWorld.cs`, строки 138–141). `Divergence` читает `velocity` из текущего буфера (`SimPass.cs`, строки 558–560). `TouchInjectVelocity` пишет на месте до VC, поэтому `Divergence` получает уже обновлённую скорость.
3. Все helpers живого теста F3.1 (в том числе `DyeInjectChain`) — `private` внутри `Fluid2DDyeInjectLiveTests`. Из нового теста без правки старого файла они недоступны. **Единственный путь — скопировать нужное в новый файл.** Выносить в общий класс нельзя: это правит существующий тест.

---

### Шаг 1 — пресет и меню

`Assets/Scripts/Editor/M3DDemoTools.cs`, рядом с `CreateFluid2DDyeInjectExperiment`.

```csharp
private const string Fluid2DDyeInjectVcPath = EffectsFolder + "/Fluid2D_DyeInjectVC.asset";

[MenuItem("Tools/M3D/Create Fluid2D DyeInjectVC Experiment")]
public static void CreateFluid2DDyeInjectVcExperiment()
{
    // Клон CreateFluid2DDyeInjectExperiment: те же 4 поля, тот же цикл resolution/format/size,
    // те же debug quads, те же параметры Seed/TouchInjectScalar/AdvectScalar/Jacobi (см. ТЗ п.3–5).
    // Единственное отличие: после TouchInjectScalarFieldPass и до DivergenceFieldPass —
    //   new VorticityConfinementPass { VelocityField = "velocity", EpsilonVc = 1f, BorderMargin = 2 }
}

[MenuItem("Tools/M3D/Assign Fluid2D DyeInjectVC Experiment To Scene")]
public static void AssignFluid2DDyeInjectVcToScene()
{
    // Клон AssignFluid2DDyeInjectToScene, включая выбор SimulationWorld из активной сцены.
    // Ошибка: "run Tools/M3D/Create Fluid2D DyeInjectVC Experiment".
}
```

**Create — отдельный метод, копия тела фабрики DyeInject, и он не вызывает `CreateFluid2DDyeInjectExperiment`**: тот перезаписывает уже принятый `Fluid2D_DyeInject.asset`. `SaveAssetIfDirty` — только на новый `Fluid2D_DyeInjectVC.asset`. **Assign** — копия F3.1: сохраняется только активная сцена. Запустить Create; `.asset` и `.meta` остаются в рабочем дереве (п.7а). Assign **не** запускать (п.7).

---

### Шаг 2 — тесты композиции и загрузки

**`Fluid2DDyeInjectVcPresetTests.cs`** (не GPU, стиль `Fluid2DDyeInjectPresetTests`): 4 поля с разрешениями/форматами/`Size` как в п.5; **12 пассов** строго в порядке п.3; ровно один `VorticityConfinementPass` (`VelocityField == "velocity"`, `EpsilonVc == 1`, `BorderMargin == 2`), стоящий **после** `TouchInjectScalarFieldPass` и **до** `DivergenceFieldPass`; ровно один `SeedScalarDiskPass` (`RadiusUV == 0.04`, `Value == 1`); ровно один `TouchInjectScalarFieldPass` (`InjectRate == 60`, `MaxValue == 1`, `RadiusScale == 1`); нет `CopyScalarPass`, `LimitedMacCormackCombinePass`; **две** `SolidWallVelocityPass`; `AdvectScalarPass.DissipationRate == 0`; `JacobiPhiPass.Iterations == 40`. Ассет грузится **с диска**. Сообщение об отсутствии ассета — «run Tools/M3D/Create Fluid2D DyeInjectVC Experiment».

**`Fluid2DDyeInjectVcWorldSmokeTests.cs`** (стиль `Fluid2DDyeInjectWorldSmokeTests`): неактивный `GameObject` с `SimulationWorld`, `effect` — ассет **с диска**, `passLibrary` — список путей, скопированный **целиком** из `Fluid2DVorticityWorldSmokeTests` (Шаг 0 п.1: `FluidPasses.compute` и `TouchGrayScottPasses.compute` там уже есть); `Rebuild()` не бросает, `world.enabled == true`. Без кадров и readback.

Красный → стоп.

---

### Шаг 3 — живая цепочка (GPU)

`Fluid2DDyeInjectVcLiveTests.cs`, `[Category("GPU")]`, **`[Timeout(900000)]`** на живой тест (как на штрих F3.1). Пассы — новыми экземплярами в порядке пресета. Helpers из `Fluid2DDyeInjectLiveTests` **копируются в новый файл** (Шаг 0 п.3); старый файл не править. Живая геометрия: 128² / 512², `Size = 32`, Jacobi×40. Touch через `SetTouches` (F3.1 Шаг 1b уже в коде).

Сценарий жеста — как F3.1 Шаг 4: `ScriptedTouchStroke` F2.3 (`StartUV (0.5,0.25)`, `EndUV (0.5,0.75)`, `Duration 0.5`, `dt = 1/60`), затем кадры без touch до ~3 с. `touch.radius = 1`, `touch.strength = 10`. `touch.pos` по `Evaluate`; **`touch.delta = pos − prev`**, на первом кадре `delta = 0`; `Evaluate`-delta не использовать.

Прогоны на каждой геометрии:

- **V1-S** — цепочка пресета, сид как в пресете.
- **V1-N** — цепочка пресета, `SeedScalarDisk.Value = 0`.
- **V0-N** — та же цепочка **без** `VorticityConfinementPass` (11 пассов), `Seed.Value = 0` (база для сравнения).

**Assert (гейты):**

- (L1) V1-S на **каждом** кадре: нет NaN/Inf в `dye` и `velocity`; `min(dye) ≥ 0`; `max(dye) ≤ maxValue + 1e-3`. **В V0-N тоже** на каждом кадре проверять NaN/Inf в `dye` и `velocity`: иначе разность скоростей сравнивается с битой базой.
- (L2) V1-N: то же, что L1; плюс (а) в кадре, когда кисть проходит каждую из пяти точек пути `UV.y ∈ {0.25, 0.35, 0.5, 0.65, 0.75}`, `dye > 0.05` в окне `±radius` вокруг этой точки; (б) сразу после последнего кадра штриха `dye > 0.05` в окне `±radius` у **головы** (`UV.y = 0.75`). Четыре ранние точки после штриха в assert **не** входят (гипотеза в том, что нить уйдёт с оси): только лог.
- (L3) VC работает: максимум нормы разности скоростей `u_V1-N − u_V0-N` во внутренних текселах (не ближе двух текселей к рамке) **строго больше `1e-6`** хотя бы на одном кадре за 3 с. Порог ниже шага R16: гейт значит лишь «буферы различаются».

**Логировать, не гейтить** (V0-N против V1-N, одной таблицей, для 128²/512² где применимо):

- сам `max` нормы разности `u_V1 − u_V0` и **номер кадра**, на котором он достигнут (L3);
- `max |ω|` (внутри) сразу после штриха и через 3 с (`ω` по формуле curl из [ADR-027](../ADR/ADR-027-Vorticity-Confinement-Pass.md), тот же шаблон, что в кернеле);
- `max |u|` в кольце рамки (индексы текселей 0–1 от любого края) и отдельно в третьем текселе (индекс 2): **оба значения, V0 и V1, раздельно**, без отношения V1/V0 (у V0 может быть ноль);
- `Σ|u|²` сразу после штриха и через 3 с;
- значения `dye` в окнах четырёх ранних точек пути (`UV.y = 0.25, 0.35, 0.5, 0.65`) после штриха (максимум в окне `±radius`, V0-N и V1-N);
- пик `dye` на оси жеста в точках `UV.y = 0.35, 0.65` (V1-N) и FWHM нити поперёк жеста (`y = 0.5`) сразу после штриха и через 3 с, `dye` 512², V0-N и V1-N;
- `elapsedMs/N` цепочки из 12 пассов (V1-S, живая геометрия, warmup вне таймера, `N ≥ 8`, без `Read*` в цикле, `Assert.Pass(report)`), `timer=cpu_driver_not_gpu`, строка рядом с F3.1 `0.373`.

Ничего из логируемого не превращать в порог. Если `max |u|` у рамки у V1 заметно выросло — сообщить в чат: это находка, не стоп.

---

### Шаг 4 — доки после зелёного EditMode

- [`ADR-051`](../ADR/ADR-051-Vorticity-On-Dye-Inject.md): статус остаётся «Предложено» до слов владельца; добавить блок «EditMode замерено» (P1, P2, L1–L3 по строке; таблица V0/V1; `ms`).
- [`plan-stable-fluid.md`](../plan-stable-fluid.md): новая строка **F3.4 — VC поверх DyeInject**, ссылки на ADR-051 и `todo-F3.4.md`, статус «В работе (EditMode готов)».
- [`status.md`](../status.md): короткая запись с датой, формат как у F3.1; без слов «принято»/«готово».
- [`capabilities.md`](../capabilities.md): новый ассет одной строкой.
- [`Techdebt.md`](Techdebt.md): 8k — `ms` рядом с F3.1; 8l — одна фраза: F3.4 пробует VC на тонкой нити, см. ADR-051.
- `play-F3.4-touch.md` — **только заготовка протокола** по образцу `play-F3.1-touch.md`: A / B / B-ε (0.5 и 2) / B-fade (0.15); что смотреть (ADR-051 §5); **строка «после B-ε и B-fade вернуть `EpsilonVc = 1` и `DissipationRate = 0`, не сохранять ассет с крутилкой»**; раздел про пустой кадр с тремя кандидатами (ADR-051 §5), без заранее выбранного тикета; без «Отчёта оператора» и выводов.

---

### Шаг 5 — visual (владелец)

После зелёного EditMode и документов — стоп и сообщить. Протокол — ADR-051 §5. `Assign … To Scene` выполняет оператор (п.7). Решение «взято / не взято» — за владельцем; до его слов статусы не менять (п.8).

---

### Вне скоупа

Порядок Harris (E2, E3); limited MacCormack dye и `dyeMacScratch` (E4); правка `VorticityConfinementPass`/кернела/маски/дефолта `ε`; `TouchInjectScalarFieldPass`; F3.2; F3.3; цветной dye; неравномерное затухание; gate по движению; `max`-режим; правка валидаторов и `ValidateMatchingFieldGeometry`; новый `.compute`; мобильная адаптация; M2d/рендер частиц; все существующие `Fluid2D*.asset` — не трогать, не Create; коммит `.unity` без решения владельца.
