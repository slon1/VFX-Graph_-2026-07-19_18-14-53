# ТЗ: точки притяжения и отталкивания роя (`SwarmPointForce`)

**Исполнитель:** Grok (код, тесты, документы). **Просмотр вида и замер на устройстве:** владелец. **Ревью:** Claude.
**Куда положить:** `DOC/last/todo-swarm-points.md`. Общие правила §0 действуют.
**Основа:** [ADR-049](../ADR/ADR-049-Swarm-Point-Forces.md) (решение владельца 2026-10-20), [ADR-048](../ADR/ADR-048-Default-Walk-Mode.md). База кадра для замера: S200, режим B, `walk 1`, **40,59 цикла на частицу** (сборка `cb661ba`, `DOC/MEASURE/cb661ba-ccae6602/`).
**Коммиты:** (А) код, ассеты, тесты; (Б) документы. Каждый коммит отдельно, EditMode зелёный в каждом.

## Что делаем

Новый пасс `SwarmPointForcePass` с ядром `SwarmPointForce`: каждая частица любой команды получает `velocity += float3(f.x, 0, f.y)`, где `f = Σ normalize(d_k) · weight_k · профиль_k(|d_k|)`, `d_k` кратчайший XZ-вектор к точке. Без `dt`, без `teamId`, без чтения хэша (только его параметры тора). Пасс стоит в списке эффекта между `BoidNeighborForce` и `TeamHeadingSteer`. Точки задаются в редакторе в эффекте и после старта не меняются.

## Часть А. Разведка (до кода, ответом в сдаче; код этой части не меняет)

1. Подтвердить, что `ParticleKernelPass.SelectKernel` может вернуть невалидный `KernelHandle` и тогда диспатча нет (так делается пропуск при нуле точек).
2. Как `SpatialHashValidator`, `RepeatCountValidator` подключены в `SimulationWorld` (вызов около строки 215), в каком виде возвращают предупреждения.
3. Как `SpatialHashSet` пушит `HashSize` и `HashWrap` (`PushParams`): достаточно ли того, что их уже установил `BoidNeighborForce` в этом же шейдере в этом кадре, или пассу точек нужно пушить самому (эффект без `BoidNeighborForce`, но с хэшем; эффект без хэша: тогда `HashWrap = 0` привязать явно). Выбрать вариант, который не зависит от порядка соседних пассов.
4. Где в `SimulationWorld` покадрово перезаливается `Teams` (около строки 95), где освобождается `teamBuffer`: туда же добавить буфер точек.
5. Как `EffectAssetEditor` рисует список команд: переиспользовать для точек.

## Часть Б. Реализация (коммит А)

**Б1. Данные.**
- `SwarmPoint` (`[Serializable]`, рядом с `TeamProfile`): `Vector2 positionXZ` (в инспекторе «Position XZ»; `[Tooltip("Позиция в плоскости XZ; Y не используется")]`, без PropertyDrawer), `float weight` (по умолчанию 0,6, диапазон −10..10), `float radius` (≥ 0, по умолчанию 10; 0 значит без границы), `float edge` (0..1, по умолчанию 0,25). `const int MaxPoints = 16`.
- `EffectAsset`: `[SerializeField] List<SwarmPoint> swarmPoints`, `IReadOnlyList<SwarmPoint> SwarmPoints` (null-защита, как у `Teams`), `SetSwarmPoints(...)`. Существующие эффекты загружаются без правок, список пуст.
- GPU-структура `SwarmPointParams`: два `float4` (32 байта): `(x, z, weight, radius)` и `(edge, 0, 0, 0)`. `Stride = 32`, статический `Upload(GraphicsBuffer, IReadOnlyList<SwarmPoint>, scratch)` по образцу `TeamParams.Upload` (буфер ровно `MaxPoints` слотов, остальные нули, лишние точки отбрасываются).
- `SimContext`: `GraphicsBuffer SwarmPoints`, `int SwarmPointCount` (число живых точек, не больше 16).
- `SimulationWorld`: буфер создаётся при инициализации только если `effect.SwarmPoints.Count > 0`, заливается при инициализации и каждый кадр рядом с `Teams` (дёшево, живая подстройка весов в Play), освобождается там же, где `teamBuffer`.

**Б2. Пасс.** `SwarmPointForcePass : ParticleKernelPass` в `BoidsPasses.cs`, `[Serializable]`, `DisplayName` «Swarm Point Force», `Category = PassCategory.Force`, `Reads = AttrSets.Position`, `Writes = AttrSets.Velocity`, `KernelName = "SwarmPointForce"`. `SetParams`: привязать `SwarmPoints`, `SwarmPointCount`. Тор не зависит от порядка пассов: если `context.SpatialHash != null`, вызвать `PushParams(cmd, Kernel.Shader)` (пишет `HashWrap` и `HashSize`, их читает `MinImage`); иначе явно записать `HashWrap = 0` в тот же шейдер. `SelectKernel` возвращает невалидный handle, если `context.SwarmPoints == null` или `SwarmPointCount == 0` (диспатча нет, `LastExecuteDispatched == false`).

**Б3. Ядро.** В `BoidsPasses.compute`: строка `#pragma kernel SwarmPointForce` рядом с остальными `#pragma kernel` и блок ядра **в конце файла**. Больше ничего в файле не менять (критерий: `git diff` по файлу содержит только эти две вставки). Спецификация:

```
StructuredBuffer<float4> SwarmPoints; // слот 2k: (x, z, weight, radius); слот 2k+1: (edge, 0, 0, 0)
uint SwarmPointCount;

[numthreads(THREADS, 1, 1)]
void SwarmPointForce(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount) return;
    float2 x = position[id.x].xz;
    float2 f = 0;
    [loop] for (uint k = 0; k < SwarmPointCount; k++)
    {
        float4 a = SwarmPoints[2 * k];
        float4 b = SwarmPoints[2 * k + 1];
        float2 d = MinImage(a.xy - x);
        float dist = length(d);
        if (dist <= 1e-5) continue;
        float prof = 1;
        if (a.w > 0)
            prof = (b.x > 0) ? 1 - smoothstep(a.w * (1 - b.x), a.w, dist) : (dist < a.w ? 1 : 0);
        f += (d / dist) * (a.z * prof);
    }
    velocity[id.x] += float3(f.x, 0, f.y);
}
```

Имена буферов `position`, `velocity` и `THREADS` как в остальных ядрах файла. Если `MinImage` в этом `.compute` ещё не доступен (включение `SpatialHash.hlsl`), он уже подключён (его использует `walk 0`); подтвердить в сдаче.

**Б4. Валидатор.** `SwarmPointValidator` (в `Runtime`, по образцу `SpatialHashValidator`), вызывается из `SimulationWorld` там же. Предупреждения (текст обязан называть пасс и причину):
1. `SwarmPointForcePass` стоит после `TeamHeadingSteer` или до `ClearVelocity`.
2. В списке эффекта больше одного `SwarmPointForcePass`.
3. В `swarmPoints` больше 16 точек (лишние отбрасываются).
4. Точка с `weight < 0` и `radius == 0` при включённом `HashWrap` (линия сбора на противоположной стороне тора, ADR-049 §2.3).
Если пасс есть, а список точек пуст, предупреждения нет (пасс просто ничего не делает).

**Б5. Редактор.** Список точек в инспекторе эффекта (`EffectAssetEditor`). Гизмо в сцене при выделении `SimulationWorld`: диск радиуса и диск плато `radius·(1−edge)` в плоскости XZ вокруг каждой точки, зелёный для притяжения, красный для отталкивания, центр маркером. Ручки перетаскивания позиций (Handles) необязательны, если делать, то отдельным коммитом после приёмки.

**Б6. Эффект для приёмки.** `Assets/Effects/Boids_hash_points.asset`: копия `Boids_hash` с `SwarmPointForcePass` между `BoidNeighborForce` и `TeamHeadingSteer` и тремя точками: притяжение `weight 0,6`, притяжение `weight 1,5`, отталкивание `weight −1,5` с радиусом 12 (положения выбрать внутри мира, чтобы каждая была видна в кадре камеры 46). Для замера на устройстве копия `Boids_hash200k` с тем же пассом и четырьмя точками (`Boids_hash200k_points`) и сцена к ней по образцу сцены S200; в Build Settings сцену добавляет владелец. Существующие эффекты и пресеты **не менять**.

**Б7. Тесты EditMode** (в стиле `TeamHeadingSteerTests`, GPU, существующий каркас).
1. Независимость от `dt`: два разных `DeltaTime` дают одинаковую добавку в `velocity` (бит в бит).
2. Величина и знак: одна точка `weight 0,6`, частица на известном смещении: добавка равна `0,6 · unit(d)` в допуске 1e-6; `weight −0,6` даёт противоположную добавку.
3. Тор: точка и частица по разные стороны шва, `wrap` включён: направление через шов; `wrap` выключен: прямая разность.
4. Профиль: радиус 10, `edge 0,5`: на 4 полный вес, на 7,5 половина, на 10 и 12 ноль; `edge 0`: на 9,9 полный, на 10,1 ноль; радиус 0: вес на расстоянии 1000 (при выключенном `wrap`) полный.
5. Сумма: несколько точек дают сумму отдельных вкладов (допуск 1e-6); 16 точек работают, 17-я отбрасывается.
6. `velocity.y` не меняется при любом `position.y`; добавка `y` равна 0.
7. Пасс добавляет, а не перезаписывает: заданная `velocity` `(1,0,0)` плюс добавка.
8. Расстояние меньше 1e-5: `velocity` не изменилась, нет NaN.
9. Ноль точек: `LastExecuteDispatched == false`.
10. **Курс поворачивается:** `BoidNeighborForce` или заданная сила плюс `SwarmPointForce` плюс `TeamHeadingSteer`: частица с курсом `(1,0,0)` и точкой притяжения со стороны `+z` и большим весом: после шага курс повернулся в сторону `+z` (знак `heading.z > 0`); при `weight 0` курс не меняется; при `weight < 0` поворот в сторону `−z`.
11. Валидатор: каждое из четырёх предупреждений срабатывает на своём примере, на правильном порядке предупреждений нет.
12. Раскладка: `SwarmPointParams.Stride == 32`, значения в слотах `2k` и `2k+1` как в Б1.
13. Загрузка прежних эффектов: `swarmPoints` пуст, пасса нет, прежние тесты (включая тест пресетов из F3c) зелёные без изменения ожиданий.

## Часть В. Приёмка

1. **EditMode:** зелёный, число «до» снять фактическим прогоном перед правками, число «после» в сдаче.
2. **Файл ядра:** `git diff` по `BoidsPasses.compute` показывает только `#pragma` и блок в конце; `git diff` по `ForcePasses.compute` и `DynamicsPasses.compute` пуст.
3. **Play (Grok):** `Boids_Hash` с пассом и тремя точками (Б6), скриншоты на 1 с, 15 с и 30 с, отдельно при `weight` около 0,6, 1,5 и −1,5. Ожидание: притяжение сносит и изгибает стаи, отталкивание огибается, на противоположной от отталкивающей точки стороне мира нет скопления. Положения частиц не сравнивать (хаос, см. контроль F3c), судить по виду. **Решение о виде принимает владелец.**
4. **Устройство (владелец, после приёмки вида):** `Boids_hash200k_points`, S200, режим B, три повтора и один профиль Streamline, сборка из чистого коммита, заряд не ниже 50%, `powersave=0`. Прирост не больше 5% от базы 40,59 (то есть не больше 42,6 цикла на частицу), иначе разбор. Метка `vsync` в сводке остаётся.

## Часть Г. Документы (коммит Б)

- `ADR-049`: заполнить §6 «Результат» (что сделано, ответы на разведку А, число EditMode до и после, отклонения от ТЗ).
- `DOC/pass-catalog.md` и `DOC/capabilities.md`: запись «Swarm Point Force» (место в кадре, семантика, шкала весов 0,6 / 1–1,5 / более 2,6).
- `DOC/architecture.md`: в описание `SimContext` рядом с `Teams` добавить `SwarmPoints`, `SwarmPointCount` (если `Teams` там описан).
- `DOC/status.md`: строка «Итерация».
- `DOC/last/Techdebt.md`: (1) ручки позиций в сцене (фаза 2); (2) заметка для будущей половинной силы: сила соседей живёт в `velocity`, которую очищает `ClearVelocity`, нужен отдельный постоянный буфер.

## Что присылает Grok

Файлы (с `.meta`), хеши коммитов, ответы части А, число EditMode до и после, скриншоты Play, расхождения с ТЗ, вопросы владельцу.

## Не делать

- Не менять `PointForce`, `Attractor`, `Repulsor` и их ядро; не менять `BoidNeighborForce`, `TeamHeadingSteer`, хэш-ядра, `walkMode`, режимы `walk`.
- Не создавать новый `.compute`-файл и не менять `passLibrary` сцен (ядро идёт в `BoidsPasses.compute`).
- Не добавлять маску команд, движение точек во время игры, чтение `teamId`.
- Не добавлять пасс в существующие эффекты и пресеты, не менять их значения.
- Не начинать P8, F5.
- Не править сырые данные `DOC/MEASURE/`, прежние `todo-*.md`, ADR-048.
