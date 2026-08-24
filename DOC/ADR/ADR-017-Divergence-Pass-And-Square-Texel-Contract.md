## ADR-017: DivergenceFieldPass и контракт RequiresSquareTexel

**Статус:** Принято (F1.1)
**Дата:** 2026-08-23
**Контекст:** M3D Framework, фаза F1 (F1.1) — первый кернел fluid-контура
**Реализует:** [ADR-016](ADR-016-Units-By-Pass-Family.md) §2, §2.1, §2.2, §2.3
**ТЗ:** [`todo-F1.1.md`](../last/todo-F1.1.md)

### Контекст

ADR-016 определил математику и контракт fluid-контура (`D`, `Φ`, квадратный тексель, границы через `Load`+clamp), но не добавил кода — «свойство без переопределяющих было бы мёртвым» (ADR-016 §2.1). Этот тикет — первое переопределение: `DivergenceFieldPass`, первый пасс, физически принадлежащий fluid-семейству, и вместе с ним — механизм `RequiresSquareTexel`, без которого он остаётся текстом.

Divergence выбран первым кернелом контура (а не Jacobi или Advect-dye) по трём причинам. Он не итеративен — `RepeatCount = 1`, поэтому не зависит от ADR-015 сверх уже готового механизма. Он не требует `WritePingPong` — `velocity` (Read) и `fluidD` (Write) это разные поля, самозависимости нет, значит не требует ping-pong семантики вообще, и DoD на харнесе проще: сравнение с CPU-эталоном без цикла Execute+Swap. И у него есть точный аналитический эталон (§2), которого нет ни у Jacobi (итеративная сходимость), ни у Subtract (зависит от Jacobi).

### Решение

#### 1. `SimPass.RequiresSquareTexel` и `SquareTexelValidator`

```csharp
/// <summary>Fluid operators assume hx == hy (ADR-016 §2.1). Validated at Build.</summary>
public virtual bool RequiresSquareTexel => false;
```

По тому же образцу, что `RepeatCount` (ADR-015 §1): `virtual` в базе, без `[SerializeField]` — большинству пассов это свойство не нужно, и засорять инспектор незачем.

`SquareTexelValidator` — статический класс по образцу `RepeatCountValidator`/`FieldAccumPassValidator`: принимает `IReadOnlyList<SimPass>` и `FieldSet`, не `SimulationWorld`, чтобы быть тестируемым без сборки эффекта. Для каждого пасса с `RequiresSquareTexel == true` выполняет **два** независимых утверждения:

1. **Квадратный тексель на каждом поле.** Для каждого дескриптора из `FieldReads`/`FieldWrites`: `hx = Size.x/Resolution.x`, `hy = Size.y/Resolution.y`, относительная разница `< 1e-4`. При нарушении — `InvalidOperationException` с именем пасса, полем, посчитанными `hx`/`hy` и ссылкой на ADR-016 §2.1.
2. **Совпадающее разрешение между всеми полями пасса.** Общий `ValidatePassFieldCoordinates` (`SimulationWorld.cs`) намеренно разрешает read-полям отличаться в разрешении от write-поля — это механизм под UV-read (ADR-001 §8) и будущий cross-resolution G2P через `SampleLevel` (F0.6). Поправка ADR-008 (2026-08-23) закрепляет то же исключение на уровне контракта: matching Resolution не покрывает UV-read; `Load` с чужой сетки по-прежнему запрещён. Fluid-кернелы индексируют соседей через `Load(id.xy)`: `id` пробегает домен диспатча (разрешение write-поля), и при разном разрешении read-поля `Load` читает мусор по чужим координатам — молча, без единого предупреждения. Поэтому для `RequiresSquareTexel`-пассов общее послабление **переопределяется**: все дескрипторы из `FieldReads ∪ FieldWrites` обязаны иметь одинаковый `Resolution`. Нарушение — `InvalidOperationException` с именами двух полей, их `Resolution` и ссылкой на этот параграф. `FieldKernelPass.ValidateMatchingFieldGeometry` до F0.5 всё ещё дублирует старый запрет ADR-008 на все роли.

Оба утверждения проверяются в одном проходе по объединённому множеству дескрипторов пасса, а не в двух отдельных обходах.

Выключенные пассы пропускаются — тем же условием, что везде в `SimulationWorld.Build`/`Update` (`pass == null || !pass.Enabled`). Решение согласовано с ADR-015 §4 после того, как F0.4 установил истинную причину четырёх красных тестов (guard в `VfxParticleBinder`, не инициализация выключенных пассов) — то есть основание для симметрии между `RepeatCountValidator` и `SquareTexelValidator` подтверждено фактом, не предположением.

Верхней границы или предупреждения на «почти квадратный» тексель нет: допуск `1e-4` — это порог false positive от округления `float`, не художественный люфт.

#### 2. `DivergenceFieldPass`

Новый файл `Assets/Shaders/GPU/Passes/FluidPasses.compute` — отдельно от `FieldPasses.compute`. Причина не организационная: `FieldPasses.compute` целиком принадлежит texel/UV-семействам ADR-016 (`DiffuseVelocityField`, `SteerToVelocityField`, `AddNormalized*`), и класть туда world-кернел означало бы читателю искать соглашение по контексту файла, а не по декларации. Разделение семейств по файлам делает нарушение контракта видимым на уровне «в каком файле».

```hlsl
#pragma kernel Divergence

#define FIELD_THREADS 8

// Two field names cannot share Role A (ADR-008). Read=A / Write=B.
Texture2D<float2> FieldReadA;   // velocity
RWTexture2D<float> FieldWriteB; // fluidD

float2 LoadClampedVelocity(int2 q)
{
    int2 maxP = FieldResolution - 1;
    q = clamp(q, int2(0, 0), maxP);
    return FieldReadA.Load(int3(q, 0));
}

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void Divergence(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y) return;

    int2 p = int2(id.xy);
    float2 uE = LoadClampedVelocity(p + int2( 1, 0));
    float2 uW = LoadClampedVelocity(p + int2(-1, 0));
    float2 uN = LoadClampedVelocity(p + int2( 0, 1));
    float2 uS = LoadClampedVelocity(p + int2( 0,-1));

    FieldWriteB[p] = uE.x - uW.x + uN.y - uS.y;
}
```

`LoadClampedVelocity` — та же форма, что `LoadClamped` в `DiffusePasses.compute` (clamp индекса, затем `Load`), только для `float2`. Копирование формы, а не изобретение новой, снижает риск разъехаться с уже проверенным паттерном.

Соседи читаются через `Load` с **явным `clamp` индекса**, не через незащищённый `Load` за границей текстуры. ADR-016 §2.3 аргументирует запрет незащищённого чтения тем, что OOB `Load` возвращает 0 и вносит ложную дивергенцию из ниоткуда — clamp здесь применяет ту же логику, что `LoadClamped` в `DiffusePasses.compute`, а не буквальный ноль-паддинг. Следствие для DoD в §3: результат на рамке домена **не обязан** совпадать с континуальным эталоном — граница считает Neumann-подобное продолжение поля, а не истинную дивергенцию за пределами домена. Это ожидаемое поведение, не дефект; истинные граничные условия (обнуление нормальной компоненты после проекции) — задача F1.4, здесь не решается.

C#: `DivergenceFieldPass : FieldKernelPass`, `KernelName = "Divergence"`, `FieldReads = { ("velocity", Read, Role A) }`, `FieldWrites = { ("fluidD", WriteInPlace, Role B) }`, слоты `FieldReadA` / `FieldWriteB`, `RequiresSquareTexel => true`. Два разных имени поля не могут делить Role A — `FieldKernelPass.AssignSlotIdsAndValidateRoles` (ADR-008) повесит на legacy `FieldRead`/`FieldWrite` только одно имя (типичный `WritePingPong` на себе). Поэтому это multi-role пасс в смысле биндинга, хотя кернел по-прежнему «одна read-текстура + одна write-текстура». `WriteInPlace`, не `WritePingPong` — нет самозависимости (пасс не читает `fluidD`), ping-pong не нужен.

Побочный эффект Role A+B: на `Initialize` срабатывает `ValidateMatchingFieldGeometry` — не только `Resolution`, но Origin / оси / Size. Это жёстче, чем утверждение (b) у `SquareTexelValidator`, и нужно: два квадратных поля с одним `Resolution` и разным `Size` имеют разный `h`, и `Load(id.xy)` тогда читает согласованные индексы по несогласованной сетке. Jacobi / Subtract в F1.2+ обязаны копировать эту схему слотов, не возвращаться к `FieldRead`/`FieldWrite`.

#### 3. Поле `fluidD`: `R32_SFloat`, не общий формат полей

`FieldDescriptor` для `fluidD`: `Semantic = Scalar`, `Channels = 1`, `Format = R32_SFloat`. Не `R16_SFloat`, которым размечены большинство существующих Scalar-полей (`dye`, `density`). Основание перенесено из более раннего расчёта: `fluidD` — вход в Jacobi (ADR-016 §2, `ΣΦ − 4ΦC = D`), а требуемый динамический диапазон решателя Пуассона растёт как `N²`; при 256² гладкая мода превышает источник на три порядка, и мантиссы `half` (10 бит) не хватает примерно в 8 раз, чтобы Jacobi не садился на пол округления раньше, чем невязка упадёт на порядок. `fluidD` сам по себе не итерируется, но его точность — потолок для точности всего, что из него вычисляется, и подменять её позже нельзя: `Φ` создаётся в F1.2 тем же полем, и если `fluidD` уже `R16F`, разговор о precision для `Φ` бессмысленен. `Techdebt.md`/`pass-catalog.md` фиксируют это явно, чтобы решение не потерялось между тикетами F1.1 и F1.2/F1.3.

### Отклонённые варианты

**`RequiresSquareTexel` как atribute/reflection-маркер вместо virtual property.** Проект уже использует virtual property для аналогичного контракта (`RepeatCount`, ADR-015), а `SquareTexelValidator` по структуре идентичен `RepeatCountValidator` — рассинхронизация двух похожих механизмов в стиле не оправдана.

**Ноль-паддинг вместо clamp на границе `Divergence`.** Буквальная реализация «`Load` за границей = 0» дала бы ложный отток/приток на каждом краю домена на **каждом** кадре, а не только в тесте — визуально заметный артефакт по периметру экрана. Clamp — тот же приём, что уже принят в `DiffusePasses.compute`, и он лишь отодвигает проблему истинных BC на F1.4, не решает её здесь.

**Divergence сразу как `WritePingPong`** «на будущее, если понадобится история». Отклонено: нет читателя `fluidD.Next` в этом пасе, добавление ping-pong без причины — это лишний swap в `SimulationWorld.Update` и лишняя сложность в тесте на харнесе без единого текущего потребителя.

**Legacy-слоты `FieldRead`/`FieldWrite` для двух имён** «потому что одна read-роль и одна write-роль, ADR-008 не нужен». Отклонено фактом: `AssignSlotIdsAndValidateRoles` трактует Role как имя слота, не как направление доступа. Два имени с дефолтным Role A — hard-error ADR-008. Правильная схема — как у `AgentBoostFieldPass`: Read = A, WriteInPlace = B → `FieldReadA` / `FieldWriteB`. Следующие fluid-кернелы (Jacobi, Subtract) копируют это, а не образец `DiffuseField`.

### Последствия

- (+) `RequiresSquareTexel` из текста ADR-016 становится проверяемым механизмом, а не обещанием.
- (+) Первый fluid-кернел имеет точный (не приближённый) аналитический DoD на внутренних текселях.
- (+) Разделение fluid-кернелов по отдельному файлу делает нарушение единичного соглашения видимым по расположению кода, не только по декларации.
- (+) Слоты `FieldReadA`/`FieldWriteB` (ADR-008) — образец для Jacobi/Subtract, не legacy `FieldRead`/`FieldWrite`.
- (−) Граница домена в `Divergence` содержит структурную неточность до прихода F1.4 (истинные BC). Задокументировано, DoD F1.1 её не покрывает по построению.
- (−) `R32_SFloat` на `fluidD` — вдвое дороже по памяти и полосе, чем `R16_SFloat` соседних Scalar-полей. Приемлемо для desktop-first (см. предыдущее обсуждение precision), пересмотреть при мобильном таргете.

### DoD

1. `SquareTexelValidator`: `RequiresSquareTexel = true` на неквадратном текселе (`Size.x/Res.x != Size.y/Res.y`, разница выше `1e-4`) — падает с именем пасса и посчитанными `hx`/`hy`; на квадратном (включая неквадратный **домен** с квадратным текселем, `Size=(16,9)`, `Resolution=256×144`) — не падает. Выключенный пасс с несовпадающим текселем — Build проходит.
2. `Divergence`, харнес ADR-014, внутренние тексели (исключая рамку в 1 тексель):
   - Uniform velocity `(a, b)` — `D = 0` точно.
   - Линейное поле `u = (x, y)` в мировых координатах (радиальная экспансия) — `div u = 2`, `D = 2h·div = 4h` точно во всех внутренних текселях (центральные разности дифференцируют линейную функцию без ошибки дискретизации).
   - Rotational `u = (-y, x)` — `D = 0` точно (соленоидальное поле).
3. Формат `fluidD` — `R32_SFloat`, зафиксировано тестом на дескрипторе (не только документом).
4. `pass-catalog.md`: раздел Divergence с явной пометкой «граница — Neumann-подобное продолжение через clamp, не истинная дивергенция; истинные BC — F1.4».
