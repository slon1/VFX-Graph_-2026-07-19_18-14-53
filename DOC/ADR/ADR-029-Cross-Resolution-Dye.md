## ADR-029: Cross-resolution dye (F3.0)

**Статус:** Принято. F3.0 — **Готово**. Visual 2026-09-20: HighResDye заметно острее Fluid2D, тот же макро-гриб. `Fluid2D.asset` не меняли. [`play-F3.0-touch.md`](../last/play-F3.0-touch.md). §«Находка» дополнена 2026-09-20 (второй, более строгий guard — см. §1a).
**Дата:** 2026-09-20 (правки 2026-09-20: §1a, дважды)
**Контекст:** M3D Framework, после закрытия F2 ([ADR-026](ADR-026-F2-Small-Scale-Structure.md)) — VC (F2.1/F2.1b) и limited MacCormack dye (F2.2) реализованы корректно, но не режут пятно в нить. Look F2 не взят.
**План:** [`plan-stable-fluid.md`](../plan-stable-fluid.md) §4
**ТЗ:** [`todo-F3.0.md`](../last/todo-F3.0.md)
**Не меняет в этом ADR:** кернелы F1 (Divergence/Jacobi/ZeroMean/Subtract/SolidWall); кернелы F2 (VorticityConfinement/CopyScalar/MacCormackCombine); `Fluid2D.asset`, `Fluid2D_HarrisOrder.asset`, `Fluid2D_Vorticity.asset`, `Fluid2D_MacCormackDye.asset`.

### Контекст

F2 закрыта без look ([ADR-026](ADR-026-F2-Small-Scale-Structure.md), [ADR-027](ADR-027-Vorticity-Confinement-Pass.md), [ADR-028](ADR-028-Limited-MacCormack-Dye.md)). Три причины, все задокументированы:

1. **Диссипация velocity** ([Techdebt 5](../last/Techdebt.md)) — bilinear semi-Lagrangian на скорости, не на dye, названа явным триггером для «MacCormack velocity» в ADR-026 «Что не входит».
2. **Авторский `radiusUV=0.16` + короткий жест** ([Techdebt 8n](../last/Techdebt.md)) — тонкий хребет F1.8b получился от 10-секундного кругового мазка рукой, не от солвера; F2.3 на скриптованном 0.5с мазке дал один макро-гриб на всех четырёх режимах A/B/C/D.
3. **VC садится на рамку через clamp-curl** ([Techdebt 8l](../last/Techdebt.md)) — маска `f=0` (F2.1b) сняла торнадо, но интерьер не стал тоньше.

Перед тем как открывать M2d (переписка рендера частиц, [Techdebt 9](../last/Techdebt.md) — отдельный, дорогой трек про VFX Graph на мобиле, не про fluid), решили дёшево проверить механизм, который в плане `stable-fluid` был явно вынесен за скоуп F2 под именем **F0.5** («снять matching-resolution для read в multi-role (cross-res dye/velocity)»).

Это ровно приём `paveldogreat/webgl-fluid-simulation` (Magic Fluids, на который ориентируется этот фреймворк, см. `architecture.md` §Contexts): `SIM_RESOLUTION` (скорость/давление) — низкое, `DYE_RESOLUTION` — высокое. Bilinear-диссипация на каждом шаге адвекции масштабируется с размером текселя **dye** в world-единицах (полширины ядра bilinear-фильтра ≈ полтекселя); если тексель dye мельче при том же самом переносе (том же `velocity`), потеря амплитуды на шаг меньше — без нового кернела, без второго Poisson, без MacCormack.

### Находка при чтении кода — F0.5 дешевле, чем предполагал план

Формулировка F0.5 в плане подразумевала, что multi-role read-полям сейчас **запрещено** разное разрешение, и это нужно «снять». При чтении кода это не подтвердилось:

```
439:    /// FieldKernelPass pushes one FieldParams block from the primary field. All fields on a
440:    /// pass must share plane basis; write fields must also share resolution (dispatch size).
441:    /// Read fields may differ in resolution (normalized UV sampling — e.g. high-res dye).
```
*(`SimulationWorld.cs`, `ValidatePassFieldCoordinates`)*

`ValidatePlaneAgainstPrimary` (тот же файл) сравнивает у read/write-полей одного пасса только `Origin`/`AxisU`/`AxisV`/`Size` — **не** `Resolution`. `SquareTexelValidator` (отдельный, более строгий чек) применяется только к пассам с `RequiresSquareTexel == true` — это ровно проекционные пассы Stam (`Divergence`/`ZeroMean`/`Jacobi`/`Subtract`/`SolidWall`/`VorticityConfinement`), которые все работают только с `velocity`/`fluidD`/`fluidPhi` и dye не касаются. `AdvectScalarPass` (F1.7) — `RequiresSquareTexel => false`, читает `velocity` (Role B) через `FieldReadB.SampleLevel(sampler_linear_clamp, uv, 0)` — нормализованная UV-выборка, независимая от разрешения `velocity`. `SeedScalarDiskPass`, `CopyScalarPass`, `LimitedMacCormackCombinePass` — тоже `RequiresSquareTexel => false` (не переопределяют, база `SimPass.RequiresSquareTexel => false`).

Вывод (на момент первой редакции): **инфраструктура cross-res dye уже на месте**, не требует нового кода в валидаторах или кернелах F1.7/F2.2. Это оказалось неполным — см. §1a.

#### 1a. Уточнение: второй, более строгий guard — `SimPass.ValidateMatchingFieldGeometry`

`ValidatePassFieldCoordinates` (выше) — проверка **World**-уровня, при `Rebuild`/`Build`. Есть отдельная, более строгая проверка **pass**-уровня, при `FieldKernelPass.Initialize` (`SimPass.cs`, вызывается для любого пасса с `multiRoleBindings == true`, т.е. с ролями `{A,B}`):

```
699:            if (descriptor.Resolution != reference.Resolution ||
700:                descriptor.Origin != reference.Origin ||
...
                throw new InvalidOperationException(
                    $"{DisplayName}: multi-field-per-kernel requires matching Resolution and plane " +
```

Она сравнивает `Resolution` **и** plane у **всех** полей пасса, независимо от роли (Read или Write). `AdvectScalarPass` — multi-role (`dye` Role A WritePingPong, `velocity` Role B Read) → этот guard сработает при `Initialize`, раньше, чем дело дойдёт до `Execute`. Регрессия уже зафиксирована тестом F1.7: `AdvectScalarPassTests.Initialize_MismatchedResolution_ThrowsMatchingResolutionAndPlane` (`dye` 32² / `velocity` 64², тот же `Size` → throw).

Итог: `dye=512²` + `velocity=128²` (тот же `Size`) **бросит** на `Initialize`, до любого GPU-кода. §1 (World-уровень) верно, но недостаточно — F3.0 не реализуется без правки этого guard.

**Решение — точечный opt-in, не общее правило.** Общее «у Read-роли не сравнивать Resolution» (первая версия этого фикса) незаметно снимает тот же guard и у `JacobiPhiPass`/`SubtractPhiGradientPass` — там несовпадение резолюшна тоже проверяется через Read-роль (`fluidD`/`fluidPhi`, Role B). В живом `SimulationWorld` их всё равно поймает `SquareTexelValidator` (они `RequiresSquareTexel => true`), но их **изолированные** unit-тесты (`JacobiPhiPassTests`, `SubtractPhiGradientPassTests`) в общем случае перестали бы бросать в `Initialize()` — молчаливое расхождение между «что ловит `Initialize` сам по себе» и «что ловит только Build». Не трогать.

Вместо этого — новый bool-флаг на `FieldRequest` (например `AllowResolutionMismatch`, default `false`). Флаг ставится **только** на `velocity`-запрос (Role B, Read) в `AdvectScalarPass.FieldReads`. Ни один другой пасс его не выставляет — дефолт `false` для всех остальных, включая тот же `FieldRequestSets.Single`/`Pair`, если у них нет отдельного параметра (не менять сигнатуру без необходимости — можно завести отдельный вызов/оверлоад именно для этого случая).

**Уточнение порядка сравнения (вторая правка §1a, 2026-09-20).** `Initialize` вызывает `CollectFieldBinds(FieldReads)` **до** `CollectFieldBinds(FieldWrites)` — у `AdvectScalarPass` это значит `fieldBinds[0] = velocity` (Role B, **с** флагом), `fieldBinds[1] = dye` (Role A, **без** флага). `ValidateMatchingFieldGeometry` берёт `reference = fieldBinds[0]` (велосити) и в цикле сравнивает **текущий** элемент (`dye`, i=1) с `reference`. Формулировка «пропускать сравнение `Resolution`, если флаг стоит у **текущего** биндинга» проверяла бы флаг у `dye` — там его нет, бросок не снялся бы. Правильно: **пропускать сравнение `Resolution` в этой паре, если флаг стоит хотя бы у одного из двух** (у `reference` **или** у текущего) — `Origin`/`AxisU`/`AxisV`/`Size` сравнивать всегда, независимо от флага с любой стороны. Порядок `CollectFieldBinds` не менять. Поскольку флаг сегодня стоит только на `velocity` в `AdvectScalarPass` (единственный пасс с двумя биндами, где он используется), «любая сторона» здесь эквивалентно «сторона, где стоит флаг» — расширять до пассов с 3+ биндами сейчас не нужно, в кодовой базе таких нет.

Затронутый тест — **один**: `AdvectScalarPassTests.Initialize_MismatchedResolution_ThrowsMatchingResolutionAndPlane`. Его текущий сценарий (`dye` 32² / `velocity` 64², тот же `Size`) с флагом перестанет бросать — это осознанное изменение контракта именно этого пасса, не побочный эффект. Заменить его содержимое на две проверки:

1. Разный **plane** (`Size` отличается) — по-прежнему throw (плейн не относится к флагу).
2. Разный **только** `Resolution`, тот же `Size` — **не** throw (новое поведение, ровно то, что нужно F3.0).

`JacobiPhiPassTests.Initialize_MismatchedResolution_ThrowsMatchingResolutionAndPlane`, `SubtractPhiGradientPassTests.Initialize_MismatchedResolution_ThrowsMatchingResolutionAndPlane`, `FieldSlotNamingTests.FieldKernelPass_DualRole_MismatchedResolution_Throws` — **не трогать**, у них флаг не ставится, поведение не меняется (проверено: `FieldSlotNamingTests`' стаб — Write/Write, `Jacobi`/`Subtract` — Read-роль без флага, дефолт `false` сохраняет throw).

### Решение

#### 1. Изолировать одну переменную — разрешение dye, не VC/MacCormack

По методологии F2 (каждый тикет — один механизм: F2.1 отдельно от F2.2): F3.0 берёт **production-порядок** (`Fluid2D.asset`: Touch → Seed(dye) → Divergence → ZeroMean → Jacobi×40 → Subtract → SolidWall → Advect velocity → SolidWall → Advect dye), без VC и без limited MacCormack. Единственное отличие от `Fluid2D.asset` — разрешение поля `dye`.

Если F3.0 даст видимый эффект — F3.1/F3.2 (continuous injection, non-clamp VC-стенсиль) кладутся **на тот же cross-res dye**, не переигрывают его отдельно.

#### 2. Состав `Fluid2D_HighResDye.asset`

Пять полей... нет, **четыре**, как `Fluid2D.asset` — `dyeMacScratch` не входит (нет MacCormack в этом тикете):

| Поле | Resolution | Size | Format |
| --- | --- | --- | --- |
| `velocity` | 128² (не менять) | 32 | `R16G16_SFloat` |
| `fluidD` | 128² (не менять) | 32 | `R32_SFloat` |
| `fluidPhi` | 128² (не менять) | 32 | `R32_SFloat` |
| `dye` | **512²** | 32 (тот же `Size`, обязательно — `ValidatePlaneAgainstPrimary`) | `R16_SFloat` (не менять на R32 — точность представления не то ограничение, ограничение — размер текселя) |

`Size` у `dye` **обязан** совпадать с `velocity`/`fluidD`/`fluidPhi` (32×32) — иначе `ValidatePlaneAgainstPrimary` в `AdvectScalarPass` (primary = `dye`, read = `velocity`) кинет `InvalidOperationException` про разные plane bases. Именно совпадение `Size` при разном `Resolution` — это и есть cross-res: одна и та же мировая протяжённость, разная плотность текселей.

Пассы — **тот же порядок**, что `Fluid2D.asset`, без изменений класса/параметров:

```
TouchInjectVelocity → SeedScalarDisk(dye) → Divergence → ZeroMean → Jacobi×40 → Subtract
  → SolidWall → AdvectVelocity → SolidWall → AdvectScalar(dye, velocity)
```

`SeedScalarDiskPass` дисперчится по разрешению `dye` (единственное write-поле пасса) — диск будет ровнее на 512², это ожидаемо и не гейт. `AdvectScalarPass` дисперчится по `dye` (primary write), читает `velocity` по UV — резолюшн-агностик, как показано выше.

Имя — **техника, не номер фазы** ([ADR-026 §«Конвенция имён»](ADR-026-F2-Small-Scale-Structure.md)): `Fluid2D_HighResDye.asset`. Не `Fluid2D_F3`.

#### 3. Числовой гейт — синтетический оракул, не живой ассет

Живой ассет (128²/512², `Size=32`) — для смоука и визуала. Численно **дешевле и честнее** мерить механизм на отдельном харнесе, где `velocity` зафиксирована в точности как существующие оракулы F1.7/ADR-028 §7 (`Size=64`, `velocity` 64², `dt=h=1` → texel-CFL=1), чтобы старые числа (`dCOM_x≈13.6`, пик bilinear `0.744` на carrier `(1.7,0)`, 8 шагов, R32) были строкой `dye=64` этой же таблицы — регрессия, не новый замер с нуля.

`dye` варьируется на **той же** `Size=64`: **64² (контроль) / 128² / 256²**. Отношение текселей `dye:velocity` на строке 256² — **4×** (`64/256` против `64/64`), то же отношение, что у живого ассета (`512/128`). Строка 128² даёт промежуточную точку (2×) — на случай, если 512² окажется избыточно дорогим на визуале, у оператора будут числа для 256² живого ассета без нового прогона.

**Гаусс — в UV, не в текселях dye.** F1.7 сеет `σ=1.5`, центр `(20.5, 32.5)` в **текселях** сетки 64² (там `dye` и `velocity` совпадали, тексель = единица). Если F3.0 повторит те же числа как индексы **dye**-сетки — на 256² это другой мировой blob, пик несравним между строками. Сид этого харнеса задаётся в **UV** (мировая, resolution-независимая координата), зафиксированной по F1.7: `centerUV = (20.5/64, 32.5/64)`, `σ_UV = 1.5/64`. Растеризация сида (запись значений в текстуру `dye` нужного разрешения) — на CPU по UV, не переносом текселя 1:1. Пик/рамка сравнения — 1 тексель **dye** той строки (не 1 тексель `velocity`).

| Замер | `dye=64` (контроль, UV-сид = F1.7) | `dye=128` | `dye=256` |
| --- | --- | --- | --- |
| `dCOM_x` (8 шагов, carrier `(1.7,0)`, world-единицы) | **13.594** (`\|dCOM−13.6\|=0.006`) | **13.594** (`0.006`) | **13.602** (`0.002`) |
| Пик интерьера после 8 шагов | **0.74387** | **0.89015** | **0.97924** |

**EditMode замерено (2026-09-20).** Same-run гейт `пик256 > пик64` зелёный (`0.97924 > 0.74387`). Контроль `dye=64` совпал с bilinear-строкой ADR-028 §7 (`13.594` / `0.74387`). `min(dye)≥0`, Inf/NaN нет. Живая цепочка 128²/512², Jacobi×40, warmup вне таймера, N=8: **`elapsedMs/N=0.184`**, `timer=cpu_driver_not_gpu` (рядом F2.0 `0.208`, F2.1 `0.272` — запись, не порог). `Rebuild()` на `Fluid2D_HighResDye.asset` зелёный.

**Visual (2026-09-20).** A=`Fluid2D.asset` vs B=`Fluid2D_HighResDye.asset`, тот же `ScriptedTouchStroke` F2.3, `radiusUV=0.16`. B безусловно острее A: край шляпки, ножевая прорезь, ножка читается тонкой нитью (на 128² её съедал bilinear). Макро-силуэт тот же гриб — tracer, не смена velocity. Production не меняли. [`play-F3.0-touch.md`](../last/play-F3.0-touch.md).

**Гейт — внутри одного прогона (same-run assert), не бит-в-бит к цифрам другой сессии/другого драйвера/GPU:** пик `dye=256` **строго выше** пика `dye=64` на **том же** прогоне (тот же carrier/шаги/`velocity`-сид) — `Assert.Greater(peak256, peak64)`, тот же стиль, что ADR-028 §7 (`macPeak > bilinearPeak`), но без завязки на абсолютные числа предыдущей сессии. Абсолютные числа (`13.594`/`0.74387`) — ориентир в лог/комментарий, не hard-coded ожидание. Красный → стоп, гипотеза cross-res численно не подтверждена, F3.1+ не открывать.

**Важно:** это отдельная схема от [ADR-028](ADR-028-Limited-MacCormack-Dye.md) §7 — там `13.261`/`0.75639` — числа **limited MacCormack**, не bilinear-контроля. F3.0 не имеет MacCormack в цепочке; путать эти две строки одной таблицы — ошибка (была допущена в первой редакции этого ADR, исправлено).

`velocity` в этом харнесе — **новые экземпляры пассов**, не объекты живого ассета (как во всех F1/F2 харнесах).

#### 4. Смоук — до численного гейта, не после

Первый шаг реализации — не численный харнес, а build/smoke-тест, который **доказывает утверждение §«Находка»** практически: `EffectAsset` с `dye` 512² и `velocity`/`fluidD`/`fluidPhi` 128² на одном `SimulationWorld` строится (`Rebuild()`) и один кадр `Execute` проходит без исключений и без NaN/Inf. Если здесь красно — весь остальной план ADR неверен, дальше не идти.

#### 5. Что не входит

- VC, limited MacCormack, `dyeMacScratch` — F3.1/F3.2/F3.3 отдельно, после visual-сигнала F3.0.
- Continuous dye injection (сейчас — один статичный `SeedScalarDiskPass`) — F3.1.
- Правка VC-стенсиля у рамки — F3.2.
- MacCormack velocity / второй Poisson — F3.3, самый дорогой тикет фазы.
- Подъём Jacobi iterations, подъём разрешения `velocity` — не в этом тикете (гипотеза F3.0 — именно **разница** разрешений, поднимать обе стороны значило бы не изолировать переменную).
- `Fluid2D.asset` / `Fluid2D_HarrisOrder.asset` / `Fluid2D_Vorticity.asset` / `Fluid2D_MacCormackDye.asset` — не трогать, не Create.
- Мобильная адаптация; M2d / рендер частиц (Techdebt 9) — отдельный, ортогональный трек.
- `dye` на `R32_SFloat` — формат не гейт этого тикета; если R16 на 512² даст артефакт precision, это отдельная находка, не повод менять план до замера.

### Отклонённые варианты

**Сразу клонировать `Fluid2D_MacCormackDye.asset` (Harris + VC + MacCormack) и поднять там `dye`.** Три механизма в одном тикете — если look не взят, непонятно, какой из трёх виноват. Методология F2 (F2.1 отдельно от F2.2) сохраняется.

**Численный гейт прямо на живом ассете (128²/512², `Size=32`).** Дороже (512² GPU readback в EditMode) и не даёт регрессии к существующим числам ADR-028 §7 (другая геометрия `Size`/`dt`/`h`). Синтетический оракул на `Size=64` с той же геометрией, что F1.7/F2.2, — переиспользует уже провалидированные числа как контрольную строку.

**Поднять и `velocity`, и `dye` вместе («выше всё разрешение»).** Не изолирует переменную; дороже на GPU без ясного дополнительного сигнала (VC/Jacobi качество зависит от `velocity`-разрешения по-другому — Jacobi-дыра [ADR-019](ADR-019-Fluid2D-Solver.md) масштабируется с `k`, это отдельный вопрос).

**Общее правило «у Read-роли не сравнивать Resolution» в `ValidateMatchingFieldGeometry`.** Отклонено при разборе ТЗ (2026-09-20): `JacobiPhiPass`/`SubtractPhiGradientPass` тоже читают вторую роль как Read (`fluidD`/`fluidPhi`), общее правило тихо снимает guard и у них в изолированных unit-тестах — они всё ещё защищены `SquareTexelValidator` на уровне `SimulationWorld.Build`, но расходятся в том, что ловит `Initialize()` сам по себе. Точечный opt-in-флаг только на `AdvectScalarPass.velocity` — см. §1a.

**Патч валидатора без основания.** Отклонено в первой редакции («ограничения не существует») — оказалось неверно для pass-уровня; см. §1a, где патч теперь обоснован конкретным guard и заявленным существующим тестом-регрессией.

### Последствия

- (+) Гипотеза подтверждена числом (пик 0.979 vs 0.744) и visual: тот же гриб, dye 512² заметно острее; ножка жеста выживает. Без нового кернела, без второго Poisson.
- (+) Числа переиспользуют существующий оракул ADR-028 §7 как контрольную строку — не с нуля.
- (−) `dye` 512² — заметно больше памяти/bandwidth на кадр (R16, 512² × 2 (ping-pong) против 128² × 2); GPU ms — запись, не порог ([Techdebt 8k](../last/Techdebt.md)), но стоит смотреть на цифру перед тем, как поднимать выше 512².
- (−) Не решает Techdebt 5 по сути (диссипация **velocity** остаётся) — F3.0 снижает эффект diffusion на **dye**-стороне, F3.3 (MacCormack velocity) — на **velocity**-стороне; это два независимых лечения одного симптома, не взаимозаменяемые.

**Вне скоупа документа:** реализация F3.1–F3.3 (перечислены в [`plan-stable-fluid.md`](../plan-stable-fluid.md) §4 как будущие тикеты, без собственного ADR до открытия); подбор итогового `radiusUV`/жеста для visual (оператор, не этот файл).
