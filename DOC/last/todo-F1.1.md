## ТЗ для Grok — F1.1 (DivergenceFieldPass + RequiresSquareTexel)

### Контекст

Прочитать [ADR-017](../ADR/ADR-017-Divergence-Pass-And-Square-Texel-Contract.md) и разделы §2/§2.1/§2.2/§2.3 [ADR-016](../ADR/ADR-016-Units-By-Pass-Family.md), на которые он ссылается. Первый тикет фазы F1: первый реальный fluid-кернел и механизм, который до сих пор существовал только как текст в ADR-016.

Референсы по месту:

- `Assets/Scripts/Runtime/SimPass.cs` (`RepeatCount`, virtual property около строки объявления `LastExecuteDispatched`; сверить точную строку по факту — не полагаться на номер из этого ТЗ) — образец того же рода свойства.
- `Assets/Scripts/Runtime/RepeatCountValidator.cs` — образец статического валидатора, принимающего список пассов, не `SimulationWorld`. Строить `SquareTexelValidator` так же. **При копировании паттерна не переносить комментарий `"Revisit when F0.4 initializes disabled passes"`** — F0.4 уже установил истинную причину четырёх красных тестов (guard в `VfxParticleBinder`), инициализация выключенных пассов не рассматривается и не планируется; этот комментарий в `RepeatCountValidator.cs` устарел, заодно исправить его на месте (одна строка, не отдельный тикет).
- `Assets/Shaders/GPU/Passes/DiffusePasses.compute` (`LoadClamped`, `#define FIELD_THREADS 8`) — образец clamp-чтения соседей через `Load` и общего thread-group размера для field-кернелов. Divergence использует ту же форму для `float2` (см. шаг 3).
- `Assets/Scripts/Passes/FieldPasses.cs` — образец C#-обёртки `FieldKernelPass` с `FieldReads`/`FieldWrites`.
- `Assets/Tests/Editor/FieldTestHarness.cs` — харнес ADR-014, `RunPass`, `AssertApproximately`, `RelativeTolerance`.
- `Assets/Scripts/Runtime/SimulationWorld.cs` (`ValidatePassFieldCoordinates`, комментарий "Read fields may differ in resolution") — общее правило фреймворка, которое `SquareTexelValidator` **переопределяет** для fluid-пассов (см. шаг 2).

Include-путь в проекте — абсолютный от корня, как в `DiffusePasses.compute`: `#include "Assets/Shaders/GPU/Includes/FieldSampling.hlsl"`, не относительный `../Includes/...`.

---

### Шаг 1 — `SimPass.RequiresSquareTexel`

```csharp
/// <summary>Fluid operators assume hx == hy (ADR-016 §2.1). Validated at Build.</summary>
public virtual bool RequiresSquareTexel => false;
```

`virtual` в базовом `SimPass`, без `[SerializeField]` — по той же причине, что `RepeatCount`: свойство нужно единицам пассов, не всем.

### Шаг 2 — `SquareTexelValidator`

Новый файл `Assets/Scripts/Runtime/SquareTexelValidator.cs`. Сигнатура по образцу `RepeatCountValidator`:

```csharp
internal static class SquareTexelValidator
{
    internal static void Validate(IReadOnlyList<SimPass> passes, FieldSet fields);
}
```

Для каждого `pass` где `pass != null && pass.Enabled && pass.RequiresSquareTexel` выполняются **два** независимых утверждения над объединённым множеством дескрипторов `FieldReads ∪ FieldWrites` (через `fields.Get(name).Descriptor`):

**(a) Квадратный тексель на каждом поле.** `hx = descriptor.Size.x / descriptor.Resolution.x`, `hy = descriptor.Size.y / descriptor.Resolution.y`, относительный допуск `Mathf.Abs(hx - hy) / Mathf.Max(hx, hy) < 1e-4`. При нарушении — `InvalidOperationException` с именем пасса, именем поля, значениями `hx`/`hy`, строкой `"ADR-016 §2.1"`.

**(b) Совпадающее разрешение между всеми полями пасса.** Взять `Resolution` первого дескриптора как эталон, сравнить с остальными (`Vector2Int` равенство, точное, не допуск). При несовпадении — `InvalidOperationException` с именами двух полей, их `Resolution`, строкой `"ADR-017 §1"`.

Проверка (b) обязательна **отдельно от** (a): `ValidatePassFieldCoordinates` в `SimulationWorld.cs` намеренно разрешает read-полям отличаться в разрешении от write-поля — это механизм под будущий cross-resolution G2P через `SampleLevel` (F0.6, не входит в этот тикет). Fluid-кернелы индексируют соседей через `Load(id.xy)`, где `id` пробегает разрешение **write**-поля; если read-поле имеет другое разрешение, `Load` читает по чужим координатам молча, без единого предупреждения на Build. Проверить это явным тестом (шаг 5.2b) — два квадратных поля с разным `Resolution` обязаны провалить Build, хотя оба по отдельности проходят (a).

Если `Resolution.x == 0` или `Resolution.y == 0` у какого-либо дескриптора — сообщение исключения обязано содержать это явно (например `"has zero Resolution"`), не давать делению уйти в `NaN`/`Infinity` с непонятным сообщением.

Выключенные пассы (`!pass.Enabled`) **пропускаются** в обоих утверждениях — тем же условием, что везде в `SimulationWorld.Build`/`Update`. Это согласовано и не подлежит пересмотру в этом тикете.

Вызов — в `SimulationWorld.Build`, рядом с существующим вызовом `RepeatCountValidator` (или аналогичной точкой валидации пассов). Путь ошибки — тот же, что у прочих валидаторов Build (`LogError` + `Teardown` + `enabled = false`), не проброс исключения наружу из `Build`.

### Шаг 3 — `Assets/Shaders/GPU/Passes/FluidPasses.compute` (новый файл)

**Не в `FieldPasses.compute`.** Тот файл целиком принадлежит texel/UV-семействам ADR-016; fluid-кернелы (world-семейство) собираются в отдельном файле, чтобы нарушение соглашения было видно по расположению кода.

```hlsl
#pragma kernel Divergence

#include "Assets/Shaders/GPU/Includes/FieldSampling.hlsl"

#define FIELD_THREADS 8

Texture2D<float2> FieldReadA;
RWTexture2D<float> FieldWriteB;

float2 LoadClampedVelocity(int2 q)
{
    int2 maxP = FieldResolution - 1;
    q = clamp(q, int2(0, 0), maxP);
    return FieldReadA.Load(int3(q, 0));
}

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void Divergence(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y)
    {
        return;
    }

    int2 p = int2(id.xy);
    float2 uE = LoadClampedVelocity(p + int2( 1, 0));
    float2 uW = LoadClampedVelocity(p + int2(-1, 0));
    float2 uN = LoadClampedVelocity(p + int2( 0, 1));
    float2 uS = LoadClampedVelocity(p + int2( 0,-1));

    FieldWriteB[p] = uE.x - uW.x + uN.y - uS.y;
}
```

`LoadClampedVelocity` — форма `LoadClamped` из `DiffusePasses.compute`, перенесённая на `float2`, а не переизобретённая. Соседи — **`Load` с явным clamp индекса**, не `SampleLevel` и не незащищённый `Load`. Незащищённый `Load` за границей вернул бы 0 и внёс ложную дивергенцию на каждом кадре по всему периметру, не только в тесте — это сознательно другое решение, чем «ноль-паддинг», и граница в результате будет отличаться от континуального эталона (ожидаемо, см. шаг 5).

Слоты — **`FieldReadA` / `FieldWriteB`**, не legacy `FieldRead`/`FieldWrite`. Два разных имени (`velocity` Read, `fluidD` WriteInPlace) не могут делить Role A: `FieldKernelPass.AssignSlotIdsAndValidateRoles` (ADR-008) разрешает на одну роль только одно имя. Legacy-слоты остаются для одного имени (WritePingPong на себе). Образец — `AgentBoostFieldPass`. Побочный эффект A+B: `ValidateMatchingFieldGeometry` на Initialize сверяет ещё Origin/оси/Size. Jacobi / Subtract копируют эту схему.

Добавить в `M3DDemoTools.PassLibraryPaths` — это production-пасс, не test-only (в отличие от `HarnessProbes.compute` из ADR-014).

### Шаг 4 — `DivergenceFieldPass`

`Assets/Scripts/Passes/FluidPasses.cs` (новый файл, параллельно новому `.compute`):

```csharp
public sealed class DivergenceFieldPass : FieldKernelPass
{
    [SerializeField] private string velocityField = "velocity";
    [SerializeField] private string divergenceField = "fluidD";

    public override string DisplayName => "Divergence";
    public override PassCategory Category => PassCategory.Transport; // сверить с фактическими значениями enum
    protected override string KernelName => "Divergence";
    public override bool RequiresSquareTexel => true;

    public override IReadOnlyList<FieldRequest> FieldReads => /* velocityField, Read, Velocity, 2, Role A */;
    public override IReadOnlyList<FieldRequest> FieldWrites => /* divergenceField, WriteInPlace, Scalar, 1, Role B */;
}
```

`WriteInPlace`, **не** `WritePingPong` — пасс не читает `fluidD`, самозависимости нет, ping-pong не нужен. Имена полей — сериализованные строки по образцу существующих пассов (`FieldName` в `DecayFieldPass` и т.п.), не хардкод, чтобы пасс был переиспользуем на другом имени поля при необходимости.

**Дефолт `velocity`, не `flockVel` — сознательно другое поле, не опечатка.** `flockVel` принадлежит boids-пресету (texel-семейство ADR-016). Grid-only fluid v1 (решение зафиксировано ранее в этом чате: «сейчас grid-only, velocity + dye на весь экран, `Source Kind = None`») использует собственное поле `velocity` в world-семействе — смешивать их с `flockVel` нельзя даже по имени, иначе один пресет случайно утащит настройки другого через общее имя поля. Оставить `velocity` как дефолт сериализованного поля, не переименовывать и не объединять с `flockVel`.

### Шаг 5 — тесты

Файл `Assets/Tests/Editor/DivergenceFieldPassTests.cs`, `[Category("GPU")]` где используется харнес.

**5.1 — три случая на внутренних текселях, оракул на `R32G32_SFloat`.** Поле `velocity` для этого теста — **`R32G32_SFloat`, не боевой `R16G16_SFloat`**. Причина: на `Size = 32`, `64²` значения `u = (x, y)` доходят по модулю до ~16, ULP `half` в этом диапазоне — сотые доли, а накопление в разности четырёх соседей легко даёт ошибку `D` порядка `0.01–0.03` при ожидаемом сигнале `4h = 2` — это не допуск формата, это порча самого теста. `R32G32F` — оракул для этого теста, боевой `R16G16F` остаётся форматом полей в реальном пресете (F1.6) и не меняется.

Разрешение и `Size` — выбрать так, чтобы `h` был не равен 1 (например `Size = 32`, `64²`, `h = 0.5`) — по той же причине, что и `h²`-тест в `HarnessDiffuseTests`: при `h = 1` некоторые ошибки в коэффициентах становятся невидимыми. Поле `fluidD`: `Scalar`, `R32_SFloat`, то же разрешение/`Size`.

Заливка `velocity` — аналитической функцией мировой позиции (не UV, не индекса), координаты пересчитываются из индекса текселя через центр ячейки и геометрию поля (`Origin`/`AxisU`/`AxisV`/`Size`), не хардкодом смещений.

Три случая, **проверка только на внутренних текселях** (исключить рамку в 1 тексель по каждой стороне — граница считает clamp-продолжение, не континуальный эталон, это ожидаемо и не является дефектом):

| Случай | `u(world)` | `div u` | Ожидание `D = 2h·div` |
| --- | --- | --- | --- |
| Uniform | `(a, b)`, любые константы | `0` | `0` точно |
| Linear (expansion) | `(x, y)` в мировых координатах поля | `2` | `4h` точно во всех внутренних текселях |
| Rotational | `(-y, x)` | `0` | `0` точно |

«Точно» означает допуск по `RelativeTolerance(R32G32_SFloat)` — центральные разности дифференцируют линейную функцию без ошибки дискретизации, поэтому это не приближение, а точное равенство в пределах формата.

**5.2 — `SquareTexelValidator`, утверждение (a): неквадратный тексель.** `DivergenceFieldPass` на паре полей с `Size = (10, 20)`, **одинаковое** разрешение (то есть `hx ≠ hy` на каждом поле) → падает с `InvalidOperationException`, текст содержит имя пасса, `"hx="`/`"hy="` с посчитанными значениями и `"ADR-016 §2.1"`.

**5.2b — `SquareTexelValidator`, утверждение (b): разное разрешение при квадратном текселе на каждом поле.** `velocity` — `32²`, `Size = 10` (`h = 0.3125`, квадратный); `fluidD` — `64²`, `Size = 10` (`h = 0.15625`, тоже квадратный). Каждое поле по отдельности проходит проверку (a); пасс всё равно обязан упасть — на несовпадении `Resolution`, с текстом, содержащим имена обоих полей, строки `"(32, 32)"` и `"(64, 64)"` и `"ADR-017 §1"`. Это тест на дыру, которую этот тикет закрывает: без утверждения (b) `Load(id.xy)` тихо читает мусор по чужим координатам.

Тест валидатора — прямым вызовом `SquareTexelValidator.Validate(passes, fields)`, как `RepeatCountTests` вызывает `RepeatCountValidator.Validate` напрямую, а не через `Rebuild` + `LogAssert`: точнее в проверке текста сообщения и не требует поднимать `VisualEffect`/`SimulationWorld`.

**5.3 — `SquareTexelValidator` passes.** Тот же пасс на квадратном текселе при **неквадратном домене**: `Size = (16, 9)`, `Resolution = (256, 144)` (`h = 0.0625` по обеим осям, у обоих полей одинаковое `Resolution`) → валидация проходит оба утверждения. Это отличает «квадратный тексель» от «квадратное поле» — обе конфигурации должны быть покрыты, чтобы не закрепить более узкое требование, чем контракт.

**5.4 — выключенный пасс не валидируется.** `DivergenceFieldPass.Enabled = false` на неквадратном текселе (нарушает и (a), и (b)) → `Build` проходит. Согласовано с ADR-015 §4.

**5.5 — дескриптор `fluidD`.** Явная проверка формата в тесте: дескриптор поля `fluidD`, использованный в 5.1, — `GraphicsFormat.R32_SFloat`, не общий `R16_SFloat` соседних Scalar-полей. Отдельной фабрики `FieldDescriptor.CreateFluid(...)` в этом тикете **не создавать** — YAGNI до F1.6, где `Fluid2D`-пресет всё равно потребует собрать полноценный набор полей (`velocity`, `dye`, `fluidD`, `fluidPhi`) и тогда фабрика или явные дескрипторы в пресете появятся по факту реальной потребности, а не заранее. Assert формата в тесте — достаточный guard на этом шаге.

### Шаг 6 — документация

- `DOC/pass-catalog.md`: раздел `Divergence` — вход/выход, `RequiresSquareTexel`, явная строка «граница — clamp-продолжение поля, не истинная дивергенция; истинные граничные условия — F1.4».
- `DOC/status.md`, `DOC/capabilities.md`: F1.1 закрыт, `RequiresSquareTexel` реализован (снять пометку «— F1.1» в существующих упоминаниях).
- `Techdebt.md`: если там есть открытый пункт про precision `fluidD`/`fluidPhi` — закрыть здесь для `fluidD` (формат зафиксирован), оставить открытым для `fluidPhi` (придёт в F1.2/F1.3).

### Отчёт

1. Три числа из 5.1 (obtained/expected/Δ на internal-тексели) для каждого случая, формат оракула (`R32G32_SFloat`) явно указан.
2. Подтверждение 5.2 / 5.2b / 5.3: сообщения исключений из 5.2 и 5.2b целиком, факт прохождения 5.3.
3. Формат `fluidD` — подтверждение `R32_SFloat`.
4. Diff по production-коду: должен затронуть `SimPass.cs`, новый `SquareTexelValidator.cs`, вызов в `SimulationWorld.cs`, новый `FluidPasses.compute`, новый `FluidPasses.cs`, `M3DDemoTools.PassLibraryPaths`, и одну строку правки устаревшего комментария в `RepeatCountValidator.cs`. Ничего в `FieldPasses.compute`/`.cs`.
5. Состояние сьюта — ожидается ноль красных (78 + новые).

### Вне скоупа

- `JacobiPhiPass`, `SubtractPressureGradientPass`, `AdvectScalarPass`, `Fluid2D`-пресет — следующие тикеты F1 (F1.2+).
- Истинные граничные условия (обнуление нормальной компоненты после проекции) — F1.4.
- `fluidPhi`, любая математика Jacobi/Subtract — не этот тикет.
- Explicit viscosity — не открывается (ADR-016 §3).
- `Divergence` в Pass Library для существующих demo-пресетов — этот тикет добавляет пасс в библиотеку, но не подключает его ни к одному пресету; подключение — вместе с `Fluid2D` в F1.6.
