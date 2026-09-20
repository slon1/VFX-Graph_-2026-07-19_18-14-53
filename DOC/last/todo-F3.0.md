## ТЗ для программиста — F3.0 (cross-resolution dye)

**Закрыто 2026-09-20.** EditMode + visual. F3.0 в плане **Готово**. Production остаётся `Fluid2D.asset`. Протокол: [`play-F3.0-touch.md`](play-F3.0-touch.md). Правка 2026-09-20: добавлен Шаг 0 (блокер найден при разборе ТЗ — второй, более строгий guard `SimPass.ValidateMatchingFieldGeometry`, не тот, что разбирал ADR-029 в первой редакции), исправлены счёт пассов (9→10), сетка гаусса (UV, не тексели dye) и численный гейт (same-run assert, не бит-в-бит к числам другой сессии). ADR: [ADR-029](../ADR/ADR-029-Cross-Resolution-Dye.md), особенно **§1a**. Это ТЗ не переоткрывать без явного визуального/численного сигнала F3.0.

Роль: проверить числом гипотезу F0.5/Magic Fluids — dye выше разрешением, чем velocity, меньше диссипирует при том же переносе. Один пасс-состав `Fluid2D.asset` (Project→Advect), **без** VC, **без** limited MacCormack. Новых кернелов нет. По [ADR-029](../ADR/ADR-029-Cross-Resolution-Dye.md).

Прочитать ADR-029 **до кода**, особенно §1a (правка 2026-09-20). Два валидатора, `SquareTexelValidator` и `SimulationWorld.ValidatePassFieldCoordinates`, уже разрешают cross-res read — их не трогать. Есть третий, отдельный guard — `SimPass.ValidateMatchingFieldGeometry` (per-pass, при `Initialize`) — он **не** разрешает, и его нужно точечно поправить (Шаг 0). Без Шага 0 F3.0 не реализуется технически, не только «численно не подтверждается».

Зафиксировано — не пересматривать:

1. **`Fluid2D.asset` / `Fluid2D_HarrisOrder.asset` / `Fluid2D_Vorticity.asset` / `Fluid2D_MacCormackDye.asset` не трогать.** Не запускать их Create. Новый путь — `Assets/Effects/Fluid2D_HighResDye.asset`.
2. **Кернелы F1/F2 не менять.** `DivergenceFieldPass`, `ZeroMeanScalarPass`, `JacobiPhiPass`, `SubtractPhiGradientPass`, `SolidWallVelocityPass`, `AdvectVelocityFieldPass`, `SeedScalarDiskPass`, `TouchInjectVelocityFieldPass` — без правок класса/кернела/параметров. `AdvectScalarPass` — **единственное** исключение: Шаг 0 добавляет ей opt-in-флаг на `velocity`-запросе, кернел и формулу не трогает. `SquareTexelValidator` и `SimulationWorld.ValidatePassFieldCoordinates` — без правок (они уже разрешают cross-res read). `SimPass.ValidateMatchingFieldGeometry` — правится **точечно**, по Шагу 0, не шире. Если Шаг 0 недостаточен на практике — **стоп**, разбор в чат, не патчить валидатор шире молча.
3. **Нет VC, нет MacCormack, нет `dyeMacScratch`.** Состав пассов — ровно как `Fluid2D.asset` (`CreateFluid2DEffect` в `M3DDemoTools.cs`), только разрешение поля `dye` другое.
4. **`velocity`/`fluidD`/`fluidPhi` — 128², `Size=32`, форматы как везде (`R16G16_SFloat`/`R32_SFloat`/`R32_SFloat`).** Не менять.
5. **`dye` — 512², тот же `Size=32`, `R16_SFloat`.** `Size` **обязан** совпадать со всеми остальными полями пасса (`ValidatePlaneAgainstPrimary` кидает на разных `Size`, не на разном `Resolution`). Не поднимать `dye` на `R32_SFloat` «на всякий случай» — не гейт этого тикета.
6. **Численный гейт — синтетический харнес, не живой ассет.** `Size=64`, `velocity` 64² (`h=1`, `dt=1`, как ADR-028 §7), `dye` 64/128/256² на той же `Size=64`. Контрольная строка `dye=64` — **bilinear-строка** таблицы ADR-028 §7 (чистый `AdvectScalar`, без MacCormack): `dCOM_x=13.594`, пик `0.74387`. Не путать с MacCormack-строкой той же таблицы (`dCOM_x=13.261`, пик `0.75639`) — F3.0 не имеет MacCormack в цепочке, там другая схема.
7. **Гейт: пик `dye=256` строго выше пика `dye=64` (тот же carrier, те же 8 шагов, та же `velocity`).** Assert, не лог. Красный → стоп, числа в чат, не крутить carrier/шаги/σ, F3.1 не открывать.
8. **`SeedScalarDiskPass`/`AdvectScalarPass` дисперчатся по `dye` (primary write).** Не трогать `RequiresSquareTexel` (обе `false`, уже так).
9. **GPU ms — запись, не порог** (Techdebt 8k). Нет `Assert.Less`.
10. **EditMode зелёный ≠ F3.0 закрыт.** Нужен visual оператора — новый `play-F3.0-touch.md` по образцу `play-F2.0-touch.md`, не в этом ТЗ (пишется после кода).
11. **`radiusUV` живого ассета** — Create пишет фабричный `0.08` (как везде); после Create поднять на **0.16** (`Load`+`SetDirty`, как F2.0/F2.1/F2.2), не второй Create.
12. Assign в сцену после EditMode не делать — для visual, отдельным шагом оператора.

13. **Блокер — читать §1a до Шага 0.** `AdvectScalarPass` — multi-role (`dye` A / `velocity` B), `FieldKernelPass.Initialize` вызывает `ValidateMatchingFieldGeometry` (`SimPass.cs`), которая сравнивает `Resolution` у **всех** ролей, не только plane. Это отдельный guard от `SimulationWorld.ValidatePassFieldCoordinates` (World-уровень, только plane для read). Без Шага 0 `Initialize` бросит на `dye=512²`/`velocity=128²`, Шаг 1 и 2 не дойдут до `Execute`.
14. **Фикс — точечный opt-in-флаг, не общее правило.** Не убирать сравнение `Resolution` у Read-роли **глобально** — это тихо снимет тот же guard у `JacobiPhiPass`/`SubtractPhiGradientPass` (там вторая роль тоже Read: `fluidD`/`fluidPhi`). Их изолированные тесты (`JacobiPhiPassTests`, `SubtractPhiGradientPassTests`) и `FieldSlotNamingTests.FieldKernelPass_DualRole_MismatchedResolution_Throws` — **не трогать**, у них флаг не ставится.

Референсы: `M3DDemoTools.CreateFluid2DEffect` (клонировать состав пассов **этого** метода, не Vorticity/MacCormack); `Fluid2DProductionProfileTests` (профиль F2.0, не переносить сюда — другая геометрия); `LimitedMacCormackDyeTests` / ADR-028 §7 (оракул `Size=64`, `dt=h=1`, carrier `(1.7,0)`, 8 шагов — **геометрию** переиспользовать, MacCormack-шаги не переносить); `Fluid2DMacCormackDyePresetTests` (стиль composition-теста, не GPU); `Fluid2DWorldSmokeTests` (стиль 8j).

Имена: ассет `Fluid2D_HighResDye.asset`. Меню `Tools/M3D/Create Fluid2D HighResDye Experiment` / `Tools/M3D/Assign Fluid2D HighResDye Experiment To Scene`. Тесты: `Fluid2DHighResDyeSmokeTests.cs` (шаг 1), `Fluid2DHighResDyeCrossResTests.cs` (шаг 2, `[Category("GPU")]`), `Fluid2DHighResDyePresetTests.cs` (шаг 3, не GPU), `Fluid2DHighResDyeWorldSmokeTests.cs` (шаг 4).

Код писать. Документацию из шага 6 — тоже.

---

### Шаг 0 — точечный opt-in на `FieldRequest`/`ValidateMatchingFieldGeometry`

Читать [ADR-029 §1a](../ADR/ADR-029-Cross-Resolution-Dye.md) целиком до кода.

1. `FieldRequest` (`FieldDescriptor.cs` или где объявлен) — новый параметр, например `bool allowResolutionMismatch = false` (имя на выбор реализатора, суть — «этот биндинг не участвует в сравнении `Resolution`, участвует в сравнении plane»). Не менять существующие вызовы `FieldRequestSets.Single`/`Pair` без явной необходимости — можно добавить отдельный оверлоад/оптион для этого одного случая, не трогая сигнатуру для всех остальных пассов.
2. `SimPass.ValidateMatchingFieldGeometry` (`SimPass.cs:691`) — **важно про порядок**: `Initialize` вызывает `CollectFieldBinds(FieldReads)` до `CollectFieldBinds(FieldWrites)`, поэтому у `AdvectScalarPass` `fieldBinds[0] = velocity` (флаг стоит) становится `reference`, а `dye` (флага нет) — «текущим» в сравнении. Формулировка «пропускать, если флаг у текущего» здесь **не сработает** (текущий — `dye`, без флага; `reference` со флагом молча остаётся эталоном, и условие всё равно бросит). Правильно: при сравнении `descriptor.Resolution != reference.Resolution` пропускать эту часть условия, если флаг стоит **хотя бы у одного** из двух — у `reference` **или** у текущего биндинга. Условие по `Origin`/`AxisU`/`AxisV`/`Size` — **всегда**, без исключений, независимо от флага с любой стороны. Порядок `CollectFieldBinds` не менять.
3. Флаг ставить **только** в `AdvectScalarPass.FieldReads` на запросе `velocityField` (Role B, Read). Ни один другой пасс (`JacobiPhiPass`, `SubtractPhiGradientPass`, `VorticityConfinementPass`, `CopyScalarPass`, `LimitedMacCormackCombinePass`, стабы `FieldSlotNamingTests`) флаг не выставляет — дефолт `false` сохраняет их текущее поведение бит-в-бит. Флаг участвует в `FieldRequest.Equals`/`GetHashCode` (кэш `FieldRequestSets` сравнивает запросы по значению — без этого можно получить неверный кэш-хит между запросом с флагом и без).
3a. **Контракт-тест:** в существующем/новом `Contract_Roles_*`-тесте `AdvectScalarPassTests` проверить явно, что флаг на `velocity`-запросе (Role B) — `true`, а на `dye`-запросе (Role A) — `false`/дефолт. Регрессия на случай, если кто-то потом переставит флаг на другое поле.
4. **Тест-регрессия, которую это меняет (одна):** `AdvectScalarPassTests.Initialize_MismatchedResolution_ThrowsMatchingResolutionAndPlane`. Разбить на два теста:
   - `Initialize_MismatchedPlane_Throws` — тот же дух, но меняется `Size` (не только `Resolution`) у одного из полей → по-прежнему throw, сообщение содержит «matching Resolution and plane» (или обновлённое сообщение, если поменяли текст — сверить с `JacobiPhiPassTests`/`SubtractPhiGradientPassTests`, у них сообщение **не** менять, значит текст ошибки как строка должен остаться прежним для их сценария; для нового opt-in-пути можно оставить то же сообщение, раз оно всё ещё говорит о несовпадении, только теперь это только про plane).
   - `Initialize_MismatchedResolutionSamePlane_DoesNotThrow` — новый тест: `dye` 32² / `velocity` 64², **тот же** `Size` → `Initialize` **не** бросает.
5. **Не трогать:** `JacobiPhiPassTests.Initialize_MismatchedResolution_ThrowsMatchingResolutionAndPlane`, `SubtractPhiGradientPassTests.Initialize_MismatchedResolution_ThrowsMatchingResolutionAndPlane`, `FieldSlotNamingTests.FieldKernelPass_DualRole_MismatchedResolution_Throws`. Прогнать все три после Шага 0 — зелёные без правок, это доказательство, что фикс точечный, не общий.
6. Красный на пункте 5 после правки Шага 0 — **стоп**, разбор в чат, значит фикс задел не только `AdvectScalarPass`.

---

### Шаг 1 — build/smoke гейт (до численного харнеса)

Новый файл `Assets/Tests/Editor/Fluid2DHighResDyeSmokeTests.cs`, `[Category("GPU")]` (реальный `Execute`, не только Build).

Цель — доказать практически утверждение ADR-029 §«Находка»: композиция с разным `Resolution` у `dye` и `velocity`/`fluidD`/`fluidPhi` (тот же `Size`) строится и исполняется.

```
FieldDescriptor velocity = new(name="velocity", res=128², size=32, R16G16_SFloat)
FieldDescriptor fluidD   = new(name="fluidD",   res=128², size=32, R32_SFloat)
FieldDescriptor fluidPhi = new(name="fluidPhi", res=128², size=32, R32_SFloat)
FieldDescriptor dye      = new(name="dye",      res=512², size=32, R16_SFloat)
```

Новые экземпляры пассов (не из ассета), состав как `CreateFluid2DEffect`: `TouchInjectVelocity → SeedScalarDisk(dye) → Divergence → ZeroMean(Jacobi×40 хватит 8, не 40, — численный смысл не важен на этом шаге) → Subtract → SolidWall → AdvectVelocity → SolidWall → AdvectScalar(dye, velocity)`.

Гейты:
- `Rebuild`/`Initialize` не бросает (`ValidatePassFieldCoordinates`, `SquareTexelValidator` не красные).
- Один кадр `Execute` не бросает.
- Нет NaN/Inf на `dye` и `velocity` после кадра (readback, простая проверка).

Красный здесь — **стоп всего тикета**, разбор в чат, не патчить валидатор внутри F3.0 без обсуждения.

---

### Шаг 2 — численный гейт, синтетический оракул

Новый файл `Assets/Tests/Editor/Fluid2DHighResDyeCrossResTests.cs`, `[Category("GPU")]`. Compute: `FieldPasses.compute` (`AdvectScalar`), никаких новых кернелов.

Геометрия — **ровно** ADR-028 §7 / F1.7 (`Techdebt 5`): `Size=64`, `dt=1`, `h_velocity=1` (`velocity` 64²), carrier `(1.7, 0)` (off-grid), **8 шагов**, R32 (не R16 — половинная точность здесь не измеряется, это F2.2 §7 отдельно отмечал). Один `AdvectScalarPass`, `Reverse=false`, без MacCormack, без VC.

**Сид гаусса — в UV, не в текселях `dye`.** F1.7 сеет `σ=1.5`, центр `(20.5, 32.5)` **в текселях** сетки 64² (`dye`==`velocity` там). Если F3.0 повторит эти числа как индексы `dye`-сетки — на 256² это другой мировой blob, пик несравним между строками (ADR-029 §3, правка 2026-09-20). Задавать сид через `centerUV = (20.5/64, 32.5/64)`, `σ_UV = 1.5/64`, растеризовать на CPU по UV в текстуру `dye` нужного разрешения перед `harness.RunPass`. `dCOM_x`/пик — в world/`velocity`-текселях (`h_velocity=1`), не в индексах `dye`.

Три прогона, отличаются только `Resolution` поля `dye` (**та же** `Size=64`, **тот же** UV-сид, **тот же** `velocity`-сид):

| Строка | `dye.Resolution` | `dye:velocity` texel ratio |
| --- | --- | --- |
| контроль | 64² | 1× (ориентир — bilinear-строка ADR-028 §7: `dCOM_x≈13.594`, пик `≈0.74387`; **не** MacCormack-строка той же таблицы `13.261`/`0.75639` — F3.0 без MacCormack) |
| middle | 128² | 2× |
| target | 256² | 4× (то же отношение, что живой ассет `512/128`) |

`velocity` — **один и тот же** экземпляр/сид во всех трёх прогонах.

Замерить на каждой строке: `dCOM_x`, пик интерьера после 8 шагов, min(dye)≥0, Inf/NaN.

**Гейт — внутри одного прогона (same-run), не бит-в-бит к цифрам другой сессии:** пик(`dye=256`) **строго выше** пика(`dye=64`), посчитанных в **этом же** тесте на **этом же** `velocity`-сиде — `Assert.Greater`. Формат сообщения ассерта — числа обеих строк, не булево. Красный → стоп, в чат таблицу, не крутить `σ`/carrier/шаги, **не открывать F3.1**. Не требовать точного совпадения с `13.594`/`0.74387` — это ориентир для лога, не hard-coded expected (другая сессия/драйвер даёт немного другие числа).

Лог (не гейт): `dCOM_x` всех трёх строк, допуск `|dCOM_x−13.6|<0.5` как мягкая проверка (не строгий assert, как ADR-028 §7 делает для похожего случая) — если заметно расходится между строками, отдельная находка, не повод чинить формулу до разбора. `elapsedMs/N` каждой строки, `timer=cpu_driver_not_gpu`, без порога.

---

### Шаг 3 — пресет `Fluid2D_HighResDye.asset`

`M3DDemoTools.cs`, рядом с `CreateFluid2DEffect`.

```csharp
private const string Fluid2DHighResDyePath = "Assets/Effects/Fluid2D_HighResDye.asset";

[MenuItem("Tools/M3D/Create Fluid2D HighResDye Experiment")]
public static void CreateFluid2DHighResDyeExperiment()
{
    // Клон CreateFluid2DEffect: те же 4 FieldDescriptor, тот же порядок пассов
    // (TouchInject → Seed → Divergence → ZeroMean → Jacobi×40 → Subtract → Wall
    //   → AdvectVelocity → Wall → AdvectScalar), НЕТ VorticityConfinementPass,
    // НЕТ CopyScalar/MacCormackCombine, нет dyeMacScratch.

    Vector2Int res128 = new Vector2Int(128, 128);
    Vector2Int res512 = new Vector2Int(512, 512);   // dye только
    Vector2 planeSize = new Vector2(32f, 32f);

    // ... CreateEffect(...) как CreateFluid2DEffect, путь Fluid2DHighResDyePath ...

    // В цикле по fieldsProp: resolution по имени, не единый res128 для всех:
    //   name == "dye"  → res512
    //   иначе          → res128
    // size — planeSize для ВСЕХ полей без исключения (иначе ValidatePlaneAgainstPrimary
    // кидает "different plane bases" в AdvectScalarPass).
}
```

Форматы в том же цикле — как `CreateFluid2DEffect` (`velocity`→`R16G16_SFloat`, `dye`→`R16_SFloat`, иначе `R32_SFloat`). После Create поднять `SeedScalarDiskPass.RadiusUV` на `0.16` (`Load`+`SetDirty`, без второго Create) — как F2.0/F2.1/F2.2.

`Tools/M3D/Assign Fluid2D HighResDye Experiment To Scene` — клон `AssignFluid2DToScene` (Effect + GroundXZ InputRouter + `EnsurePassLibrary`), `visualEffect` не трогать.

**`Fluid2DHighResDyePresetTests.cs`** (не GPU, как `Fluid2DMacCormackDyePresetTests`): 4 поля (velocity/fluidD/fluidPhi/dye), `dye.Resolution == (512,512)`, остальные `(128,128)`, все `Size == (32,32)`, **10 пассов** (`TouchInject, Seed, Divergence, ZeroMean, Jacobi, Subtract, SolidWall, AdvectVelocity, SolidWall, AdvectScalar` — считать по `CreateFluid2DEffect`, не по Harris/9-пасс-варианту без второй стены), типы/порядок как `Fluid2D.asset` (без VC/MacCormack), одна `SeedScalarDiskPass` с `RadiusUV==0.16`, **две** `SolidWallVelocityPass`, форматы как указано. Сообщение об ошибке — «run Tools/M3D/Create Fluid2D HighResDye Experiment».

---

### Шаг 4 — world smoke (стиль 8j)

`Fluid2DHighResDyeWorldSmokeTests.cs`: неактивный `GameObject` с `SimulationWorld`, `effect = Fluid2D_HighResDye.asset` (загруженный с диска, не `CreateInstance`-копия), `passLibrary` — дублированный список путей строками в тесте (как остальные world-smoke тесты). `Rebuild()` не бросает; `world.enabled == true`. Без N кадров, без readback, без Touch. `Build()` private — не менять доступность.

---

### Шаг 5 — GPU ms (запись)

В шаге 2 или отдельным методом: `elapsedMs/N` для полной цепочки (Div→ZeroMean→Jacobi×40→Subtract→Wall→AdvectVel→Wall→AdvectScalar) на **живой** геометрии (128²/512², `Size=32`, как ассет), один warmup-кадр вне таймера, `N≥8` кадров в таймере, без `Read*` внутри цикла. `Assert.Pass(report)`. Строка рядом с F2.0 `elapsedMs/N=0.208` и F2.1 `0.272` — без порога, в Techdebt 8k.

---

### Шаг 6 — доки после зелёного EditMode

- ADR-029: добавить «EditMode замерено» + таблицу §3 (три строки `dye=64/128/256`) + ms.
- `plan-stable-fluid.md` F3.0: **Готово** после visual. Протокол: [`play-F3.0-touch.md`](play-F3.0-touch.md) (A=`Fluid2D` vs B=`Fluid2D_HighResDye`, тот же скрипт/`radiusUV`).
- `status.md` — короткая запись с датой.
- `pass-catalog.md` / `capabilities.md` — новый ассет одной строкой (новых пассов нет, менять секции пассов не нужно).
- Techdebt 8k — новое число ms рядом с F2.0/F2.1, без порога.

---

### Вне скоупа

VC; limited MacCormack; `dyeMacScratch`; continuous dye injection (F3.1); правка VC-стенсиля (F3.2); MacCormack velocity / второй Poisson (F3.3); подъём Jacobi; подъём `velocity`-разрешения; `dye` на `R32_SFloat`; правка `SquareTexelValidator`/`ValidatePassFieldCoordinates` (если гейт шага 1 красный — стоп и разбор, не тихий патч); `Fluid2D.asset`/`HarrisOrder`/`Vorticity`/`MacCormackDye` — не трогать, не Create; мобильная адаптация; M2d/рендер частиц.
