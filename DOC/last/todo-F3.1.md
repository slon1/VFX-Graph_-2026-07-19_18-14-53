## ТЗ для программиста — F3.1 (продолжительный впрыск dye вдоль жеста)

**Открыто 2026-10-20, ред. 2 (учтены решения владельца).** Статус в плане: **Не начат → В работе** после Шага 0. Production остаётся `Fluid2D.asset`. ADR: [ADR-050](../ADR/ADR-050-Continuous-Dye-Injection.md) — прочитать **до кода**, целиком. Протокол visual: `play-F3.1-touch.md` (после кода, не в этом ТЗ).

Роль: проверить гипотезу F3.1 — если dye подаётся **штрихом вдоль жеста** (кисть как у velocity), а сид сжат до размера кисти, то на dye 512² нить становится основным элементом картинки. Один новый пасс, один кернел, один пресет. Всё остальное не трогать.

Опора: F3.0 ([ADR-029](../ADR/ADR-029-Cross-Resolution-Dye.md), [`todo-F3.0.md`](todo-F3.0.md), [`play-F3.0-touch.md`](play-F3.0-touch.md)) закрыта и не переоткрывается. `Fluid2D_HighResDye.asset` — база для клона.

Решения владельца (не пересматривать): закон — **аддитивный** (с мягким потолком, ADR §2); красить **всегда, пока есть touch**; сид **остаётся, тонкий** (`RadiusUV=0.04`); ширина штриха = `touch.radius × radiusScale`, `radiusScale=1`; затухание — через **существующий** `AdvectScalarPass.DissipationRate`, нового кода нет (ADR §3a).

Зафиксировано — не пересматривать:

1. **Не трогать:** `Fluid2D.asset`, `Fluid2D_HarrisOrder.asset`, `Fluid2D_Vorticity.asset`, `Fluid2D_MacCormackDye.asset`, `Fluid2D_HighResDye.asset`. Не запускать их Create. Новый путь — `Assets/Effects/Fluid2D_DyeInject.asset`.
2. **Кернелы и классы F1/F2/F3.0 не менять:** `DivergenceFieldPass`, `ZeroMeanScalarPass`, `JacobiPhiPass`, `SubtractPhiGradientPass`, `SolidWallVelocityPass`, `AdvectVelocityFieldPass`, `AdvectScalarPass` (в т.ч. не добавлять атрибуты), `SeedScalarDiskPass`, `TouchInjectVelocityFieldPass`, `TouchInjectGrayScottPass`, `VorticityConfinementPass`, `CopyScalarPass`, `LimitedMacCormackCombinePass`. `SquareTexelValidator`, `SimulationWorld.ValidatePassFieldCoordinates`, `SimPass.ValidateMatchingFieldGeometry`, `InputRouter`, `ScriptedTouchStroke` — без правок.
3. **Состав пассов — ровно как `Fluid2D_HighResDye`, с тремя отличиями:** (а) `SeedScalarDiskPass.RadiusUV = 0.04` (было `0.16`); (б) сразу после `SeedScalarDiskPass` добавлен `TouchInjectScalarFieldPass`; (в) остальное без изменений. Итого **11** пассов: `TouchInjectVelocity, SeedScalarDisk, TouchInjectScalar, Divergence, ZeroMean, Jacobi×40, Subtract, SolidWall, AdvectVelocity, SolidWall, AdvectScalar`. Без VC, без MacCormack.
4. **Поля как в `Fluid2D_HighResDye`:** `velocity` 128² `R16G16_SFloat`, `fluidD` 128² `R32_SFloat`, `fluidPhi` 128² `R32_SFloat`, `dye` **512²** `R16_SFloat`; у всех `Size=(32,32)`; `clearValue = Color.clear`. Не менять.
5. **Закон впрыска — по ADR-050 §2, не пересматривать.** Для каждого текселя: `s = Σ_touch TouchFalloff(dist_to_segment, touch.radius·radiusScale)`; `dye ← dye + max(maxValue − dye, 0) · (1 − exp(−injectRate·dt·s))`. Расстояние — до **отрезка** `[pos − delta, pos]`. Touch суммируются. `touch.strength` **не используется**. `max`-режим, gate по движению, неравномерное затухание — не делать.
6. **Кернел `TouchInjectScalar` — в существующий `Assets/Shaders/GPU/Passes/TouchGrayScottPasses.compute`**, новый `.compute` не создавать, файл не переименовывать; в шапку — одна строка комментария про новый kernel.
7. **Контракт пасса:** `TouchInjectScalarFieldPass : FieldKernelPass`, `[Serializable]`; `DisplayName "Touch Inject Scalar Field"`; `Category = PassCategory.Emit`; `KernelName "TouchInjectScalar"`; `FieldReads` пусты; `FieldWrites` — одно поле `fieldName` (дефолт `"dye"`), `WriteInPlace`, `FieldSemantic.Scalar`, 1 канал, массив кэшируется через `FieldRequestSets.Single` (ноль аллокаций). Параметры (с публичными свойствами): `injectRate` (`Min(0)`, дефолт `60`), `maxValue` (`Min(0)`, дефолт `1`), `radiusScale` (`Min(0.01)`, дефолт `1`). `RequiresSquareTexel` не переопределять.
8. **`K = injectRate · deltaTime` считать на CPU** в `SetParams` (как `Dissipation` в `AdvectScalarPass`), в шейдер передавать `ScalarInjectK` и `ScalarInjectMax`. Имена uniform — без коллизий с существующими в файле (`Touches`, `TouchCount`, `FieldWriteA/B` заняты). Слот — single-field `RWTexture2D<float> FieldWrite` ([ADR-003](../ADR/ADR-003-Generic-Field-Slot-Naming.md)).
9. **Затухание: нового кода нет.** В пресете `AdvectScalarPass.DissipationRate = 0` (чистое сравнение с F3.0). Оператор крутит параметр живьём в Play. Таблицу `λ → t½` (ADR §3a) перенести в заготовку `play-F3.1-touch.md` и в `pass-catalog.md` как справку.
10. **GPU ms — запись, не порог** ([Techdebt 8k](Techdebt.md)).
11. **EditMode зелёный ≠ F3.1 закрыт.** Нужен visual оператора. Assign в сцену **не делать**.
12. **Красный гейт → стоп.** Числа/сообщение в чат; не крутить допуски и не менять закон впрыска молча.

Референсы: `TouchInjectGrayScottPass` + `TouchGrayScottPasses.compute` (скелет кернела, `worldPos`, `TouchFalloff`); `SeedScalarDiskPass` (single-field WriteInPlace, кэш `Single`); `AdvectScalarPass.SetParams` (`Mathf.Exp(−rate·dt)` на CPU); `TouchInjectVelocityFieldPass` (`BindBuffer(Touches)`, `SetInt(TouchCount)`); `TouchInjectGrayScottPassTests` (contract-тест через рефлексию на `KernelName`); `M3DDemoTools.CreateFluid2DHighResDyeExperiment` (клонировать **его**); `Fluid2DHighResDyePresetTests`, `Fluid2DHighResDyeWorldSmokeTests`, `Fluid2DHighResDyeSmokeTests`, `Fluid2DHighResDyeCrossResTests`; `FieldTestHarness` (ADR-014).

Имена: класс `TouchInjectScalarFieldPass`, kernel `TouchInjectScalar`, ассет `Fluid2D_DyeInject.asset`. Меню `Tools/M3D/Create Fluid2D DyeInject Experiment` / `Tools/M3D/Assign Fluid2D DyeInject Experiment To Scene`. Тесты: `TouchInjectScalarPassTests.cs` (Шаг 1, не GPU), `TouchInjectScalarKernelTests.cs` (Шаг 2, `[Category("GPU")]`), `Fluid2DDyeInjectPresetTests.cs` и `Fluid2DDyeInjectWorldSmokeTests.cs` (Шаг 3), `Fluid2DDyeInjectLiveTests.cs` (Шаг 4, `[Category("GPU")]`).

Код писать. Документацию из Шага 5 — тоже.

---

### Шаг 0 — закрыт (ответы Grok, 2026-10-20)

Проверено кодом, допущения ADR-050 подтверждены:

1. `SimContext.FindKernel` перебирает всю `passLibrary`, берёт первый шейдер с `HasKernel`. Новый `#pragma kernel TouchInjectScalar` в `TouchGrayScottPasses.compute` подхватится без правок списков.
2. `SimulationWorld` пассы не сортирует: цикл идёт по `effect.Passes` в порядке ассета.
3. `FieldTestHarness.RunPass(pass, deltaTime)` принимает произвольный `dt`, но `TouchBuffer` не принимает (в `SimContext` передаётся `null`, сеттера нет). Решение — Шаг 1b.
4. `Test1.unity` в репозитории нет. В `Physarum.unity` и `Boids_Fluid2d_test.unity` `touchRadius = 1`, `touchStrength = 10`; кернел `strength` не читает. Оценка ADR §2b при радиусе 1 остаётся в силе.
5. `FieldDescriptor.CreateDefault` → `clearValue = Color.clear`; `Rebuild` → `FieldSet.Allocate` → `ClearBoth`. `dye` стартует нулём.

---

### Шаг 1 — пасс и кернел, contract-тест

Файлы: `Assets/Scripts/Passes/FieldPasses.cs` (класс рядом с `SeedScalarDiskPass`/`TouchInjectGrayScottPass`), `Assets/Shaders/GPU/Passes/TouchGrayScottPasses.compute`.

Кернел (суть, форму допустимо адаптировать к соседнему коду):

```hlsl
#pragma kernel TouchInjectScalar

RWTexture2D<float> FieldWrite;     // single-field slot
float ScalarInjectK;               // injectRate * dt, считается на CPU
float ScalarInjectMax;             // maxValue
float ScalarInjectRadiusScale;

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void TouchInjectScalar(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y) return;

    float2 uv = (float2(id.xy) + 0.5) * FieldTexelSize;
    float3 p = FieldOrigin
        + FieldAxisU * ((uv.x - 0.5) * FieldSize.x)
        + FieldAxisV * ((uv.y - 0.5) * FieldSize.y);

    float s = 0.0;
    for (uint t = 0; t < TouchCount; t++)
    {
        TouchForce touch = Touches[t];
        float3 a  = touch.pos - touch.delta;
        float3 ab = touch.delta;
        float  u  = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-8));
        float  d  = length(p - (a + ab * u));
        s += TouchFalloff(d, touch.radius * ScalarInjectRadiusScale);
    }

    float cur = FieldWrite[id.xy];
    FieldWrite[id.xy] = cur + max(ScalarInjectMax - cur, 0.0) * (1.0 - exp(-ScalarInjectK * s));
}
```

`SetParams`: `BindBuffer(Touches)`, `SetInt(TouchCount)`, `SetFloat(ScalarInjectK, injectRate * deltaTime)`, `SetFloat(ScalarInjectMax, maxValue)`, `SetFloat(ScalarInjectRadiusScale, radiusScale)`.

`TouchInjectScalarPassTests.cs` (не GPU): `DisplayName`, `Category == Emit`, `KernelName == "TouchInjectScalar"` (рефлексия), `FieldReads.Count == 0`, `FieldWrites.Count == 1`, у write: имя по умолчанию `"dye"`, `Access == WriteInPlace`, `RequiredSemantic == Scalar`, `Channels == 1`; повторное чтение `FieldWrites` возвращает **тот же** массив (кэш); дефолты `injectRate == 60`, `maxValue == 1`, `radiusScale == 1`; смена `fieldName` меняет `FieldWrites[0].FieldName`.

Красный новый или любой существующий тест → стоп.

---

### Шаг 1b — минимальная правка харнеса (одобрена архитектором)

Нужна только чтобы Шаги 2 и 4 могли подать touch-пасс. Рамки:

- `SimContext.TouchBuffer` — **internal**-сеттер (не public); поведение рантайма не меняется. `InternalsVisibleTo` — как уже настроено в `AssemblyInfo.cs`; если для тестовой сборки его нет — стоп и в чат, не расширять доступ молча.
- На `FieldTestHarness` — метод `SetTouches(TouchForce[] touches)`: создаёт/пересоздаёт `GraphicsBuffer` под массив, выставляет `TouchBuffer` и `TouchCount`, освобождает буфер в `Dispose`.
- **Буфер создаётся и при `TouchCount = 0`** (минимальный размер, один элемент), иначе `BindBuffer(null)` падает раньше кернела (нужно для G6).
- Больше ничего в харнесе и в `SimContext` не менять. Все существующие тесты — зелёные без правок. Заодно **не** переделывать `Fluid2DHighResDyeSmokeTests` (там touch-пасс пропущен намеренно; это другой тикет).

Красный любой существующий тест после 1b → стоп.

---

### Шаг 2 — гейты кернела (синтетика, GPU)

`Assets/Tests/Editor/TouchInjectScalarKernelTests.cs`, `[Category("GPU")]`. Поле `R32_SFloat`, `Size=32`, плоскость XZ. Разрешение: 64² для G1–G7; 128² и 512² для G8; **256² для G9** (при 64² тексель 0.5 равен радиусу кисти, гейт «последний ненулевой тексель» некорректен: ошибка постановки G9 в ред. 2, не пасса). touch подаётся через `SetTouches` (Шаг 1b), `dt` задаётся тестом (взять `dt = 0.05`, `injectRate = 60`, `maxValue = 1`, `radius = 1` — гейты читаются, пока `K·s` порядка единицы).

Профиль в тестах считать формулой: `f(d) = (1 − d/R)²` при `d < R`, иначе `0`; `v(d) = maxValue · (1 − exp(−injectRate·dt·f(d)))`, `R = radius·radiusScale` (на ту же формулу ссылается ADR §4). Допуск R32 — `1e-4`, где не оговорено иное.

| # | Сценарий | Assert |
| --- | --- | --- |
| G1 | Один touch, `delta=0`, поле 0, центр в **центре текселя** | значения текселей на нескольких расстояниях `d` равны `v(d)`; центр — `v(0)`; при `d ≥ R` строго `0` |
| G2 | Предзаполнение: левая половина `0.9`, правая `0.2`, `maxValue = 0.5`; touch над границей | везде `out ≥ in`; левая половина (`in ≥ maxValue`) **бит-в-бит** без изменений; правая `out ≤ 0.5` |
| G3 | Один touch, длина `delta` равна `3·radius`, вдоль U; начало, середина и конец отрезка совпадают с центрами текселей | значения в начале, середине и конце равны `v(0)`; тексель на `0.5·R` по перпендикуляру от середины равен `v(0.5R)`. В сообщение напечатать, что точечный штамп в середине дал бы `0` |
| G4 | Независимость от FPS, `delta = 0` | один вызов с `dt` ≡ четыре вызова с `dt/4` подряд, поэлементно `1e-5` |
| G5 | `TouchCount = 2` | (а) два **одинаковых** touch ≡ один touch с `injectRate × 2` (`1e-5`); (б) два touch на расстоянии больше `2R` друг от друга: поле поэлементно равно сумме двух отдельных прогонов (носители не пересекаются) |
| G6 | `TouchCount = 0` на предзаполненном поле | **бит-в-бит** без изменений |
| G7 | `injectRate = 0` на предзаполненном поле | **бит-в-бит** без изменений |
| G8 | Один и тот же мировой штрих на `dye` 128² и 512² (тот же `Size`) | `Σ dye·(Size/Resolution)²` расходится **не более 3 %** |
| G9 | `radiusScale` | сетка **256²** (тексель 0.125), touch в центре текселя, `delta=0`; вдоль оси U в текселях `d = k·0.125`, `k = 0…9`, при `radiusScale = 1` и при `0.5`: значение равно `v(d)` с `R = radius·radiusScale` (1e-4); тексель `d = 0.75` равен `0` при `0.5` и `> 0` при `1` |

Сообщения ассертов — числа. Красный любого G → стоп, таблицу в чат. Gate на коллинеарное разбиение движущегося штриха **не** ставить: при аддитиве капсулы накладываются, равенства нет (ADR §2).

---

### Шаг 3 — пресет `Fluid2D_DyeInject.asset`

`Assets/Scripts/Editor/M3DDemoTools.cs`, рядом с `CreateFluid2DHighResDyeExperiment`.

```csharp
private const string Fluid2DDyeInjectPath = EffectsFolder + "/Fluid2D_DyeInject.asset";

[MenuItem("Tools/M3D/Create Fluid2D DyeInject Experiment")]
public static void CreateFluid2DDyeInjectExperiment()
{
    // Клон CreateFluid2DHighResDyeExperiment: те же 4 FieldDescriptor, тот же цикл
    // resolution/format/size, те же debug quads. Отличия — ровно три (см. ТЗ п.3):
    //   1. SeedScalarDiskPass.RadiusUV = 0.04 (в фабрике сразу 0.04; блок "поднять на 0.16" НЕ переносить);
    //   2. после SeedScalarDiskPass идёт
    //        new TouchInjectScalarFieldPass { FieldName = "dye", InjectRate = 60f, MaxValue = 1f, RadiusScale = 1f }
    //   3. остальное, включая DissipationRate = 0 у AdvectScalarPass, Jacobi Iterations = 40 — как в HighResDye.
}

[MenuItem("Tools/M3D/Assign Fluid2D DyeInject Experiment To Scene")]
public static void AssignFluid2DDyeInjectToScene()
{
    // Клон AssignFluid2DHighResDyeToScene: Effect + GroundXZ InputRouter + EnsurePassLibrary,
    // visualEffect не трогать. Ошибка: "run Tools/M3D/Create Fluid2D DyeInject Experiment".
    // ОТЛИЧИЕ от клона: SimulationWorld брать из АКТИВНОЙ сцены (SceneManager.GetActiveScene(),
    // объекты с gameObject.scene == активная), не через FindAnyObjectByType: при двух открытых
    // сценах тот может записать эффект в чужую (в т.ч. грязную Physarum). Если в активной
    // сцене нет SimulationWorld — ошибка с понятным сообщением, ничего не менять.
}
```

**`Fluid2DDyeInjectPresetTests.cs`** (не GPU, стиль `Fluid2DHighResDyePresetTests`): 4 поля; `dye.Resolution == (512,512)`, остальные `(128,128)`; все `Size == (32,32)`; форматы как указано; **11 пассов** строго в порядке п.3 ТЗ; ровно один `SeedScalarDiskPass` (`FieldName == "dye"`, `RadiusUV == 0.04`, `Value == 1`); ровно один `TouchInjectScalarFieldPass` (`FieldName == "dye"`, `InjectRate == 60`, `MaxValue == 1`, `RadiusScale == 1`); он стоит **после** `SeedScalarDisk` и **до** `Divergence`; нет `VorticityConfinementPass`, `CopyScalarPass`, `LimitedMacCormackCombinePass`; **две** `SolidWallVelocityPass`; `AdvectScalarPass.DissipationRate == 0`; `JacobiPhiPass.Iterations == 40`. Ошибка — «run Tools/M3D/Create Fluid2D DyeInject Experiment».

**`Fluid2DDyeInjectWorldSmokeTests.cs`** (стиль `Fluid2DHighResDyeWorldSmokeTests`): неактивный `GameObject` с `SimulationWorld`, `effect` — **загруженный с диска** ассет, `passLibrary` — дублированный список путей строками **как в `Fluid2DHighResDyeWorldSmokeTests`** (список не расширять); `Rebuild()` не бросает, `world.enabled == true`. Без кадров и readback. Красный из-за ненайденного kernel → стоп (вывод Шага 0 п.1 тогда неверен), новый `.compute` не создавать.

---

### Шаг 4 — живая цепочка с касаниями (GPU)

`Fluid2DDyeInjectLiveTests.cs`, `[Category("GPU")]`. Пассы — новыми экземплярами (как `Fluid2DHighResDyeSmokeTests`) в порядке пресета. Геометрия живая: 128²/512², `Size=32`, Jacobi×40. Touch подаётся через `SetTouches` (Шаг 1b).

Сценарий: эмуляция `ScriptedTouchStroke` F2.3 по кадрам (`StartUV (0.5,0.25)`, `EndUV (0.5,0.75)`, `Duration 0.5`, `dt = 1/60`), затем кадры без touch до ~3 с. `touch.radius` и `touch.strength` — как в сцене (1 и 10).

- `touch.pos` — по `ScriptedTouchStroke.Evaluate`.
- **`touch.delta = pos − prev`** (позиция этого кадра минус позиция прошлого), на **первом кадре `delta = 0`** — ровно как `ScriptedTouchStroke.Sample`. Нельзя брать `delta` из `Evaluate`: он возвращает смещение от начала всего жеста до текущей точки.
- Кадр касания идёт, пока `elapsed < Duration`; после — `TouchCount = 0`.

Два прогона на каждой геометрии:

- **Прогон S (как пресет):** сид `RadiusUV = 0.04`, `Value = 1`. Assert: нет NaN/Inf на `dye` и `velocity`; `min(dye) ≥ 0`; `max(dye) ≤ maxValue + 1e-3` на **каждом** кадре.
- **Прогон N (`SeedScalarDisk.Value = 0`):** те же проверки безопасности **плюс** сразу после последнего кадра штриха `dye > 0.05` в окне `±radius` у каждой из пяти точек пути `UV.y ∈ {0.25, 0.35, 0.5, 0.65, 0.75}`. Только в этом прогоне: точка `0.5` лежит в центре сида и в прогоне S порог выполнился бы без штриха; адвекция сида может зацепить и соседние точки.

**Логировать, не гейтить:**
- пик `dye` на оси жеста в прогоне N (точки `UV.y = 0.35, 0.65`) сразу после штриха — калибровка оценки ADR §2b (расчёт: ≈ `0.71·maxValue` при `injectRate=60`, `v≈32` ед./с, `radius=1`); если расходится больше чем вдвое — написать в чат;
- FWHM нити поперёк жеста (`y = 0.5`, от локального пика), в мировых единицах, после 0.5 с и после 3 с, прогон N, для `dye` 512² и варианта `dye` 128² (то же `Size`), одной таблицей;
- `elapsedMs/N` всей цепочки (прогон S, живая геометрия, warmup вне таймера, `N ≥ 8`, без `Read*` в цикле, `Assert.Pass(report)`), строка рядом с F3.0 `0.184`.

FWHM-сравнение между 128² и 512² — **не assert**.

---

### Шаг 5 — доки после зелёного EditMode

- [`ADR-050`](../ADR/ADR-050-Continuous-Dye-Injection.md): статус остаётся «Предложено» до visual; блок «EditMode замерено» (G1–G9, живая цепочка, пик на оси, FWHM-таблица, ms).
- [`plan-stable-fluid.md`](../plan-stable-fluid.md): F3.1 — «В работе (EditMode готов)», ссылки на ADR-050 и `todo-F3.1.md`. «Готово» ставит владелец после visual.
- [`status.md`](../status.md): короткая запись с датой.
- [`pass-catalog.md`](../pass-catalog.md): **новый пасс** `TouchInjectScalarFieldPass` (параметры и закон) + справка «`DissipationRate` → `t½`» (таблица ADR §3a). [`capabilities.md`](../capabilities.md): новый ассет одной строкой.
- [`Techdebt.md`](Techdebt.md): (а) 8k — ms рядом с F2.0/F2.1/F3.0; (б) строка: kernel `TouchInjectScalar` в `TouchGrayScottPasses.compute`, имя вводит в заблуждение; (в) 8n — одна фраза: F3.1 заменяет жирный сид штрихом и тонким сидом, см. ADR-050.
- `play-F3.1-touch.md` — только **заготовка протокола** по образцу `play-F3.0-touch.md`: A / B / B0 / B-fade (`DissipationRate` 0.15 и 0.5) / B-rate (`injectRate` 20 и 200, опц.); **без** «Отчёта оператора» и выводов.

---

### Шаг 6 — visual (оператор, не Grok)

После зелёного EditMode и документов — стоп, сообщить архитектору/оператору. Протокол — ADR-050 §6. `Assign … To Scene` — действие оператора.

---

### Вне скоупа

Цветной dye; неравномерное затухание, новый пасс затухания, правка `AdvectScalarPass`; `max`-режим; gate по движению; правка `touchRadius`/`touchStrength`/`InputRouter`/`ScriptedTouchStroke`; VC; MacCormack (dye и velocity); `dyeMacScratch`; F3.2; F3.3; второй Poisson; подъём Jacobi; подъём `velocity`; `dye` на `R32_SFloat`; `deltaTime` clamp; правка `AdvectScalar`/`AdvectVelocity`/`SeedScalarDisk`/`TouchInjectVelocity`/`TouchInjectGrayScott`, валидаторов, `ValidateMatchingFieldGeometry`; новый `.compute`; переименование `TouchGrayScottPasses.compute`; мобильная адаптация; M2d/рендер частиц; существующие `Fluid2D*.asset` — не трогать, не Create.
