# ADR-057: alignment по скорости соседей (`AlignFromVelocity`) для классического хвоста

**Статус:** код P9.4.1 принят 2026-10-30 после исправления нуля частиц (А0), P9.4.2 ждёт Play.
**Дата:** 2026-10-30.
**Контекст:** M3D Framework, ветка `experiment/align-from-velocity` (от `feature/classic-boids` после P9). Связано с [ADR-054](ADR-054-Classic-Boids-Tail.md), [ADR-048](ADR-048-Default-Walk-Mode.md), [ADR-053](ADR-053-Sum-Separation.md), [ADR-047](ADR-047-Sorted-Order-Neighbor-Walk.md).

## 1. Контекст

**Что есть.** Снимок хэша хранит в `SortedDirections` курс соседей (`heading.xz`, `float2`). Обход складывает курсы тех, кто в радиусе alignment и той же команды, и в конце делает `normalize(сумма) · wAlign`. Alignment видит только направление, а сила не зависит от того, насколько стая уже выровнена: у выровненной стаи она всё равно толкает вперёд с полным весом.

**Что показал Play P9.2** (метрики на 300 кадров, `classic` и `classic_points`): скорость уходит к верхней границе `maxSpeed` (78% и 73% частиц у верхнего клампа, средняя 7,75 при коридоре 4–8, нижняя граница почти не срабатывает), у `Boids_hash` скорость по построению постоянна 6. Причина в постоянной тяге вперёд от нормализованного alignment плюс отсутствие трения. Владелец принял результат, но хочет приблизить модель к классике.

**Классический alignment** это совпадение скорости с соседями: `steering ∝ (средняя скорость соседей − своя скорость)`. В выровненной стае он равен нулю, поэтому скорость сходится к общей.

## 2. Решение

### 2.1 Формула (в новом варианте обхода)

```
align = ((Σ v_j) / alignN − v_i) / maxSpeed[team_i] · wAlign[team_i]
```

- `v_j` это `inertialVelocity.xz` соседей j той же команды в радиусе alignment, `v_i` своя; `alignN` число таких соседей (как сейчас, `r² ≥ 1e-6`, учитывается капом `maxNeighbors` как все).
- Нет соседей (`alignN = 0`) значит нет вклада.
- **Без нормализации.** Деление на `maxSpeed` команды (`Teams[...].Classic.y`) делает вес безразмерным, сопоставимым с весами separation и cohesion. Это постоянный множитель на команду: формула классическая «match velocity» с коэффициентом `wAlign / maxSpeed`.
- Separation (`(sep / sepN) · wSep`) и cohesion (`normalize(mean) · wCoh`) остаются как в walk 1. `velocity += float3(force.x, 0, force.y)`, без `dt`, `maxForce` обрезает сумму в хвосте.
- Пример для теста: свой `v = (6, 0)`, один сосед `v = (8, 0)`, `maxSpeed 8`, `wAlign 0,8`: вклад `(8 − 6) / 8 · 0,8 = 0,2` по +x; сосед `v = (−6, 0)`: `(−6 − 6) / 8 · 0,8 = −1,2`.

### 2.2 Снимок

- Новое ядро `HashScatterVelocity` в `SpatialHashPasses.compute`: копия `HashScatter`, в которой меняется **одна строка**: `SortedDirections[slot] = inertialVelocity[id.x].xz;` вместо `heading[id.x].xz`. Остальные буферы (`SortedIndices`, `SortedPositions`, `SortedTeams`) те же.
- Размер снимка и число чтений в горячем цикле не меняются. Лишнее чтение одно: своя скорость на частицу в обходе.
- Флаг `SpatialHashSet.VelocityRequested` (internal, по образцу `PackedRequested`) выставляет потребитель. Если он взведён, `BuildSpatialHashPass` на этом кадре использует `HashScatterVelocity`, привязывает `inertialVelocity` и отмечает `VelocityWritten` (по образцу `PackedWritten`). `HashScatter` и `HashScatterPacked` не меняются.
- Снимок строится в начале кадра из `inertialVelocity` конца прошлого кадра (как снимок курса). Первый кадр: скорость нулевая, соседей нулевых, вклад alignment нулевой. Это безвредно.

### 2.3 Обход

Новое ядро `BoidNeighborForceSortedVel` в конце `BoidsPasses.compute`: **дубликат** ядра walk 1 (`BoidNeighborForceSorted`), в котором отличается только концовка (alignment по §2.1, чтение `inertialVelocity[i]`). Вспомогательные функции (`AccumulateSortedRange`, структура `NeighborWalk`) используются без изменений: они уже складывают `SortedDirections` без нормализации и считают `alignN`. Текст walk 0, 1 и 2 не меняется (ADR-048, ADR-053 §6: вариант ядра, а не uniform в горячем цикле).

### 2.4 Пасс

- `BoidNeighborForcePass.AlignFromVelocity` (bool, по умолчанию `false`, сериализуется). **Это не режим walk:** `WalkMode` остаётся 0..2, существующий тест `WalkMode_RejectsOutOfRange` и все тесты `WalkMode == 1` не меняются. Пресет с флагом имеет `WalkMode = 1`.
- При `true`: `Reads` пасса дополняется `InertialVelocity`; `Initialize` находит ядро `BoidNeighborForceSortedVel` и выставляет `spatialHash.VelocityRequested = true` (сеттер свойства обновляет флаг, как у `WalkMode`); `SelectKernel` выбирает это ядро; `EffectiveWalkMode` возвращает 1, плюс internal `EffectiveAlignFromVelocity` для тестов.
- **Без тихого запасного пути.** В отличие от packed (результат совпадает), возврат к alignment по курсу меняет смысл силы. Поэтому при `AlignFromVelocity` и `WalkMode != 1`, при блокировке сортированного обхода радиусом (`RadiusBlocksSortedWalk`), при отсутствии ядра или `inertialVelocity`, при невзведённом `VelocityWritten` бросается `InvalidOperationException` с понятным текстом (в `Initialize`, если известно сразу, иначе в `SelectKernel`).
- Остальное в пассе (кап, счётчик попаданий в кап, `ForceRepeatCount`, `scratch`) работает как у walk 1.

### 2.5 Валидатор

Ошибка, если включён `BoidNeighborForcePass` с `AlignFromVelocity` и нет включённого `TeamClassicSteerPass` (скорость не накапливалась бы), либо `WalkMode != 1`. Порядок пассов проверяется по существующим правилам между обходом и хвостом (Grok сверяет в разведке, есть ли такое правило для `TeamHeadingSteer`, и повторяет его для `TeamClassicSteer`).

### 2.6 Числа и пресет

Начальные веса не меняются (`wAlign 0,8`; остальное по ADR-054 §2.5). В выровненной стае сила alignment слабеет по определению, поэтому вес возможно придётся поднять в Play. Пресет `Boids_hash_classic_vel` (3000 частиц, без точек, рецепт `Boids_hash_classic` с `AlignFromVelocity = true`) делается в P9.4.2. Пресет 200k не делается.

## 3. Отвергнутые варианты

- **`WalkMode = 3`.** Ломает существующий тест диапазона и смешивает «как обходить» с «что складывать».
- **Переключатель внутри ядра walk 1** (uniform или ветка). Меняет скомпилированное горячее ядро S200.
- **Чтение скорости соседей напрямую из `inertialVelocity[SortedIndices[j]]`.** Случайный доступ по всем соседям, чего снимок как раз избегает ради P7.
- **Менять снимок для всех пресетов.** Ломает кинематику и существующие тесты.
- **Запасной переход на alignment по курсу.** Скрывал бы ошибку конфигурации и менял смысл силы.
- **Сумма separation и отталкивание чужой команды.** Остаются отложенными (§4, ADR-053).
- **Сразу трение (`drag`) в хвосте.** Лечит симптом; остаётся запасным вариантом, если alignment по скорости не снимет снос.

## 4. Последствия

- **Цена.** Одно ядро около 170 строк (дубликат walk 1), одно ядро хэша около 10 строк, один флаг и условные чтения в пассе, один флаг в `SpatialHashSet`, одно лишнее чтение на частицу. Память не растёт. Walk 0/1/2 и `HashScatter*` не меняются, база S200 по walk 1 не затрагивается.
- **Дубликат ядра** нужно поддерживать вместе с walk 1; фиксируется в техдолге. Любая правка walk 1 после этого требует решения, переносить ли её в вариант.
- **Поведение.** Гипотеза (проверяется в P9.4.2, не обещание): скорость стаи сходится к общей, доля частиц у верхней границы падает, нижняя граница получает смысл. Если нет, остаётся трение в хвосте.
- **Сумма separation** остаётся заблокированной капом (ADR-053 §4), **отталкивание чужой команды** остаётся за P8.

## 5. Критерии приёмки (P9.4.1)

1. EditMode: красных ровно четыре известных (пресеты двух команд ×2 и два пресета Fluid2D); число тестов до и после записано; новые тесты названы.
2. `BoidsPasses.compute`: в диффе только строка `#pragma kernel BoidNeighborForceSortedVel` и блок в конце файла. `SpatialHashPasses.compute`: только строка `#pragma kernel HashScatterVelocity`, объявление `inertialVelocity` и блок ядра. Текст `BoidNeighborForce*`, `HashScatter`, `HashScatterPacked`, `TeamClassicSteer` не затронут.
3. Существующие тесты не изменены (включая `WalkMode_RejectsOutOfRange`, `EveryNeighborForcePreset_IsWalk1_AndFitsTheCell`).
4. Снимок: при `VelocityRequested` в `SortedDirections` лежит `inertialVelocity.xz` в отсортированном порядке, остальные буферы совпадают с `HashScatter`; без флага снимок побайтно прежний.
5. Обход: ядро совпадает с CPU-оракулом alignment по скорости на двух командах, с тором и без, на шве, при капе и без, `teamId > 7`, числе частиц не кратном 64, на 30 случайных сидах. Якоря: при `wAlign = 0` силы ядра равны силам walk 1; при равных скоростях соседей и своей alignment равен нулю; пример из §2.1 даёт `0,2` и `−1,2`; alignment не зависит от `heading`.
6. Пасс: `AlignFromVelocity` по умолчанию `false`, `WalkMode` по-прежнему 0..2; `Reads` содержит `InertialVelocity` только при `true`; перечисленные в §2.4 ошибки.
7. Валидатор: оба случая из §2.5.
8. Двухкадровая цепочка (хэш, `ClearVelocity`, обход, `TeamClassicSteer`, `Integrate`): на втором кадре alignment использует скорости первого.

## 6. Фазы

| Фаза | Что | Гейт |
| --- | --- | --- |
| P9.4.0 | этот ADR и ТЗ P9.4.1 | «принято» владельца |
| P9.4.1 | `HashScatterVelocity`, `VelocityRequested`, ядро `BoidNeighborForceSortedVel`, `AlignFromVelocity`, валидатор, оракул, тесты | критерии §5, ревью Claude |
| P9.4.2 | пресет `Boids_hash_classic_vel` (3000), метрики, Play A/B с `classic` | «принято» владельца |
| P9.4.3 (по результатам) | подбор весов; трение, если снос не ушёл | отдельное решение |

## 7. Результат

Голова `feature/classic-boids` перед веткой: `4bd80030e9f1e573d170b9a597d35d90c37972d8`. Коммит 0 `cef61ad5eb18d6c689b6a79acbb77fd298c021df`, А1 `2263ae75c21e5b93f8c1888b02f53f343f829b2c`, А2 `7b1ac8f69602b5337921c3849dda7a215753aa9d`. Хеш коммита документов в этом разделе не записан: он появляется после коммита.

### Разведка

1. EditMode до кода: 469 тестов, прошло 465, упало 4. Красные: `BoidsHashTwoTeamsPresetTests.Preset_MatchesTwoTeams`, `BoidsHashTwoTeamsPresetTests.Chain_ThreeHundredFrames_StaysFiniteInsideBox`, `Fluid2DDyeInjectPresetTests.Preset_MatchesDyeInjectComposition`, `Fluid2DDyeInjectVcPresetTests.Preset_MatchesDyeInjectVcComposition`.
2. `PackedRequested` выставляет `BoidNeighborForcePass`, когда `WalkMode == 2` (сеттер и `Initialize`). `PackedWritten` сбрасывается в начале каждого `BuildSpatialHashPass.Execute` и становится истиной только после диспатча `HashScatterPacked`. `VelocityRequested` выставляет сеттер `AlignFromVelocity`, если хэш уже есть, и `Initialize` пасса. `VelocityWritten` сбрасывается в том же начале `Execute` и становится истиной только после диспатча `HashScatterVelocity`. Оба запроса сразу и запрос без ядра бросают `InvalidOperationException`. Запасного scatter нет.
3. `ParticleSet.Get` бросает `KeyNotFoundException`, если атрибута нет. `SimulationWorld` регистрирует `Reads`/`Writes` всех пассов и обнуляет новый буфер. `InertialVelocity` в `Reads` при включённом флаге достаточно, чтобы мир выделил и обнулил скорость даже без `TeamClassicSteerPass`.
4. Правила «обход силы стоит до хвоста» нет ни для `TeamHeadingSteerPass`, ни для `TeamClassicSteerPass`. Новое правило порядка не вводилось. Валидатор по-прежнему требует, чтобы потребитель хэша стоял после билдера, и запрещает оба хвоста сразу.
5. Выключенный флаг возвращает прежний массив `ReadPositionTeam`. Диапазон `WalkMode` не менялся. Существующие проверки (`BoidNeighborForceDefaultsTests`, `BoidNeighborForceRepeatTests`, `BoidsHashPresetsWalkDefaultTests`, отражение имён ядер, `SelectKernel`/`SetParams`) остались зелёными без правки ожиданий.
6. `ParticleKernelPass.Execute` вызывает `SelectKernel`, при невалидном ядре выходит, при нуле частиц выходит до диспатча. `SelectKernel` при нуле частиц выполняется.
7. `EveryNeighborForcePreset_IsWalk1_AndFitsTheCell` и `WalkMode_RejectsOutOfRange` прошли без правки.

### EditMode

После А1: 472 / 468 / 4. После А2: 486 / 482 / 4. Красные те же четыре. Новые тесты: `BuildSpatialHashVelocityTests` (снимок, сброс `VelocityWritten`, отсутствие ядра, оба флага) и `BoidNeighborForceVelocityAlignTests` (оракул на 30 сидах, шов, плотный кап при `wAlign = 0`, равные скорости, пример §2.1, нет соседей, независимость от курса, чужая команда, контракт, валидатор, двухкадровая цепочка).

### Отклонения

- Ядра в рабочем шейдере не убрать отдельным ассетом: список коммита его не разрешает. Тест отсутствия ядра после `Initialize` обнуляет приватный хэндл отражением. `Execute` бросает «velocity alignment kernel not found». `Initialize` бросает то же, если поиск ядра не удался.
- `RadiusBlocksSortedWalk` перед исключением всё ещё пишет прежнее предупреждение «executing walk 0». Отката на walk 0 нет: `SelectKernel` бросает `InvalidOperationException`.
- Геометрический оракул не сходится с обходом, если частица стоит ровно на верхней границе тора: хэш кладёт её в первую ячейку, а в снимке остаётся координата на период дальше, и сдвиг той же ячейки равен нулю. Случайные сиды держат частицы внутри сетки. Шов проверен фикстурой отсортированного обхода, она с оракулом сходится.
- Плотный кап сравнивается с walk 1 при `wAlign = 0` (допуск 1e-6), а не с геометрическим оракулом: порядок слотов задаёт `InterlockedAdd`. Без капа, включая 30 сидов, сравнение идёт с оракулом.
- Если после `Initialize` `WalkMode` уходит с 1, `SelectKernel` тоже бросает, а не переходит на alignment по курсу.

### P9.4.2

Голова перед этим ТЗ: `f6dfff465f31dc3df3855dbd1136501265a5bd87`. Коммит 0 `61bb01800a1ed3303c40e0ce1d54f0e6ad1212a0`, А0 `d1665d65e3abe70b743c040d89c33d922a3a5091`, А `d8824b921d99d99393bd5b771ea00bb215ea1538`. Хеш коммита документов появляется после него.

**Разведка.** EditMode после А0: 488 / 484 / 4, красные те же четыре. `CreateBoidsHashClassicVelAsset` вызывает существующий `CreateBoidsHashClassicAsset(path, false)` и затем ставит `AlignFromVelocity`; метод и вывод `Boids_hash_classic` не менялись. `InternalsVisibleTo("Assembly-CSharp-Editor")` уже есть, тесты читают `EffectiveAlignFromVelocity`. Фиксированного числа ассетов или сцен ни один тест не ждёт. `EveryNeighborForcePreset_IsWalk1_AndFitsTheCell` смотрит все эффекты, но проверяет только `WalkMode == 1` и радиус в ячейке; у нового пресета оба условия как у classic.

**Нуль частиц.** `SelectVelocityKernel` не проверяет `VelocityWritten`, когда `Count == 0`, и возвращает ядро. `Execute` выходит до диспатча. При `Count > 0` ложный снимок по-прежнему бросает.

**EditMode.** До кода пресета (после А0) 488 / 484 / 4. После А: 493 / 489 / 4. Красные те же четыре. Новые тесты: `ZeroParticles_ExecuteDoesNotDispatch`, `Execute_MissingVelocitySnapshot_ThrowsWhenParticlesExist`, `BoidsHashClassicVelPresetTests` (состав, валидатор, Rebuild на 3000, коридор 300 кадров, лог метрик).

**Метрики.** 3000 частиц, seed 1, `DeltaTime` 1/60. Скорость — `velocity.magnitude`, стандартное отклонение по всем частицам кадра (`sqrt(mean(s²) − mean(s)²)`). Доля у 4: `|v| < 4 + 1e-3`, у 8: `|v| > 8 − 1e-3`. Лог, не порог.

| Пресет | Кадр | Средняя | Ст. откл. | Поляризация | Доля у 4 | Доля у 8 |
| --- | --- | --- | --- | --- | --- | --- |
| `Boids_hash` | 100 | 6.0000 | 0.0000 | 0.1357 | 0 | 0 |
| `Boids_hash` | 200 | 6.0000 | 0.0000 | 0.2113 | 0 | 0 |
| `Boids_hash` | 300 | 6.0000 | 0.0000 | 0.2445 | 0 | 0 |
| `Boids_hash` | 600 | 6.0000 | 0.0000 | 0.2695 | 0 | 0 |
| `Boids_hash_classic` | 100 | 6.8263 | 1.4322 | 0.0777 | 0.0633 | 0.4053 |
| `Boids_hash_classic` | 200 | 7.4706 | 1.1017 | 0.1520 | 0.0313 | 0.6733 |
| `Boids_hash_classic` | 300 | 7.7451 | 0.7625 | 0.2134 | 0.0130 | 0.7693 |
| `Boids_hash_classic` | 600 | 7.8001 | 0.7317 | 0.6009 | 0.0120 | 0.8887 |
| `Boids_hash_classic_vel` | 100 | 4.0775 | 0.2120 | 0.0267 | 0.7037 | 0 |
| `Boids_hash_classic_vel` | 200 | 4.0846 | 0.2050 | 0.0336 | 0.6737 | 0 |
| `Boids_hash_classic_vel` | 300 | 4.1374 | 0.2984 | 0.0475 | 0.6180 | 0 |
| `Boids_hash_classic_vel` | 600 | 4.2245 | 0.4112 | 0.1069 | 0.5360 | 0 |

Числа приведены как есть. Веса не менялись. Play и подбор за владельцем.

**Отклонения.** У `Boids_hash` нет `inertialVelocity`, поэтому лог метрик его не читает. Коридор и `EffectiveAlignFromVelocity` проверяются только у `classic_vel`. `Assets/Effects/Boids_hash_classic.asset` получил строку `alignFromVelocity: 0` от сериализации нового поля и оставлен вне коммита: поведение совпадает с отсутствием поля. У `classic_vel` к кадру 300 доля у 8 равна 0, средняя скорость 4.1374, доля у 4 равна 0.6180.
