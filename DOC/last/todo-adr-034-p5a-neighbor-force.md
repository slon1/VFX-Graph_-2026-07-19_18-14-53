# ТЗ: ADR-038 P5a — сила соседей

**Статус:** EditMode зелёный (2026-10-03). Steer и пресета нет.
**Исполнитель:** одна приёмка. `TeamHeadingSteer`, пресет, меню и Play не делать.

Прочитать [`ADR-038-Boid-Neighbor-Force.md`](../ADR/ADR-038-Boid-Neighbor-Force.md) перед кодом. План §2.3 и §P5 длиннее этого файла в абзаце про кап. Где они расходятся, делать по ТЗ и написать об этом в сдаче.

**Не трогать:** кернелы `SpatialHashPasses.compute`, `HashCellCoord`, `SwarmSource`, `HeadingSteer` (класс и кернел), `HashProbe*`, `ParticleBillboard`, `PrimitiveParticleBinder`, `Boids_mk1.asset`, Physarum, fluid, `Test1.unity`. Шесть старых красных EditMode не чинить. `Boids_Rivalry/` только читать.

**Git:** не коммитить. Не делать `git add -A`, `git add .`, `git commit -a`, `stash`, `checkout` / `restore` / `reset`. Чужие незакоммиченные правки не трогать.

## 0. Файлы

Новые:

- `Assets/Scripts/Passes/BoidsPasses.cs`
- `Assets/Shaders/GPU/Passes/BoidsPasses.compute`
- `Assets/Scripts/Runtime/TeamRadiusPlayCheck.cs` — повторная проверка радиуса. Мир только вызывает её.
- `Assets/Tests/Editor/BoidNeighborForceTests.cs`

Менять:

- `Assets/Scripts/Runtime/SimPass.cs` — хук `OnDispatched` в `ParticleKernelPass`. Отдельного файла класса нет.
- `Assets/Scripts/Runtime/SimulationWorld.cs` — вызов проверки после заливки команд.
- `Assets/Scripts/Runtime/SpatialHashValidator.cs` — пустой список у пасса, который читает `Teams`.
- `Assets/Shaders/GPU/Includes/SpatialHash.hlsl` — только `MinImage`.
- `Assets/Scripts/Editor/M3DDemoTools.cs` — путь compute в `PassLibraryPaths`.
- `DOC/status.md`, `DOC/pass-catalog.md`, `DOC/capabilities.md`, `DOC/getting-started.md`.

Хук пустой для всех старых пассов. Существующие тесты не править, они остаются зелёными.

---

## 1. Что меняется против короткого плана

- Пустой `teams` у пасса, который читает `context.Teams`, — ошибка валидатора. В 5a в списке только сила. Steer добавится в 5b одной строкой.
- В Play радиус снова сравнивается с ячейкой, один раз пишется предупреждение. Кернел радиус не режет.
- Индекс команды в кернеле силы — `min(teamId, 7)`, не `min(teamId, count - 1)`. В steer ту же строку копирует 5b.
- Раскладка `TeamParams` проверяется чтением из кернела, включая `Motion`, не только `Marshal.SizeOf`.
- `cruise`, `turn` и `turn == 0` в 5a не делать.
- `SetVector4` в `ParticleKernelPass` не добавлять.

## 2. Пасс

Новый `Assets/Scripts/Passes/BoidsPasses.cs`.

`BoidNeighborForcePass : ParticleKernelPass, ISpatialHashConsumer, IDisposable`.

| | |
| --- | --- |
| `DisplayName` | `Boid Neighbor Force` |
| `Category` | `PassCategory.Force` |
| `KernelName` | `BoidNeighborForce` |
| `Reads` | `Position`, `TeamId` |
| `Writes` | `Velocity` |
| `RepeatCount` | не переопределять, остаётся 1 |

`maxNeighbors`, `int`, по умолчанию 48. Сеттер: значение `< 0` — `ArgumentOutOfRangeException`. Ноль значит «без капа».

`countCapHits`, `bool`, по умолчанию `false`.

`Initialize`:

- если `context.SpatialHash == null` — `InvalidOperationException`;
- если `context.Teams == null` — `InvalidOperationException`;
- создать `GraphicsBuffer` `uint[1]` для счётчика капа и запомнить его.

`SetParams` биндит:

- `context.Teams` как `Teams`;
- буферы снимка `SortedPositions`, `SortedDirections`, `SortedTeams`, `CellStarts`, `CellCounts` (имена HLSL те же);
- `hash.PushParams` на шейдер кернела;
- `maxNeighbors`;
- флаг счётчика капа.

Живой `heading` не биндить и не читать. `SetVector4` не заводить: в базе уже есть `BindBuffer`.

`Execute` у `ParticleKernelPass` закрыт. Добавить в него один вызов `protected virtual void OnDispatched(SimContext context)` сразу после `DispatchCompute`, до выхода. Пустая реализация в базе. Сила переопределяет его и только там ставит `RequestAsyncReadback`. Обнуление счётчика — в `SetParams`, до диспатча. При `countCapHits == false` счётчик не обнулять и readback не ставить.

`Dispose` делает по порядку: помечает пасс освобождённым и поднимает поколение (поздние колбэки в окно не попадают); если запросов в полёте больше нуля, вызывает `AsyncGPUReadback.WaitAllRequests()`; затем освобождает счётчик один раз. Ждать колбэки отдельно не нужно. Так буфер не освобождается под читающим его запросом. Если `WaitAllRequests` в редакторе даёт проблему, написать в сдаче, не искать обход. Мир уже вызывает `Dispose` у таких пассов.

Шейдер добавить в `M3DDemoTools.PassLibraryPaths`. Сцены пробников не пересобирать.

## 3. Кернел

Новый `Assets/Shaders/GPU/Passes/BoidsPasses.compute`. В `Assets/Shaders/GPU/Includes/SpatialHash.hlsl` добавить только `MinImage`. `HashCellCoord` не менять.

```hlsl
struct TeamParams
{
    float4 Radii;
    float4 Weights;
    float4 Motion;
};

float2 MinImage(float2 d)
{
    if (HashWrap == 0)
    {
        return d;
    }

    return d - HashSize * round(d / HashSize);
}
```

Другого имени у структуры нет. `Radii.x/y/z` — separation / alignment / cohesion, `Radii.w` — множитель между командами. `Weights.x/y/z` — веса sep / align / coh, `Weights.w` — 0. `Motion.xy` — cruise / turn, их сила не читает; тест раскладки всё равно сверяет все три `float4`.

На частицу `i`:

1. Свой номер и номер соседа — оба после `min(teamId, 7u)`, и только потом сравнение «своя / чужая». Свои параметры читать из `Teams[свой номер]`. Три радиуса брать из профиля как есть, без `min` с ячейкой.
2. Своя позиция — `position[i].xz`. Свой курс не читать.
3. Девять ячеек вокруг своей, порядок со сдвигом `i % 9`. При `HashWrap != 0` координата ячейки по модулю `HashRes`, тем же приёмом, что `HashCellCoord`. При `HashWrap == 0` ячейка вне сетки пропускается до `HashCellIndex`: иначе клэмп индекса подтянет чужую последнюю ячейку.
4. Слоты ячейки — `[CellStarts, CellStarts + CellCounts)`. Для чужого слота `d = MinImage(sortedPos - self)`, `r2 = dot(d, d)`.
5. `r2 < 1e-6` — это сама частица: пропуск, и в кап она не входит.
6. Перед разбором слота: если `maxNeighbors > 0` и посещённых уже `maxNeighbors`, остановиться. Это и есть срабатывание капа: при включённом счётчике один `InterlockedAdd` на частицу. Иначе слот считается посещённым, затем идут отсечки радиуса.
7. `r2 >= max(sep, align, coh)²` — к силе не добавлять.
8. Иначе формулы ниже. Сравнение со **квадратом** длины: `dot(v, v) > 1e-6`, то есть длина больше `1e-3`. В плане §2.3 написано `|align| > eps` при том же `eps`, то есть длина больше `1e-6`. Эталон и кернел пишутся по этому ТЗ, не по той фразе плана. Энтропию и `dirSum` не считать.
9. `velocity[i] += float3(force.x, 0, force.y)`. Не заменять вектор.

`d` — `MinImage` от позиции соседа минус своей, `ndir` — его `SortedDirections`. `inter` — `Radii.w` своей команды. `u` и `t` — номера после `min(..., 7)`.

```
sep  += (-d / (r2 + 1e-3)) * (u == t ? 1 : inter);  sepN++;     // если r2 < sepR^2
align += ndir;  alignN++;                                        // только u == t и r2 < alignR^2
coh  += d;      cohN++;                                          // только u == t и r2 < cohR^2
force = (sepN > 0 ? sep / sepN * wSep : 0)
      + (dot(align, align) > 1e-6 ? normalize(align) * wAlign : 0)
      + (cohN > 0 && dot(coh / cohN, coh / cohN) > 1e-6 ? normalize(coh / cohN) * wCoh : 0);
```

Чужие: separation с множителем, alignment и cohesion только для своей команды. Курс соседа — `SortedDirections`, как есть, без повторной нормализации до суммы.

Второй кернел `BoidReadTeamParams`: слот 0 раскладывает в `RWStructuredBuffer<float4>` длины 3 (`Radii`, `Weights`, `Motion`). В симуляции не вызывается.

## 4. Счётчик капа

Пока `countCapHits == false`, `LastCapHitFraction` равен `-1`, буфер счётчика не меняется.

Пока `true`:

1. В `SetParams` обнулить `uint[1]` через `SetBufferData` на том же command buffer.
2. Диспатч силы.
3. В `OnDispatched` — `RequestAsyncReadback` этого буфера. Если в полёте уже 4 запроса, кадр пропустить.
4. В колбэке сначала уменьшить счётчик полёта. Дальше выйти, ничего не добавляя в окно, если `request.hasError`, либо поколение не совпало с запомненным при постановке, либо пасс уже освобождён. Поколение растёт в `Initialize`, `Dispose` и `ResetCapHitWindow`.
5. Иначе доля — значение счётчика, делённое на `Count` того кадра. Окно — последние 60 долей. `LastCapHitFraction` — их среднее. Пока ни одной доли нет, свойство равно `-1`.

`ResetCapHitWindow` очищает окно, ставит `-1` и поднимает поколение, чтобы старый колбэк в окно не попал.

## 5. Валидатор и проверка в Play

В `ValidateTeams`, после уже существующих проверок радиуса на Build:

Список типов, которые читают `context.Teams`. В 5a в нём один элемент: включённый `BoidNeighborForcePass`. Если список не пуст и `teams.Count == 0` — `InvalidOperationException`. Выключенный пасс не считается. `cruise` и `turn` не смотреть. В 5b в этот список добавляется `TeamHeadingSteerPass`, других правок правила нет.

Проверка радиуса на Build не меняется. Класс `TeamRadiusPlayCheck` хранит уже выданные предупреждения. Ключ — номер команды и какой из трёх радиусов (один ключ на радиус). Для ключа хранится последнее значение, о котором предупредили. Нарушение (`value > min(cell) * (1 + GridEpsilon)`, равенство не нарушение) даёт текст, если значение отличается от запомненного, и `null`, если то же. Любая смена числа заменяет запомненное: последовательность 6 → 7 → 6 даёт три предупреждения, а два вызова подряд с 6 — одно. Возврат в допуск ключ забывает. Метод принимает список команд и меньшую сторону ячейки (допуск применяется внутри), возвращает один текст на все новые нарушения кадра или `null`. Исключений нет. Состояние лежит на мире, `Teardown` его забывает.

`SimulationWorld.Update` вызывает его после заливки `Teams` и до цикла пассов, если есть `context.SpatialHash` и включённая сила. Непустой текст — один `Debug.LogWarning`. Мир состояние проверки не дублирует.

## 6. Тесты

Новый `Assets/Tests/Editor/BoidNeighborForceTests.cs`. Старые файлы тестов не править. GPU-тесты гонять как `SpatialHashGpuTests`: свой `ParticleSet`, свой `SimContext`, шейдеры хэша и силы, `Graphics.ExecuteCommandBuffer`. Эталон `BoidForceOracle` живёт в тестовом файле и повторяет §3.

Допуск к эталону, по компонентам: `abs(gpu - cpu) <= 1e-4 + 1e-4 * abs(cpu)`. Если первый прогон чуть шире, формулы не подкручивать: написать число в сдаче.

Позиции эталона держать хотя бы на `1e-2` дальше всех трёх радиусов и от `size/2`, кроме теста шва.

| Тест | Проверка |
| --- | --- |
| Раскладка | `Upload` известных `Radii` / `Weights` / `Motion`, диспатч `BoidReadTeamParams`, `GetData`. Три `float4` совпадают побитово |
| Эталон | Около 200 частиц, 2 команды, `maxNeighbors = 0`, фикс. seed. После `ClearVelocity` и силы GPU совпадает с оракулом. Хотя бы одна пара чужих команд, множитель не 1 |
| Ноль капа | Отдельный прогон `maxNeighbors = 0`: есть сосед внутри радиуса, сила ненулевая |
| Шов | Две частицы по разные стороны границы Wrap. Сила как у оракула через `MinImage` |
| Клэмп номера | Слот 7 записать напрямую, не через `Upload`: у него другие веса, чем у команды 0. Частица с `teamId = 100` получает силу слота 7, не слота 0 и не ноль. Оракул тоже берёт номер после `min(id, 7)` |
| Без тора | `wrap = false`. Частица в углу сетки, другая у противоположного края. Силы нет: ячейка за границей пропускается, а не заворачивается. Отдельный эталон с `wrap = false` и соседями внутри сетки совпадает с GPU |
| Имена кернелов | `BoidNeighborForce` и `BoidReadTeamParams` находятся рефлексией, как в существующих контракт-тестах |
| Кап без смещения | Симметричная конфигурация, `maxNeighbors = 1`. Все силы конечны. Длина средней силы меньше половины средней длины отдельной силы. Если первый прогон шире, формулы не менять: записать число в сдаче |
| Dispose | `Dispose` при запросе в полёте не бросает и не меняет окно |
| Кап выключен | `countCapHits == false`. После диспатча `LastCapHitFraction == -1`. Буфер счётчика не стал нулём, если его заранее записать единицей |
| Кап ноль | `countCapHits == true`, `maxNeighbors = 0`, дождаться readback. Доля 0 |
| Кап плотный | `maxNeighbors = 1`, много частиц в одной ячейке, флаг включён. Доля около 1. Все компоненты `velocity` конечны |
| Сброс окна | `ResetCapHitWindow` при запросе в полёте не пускает старую долю в окно: после сброса и до нового замера свойство `-1` |
| Пустые команды | Включённая сила и `teams.Count == 0` — `InvalidOperationException`. Выключенная сила с пустым списком — нет |
| Радиус в Play | Вызов `TeamRadiusPlayCheck`, не `Update`. Два вызова с одним и тем же слишком большим значением дают одно предупреждение. Последовательность 6 → 7 → 6 даёт три. Значение ровно на пороге предупреждения не даёт. Несколько новых нарушений в одном вызове возвращаются одним текстом. Радиус внутри ячейки предупреждения не даёт. После возврата в допуск следующее нарушение предупреждает снова |
| Повтор | `RepeatCount == 1` |

Ожидание readback в тесте — `AsyncGPUReadback.WaitAllRequests` после исполнения command buffer. Второй запрос из теста не ставить.

Смоук мира: неактивный хост, Swarm, одна команда, спавн `teamIndex = 0`, count 4, пассы `BuildSpatialHash` (Wrap, границы как у `BoxBounds`), `ClearVelocity`, `BoidNeighborForce`, `BoxBounds` Wrap. Библиотека — оба compute. Без `VisualEffect`. `Rebuild` не бросает, частиц 4. Картинку не проверять.

## 7. Документы

Коротко:

- `DOC/status.md` — ADR-038, EditMode, стаи на экране нет.
- `DOC/pass-catalog.md` — раздел пасса `Boid Neighbor Force`: читает снимок, пишет `velocity +=`, соседей за ячейкой не ищет.
- `DOC/capabilities.md` — одна строка: сила соседей есть, steer по командам нет.
- `DOC/getting-started.md` — пасса в меню нет.

ADR-034, ADR-037 и ADR-038 не переписывать. План не расширять.

## 8. Готово, когда

- Тесты из §6 зелёные.
- `HeadingSteer` и кернелы хэша не изменены.
- Нет пресета, меню и `TeamHeadingSteer`.
- Коммита нет.
