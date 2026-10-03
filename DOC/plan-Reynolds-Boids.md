## ADR-034 + ТЗ: Reynolds Boids — от единого источника спавна до grid-based steering

**Контекст:** M3D Framework, сразу после Physarum (ADR-032). Полностью самодостаточный документ — не опирается на внешний контекст, все ссылки — на реально существующий код текущего репозитория.

### Зачем этот документ так устроен

Пять фаз, каждая — самостоятельный, проверяемый шаг, не блок одной большой задачи. Порядок выбран по одному принципу, уже многократно доказавшему себя в этом проекте (`SwapFieldsPass` перед `GrayScottPass`, `AgentFieldEcho` перед полным Gray-Scott-boids, `flockVel`-based Tier-1 boids перед этим самым тикетом): **сначала доказать самый рискованный, самый новый примитив в изоляции, потом наращивать сложность поверх него.** Самый рискованный, никогда не реализованный в этом проекте примитив — spatial hash (Фаза 3). Всё, что до неё, — подготовка данных; всё, что после — потребители.

---

## Фаза 1 — Unified Swarm Source

### Контекст

Сейчас источники частиц (`Assets/Scripts/Sources/`) — три отдельных класса (`CubeSource`, `BitmapSource`, `MeshSource`), каждый со своей генерацией позиций, реализующие `IDataSource` (`Setup(ParticleSet)`/`Tick(ParticleSet)` — чисто CPU-side, один вызов `ParticleSet.EnsureCapacity` + `RegisterAttribute`+`SetData`, без GPU-кернелов спавна). Ни один не умеет: несколько независимых роёв с разными стартовыми направлениями, `teamId`, цвет команды.

### Решение

#### 1.1 — новые атрибуты частиц

В `BuiltinAttributes.cs` (тот же паттерн, что `Heading`, `Value`):

csharp

```csharp
public static readonly AttributeId TeamId = new AttributeId("teamId", AttributeType.UInt, true);
public static readonly AttributeId ParticleColor = new AttributeId("particleColor", AttributeType.Float4, true);
```

`AttributeType.UInt` уже существует в перечислении — не требует нового типа.

#### 1.2 — `SpawnDomain` (форма + плотность, CPU-side)

csharp

```csharp
public enum DensityShape { Cube, Disk, Sphere, Bitmap }

[Serializable]
public sealed class SpawnDomain
{
    public Vector3 center = Vector3.zero;
    public Vector3 size = new Vector3(10, 0, 10);
    public DensityShape shape = DensityShape.Cube;
    public Texture2D bitmapMask; // используется только при shape == Bitmap
    public Vector3Int resolution = new Vector3Int(32, 1, 32);
    [Range(0f, 1f)] public float threshold = 0.5f;
}
```

`EvaluateDensity(SpawnDomain domain, Vector3 uvw)` — чистая C#-функция, `switch(shape)`: `Cube→1`, `Disk→ (length(uvw.xz-0.5)<0.5 ? 1:0)`, `Sphere→` 3D-аналог, `Bitmap→ bitmapMask.GetPixelBilinear(uvw.x, uvw.z).r`. Никакого GPU-кернела и `#if`-ветвления в HLSL — вся плотность считается один раз при `Setup()`.

#### 1.3 — `SwarmProfile` (один рой)

csharp

```csharp
[Serializable]
public sealed class SwarmProfile
{
    public string swarmName;
    public uint teamId;
    public SpawnDomain domain;
    public Vector3 initialDirection = Vector3.forward;
    public float initialSpeed = 5f;
    [Range(0f, 180f)] public float directionSpreadDegrees = 15f;
    public Color color = Color.white;
}
```

#### 1.4 — `SwarmSource : IDataSource`

Новый класс в `Assets/Scripts/Sources/`, рядом с `CubeSource`. CPU rejection sampling по сетке `resolution` каждого `SwarmProfile`, накопление в `List<>`, **один** вызов `EnsureCapacity(итоговое число)` — контракт `ParticleSet` (capacity фиксируется один раз, дальше не растёт) не нарушается, потому что итоговое число известно **до** вызова.

csharp

```csharp
public sealed class SwarmSource : IDataSource
{
    [SerializeField] private SwarmProfile[] swarms;
    public string Name => "Swarm";

    public void Setup(ParticleSet particles)
    {
        List<Vector3> positions = new();
        List<Vector3> velocities = new();
        List<uint> teamIds = new();
        List<Vector4> colors = new();

        foreach (SwarmProfile swarm in swarms)
        {
            for (int z = 0; z < swarm.domain.resolution.z; z++)
            for (int y = 0; y < swarm.domain.resolution.y; y++)
            for (int x = 0; x < swarm.domain.resolution.x; x++)
            {
                Vector3 uvw = new Vector3(
                    (x + 0.5f) / swarm.domain.resolution.x,
                    swarm.domain.resolution.y > 1 ? (y + 0.5f) / swarm.domain.resolution.y : 0.5f,
                    (z + 0.5f) / swarm.domain.resolution.z);

                if (EvaluateDensity(swarm.domain, uvw) < swarm.domain.threshold) continue;

                positions.Add(DomainUvwToWorld(swarm.domain, uvw));
                Vector3 dir = RandomConeDirection(swarm.initialDirection, swarm.directionSpreadDegrees);
                velocities.Add(dir * swarm.initialSpeed);
                teamIds.Add(swarm.teamId);
                colors.Add(swarm.color);
            }
        }

        particles.EnsureCapacity(positions.Count);
        particles.RegisterAttribute(BuiltinAttributes.RestPosition).SetData(positions.ToArray());
        particles.RegisterAttribute(BuiltinAttributes.Velocity).SetData(velocities.ToArray());
        particles.RegisterAttribute(BuiltinAttributes.TeamId).SetData(teamIds.ToArray());
        particles.RegisterAttribute(BuiltinAttributes.ParticleColor).SetData(colors.ToArray());
    }

    public void Tick(ParticleSet particles) { }
}
```

`DomainUvwToWorld`/`RandomConeDirection` — тривиальные хелперы (linear remap `uvw→center+((uvw-0.5)*size)`; равномерный случайный вектор в конусе вокруг `initialDirection` с половинным углом `directionSpreadDegrees`).

`CubeSource`/`BitmapSource`/`MeshSource` не трогаются — `SwarmSource` добавляется как альтернатива, не замена.

#### DoD Фазы 1

Контракт-тест: `SwarmSource.Setup` с двумя профилями (`teamId=0`/`teamId=1`, разные `center`) даёт частицы обеих команд в ожидаемых половинах домена, `teamId`/`color`-буферы корректно заполнены по длине и содержимому. Ручная проверка: новый временный `EffectAsset` с `SwarmSource` (два профиля, `Cube`-shape) — Play, частицы двух команд визуально разделены при старте.

---

## Фаза 2 — Градиент цвета частиц по скорости/направлению

### Контекст

`particleColor` (Фаза 1) — статичный цвет команды. Нужна возможность красить частицу **динамически**, каждый кадр, по текущей `velocity` — без новых атрибутов (`velocity` уже есть).

### Решение

#### 2.1 — вынести bake LUT в общую утилиту

Сейчас `BakeLutTexture(Gradient)` — `private static` метод внутри `Assets/Scripts/Runtime/FieldDebugQuadsBinder.cs` (строка ~209), используется для полевых debug-quad'ов. Вынести в `internal static class LutTextureUtility` (новый файл `Assets/Scripts/Runtime/LutTextureUtility.cs`), сохранив сигнатуру/поведение (256×1 текстура, `Clamp`+`Bilinear`, `HideFlags.HideAndDontSave`). Обновить `FieldDebugQuadsBinder`, чтобы звал `LutTextureUtility.Bake(gradient)` вместо своего приватного метода — регрессии по существующим полевым quad'ам быть не должно (та же реализация, другое место).

#### 2.2 — расширение `PrimitiveParticleBinder`/`ParticleBillboard.shader`

`PrimitiveParticleBinder.cs` (`Assets/Scripts/Runtime/`) и `ParticleBillboard.shader` (`Assets/Shaders/GPU/`) уже существуют (рендер частиц через `Graphics.RenderPrimitives`, читает `_Positions`/`_Size`/`_Color` напрямую из `GraphicsBuffer`, без CPU-копии). Расширить:

csharp

```csharp
public enum ParticleColorMode { Constant = 0, SpeedGradient = 1, DirectionGradient = 2 }
```

На `EffectAsset` (рядом с `particleSize`/`particleColor` из ADR-030):

csharp

```csharp
[SerializeField] private ParticleColorMode particleColorMode = ParticleColorMode.Constant;
[SerializeField] private Gradient particleColorGradient = DebugFieldQuadSlot.DefaultFireGradient();
[SerializeField] private float particleColorMaxSpeed = 10f;
[SerializeField] private Vector3 particleColorAxis = Vector3.up;
```

`PrimitiveParticleBinder.Initialize` печёт `Texture2D` через `LutTextureUtility.Bake(...)` один раз (не per-frame), биндит `_LutTex`, `_ColorMode`, `_ColorMaxSpeed`, `_ColorAxis`. `Execute` дополнительно биндит `_Velocities` (тот же `context.Particles.Get(BuiltinAttributes.Velocity)`, без копии, напрямую как `_Positions`).

В `ParticleBillboard.shader`, `vert`:

hlsl

```hlsl
StructuredBuffer<float3> _Velocities;
TEXTURE2D(_LutTex); SAMPLER(sampler_LutTex);
int _ColorMode; float _ColorMaxSpeed; float3 _ColorAxis;

// внутри vert(), после вычисления centerWS:
float3 vel = _Velocities[instanceID];
float t = 0;
if (_ColorMode == 1) t = saturate(length(vel) / max(_ColorMaxSpeed, 1e-4));
else if (_ColorMode == 2) t = saturate(dot(normalize(vel + 1e-6), _ColorAxis) * 0.5 + 0.5);
o.gradientT = t; // передать через Varyings, сэмплировать LUT в frag (SAMPLE_TEXTURE2D_LOD, mip 0)
```

`frag` — при `_ColorMode==0` использовать константный `_Color`, иначе сэмплировать `_LutTex` по `gradientT`.

#### DoD Фазы 2

Контракт-тест дефолтов (`Constant` по умолчанию — не ломает существующие ассеты, у которых нет этого поля). Ручная проверка: `SpeedGradient` — быстрые частицы заметно ярче/другого цвета по LUT, чем медленные; `DirectionGradient` с `axis=(0,1,0)` — частицы, летящие вверх, окрашены иначе, чем летящие вниз (два конца градиента, не один и тот же цвет для обоих направлений).

---

## Фаза 3 — Spatial Hash Grid (новый примитив, самый рискованный шаг)

### Контекст

`architecture.md` с самого начала проекта относил spatial hash к будущему, с явным намеченным алгоритмом: `Hash → Histogram/InterlockedAdd → Scan → Scatter` (counting sort). Это ни разу не реализовывалось. Это **новый тип ресурса**, не `Field` и не просто атрибут `ParticleSet` — не пытаться встроить в существующие абстракции, оформить отдельным, самостоятельным классом.

**Скоуп v1 — плоскость XZ, не полный 3D.** Весь проект (поля, boids через `HeadingSteerPass`, `Physarum`) уже работает в плоскости XZ (`heading.y` явно обнуляется в `HeadingSteerPass`) — 3D-хеш-грид был бы существенно дороже (третье измерение индекса ячейки, 27 соседей вместо 9) без текущей необходимости.

### Решение

#### 3.1 — `SpatialHashGrid` (новый класс, `Assets/Scripts/Core/SpatialHashGrid.cs`)

Владеет тремя `GraphicsBuffer`: `cellCount` (`uint[cellCountTotal]`), `cellStart` (`uint[cellCountTotal]`, после scan), `sortedParticleIndices` (`uint[particleCapacity]`). Параметры: `cellSize` (мировые единицы), `gridOrigin`/`gridSize` (тот же смысл, что `Origin`/`Size` у `FieldDescriptor`, но не путать с самим `FieldDescriptor` — это отдельный ресурс).

#### 3.2 — четыре новых кернела, новый файл `Assets/Shaders/GPU/Passes/SpatialHashPasses.compute`

hlsl

```hlsl
#pragma kernel ClearCellCounts
#pragma kernel CountParticlesPerCell
#pragma kernel ScanCellCounts
#pragma kernel ScatterParticlesToSortedOrder
```

**`ClearCellCounts`** — `cellCount[id.x] = 0`, dispatch по `cellCountTotal`.

**`CountParticlesPerCell`** — dispatch по `ParticleCount`, каждая частица: `cellIndex = WorldToCellIndex(position[id.x])`, `InterlockedAdd(cellCount[cellIndex], 1)`. Простой integer-счётчик, без fixed-point-кодирования (в отличие от P2G `ADR-002` — там `InterlockedAdd` копил взвешенную **сумму** float-значений через fixed-point трюк специально потому что float-атомиков нет; здесь копится обычный **счётчик** частиц, `uint`-атомик по прямому назначению, без кодирования).

**`ScanCellCounts`** — exclusive prefix sum по `cellCount` → `cellStart`. Для v1 — простой Hillis-Steele parallel scan (`log2(cellCountTotal)` проходов одного и того же кернела, каждый проход читает предыдущий результат и пишет следующий через ping-pong между двумя `GraphicsBuffer`, тот же принцип swap, что уже используется для полей, только на буфере, не текстуре). **Не пытаться сделать более сложный multi-level scan (Blelloch) в v1** — при разумном числе ячеек (`128×128=16384` или `256×256=65536`) простой Hillis-Steele достаточно быстр, не требует доп. сложности.

**`ScatterParticlesToSortedOrder`** — dispatch по `ParticleCount`, каждая частица атомарно резервирует себе слот через `InterlockedAdd` на **копию** `cellStart` (используемую как "текущий курсор записи" для своей ячейки, не сам `cellStart`, который должен остаться неизменным для последующего чтения соседей), пишет свой индекс в `sortedParticleIndices[слот]`.

#### 3.3 — численная верификация, обязательна перед Фазой 4

Тот же харнес-паттерн (`FieldTestHarness`-подобный, GPU readback), что использовался весь Stable Fluids трек. DoD: на синтетическом наборе частиц с известными позициями — `cellCount`/`cellStart` после scan корректно предсказывают диапазон индексов каждой ячейки; `sortedParticleIndices` содержит ровно те частицы, что физически лежат в соответствующей ячейке (проверка через сравнение с CPU-эталоном counting sort на том же наборе данных). **Не переходить к Фазе 4, пока этот тест не зелёный** — весь Reynolds-steering физически бессмысленен на неверном хеш-гриде, и отладка "неверные соседи" внутри уже сложного steering-кернела на порядок труднее, чем изолированная проверка самого хеша.

#### DoD Фазы 3

Харнес-тест (3.3) зелёный. Отдельный маленький демо-кернел `DebugCountNeighbors` (dispatch по частицам, пишет в scalar-атрибут "число найденных соседей в радиусе" через перебор 3×3 ячеек) — визуальная/численная sanity-проверка на реальном `SwarmSource`-пресете перед тем, как строить полноценный steering поверх.

---

## Фаза 4 — Classical Reynolds Steering через grid

### 4a — один "клан" (single-team), доказать сам neighbor search

Не добавлять team-веса сразу — изолировать риск (та же дисциплина, что "отключи cohesion, потом curl noise" при разборе overshoot). Новый пасс `ReynoldsSteerPass : ParticleKernelPass` (`Assets/Scripts/Passes/DynamicsPasses.cs`, рядом с `HeadingSteerPass`), читает `position`/`velocity`/`SpatialHashGrid`-буферы, для каждой частицы перебирает 3×3 соседних ячейки, накапливает классические силы Рейнольдса (align = среднее `velocity` соседей; cohesion = направление к среднему `position` соседей; separation = сумма `1/dist` от слишком близких), пишет **напрямую в `velocity`** с `*dt` — Newtonian force-accumulation, тот же контракт, что `SampleGradientFieldPass` (`Category=Force`).

### 4b — добавить `teamId`-взвешивание (Rivalry Matrix)

Только после того, как 4a визуально подтверждена (одна команда честно роится через grid, не через поля). Новый `GraphicsBuffer` — плоская `maxTeams×maxTeams` матрица `float3(cohesion, alignment, separation)`, конфигурируется на `EffectAsset` через `TeamRelation[]` (пары `teamA`/`teamB`+три веса). `ReynoldsSteerPass` читает `teamId[neighborIndex]` при переборе соседей, домножает вклад каждого соседа на вес из матрицы для пары `(myTeam, neighborTeam)`.

#### DoD Фазы 4

4a: контракт-тест `ReynoldsSteerPass` (Category=Force, атрибуты). Численный тест на харнесе — два кластера частиц на известном расстоянии, после N шагов cohesion-сила измеримо притягивает их (тот же паттерн, что численные smoke-тесты Gradient/Density). Ручная проверка на `SwarmSource`-пресете — рой формируется, не расползается плоским слоем (тот же критерий, что был у Tier-1 boids). 4b: контрольный тест — команда с `cohesion=-2` к другой команде физически избегает её кластера за N шагов.

---

## Фаза 5 — Kinematic Heading вариант (Rivalry-style: постоянная скорость, ограниченный поворот)

### Контекст

`HeadingSteerPass`/`desiredForce`-аккумулятор уже существуют (ADR-011/012) — `nlerp` направления к желаемому, snap модуля скорости к `CruiseSpeed`. Нужен вариант `ReynoldsSteerPass`, пишущий **не** в `velocity` напрямую, а в `desiredForce` (unit-direction × вес, без `dt` — та же конвенция, что `AddNormalizedGradientFieldPass`), для дальнейшей обработки уже существующим `HeadingSteerPass`.

### Решение

**Не дублировать всю математику neighbor-search.** Вынести накопление сырых Reynolds-сил (align/cohesion/separation векторы, до записи в конечный буфер) в общую HLSL-функцию `ComputeReynoldsForces(...)` в новом инклюде `Assets/Shaders/GPU/Includes/ReynoldsForces.hlsl`, вызываемую из **двух** тонких кернелов: `ReynoldsSteerNewtonian` (Фаза 4, пишет `velocity += force*dt`) и `ReynoldsSteerKinematic` (новый, Фаза 5, пишет `desiredForce += normalize(force)*weight`, без `dt`). Общая функция принимает `SpatialHashGrid`-буферы и Rivalry Matrix одинаково для обоих режимов.

#### DoD Фазы 5

Контракт-тест нового пасса (Category, атрибуты `desiredForce`). Ручная проверка: тот же `SwarmSource`-пресет, но с `ReynoldsSteerKinematic → HeadingSteerPass` вместо `ReynoldsSteerNewtonian` — та же рой-структура, но движение с постоянной скоростью, плавными поворотами (визуально ближе к "живому организму", не к физическому облаку частиц).



### Правка к Фазе 3 — конкретные числа и явная (не молчаливая) связь с `BoxBounds`

**`cellSize = 2`** (даёт `25×25` ячеек на мир `50×50`, как у `Boids_mk1`) — берём как стартовую точку, не финальную калибровку.

**Важное уточнение, которое стоит явно зафиксировать в ADR, а не оставить implicit**: `SpatialHashGrid.gridOrigin`/`gridSize` в v1 настраиваются **вручную**, отдельно от `BoxBoundsPass.center`/`.extents` — то есть временно два независимых места, где задан "размер мира", которые должны совпадать по смыслу, но не связаны кодом. Это осознанный, не забытый компромисс (тот же класс решения, что `SquareTexelValidator`, введённый явно, не молча): **follow-up после Фазы 3** — либо `SpatialHashGrid` читает `gridSize` из `BoxBoundsPass.extents` конкретного пасса в пайплайне (требует ссылки на пасс по имени/индексу — новый паттерн для проекта), либо оба явно берут значение из одного общего поля на `EffectAsset`. Не решать сейчас, но занести строкой в `Techdebt.md` сразу после Фазы 3, чтобы не забыть — рассинхрон этих двух чисел дал бы частицам, блуждающим за пределами хеш-сетки (в `Wrap`-зоне `BoxBounds`, но вне `gridSize` хеша) без соседей вообще, тихий баг того же класса, что мы ловили весь проект.

### Правка к порядку Фаз 4→5 — явный чекпоинт, не автоматический переход

Между Фазой 4 (Newtonian Reynolds, полностью) и Фазой 5 (kinematic-вариант) — **обязательная пауза с ручной оценкой**, не автоматический переход по завершении DoD. Формулирую явно как отдельный шаг:

#### Фаза 4.5 — Чекпоинт (не код, оценка)

После полного закрытия Фазы 4 (4a single-team + 4b team-weighted) — пожить с чистым Newtonian-Reynolds достаточное время (несколько сессий ручного тестирования, калибровки `cellSize`/весов), прежде чем открывать Фазу 5. Критерий перехода к Фазе 5 — не таймер, а осознанное решение: "текущее Newtonian-поведение изучено, хочется сравнить с kinematic-альтернативой" — та же дисциплина, что уже применялась между F1 и F2 Stable Fluids (пауза для трезвой оценки, не по инерции продолжать).

### Правка к порядку выполнения — строго последовательно, без параллелизации

Убираю из плана любую формулировку вида "можно делать параллельно" — фазы **строго последовательны**: 1 → 2 → 3 → 4a → 4b → 4.5 (пауза) → 5. Каждая начинается только после закрытия DoD предыдущей.

### Новый под-шаг — Фаза 3.4: визуальный дебаг хеш-грида

#### Контекст

Численный харнес-тест (3.3) доказывает корректность на синтетических данных, но не даёт глазами увидеть, как хеш ведёт себя на реальном, живом рое — тот же разрыв, что был между "EditMode зелёный" и "Play-сеть оператора" на протяжении всего Physarum/Stable Fluids трека. Раз Фаза 3 — самый новый и рискованный примитив всего плана, визуальная проверка обоснованно важнее здесь, чем для более мелких шагов.

#### Решение — переиспользовать инфраструктуру Фазы 2, не строить новую

Фаза 2 уже выносит `LutTextureUtility` (bake `Gradient → Texture2D`) в общий, не field-specific класс. Дебаг-визуализация грида — прямой повторный потребитель этой же утилиты, только источник данных — `cellCount`-буфер (`StructuredBuffer<uint>`), не `RenderTexture` поля.

csharp

```csharp
public sealed class SpatialHashDebugBinder : IRenderBinder, IDisposable
{
    // Тот же паттерн, что FieldDebugQuadsBinder: один quad, один Material,
    // LutTextureUtility.Bake(gradient) один раз в Initialize.
    // Material.SetBuffer("_CellCounts", grid.CellCountBuffer);
    // Material.SetInt("_GridResolution", grid.Resolution); // для UV→cellIndex внутри шейдера
}
```

Новый маленький шейдер `Assets/Shaders/GPU/GridDebug.shader` (рядом с `FieldDebug.shader`, тот же URP-стиль): читает `_CellCounts[cellIndex]` по UV-координате quad'а, нормализует по разумному максимуму (`Min(count / maxExpectedPerCell, 1)`), красит через тот же `_LutTex`. Не FieldKernelPass-инфраструктура (это render-биндер, не compute pass — аналог `FieldDebugQuadsBinder`, не `DiffuseFieldPass`).

#### DoD 3.4

Ручная проверка: на `SwarmSource`-пресете с активным `SpatialHashGrid` — quad показывает плотность частиц по ячейкам, визуально согласующуюся с реальным расположением роя на экране (там, где рой плотнее, quad ярче). Не формальный численный тест — чисто диагностический инструмент для глаз, тот же уровень строгости, что `Debug Field Quads` для полей.

---

### Итоговая последовательность (обновлено)

```
Фаза 1 — Unified Swarm Source
Фаза 2 — Градиент цвета (+ вынос LutTextureUtility)
Фаза 3 — Spatial Hash Grid
  3.1-3.3 — сам хеш + численная верификация (харнес, обязателен перед 3.4/Фазой 4)
  3.4 — визуальный дебаг грида (переиспользует LutTextureUtility из Фазы 2)
Фаза 4 — Classical Reynolds (Newtonian)
  4a — single-team
  4b — team-weighted (Rivalry Matrix)
Фаза 4.5 — Чекпоинт (пауза, ручная оценка, не код)
Фаза 5 — Kinematic Heading вариант (после явного решения продолжать)
```
