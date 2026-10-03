# ТЗ: ADR-039 P5b — курс и скорость из команды

**Статус:** EditMode зелёный (2026-10-03). Пресета нет.
**Исполнитель:** одна приёмка. Пресет, меню и Play не делать. Силу соседей не менять.

Прочитать [`ADR-039-Team-Heading-Steer.md`](../ADR/ADR-039-Team-Heading-Steer.md) перед кодом. Источник истины по телу кернела — цитата ниже, не пересказ в плане §2.3. Где план короче, делать по этому файлу.

**Не трогать:** `HeadingSteer` (класс и кернел в `DynamicsPasses`), `BoidNeighborForce`, `TeamRadiusPlayCheck`, кернелы `SpatialHashPasses.compute`, `HashCellCoord`, `SwarmSource`, `HashProbe*`, `ParticleBillboard`, `PrimitiveParticleBinder`, `Boids_mk1.asset`, Physarum, fluid, `Test1.unity`. Шесть старых красных EditMode не чинить. `Boids_Rivalry/` только читать. P2 не открывать.

**Git:** не коммитить. Не делать `git add -A`, `git add .`, `git commit -a`, `stash`, `checkout` / `restore` / `reset`. Чужие незакоммиченные правки не трогать.

## 0. Файлы

Новые:

- `Assets/Tests/Editor/TeamHeadingSteerTests.cs`

Менять:

- `Assets/Shaders/GPU/Passes/BoidsPasses.compute` — кернел `TeamHeadingSteer` и копия `UnitOr`.
- `Assets/Scripts/Passes/BoidsPasses.cs` — класс пасса в том же файле, что и сила.
- `Assets/Scripts/Runtime/TeamProfile.cs` — сеттеры `Cruise` и `Turn`.
- `Assets/Scripts/Runtime/SpatialHashValidator.cs` — знак `cruise`/`turn` и вторая строка в уже существующем цикле пустого списка.
- `DOC/status.md`, `DOC/pass-catalog.md`, `DOC/capabilities.md`, `DOC/getting-started.md`.

`SimulationWorld`, `SimPass`, `M3DDemoTools` и шейдер хэша не менять. Путь `BoidsPasses.compute` в библиотеке уже есть. Существующие тесты не править, они остаются зелёными.

## 1. Что фиксируется против короткого плана

- `cruise < 0` и `turn < 0` отвергают и сеттер, и валидатор. Ноль разрешён. Проверки в Play нет.
- `turn == 0` не замораживает буфер. Курс не поворачивается к силе, но Y обнуляется и вектор нормализуется, как в `HeadingSteer`.
- Пасс не реализует `ISpatialHashConsumer` и не требует хэш.
- Полей `TurnSpeed` и `CruiseSpeed` на пассе нет. Числа только из слота команды.
- `UnitOr` копируется в `BoidsPasses.compute`. `DynamicsPasses.compute` не править.

## 2. Знак cruise и turn

В `TeamProfile` сеттеры `Cruise` и `Turn` идут через уже существующий `NonNegative`. Отрицательное значение — `ArgumentOutOfRangeException`, `ParamName` равен `"value"`, как у радиусов. Ноль проходит.

В `ValidateTeams`, в том же цикле, где уже проверяются радиусы и множитель, добавить:

- `RequireNonNegative(profile.Cruise, "cruise")`
- `RequireNonNegative(profile.Turn, "turn")`

Это `ArgumentOutOfRangeException` с `ParamName` `"cruise"` или `"turn"`. Проверка идёт для каждой команды списка, даже если steer выключен и даже если хэша нет. С ячейкой `cruise` и `turn` не сравнивать.

`turn == 0` исключением не является.

## 3. Пустой список

Правило ADR-038 не переписывать. В цикле, который уже бросает на включённом `BoidNeighborForcePass` при `teams.Count == 0`, добавить такую же ветку для включённого `TeamHeadingSteerPass`. Сообщение: `SimulationWorld: Team Heading Steer requires a non-empty team list.`

Цикл по-прежнему стоит после проверки полей команд и не выполняется при `passes == null` или при непустом списке. Ранние выходы валидатора (нет пассов, нет билдера хэша) по-прежнему вызывают `ValidateTeams` с переданным списком, не с `effect.Passes`. При `effect == null` правило молчит. Выключенный steer пустой список не требует.

## 4. Пасс

`[Serializable] public sealed class TeamHeadingSteerPass : ParticleKernelPass` в `BoidsPasses.cs`. `[Serializable]` обязателен: пассы лежат в ассете через `SerializeReference`, как у силы.

| | |
| --- | --- |
| `DisplayName` | `Team Heading Steer` |
| `Category` | `PassCategory.Dynamics` |
| `KernelName` | `TeamHeadingSteer` |
| `Reads` | `Heading`, `Velocity`, `TeamId` |
| `Writes` | `Heading`, `Velocity` |
| `RepeatCount` | не переопределять, остаётся 1 |

Свой массив атрибутов на пассе, как у силы. В `AttrSets` новую константу не добавлять.

`Initialize`: если `context.Teams == null` — `InvalidOperationException` с тем же текстом, что у валидатора. `context.SpatialHash == null` не ошибка. Дальше `base.Initialize`. Своего буфера пасс не заводит и `IDisposable` не реализует.

`SetParams`:

- `BindBuffer` для `context.Teams` под именем `Teams`;
- `SetFloat` для `DeltaTime` через `SimShaderIds.DeltaTime`.

Хэш не биндить. `SetVector4` не добавлять. Живую позицию не читать.

## 5. Кернел

В `BoidsPasses.compute` объявить `RWStructuredBuffer<float3> heading` и `float DeltaTime`. Буферы `velocity`, `teamId` и `Teams` уже есть. `struct TeamParams` не дублировать.

`UnitOr` скопировать дословно, вместе с комментарием. Тело не улучшать и проверку NaN не добавлять.

```hlsl
// Finite-only unit vector. Zero (180° lerp) takes fallback. Does not detect NaN:
// fast-math may fold isnan / (x != x), and Show compiled code already evaluates
// both ternary sides (unconditional div). rsqrt arg is clamped off zero.
float3 UnitOr(float3 v, float3 fallback)
{
    float lenSq = dot(v, v);
    return (lenSq > 1e-12) ? (v * rsqrt(max(lenSq, 1e-12))) : fallback;
}
```

Кернел ниже — `HeadingSteer` (строки 83–103) с четырьмя допустимыми отличиями: чтение слота, `min(teamId, 7)`, `TurnSpeed` → `turn`, `CruiseSpeed` → `cruise`. Пять вызовов `UnitOr` остаются на своих местах: защита `force` (включая вложенный запасной `h`), `h` до блендинга, `blended`, `h` после обнуления Y.

```hlsl
[numthreads(THREADS, 1, 1)]
void TeamHeadingSteer(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount)
    {
        return;
    }

    uint team = min(teamId[id.x], 7u);
    float turn = Teams[team].Motion.y;
    float cruise = Teams[team].Motion.x;

    float3 force = velocity[id.x];
    float3 h = heading[id.x];
    float3 desired = UnitOr(force, UnitOr(h, float3(1, 0, 0)));
    h = UnitOr(h, desired);

    float k = saturate(turn * DeltaTime);
    float3 blended = lerp(h, desired, k);
    h = UnitOr(blended, desired);
    h.y = 0.0;
    h = UnitOr(h, float3(1, 0, 0));

    heading[id.x] = h;
    velocity[id.x] = h * cruise;
}
```

Других отличий от `HeadingSteer` нет. `Motion` сила по-прежнему не читает. Кернел силы не менять.

`turn == 0` даёт `k = 0`. Единичный курс с `y = 0` не поворачивается к силе. Курс с ненулевым Y прижимается к XZ и нормализуется: сравнивать его с `HeadingSteer`, а не с исходным вектором.

## 6. Тесты

Новый `TeamHeadingSteerTests`. Допуск паритета: каждый компонент `heading` и `velocity` отличается от `HeadingSteer` не больше чем на `1e-6`. Оба диспатча получают одни и те же `heading`, `velocity`, `dt`; для этого у каждого диспатча свой `ParticleSet` (или буферы перед вторым диспатчем записываются заново), иначе второй увидит результат первого. Оба compute, `DynamicsPasses` и `BoidsPasses`, лежат в библиотеке контекста. У `HeadingSteer` униформы равны `cruise` и `turn` слота 0. Слот 7, где он нужен, писать прямым `SetData` в элемент 7, не через `TeamParams.Upload`: заливка обнуляет хвост.

| Тест | Ожидание |
| --- | --- |
| Знак | `Cruise = -1` и `Turn = -1` бросают из сеттера. Ноль проходит. Валидатор на профиле с отрицательным `cruise` или `turn` бросает `ArgumentOutOfRangeException`, даже если steer в списке выключен |
| Пустой список | Включённый steer без команд и без хэша бросает, в тексте есть `team list`. Выключенный не бросает. `Validate(list, null)` не бросает. Старый тест силы не менять |
| `turn == 0` | Курс `(1,0,0)`, любая сила, `dt > 0`: курс остаётся `(1,0,0)` в допуске `1e-5`, `velocity = heading * cruise` |
| Изолированная | Одна частица, `velocity = 0`, курс `(1,0,0)`, `turn > 0`: курс не меняется, `\|velocity\| = cruise` |
| Две скорости | Две частицы с `teamId` 0 и 1, курсы одинаковые, `turn = 0`, `cruise` команды 0 равен 2, команды 1 равен 5: длины скорости 2 и 5, курсы не разъехались |
| Антипараллель | Курс `(1,0,0)`, сила `(-1,0,0)`. `turn = 1`, `dt = 0.25` (`k < 0.5`): `heading.x > 0`. `dt = 0.5` и `dt = 0.75` (`k >= 0.5`): `heading.x < 0`. NaN нет, `\|heading\| = 1`, `\|velocity\| = cruise`. Те же входы совпадают с `HeadingSteer` |
| Масштаб силы | Сила `(0,0,1)` и `(0,0,1000)` дают один и тот же курс. Длина скорости равна `cruise`, не длине силы |
| Паритет | На тех же входах результат равен `HeadingSteer` в допуске `1e-6`. Набор: нулевая сила; нулевой `heading`; антипараллельная сила; сила с большой Y; `heading` с большой Y; `k = 1` (`turn * dt >= 1`) |
| Слот 7 | `teamId = 100`. Слот 0: `cruise = 2`. Слот 7 записан напрямую: `cruise = 9`, `turn = 0`. Курс единичный по X. Длина скорости 9, не 2 и не 0 |
| Имена и контракт | `KernelName` равен `TeamHeadingSteer`. `RepeatCount` остаётся 1. `Reads` — `Heading`, `Velocity`, `TeamId`; `Writes` — `Heading`, `Velocity`; `Category` — `Dynamics` |
| Initialize без команд | `Initialize` на контексте с `Teams == null` бросает `InvalidOperationException`, в тексте есть `team list`. Контекст без хэша при непустых `Teams` не бросает |
| Смоук мира | `Swarm` на 4 частицы, одна команда, в списке только `TeamHeadingSteer`, хэша нет, `visualEffect` пустой. `Rebuild` не бросает, частиц 4. В библиотеке достаточно `BoidsPasses.compute` |

После зелёных тестов обновить документы коротко: `status.md` — итерация про ADR-039, EditMode, пресета нет; `pass-catalog.md` — секция `Team Heading Steer` следом за силой соседей, с пометкой «ставится после `ClearVelocity` и силы: `velocity` на входе считается силой, порядок валидатор не проверяет»; `capabilities.md` — одна строка, что курс берётся из команды и пресета нет; `getting-started.md` — пасса в меню нет. ADR-038 и ADR-039 не переписывать. План не раздувать.

В сдаче написать, если паритет на первом прогоне шире `1e-6`: число записать, формулы не подгонять.
