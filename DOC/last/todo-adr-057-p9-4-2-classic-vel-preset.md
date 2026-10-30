# ТЗ P9.4.2: исправление нуля частиц, пресет `Boids_hash_classic_vel` и метрики (ADR-057)

**Исполнитель:** Grok (код, ассеты, сцена, тесты, документы). **Ревью:** Claude. **Play и подбор чисел:** владелец (часть Д).
**Куда положить:** `DOC/last/todo-adr-057-p9-4-2-classic-vel-preset.md`.
**Основа:** [ADR-057](../ADR/ADR-057-Align-From-Velocity.md) (в ТЗ и сообщениях Claude он ещё назывался ADR-056; в репозитории 056 занят `ADR-056-Default-MaxNeighbors.md`), [ADR-054](../ADR/ADR-054-Classic-Boids-Tail.md) §2.5, [todo-adr-054-p9-2-classic-preset.md](todo-adr-054-p9-2-classic-preset.md). Где ТЗ короче ADR, делать по ADR; если ТЗ, ADR и код расходятся, остановиться и спросить владельца.
**Решения владельца 2026-10-30:** в P9.4 только alignment по скорости; пресет только на 3000 частиц (пресета 200k нет).
**Коммиты:** (0) это ТЗ; (А0) исправление нуля частиц и его тест; (А) фабрика, ассет, сцена, тесты; (Б) документы. После А0, А и Б красных ровно четыре известных (`Preset_MatchesTwoTeams`, `Chain_ThreeHundredFrames_StaysFiniteInsideBox` у `BoidsHashTwoTeams`, `Preset_MatchesDyeInjectComposition`, `Preset_MatchesDyeInjectVcComposition`).

## Часть 0. Git

1. Ветка `experiment/align-from-velocity`, голова `f6dfff465f31dc3df3855dbd1136501265a5bd87` или потомок (записать хеш в сдачу).
2. Явные пути включая `.meta` новых файлов; запрещены `git add -A`, `git add .`, `git commit -a`, `git stash`, `git reset`, `git restore`, `git checkout` на файлах вне ТЗ. Перед каждым коммитом `git status` и `git diff --cached --stat`.
3. Чужие файлы (`.gitignore`, `CurlNoise2D.asset`, `DOC/MEASURE/nabuild-na/`, `DOC/last/todo-F6-default-maxn-128.md`, три ассета с красными тестами) не трогать и не добавлять.
4. Без `merge`, `rebase`, `push`.

## Часть А0. Исправление: нуль частиц при `AlignFromVelocity`

**Дефект (найден при ревью P9.4.1).** `SimPass.Execute` вызывает `SelectKernel` до проверки `count == 0`. `BuildSpatialHashPass.Execute` при нуле частиц выходит (строка с `ParticleCount == 0`) уже после `VelocityWritten = false` и до диспатча, поэтому `VelocityWritten` остаётся ложным. `SelectVelocityKernel` (проверка `!spatialHash.VelocityWritten`) тогда бросает `InvalidOperationException` каждый кадр. Эффект с `AlignFromVelocity` и нулём частиц (пустой источник, Rebuild с нулевыми спавнами) падает, хотя диспатча быть не должно.

**Исправление.** В `SelectVelocityKernel` проверку `VelocityWritten` не выполнять, если `context.Particles.Count == 0` (вернуть найденный хэндл ядра, `Execute` затем выйдет до диспатча). При `Count > 0` строгость остаётся: ложный `VelocityWritten` по-прежнему бросает. Остальную логику не менять.

**Тест** (в `BoidNeighborForceVelocityAlignTests.cs`, новые методы; существующие не менять):
- мир с нулём частиц и `AlignFromVelocity = true`: `Execute` обоих пассов не бросает, `LastExecuteDispatched` ложен;
- при `Count > 0`, когда хэш не выполнялся (`VelocityWritten` ложен), `Execute` по-прежнему бросает `InvalidOperationException` (существующее поведение сохранено).

## Часть В. Разведка (ответом в сдаче, до кода А)

1. Число EditMode «до» (после А0) и красные.
2. Как устроена фабрика `CreateBoidsHashClassicAsset(path, withPoints)` из P9.2: можно ли получить `Boids_hash_classic_vel` без правки существующего метода (вызвать его и выставить `AlignFromVelocity` на пассе обхода до сохранения, либо копия рецепта). Выбрать путь, при котором существующий метод и его вывод остаются побайтно прежними.
3. `internal`-доступ тестов к `EffectiveAlignFromVelocity` (есть ли `InternalsVisibleTo` для тестовой сборки).
4. Не сканирует ли какой-нибудь тест `Assets/Effects`/`Assets/Scenes` на фиксированное число ассетов или сцен.

## Часть Б. Реализация (коммит А)

**Б1. Фабрика и меню** (`M3DDemoTools.cs`, только добавление строк): константы `Assets/Effects/Boids_hash_classic_vel.asset`, `Assets/Scenes/Boids_HashClassicVel.unity`; метод `CreateBoidsHashClassicVelAsset(string path)`; пункт меню `Tools/M3D/Create Boids Hash Classic Vel Effect` (ассет и сцена, как у `Create Boids Hash Classic Effect`, через `CreateBoidsHashScene`). Состав пресета: **рецепт `Boids_hash_classic` без точек** (`BuildSpatialHash`, `HashCountsToField`, `ClearVelocity`, `BoidNeighborForce`, `TeamClassicSteer`, `Integrate`, `BoxBounds`, `HeadingToValue`; 3000 частиц, `Seed 1`, профиль 4 / 8 / 12 / 5, веса соседей прежние) с единственным отличием: у `BoidNeighborForcePass` `AlignFromVelocity = true`, `WalkMode` остаётся 1 (по умолчанию), `MaxNeighbors 48`, `CountCapHits false`. Существующие методы, константы, пункты меню, ассеты и сцены не менять.

**Б2. Ассет и сцена.** Выполнить пункт меню и закоммитить `.asset`, `.unity` и их `.meta`.

**Б3. Тесты.** Новый файл `Assets/Tests/Editor/BoidsHashClassicVelPresetTests.cs` (каркас `BoidsHashClassicPresetTests`; вспомогательные методы копировать, существующие тест-файлы не править):
1. **Состав:** порядок и типы пассов как у `Boids_hash_classic`; `TeamHeadingSteer` и `SwarmPointForce` отсутствуют; у пасса обхода `AlignFromVelocity == true`, `WalkMode == 1`, `MaxNeighbors 48`; одна команда 4 / 8 / 12 / 5, `Cruise 6`, `Turn 4`; 3000 частиц, `Seed 1`, `MinCellSize 3`, `Wrap`, `SimulationSpeed 1`.
2. **Валидатор без предупреждений** (`SpatialHashValidator`).
3. **Rebuild без `VisualEffect`** держит 3000 частиц.
4. **Smoke на 300 кадров с readback** (образец `Chain_Classic_ThreeHundredFrames_StaysInTheCorridor`). Assert: конечны `position`, `heading`, `velocity`, `inertialVelocity`; позиции внутри бокса; `|v.xz|` каждой частицы в `[4 − 1e-3, 8 + 1e-3]`; `v.y = 0`; `|heading| = 1` и `heading ≈ normalize(velocity)` (1e-3); `teamId` не изменился; частиц 3000; после цепочки `EffectiveAlignFromVelocity` пасса обхода истинен (это показывает, что работал именно вариант по скорости).
5. **Метрики (только лог, без assert):** для `Boids_hash` (кинематика), `Boids_hash_classic` и `Boids_hash_classic_vel` на тех же кадрах и сиде (100, 200, 300 и **600** кадров, потому что консенсус скорости может сходиться медленнее): средняя скорость, **стандартное отклонение скорости** (мера консенсуса), поляризация `|mean(heading)|`, доля частиц у нижней границы (`|v| < 4 + 1e-3`) и у верхней (`|v| > 8 − 1e-3`). Ожидание для сравнения (гипотеза из ADR-057 §4, не критерий): доля у верхней границы заметно ниже, чем у `Boids_hash_classic` (78% к 300 кадру), нижняя граница получает смысл. Если ожидание не оправдалось, привести числа как есть и не делать выводов.

## Часть Г. Приёмка

1. **EditMode:** красных ровно четыре известных; числа «до» (после А0) и «после»; имена новых тестов. Существующие тесты зелёные без правок ожиданий.
2. **`git diff --stat` коммита А0:** только `Assets/Scripts/Passes/BoidsPasses.cs` и `Assets/Tests/Editor/BoidNeighborForceVelocityAlignTests.cs`. **Коммита А:** только `Assets/Scripts/Editor/M3DDemoTools.cs` (в диффе только добавленные строки), один ассет, одна сцена, новый тест-файл и их `.meta`. Другой путь остановка и вопрос.
3. Существующие `Assets/Effects/*` и `Assets/Scenes/*` не изменены (только новые файлы). Ядра и код P9.1, S1, P9.2, P9.4.1 (кроме строки в `SelectVelocityKernel` из А0) не затронуты.
4. Таблица метрик из Б3.5 приведена в сдаче (три пресета, четыре момента времени).

## Часть Д. Play (владелец, после коммита А)

Открыть `Boids_HashClassic.unity` и `Boids_HashClassicVel.unity`, сравнить: держит ли стая форму, плавны ли повороты, исчез ли снос скорости к верхней границе (по метрикам Б3.5 и на глаз: однородность скорости в стае), не появились ли дрожание или схлопывание стаи. Вес alignment в выровненной стае слабеет по определению, поэтому подбор `wAlign` ожидается. Грок по запросу владельца правит число в фабрике (веса соседей профиля) и пересоздаёт ассет; каждая итерация одной строкой в `DOC/last/play-p9-classic.md` (раздел «P9.4.2»: было, стало, что увидел). Решение «принято» за владельцем. Если снос скорости не уйдёт, запасной вариант трение в `TeamClassicSteer` (поле `drag`), отдельным ТЗ.

## Часть Е. Документы (коммит Б)

- **ADR-057 §7:** подраздел «P9.4.2» (хеши, EditMode до и после, ответы разведки, таблица метрик, отклонения); строка «Статус» после ревью Claude: «код P9.4.1 принят 2026-10-30 после исправления нуля частиц (А0), P9.4.2 ждёт Play».
- **`DOC/last/play-p9-classic.md`:** раздел «P9.4.2» с таблицей метрик и пустыми разделами наблюдений и подбора.
- **`DOC/getting-started.md`:** пункт меню `Create Boids Hash Classic Vel Effect` и сцена. **`DOC/status.md`:** строка «Итерация». **`DOC/capabilities.md`:** пресет `Boids_hash_classic_vel`.
- **`DOC/last/Techdebt.md`:** только если разведка нашла долг; номера свободные, старые не перенумеровывать.

## Что присылает Grok

Хеш головы; хеши коммитов 0, А0, А, Б; ответы разведки; число EditMode до и после; список красных; таблица метрик; `git diff --stat` по А0 и А; расхождения с ТЗ; вопросы владельцу.

## Не делать

- Не менять числа профиля (4 / 8 / 12 / 5) и веса соседей: подбор только по запросу владельца после Play.
- Не менять ядра (`BoidNeighborForceSortedVel`, walk 0/1/2, `HashScatter*`, `TeamClassicSteer`), кроме исправления в `SelectVelocityKernel` из А0.
- Не добавлять пресет 200k, пресет с точками, сумму separation, отталкивание чужой команды, трение (`drag`).
- Не трогать три ассета и четыре красных теста.
- Не делать `merge`, `rebase`, `push`.
