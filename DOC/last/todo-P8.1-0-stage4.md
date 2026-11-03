# ТЗ: P8.1-0, этап 4 — сцена P400c

**Исполнитель:** Grok. **Ревью:** Claude и владелец.
**Куда положить:** `DOC/last/todo-P8.1-0-stage4.md`.
**Основа:** [ADR-060](../ADR/ADR-060-P400c-Scene-Builder.md) (решения этого ТЗ), [ADR-059](../ADR/ADR-059-P8-1-0-Scene-Baseline-Model-Checks.md) §2.1 (числа сцены), [ADR-058](../ADR/ADR-058-Field-Neighbor-Force.md). Общие правила и список «не делать» из [`todo-P8.1-0.md`](todo-P8.1-0.md) §0 и §6 действуют. Этот файл заменяет §4 того ТЗ; этап 5 (телефон) остаётся там и здесь **не выполняется**.
**Остановка:** одна, после этого этапа. Следующий этап после слова «принято».

## 0. Условия

1. **Ветка:** `feature/p8-field-neighbors`, основной каталог, worktree не используется. Начать с вывода `git branch --show-current`, `git rev-parse HEAD`, `git rev-parse experiment/align-from-velocity` (ожидается `d58fc64a0a84ddc53545bf3a0bcded0efa5e4fc9`), `git status --short` (рабочее дерево должно быть чистым; если нет, остановиться и спросить). В `experiment/align-from-velocity` не коммитить и её не переключать. Не пушить.
2. **Unity MCP.** Работу со сценой, ассетами, меню и тестами вести через него. Если он недоступен, предупредить владельца и остановиться.
3. **Красные тесты не чинить.** На голове ветки известны три красных: `BoidsHashTwoTeamsPresetTests.Preset_MatchesTwoTeams`, `Fluid2DDyeInjectPresetTests.Preset_MatchesDyeInjectComposition`, `Fluid2DDyeInjectVcPresetTests.Preset_MatchesDyeInjectVcComposition` (база этапа 1: 493 теста, 490 прошло). Любое **новое** падение: остановиться, приложить сообщение, не чинить.
4. Не менять: существующие эффекты и сцены (в том числе `Boids_hash_2teams`, `Boids_hash400k_2teams` и сцены 400k), `BoidNeighborForcePass`, `BoidsPasses.compute`, `SpatialHashValidator`, существующие билдеры в `M3DDemoTools.cs`, скрипты `Tools/s10`, `FPSDisplay.cs`.

## 1. Билдер

В `Assets/Scripts/Editor/M3DDemoTools.cs` добавить рядом с `CreateBoidsHashTwoTeamsAsset`:

1. Константы путей: ассет `Assets/Effects/Boids_hash_P400c.asset`, сцена `Assets/Scenes/Boids_Hash_P400c.unity`.
2. **Общий приватный помощник параметров** (ADR-060 §2.1): настраивает спавн, профили команд и общие поля `particleSize`, `particleValueScale`, `particleGradient`. Подписи и градиенты команд (`RedBlue`, `YellowGreen`) и `RedBlueGradient()` / `YellowGreenGradient()` брать существующие. Существующие билдеры не рефакторить и не подключать к помощнику.
3. `CreateBoidsHashP400cAsset(string path)` по образцу двух команд (удалить существующий ассет по пути, создать, сохранить), параметры **строго** по ADR-059 §2.1:
   - `EditorConfigure(DataSourceKind.Swarm, 1f, ...)`, `simulationSpeed` 1;
   - пассы в порядке: `BuildSpatialHashPass { Center = zero, Extents = (112, 0, 112), MinCellSize = 3, Wrap = true }`, `ClearVelocityPass`, `BoidNeighborForcePass { MaxNeighbors = 48, CountCapHits = false }`, `TeamHeadingSteerPass`, `IntegratePass`, `BoxBoundsPass { Center = zero, Extents = (112, 0, 112), Behaviour = Wrap }`, `HeadingToValuePass`. **Без** `HashCountsToFieldPass` и без полей (`Fields` пусто);
   - `WalkMode` явно не задавать (по умолчанию 1), но тест проверяет его;
   - `SwarmSource`: seed 1, jitter 0, два спавна: команда 0, 200 000, центр (−55, 0), радиус 45, курс (1, 0); команда 1, 200 000, центр (+55, 0), радиус 45, курс (−1, 0);
   - команды: радиусы разделения / выравнивания / сплочения 3 / 3 / 3, множитель чужих 4, веса 1,2 / 0,8 / 0,6, `cruise` 6, `turn` 4;
   - `particleSize` 0,5, `particleValueScale` 1.
4. `CreateBoidsHashP400cScene(string scenePath, string assetPath)`: как у двух команд, но камера ортографическая размера **112**, позиция (0, 40, 0), поворот (90, 0, 0); объект `M3D Boids Hash P400c`, `visualEffect` и `inputRouter` пустые, `EnsurePassLibrary`.
5. Меню `Tools/M3D/Create Boids Hash P400c Effect` с тем же порядком действий, что у `Create Boids Hash 2 Teams Effect` (папка, ассет, сохранение сцены, сцена, лог).
6. Запустить меню через Unity MCP, убедиться, что ассет и сцена созданы, в консоли нет ошибок. В отчёт: вывод `git status --short`.

## 2. Тест

Файл `Assets/Tests/Editor/BoidsHashP400cPresetTests.cs` (+ `.meta`), по образцу `BoidsHashTwoTeamsPresetTests`, но **не зависящий** от него и от `Boids_hash_2teams`:

1. `Preset_MatchesP400cConstants`: константы ТЗ, не состояние ассета. Сверить: источник `SwarmSource`, seed 1, jitter 0, два спавна (индексы команд, `Count` 200 000, центры ±55, радиус 45, курсы ±1); две команды (имена, радиусы 3/3/3, множитель 4, веса 1,2 / 0,8 / 0,6, `cruise` 6, `turn` 4); `particleSize` 0,5; `simulationSpeed` 1 (через `SerializedObject`, если публичного свойства нет); список пассов ровно из 7 в порядке §1.3, их типы; `Extents` хэша и ящика (112, 0, 112), `Center` ноль, `MinCellSize` 3, `Wrap`, `Behaviour` Wrap; `MaxNeighbors` 48, `WalkMode` 1, `CountCapHits` false; `Fields.Count == 0`. Ассет не загрузился: `Assert.IsNotNull` с подсказкой про меню.
2. `Validator_AcceptsPresetWithoutWarnings`: `SpatialHashValidator.Validate(asset.Passes, asset)` не бросает и не возвращает предупреждений.
3. `Chain_ThirtyFrames_StaysFiniteInsideBox` с `[Timeout(180000)]` и `Assume.That(SystemInfo.supportsComputeShaders)`: цепочка как в `Chain_ThreeHundredFrames_StaysFiniteInsideBox`, но **30** кадров при `dt = 1/60`, на экземпляре ассета (`Instantiate`, уничтожается в `TearDown`, пассы с `IDisposable` освобождаются). Проверки: все позиции, курсы и скорости конечны, курс единичной длины, модуль скорости 6, позиции внутри ±112 (допуск 1e-3), в командах ровно по 200 000, всего 400 000. Библиотека шейдеров: те же четыре compute-файла, что у образца (`SpatialHashPasses`, `HashDebugPasses`, `DynamicsPasses`, `BoidsPasses`). **Не переиспользовать приватные методы образца копированием файла целиком**: нужные помощники (`SwapPingPong`, `RegisterLikeWorld`, `LoadLibrary`) повторить в новом файле коротко.
4. Если тест 3 не укладывается в 180 с, не увеличивать таймаут и не уменьшать частицы: остановиться, записать время и причину.

## 3. Build Settings

Добавить `Assets/Scenes/Boids_Hash_P400c.unity` в Build Settings через Unity MCP (`add_scene_to_build`). Остальные сцены в списке не менять. Записать в отчёт итоговый список сцен.

## 4. EditMode

Полный прогон EditMode на голове ветки **до** изменений кода (если уже снят в этапе 1, повторить заново: ассеты могли измениться) и **после**: общее число, прошло, упало, имена упавших. Ожидание: после = до плюс три новых зелёных теста; три красных те же. Новые красные: остановиться.

## 5. Play-проверка в Editor (качественная, ADR-060 §2.3)

1. Открыть сцену `Boids_Hash_P400c`, запустить Play через Unity MCP. Перед запуском очистить консоль.
2. Записать **условия**: среднюю частоту кадров Editor (`get_performance_stats` или разница `Time.frameCount` за интервал), `Time.time` и `Time.frameCount` в моменты съёмки (через `eval`), размер окна Game view. Если частота ниже 5 кадров/с, записать как факт, **сцену не уменьшать**.
3. Скриншоты `capture_game_view` на реальном времени 5, 15 и 30 с после старта (если Editor не тянет, записать, какие отметки удалось снять). Папка `DOC/last/play-P8.1-0-baseline/`, имена `p400c-cap48-t05.png`, `-t15.png`, `-t30.png`.
4. Консоль после Play: ошибки и предупреждения перечислить (в частности `TeamRadiusPlayCheck`, исключения валидатора, ошибки шейдеров).
5. Запись `DOC/last/play-P8.1-0-baseline.md`: условия съёмки (п. 2), перечень скриншотов, и **наблюдаемое**: видны ли два диска и их курс друг на друга, проходят ли стаи друг сквозь друга при капе 48 (да/нет/неясно по каждому снимку), нет ли NaN, видна ли вся сцена при камере 112, шов переноса. Вердикта о «живости», сравнений с моделью и выбора K не писать.
6. Остановить Play и убедиться, что сцена не изменилась (`git status`: только файлы этапа).

## 6. Коммит

Один коммит, только файлы этапа: `Assets/Scripts/Editor/M3DDemoTools.cs`, ассет и его `.meta`, сцена и её `.meta`, тест и его `.meta`, `ProjectSettings/EditorBuildSettings.asset` (Build Settings), скриншоты и запись Play, этот отчёт. Сообщение по-английски, в стиле репозитория. Чужие файлы не включать; если Unity пересохранил посторонние ассеты, не коммитить их и записать в отчёт.

## 7. Отчёт (`DOC/last/P8.1-0-stage4-report.md`)

Ветка и коммит; вывод `git status` до и после; число EditMode до и после и имена упавших; время теста 3; итоговый список сцен Build Settings; ссылка на запись Play; список ошибок и предупреждений консоли; отклонения от ТЗ (если были) с причиной. Формулировок «принят», «окончательно», «рекомендуется» не использовать.

## 8. Не делать

Не писать полевые пассы, кернелы, `FieldNeighborSet`, пресет `Boids_hash_P400c_field`, аргументы `-m3d_fieldrep` / `-m3d_fieldstage`. Не менять `SpatialHashValidator`, `BoidNeighborForcePass`, `BoidsPasses.compute`, существующие билдеры, пресеты и сцены. Не подгонять параметры сцены (радиусы, число частиц, диски, камеру), не выбирать K, интерполяцию и кап. Не запускать телефон и скрипты `Tools/s10`. Не править красные тесты. Не коммитить чужие несохранённые файлы. Не коммитить в `experiment/align-from-velocity` и не менять её.

## 9. Гейт

- Билдер, меню, ассет и сцена созданы; параметры совпадают с ADR-059 §2.1; полевых пассов нет.
- Тест: три теста зелёные; EditMode: новых падений нет, три прежних красных те же.
- Сцена в Build Settings.
- Play: запись и скриншоты на месте, условия съёмки записаны, консоль перечислена.
- Нигде нет изменений в файлах из §0.4 и §8.
- Рабочее дерево после коммита чистое, `experiment/align-from-velocity` на `d58fc64`.
