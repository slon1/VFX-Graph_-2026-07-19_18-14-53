# ТЗ: Boids Alignment Steering Pass + Velocity Field Blur (rev. 3)

Прочитать `ADR-011-Boids-Alignment-DeltaTime-And-Blur.md` (rev. 3, раздел "Изменения после ревью") перед началом.

**rev. 1 → rev. 2:** `SampleVelocityFieldPass` **не трогаем** (живой контракт `HybridTouchField(.1)`/`AgentFieldEcho 1`). Alignment получает новый, отдельный пасс со steering-формулой.

**rev. 2 → rev. 3 (важно, меняет код и числа ниже):**
- В `FieldPasses.compute` **нет** `float DeltaTime` — исправлена ошибка в тексте ниже, надо объявить на уровне файла.
- `SteerDeltaTime` как отдельный shader-параметр убран — используем общий `SimShaderIds.DeltaTime`.
- В кернел `SteerToVelocityField` добавлен `saturate(strength*dt)` — не факультативно, часть фикса.
- Early-out по UV — явно "пропустить", не "target=0".
- `Boids_mk1`: `Speed≈20` (не 50), `flockVel` 64×64 (не 128×128), `6×` Diffuse (не 2-3), `rate≈0.15` (не 0.1).

---

## 1. Новый пасс `SteerToVelocityFieldPass` + кернел `SteerToVelocityField`

Alignment "по-честному" (Reynolds steering, не накопление): `v += (fieldVel - v) * strength * dt`.

### `Assets/Shaders/GPU/Passes/FieldPasses.compute`

Добавить в список кернелов:

```hlsl
#pragma kernel SteerToVelocityField
```

Кернел — этот пасс `ParticleKernelPass`, не `FieldKernelPass`: читает поле как `Read` (bilinear sample через `FieldRead` слот, как `SampleVelocityField`), пишет **particle velocity**, не текстуру поля. Смотри структуру секции `SampleVelocityField` (строки ~79-101) — новый кернел зеркалит её структуру (объявления `position`/`velocity`/`ParticleCount` уже есть в файле, не дублировать), отличается формулой и явным `saturate`:

```hlsl
// --- SteerToVelocityField (Reynolds-style alignment: steer toward field velocity, not accumulate) ---
// Separate from SampleVelocityField (Transport, used by Hybrid/Echo) — do not merge, different contract.
float SteerStrength;

[numthreads(PARTICLE_THREADS, 1, 1)]
void SteerToVelocityField(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount)
    {
        return;
    }

    float2 uv = WorldToFieldUV(position[id.x]);
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
    {
        return; // outside field plane — no alignment force, NOT target=0 (that would brake to a stop)
    }

    float2 fieldVel = FieldRead.SampleLevel(sampler_linear_clamp, uv, 0);
    float3 target = FieldUVToWorldVelocity(fieldVel);
    float k = saturate(SteerStrength * DeltaTime); // clamp — explicit Euler overshoots target past k>1
    velocity[id.x] += (target - velocity[id.x]) * k;
}
```

`float DeltaTime;` — в `FieldPasses.compute` его **нет** сейчас (проверено: единственный файл из `Assets/Shaders/GPU/Passes/*.compute`, где `DeltaTime` не объявлен). Добавить `float DeltaTime;` на уровне файла (рядом с новым `float DiffusionRate;` из п.2 ниже) — общий для `SteerToVelocityField` и `DiffuseVelocityField`. Отдельный `SteerDeltaTime` **не нужен**: `SetParams` вызывается прямо перед dispatch того же кернела в том же `CommandBuffer`, значение не может быть перезатёрто другим пассом между вызовами (тот же паттерн, что `SpringToRest`/`SampleGradient`/`DiffuseField` — все шлют общий `SimShaderIds.DeltaTime`). Нужен только уникальный `SteerStrength` (чтобы не пересечься с `SampleStrength` этого же файла).

### `Assets/Scripts/Passes/FieldPasses.cs`

Разместить рядом с `SampleVelocityFieldPass`:

```csharp
/// <summary>
/// Reynolds-style alignment: steer particle velocity toward the locally sampled field
/// velocity (v += (fieldVel - v) * strength * dt), not accumulate onto it. Self-limiting —
/// unlike SampleVelocityFieldPass (Transport, no dt, used by Hybrid/Echo demos), this pass
/// is Force/dt-scaled by design and must not be merged with SampleVelocityFieldPass:
/// different contract, different consumers.
/// </summary>
[Serializable]
public sealed class SteerToVelocityFieldPass : ParticleKernelPass
{
    private static readonly int SteerStrengthId = Shader.PropertyToID("SteerStrength");

    [SerializeField] private string velocityFieldName = "flockVel";
    [SerializeField] private float strength = 1f;

    private FieldDescriptor fieldDescriptor;
    private int velocityReadId;

    [NonSerialized] private FieldRequest[] fieldReadsCache;

    public string VelocityFieldName
    {
        get => velocityFieldName;
        set => velocityFieldName = value;
    }

    public float Strength
    {
        get => strength;
        set => strength = value;
    }

    public override string DisplayName => "Steer To Velocity Field";
    public override PassCategory Category => PassCategory.Force;
    protected override string KernelName => "SteerToVelocityField";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Position;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Velocity;

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, velocityFieldName,
            FieldAccess.Read, FieldSemantic.Velocity, 2);

    public override void Initialize(SimContext context)
    {
        base.Initialize(context);
        fieldDescriptor = context.Fields.Get(velocityFieldName).Descriptor;
        velocityReadId = SimShaderIds.FieldRead;
    }

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SimField field = context.Fields.Get(velocityFieldName);
        context.Cmd.SetComputeTextureParam(Kernel.Shader, Kernel.Index, velocityReadId, field.Current);
        FieldShaderParams.Push(context.Cmd, Kernel.Shader, fieldDescriptor);
        SetFloat(context, SteerStrengthId, strength);
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
    }
}
```

Не менять `SampleVelocityFieldPass` — ни код, ни XML-doc-комментарий, он остаётся точным описанием его текущего (правильного для Hybrid/Echo) поведения.

---

## 2. Новый пасс `DiffuseVelocityFieldPass` + кернел `DiffuseVelocityField`

Блюр `flockVel` (и любого другого 2-канального Velocity-поля). Без изменений относительно rev. 1, кроме дефолта `diffusionRate`.

### `Assets/Shaders/GPU/Passes/FieldPasses.compute`

```hlsl
#pragma kernel DiffuseVelocityField
```

Кернел (переиспользует глобальные `Texture2D<float2> FieldRead; RWTexture2D<float2> FieldWrite;`, уже объявленные в этом файле — **не** создавать новый файл, `DiffusePasses.compute` занят `Texture2D<float>` с тем же именем `FieldRead`, конфликт типов):

```hlsl
// --- DiffuseVelocityField (WritePingPong, 5-point Laplacian per-component) ---
float DeltaTime;    // не объявлена в этом файле сейчас — добавить (shared с SteerToVelocityField, п.1)
float DiffusionRate; // не объявлена в этом файле сейчас — добавить

float2 LoadVelocityClamped(int2 q)
{
    int2 maxP = FieldResolution - 1;
    q = clamp(q, int2(0, 0), maxP);
    return FieldRead.Load(int3(q, 0));
}

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void DiffuseVelocityField(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y)
    {
        return;
    }

    int2 p = int2(id.xy);
    float2 c = LoadVelocityClamped(p);
    float2 n = LoadVelocityClamped(p + int2(0, 1));
    float2 s = LoadVelocityClamped(p + int2(0, -1));
    float2 e = LoadVelocityClamped(p + int2(1, 0));
    float2 w = LoadVelocityClamped(p + int2(-1, 0));

    float2 laplacian = n + s + e + w - 4.0 * c;
    FieldWrite[p] = c + DiffusionRate * DeltaTime * laplacian;
}
```

**Проверено: ни `DeltaTime`, ни `DiffusionRate` сейчас в `FieldPasses.compute` не объявлены** (в отличие от `DecayFactor`, который там есть, но это отдельный, CPU-предвычисленный параметр `DecayFieldPass` — не путать). Обе переменные — новые для этого файла, добавить один раз на уровне файла, использовать в обоих новых кернелах (`SteerToVelocityField`, `DiffuseVelocityField`).

### `Assets/Scripts/Passes/FieldPasses.cs`

```csharp
/// <summary>
/// Explicit 5-point Laplacian diffusion on a 2-channel velocity field (WritePingPong).
/// Same CFL rule as DiffuseFieldPass (rate * dt ≲ 0.2-0.25), applied per-component —
/// Laplacian is separable, no cross-channel coupling. Default matches cohesionDensity's
/// diffusionRate (0.15) — a lower rate does not accumulate a real averaging radius over
/// a realistic pass count at moderate SimulationSpeed (ADR-011 Дефект 3 diffusion-length math).
/// </summary>
[Serializable]
public sealed class DiffuseVelocityFieldPass : FieldKernelPass
{
    [SerializeField] private string fieldName = "flockVel";
    [SerializeField, Min(0f)] private float diffusionRate = 0.15f;

    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string FieldName
    {
        get => fieldName;
        set => fieldName = value;
    }

    public float DiffusionRate
    {
        get => diffusionRate;
        set => diffusionRate = value;
    }

    public override string DisplayName => "Diffuse Velocity Field";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "DiffuseVelocityField";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, fieldName,
            FieldAccess.WritePingPong, FieldSemantic.Velocity, 2);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
        SetFloat(context, SimShaderIds.DiffusionRate, diffusionRate);
    }
}
```

`FieldPasses.compute` уже в Pass Library у обоих ассетов — правок Pass Library на `SimulationWorld` не требуется.

---

## 3. Правка пресетов — **порядок калибровки: Speed → dt-константы → strength**

### `Assets/Effects/Boids_mk1.asset` (сейчас `simulationSpeed: 0.2`)

**Важно:** менять через Inspector/Play Mode (`SerializeField` пушится каждый кадр, кроме структуры списка пассов), не руками в YAML — список пассов использует `SerializeReference` с `rid`, легко сломать вручную.

1. **Сначала `simulationSpeed` → `≈20`** (**не 50** — это калибровка CFL реакции Gray-Scott, не универсальный ориентир dt; при `Speed=20`, 60 FPS: `dt≈0.33`, steering `k=strength*dt≈0.33` при `strength=1` — безопасно даже без `saturate`, с ним — тем более). Финальное число калибровать визуально (скорость общего движения — это отдельное ощущение от диффузии, полностью зависящей от `rate*dt`, не только от `Speed`).
2. **Пересчитать `CurlNoisePass.Amplitude` и `DragPass.Drag`** — при новом (в разы большем) `Speed` их вклад за кадр вырастет пропорционально, скорее всего понадобится уменьшить `Amplitude`/увеличить `Drag`, чтобы не улетело в хаос. Калибровать в Play, наблюдая за одиночным CurlNoise (выключить остальные силы).
3. **Понизить resolution `flockVel` до `64×64`** (было `128×128`, `size` оставить `50×50` — тексель вырастет с ≈0.39 до ≈0.78) — по рецепту `architecture.md`, и потому что при доступном на кадр числе Diffuse-проходов радиус усреднения на мелком текселе не набирается физически (ADR-011, Дефект 3, расчёт диффузионной длины).
4. **Добавить `DiffuseVelocityFieldPass`** (`fieldName: flockVel`) сразу после `DecayFieldPass(flockVel)`. Стартовать с **`6×` повторов** (как `cohesionDensity`, не 2-3), `diffusionRate: 0.15`. Проверить в Play, что `flockVel` debug quad визуально сглаживается за разумное время (секунды). Если и на `64×64`+`6×` радиус визуально мал — поднимать `diffusionRate` к потолку CFL (`rate*dt≲0.2-0.25`, на `Speed=20`/60fps это `rate≲0.6-0.75`) раньше, чем добавлять ещё проходы (дешевле по бюджету диспатчей).
5. **Заменить** `SampleVelocityFieldPass` на **`SteerToVelocityFieldPass`** (`velocityFieldName: flockVel`) в позиции текущего alignment-пасса (rid `7167045751568859265`) — старый пасс удалить из списка, не оставлять оба одновременно. `strength` — начать с `1.0`; `saturate(k)` в кернеле защищает от разгона, если `strength` окажется избыточным.
6. Пересчитать `strength` cohesion/separation (`SampleGradientFieldPass`, rid `...266`/`...267`) под новый `Speed` — не переносить старые `1`/`-0.9` механически, они калибровались под другой `dt`.
7. Все три G2P-силы (align/cohesion/separation) — включать по одной (остальные `enabled: 0`) для независимой калибровки, потом вместе.
8. **Rebuild** на `SimulationWorld` после любых изменений структуры списка пассов (новый/удалённый пасс) или полей (resolution), не только чисел.

### `Assets/Effects/Gray-Scott-Boids.asset` (`simulationSpeed: 50` — **не менять**, калибровка Gray-Scott-реакции уже завязана на это число)

1. Тот же приём с resolution: понизить `flockVel` до `64×64` (тот же аргумент диффузионной длины, хотя при `Speed=50` он менее критичен, чем на `Boids_mk1`).
2. `DiffuseVelocityFieldPass(flockVel)` после `DecayFieldPass` (rid `8100000000000000009`), **`6×`** повторов, `diffusionRate: 0.15`.
3. Заменить `SampleVelocityFieldPass` (rid `8100000000000000019`) на `SteerToVelocityFieldPass`, пересчитать `strength` вместе с cohesion/separation (rid `...020`/`...021`) той же процедурой (по одной силе, потом вместе).
4. Учесть CFL-риск (ADR-011, Дефект 3 / вопрос про CFL): при `Speed=50`/60 FPS `rate*dt` для `DiffuseVelocityField` при `rate=0.15` уже `≈0.125` — близко к грани на низком FPS/хитче мобильного. Не поднимать `diffusionRate` выше без явной проверки на целевом устройстве. Тот же класс бага, что Techdebt 1b (dt clamp) — не чиним здесь, только не усугубляем.

---

## 4. Тесты (`Assets/Tests/Editor/`)

Contract-тесты по шаблону `SampleGradientFieldPassTests.cs`/`DiffuseFieldPassTests.cs` (reflection, без числового GPU readback).

### `SteerToVelocityFieldPassTests.cs` (новый файл)

Проверить: `Category == PassCategory.Force`, `DisplayName == "Steer To Velocity Field"`, `KernelName == "SteerToVelocityField"`, default `VelocityFieldName == "flockVel"`, default `Strength == 1f`, `Reads == AttrSets.Position`, `Writes == AttrSets.Velocity`, `FieldReads` (1 элемент, `Access=Read`, `Semantic=Velocity`, `Channels=2`).

### `DiffuseVelocityFieldPassTests.cs` (новый файл)

Проверить: `Category`, `DisplayName == "Diffuse Velocity Field"`, `KernelName == "DiffuseVelocityField"`, default `FieldName == "flockVel"`, default `DiffusionRate == 0.15f` (не `0.1f` — это старое значение из rev.2, исправлено в rev.3, см. §2/§5 выше), `FieldWrites` (1 элемент, `Access=WritePingPong`, `Semantic=Velocity`, `Channels=2`).

(У `SampleVelocityFieldPass` сейчас нет отдельных тестов в `Assets/Tests/Editor/` — пункт "регрессия существующих тестов" не нужен, пасс не менялся.)

---

## 5. Документация и генератор (в скоуп)

- `DOC/pass-catalog.md` — добавить строки для `SteerToVelocityField` и `DiffuseVelocityField` (по образцу существующих `SampleVelocityField`/`DiffuseField`), явно указать разницу с `SampleVelocityField` (Transport, без dt) в столбце "Назначение".
- `DOC/status.md` — одна строка в актуальном разделе: alignment теперь через `SteerToVelocityFieldPass` (Force, steering, dt), не `SampleVelocityFieldPass` (Transport, тот остаётся только для Hybrid/Echo).
- `DOC/capabilities.md` — добавить оба новых пасса в список Fields/Pass library.
- `DOC/getting-started.md` — упомянуть новую пару в разделе "Boids → Gray-Scott" / "Как добавить пасс".
- `DOC/ADR/ADR-004-Gradient-Sample-Pass.md` — короткая сноска: с ADR-011 в фреймворке два явно разных G2P-инструмента для velocity-поля — `SampleVelocityFieldPass` (Transport, hybrid/echo, без dt) и `SteerToVelocityFieldPass` (Force, alignment, с dt) — не путать, разные контракты.
- `Assets/Scripts/Editor/M3DDemoTools.cs`, метод `CreateGrayScottBoidsEffect()` (относится **только** к `Gray-Scott-Boids`, `Speed=50` — `Boids_mk1` без генератора, править только `.asset`): обновить, чтобы повторный запуск меню не стирал ручной тюнинг —
  - `simulationSpeed: 20f` → `50f` (канон — уже закоммиченный `.asset`, генератор отстал от него ещё до этого тикета);
  - resolution `flockVel` в генераторе → `64×64` (сейчас `res128`, см. блок `else if (name == "flockVel")`);
  - заменить `new SampleVelocityFieldPass { Strength = 0.6f }` на `new SteerToVelocityFieldPass { Strength = <откалиброванное значение> }`;
  - добавить `6×new DiffuseVelocityFieldPass { FieldName = "flockVel", DiffusionRate = 0.15f }` в список после `new DecayFieldPass { FieldName = "flockVel", ... }`.
  - `CreateGrayScottAgentsEffect()` не трогать — там нет alignment/cohesion/separation вообще, вне скоупа.

**Вне скоупа:** `Assets/Effects/Boids_mk1 1.asset` — неиспользуемый дубликат, не подключён в `Test1.unity` (сейчас там `Gray-Scott-Agents`), не трогать.

---

## 6. Ручная проверка

0. **`Test1.unity`: `SimulationWorld.Effect` сейчас указывает на `Gray-Scott-Agents`** (проверено) — явно переключить на `Boids_mk1` перед тестированием пп.1-3, иначе Play ничего не покажет по теме тикета.
1. `Boids_mk1.asset`: после калибровки Speed/CurlNoise/Drag/resolution — `flockVel` debug quad заметно более гладкий/непрерывный, не "зернистый" per-texel шум, за разумное время (секунды).
2. Каждая из трёх сил по отдельности (`enabled` у двух других — `0`): alignment (`SteerToVelocityFieldPass`) даёт локальную согласованность направления соседей; cohesion — стягивание в кластеры; separation — расталкивание при скучивании.
3. Все три вместе — сравнить с референсом (проект с честными соседями).
4. Явно подтвердить: `HybridTouchField(.1).asset` и `AgentFieldEcho(.1).asset` визуально не изменились (Play, тач/движение полем) — `SampleVelocityFieldPass` не менялся, но лишний прогон не помешает.
5. Повторить п.1-3 на `Gray-Scott-Boids.asset` (Speed не менять, только новые пассы + strength).
6. Если после этого поведение всё ещё "текучий шум", а не узнаваемые кластеры/согласованное движение — сигнал к spatial hash (ADR-011, "Вне скоупа").
