# ТЗ P9.4.1: скоростной снимок хэша и обход с alignment по скорости (ADR-057)

**Исполнитель:** Grok (код, тесты, документы). **Ревью:** Claude. Play и вид не проверяются (пресета нет), устройство не нужно.
**Куда положить:** `DOC/last/todo-adr-056-p9-4-1-align-from-velocity.md`.
**Основа:** [ADR-057](../ADR/ADR-057-Align-From-Velocity.md) §2, §5; [ADR-054](../ADR/ADR-054-Classic-Boids-Tail.md), [ADR-048](../ADR/ADR-048-Default-Walk-Mode.md), [ADR-053](../ADR/ADR-053-Sum-Separation.md) §6. Где ТЗ короче ADR, делать по ADR; если ТЗ, ADR и код расходятся, остановиться и спросить владельца.
**Коммиты:** (0) ADR-057 и это ТЗ; (А1) снимок скорости и его тесты; (А2) обход, флаг пасса, валидатор, оракул, тесты; (Б) документы. После А1, А2 и Б EditMode: красных ровно четыре известных (`Preset_MatchesTwoTeams`, `Chain_ThreeHundredFrames_StaysFiniteInsideBox` у `BoidsHashTwoTeams`, `Preset_MatchesDyeInjectComposition`, `Preset_MatchesDyeInjectVcComposition`).

## Часть 0. Git

1. Ветка `experiment/align-from-velocity` от головы `feature/classic-boids` (после закрытия P9, то есть после коммита документов из `todo-adr-054-p9-close.md`; если он ещё не сделан, остановиться и спросить владельца). Записать хеш головы в сдачу.
2. Явные пути включая `.meta` новых файлов; запрещены `git add -A`, `git add .`, `git commit -a`, `git stash`, `git reset`, `git restore`, `git checkout` на файлах вне ТЗ. Перед каждым коммитом `git status` и `git diff --cached --stat`.
3. Чужие файлы (`.gitignore`, `CurlNoise2D.asset`, `DOC/MEASURE/nabuild-na/`, `DOC/last/todo-F6-default-maxn-128.md`, три ассета с красными тестами) не трогать и не добавлять.
4. Без `merge`, `rebase`, `push`.

## Что делаем

Опциональный вариант обхода с alignment по скорости соседей. Включается флагом `BoidNeighborForcePass.AlignFromVelocity` (по умолчанию выключен). Включённый флаг:
- заставляет хэш писать в `SortedDirections` скорость `inertialVelocity.xz`, а не курс;
- выбирает новое ядро `BoidNeighborForceSortedVel`, где alignment равен `((Σ v_j) / alignN − v_i) / maxSpeed · wAlign`.

Выключенный флаг не меняет ничего: все существующие пресеты и тесты работают как раньше. Пресета, Play и подбора чисел нет (P9.4.2).

## Часть А. Разведка (ответом в сдаче, до кода)

1. Число EditMode «до» (голова ветки) и список красных.
2. Как `BuildSpatialHashPass` и `SpatialHashSet` обращаются с `PackedRequested`/`PackedWritten` (флаг, сброс на каждом `Execute`, кто выставляет). Повторить для `VelocityRequested`/`VelocityWritten`.
3. Как `context.Particles.Get(...)` ведёт себя, если атрибут не зарегистрирован, и как атрибуты регистрируются из `Reads`/`Writes` всех пассов (достаточно ли добавить `InertialVelocity` в `Reads` обхода при `AlignFromVelocity`, чтобы он был выделен и обнулён, если `TeamClassicSteerPass` в эффекте нет).
4. Есть ли в `SpatialHashValidator` правило порядка «обход силы стоит до хвоста» для `TeamHeadingSteerPass`; если есть, повторить для `TeamClassicSteerPass` (если такого правила нет, не вводить).
5. Все места, где перечисляются или проверяются ядра обхода или `Reads` пасса (`BoidNeighborForceDefaultsTests`, `BoidNeighborForceRepeatTests`, `BoidsHashPresetsWalkDefaultTests` и др.), чтобы подтвердить, что при выключенном флаге их поведение не меняется.
6. Что делает базовый `ParticleKernelPass.Execute` при нуле частиц и когда зовёт `SelectKernel` (нужно для проверки `VelocityWritten`).
7. `EveryNeighborForcePreset_IsWalk1_AndFitsTheCell` и `WalkMode_RejectsOutOfRange`: подтвердить, что они проходят без правки.

## Часть Б. Реализация

### Коммит А1: снимок скорости

**Б1.** `SpatialHashPasses.compute`: строка `#pragma kernel HashScatterVelocity`, объявление `StructuredBuffer<float3> inertialVelocity;` рядом с другими входами и ядро в конце файла:

```hlsl
[numthreads(THREADS, 1, 1)]
void HashScatterVelocity(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount) return;

    uint slot;
    InterlockedAdd(CellCursors[ParticleCells[id.x]], 1u, slot);

    SortedIndices[slot] = id.x;
    SortedPositions[slot] = position[id.x].xz;
    SortedDirections[slot] = inertialVelocity[id.x].xz;   // единственное отличие от HashScatter
    SortedTeams[slot] = teamId[id.x];
}
```

Остальной текст файла (в том числе `HashScatter` и `HashScatterPacked`) не менять.

**Б2.** `SpatialHashSet`: `internal bool VelocityRequested { get; set; }` и `internal bool VelocityWritten { get; set; }` по образцу `PackedRequested`/`PackedWritten`.

**Б3.** `BuildSpatialHashPass`: `FindVelocityScatter(context)` по образцу `FindPackedScatter` (ядро в том же `ComputeShader`, проверка единого шейдера). В `Execute`: `VelocityWritten = false` в начале; если `VelocityRequested`, использовать `HashScatterVelocity` и привязать `inertialVelocity` из `context.Particles.Get(BuiltinAttributes.InertialVelocity)`; отметить `VelocityWritten = true` после диспатча. Если `VelocityRequested`, а ядра нет, бросить `InvalidOperationException` («velocity snapshot kernel not found»). `VelocityRequested` и `PackedRequested` одновременно взведены быть не должны (упаковка требует `WalkMode 2`, скорость требует `WalkMode 1`): если оба, бросить исключение. Без флага поведение побайтно прежнее.

**Б4. Тесты А1** (новый файл `Assets/Tests/Editor/BuildSpatialHashVelocityTests.cs`, каркас `BuildSpatialHashPassTests`; вспомогательные методы копировать в новый файл):
- с `VelocityRequested` в `SortedDirections[slot]` лежит `inertialVelocity[SortedIndices[slot]].xz` (случайные скорости, разные от курса, несколько клеток, шов тора); `SortedIndices`, `SortedPositions`, `SortedTeams` совпадают с тем, что пишет `HashScatter` на тех же данных;
- без флага `SortedDirections` равен `heading.xz` (как раньше);
- `VelocityWritten` истинен после `Execute` с флагом и ложен без него; на следующем `Execute` без флага сбрасывается;
- флаг без ядра и оба флага сразу бросают `InvalidOperationException`.

### Коммит А2: обход, флаг, валидатор

**Б5. Ядро `BoidNeighborForceSortedVel`.** В `BoidsPasses.compute`: строка `#pragma kernel BoidNeighborForceSortedVel` рядом с остальными и блок **в конце файла** (после `SwarmSpringForce`). Ядро это точная копия действующего `BoidNeighborForceSorted` (включая обход клеток, кап, счётчик `CapHits`), в которой отличается **только концовка силы**:

```hlsl
float2 force = 0;
if (state.sepN > 0)
{
    force += (state.sep / (float)state.sepN) * team.Weights.x;
}

if (state.alignN > 0)
{
    float invMax = team.Classic.y > 1e-6 ? 1.0 / team.Classic.y : 0.0;
    float2 ownV = inertialVelocity[i].xz;
    force += ((state.align / (float)state.alignN) - ownV) * (invMax * team.Weights.y);
}

// cohesion: без изменений относительно walk 1
...
velocity[i] += float3(force.x, 0, force.y);
```

`inertialVelocity` читается через уже существующее объявление `RWStructuredBuffer<float3> inertialVelocity;` в этом файле (оно объявлено до конца файла, ядро стоит после него; если порядок объявлений мешает, объявить выше и сообщить). Вспомогательные функции и `NeighborWalk` не менять. Любая другая правка в файле запрещена.

**Б6. Пасс** (`BoidsPasses.cs`, `BoidNeighborForcePass`):
- `[SerializeField] private bool alignFromVelocity;` и свойство `AlignFromVelocity` (get/set; сеттер при `spatialHash != null` обновляет `spatialHash.VelocityRequested`). `WalkMode` и его диапазон 0..2 не менять.
- `Reads`: при `alignFromVelocity` возвращает набор с `InertialVelocity`, иначе прежний `ReadPositionTeam` (то же поведение, что сейчас).
- `Initialize`: после существующих шагов `spatialHash.VelocityRequested = alignFromVelocity`; найти ядро `BoidNeighborForceSortedVel` (если флаг включён и ядра нет, бросить `InvalidOperationException`). Если флаг включён и `walkMode != 1`, бросить `InvalidOperationException`.
- `SelectKernel`: при `alignFromVelocity` вернуть `BoidNeighborForceSortedVel`, `effectiveWalkMode = 1`, `EffectiveAlignFromVelocity = true`. Если `RadiusBlocksSortedWalk` вернул `true` или `spatialHash.VelocityWritten` ложен, бросить `InvalidOperationException` (не переходить на walk 0, не переходить на alignment по курсу). Без флага логика побайтно прежняя.
- `SetParams`: ядро `Vel` читает тот же набор буферов, что walk 1 (`SortedPositions`, `SortedDirections`, `SortedTeams`, `SortedIndices`, команды); `inertialVelocity` привязывается как атрибут из `Reads`.
- `internal bool EffectiveAlignFromVelocity` для тестов (по умолчанию `false`).

**Б7. Валидатор** (`SpatialHashValidator`): ошибка, если включён `BoidNeighborForcePass` с `AlignFromVelocity` и (а) нет включённого `TeamClassicSteerPass` или (б) `WalkMode != 1`. Правило порядка по разведке А.4. Тексты ошибок называют пасс и причину.

**Б8. Тесты А2** (новый файл `Assets/Tests/Editor/BoidNeighborForceVelocityAlignTests.cs`; каркас и CPU-оракул копировать из `BoidNeighborForceSortedTests` в новый файл, существующий файл не править). Оракул `OracleForceVel` повторяет `OracleForce`, но alignment считает по §2.1 ADR-057 (скорости соседей из массива `velocities`, `maxSpeed` из `Classic.y` команды).
1. **Против оракула:** две команды, тор и без тора, шов, кап выключен и плотный, `teamId > 7`, число частиц не кратно 64, пустые промежутки, 30 случайных сидов (образец `Random_ThirtySeeds_...`). Допуск как в образце.
2. **Якорь «wAlign = 0»:** силы ядра `Vel` совпадают с walk 1 на тех же данных (допуск 1e-6; во входных данных `velocities` отличаются от курсов).
3. **Равные скорости:** свои и соседей равны, вклад alignment равен нулю (общая сила равна силе без alignment).
4. **Пример ADR-057 §2.1:** свой `v = (6,0)`, один сосед `v = (8,0)`, `maxSpeed 8`, `wAlign 0,8`: вклад `(0,2; 0)` (допуск 1e-4); сосед `v = (−6,0)`: `(−1,2; 0)`. Соседи на расстоянии внутри радиуса alignment, вне радиусов separation и cohesion (или с нулевыми весами этих правил).
5. **Нет соседей:** вклад alignment равен нулю.
6. **Независимость от `heading`:** при одинаковых скоростях и разных курсах сила не меняется.
7. **Чужая команда не входит** в alignment.
8. **Контракт пасса:** `AlignFromVelocity` по умолчанию `false`; `WalkMode` по-прежнему принимает 0..2 и бросает на 3 (это ожидание существующего теста, не менять); `Reads` содержит `InertialVelocity` только при `true`; `EffectiveAlignFromVelocity` ложен без флага и истинен с флагом; `EffectiveWalkMode == 1` с флагом; исключения: флаг при `WalkMode` 0 или 2, флаг при радиусе больше ячейки (блок `RadiusBlocksSortedWalk`), отсутствие ядра.
9. **Валидатор:** флаг без `TeamClassicSteerPass`; флаг при `WalkMode != 1`; корректная связка проходит без ошибок; выключенный флаг не вызывает проверок.
10. **Двухкадровая цепочка** (`BuildSpatialHash`, `ClearVelocity`, обход с флагом, `TeamClassicSteer`, `Integrate`, два-три кадра, две-четыре частицы): на втором кадре alignment использует скорости, записанные хвостом на первом (снимок свежий); на первом кадре вклад alignment равен нулю.

## Часть В. Приёмка

1. **EditMode:** красных ровно четыре известных; числа «до» и «после»; имена новых тестов. Все существующие тесты зелёные без правок ожиданий.
2. **`git diff --stat`** по А1 и А2 содержит только: `Assets/Scripts/Passes/BoidsPasses.cs`, `Assets/Scripts/Passes/SpatialHashPasses.cs`, `Assets/Scripts/Core/SpatialHashSet.cs`, `Assets/Scripts/Runtime/SpatialHashValidator.cs`, `Assets/Shaders/GPU/Passes/BoidsPasses.compute`, `Assets/Shaders/GPU/Passes/SpatialHashPasses.compute`, два новых тест-файла с `.meta`. Другой путь остановка и вопрос.
3. **Шейдеры:** `BoidsPasses.compute` в диффе содержит только новую строку `#pragma kernel` и блок в конце файла; `SpatialHashPasses.compute` только строку `#pragma kernel`, объявление `inertialVelocity` и блок ядра. Текст `BoidNeighborForce`, `BoidNeighborForceSorted`, `BoidNeighborForceSortedPacked`, `HashScatter`, `HashScatterPacked`, `TeamClassicSteer`, `SwarmPointForce`, `SwarmSpringForce` не затронут.
4. `git diff` по `Assets/Effects` и `Assets/Scenes` пуст.

## Часть Г. Документы (коммит Б)

- **ADR-057 §7 «Результат»:** разведка, хеши, EditMode до и после, отклонения.
- **`DOC/pass-catalog.md`:** «Boid Neighbor Force»: опция `AlignFromVelocity` (формула, требования: `WalkMode 1`, `TeamClassicSteer`, строгие ошибки вместо запасного пути); «Build Spatial Hash»: снимок скорости по запросу потребителя. **`DOC/capabilities.md`**, **`DOC/status.md`** (строка «Итерация»).
- **`DOC/last/Techdebt.md`:** (а) ядро `BoidNeighborForceSortedVel` дублирует walk 1, правки walk 1 нужно переносить вручную; (б) первый кадр alignment по скорости равен нулю, пока хвост не записал скорость (безвредно, но зафиксировать). Следующие свободные номера, старые не перенумеровывать.

## Что присылает Grok

Хеш головы перед работой; хеши коммитов 0, А1, А2, Б; ответы разведки; число EditMode до и после; список красных в конце; `git diff --stat` по А1 и А2; расхождения с ТЗ; вопросы владельцу.

## Не делать

- Не создавать пресеты, сцены, меню (P9.4.2), не запускать Play, не подбирать числа.
- Не менять `WalkMode` и его диапазон, `TeamClassicSteer`, обходы 0, 1, 2, `HashScatter`, `HashScatterPacked`, точечные пассы, существующие тесты, фабрики, ассеты.
- Не добавлять сумму separation, отталкивание чужой команды, трение (`drag`).
- Не вводить запасной переход на alignment по курсу.
- Не трогать три ассета с красными тестами.
- Не делать `merge`, `rebase`, `push`.
