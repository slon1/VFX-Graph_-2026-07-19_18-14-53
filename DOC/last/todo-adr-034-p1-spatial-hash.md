# ТЗ: ADR-034 P1 — spatial hash (ресурс, пасс, валидатор, пробник)

**Статус:** EditMode зелёный (2026-10-03). Замер S10 записан в [`status.md`](../status.md).
**Исполнитель:** Grok. Одна фаза. Сила соседей, команды, `SwarmSource`, палитра — **не** в этой фазе.

Перед кодом прочитать [`ADR-034-Spatial-Hash-And-Teams.md`](../ADR/ADR-034-Spatial-Hash-And-Teams.md) §1–§3 и [`plan-Reynolds-Boids-v2.md`](../plan-Reynolds-Boids-v2.md) §2.2 и P1. Где план и ADR расходятся с этим ТЗ, делать по ТЗ и написать об этом в сдаче.

**Не трогать:**
- `DynamicsPasses.compute`, `HeadingSteerPass`, `BoxBoundsPass`;
- все существующие пассы и их `.compute`;
- `ParticleSet`, `FieldSet`, `FieldTestHarness`, `PrimitiveParticleBinder`, `ParticleBillboard.shader`;
- `EffectAsset`, `EffectAssetEditor`;
- `Boids_mk1.asset` и все существующие ассеты в `Assets/Effects/`;
- `Assets/Scenes/Test1.unity` — не открывать на запись и не сохранять;
- `Boids_Rivalry/`.

В `SimPass.cs` разрешена только одна строка в `AttrSets` (§1).

**Git:** не коммитить. Запрещены `git add -A`, `git add .`, `git commit -a`, `stash`, а также `checkout`/`restore`/`reset` на любых файлах. В репозитории есть чужие незакоммиченные правки (`Boids_mk1.asset`, `Physarum_Fluid.asset`, `Test1.unity`, `Mobile_RPAsset.asset`, `UniversalRenderPipelineGlobalSettings.asset`), их не трогать.

---

## 1. Атрибут `teamId`

`Assets/Scripts/Core/BuiltinAttributes.cs`:

```csharp
/// <summary>Swarm membership (ADR-034). Zero when no source writes it.</summary>
public static readonly AttributeId TeamId = new AttributeId("teamId", AttributeType.UInt, true);
```

`Assets/Scripts/Runtime/SimPass.cs`, класс `AttrSets`:

```csharp
public static readonly AttributeId[] PositionHeadingTeam =
    { BuiltinAttributes.Position, BuiltinAttributes.Heading, BuiltinAttributes.TeamId };
```

Больше в `SimPass.cs` ничего не менять.

## 2. Include `SpatialHash.hlsl`

Новый файл `Assets/Shaders/GPU/Includes/SpatialHash.hlsl`. Код дословно:

```hlsl
#ifndef M3D_SPATIAL_HASH_INCLUDED
#define M3D_SPATIAL_HASH_INCLUDED

// XZ grid of the spatial hash (ADR-034 §2). Pushed by SpatialHashSet.PushParams.
float2 HashOrigin;
float2 HashSize;
float2 HashCellSize;
int2 HashRes;
int HashWrap;
uint HashCellCount;

int2 HashCellCoord(float2 xz)
{
    int2 c = (int2)floor((xz - HashOrigin) / HashCellSize);
    if (HashWrap != 0)
    {
        c = ((c % HashRes) + HashRes) % HashRes;
    }
    else
    {
        c = clamp(c, int2(0, 0), HashRes - 1);
    }

    return c;
}

// min() keeps a NaN/huge position inside the buffers.
uint HashCellIndex(int2 c)
{
    uint index = (uint)(c.y * HashRes.x + c.x);
    return min(index, HashCellCount - 1u);
}

#endif
```

`%` в HLSL для `int` сохраняет знак делимого, поэтому нужна форма `((c % r) + r) % r`. Позиция ровно на верхней границе даёт `c = res` и уходит в 0. Позиция чуть ниже нижней даёт `c = -1` и уходит в `res - 1`. Clamp только при `HashWrap == 0`.

Функции соседей (`HashNeighborCell`, `HashMinImage`) в P1 **не добавлять**. Они появятся в P5 вместе с тестами.

## 3. Кернелы `SpatialHashPasses.compute`

Новый файл `Assets/Shaders/GPU/Passes/SpatialHashPasses.compute`. Код дословно:

```hlsl
// Spatial hash build over the XZ plane (ADR-034 §3). Six kernels per Execute.
#pragma kernel HashClear
#pragma kernel HashCount
#pragma kernel HashScanBlocks
#pragma kernel HashScanBlockSums
#pragma kernel HashAddOffsets
#pragma kernel HashScatter

#include "Assets/Shaders/GPU/Includes/SpatialHash.hlsl"

#define THREADS 64
#define SCAN_BLOCK_SIZE 256

StructuredBuffer<float3> position;
StructuredBuffer<float3> heading;
StructuredBuffer<uint> teamId;
uint ParticleCount;
uint HashBlockCount;

RWStructuredBuffer<uint> CellCounts;
RWStructuredBuffer<uint> CellStarts;
RWStructuredBuffer<uint> CellCursors;
RWStructuredBuffer<uint> BlockSums;
RWStructuredBuffer<uint> BlockOffsets;
RWStructuredBuffer<uint> ParticleCells;
RWStructuredBuffer<uint> SortedIndices;
RWStructuredBuffer<float2> SortedPositions;
RWStructuredBuffer<float2> SortedDirections;
RWStructuredBuffer<uint> SortedTeams;

groupshared uint ScanTemp[SCAN_BLOCK_SIZE];

// Blelloch exclusive scan of ScanTemp (port of Rivalry GridUtils PrefixSum_ScanBlocks).
// Must be called from uniform control flow by all SCAN_BLOCK_SIZE threads.
uint ExclusiveScanBlock(uint tid)
{
    GroupMemoryBarrierWithGroupSync();

    for (uint up = 1u; up < SCAN_BLOCK_SIZE; up <<= 1u)
    {
        uint idx = (tid + 1u) * up * 2u - 1u;
        if (idx < SCAN_BLOCK_SIZE)
        {
            ScanTemp[idx] += ScanTemp[idx - up];
        }

        GroupMemoryBarrierWithGroupSync();
    }

    uint total = ScanTemp[SCAN_BLOCK_SIZE - 1u];
    GroupMemoryBarrierWithGroupSync();

    if (tid == 0u)
    {
        ScanTemp[SCAN_BLOCK_SIZE - 1u] = 0u;
    }

    GroupMemoryBarrierWithGroupSync();

    for (uint down = SCAN_BLOCK_SIZE >> 1u; down > 0u; down >>= 1u)
    {
        uint idx = (tid + 1u) * down * 2u - 1u;
        if (idx < SCAN_BLOCK_SIZE)
        {
            uint t = ScanTemp[idx - down];
            ScanTemp[idx - down] = ScanTemp[idx];
            ScanTemp[idx] += t;
        }

        GroupMemoryBarrierWithGroupSync();
    }

    return total;
}

[numthreads(THREADS, 1, 1)]
void HashClear(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= HashCellCount)
    {
        return;
    }

    CellCounts[id.x] = 0u;
}

[numthreads(THREADS, 1, 1)]
void HashCount(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount)
    {
        return;
    }

    uint cell = HashCellIndex(HashCellCoord(position[id.x].xz));
    ParticleCells[id.x] = cell;
    InterlockedAdd(CellCounts[cell], 1u);
}

[numthreads(SCAN_BLOCK_SIZE, 1, 1)]
void HashScanBlocks(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    uint tid = groupThreadId.x;
    uint cell = groupId.x * SCAN_BLOCK_SIZE + tid;
    ScanTemp[tid] = (cell < HashCellCount) ? CellCounts[cell] : 0u;

    uint total = ExclusiveScanBlock(tid);

    if (tid == 0u)
    {
        BlockSums[groupId.x] = total;
    }

    if (cell < HashCellCount)
    {
        CellStarts[cell] = ScanTemp[tid];
    }
}

[numthreads(SCAN_BLOCK_SIZE, 1, 1)]
void HashScanBlockSums(uint3 groupThreadId : SV_GroupThreadID)
{
    uint tid = groupThreadId.x;
    ScanTemp[tid] = (tid < HashBlockCount) ? BlockSums[tid] : 0u;

    ExclusiveScanBlock(tid);

    if (tid < HashBlockCount)
    {
        BlockOffsets[tid] = ScanTemp[tid];
    }
}

[numthreads(SCAN_BLOCK_SIZE, 1, 1)]
void HashAddOffsets(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    uint cell = groupId.x * SCAN_BLOCK_SIZE + groupThreadId.x;
    if (cell >= HashCellCount)
    {
        return;
    }

    uint start = CellStarts[cell] + BlockOffsets[groupId.x];
    CellStarts[cell] = start;
    CellCursors[cell] = start;
}

[numthreads(THREADS, 1, 1)]
void HashScatter(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount)
    {
        return;
    }

    uint slot;
    InterlockedAdd(CellCursors[ParticleCells[id.x]], 1u, slot);

    SortedIndices[slot] = id.x;
    SortedPositions[slot] = position[id.x].xz;
    SortedDirections[slot] = heading[id.x].xz;
    SortedTeams[slot] = teamId[id.x];
}
```

Диспатчи:
- `HashClear`: `ceil(CellCount / 64)`;
- `HashCount`, `HashScatter`: `ceil(ParticleCount / 64)`;
- `HashScanBlocks`, `HashAddOffsets`: `BlockCount = ceil(CellCount / 256)`;
- `HashScanBlockSums`: 1 группа.

`BlockCount <= 256` гарантирует лимит `CellCount <= 65536`.

Не оптимизировать:
- без groupshared-гистограммы;
- без Morton-индекса;
- без слияния `HashCount` со скатом.

Не менять формулу `ExclusiveScanBlock`: барьер в начале нужен, потому что вызывающий кернел только что записал `ScanTemp`.

## 4. `SpatialHashSet` и раскладка сетки

Новый файл `Assets/Scripts/Core/SpatialHashSet.cs`.

```csharp
public readonly struct SpatialHashLayout
{
    public readonly Vector2 Origin;
    public readonly Vector2 Size;
    public readonly Vector2 CellSize;
    public readonly Vector2Int Resolution;
    // ctor со всеми четырьмя полями
    public int CellCount => Resolution.x * Resolution.y;
    public int BlockCount => (CellCount + SpatialHashSet.ScanBlockSize - 1) / SpatialHashSet.ScanBlockSize;
}

public sealed class SpatialHashSet : IDisposable
{
    public const int ScanBlockSize = 256;
    public const int MaxCells = ScanBlockSize * ScanBlockSize;   // 65536
    public const float GridEpsilon = 1e-5f;

    public SpatialHashLayout Layout { get; }
    public bool Wrap { get; }
    public int ParticleCount { get; }

    internal GraphicsBuffer CellCounts { get; private set; }      // uint, CellCount
    internal GraphicsBuffer CellStarts { get; private set; }      // uint, CellCount
    internal GraphicsBuffer CellCursors { get; private set; }     // uint, CellCount
    internal GraphicsBuffer BlockSums { get; private set; }       // uint, ScanBlockSize
    internal GraphicsBuffer BlockOffsets { get; private set; }    // uint, ScanBlockSize
    internal GraphicsBuffer ParticleCells { get; private set; }   // uint, ParticleCount
    internal GraphicsBuffer SortedIndices { get; private set; }   // uint, ParticleCount
    internal GraphicsBuffer SortedPositions { get; private set; } // float2 (stride 8), ParticleCount
    internal GraphicsBuffer SortedDirections { get; private set; }// float2 (stride 8), ParticleCount
    internal GraphicsBuffer SortedTeams { get; private set; }     // uint, ParticleCount

    public SpatialHashSet(SpatialHashLayout layout, bool wrap, int particleCount);
    public static SpatialHashLayout ComputeLayout(Vector3 center, Vector3 extents, float minCellSize);
    public static void ValidateLayout(SpatialHashLayout layout, bool wrap);
    internal void PushParams(CommandBuffer cmd, ComputeShader shader);
    public void Dispose();
}
```

**`ComputeLayout`.** Бросает `ArgumentOutOfRangeException`, если `minCellSize <= 0`, `extents.x <= 0`, `extents.z <= 0` или любое из них не конечно. Y не используется. Считать в `double`:

```
size.x = 2 * extents.x
res.x  = floor(size.x / minCellSize * (1 + GridEpsilon)), не меньше 1, не больше MaxCells + 1
cell.x = size.x / res.x
origin = (center.x - extents.x, center.z - extents.z)
```

Ось Z считается так же. Кламп сверху `MaxCells + 1` нужен только от переполнения `int`, проверка лимита — в `ValidateLayout`. Коррекции `res` до 3 **нет**.

**`ValidateLayout`.** Бросает `InvalidOperationException` с числами в тексте в двух случаях:
- `(long)res.x * res.y > MaxCells`;
- `wrap && (res.x < 3 || res.y < 3)`. Текст: «wrap needs at least 3 cells per axis; the wrapped neighbour cells would repeat».

**Конструктор.**
- Cell-буферы и блочные буферы `GraphicsBuffer.Target.Structured` создаются всегда.
- Буферы частиц — только при `particleCount > 0`. Нулевой размер `GraphicsBuffer` не допускает.
- `particleCount < 0` — `ArgumentOutOfRangeException`.

**`PushParams`.** Ставит униформы из §2 и `HashBlockCount`:
- `HashOrigin`, `HashSize`, `HashCellSize` — `SetComputeVectorParam` с `(x, y, 0, 0)`;
- `HashRes` — `SetComputeIntParams(shader, id, res.x, res.y, 0, 0)`, как `FieldShaderParams.Push`;
- `HashWrap`, `HashCellCount`, `HashBlockCount` — `SetComputeIntParam`.

ID через `Shader.PropertyToID` в `static readonly` полях.

**`Dispose`.** Освобождает все буферы, обнуляет ссылки. Повторный вызов безопасен.

`BindForRead` и прочий API потребителя в P1 **не делать**. Он появится в P5.

## 5. Пасс `BuildSpatialHashPass`

Новый файл `Assets/Scripts/Passes/SpatialHashPasses.cs`.

```csharp
/// <summary>Marks passes that read SimContext.SpatialHash; validated to sit after the builder (ADR-034 §1).</summary>
internal interface ISpatialHashConsumer
{
}

/// <summary>
/// Builds the XZ spatial hash and a cell-sorted snapshot of position/heading/teamId (ADR-034).
/// Owns the SpatialHashSet; six kernels per Execute.
/// </summary>
[Serializable]
public sealed class BuildSpatialHashPass : SimPass, IDisposable
```

Поля:

| Поле | Тип | Дефолт | Атрибут |
| --- | --- | --- | --- |
| `center` | `Vector3` | `Vector3.zero` | `[SerializeField]` |
| `extents` | `Vector3` | `(16, 0, 16)` | `[SerializeField]` |
| `minCellSize` | `float` | `2` | `[SerializeField, Min(1e-3f)]` |
| `wrap` | `bool` | `true` | `[SerializeField]` |

Публичные get/set: `Center`, `Extents`, `MinCellSize`, `Wrap`. Рантайм-поля помечать `[NonSerialized]`, по образцу `ZeroMeanScalarPass`: `SpatialHashSet hash`, шесть `KernelHandle`. `internal SpatialHashSet Hash => hash;` нужен для тестов.

Контракт:
- `DisplayName` — `"Build Spatial Hash"`;
- `Category` — `PassCategory.Emit` (раскладка частиц в структуру, как P2G);
- `Reads` — `AttrSets.PositionHeadingTeam`;
- `Writes` — `AttrSets.None`;
- `RepeatCount` не переопределять.

**`Initialize(context)`, строго в этом порядке:**
1. Если `context.SpatialHash != null`, бросить `InvalidOperationException("SimulationWorld: only one enabled 'Build Spatial Hash' pass per effect.")`.
2. `FindKernel` для шести имён. Если у любого `Shader` отличается от первого, бросить `InvalidOperationException`: униформы ставятся один раз на шейдер.
3. `layout = SpatialHashSet.ComputeLayout(center, extents, minCellSize)`, затем `SpatialHashSet.ValidateLayout(layout, wrap)`.
4. `count = context.Particles != null ? context.Particles.Count : 0`.
5. Если `count > 0` и нет хотя бы одного из атрибутов `position`/`heading`/`teamId` (`TryGet`), бросить `InvalidOperationException` с именем атрибута. В мире `AutoRegisterAttributes` уже создаёт их по `Reads`.
6. `Dispose()` старого хэша, `hash = new SpatialHashSet(layout, wrap, count)`, `context.SpatialHash = hash`.

**`Execute(context, dt)`.**
- `LastExecuteDispatched = false`.
- Ранний выход, если `hash == null`, `hash.ParticleCount == 0` или любой кернел невалиден.
- `hash.PushParams(cmd, shader)`, затем `ParticleCount` через `SimShaderIds.ParticleCount`.
- Биндинг буферов на каждый кернел через `cmd.SetComputeBufferParam` (имена из §3), шесть `DispatchCompute` по таблице §3.
- Атрибуты: `context.Particles.Get(BuiltinAttributes.Position / Heading / TeamId)`.
- В конце `LastExecuteDispatched = true`.

`dt` не используется.

**`Dispose()`.** `hash?.Dispose(); hash = null;`. Повторный вызов безопасен.

`SimContext` (`Assets/Scripts/Runtime/SimContext.cs`) — добавить одно свойство, конструктор не трогать:

```csharp
/// <summary>Set by BuildSpatialHashPass.Initialize (ADR-034). Null when the effect has no builder.</summary>
public SpatialHashSet SpatialHash { get; internal set; }
```

## 6. Валидатор, мир, пробник

### 6.1 `SpatialHashValidator`

Новый файл `Assets/Scripts/Runtime/SpatialHashValidator.cs`.

```csharp
internal static class SpatialHashValidator
{
    /// <summary>Throws InvalidOperationException on errors; returns warnings for the caller to log.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<SimPass> passes);
}
```

Учитываются только включённые пассы (`pass != null && pass.Enabled`), как в `RepeatCountValidator`.

Ошибки (`InvalidOperationException`):
1. Больше одного `BuildSpatialHashPass`.
2. Есть `ISpatialHashConsumer`, а билдера нет. Или consumer стоит в списке **раньше** билдера.
3. Ошибки `ComputeLayout` и `ValidateLayout` пробрасываются как есть.
4. `wrap == true`:
   - нет ни одного `BoxBoundsPass` с `Behaviour == Wrap` — ошибка. Текст объясняет фантомных соседей через шов;
   - любой `BoxBoundsPass` с режимом Bounce — ошибка.
5. `wrap == false`: любой `BoxBoundsPass` с режимом Wrap — ошибка.
6. Любой включённый `BoxBoundsPass`: `|center.x - b.center.x|`, `|center.z - b.center.z|`, `|extents.x - b.extents.x|`, `|extents.z - b.extents.z|` — всё не больше `1e-3`, иначе ошибка с обоими значениями. Y не сравнивать.

Предупреждение: `wrap == false` и нет ни одного `BoxBoundsPass` — «particles may leave the hash grid and pile up in edge cells».

Без билдера и без consumer — пустой список, ничего не проверяется.

### 6.2 `SimulationWorld`

В `Build`, в существующем `try` после `SquareTexelValidator.Validate(...)`:

```csharp
IReadOnlyList<string> hashWarnings = SpatialHashValidator.Validate(effect.Passes);
for (int i = 0; i < hashWarnings.Count; i++)
{
    Debug.LogWarning(hashWarnings[i], this);
}
```

Переключатель рендера, только для замера:

```csharp
[SerializeField] private bool renderParticles = true;

/// <summary>Probe switch (ADR-034 P1): false skips PrimitiveParticleBinder.Execute. No Rebuild needed.</summary>
public bool RenderParticles
{
    get => renderParticles;
    set => renderParticles = value;
}
```

В `Update`, в цикле биндеров:

```csharp
if (!renderParticles && binders[i] is PrimitiveParticleBinder)
{
    continue;
}
```

`SetupBinders`, `Teardown`, `ParticleSet` и порядок `Build` не менять.

### 6.3 Пробник `HashProbeControls`

Новый файл `Assets/Scripts/Runtime/HashProbeControls.cs`, `MonoBehaviour`:
- `[SerializeField] SimulationWorld world`, `[SerializeField] string label` (например `"30k"`).
- `OnEnable`: `Application.targetFrameRate = 120`. На Android по умолчанию 30, и замер был бы бессмысленным. Экран S10 всё равно ограничит 60.
- `Update`: скользящее среднее `Time.unscaledDeltaTime` (`ema = lerp(ema, dt, 0.05)`).
- `OnGUI`: масштаб `GUI.matrix = Matrix4x4.Scale(Vector3.one * Screen.height / 720f)`. Строка `"{label}  {1/ema:F1} FPS  {ema*1000:F2} ms  hash:{on/off}  render:{on/off}"` и две кнопки:
  - «Hash» переключает `Enabled` у всех `BuildSpatialHashPass` в `world.Effect.Passes`. Потребителей в пробнике нет, мир пропускает выключенный пасс. Rebuild не нужен.
  - «Render» переключает `world.RenderParticles`.
- `OnDisable`: вернуть `Enabled = true` тем билдерам, которые выключил этот компонент. Иначе в Editor выключенный пасс останется в ассете после выхода из Play.

### 6.4 Меню и ассеты пробника

`M3DDemoTools`, меню `Tools/M3D/Create Spatial Hash Probe`. Образец — `CreatePhysarumEffect`.

Два ассета через существующий `CreateEffect`, пустые массивы полей и квадов:

| Ассет | `CubeSource.resolution` | Частиц |
| --- | --- | --- |
| `Assets/Effects/HashProbe_30k.asset` | 31 | 29 791 |
| `Assets/Effects/HashProbe_100k.asset` | 46 | 97 336 |

Общее у обоих ассетов:
- `cubeSize = 32`, `simulationSpeed = 1`, `particleSize = 0.2`. Через `SerializedObject`, как у Physarum.
- Пассы ровно в этом порядке:
  1. `BuildSpatialHashPass` — center 0, extents `(16, 0, 16)`, minCellSize 2, wrap `true`. Сетка 16 × 16.
  2. `ClearVelocityPass`.
  3. `HeadingSteerPass` — дефолты. Сила нулевая, курс берёт запасной `(1,0,0)`, частицы едут по +X.
  4. `IntegratePass`.
  5. `BoxBoundsPass` — center 0, extents `(16, 0, 16)`, Wrap.

Две **новые** сцены: `Assets/Scenes/HashProbe_30k.unity` и `Assets/Scenes/HashProbe_100k.unity`.
- Создать через `EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single)`. Перед этим вызвать `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()`; если пользователь отказался, выйти.
- Камера: позиция `(0, 40, 0)`, поворот `(90, 0, 0)`.
- Объект `M3D Probe` с `SimulationWorld`:
  - `effect` — ассет;
  - `passLibrary` — из `PassLibraryPaths`;
  - `visualEffect = null`, `inputRouter = null`;
  - на том же объекте `HashProbeControls` с `world` и `label`.
- Сохранить сцену в свой путь (`EditorSceneManager.SaveScene(scene, path)`).

`Test1.unity` меню не открывает и не сохраняет. Build Settings не трогать: сцену в билд добавит владелец.

## 7. Библиотека шейдеров

В `M3DDemoTools.PassLibraryPaths` добавить `"Assets/Shaders/GPU/Passes/SpatialHashPasses.compute"` последним элементом.

Сцену `Test1` не переписывать: `passLibrary` там обновит владелец, когда понадобится, или следующее Assign-меню.

## 8. Тесты (EditMode, `Assets/Tests/Editor/`)

Общие правила:
- GPU-тесты: `Assume.That(SystemInfo.supportsComputeShaders)`, шейдер грузится через `AssetDatabase.LoadAssetAtPath<ComputeShader>`.
- Каждый тест строит свой `ParticleSet`, `new FieldSet()`, свой `CommandBuffer` и `new SimContext(particles, fields, new[] { hashShader }, null)` с `ctx.Cmd = cmd`. Образец — `PhysarumSteerGpuTests`.
- Прогон: `pass.Initialize(ctx); pass.Execute(ctx, 0f); Graphics.ExecuteCommandBuffer(cmd); cmd.Clear();`, затем `GetData` из буферов `pass.Hash`.
- `TearDown` освобождает pass, particles, fields, cmd.

**Случайные позиции и граница ячейки.** CPU-эталон считает ячейку той же формулой во `float`, но деление на GPU может отличаться в последнем бите. Генератор отбрасывает точки ближе `1e-3 * cell` к любой границе ячейки по X или Z. Только фиксированный seed, `System.Random`, не `UnityEngine.Random`.

**Эталон.** Для каждой ячейки множество `SortedIndices[CellStarts[c] .. CellStarts[c] + CellCounts[c])` равно множеству частиц этой ячейки по CPU. Порядок внутри ячейки **не** сравнивать: атомарный scatter недетерминирован.

### `SpatialHashGpuTests`

| Тест | Сетка | Проверка |
| --- | --- | --- |
| `CellSets_MatchCpu_MultiBlock` | extents `(20,0,20)`, min 1, wrap, 40×40 = 1600 ячеек (7 блоков), N = 5000 | эталон, `Σ CellCounts == N`, `CellStarts` неубывающий, `CellStarts[0] == 0` |
| `CellSets_MatchCpu_256Cells` | extents `(8,0,8)`, min 1, wrap, 16×16 | эталон, ровно один блок |
| `CellSets_MatchCpu_257Cells` | extents `(128.5,0,0.5)`, min 1, **wrap = false**, 257×1 | эталон, два блока, второй с одной ячейкой |
| `CellSets_MatchCpu_65536Cells` | extents `(128,0,128)`, min 1, wrap, 256×256, N = 2000 | эталон, 256 блоков |
| `Snapshot_MatchesSourceAttributes` | 1600 ячеек, N = 1000, случайные `heading` и `teamId` 0..7 | для каждого слота `k`: `SortedPositions[k] == position[SortedIndices[k]].xz`, то же для `SortedDirections` (из `heading`) и `SortedTeams`; точное равенство (копия без арифметики); `SortedIndices` — перестановка `0..N-1` |
| `DenseSingleCell` | 16×16, все N = 1000 в одной точке центра ячейки `(3,5)` | `CellCounts` этой ячейки 1000, остальные 0 |
| `Wrap_UpperBoundary_GoesToFirstCell` | extents `(8,0,8)`, min 2, wrap, 8×8, cell 2 (точные числа) | частица `(8, 0, 1)` в ячейке `x = 0`; частица `(1, 0, 8)` в ячейке `z = 0` |
| `Wrap_BelowLowerBoundary_GoesToLastCell` | та же сетка | `x = следующее float ниже -8` в ячейке `x = 7`, аналогично по Z |
| `NoWrap_OutOfRange_IsClamped` | та же сетка, wrap = false | `x = 100` → ячейка 7, `x = -100` → 0 |
| `ZeroParticles_ExecuteIsNoOp` | `Count == 0`, атрибуты не регистрируются | `Initialize` и `Execute` не бросают, `LastExecuteDispatched == false`, `Hash.ParticleCount == 0` |

«Следующее float ниже -8»: `BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(-8f) + 1)`. Для отрицательного числа рост битов увеличивает модуль. Не `-8f - 1e-7f`: при модуле 8 такой сдвиг не представим и даёт ровно `-8`.

Для `ZeroParticles` `ParticleSet` без `EnsureCapacity` (Count = 0): `RegisterAttribute` при нулевой ёмкости бросает.

### `SpatialHashLayoutTests` (без GPU)

| Тест | Проверка |
| --- | --- |
| `ComputeLayout_CellWithinTolerance` | наборы `(extents, min)`: `(45, 3)` → res 30; `(0.15, 0.1)` → res 3; `(10, 3)` → res 6; `(16, 2)` → res 16. Для каждого `cell >= min * (1 - 1e-5)` и `abs(res * cell - size) <= 1e-5 * size`. Строгое `>=` не писать |
| `ComputeLayout_InvalidInputs_Throw` | `min = 0`, `min < 0`, `extents.x = 0`, `extents.z < 0`, `NaN` → `ArgumentOutOfRangeException` |
| `ValidateLayout_WrapNeedsThreeCells` | extents `(2,0,2)`, min 2 → res 2: wrap бросает, без wrap не бросает |
| `ValidateLayout_SingleCellWithoutWrap_Ok` | extents `(1,0,1)`, min 2 → res 1, wrap = false: не бросает |
| `ValidateLayout_TooManyCells_Throws` | extents `(128.5,0,128)`, min 1 → 257×256 → бросает при любом wrap |

### `BuildSpatialHashPassTests` (без GPU, кроме отмеченных)

- `Contract`: имя, категория `Emit`, `Reads == AttrSets.PositionHeadingTeam`, `Writes == AttrSets.None`, `RepeatCount == 1`, дефолты из таблицы §5, `pass is IDisposable`.
- `SecondBuilder_OnSameContext_Throws` (GPU): второй `Initialize` на том же `ctx` бросает `InvalidOperationException`.
- `Initialize_MissingTeamId_Throws` (GPU): `Count > 0`, зарегистрированы только `position` и `heading`.
- `Dispose_Twice_DoesNotThrow` (GPU): после `Dispose` `Hash == null`.

### `SpatialHashValidatorTests` (без GPU, списки пассов в памяти)

В тестовом файле объявить `FakeHashConsumerPass : SimPass, ISpatialHashConsumer` с пустыми `Initialize`/`Execute`.

| Тест | Ожидание |
| --- | --- |
| `Valid_WrapWithMatchingBounds` | builder + `BoxBounds` Wrap `(0,(16,0,16))` → без исключения, 0 предупреждений |
| `TwoBuilders_Throw` / `DisabledSecondBuilder_Ok` | исключение / нет |
| `ConsumerBeforeBuilder_Throws`, `ConsumerWithoutBuilder_Throws`, `ConsumerAfterBuilder_Ok` | как в названии |
| `Wrap_NoBoxBounds_Throws` | исключение |
| `Wrap_DisabledBoxBounds_Throws` | выключенный `BoxBounds` не считается |
| `Wrap_BounceBoxBounds_Throws` | исключение |
| `ExtentsMismatch_Throws`, `CenterMismatch_Throws` | разница 0.01 → исключение; разница 1e-4 → нет |
| `ExtentsY_Ignored` | builder `extents.y = 0`, bounds `extents.y = 5` → нет исключения |
| `NoWrap_NoBoxBounds_Warns` | без исключения, ровно 1 предупреждение |
| `NoWrap_WrapBoxBounds_Throws` | исключение |
| `LayoutError_Propagates` | wrap, extents `(2,0,2)`, min 2 → `InvalidOperationException` |

### `SpatialHashWorldTests` (GPU)

Образец — `SimulationWorldWithoutVisualEffectTests`: `EffectAsset` в памяти, Cube `resolution = 4`, `cubeSize = 4` (позиции ±2). Библиотека: `DynamicsPasses.compute` и `SpatialHashPasses.compute`.

- `Build_WithHash_SetsContextSpatialHash`. Пассы: `BuildSpatialHash(extents (2,0,2), min 1, wrap)`, `Integrate`, `BoxBounds Wrap (0,(2,0,2))`. `Rebuild()` не бросает, `world.enabled == true`. Приватный `context` через reflection: `SpatialHash != null`, `SpatialHash.ParticleCount == 64`.
- `Build_WrapWithoutBounds_DisablesWorld`. Без `BoxBounds`, `LogAssert.Expect(LogType.Error, new Regex("phantom|BoxBounds"))`, затем `world.enabled == false`.
- `RenderParticles_DefaultTrue`. У нового `SimulationWorld` `RenderParticles == true`.

### `SpatialHashProbePresetTests` (после меню §6.4)

Загрузить оба ассета, Create из теста не вызывать.
- `CubeSource.resolution` 31 / 46, `cubeSize` 32.
- Пять пассов в порядке §6.4.
- Builder: extents `(16,0,16)`, min 2, wrap.
- `BoxBounds`: Wrap, те же extents.
- `particleSize == 0.2`.

Все существующие тесты проекта зелёные без правок.

## 9. Документы

Коротко, без пересказа ADR:

- `DOC/pass-catalog.md` — `Build Spatial Hash` (Emit; reads position/heading/teamId; нет `dt`; wrap и инварианты; снимок; consumer'ов пока нет).
- `DOC/architecture.md` — абзац: `SpatialHashSet` — ресурс, которым владеет пасс, доступ через `SimContext.SpatialHash`. Не третий ресурс мира (ADR-034 §1).
- `DOC/status.md` — запись «ADR-034 P1 — spatial hash (EditMode)», замер S10 не сделан.
- `DOC/getting-started.md` — меню `Create Spatial Hash Probe`, две сцены, как мерить (§10).
- `DOC/last/Techdebt.md` — п. 11: «снят ADR-034, хэш в P1». П. 12 не трогать по смыслу, только приписать «хэш не стал ресурсом мира, триггер не сработал (ADR-034)».
- `capabilities.md` не трогать: пользовательской возможности ещё нет.
- ADR-034 и план не переписывать.

## 10. Сдача и гейт

Сдача Grok:
1. Список созданных и изменённых файлов, включая `.meta` новых файлов, ассеты и сцены пробника.
2. Результат прогона **всех** EditMode-тестов: число, имена упавших.
3. Отклонения от ТЗ с причиной. Если код из §2–§3 пришлось поменять, показать дифф.

Дальше Claude ревьюит кернелы построчно и сам прогоняет тесты через MCP.

Гейт после «принято» (делает владелец, на S10):
1. Собрать билд со сценой `HashProbe_30k`, затем `HashProbe_100k`.
2. Для каждой сцены четыре режима по 10 с: хэш вкл/выкл × рендер вкл/выкл. Записать FPS и мс.
3. Если все четыре упёрлись в 60 FPS, цифра ничего не говорит о хэше. Тогда опираться на `100k`.
4. Цифры уходят в `status.md`. Решение по гипотезе «20–30k на S10» принимается по ним до начала P2.
