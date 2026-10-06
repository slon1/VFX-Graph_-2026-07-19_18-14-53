# ТЗ: ADR-040 P5c — отладка хэша, пресет Boids_hash, Play-гейт

**Статус:** закрыт (2026-10-07). EditMode зелёный (2026-10-03). Порог доли cap опровергнут замером и снят (ADR-040 §4), на диске 3000/48. Вид принят владельцем.
**Исполнитель:** две приёмки подряд. **Часть A** (отладка хэша, P2) сдаётся и принимается до начала **части B** (пресет и Play). Часть B без «принято» по A не начинать.

Прочитать [`ADR-040`](../ADR/ADR-040-Boids-Hash-Preset-And-Play-Gate.md) перед кодом. План §P2 и §P5 короче этого файла. Где они расходятся, делать по ТЗ и написать об этом в сдаче. Известное расхождение: план называет 10 000 частиц, ТЗ даёт 3000 (арифметика в ADR-040).

**Не трогать:** `HeadingSteer`, `BoidNeighborForce`, `TeamHeadingSteer` (классы и кернелы), `TeamRadiusPlayCheck`, `SpatialHashPasses.compute`, `HashCellCoord`, `SwarmSource`, `HashProbe*` (ассеты, сцены, `HashProbeControls`), `ParticleBillboard`, `PrimitiveParticleBinder`, `Boids_mk1.asset`, Physarum, fluid, `Test1.unity`, `EditorBuildSettings`. `FieldDebugQuadsBinder` и `SimulationWorldTestCleanup` не менять. `Boids_Rivalry/` только читать. P6 (палитра команд) не открывать. План не править.

**Git:** не коммитить. Не делать `git add -A`, `git add .`, `git commit -a`, `stash`, `checkout` / `restore` / `reset`. Чужие незакоммиченные правки (`Boids_mk1.asset`, `Physarum_Fluid.asset`, `Test1.unity`, `Mobile_RPAsset.asset`, `UniversalRenderPipelineGlobalSettings.asset`) не трогать.

**Красные тесты.** Тикет шести старых красных закрыт, база: весь EditMode 274/274. Гейт «весь EditMode» = все зелёные. Новые тесты в фикстурах с `SimulationWorld` разбирают хост через `SimulationWorldTestCleanup.DestroyHost` в `TearDown`.

---

## Часть A. Отладка хэша (P2)

### A0. Файлы

Новые:

- `Assets/Scripts/Passes/HashDebugPasses.cs`
- `Assets/Shaders/GPU/Passes/HashDebugPasses.compute`
- `Assets/Tests/Editor/HashCountsToFieldTests.cs`

Менять:

- `Assets/Scripts/Editor/M3DDemoTools.cs` — только путь `HashDebugPasses.compute` в `PassLibraryPaths`.
- `DOC/status.md`, `DOC/pass-catalog.md`, `DOC/capabilities.md`, `DOC/getting-started.md`.

`SimulationWorld`, `SimPass`, `SpatialHashValidator`, `SpatialHash.hlsl` не менять. Существующие тесты не править.

### A1. Пасс

`[Serializable] public sealed class HashCountsToFieldPass : FieldKernelPass, ISpatialHashConsumer` в `HashDebugPasses.cs`.

| | |
| --- | --- |
| `DisplayName` | `Hash Counts To Field` |
| `Category` | та же, что у ближайшего пасса, пишущего скалярное поле без чтения частиц (смотреть `SeedScalarDiskPass`) |
| `KernelName` | `HashCountsToField` |
| `FieldName` | свойство, по умолчанию `"hashCount"` |
| `FieldWrites` | одно поле, `FieldAccess.WriteInPlace`, `FieldSemantic.Scalar`, 1 канал (через `FieldRequestSets.Single` с кэшем, как у соседей) |

`Initialize`:

- `context.SpatialHash == null` — `InvalidOperationException`;
- `base.Initialize(context)` — после нашей проверки, чтобы текст про хэш был первым;
- разрешение поля не равно `hash.Layout.Resolution` — `InvalidOperationException`, в тексте есть имя поля и оба разрешения;
- размер поля (`Descriptor.Size`) отличается от `hash.Layout.Size` больше чем на `1e-3` по любой оси — `InvalidOperationException`.

`SetParams`: `BindBuffer` счётчиков ячеек под именем `CellCounts` (буфер хэша уже internal, пасс в той же сборке), `hash.PushParams` на шейдер кернела. Живые частицы не читать.

Если в `FieldKernelPass` нет готового `BindBuffer`, использовать то, что есть для буферов в `SimPass`/`ParticleKernelPass`. Новых методов в базе не заводить: если без них нельзя, остановиться и написать в сдаче.

### A2. Кернел

`HashDebugPasses.compute`: `#pragma kernel HashCountsToField`, `FIELD_THREADS` 8 (как в `FieldPasses.compute`), включения и объявления униформ скопировать с кернела `DecayFieldScalar` (`FieldPasses.compute`), включая имя записываемой текстуры. Скаляр `RWTexture2D<float>`.

Тело:

- выход за `FieldResolution` — `return`;
- `uint index = id.y * HashRes.x + id.x` (формат индекса тот же, что `HashCellIndex`, см. `SpatialHash.hlsl`); `index` не меньше `HashCellCount` — записать 0;
- `FieldWrite[id.xy] = (float)CellCounts[index];`

Значение пишется, не прибавляется. Масштаба нет: цвет на экране задаёт `colorScale` квада.

Ось поля U совпадает с X мира, ось V с Z. Эта ориентация задаётся полем, не кернелом.

### A3. Тесты

`HashCountsToFieldTests`. GPU-тесты как `SpatialHashGpuTests`: свой `ParticleSet`, свой `SimContext` с хэшем и полем, оба compute, `Graphics.ExecuteCommandBuffer`.

| Тест | Ожидание |
| --- | --- |
| Гистограмма | 2000 частиц со случайными XZ в мире (фикс. seed), сетка с полем того же разрешения. После билдера и пасса поле равно CPU-гистограмме по ячейкам, **точное равенство** целых. Сумма поля равна числу частиц |
| Перезапись | Два прогона подряд: между ними часть частиц перенесена в другие ячейки. Второй прогон даёт гистограмму второго положения, без остатка первого |
| Разрешение | Поле другого разрешения — `InvalidOperationException` в `Initialize`, в тексте есть имя поля |
| Размер | Поле того же разрешения, но другого размера — `InvalidOperationException` |
| Без хэша | `Initialize` на контексте без `SpatialHash` бросает, текст про хэш |
| Порядок | Включённый пасс без `BuildSpatialHashPass` выше — ошибка существующего валидатора (потребитель без билдера). Новый код валидатора не нужен |
| Контракт | Кернел `HashCountsToField` находится рефлексией, `FieldName` по умолчанию `"hashCount"` |

Эталон гистограммы использует ту же формулу ячейки, что `SpatialHashGpuTests`; частицы на границах ячеек не ставить (зазор `1e-2`).

### A4. Документы

Коротко: `status.md` — итерация про часть A ADR-040, EditMode, поля в пресете ещё нет; `pass-catalog.md` — секция `Hash Counts To Field`; `capabilities.md` — одна строка; `getting-started.md` — пасса в меню нет. ADR и план не переписывать.

### A5. Сдача A

Тесты `HashCountsToField` зелёные, весь EditMode зелёный (274 + новые). После «принято» — часть B.

---

## Часть B. Пресет и Play-гейт

### B0. Файлы

Новые:

- `Assets/Tests/Editor/BoidsHashPresetTests.cs`
- `Assets/Effects/Boids_hash.asset` и `Assets/Scenes/Boids_Hash.unity` (создаёт меню, вместе с `.meta`)
- `DOC/last/play-5c-boids-hash.md` — журнал Play-гейта

Менять:

- `Assets/Scripts/Editor/M3DDemoTools.cs` — меню, пути, создатель ассета и сцены.
- `DOC/status.md`, `DOC/pass-catalog.md`, `DOC/capabilities.md`, `DOC/getting-started.md`.

`SimulationWorld`, `SimPass` и шейдеры не менять.

### B1. Меню и пресет

`[MenuItem("Tools/M3D/Create Boids Hash Effect")]`. Создаёт ассет и сцену по образцу `CreateSpatialHashProbe`: `SaveCurrentModifiedScenesIfUserWantsTo`, новая сцена `NewSceneSetup.DefaultGameObjects`, сохранение по пути. **Открытую сцену не использовать и не сохранять** (никакого `FindAnyObjectByType<SimulationWorld>` в открытой сцене, никакого `SaveOpenScenes` по ней). Повторный запуск пересоздаёт ассет, как делают остальные создатели.

Ассет `Boids_hash.asset`: источник `Swarm`, `simulationSpeed = 1`.

| Что | Значение |
| --- | --- |
| Спавн | один: `teamIndex = 0`, `count = 3000`, центр `(0,0)`, радиус `44`, направление `(0,0)` (случайное), `seed = 1` |
| Команда 0 | имя `Swarm`, радиусы sep / align / coh `3 / 3 / 3`, множитель `4`, веса `1,2 / 0,8 / 0,6`, `cruise = 6`, `turn = 4` |
| Частицы | `particleSize = 0,5`, `particleValueScale = 1`, `particleGradient = DebugFieldQuadSlot.DefaultFireGradient()` |
| Поле | `hashCount`, скаляр, разрешение `30×30`, размер `90×90`, центр мира по X и Z, по Y `-1` |
| Debug-квад | `DebugFieldQuadSlot.Density("hashCount")`, `colorScale = 0,05` (подкрутить так, чтобы 10–30 частиц в ячейке читались) |

Пассы, в этом порядке:

1. `BuildSpatialHashPass`: центр `(0,0,0)`, половинные размеры `(45,0,45)`, `minCellSize = 3`, `wrap = true`
2. `HashCountsToFieldPass` (`FieldName = "hashCount"`)
3. `ClearVelocityPass`
4. `BoidNeighborForcePass`: `maxNeighbors = 48`, `countCapHits = false`
5. `TeamHeadingSteerPass`
6. `IntegratePass`
7. `BoxBoundsPass`: тот же центр и размеры, `BoundsBehaviour.Wrap`
8. `HeadingToValuePass`

Сцена `Boids_Hash.unity`: камера ортографическая, `orthographicSize = 46`, позиция `(0,40,0)`, поворот `(90,0,0)`. Хост `M3D Boids Hash` с `SimulationWorld`: эффект, `visualEffect = null`, `inputRouter = null`, библиотека пассов через `EnsurePassLibrary`. `HashProbeControls` не добавлять. `EditorBuildSettings` не менять.

### B2. Тесты

`BoidsHashPresetTests`. Ассет в тестах только читать; если нужно менять — `Instantiate`, оригинал не трогать (иначе в памяти редактора ассет остаётся изменённым и ломает соседние тесты).

| Тест | Ожидание |
| --- | --- |
| Пресет | Загружается `Boids_hash.asset`. Источник `Swarm`, один спавн с числами из B1, один профиль команды с числами из B1, `particleSize` 0,5. Пассы в порядке B1 по типам. Параметры билдера и `BoxBounds` из B1 и они совпадают между собой (центр, размеры) |
| Валидатор | `SpatialHashValidator.Validate(passes, effect)` не бросает и не пишет предупреждений |
| Сборка | Неактивный хост, `Rebuild` пресета без `VisualEffect` не бросает, частиц 3000 |
| Цепочка | См. ниже |
| Дефолты | `countCapHits` в ассете `false` |

**Цепочка.** Пасс за пассом прогнать пресет 300 кадров при `dt = 1/60` и прочитать буферы. Условия: все позиции, курсы и скорости конечны; `|heading|` в допуске `1e-4` от 1; `|velocity|` в допуске `1e-4` от 6; позиции внутри `[-45, 45]` по X и Z с допуском `1e-3`; сумма поля `hashCount` равна 3000. Способ: те же приёмы, что в `SpatialHashGpuTests` и `BoidNeighborForceTests` (свой `ParticleSet` и `SimContext`, Teams-буфер через `TeamParams.Upload`, `SwarmSource.Setup`, регистрация атрибутов теми же вызовами, что в мире). Если нужно скопировать из мира больше 30 строк, допустимо вызвать закрытый `Update` мира через рефлексию, только в тесте. Если ни тот, ни другой способ не работает без правки `SimulationWorld`, остановиться и написать об этом в сдаче, мир не править.

### B3. Play-гейт

Выполнять через `user-unity` MCP. Если MCP недоступен, остановиться и написать об этом.

Порядок:

1. `open_scene` `Assets/Scenes/Boids_Hash.unity`, `clear_console`, `editor_play`.
2. Сразу после старта через `eval` найти `SimulationWorld`, в его `effect.Passes` найти `BoidNeighborForcePass`, выставить `CountCapHits = true` и вызвать `ResetCapHitWindow()`.
3. Через 5, 15 и 30 секунд после старта: прочитать `LastCapHitFraction`, сделать `capture_game_view`, вызвать `get_performance_stats`. Значение `-1` в любой точке — гейт не пройден.
4. На 30 секундах поле `hashCount` оставить включённым, на 5 и 15 дополнительно один снимок с выключенным квадом поля.
5. До 60 секунды следить за консолью (`console`): ни одной ошибки, исключения или предупреждения мира.
6. `editor_stop`. Через `eval` вернуть `CountCapHits = false` на пассе ассета (изменение в Play живёт в объекте ассета и после остановки).
7. `git status` и `git diff` по `Boids_hash.asset`: в рабочей копии ассет не отличается от созданного меню.

**Конфигурации и критерий.** Замер сделан по трём конфигурациям (3000/48, 3000/64, 2000/48), числа в журнале. Порог «меньше 1%» снят: доля cap записывается как информация, критерием остаётся только отсутствие `-1`. В ассете 3000/48, пресет не подгонять. Пункт 3 порядка выше про `-1` сохраняется, остальное по доле не проверяется.

**Журнал** `DOC/last/play-5c-boids-hash.md`: для каждой конфигурации — доля капа в трёх точках, FPS из `get_performance_stats`, пути снимков и по одной фразе на снимок (форма стаи, граница, поле хэша). Если снимок показывает пустую полосу или скопление на границе, это пишется прямо.

Снимки сохранить рядом с журналом в `DOC/last/` (имя `play-5c-<config>-<t>s.png`) и вставить в журнал ссылками.

Что в гейт не входит: подтверждение вида владельцем (отдельный шаг после сдачи), FPS на S10 (P7), цвет по командам (P6).

### B4. Документы

Коротко, ADR и план не переписывать:

- `status.md` — итерация 5c, пресет `Boids_hash` есть, Play-гейт пройден по автоматическим критериям; доля cap 86–99.8%, порог 1% снят (ADR-040 §4), числа в журнале; вид принят владельцем (2026-10-07). Строку ADR-037 про «Сила соседей буфер не читает» заменить: буфер читают сила и steer.
- `pass-catalog.md` — ничего нового, если не появилось новых пассов.
- `capabilities.md` — строка про пресет и меню; строки Swarm и teams про «силы соседей нет» привести к фактам (сила и steer есть, пресет есть).
- `getting-started.md` — меню `Create Boids Hash Effect`, сцена `Boids_Hash`.

---

## Готово, когда

- Часть A принята отдельно.
- Тесты B2 зелёные, весь EditMode зелёный, красных нет.
- Журнал Play-гейта заполнен, снимки на месте, `Boids_hash.asset` в рабочей копии без изменений после замера.
- `HeadingSteer`, кернелы силы, steer и хэша, `SimulationWorld`, `Test1.unity` не изменены.
- Коммита нет.
