# ТЗ: ADR-042 P6b — пресет двух стай

**Статус:** закрыт (2026-10-08). EditMode 298/298. На 15 с перекрытие двух цветов, множитель чужих 4.
**Исполнитель:** одна приёмка. Начинать после коммита 6a и «принято» по 6a. Если палитры в коде нет, остановиться и написать об этом.

Прочитать [`ADR-042`](../ADR/ADR-042-Two-Team-Preset.md) перед кодом. План §P6b короче этого файла. Где они расходятся, делать по ТЗ и написать об этом в сдаче. Известное расхождение: план говорит «тюнинг радиусов и весов». ТЗ разрешает только один шаг множителя чужих, 4 → 8, и только если стаи слиплись.

**Менять:**

- `Assets/Scripts/Editor/M3DDemoTools.cs` — новое меню и новый создатель. `CreateBoidsHashAsset` не менять.
- Новый файл `Assets/Tests/Editor/BoidsHashTwoTeamsPresetTests.cs`.
- Меню создаёт `Assets/Effects/Boids_hash_2teams.asset` и `Assets/Scenes/Boids_Hash_2Teams.unity`.
- Документы: `DOC/status.md`, `DOC/capabilities.md`, `DOC/getting-started.md`.

**Не трогать:** кернелы и классы хэша, силы, steer, `TeamParams`, `SwarmSource`, `PrimitiveParticleBinder`, `ParticleBillboard.shader`, `SimulationWorld`, `FieldDebugQuadsBinder`, `FPSDisplay.cs`, `VfxParticleBinder`, `HashProbe*`, `Boids_hash.asset`, `Boids_Hash.unity`, `Test1.unity`, Physarum, fluid, `EditorBuildSettings`, `SimulationWorldTestCleanup`. Существующие тесты `BoidsHashPresetTests` и `PrimitiveParticleBinder*Tests` не править. План не править. `DebugFieldQuadSlot` не расширять новым градиентом.

**Git:** не коммитить. Не делать `git add -A`, `git add .`, `git commit -a`, `stash`, `checkout` / `restore` / `reset`. Чужие незакоммиченные правки не трогать.

---

## 1. Меню

`Tools/M3D/Create Boids Hash 2 Teams Effect`.

Порядок как у `CreateBoidsHashEffect`: если папки эффектов нет, создать её; записать ассет; `SaveCurrentModifiedScenesIfUserWantsTo`; при отказе записать в лог, что сцена отменена, а ассет уже записан, и выйти; иначе новая сцена `DefaultGameObjects` и сохранение в `Assets/Scenes/Boids_Hash_2Teams.unity`. Открытую сцену под эффект не использовать.

Если ассет по этому пути уже есть, удалить его и создать заново. `Boids_hash.asset` не удалять и не перезаписывать.

## 2. Ассет

Путь `Assets/Effects/Boids_hash_2teams.asset`. Копия чисел `CreateBoidsHashAsset`, кроме источника и списка команд.

Цепочка, по порядку: `BuildSpatialHash` (center 0, extents `(45, 0, 45)`, `minCellSize` 3, wrap), `HashCountsToField` с полем `hashCount`, `ClearVelocity`, `BoidNeighborForce` (`maxNeighbors` 48, `countCapHits` false), `TeamHeadingSteer`, `Integrate`, `BoxBounds` (тот же center и extents, Wrap), `HeadingToValue`.

Поле `hashCount`: скаляр, 30×30, size 90×90, origin `(0, -1, 0)`. Квад `Density("hashCount")`, `colorScale` 0.05. `simulationSpeed` 1. `particleSize` 0.5, `particleValueScale` 1, `particleGradient` = `DefaultFireGradient()`.

`SwarmSource`: seed 1, jitter 0. Два спавна:

| | Fire | Ice |
| --- | --- | --- |
| `teamIndex` | 0 | 1 |
| `count` | 1500 | 1500 |
| `center` | (−22, 0) | (22, 0) |
| `radius` | 12 | 12 |
| `initialDirection` | (1, 0) | (−1, 0) |

`Vector2.y` центра и курса — это мир Z. Курс (1, 0) смотрит на +X.

Команды, обе с радиусами 3/3/3, весами 1.2/0.8/0.6, cruise 6, turn 4, `InterGroupSeparationMultiplier` 4:

- `Fire`: `Color = DebugFieldQuadSlot.DefaultFireGradient()`.
- `Ice`: новый градиент в создателе, не в `DebugFieldQuadSlot`. Цветовые ключи `(0.02, 0.08, 0.35)` на 0, `(0.10, 0.45, 0.90)` на 0.45, `(0.75, 0.95, 1)` на 1. Альфа 1 на 0 и на 1.

Имена команд именно `Fire` и `Ice`.

## 3. Сцена

`Assets/Scenes/Boids_Hash_2Teams.unity`. Камера как в `CreateBoidsHashScene`: ортографическая, size 46, позиция `(0, 40, 0)`, Euler `(90, 0, 0)`. Хост `M3D Boids Hash 2 Teams`, на нём `SimulationWorld` с этим ассетом. `visualEffect` и `inputRouter` пустые. Pass library через уже существующий `EnsurePassLibrary`. `HashProbeControls` не добавлять. `EditorBuildSettings` не менять.

Копировать метод сцены можно. Выносить общий хелпер можно только если `CreateBoidsHashEffect` по-прежнему пишет прежние путь, камеру и имя хоста `M3D Boids Hash`. `CreateBoidsHashAsset` не редактировать.

## 4. Тесты

Новый файл `BoidsHashTwoTeamsPresetTests.cs`. Образец — `BoidsHashPresetTests`: ассет с диска для чтения, копия через `Instantiate` для прогона, `SimulationWorldTestCleanup.DestroyHost` в `TearDown`.

1. **Числа.** Путь ассета, источник `SwarmSource`, seed 1, jitter 0, два спавна и две команды как в таблице выше, имена `Fire` и `Ice`. Концы градиентов: `Fire` на 0 совпадает с чёрным, на 1 с `(1, 0.95, 0.55)`; `Ice` на 0 с `(0.02, 0.08, 0.35)`, на 1 с `(0.75, 0.95, 1)`; допуск `1/255` по каналу. Цепочка из восьми пассов в порядке раздела 2. `maxNeighbors` 48, `countCapHits` false. Поле `hashCount` 30×30, size 90×90. `particleSize` 0.5.
2. **Валидатор.** Тот же приём, что `Validator_AcceptsPresetWithoutWarnings` у одно-командного пресета: предупреждений нет.
3. **Цепочка.** 300 кадров при `dt = 1/60`. Позиции, курсы и скорости конечны. `|heading|` в допуске `1e-4` от 1. `|velocity|` в допуске `1e-4` от 6. Позиции внутри `[−45, 45]` по X и Z с допуском `1e-3`. Частиц с `teamId` 0 ровно 1500, с `teamId` 1 ровно 1500. Сумма поля `hashCount` равна 3000. Таймаут как у цепочки `BoidsHashPresetTests` (`180000`). Проход стай сквозь друг друга этим тестом не доказывать.

Существующие тесты одно-командного пресета остаются зелёными без правок. Это и есть проверка, что `Boids_hash.asset` не переписан.

## 5. Play

Через `user-unity` MCP. Если MCP недоступен, остановиться и написать об этом.

1. `open_scene` `Assets/Scenes/Boids_Hash_2Teams.unity`, `clear_console`, `editor_play`.
2. На 5 и 15 с: `capture_game_view` и `get_performance_stats`.
3. До 20 с смотреть консоль: ни одной ошибки и исключения мира. Известные warning про целочисленный modulus/divides в `SpatialHash.hlsl` и `BoidsPasses.compute` не чинить и не считать провалом.
4. `editor_stop`.

Снимки: `DOC/last/play-6b-5s.png`, `DOC/last/play-6b-15s.png`. Журнал `DOC/last/play-6b-two-teams.md`: FPS, пути снимков и по одной фразе на снимок. Писать прямо, слиплись цвета или нет, есть ли прижатие к границе, встретились ли стаи к 5 с.

**Критерий первой конфигурации.** На 15 с две отдельные цветные стаи. Нет одной перемешанной кучи и нет прижатия к границе.

**Один повтор.** Только если на 15 с цвета слиплись в одно тело. Тогда у обеих команд множитель 4 → 8, ассет пересоздать меню, повторить Play. Снимки `play-6b-x8-5s.png` и `play-6b-x8-15s.png`, те же фразы в журнале. Больше ничего не менять: не радиусы, не веса, не cruise, не turn, не число частиц, не ячейку. Если и 8 не разнял стаи, меню вернуть множитель 4 и на этом остановиться.

Если на 15 с две стаи есть, но они отскочили и не прошли сквозь друг друга, повтор не делать. Так и записать.

`countCapHits` не включать. Долю капа не снимать.

Что в гейт не входит: подтверждение вида владельцем (отдельный шаг после сдачи), замер S10 палитры (ADR-041 §5), P8.

## 6. Документы

Коротко, ADR и план не переписывать:

- `status.md` — итерация 6b, пресет `Boids_hash_2teams`, сцена, что показал Play (прошли, отскочили или слиплись; если был повтор ×8 — чем кончился).
- `capabilities.md` — строка про пресет двух стай и меню.
- `getting-started.md` — одно предложение: меню `Create Boids Hash 2 Teams Effect`, сцена `Boids_Hash_2Teams`.

---

## Готово, когда

- Тесты раздела 4 зелёные. Весь EditMode зелёный, красных нет. Существующие тесты `Boids_hash` зелёные без правок.
- Журнал Play заполнен, снимки на месте. Если был повтор, на диске множитель, на котором остановились по правилу раздела 5.
- `Boids_hash.asset` и `Boids_Hash.unity` в диффе не изменены этой работой.
- `CreateBoidsHashAsset`, шейдер билборда, биндер и `SimulationWorld` не изменены.
- Коммита нет.
