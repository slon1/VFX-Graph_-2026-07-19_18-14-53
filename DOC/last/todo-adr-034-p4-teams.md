# ТЗ: ADR-037 P4 — команды без палитры

**Статус:** EditMode зелёный (2026-10-03). Сила буфер не читает.
**Исполнитель:** одна фаза. Силу соседей, steer по командам, палитру, гистограмму хэша и пресет не делать.

Прочитать [`ADR-037-Team-Profile.md`](../ADR/ADR-037-Team-Profile.md) перед кодом. План P4 в [`plan-Reynolds-Boids-v2.md`](../plan-Reynolds-Boids-v2.md) короче этого файла. Где они расходятся, делать по ТЗ и написать об этом в сдаче.

**Не трогать:** `SwarmSource` (верхнюю границу `teamIndex` не добавлять), кернелы и шейдеры хэша, `HashProbe*`, `ParticleBillboard`, `PrimitiveParticleBinder`, `HeadingSteer`, `Boids_mk1.asset`, Physarum, fluid-пассы, `Test1.unity`. Не открывать `Test1` на запись. `Boids_Rivalry/` только читать.

**Git:** не коммитить. Не делать `git add -A`, `git add .`, `git commit -a`, `stash`, `checkout` / `restore` / `reset`. Чужие незакоммиченные правки не трогать.

---

## 1. Профиль

Новый файл `Assets/Scripts/Runtime/TeamProfile.cs`. `[Serializable]`.

| Поле | Тип | Дефолт |
| --- | --- | --- |
| `name` | `string` | `""` |
| `separationRadius` | `float` | `0` |
| `alignmentRadius` | `float` | `0` |
| `cohesionRadius` | `float` | `0` |
| `interGroupSeparationMultiplier` | `float` | `1` |
| `separationWeight` | `float` | `0` |
| `alignmentWeight` | `float` | `0` |
| `cohesionWeight` | `float` | `0` |
| `cruise` | `float` | `0` |
| `turn` | `float` | `0` |
| `color` | `Gradient` | новый `Gradient` |

Публичные get/set. Радиус `< 0` и множитель `< 0` — `ArgumentOutOfRangeException` в сеттере. Ноль разрешён. Веса, `cruise`, `turn` и `color` по знаку не проверять.

`public const int MaxTeams = 8`.

## 2. Ассет и инспектор

`Assets/Scripts/Runtime/EffectAsset.cs`:

```csharp
[SerializeField] private List<TeamProfile> teams = new List<TeamProfile>();
```

Геттер не возвращает `null`: если поле `null` после загрузки старого YAML, считать его пустым списком.

`SetTeams(IReadOnlyList<TeamProfile> value)` заменяет список новой копией. Профили внутри остаются теми же объектами. Аргумент `null` очищает список. `EditorConfigure` не менять: у существующих вызовов сигнатура та же, список остаётся пустым.

`Assets/Scripts/Editor/EffectAssetEditor.cs`: `PropertyField` списка `teams` всегда, после переключателя вида источника, не внутри `case`. Пустой список виден у куба так же, как у Swarm.

## 3. `TeamParams` и заливка

В том же файле, `[StructLayout(LayoutKind.Sequential)]`:

| Поле | Содержимое |
| --- | --- |
| `Radii` | sep, align, coh, interGroupSeparationMultiplier |
| `Weights` | sep, align, coh, `0` |
| `Motion` | cruise, turn, `0`, `0` |

`public const int Stride = 48`. `Marshal.SizeOf<TeamParams>()` равен 48. Если три `Vector4` дают другой размер, упаковать те же 12 float так, чтобы `SizeOf` стал 48. Смысл трёх групп не менять. Другой stride не сдавать.

`TeamParams.Upload(GraphicsBuffer buffer, IReadOnlyList<TeamProfile> teams)` пишет ровно 8 слотов. Хвост после `teams.Count` — нули. `teams == null` — восемь нулей. Длина буфера не 8 — `ArgumentException`. Цвет и имя в структуру не класть.

## 4. Мир

`SimContext.Teams` — `GraphicsBuffer`, `internal set`. В конструктор не добавлять.

`SimulationWorld`:

- Поле буфера рядом с `touchBuffer`. Скретч `TeamParams[8]` хранить на мире и переиспользовать в `Update`, не выделять массив каждый кадр.
- В существующем `try` вызов становится `Validate(effect.Passes, effect)`. Одноаргументный вызов оставить рабочим.
- Буфер создавать после `new SimContext` (это уже после `AutoRegisterAttributes`), только если `effect.Teams.Count > 0`: `GraphicsBuffer` Structured, длина 8, stride `TeamParams.Stride`. Сразу `Upload` и `context.Teams =` этот буфер. При пустом списке буфер не создавать, `context.Teams` оставить `null`.
- В `Update`, если буфер есть, `Upload` каждый кадр до пассов. Значения уже лежащих в списке профилей меняются без Rebuild.
- В `Teardown` освободить буфер один раз рядом с `touchBuffer` и обнулить поле. Повторный `Dispose` через `context.Teams` не делать.

Первый профиль у эффекта, который собрался с пустым списком, попадает в буфер только на следующем Build. Это не лечить созданием буфера в `Update`.

## 5. Валидатор

`SpatialHashValidator.Validate(IReadOnlyList<SimPass> passes, EffectAsset effect = null)`.

При `effect == null` проверки команд нет. Старые тесты валидатора не править.

Сейчас метод выходит раньше конца в двух местах: `passes == null` и «билдера нет». Проверку команд вызывать и на этих выходах, иначе девять команд без билдера пройдут. Исключение, которое хэш уже бросает (второй билдер, потребитель без билдера, сетка, `BoxBounds`), по-прежнему случается раньше команд. Выключенный билдер не считается.

Проверку плана §2.2 `maxRadius <= size/2` в P4 не добавлять. Это инвариант сетки хэша из P1, не радиусы команд.

При эффекте:

1. `teams.Count > 8` — `InvalidOperationException`.
2. `null`-элемент списка — `InvalidOperationException`.
3. Радиус `< 0` или множитель `< 0` — `ArgumentOutOfRangeException`. Инспектор пишет поле мимо сеттера, поэтому проверка нужна и здесь.
4. Если `ResolveSource()` — `SwarmSource`: у каждого спавна `teamIndex < teams.Count`. Иначе `InvalidOperationException` с номером и длиной списка. `null`-спавн — `InvalidOperationException`. Другие виды источника по `teamIndex` не смотреть.
5. Если список не пуст и среди включённых пассов есть `BuildSpatialHashPass`: взять `SpatialHashSet.ComputeLayout` этого билдера. Порог — `Mathf.Min(cell.x, cell.y) * (1f + SpatialHashSet.GridEpsilon)`. Каждый из трёх радиусов, который больше порога, — `InvalidOperationException` с именем радиуса, значением и порогом. Множитель с ячейкой не сравнивать. Билдера нет — радиусы не проверять.

Порядок относительно сборки мира не менять: `source.Setup` как сейчас идёт раньше валидатора. Верхнюю границу в `SwarmSource` не дублировать.

## 6. Тесты

Новый `Assets/Tests/Editor/TeamProfileTests.cs`. Существующие файлы тестов не править, кроме одной фикстуры ниже.

| Тест | Проверка |
| --- | --- |
| Упаковка | Два профиля с разными числами. Буфер длины 8, `Upload`, `GetData`. Слоты 0 и 1 совпадают с полями, слот 2 нулевой. Цвет в данные не попал. `Dispose` буфера в конце |
| Stride | `Marshal.SizeOf<TeamParams>() == 48` |
| Девятая команда | `Validate` бросает `InvalidOperationException` |
| Восемь команд без билдера | Исключения нет, даже если радиус большой |
| Номер Swarm | `teamIndex == teams.Count` бросает. `teamIndex == count - 1` не бросает. Два спавна с одним номером не бросают |
| Чужой источник | Куб с непустым списком команд валидатор по номерам не отклоняет |
| Радиус | Билдер с `extents (10, 0, 10)` и `minCellSize` 3: на этой сетке `cell` не равен `minCellSize`. Порог — `min(layout.CellSize) * (1 + GridEpsilon)` из `ComputeLayout`. Такой радиус проходит. `min(layout.CellSize) * (1 + 1e-4)` бросает. Сравнение с литералом `minCellSize` тест не проходит |
| Множитель | Большой `interGroupSeparationMultiplier` при радиусах внутри ячейки не бросает |
| Отрицательные | Сеттер радиуса и сеттер множителя бросают `ArgumentOutOfRangeException`. То же значение, записанное в приватное поле мимо сеттера, бросает из `Validate` |
| Пустой список | `Validate(passes, effect)` с пустым `teams` не добавляет ошибок команд. `SetTeams(null)` даёт пустой список, геттер не `null` |

Смоук мира, по образцу `SwarmSourceWorldTests`: объект неактивен до `AddComponent`, в памяти `EffectAsset`, `EditorConfigure` со Swarm и одним `IntegratePass`, библиотека `DynamicsPasses.compute`, без `VisualEffect`. `SetTeams` из одного профиля. На источнике спавн `teamIndex = 0`, count 4. `Rebuild` не бросает, `particles.Count == 4`. В `TearDown` уничтожить объект и ассет.

`SwarmSourceWorldTests.Rebuild_CopiesRestToPosition` после этой фазы собирает Swarm с `teamIndex` 0 и 1 при пустом `teams`, и валидатор обязан это отвергнуть. Перед `Rebuild` вызвать `SetTeams` из двух профилей с нулевыми радиусами. Утверждения теста не менять: по-прежнему 10 частиц и совпадение позиции 0 с `restPosition`. Других правок этого файла нет. Красным его не оставлять и в сдаче не называть ожидаемо красным.

Отдельный смоук не нужен для чтения буфера из мира: заливка проверяется прямым `Upload`.

## 7. Документы

Коротко:

- `DOC/status.md` — ADR-037, EditMode, буфер команд есть, сила его не читает.
- `DOC/getting-started.md` — у эффекта есть список `teams`, максимум 8. Цвет хранится и не используется. Меню нет.
- `DOC/capabilities.md` — одна строка: список команд на эффекте, без силы соседей.

Новый раздел пассов в `pass-catalog.md` не заводить. ADR-034, ADR-036 и ADR-037 не переписывать. План не расширять.

## 8. Готово, когда

- Тесты из §6 зелёные.
- Эффект без списка команд собирается как раньше, буфер не создаётся.
- Нет нового пасса, `.compute`, шейдера и пресета.
- Коммита нет.
