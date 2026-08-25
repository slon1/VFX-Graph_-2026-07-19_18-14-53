## ADR-018: JacobiPhiPass

**Статус:** Принято (F1.2b закрыт в §5.1)
**Дата:** 2026-08-23
**Контекст:** M3D Framework, фаза F1 (F1.2) — второй кернел fluid-контура
**Реализует:** [ADR-016](ADR-016-Units-By-Pass-Family.md) §2 (формула Jacobi); использует [ADR-015](ADR-015-World-Owned-Repeat-Loop.md) (`RepeatCount`) по прямому назначению и [ADR-008](ADR-008-Multi-Field-Per-Kernel-Binding.md) (multi-role) в новой форме
**ТЗ:** [`todo-F1.2.md`](../last/todo-F1.2.md)

### Контекст

`JacobiPhiPass` решает `ΣΦneighbors − 4ΦC = D` относительно `Φ` (ADR-016 §2). Класс не называется `JacobiPressurePass`: ADR-016 §2.2 запрещает называть `Φ` давлением именно потому, что это провоцирует делить на `dt` или сравнивать с физическим давлением — то же рассуждение относится и к имени типа, не только к имени поля.

**Поправка к описанию `Divergence` (F1.1).** Первая версия этого ADR ошибочно описывала `DivergenceFieldPass` как single-role пасс (оба поля под Role A), из чего выводилась необходимость `SquareTexelValidator` утверждения (b). Фактическая реализация F1.1 — **multi-role**: `velocity` (`Read`) — Role A / `FieldReadA`, `fluidD` (`WriteInPlace`) — Role B / `FieldWriteB` (`Assets/Shaders/GPU/Passes/FluidPasses.compute`, `Assets/Scripts/Passes/FluidPasses.cs`). Это значит, что `ValidateMatchingFieldGeometry` (`SimPass.cs:686`) уже срабатывает для `Divergence` на `Initialize`, и защита от несовпадения геометрии между `velocity`/`fluidD` идёт из встроенного механизма ADR-008, а не только из `SquareTexelValidator`. Утверждение (b) в `SquareTexelValidator` при этом не становится бесполезным: это Build-time проверка, которая срабатывает **раньше** `Initialize` (`SimulationWorld.Build`: `SquareTexelValidator.Validate` на строке 182, `pass.Initialize` — на строке 220) и не зависит от того, single-role или multi-role пасс. Она остаётся сеткой безопасности на случай будущего single-role fluid-пасса, но для уже существующих `Divergence` и вводимого здесь `JacobiPhiPass` — избыточна относительно встроенной проверки. Обе проверки не конфликтуют, каждая просто ловит одно и то же раньше или позже.

У `JacobiPhiPass` — **два** различных поля нужны одновременно внутри одного кернела: `fluidPhi` — через self-ping-pong (сосед читает `Current`, центр пишет в `Next`), и `fluidD` — как постоянная правая часть, читаемая, но не изменяемая за все `RepeatCount` итераций одного кадра. Это новая форма multi-role, отличная и от `Divergence` (Read=A/Write=B, оба поля различны), и от `GrayScottPass` (обе роли `WritePingPong`): здесь Role A = `WritePingPong` (самоссылающееся), Role B = чистый `Read` без записи. Формально гайд-матрица `AssignSlotIdsAndValidateRoles` (`SimPass.cs`) это не запрещает: `FieldAccess.Read` в `Execute` биндит только `ReadId`, `WriteId` не используется — значит Role B без записи механически работает. Но это непроверенный путь, и первое реальное использование заслуживает отдельного теста (§5, DoD 4), а не тихого предположения, что раз гайд не бросает исключение — значит корректно.

Записать явно для будущих тикетов: у fluid-семейства уже накопилось три разных сочетания ролей на трёх пассах (`Divergence`: Read A / Write B; `GrayScott`: WritePingPong A / WritePingPong B; `JacobiPhiPass`: WritePingPong A / Read B). `SubtractPhiGradientPass` (F1.3, [ADR-020](ADR-020-Subtract-Phi-Gradient-Pass.md)) по форме ближе к `JacobiPhiPass`, чем к `Divergence`: `velocity` читается и переписывается (но `WriteInPlace`, не `WritePingPong` — нет самоссылки, читается `velocity`, а корректируется результатом другого поля), `fluidPhi` — чистый `Read`. Не копировать слепо форму `Divergence`.

### Решение

#### 1. Роли: `fluidPhi` — Role A, `fluidD` — Role B (read-only)

```
FieldWrites = [ (fluidPhi, WritePingPong, Scalar, 1, Role A) ]
FieldReads  = [ (fluidD,   Read,          Scalar, 1, Role B) ]
```

`fluidPhi` — Role A по той же причине, что и во всех существующих multi-role пассах: `PrimaryFieldName` при `multiRoleBindings` ищет Role A **среди Write**, и dispatch обязан идти по домену **записываемого** поля, а не константного входа.

Поскольку присутствуют обе роли `{A, B}`, `multiRoleBindings = true` автоматически, и `ValidateMatchingFieldGeometry` (`SimPass.cs:686`) срабатывает без дополнительного кода — проверяет совпадение `Resolution`, `Origin`, `AxisU`, `AxisV`, `Size` между ролями на `Initialize`. `SquareTexelValidator.Validate` (F1.1) вызывается раньше, на `Build` (`SimulationWorld.cs:182`, до `Initialize` на строке 220) — проверяет утверждение (a), квадратность текселя каждого поля, которую `ValidateMatchingFieldGeometry` не проверяет. Обе проверки независимы по времени срабатывания и по тому, что именно проверяют; специальный случай под multi-role в `SquareTexelValidator` не вводится.

`JacobiPhiPass.RequiresSquareTexel => true` — обязательно, хотя в самой формуле `ΣΦneighbors − 4ΦC = D` нет ни одного `h`. Причина ровно та же, что у `Divergence`: отсутствие `h` в формуле — следствие допущения `hx = hy` из вывода ADR-016 §2, а не самостоятельный факт. Без этого допущения формула требовала бы весов `1/hx²`, `1/hy²` по отдельности.

#### 2. Компиляционный блокер: два разных типа под именем `FieldReadA` в одном файле

`FluidPasses.compute` уже объявляет `Texture2D<float2> FieldReadA` (velocity, `Divergence`). Кернелу `Jacobi` нужен `FieldReadA` типа `Texture2D<float>` (`fluidPhi`). Это не конфликт областей видимости кернелов — Unity компилирует **весь текст файла** для каждого kernel-entry, `#pragma kernel` не изолирует объявления по секциям; повторное объявление одного имени с другим типом — обычная ошибка компиляции HLSL, независимо от того, какой kernel собирается в данный момент.

Решение — токен после имени кернела в `#pragma kernel`, который Unity определяет как `#define`, активный **только** при компиляции этого конкретного kernel-варианта:

```hlsl
#pragma kernel Divergence KERNEL_DIVERGENCE
#pragma kernel Jacobi KERNEL_JACOBI

#ifdef KERNEL_DIVERGENCE
Texture2D<float2> FieldReadA;
RWTexture2D<float> FieldWriteB;
/* LoadClampedVelocity, Divergence() — весь блок Divergence */
#endif

#ifdef KERNEL_JACOBI
Texture2D<float> FieldReadA;
RWTexture2D<float> FieldWriteA;
Texture2D<float> FieldReadB;
/* LoadClampedPhi, Jacobi() — весь блок Jacobi */
#endif
```

При компиляции варианта `Jacobi` препроцессор вырезает блок `KERNEL_DIVERGENCE` целиком, прежде чем компилятор увидит конфликтующее объявление — коллизии физически не возникает. Каждая функция-хелпер (`LoadClampedVelocity`, `LoadClampedPhi`) обязана быть внутри того же блока `#ifdef`, что и глобальная переменная, которую она читает — иначе тот же конфликт типов возникнет внутри тела функции.

Это новый паттерн для проекта: до сих пор разные типы `FieldRead*` разводились по разным `.compute`-файлам (`FieldPasses.compute` — `float2`, `GrayScottPasses.compute` — `float`). Второй файл для fluid-кернелов не заводится: `FluidPasses.compute` уже принят как единое место для world-семейства (ADR-017), и это соображение важнее, чем избежать одного нового препроцессорного паттерна. Общие для всех кернелов файла объявления (`#include "FieldSampling.hlsl"`, `#define FIELD_THREADS 8`) остаются вне `#ifdef`-блоков — они не конфликтуют.

#### 3. Кернел `Jacobi`

```hlsl
#ifdef KERNEL_JACOBI
Texture2D<float> FieldReadA;    // fluidPhi, Current
RWTexture2D<float> FieldWriteA; // fluidPhi, Next
Texture2D<float> FieldReadB;    // fluidD (константа за кадр)

float LoadClampedPhi(int2 q)
{
    int2 maxP = FieldResolution - 1;
    q = clamp(q, int2(0, 0), maxP);
    return FieldReadA.Load(int3(q, 0));
}

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void Jacobi(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y) return;

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

Соседи `Φ` — `Load` с явным clamp индекса (та же форма, что `Divergence` и `DiffusePasses.compute`), обеспечивая ту же Neumann-подобную границу, что у остальных fluid-кернелов (ADR-016 §2.3). `D` читается в своей же ячейке без clamp — индекс `p` уже проверен bounds-check'ом выше по коду, второй clamp был бы избыточен.

#### 4. `RepeatCount` — прямое применение ADR-015

```csharp
[SerializeField, Range(1, 80)] private int iterations = 40;
public override int RepeatCount => iterations;
```

Это первый пасс, переопределяющий `RepeatCount` — механизм существовал с F0.1 (ADR-015) без единого потребителя. Дефолт `40` — предварительная оценка, не откалиброванная величина; уточняется по факту визуального прогона в F1.6, когда появится реальный пресет для калибровки. Калибровать по **осевым** модам `(k,0)/(0,k)` как худшему случаю 2D-Jacobi (`λ = (1+cos(kπ/N))/2`), не по диагонали `(k,k)` — errata 1 [ADR-020 §3](ADR-020-Subtract-Phi-Gradient-Pass.md), [Techdebt 8e](../last/Techdebt.md). `λ^40` по Φ не есть отношение `max|D|` после проекции ([Techdebt 8f](../last/Techdebt.md)). `[Range(1, 80)]` — мягкая верхняя граница для инспектора, не хардлимит: `SquareTexelValidator`/`RepeatCountValidator` её не проверяют, `RepeatCount ≥ 1` — единственный инвариант, гарантируемый Build (ADR-015 §4).

`deltaTime`, передаваемый в `Execute`, кернелом **не используется** — в формуле Jacobi его нет (ADR-016 §2: итерации решают одну систему на одном временном слое, эффективный шаг остаётся `dt`, а не `N·dt` — предупреждение из `pass-catalog.md` про Gray-Scott/Diffuse сюда не относится).

#### 5. Найденный при выводе риск: линейный дрейф среднего `Φ` при `ΣD ≠ 0`

Средний по сетке `Φ` при чистом Neumann (clamp-граница) обновляется по закону:

```
mean(Φ_{k+1}) = mean(Φ_k) − mean(D)/4
```

Вывод: сумма Лапласиана `Φ` по всей сетке равна нулю тождественно при clamp-границе (то же дискретное тождество, на котором держится сохранение суммы в `HarnessDiffuseTests`, ADR-014 §3.2, и уже подтверждено там численно — `Σ` после 10 итераций диффузии `0.999999642` от `1`). При таком тождестве сумма формулы Jacobi по всем ячейкам даёт `4·N·mean(Φ_{k+1}) = 4·N·mean(Φ_k) − Σ D`, откуда приведённая формула.

Если `Σ D ≠ 0` (то есть если суммарная дивергенция впрыска через `TouchInjectVelocity` не равна нулю — типичный случай для радиального «сплеша» без компенсирующего стока), средний уровень `Φ` **сдвигается линейно каждую итерацию**, а не сходится. За кадр (`RepeatCount = 40`) это `10·mean(D)`. При warm-start (`fluidPhi` — `WritePingPong`, и следующий кадр читает `Current` без явного `ClearFieldPass` перед Jacobi, то есть наследует результат предыдущего кадра) сдвиг накапливается **кадр за кадром** при удержании касания.

Для итогового `u = u* − ∇Φ` (F1.3) это не влияет на корректность: постоянная составляющая `Φ` дифференцируется в ноль (ADR-016 §2.2: `Φ ≠ давление`, называть его так и сравнивать с физическим давлением нельзя именно из-за этого). Единственная опасность — исчерпание мантиссы `R32_SFloat` (23 бита) накопленной константой, после чего физически значимая флуктуация `Φ` перестаёт быть представимой. При нескольких секундах непрерывного удержания пальца это не гипотетический, а реально достижимый сценарий для инструмента, рассчитанного на длительное интерактивное использование.

**В скоуп F1.2 не входит**: тесты этого тикета используют синтетические источники с `Σ D = 0` (см. DoD), где дрейфа не возникает вообще, и `JacobiPhiPass` полностью корректен для них. Follow-up **F1.2b** закрыт в §5.1.

#### 5.1 F1.2b — `ZeroMeanScalarPass` (решение)

ТЗ: [`todo-F1.2b.md`](../last/todo-F1.2b.md).

**Отдельный пасс**, не прелюдия внутри `JacobiPhiPass`. `FieldKernelPass.Execute` sealed и один кернел; `RepeatCount` у Jacobi = 40, reduction внутри него гонялся бы 40 раз на кадр. Цепочка в пресете (F1.6): `Divergence → ZeroMeanScalar → Jacobi ×N → SubtractPhiGradient`. Этот тикет пресет не собирает.

**Не** `FieldAccumBuffer` / `FieldAccumPassValidator`. P2G-буфер — per-texel `[value…, count]`, state machine Clear→Scatter→Normalize, кодирование с `max(x,0)` под неотрицательные скорости. `D` знаковое; сумма по всей сетке — один скаляр, не N слотов. Чужой SM и аллокация `N·(C+1)` uint не нужны.

`JacobiPhiPass` не меняется: Role B по-прежнему read-only (тест 4.2 bitwise остаётся верным, если ZeroMean в том тесте не вызывать).

Класс — `ZeroMeanScalarPass` (`PassCategory.Transport`), не FieldKernelPass: три кернела в одном `Execute`, как `ClearFieldAccumPass`. Default field `fluidD`. `RequiresSquareTexel => false` (среднее не содержит `h`). `RepeatCount` = 1. `dt` нет.

Роль: одно поле, `WriteInPlace`, Scalar, Role A. Чтение `D` из `FieldWriteA[p]` (WriteInPlace биндит только `WriteId`). `FieldReadA` не объявлять.

Буфер суммы — `GraphicsBuffer` на 1× `uint`, владение пасса. `ZeroMeanScalarPass : IDisposable`: в `Initialize` освободить предыдущий буфер, затем создать; `Dispose` идемпотентен. `SimulationWorld.Teardown` — цикл по `effect.Passes`, `if (pass is IDisposable d) d.Dispose()` (три строки, до `fields?.Dispose()`). Не виртуальный `Teardown` на `SimPass`. Тесты: `Dispose` пасса в `finally` / `using`. Не ключ `FieldSet`, не `FieldAccumBuffer`.

Три кернела в `FluidPasses.compute`, `#ifdef KERNEL_ZEROMEAN` (изолирует `FieldWriteA` как `RWTexture2D<float>` от Subtract `float2`):

```
#pragma kernel ZeroMeanClear KERNEL_ZEROMEAN
#pragma kernel ZeroMeanAccum KERNEL_ZEROMEAN
#pragma kernel ZeroMeanApply KERNEL_ZEROMEAN
```

Кодирование — InterlockedAdd **uint**, но не копия `EncodeFixed` из P2G без Bias. `D` может быть отрицательным. CPU в `Initialize` считает `N = res.x * res.y` и:

```
Bias  = 256
Scale = max(1, floor((2^32 / 4) / (2 · N · Bias)))
```

Запас `/4` в числителе, чтобы сумма не садилась на потолок uint. `Bias = 256` — **дефолт продукта**, не потолок теста. Клетка с `|D| > Bias` клипается только в аккумуляторе; Apply вычитает mean из сырого `D`. Сильный TouchInject может занизить mean и оставить частичный дрейф Φ — known limitation, калибровка Bias в F1.6, не этот тикет. На 64² Scale = 512; на 256² Scale = 32. Порог тестов `|mean(Φ)| < 0.1` — только геометрия 64² / Scale=512. Для дрейфа мантиссы Φ Scale хватает: цель — снять константу `mean(D)`, не хранить `D` в fixed-point.

```
encoded = round( (clamp(D, −Bias, Bias) + Bias) · Scale )   // NaN → encode(0)
mean    = MeanAccum[0] / (Scale · N) − Bias
D       ← D − mean
```

Порядок в `Execute`: `LastExecuteDispatched = false` в начале (как ClearAccum). Clear (1 группа) → Accum (сетка `FIELD_THREADS`) → Apply (сетка). Три `DispatchCompute` в одном CommandBuffer, не три пасса в EffectAsset. Биндинг **на каждый kernel index**: Clear — только `MeanAccum`; Accum и Apply — `FieldWriteA` = `Current` (`SimShaderIds.FieldWriteA`), `FieldShaderParams.Push`, `MeanAccum`, Bias/Scale; Apply ещё `TexelCount` (`SetComputeIntParam`). UAV-барьер не изобретать (тот же контракт, что ClearAccum→Scatter в одном кадре). Если 3.1 красный и `meanAfter≈1` — порядок Clear/Accum, не fence.

Среднее — **по всем текселям**, то же множество, что гейт 3.6 и вывод `Σ∇²Φ ≡ 0` при clamp-Neumann. F1.4 ([ADR-021](ADR-021-Solid-Wall-Velocity-Pass.md)) не меняет область: стены — пост-обработка `u`, не новая задача Пуассона.

### Отклонённые варианты

**Пин одной угловой ячейки вместо вычитания среднего.** Дешевле (не требует reduction-прохода), но фиксирует произвольную точку, а не истинное среднее — при несимметричной границе способен оставить значительный остаточный дрейф во внутренней области, которую пин не контролирует. Вычитание среднего — учебниковое устранение null-space чистой Neumann-задачи Пуассона (условие разрешимости `∫∫f = 0`), пин угла — инженерный обходной путь без этой гарантии.

**Устранять дрейф в F1.2, не открывая F1.2b.** Отклонено по объёму (закрыто в F1.2).

**Встроить reduction в `JacobiPhiPass`.** Sealed `Execute`, лишние 40 reduction на кадр, ломает bitwise-тест Role B.

**Переиспользовать `FieldAccumBuffer`.** Чужой layout и SM; `D` знаковое; аллокация на всю сетку ради одного числа.

**CPU-readback `ΣD` каждый кадр.** Харнес так может, World — нет.

**`max(x,0)` из P2G без Bias / без clamp в `±Bias`.** Отрицательный `D` станет нулём в сумме, mean сместится, дрейф останется.

**Менять F1.3 цепочку 3.6 / Jacobi 4.x в этом тикете.** Сиды там уже совместны; ZeroMean для них почти no-op. Регрессию ловят существующие тесты, если `#ifdef` сломает компиляцию.

**Отдельная не-multi-role реализация (две сериализованные строки имени, ручной биндинг без ADR-008).** Отклонено: multi-role уже существует, уже даёт `ValidateMatchingFieldGeometry` бесплатно, и ручной обход этого механизма ради «простоты» на самом деле воссоздаёт именно ту проверку, которую пришлось писать отдельно для `Divergence`.

### Последствия

- (+) Первый потребитель `RepeatCount` — механизм ADR-015 наконец используется по прямому назначению.
- (+) Первое использование Role B как read-only — проверяет ранее непроверенный путь гайд-матрицы ADR-008.
- (+) `ValidateMatchingFieldGeometry` закрывает риск несовпадения геометрии между `fluidPhi`/`fluidD` без дополнительного кода.
- (+) Явно выведена и посчитана скорость дрейфа среднего `Φ` — раньше это было качественное «закрепить нулевое пространство» без количественной оценки риска.
- (−) `FluidPasses.compute` заводит новый для проекта паттерн — `#ifdef KERNEL_*` вокруг типоспецифичных объявлений в одном файле. Оправдано (второй файл ломает решение ADR-017), но следующий кернел этого файла с третьим конфликтующим типом обязан следовать той же форме, не изобретать свою.
- (+) F1.2b (`ZeroMeanScalarPass`) закрыт в §5.1.

### DoD

1. `JacobiPhiPass` (`PassCategory.Transport`) в `FluidPasses.cs`, кернел `Jacobi` в `FluidPasses.compute` за `#ifdef KERNEL_JACOBI`. `FluidPasses.compute` уже в `M3DDemoTools.PassLibraryPaths` с F1.1 — путь не добавляется повторно.
2. Поле `fluidPhi`: `Scalar`, `R32_SFloat`, аналогично `fluidD` (ADR-017).
3. Харнес ADR-014, `RunPass` с `pass.RepeatCount`: синтетический **zero-mean** источник `D` (компактный диполь: `+A` в одной ячейке, `−A` в другой, остальное 0 — гарантирует `Σ D = 0` точно, без reduction-инфраструктуры), `RepeatCount = 40`. Метрика — **невязка** `r = ΣΦneighbors − 4ΦC − D` в каждой ячейке, вычисленная на CPU той же clamp-логикой, что кернел, и только на внутренних ячейках. Утверждение: `max|r|` после 40 итераций упала **не менее чем на порядок** относительно `max|r|` после 1 итерации. Если фактическое отношение окажется в диапазоне 3–8× — это документируется как находка отчёта (спектр Jacobi на низких частотах сходится медленно, `ρ ≈ cos²(π/N)`), а не тихо подгоняется под формулировку DoD. Дополнительно: `ΣΦ ≈ 0` после 40 итераций (дёшево, ловит опечатку знака в формуле).
4. Тест на Role B read-only: явная проверка, что `Execute` не пытается писать в `FieldWriteB` (которого просто нет в HLSL) и что повторный вызов `RunPass` с `RepeatCount > 1` не портит `fluidD` (значение до и после цикла идентично).
5. `SquareTexelValidator`: неквадратный `fluidPhi`/`fluidD` → падение с `"ADR-016 §2.1"` при прямом вызове `SquareTexelValidator.Validate`. Отдельно, через прямой вызов `pass.Initialize(context)` (не через `Build`) — несовпадающая геометрия между Role A и Role B даёт `"matching Resolution and plane"` (`ValidateMatchingFieldGeometry`, ADR-008), подтверждая, что встроенная проверка действительно работает для read-only Role B.
6. `pass-catalog.md`: раздел `Jacobi` — RepeatCount-решатель, ссылка на F1.2b как обязательный пункт перед F1.6.
7. Открыт тикет F1.2b (zero-mean projection `fluidD`) с явной пометкой «блокер перед F1.6, не перед F1.3/F1.4» в фазовом плане. Решение пасса — §5.1; закрытие — [`todo-F1.2b.md`](../last/todo-F1.2b.md).
