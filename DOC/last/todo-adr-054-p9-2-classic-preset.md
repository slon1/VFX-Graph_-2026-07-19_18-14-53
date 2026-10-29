# ТЗ P9.2: пресеты классических боидов, сцены и метрики (ADR-054)

**Исполнитель:** Grok (фабрика, ассеты, сцены, тесты, документы). **Ревью:** Claude. **Play и подбор чисел:** владелец (часть Г).
**Куда положить:** `DOC/last/todo-adr-054-p9-2-classic-preset.md`.
**Основа:** [ADR-054](../ADR/ADR-054-Classic-Boids-Tail.md) §2.5, §5 (P9.2), [ADR-055](../ADR/ADR-055-Swarm-Spring-Force.md). Где ТЗ короче ADR, делать по ADR; если ТЗ, ADR и код расходятся, остановиться и спросить владельца.
**Решения владельца 2026-10-28 (записать в ADR-054 §8):** стартовые числа 4 / 8 / 12 / 5 подходят; замер S200 только информационный; для точек берём нормализованный `Swarm Point Force`, пружину позже.
**Коммиты:** (0) это ТЗ; (А) фабрика, ассеты, сцены, тесты; (Б) документы. EditMode после А и Б красных ровно четыре известных (`Preset_MatchesTwoTeams`, `Chain_ThreeHundredFrames_StaysFiniteInsideBox` у `BoidsHashTwoTeams`, `Preset_MatchesDyeInjectComposition`, `Preset_MatchesDyeInjectVcComposition`). Решение по тем трём ассетам владелец ещё не принял; эта фаза их не трогает.

## Часть 0. Git

1. Ветка `feature/classic-boids`, голова `69a6ed2fc7e88f076fb89c7c5b01d7f6b325e6b7` или потомок (записать хеш в сдачу).
2. Явные пути включая `.meta` новых файлов; запрещены `git add -A`, `git add .`, `git commit -a`, `git stash`, `git reset`, `git restore`, `git checkout` на файлах вне ТЗ. Перед каждым коммитом `git status` и `git diff --cached --stat`.
3. Чужие файлы (`.gitignore`, `CurlNoise2D.asset`, `DOC/MEASURE/nabuild-na/`, `DOC/last/todo-F6-default-maxn-128.md`, три ассета с красными тестами) не трогать и не добавлять.
4. Без `merge`, `rebase`, `push`.

## Что делаем

Два новых пресета с классическим хвостом и по сцене на каждый, чтобы владелец сравнил их с `Boids_hash` в Play:
- `Boids_hash_classic`: рецепт `Boids_hash` с заменой `TeamHeadingSteer` на `TeamClassicSteer`. Сравнивает ровно хвост.
- `Boids_hash_classic_points`: то же плюс нормализованный `SwarmPointForce` и три точки как в `Boids_hash_points` (цель и отталкивающая точка; заодно данные для решения по P9.3).

Плюс тесты пресетов и метрики по readback. Числа не подбираются, формулы и существующий код не меняются.

## Часть А. Разведка (ответом в сдаче, до кода)

1. Число EditMode «до» (голова ветки) и список красных.
2. `CreateBoidsHashScene(scenePath, assetPath)` и `EnsurePassLibrary` общие (годятся для нового пути сцены и ассета без правки)? Подтвердить.
3. `BoidsHashPresetsWalkDefaultTests.EveryNeighborForcePreset_IsWalk1_AndFitsTheCell`: как он перечисляет пресеты (по именам или сканом папки) и будут ли новые ассеты им проверены; нужно ли что-то добавлять в его список. Не править без подтверждения владельца.
4. Нет ли других тестов или кода, которые сканируют `Assets/Effects` и `Assets/Scenes` и ждут фиксированное число ассетов или сцен.
5. `Boids_hash_points.asset`: все поля, отличные от рецепта `CreateBoidsHashAsset` (кроме пасса точек и списка точек): `particleSize`, градиент, `MaxNeighbors`, `CountCapHits`, `walk`, `simulationSpeed`, поле, спавны. Таблица отличий; пресет `classic_points` берёт значения рецепта `Boids_hash`, а не этого ассета, если они расходятся (написать, где расходятся).
6. API для задания точек эффекта из кода: как создаются `SwarmPoint` и добавляются в `EffectAsset` (образец: тесты `SwarmSpringForceTests`).

## Часть Б. Реализация (коммит А)

**Б1. Константы и фабрики** в `M3DDemoTools.cs` (только добавление строк):
- пути: `Assets/Effects/Boids_hash_classic.asset`, `Assets/Effects/Boids_hash_classic_points.asset`, сцены `Assets/Scenes/Boids_HashClassic.unity`, `Assets/Scenes/Boids_HashClassicPoints.unity`;
- метод `CreateBoidsHashClassicAsset(string path, bool withPoints)`: **копия рецепта `CreateBoidsHashAsset`** (хэш `MinCellSize 3`, `Wrap true`, `Extents (45,0,45)`, `HashCountsToField` для `hashCount` 30×30, `ClearVelocity`, `BoidNeighborForce { MaxNeighbors = 48, CountCapHits = false }`, `Integrate`, `BoxBounds` в режиме Wrap, `HeadingToValue`, спавн 3000, `Seed 1`, радиус 44, `particleSize 0.5`, градиент) со следующими отличиями:
  - `TeamHeadingSteerPass` заменён на `TeamClassicSteerPass`;
  - при `withPoints` между `BoidNeighborForce` и `TeamClassicSteer` стоит `SwarmPointForcePass` (нормализованный) и в эффекте три точки, как в `Boids_hash_points`: `(-20, 15)` вес `0,6` радиус `10` край `0,25`; `(18, 20)` вес `1,5` радиус `10` край `0,25`; `(0, -22)` вес `-1,5` радиус `12` край `0,25`;
  - профиль команды: прежние `Cruise 6`, `Turn 4` (хвост их не читает, профиль остаётся сопоставимым), плюс `MinSpeed 4`, `MaxSpeed 8`, `MaxForce 12`, `ForceGain 5`;
- два пункта меню `Tools/M3D/Create Boids Hash Classic Effect` и `Tools/M3D/Create Boids Hash Classic Points Effect`: каждый пишет ассет и сцену (как `CreateBoidsHashEffect`, включая `SaveCurrentModifiedScenesIfUserWantsTo`).
- Существующие методы, константы, пункты меню и их вывод не меняются. Общий вспомогательный код не выносить: старая фабрика остаётся побайтно прежней.

**Б2. Ассеты и сцены.** Выполнить оба пункта меню в редакторе и закоммитить результат: два `.asset`, две `.unity` и их `.meta`.

**Б3. Тесты.** Новый файл `Assets/Tests/Editor/BoidsHashClassicPresetTests.cs` (каркас `BoidsHashPresetTests`; вспомогательные методы копировать в новый файл, чужие тест-файлы не править). Для обоих пресетов:
1. **Состав.** Точный список и порядок пассов: `BuildSpatialHash`, `HashCountsToField`, `ClearVelocity`, `BoidNeighborForce`, (`SwarmPointForce` только у `points`), `TeamClassicSteer`, `Integrate`, `BoxBounds`, `HeadingToValue`; `TeamHeadingSteer` отсутствует; одна команда с числами 4 / 8 / 12 / 5; `Cruise 6`, `Turn 4`; спавн 3000, `Seed 1`; `MinCellSize 3`, `Wrap`; `MaxNeighbors 48`; `SimulationSpeed 1`; у `points` три точки с значениями из Б1.
2. **Валидатор без предупреждений** (`SpatialHashValidator`, у `points` ещё `SwarmPointValidator`).
3. **Rebuild без `VisualEffect`** держит 3000 частиц (образец из `BoidsHashPresetTests`).
4. **Smoke на 300 кадров** с readback (образец `Chain_ThreeHundredFrames_StaysFiniteInsideBox`; `DeltaTime` как в образце). Проверки (assert): конечны `position`, `heading`, `velocity`, `inertialVelocity`; позиции внутри бокса (тор) с тем же допуском, что в образце; `|v.xz|` для каждой частицы в `[4 − 1e-3, 8 + 1e-3]`; `v.y = 0`; `|heading| = 1` (допуск 1e-3); `heading ≈ normalize(velocity)` (1e-3); `teamId` не изменился; частиц 3000.
5. **Метрики (только лог, без assert):** для каждого пресета через 100, 200, 300 кадров: средняя скорость, поляризация (`|mean(heading)|`), доля частиц у нижней границы (`|v| < 4 + 1e-3`) и у верхней (`|v| > 8 − 1e-3`). Для `classic` те же метрики на `Boids_hash` (из `Boids_hash.asset`, тот же seed, те же кадры) для сравнения. Для `classic_points` дополнительно по отталкивающей точке `(0, -22)`: минимальное расстояние до её центра, доля частиц ближе 6 (половина радиуса) и ближе 9 (граница плато), считая расстояние с учётом тора.
6. Существующие тесты не менять. Если `EveryNeighborForcePreset_IsWalk1_AndFitsTheCell` сканирует папку, новые пресеты должны его проходить; иначе сообщить по А.3.

## Часть В. Приёмка

1. EditMode: красных ровно четыре известных; числа «до» и «после»; имена новых тестов.
2. `git diff --stat` коммита А содержит только: `Assets/Scripts/Editor/M3DDemoTools.cs` (в диффе только добавленные строки), два ассета, две сцены, новый тест-файл, их `.meta`. Другой путь остановка и вопрос.
3. Существующие `Assets/Effects/*` и `Assets/Scenes/*` не изменены (в диффе только новые файлы). `TeamClassicSteer`, `SwarmSpringForce` и другой код P9.1/S1 не затронуты.
4. Метрики из Б3.5 приведены таблицей в сдаче (для `Boids_hash`, `classic`, `classic_points`, три момента времени).

## Часть Г. Play (владелец, после коммита А)

Открыть `Boids_Hash.unity` и `Boids_HashClassic.unity`, затем `Boids_HashClassicPoints.unity`. Смотреть: держит ли стая форму, плавны ли повороты, нет ли разрыва стаи и швов тора, дрейфует ли скорость к границам (по метрикам Б3.5), тянут ли притягивающие точки и отклоняет ли отталкивающая (проникают ли частицы внутрь, см. долю ближе 6 и 9). Замер S200 информационный, в этой фазе пресета 200k нет. Грок по запросу владельца правит числа профиля или точек в фабрике и пересоздаёт ассет; каждая итерация одна строка в `DOC/last/play-p9-classic.md` (было, стало, что увидел). Решение «принято» за владельцем.

## Часть Д. Документы (коммит Б)

- **ADR-054 §8:** подраздел «P9.2» (хеши, EditMode до и после, ответы разведки А, таблица метрик, решения владельца 2026-10-28, отклонения). Пункты §7.2 и §7.3 помечены закрытыми, §7.1 остаётся открытым до Play.
- **`DOC/pass-catalog.md`:** в записи «Team Classic Steer» поле «Хорошо для» заменить на указание пресетов `Boids_hash_classic`, `Boids_hash_classic_points`. **`DOC/capabilities.md`**, **`DOC/getting-started.md`** (два новых пункта меню и сцены), **`DOC/status.md`** (строка «Итерация»).
- **`DOC/last/play-p9-classic.md`:** создать с таблицей метрик и пустыми разделами для наблюдений владельца и подбора чисел.
- **`DOC/last/Techdebt.md`:** только если разведка нашла долг.

## Что присылает Grok

Хеш головы; хеши коммитов 0, А, Б; ответы части А; число EditMode до и после; список красных в конце; таблица метрик; `git diff --stat` по А; расхождения с ТЗ; вопросы владельцу.

## Не делать

- Не менять числа профиля (4 / 8 / 12 / 5), веса соседей, точки и спавн: подбор идёт только по запросу владельца после Play.
- Не менять `TeamClassicSteer`, `SwarmPointForce`, `SwarmSpringForce`, `TeamHeadingSteer`, обходы, хэш, существующие фабрики, пункты меню, ассеты и сцены.
- Не добавлять пасс препятствий, alignment по скорости, пресет 200k, пружинный вариант пресета.
- Не трогать три ассета из решения владельца (`Boids_hash_2teams`, `Fluid2D_DyeInject`, `Fluid2D_DyeInjectVC`) и четыре красных теста.
- Не делать `merge`, `rebase`, `push`.
