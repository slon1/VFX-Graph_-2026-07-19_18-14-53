## ТЗ для Grok — F1.2 (JacobiPhiPass)

### Контекст

Прочитать [ADR-018](../ADR/ADR-018-Jacobi-Phi-Pass.md) целиком, включая §5 (дрейф среднего) и §2 (компиляционный блокер) — это не факультативное чтение: §5 объясняет, почему тесты этого тикета обязаны использовать zero-mean источник, §2 объясняет, почему кернел пишется через `#ifdef KERNEL_*`, а не как в черновике.

**Класс называется `JacobiPhiPass`, не `JacobiPressurePass`.** ADR-016 §2.2 запрещает называть `Φ` давлением — то же правило распространяется на имя типа, не только на имя поля.

Референсы по месту:

- `Assets/Scripts/Passes/FluidPasses.cs`, `Assets/Shaders/GPU/Passes/FluidPasses.compute` — `DivergenceFieldPass`/`Divergence` из F1.1. Расширить оба файла, не создавать новые. **Важно:** реальная реализация `Divergence` — multi-role (`velocity` Read Role A / `FieldReadA`, `fluidD` WriteInPlace Role B / `FieldWriteB`), не legacy `FieldRead`/`FieldWrite`. Прочитать оба файла перед написанием кода, черновик ADR ошибался на этот счёт в первой версии.
- `Assets/Scripts/Runtime/SimPass.cs:596–685` (`AssignSlotIdsAndValidateRoles`) — гайд-матрица Role A/B. Прочитать перед написанием `FieldReads`/`FieldWrites`, чтобы не ошибиться с ролями.
- `Assets/Scripts/Runtime/SimPass.cs:686` (`ValidateMatchingFieldGeometry`) — уже проверяет совпадение геометрии между Role A и Role B на `Initialize`. Для этого пасса — основная защита от расхождения `fluidPhi`/`fluidD`; `SquareTexelValidator` (F1.1) — вторая, более ранняя по времени (Build, до Initialize) проверка квадратности каждого поля. Обе остаются, не убирать и не менять `SquareTexelValidator`.
- Существующий multi-role пасс Gray-Scott — образец объявления двух ролей. **Отличие:** там обе роли `WritePingPong`; здесь Role A = `WritePingPong`, Role B = `Read`, без записи. Не копировать `WritePingPong` на `fluidD`.
- `Assets/Tests/Editor/DivergenceFieldPassTests.cs` — образец структуры теста на харнесе для fluid-пасса этой фазы.
- `Assets/Tests/Editor/RepeatCountTests.cs` — там тест вида «ни один существующий пасс не переопределяет `RepeatCount`» (или аналогичный по смыслу). `JacobiPhiPass` — первый легитимный переопределяющий пасс; этот тест нужно сузить (см. шаг 4.6), не обойти и не удалить.

---

### Шаг 1 — поле `fluidPhi`

Дескриптор: `Scalar`, `Channels = 1`, `Format = GraphicsFormat.R32_SFloat` — тот же уровень, что `fluidD` (ADR-017), не общий `R16_SFloat`.

### Шаг 2 — `JacobiPhiPass`

```csharp
public sealed class JacobiPhiPass : FieldKernelPass
{
    [SerializeField] private string phiField = "fluidPhi";
    [SerializeField] private string divergenceField = "fluidD";
    [SerializeField, Range(1, 80)] private int iterations = 40;

    public int Iterations
    {
        get => iterations;
        set => iterations = value;
    }

    public override string DisplayName => "Jacobi";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "Jacobi";
    public override bool RequiresSquareTexel => true;
    public override int RepeatCount => iterations;

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        /* (phiField, WritePingPong, Scalar, 1, FieldSlotRole.A) */;
    public override IReadOnlyList<FieldRequest> FieldReads =>
        /* (divergenceField, Read, Scalar, 1, FieldSlotRole.B) */;
}
```

`public int Iterations` с get/set — по образцу `VelocityField`/`DivergenceField` у `DivergenceFieldPass`. Без публичного свойства тест 4.1 либо лезет в приватное поле через reflection, либо не может подтвердить, что `RepeatCount` реально возвращает установленное значение — только гонит через трёхаргументный `RunPass(..., n)`, что не проверяет сам пасс.

**Роли — обязательно так, не наоборот.** `fluidPhi` (записываемое, самоссылающееся через ping-pong) — Role A, потому что `PrimaryFieldName` при multi-role ищет Role A среди **Write**-запросов, и от него берётся `Resolution` для домена диспатча (`SimPass.cs:434–464`). `fluidD` (read-only, не пишется вообще) — Role B.

`RepeatCount => iterations` — первый пасс во всём фреймворке, реально переопределяющий `RepeatCount`. См. шаг 4.6 про сопутствующую правку `RepeatCountTests`.

### Шаг 3 — кернел `Jacobi` в `FluidPasses.compute`, через `#ifdef`

**Блокер, если проигнорировать:** файл уже объявляет `Texture2D<float2> FieldReadA` (для `Divergence`). Добавление `Texture2D<float> FieldReadA` для `Jacobi` без изоляции — повторное объявление одного имени с другим типом в одном файле, гарантированная ошибка компиляции HLSL (Unity компилирует весь текст файла на каждый kernel-entry, `#pragma kernel` сам по себе секции не разделяет).

Решение — токен после имени кернела в `#pragma kernel`, который Unity определяет как `#define` только при компиляции этого конкретного варианта:

```hlsl
#pragma kernel Divergence KERNEL_DIVERGENCE
#pragma kernel Jacobi KERNEL_JACOBI

#include "Assets/Shaders/GPU/Includes/FieldSampling.hlsl"

#define FIELD_THREADS 8

#ifdef KERNEL_DIVERGENCE
// ... существующий блок Divergence целиком: Texture2D<float2> FieldReadA,
// RWTexture2D<float> FieldWriteB, LoadClampedVelocity, Divergence() — без изменений,
// только обёрнуть в этот #ifdef.
#endif

#ifdef KERNEL_JACOBI
Texture2D<float> FieldReadA;
RWTexture2D<float> FieldWriteA;
Texture2D<float> FieldReadB;

float LoadClampedPhi(int2 q)
{
    int2 maxP = FieldResolution - 1;
    q = clamp(q, int2(0, 0), maxP);
    return FieldReadA.Load(int3(q, 0));
}

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void Jacobi(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y)
    {
        return;
    }

    int2 p = int2(id.xy);
    float n = LoadClampedPhi(p + int2( 0, 1));
    float s = LoadClampedPhi(p + int2( 0,-1));
    float e = LoadClampedPhi(p + int2( 1, 0));
    float w = LoadClampedPhi(p + int2(-1, 0));
    float d = FieldReadB.Load(int3(p, 0));

    FieldWriteA[p] = (n + s + e + w - d) * 0.25;
}
#endif
```

**Обязательно:** первая правка этого шага — добавить токены `KERNEL_DIVERGENCE`/`KERNEL_JACOBI` к **существующей** строке `#pragma kernel Divergence` и обернуть весь существующий блок `Divergence` (объявления + `LoadClampedVelocity` + сама функция) в `#ifdef KERNEL_DIVERGENCE`/`#endif`. Без этой правки старого блока конфликт типов не исчезнет — `#ifdef` работает только если **обе** стороны конфликта изолированы, а не только новая.

`#include` и `#define FIELD_THREADS` — общие, вне `#ifdef`-блоков, не дублировать.

`D` читается **без** clamp: `p` уже прошёл bounds-check выше, второй clamp избыточен и маскировал бы реальную ошибку индексации, если она появится.

Второй `.compute`-файл вместо `#ifdef` — не рассматривается: это смена решения ADR-017 (единое место для world-семейства), не альтернатива в рамках этого тикета.

### Шаг 4 — тесты

Файл `Assets/Tests/Editor/JacobiPhiPassTests.cs`, `[Category("GPU")]`.

**4.1 — сходимость невязки, главный DoD.** Поле `fluidD` — компактный **точно zero-mean** источник: `+A` в одной ячейке, `−A` в другой, всё остальное 0 (например `A = 1`, ячейки `(20, 32)` и `(44, 32)` на поле `64²`). Это гарантирует `Σ D = 0` точно, избегая дрейфа §5 ADR-018 без reduction-инфраструктуры — не подбирать источник иначе.

`fluidPhi` стартует из `ClearValue` (0, стандартная заливка `FieldSet.Allocate`). Сравнить два прогона с одинаковым seed на двух независимых экземплярах харнеса: `pass.Iterations = 1` → `RunPass(pass, dt)`, и `pass.Iterations = 40` → `RunPass(pass, dt)` (через `pass.RepeatCount`, не трёхаргументным `RunPass`, — иначе `Iterations`/`RepeatCount` не участвуют в проверке).

Невязка в каждой внутренней ячейке (исключить рамку в 1 тексель):

```
r[p] = ΦE + ΦW + ΦN + ΦS − 4·ΦC − D[p]
```

Вычислить `r` **CPU-стороной харнеса той же clamp-логикой, что кернел** (граничные ячейки иначе дадут ложную невязку от несовпадения формул, не от Jacobi) — либо тест-only кернелом `ProbeResidual` в `FluidPasses.compute`, если так дешевле (не в `M3DDemoTools.PassLibraryPaths`, по прецеденту `HarnessProbes.compute`, ADR-014).

Утверждение: `max|r|` после 40 итераций **меньше не менее чем на порядок**, чем после 1 итерации. **Если фактическое отношение окажется в диапазоне 3–8×** (низкочастотная мода Jacobi на 64² сходится медленно, спектральный радиус `ρ ≈ cos²(π/64) ≈ 0.998` — за 40 итераций это `0.998^40 ≈ 0.92`, то есть геометрическая сходимость этой конкретной моды слабая) — зафиксировать точное отношение в отчёте как находку, **не** ослаблять формулировку DoD и не подгонять источник/число итераций под желаемый результат задним числом.

Дополнительно, дёшево: подтвердить `ΣΦ ≈ 0` после 40 итераций (сумма по всем ячейкам в пределах допуска формата) — при точном zero-mean источнике и старте с нуля это должно выполняться, и это ловит опечатку знака в `(n+s+e+w−d)*0.25`, которую сама невязка может не поймать при случайной компенсации ошибок.

**4.2 — Role B не пишется.** После прогона на 40 итерациях явно прочитать `fluidD` и сравнить с исходным seed — побитовое совпадение. Кернел не объявляет `RWTexture2D` для B-слота, значит на уровне HLSL запись невозможна; тест фиксирует это как наблюдаемый факт.

**4.3 — встроенная геометрия ролей, через прямой `Initialize`, не через `Build`.** `fluidPhi` и `fluidD` с разным `Resolution` (оба квадратные тексели по отдельности) → вызвать `pass.Initialize(context)` **напрямую** на харнесе (минуя `SquareTexelValidator`, который в `SimulationWorld.Build` вызывается раньше `Initialize`, строка 182 против 220, и упал бы первым с другим текстом). Ожидаемое исключение — `InvalidOperationException` с текстом `"matching Resolution and plane"` (`ValidateMatchingFieldGeometry`, ADR-008). Это подтверждает, что для multi-role встроенная защита реально срабатывает для комбинации ролей WritePingPong+Read, которая раньше не проверялась.

**4.4 — `SquareTexelValidator`, неквадратный тексель.** Прямой вызов `SquareTexelValidator.Validate(passes, fields)` (не `Build`, по тому же образцу, что уже используется для аналогичных тестов F1.1) — `fluidPhi`/`fluidD` на одинаковом `Resolution`, но `Size.x/Res.x ≠ Size.y/Res.y` → падение с `"ADR-016 §2.1"`.

**4.5 — формат `fluidPhi`.** Явная проверка `GraphicsFormat.R32_SFloat` на дескрипторе.

**4.6 — сузить существующий тест «дефолт `RepeatCount` = 1 у всех».** Найти этот тест в `RepeatCountTests.cs`, заменить блокирующую формулировку («у всех конкретных `SimPass` `RepeatCount == 1`») на явный allowlist известных решателей с задокументированным иным дефолтом: `{ nameof(JacobiPhiPass) }` (список, не одна строка — следующий решатель добавляется сюда явно, а не молча ломает тест). Assert остаётся для всех остальных типов без изменений.

### Шаг 5 — документация

- `DOC/pass-catalog.md`: раздел `Jacobi` — вход/выход, `RepeatCount` (дефолт 40, `[Range(1,80)]`), явная строка «warm-start между кадрами: `fluidPhi` не очищается перед Jacobi, наследует результат предыдущего кадра — см. Techdebt про дрейф среднего».
- `Techdebt.md`: новый пункт — линейный дрейф среднего `Φ` при `ΣD ≠ 0` (формула из ADR-018 §5), статус «известно, не устранено, блокер перед F1.6», ссылка на будущий F1.2b.
- `DOC/status.md`, `DOC/capabilities.md`: F1.2 закрыт, класс — `JacobiPhiPass`.
- Фазовый план: добавить строку **F1.2b — zero-mean projection `fluidD`**, статус «открыт, блокер перед F1.6», однострочное описание подхода (reduction через `InterlockedAdd` fixed-point аккумулятор по образцу P2G, ADR-002). Не реализовывать в этом тикете.

### Отчёт

1. `max|r|` после итерации 1 и после итерации 40, точное отношение (ожидание ≥10×; если 3–8× — явно так и написать, не округлять формулировку).
2. `ΣΦ` после 40 итераций.
3. Текст исключения из 4.3 — подтвердить, что сработала именно `ValidateMatchingFieldGeometry` (`"matching Resolution and plane"`), не `SquareTexelValidator`.
4. Текст исключения из 4.4 — подтвердить `"ADR-016 §2.1"` от прямого вызова валидатора.
5. Подтверждение 4.2 — побитовое совпадение `fluidD` до/после.
6. Формат `fluidPhi` — подтверждение `R32_SFloat`.
7. Diff по production-коду: `FluidPasses.cs`, `FluidPasses.compute` (включая обёртку существующего блока `Divergence` в `#ifdef KERNEL_DIVERGENCE` — это правка старого кода, не только добавление нового), правка в `RepeatCountTests.cs` (шаг 4.6). Ничего в `SimPass.cs`/`SquareTexelValidator.cs`/`SimulationWorld.cs`/`M3DDemoTools.PassLibraryPaths` — путь к `FluidPasses.compute` там уже есть с F1.1.
8. Состояние сьюта — ноль красных.
9. Подтверждение, что F1.2b открыт как отдельный пункт плана.

### Вне скоупа

- `SubtractPressureGradientPass`, `AdvectScalarPass`, `Fluid2D`-пресет — F1.3+.
- **F1.2b (zero-mean projection `fluidD`)** — открыть как тикет, не реализовывать.
- Истинные граничные условия — F1.4.
- Калибровка дефолта `iterations = 40` под реальный пресет — F1.6.
