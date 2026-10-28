# ТЗ P9.1: классический хвост `TeamClassicSteer` (ADR-054)

**Исполнитель:** Grok (код, тесты, документы). **Ревью:** Claude. Play и вид в этой фазе не проверяются (пресета нет), устройство не нужно.
**Куда положить:** `DOC/last/todo-adr-054-p9-1-classic-steer.md`.
**Основа:** [ADR-054](../ADR/ADR-054-Classic-Boids-Tail.md) §2.1–2.3, §2.5, §5 (P9.1). Где это ТЗ короче ADR, делать по ADR. Если ТЗ и ADR расходятся или код противоречит тому и другому, остановиться и задать вопрос владельцу, не выбирать самому.
**Коммиты:** (0) ADR-054 и это ТЗ; (А) код и тесты; (Б) документы. Каждый отдельно, EditMode зелёный в А и Б.

## Часть 0. Git (до любых правок)

1. Показать `git status` и хеш головы `feature/reynolds-boids`. Создать ветку: `git switch -c feature/classic-boids` от этой головы. Хеш головы записать в сдачу (база всей работы P9).
2. Чужие незакоммиченные файлы (список в §2.4 плана v2) остаются на месте. `git switch -c` их не трогает.
3. Коммитить только пути, перечисленные в этом ТЗ, явно, включая парные `.meta` новых файлов. Запрещены `git add -A`, `git add .`, `git commit -a`, `git stash`, `git checkout`/`git restore`/`git reset` на файлах вне ТЗ. Перед каждым коммитом показать `git status` и `git diff --cached --stat`.
4. Коммит 0: `DOC/ADR/ADR-054-Classic-Boids-Tail.md` и `DOC/last/todo-adr-054-p9-1-classic-steer.md` (файлы кладёт владелец; если их нет в рабочей копии, остановиться и запросить).
5. В этой фазе не делать `merge`, `rebase`, `push`. Слияние в `feature/reynolds-boids` после «принято» делает владелец; публикация ветки на remote отдельное решение владельца.

## Что делаем

Добавляем классический хвост: пасс `TeamClassicSteerPass` с ядром `TeamClassicSteer`, новый атрибут `inertialVelocity`, расширение `TeamParams` до 64 байт, поля команды `MinSpeed`, `MaxSpeed`, `MaxForce`, `ForceGain`, валидатор и тесты. Пресета, Play и подбора чисел нет (это P9.2). Существующие пассы, пресеты и ассеты не меняются.

## Часть А. Разведка (ответом в сдаче, до кода; код этой части не меняет)

1. Снять число EditMode «до» фактическим прогоном на чистой ветке (перед правками).
2. Найти **все** места, где проверяется или перечисляется `TeamHeadingSteerPass` (известны: `SpatialHashValidator` строка ~225, `SwarmPointValidator` строка ~38, `M3DDemoTools.cs` строки ~1995 и ~2126). Перечислить, что ещё нужно научить `TeamClassicSteerPass` (проверить `PerfArgs`, `FPSDisplay`, меню, тесты пресетов).
3. Добавить `float4 Classic` сразу в обе структуры (C# `TeamParams` и HLSL `struct TeamParams`), `Stride = 64`, и прогнать EditMode **до** правки тестов. Менять только константу нельзя: структуры останутся по 48 байт, `SetData` и `Teams[team]` разъедутся, и покраснеют обходы, а не тесты с зашитым размером. Выписать **все** упавшие тесты (ожидается только `Stride_Is48`), затем править по п. Б2.
4. Подтвердить, что `PrimitiveParticleBinder` и `ParticleBillboard` читают `velocity` (не `heading`) для раскраски по направлению и иглы (ADR-045).
5. Подтвердить, что в `BoidsPasses.compute` доступны `UnitOr` (объявлена перед `TeamHeadingSteer`), `THREADS`, `ParticleCount`, `DeltaTime`, `Teams`, `teamId`, `heading`, `velocity`, и что имя `inertialVelocity` в файле свободно.
6. Подтвердить, как `ParticleKernelPass` привязывает буферы атрибутов (по имени атрибута). Для `inertialVelocity` имя буфера в HLSL должно совпасть с именем атрибута.
7. Подтвердить `simulationSpeed` в рецепте `Boids_hash` (для P9.2).
8. Как `TeamProfile.Cruise` и `Turn` объявлены (поле, свойство, сеттер, `Tooltip`, `Range`), чтобы повторить для новых полей.

## Часть Б. Реализация (коммит А)

**Б1. Атрибут.** `BuiltinAttributes.InertialVelocity = new AttributeId("inertialVelocity", AttributeType.Float3, true)` с XML-комментарием: скорость между кадрами для классического хвоста (ADR-054), пишет `TeamClassicSteerPass`, ноль значит «не инициализирован».

**Б2. `TeamParams` и `TeamProfile`.**
- В `TeamParams` (C#) добавить **последним** полем `public Vector4 Classic;`, `Stride = 64`. `Upload`: `Classic = new Vector4(profile.MinSpeed, profile.MaxSpeed, profile.MaxForce, profile.ForceGain)`. Поля `Radii`, `Weights`, `Motion` и их значения не менять (в том числе `Motion.z`, `Motion.w` остаются нулями).
- В `struct TeamParams` в `BoidsPasses.compute` добавить `float4 Classic;` последним полем. Буфер `BoidReadTeamParams` (три `float4`) **не расширять**.
- `TeamProfile`: поля `MinSpeed` (по умолчанию 4), `MaxSpeed` (8), `MaxForce` (12), `ForceGain` (5). Объявление по образцу `Cruise`/`Turn` (приватное `[SerializeField]`, без `Tooltip` и `Range`, свойство с сеттером): отрицательное значение бросает `ArgumentOutOfRangeException` с `ParamName == "value"`. Старые ассеты десериализуются с этими значениями по умолчанию.
- Единственная допустимая правка существующих тестов: в `TeamProfileTests.cs` тест `Stride_Is48` содержит два ожидания (`TeamParams.Stride` и `Marshal.SizeOf<TeamParams>()`), оба становятся `64`, тест переименовывается в `Stride_Is64`. Больше в `TeamProfileTests` ничего не менять (`AssertSlot` четвёртый `float4` не читает, тест заливки остаётся зелёным). `AreEqual(48, …)` в тестах пресетов это `maxNeighbors`, не трогать. Любая другая правка существующего ожидания, в том числе по разведке А.3, остановка и вопрос.

**Б3. Пасс.** `TeamClassicSteerPass : ParticleKernelPass` в `BoidsPasses.cs`, после `TeamHeadingSteerPass`, `[Serializable]`. `DisplayName` «Team Classic Steer», `Category` как у `TeamHeadingSteerPass`, `KernelName` «TeamClassicSteer». `Reads`: `Heading`, `Velocity`, `TeamId`, `InertialVelocity`. `Writes`: `Heading`, `Velocity`, `InertialVelocity` (новые наборы атрибутов по образцу существующих). `Initialize` требует `context.Teams` (при `null` бросает `InvalidOperationException` с текстом, содержащим «team list», как `TeamHeadingSteerPass`); хэш не требует. `SetParams`: привязать буфер `Teams`, как `TeamHeadingSteerPass`. `RepeatCount` **не переопределять** (остаётся 1).

**Б4. Ядро.** В `BoidsPasses.compute`: строка `#pragma kernel TeamClassicSteer` рядом с остальными и блок **в конце файла** (после `SwarmPointForce`). Больше ничего в файле не менять, кроме строки `float4 Classic;` из Б2. Спецификация (имя буфера `inertialVelocity` совпадает с именем атрибута):

```hlsl
RWStructuredBuffer<float3> inertialVelocity;

float3 TeamClampLen(float3 v, float maxLen)
{
    float lenSq = dot(v, v);
    return (lenSq > maxLen * maxLen && lenSq > 1e-12) ? v * (maxLen * rsqrt(lenSq)) : v;
}

[numthreads(THREADS, 1, 1)]
void TeamClassicSteer(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount) return;

    uint team = min(teamId[id.x], 7u);
    float4 c = Teams[team].Classic;          // (minSpeed, maxSpeed, maxForce, forceGain)
    float minSpeed = c.x;
    float maxSpeed = c.y;
    float maxForce = c.z;
    float gain = c.w;

    float3 h = heading[id.x];
    h.y = 0.0;
    h = UnitOr(h, float3(1, 0, 0));

    float3 a = velocity[id.x] * gain;        // сумма сил кадра
    a.y = 0.0;
    if (maxForce > 0.0) a = TeamClampLen(a, maxForce);

    float3 v = inertialVelocity[id.x];
    if (dot(v, v) < 1e-12) v = h * (0.5 * (minSpeed + maxSpeed));   // старт и Rebuild
    v += a * DeltaTime;
    v.y = 0.0;

    float speed = length(v);
    float3 dir = UnitOr(v, h);
    speed = clamp(speed, minSpeed, maxSpeed);
    v = dir * speed;

    heading[id.x] = dir;
    inertialVelocity[id.x] = v;
    velocity[id.x] = v;
}
```

Формулу и порядок шагов менять нельзя. Допустимо только исправить синтаксис, если не компилируется (сообщить в сдаче). `UnitOr` используется как в `TeamHeadingSteer`: без проверки NaN, без деления на ноль.

**Б5. Валидатор.**
- `SpatialHashValidator.ValidateTeams` устроен так: цикл по профилям с `RequireNonNegative(cruise/turn)`, затем ранний `return` при `passes == null || teams.Count > 0`, затем цикл по пассам, который выполняется только при пустом списке команд. Поэтому:
  1. В существующий цикл по профилям, рядом с `cruise` и `turn`: `MinSpeed`, `MaxSpeed`, `MaxForce`, `ForceGain` не меньше 0 (работает и без нового пасса).
  2. Там же, только если в эффекте включён `TeamClassicSteerPass`: `MaxSpeed > 0` и `MinSpeed <= MaxSpeed` у каждой команды (ошибка называет индекс команды и поле).
  3. Ошибка «включены оба пасса, `TeamHeadingSteerPass` и `TeamClassicSteerPass`»: отдельным проходом по пассам **до** раннего `return`, независимо от числа команд (иначе при пустом списке первой вылетит старая ошибка и новая не проверится). Тест берёт непустой список команд.
  4. Ошибка пустого списка команд: в нижнем цикле, по образцу `TeamHeadingSteer`, с названием «Team Classic Steer».
- `SwarmPointValidator`: `TeamClassicSteerPass` учитывается там же, где `TeamHeadingSteerPass`. Текст предупреждения о порядке называет пасс, который реально стоит в списке: для `TeamHeadingSteerPass` текст остаётся **побайтно прежним** (существующие тесты не меняются), для `TeamClassicSteerPass` вместо «Team Heading Steer» подставляется «Team Classic Steer». Тест на новый текст в `TeamClassicSteerValidatorTests.cs`.
- `PerfArgs`, `FPSDisplay`, `SimulationWorld` и фабрики в `M3DDemoTools` в P9.1 не трогать (они про этот пасс не знают, список пассов в инспекторе собирается через `TypeCache`; фабрики это P9.2). Если разведка А.2 нашла другое место, где пасс обязан быть известен, перечислить его в сдаче до правки.

**Б6. Тесты EditMode.** Новый файл `Assets/Tests/Editor/TeamClassicSteerTests.cs` (GPU, каркас `TeamHeadingSteerTests`: свой `ParticleSet` с атрибутами `Heading`, `Velocity`, `TeamId`, `InertialVelocity`, буфер команд, `SimContext`, ручной диспатч). Если не хватает вспомогательных методов, копировать в новый файл, существующие тест-файлы не менять. Базовая команда: `min 4, max 8, maxForce 12, gain 5`, `heading (1,0,0)`, если не сказано иное.

Допуск для всех сравнений векторов `velocity`, `inertialVelocity` и `heading` в этих тестах `1e-4`: ядро делает `dir * clamp(length(v))` через `rsqrt`, побитовое совпадение не гарантировано, а при масштабе скорости около 6 этот допуск остаётся примерно 2·10⁻⁵ относительных и ошибку формулы или `dt` (порядка 10⁻¹) ловит с запасом. Если сравнение не укладывается в `1e-4`, остановиться и сообщить фактическое расхождение: это признак ошибки, допуск самому не ослаблять.

1. **Границы:** 256 частиц, фиксированный seed, силы в `[−1000, 1000]³` (включая Y), `inertialVelocity` со скоростью 0–20, `dt` ∈ {0,016; 0,1; 1; 10}: всё конечно, `|v| ∈ [4 − 1e-4, 8 + 1e-4]`, `v.y = 0`, `|heading| = 1`.
2. **Инерция:** `inertialVelocity (6,0,0)`, сила `(0,0,1)` при `gain 5`, `dt 0,1`: после шага `v = (6, 0, 0,5)`. Затем `velocity` обнуляется (как `ClearVelocity`), сила 0, второй шаг: `inertialVelocity` не изменился (допуск 1e-4).
3. **Обрезка:** сила `(0,0,10)`, `dt 0,1`: `a` обрезано до 12, `v = (6, 0, 1,2)`. При `maxForce = 0` (без обрезки): `v = (6, 0, 5)`.
4. **`forceGain` линеен:** `gain 2` и сила `F` равны `gain 1` и силе `2F` (допуск 1e-4), без обрезки и без активных границ.
5. **Независимость от разбиения `dt`:** постоянная сила `(0,0,1)`, `gain 1`, `inertialVelocity (6,0,0)`: один шаг `dt 0,2` и два шага `dt 0,1` (`velocity` с силой пересобирается перед каждым) дают одну `v` (допуск 1e-4).
6. **Старт:** `inertialVelocity = 0`, сила 0, `heading (0,0,1)`: `v = (0, 0, 6)`.
7. **Без силы скорость сохраняется:** `inertialVelocity (3,0,4)`, сила 0: `v = (3,0,4)`, `heading = (0,6; 0; 0,8)`.
8. **Антипараллельная сила:** `inertialVelocity (6,0,0)`, сила `(−1000,0,0)`, пять шагов `dt 0,1`: нет NaN, итог `v = (4,0,0)` (нижняя граница), `heading.x > 0`, `heading.z ≈ 0`. Это известное поведение (ADR-054 §2.1), тест нужен, чтобы его не сломали.
9. **Y:** `heading (0,5,1)`, сила `(0,1000,1)`: `v.y = 0`, `heading.y = 0`, `|heading| = 1`, `|v|` в границах.
10. **Команды:** команда 0: `min 4, max 8`; команда 1: `min 10, max 12`. Обе с `inertialVelocity (6,0,0)` и силой 0: скорость 6 и 10. Частица с `teamId = 9` читает слот 7 (образец `directSlot7` в `TeamHeadingSteerTests`).
11. **Вырожденный ввод:** нулевые `heading`, `inertialVelocity` и сила: всё конечно, `heading = (1,0,0)`, `|v| = 6`.
12. **Якорь к `TeamHeadingSteer`:** `min = max = 5`, `maxForce = 0`, `gain = 1e6`, `dt = 1`, сила с `|F.xz| ≥ 1`, `inertialVelocity = 0` (ядро засеет `v = heading · 5`, при `gain 1e6` это внутри допуска; ненулевая старая скорость якорь сорвёт), произвольная `heading`: направление и `|v|` совпадают с `TeamHeadingSteer` при `turn · dt = 1`, `cruise 5` (допуск 1e-4). Взять 4–5 сил, включая одну с большой Y.
13. **Контракт:** `FindKernel("TeamClassicSteer")` находит ядро; среди всех `.compute` библиотеки (`M3DDemoTools.PassLibraryPaths`) ядро с этим именем ровно одно (`ComputeShader.HasKernel`); `#pragma kernel TeamClassicSteer` в `BoidsPasses.compute` ровно один (отдельного существующего теста уникальности нет, этот новый); `RepeatCount == 1`; `Reads` и `Writes` как в Б3; `TeamParams.Stride == 64`; `Upload` кладёт `Classic = (MinSpeed, MaxSpeed, MaxForce, ForceGain)` и не меняет `Motion` (`Motion.z = Motion.w = 0`); `Initialize` без `Teams` бросает исключение с «team list», с `Teams` не бросает и `SpatialHash` остаётся `null`.
14. **Сеттеры:** отрицательное значение любого из четырёх новых полей `TeamProfile` бросает `ArgumentOutOfRangeException` (`ParamName == "value"`).

Новый файл `Assets/Tests/Editor/TeamClassicSteerValidatorTests.cs`: отрицательные поля; `MaxSpeed = 0` при включённом пассе; `MinSpeed > MaxSpeed`; пустой список команд при включённом пассе; оба пасса вместе; выключенный `TeamClassicSteerPass` ничего из этого не вызывает; `SwarmPointValidator` предупреждает о `SwarmPointForce` после `TeamClassicSteer` и молчит при правильном порядке; для `TeamHeadingSteerPass` текст предупреждения прежний. Отрицательные значения через свойство не записать (сеттер бросает): валидаторные тесты пишут в приватное поле через рефлексию, как `NegativeRadiusAndMultiplier_ThrowFromSetterAndFromValidate`. `MinSpeed > MaxSpeed` и `MaxSpeed = 0` через сеттер проходят.

## Часть В. Приёмка

1. **EditMode:** зелёный, число «до» (А.1) и «после» в сдаче, список имён новых тестов. Единственная правка существующих тестов: `Stride_Is48` (два ожидания и имя, Б2).
2. **`git diff --stat`** по коммиту А содержит только: `BuiltinAttributes.cs`, `TeamProfile.cs`, `BoidsPasses.cs`, `BoidsPasses.compute`, `SpatialHashValidator.cs`, `SwarmPointValidator.cs`, `TeamProfileTests.cs` (тест `Stride_Is48`), два новых тест-файла с `.meta`, и пути из разведки А.2 (если нужны, с пояснением).
3. **`BoidsPasses.compute`:** `git diff` показывает ровно три вида правок: строку `float4 Classic;` в структуре, строку `#pragma kernel TeamClassicSteer`, блок в конце файла. Текст walk 0/1/2, `TeamHeadingSteer`, `SwarmPointForce` не затронут. `DynamicsPasses.compute`, `ForcePasses.compute`, `SpatialHashPasses.compute` не затронуты.
4. **Ассеты и сцены:** `git diff` по `Assets/Effects`, `Assets/Scenes` пуст.
5. Все существующие тесты обходов (walk 0/1/2, оракул, тор, кап) зелёные **без** правок ожиданий.

## Часть Г. Документы (коммит Б)

- `ADR-054`: заполнить §8 «Результат» (что сделано, ответы на разведку А, число EditMode до и после, отклонения от ТЗ, хеш головы-базы).
- `DOC/pass-catalog.md` и `DOC/capabilities.md`: запись «Team Classic Steer» (место в кадре: после силы соседей и `SwarmPointForce`, вместо `TeamHeadingSteer`; `velocity` до пасса это сумма сил, после реальная скорость; четыре параметра команды; стартовые числа помечены гипотезами).
- `DOC/architecture.md`: если там описан `TeamParams`, поправить размер на 64 байта и упомянуть `Classic`.
- `DOC/status.md`: строка «Итерация».
- `DOC/last/Techdebt.md`: (1) `BoidReadTeamParams` читает три `float4`, `Classic` не включён; (2) alignment по скорости соседей и сумма separation отложены до P9.4 (ссылки на ADR-054 §6 и ADR-053).

## Что присылает Grok

Хеш головы `feature/reynolds-boids` в момент создания ветки; хеши коммитов 0, А, Б; ответы части А; число EditMode до и после; полный список упавших тестов из А.3; вывод `git diff --stat`; расхождения с ТЗ; вопросы владельцу.

## Не делать

- Не менять `TeamHeadingSteer`, `HeadingSteer`, walk 0/1/2, `SwarmPointForce`, `ClearVelocity`, `Integrate`, `BoxBounds`, хэш-ядра и ресурсы, `SwarmSource`, `PointForce`, `Drag`, `SpeedLimit`.
- Не создавать пресет, сцену, меню, не запускать Play, не подбирать числа (P9.2). Не создавать новый `.compute`.
- Не добавлять препятствия, alignment по скорости, сумму separation (P9.3, P9.4).
- Не расширять `BoidReadTeamParams`, не занимать `Motion.z` и `Motion.w`.
- Не менять существующие `.asset`, сцены и ожидания существующих тестов (кроме `Stride`).
- Не править `DOC/MEASURE/`, прежние `todo-*.md`, ADR других номеров.
- Не делать `merge`, `rebase`, `push`.
- Не учить `PerfArgs`, `FPSDisplay`, `SimulationWorld` и `M3DDemoTools` новому пассу (это P9.2).
