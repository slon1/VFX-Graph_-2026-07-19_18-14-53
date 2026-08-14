# ТЗ: ADR-012 Kinematic Heading Boids (`Boids_mk1`)

Прочитать [`ADR-012-Kinematic-Heading-Boids.md`](ADR-012-Kinematic-Heading-Boids.md) перед кодом.

Референс формул: `Boids_Rivalry` `Assets/Shaders/Compute/BoidsSimulation.compute` — `ClearForces`, modern-ветка `BoidsForceAndPolarization` (normalize avgVel / centroid), `IntegrateMotion` modern (`useLegacy*=false`).

**Не трогать:** `SampleVelocityFieldPass`, `SampleGradientFieldPass`, `SteerToVelocityFieldPass`, `CurlNoisePass` (классы/кернелы). `Gray-Scott-*`, Hybrid, Echo, `Boids_mk1 1/2`. YAML `SerializeReference` руками не править — Inspector / Unity `execute_code`.

---

## 1. Атрибут `heading`

### `Assets/Scripts/Core/BuiltinAttributes.cs`

```csharp
public static readonly AttributeId Heading = new AttributeId("heading", AttributeType.Float3, true);
```

### `Assets/Scripts/Runtime/SimPass.cs` — `AttrSets`

```csharp
public static readonly AttributeId[] Heading = { BuiltinAttributes.Heading };
public static readonly AttributeId[] HeadingVelocity = { BuiltinAttributes.Heading, BuiltinAttributes.Velocity };
```

World уже регистрирует Writes/Reads через `AutoRegisterAttributes` + `RegisterZeroed`. Отдельный init-пасс не нужен. VFX по-прежнему читает только `position`.

`ParticleKernelPass` биндит буфер по `AttributeId.Name` (`heading` → `RWStructuredBuffer<float3> heading`). Имя в HLSL должно совпасть буквально.

---

## 2. `ClearVelocityPass`

### `Assets/Shaders/GPU/Passes/DynamicsPasses.compute`

`#pragma kernel ClearVelocity`

```hlsl
[numthreads(THREADS, 1, 1)]
void ClearVelocity(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount) return;
    velocity[id.x] = 0;
}
```

`RWStructuredBuffer<float3> velocity` в файле уже есть.

### `Assets/Scripts/Passes/DynamicsPasses.cs`

`ClearVelocityPass : ParticleKernelPass`  
DisplayName `"Clear Velocity"`, Category `Dynamics`, KernelName `"ClearVelocity"`, Reads `None`, Writes `Velocity`. `SetParams` пустой.

---

## 3. `AddNormalizedVelocityFieldPass` (alignment)

### `Assets/Shaders/GPU/Passes/FieldPasses.compute`

`#pragma kernel AddNormalizedVelocityField`

Переиспользовать `position` / `velocity` / `ParticleCount` / `FieldRead` / `sampler_linear_clamp` — **не дублировать**. Уникальный uniform: `float AddWeight;` (не `SampleStrength` / `SteerStrength`).

```hlsl
[numthreads(PARTICLE_THREADS, 1, 1)]
void AddNormalizedVelocityField(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount) return;
    float2 uv = WorldToFieldUV(position[id.x]);
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) return;

    float3 fieldVel = FieldUVToWorldVelocity(FieldRead.SampleLevel(sampler_linear_clamp, uv, 0));
    float len = length(fieldVel);
    if (len < 1e-6) return;
    velocity[id.x] += (fieldVel / len) * AddWeight;
}
```

Без `DeltaTime`. Early-out вне поля — не `target=0`.

### C# рядом с `SteerToVelocityFieldPass` в `FieldPasses.cs`

- Force, KernelName `"AddNormalizedVelocityField"`
- DisplayName `"Add Normalized Velocity Field"`
- default `velocityFieldName = "flockVel"`, `weight = 0.8f`
- public `VelocityFieldName` / `Weight` (как у `SteerToVelocityFieldPass.Strength`)
- Reads Position, Writes Velocity
- FieldReads: **один** `FieldRequest` — `flockVel`, `FieldAccess.Read`, `FieldSemantic.Velocity`, **2 канала** (тот же контракт, что `SteerToVelocityFieldPass`)
- SetParams: texture + `FieldShaderParams.Push` + `AddWeight` (без `DeltaTime`)
- Property ID `"AddWeight"` (файл `FieldPasses.compute`; не пересекается с `SampleStrength` / `SteerStrength`)

---

## 4. `AddNormalizedGradientFieldPass` (cohesion / separation)

### `Assets/Shaders/GPU/Passes/GradientPasses.compute`

Отдельный файл — там уже `Texture2D<float> FieldRead` (`SampleGradient`). `#pragma kernel AddNormalizedGradient`. Не класть в `FieldPasses.compute` (там `Texture2D<float2> FieldRead`).

Скопировать stencil `SampleGradient` целиком (central-diff + `FieldUvGradientToWorld`, `StructuredBuffer<float3> position`). Не дублировать объявления `FieldRead` / `position` / `velocity` / `ParticleCount`. Вместо `+= direction * SampleStrength * DeltaTime`:

```hlsl
float len = length(direction);
if (len < 1e-6) return;
velocity[id.x] += (direction / len) * AddWeight;
```

Без `DeltaTime`. Uniform `AddWeight` (signed). UV вне `[0,1]` — return.

### C# рядом с `SampleGradientFieldPass`

- Force, KernelName `"AddNormalizedGradient"`
- DisplayName `"Add Normalized Gradient Field"`
- default `fieldName = "density"`, `weight = 0.6f`
- public `FieldName` / `Weight` (signed)
- Reads Position, Writes Velocity
- FieldReads: один `FieldRequest` — Scalar, 1 канал, `FieldAccess.Read` (как `SampleGradientFieldPass`)
- SetParams: texture + `FieldShaderParams.Push` + `AddWeight` (без `DeltaTime`)

На пресете два инстанса: cohesion `weight=+0.6` / `cohesionDensity`, separation `weight=−1.2` / `separationDensity` (стартовые Rivalry `BoidGroupConfig`).

---

## 5. `HeadingSteerPass`

### `Assets/Shaders/GPU/Passes/DynamicsPasses.compute`

`#pragma kernel HeadingSteer`

Объявить `RWStructuredBuffer<float3> heading;` на уровне файла (имя = `BuiltinAttributes.Heading`).

```hlsl
float TurnSpeed;
float CruiseSpeed;

[numthreads(THREADS, 1, 1)]
void HeadingSteer(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount) return;

    float3 force = velocity[id.x];
    float3 h = heading[id.x];
    float forceLen = length(force);
    float hLen = length(h);

    float3 desired = (forceLen > 1e-6) ? (force / forceLen) : ((hLen > 1e-6) ? (h / hLen) : float3(1, 0, 0));
    if (hLen < 1e-6)
        h = desired;
    else
        h = h / hLen;

    float k = saturate(TurnSpeed * DeltaTime);
    h = normalize(lerp(h, desired, k));
    h.y = 0.0;
    float flat = length(h);
    if (flat < 1e-6)
        h = float3(1, 0, 0);
    else
        h = h / flat;

    heading[id.x] = h;
    velocity[id.x] = h * CruiseSpeed;
}
```

`DeltaTime` в этом файле уже есть.

### C# в `DynamicsPasses.cs`

- Dynamics, KernelName `"HeadingSteer"` (имя уникально в shader library — `FindKernel` ищет по всем `.compute`)
- DisplayName `"Heading Steer"`
- Reads **и** Writes `AttrSets.HeadingVelocity` (иначе `heading` не зарегистрируется / не забиндится)
- public `TurnSpeed` / `CruiseSpeed`
- default `turnSpeed = 0.15f`, `cruiseSpeed = 4f`
- SetParams: `TurnSpeed`, `CruiseSpeed`, `SimShaderIds.DeltaTime`

---

## 6. Пресет `Assets/Effects/Boids_mk1.asset`

Менять через Unity (SerializedObject / Inspector), не YAML rid.

**Speed оставить 20.** `CruiseSpeed=4`, `TurnSpeed=0.15` (чтобы `Speed * TurnSpeed ≈ 3` как Rivalry `maxTurnSpeed`).

Сейчас в ассете порядок Ньютона: Curl → Drag → Limit → **Integrate → BoxBounds** → P2G → SampleGradient → DiffuseVelocity ×6 → Steer. Нужно **переставить**, не только заменить типы: P2G должен видеть cruise `velocity` прошлого кадра, Integrate — только после `HeadingSteer`.

**Удалить из списка:** `DragPass`, `SpeedLimitPass`, `SteerToVelocityFieldPass`, оба `SampleGradientFieldPass`, **`CurlNoisePass`**. Не оставлять `enabled=0` в окне `ClearVelocity → HeadingSteer`: текущий curl — dt-scaled Force (`v += curl*amp*dt`), галка калибровки испортит unit-веса. Шум, если понадобится — новый unit-direction / no-dt пасс отдельным тикетом, не заранее. В это окно не класть и любой другой существующий dt-scaled Force.

**Порядок пассов (целевой):**

1. P2G `flockVel`: ClearAccum → ScatterVelocity → Normalize → Decay → **DiffuseVelocity ×6** (сейчас DiffuseVelocity стоит в конце списка, перед Steer — **перенести сюда**, к splat скорости).
2. P2G `cohesionDensity`: ClearAccum → Scatter → Normalize → DecayScalar → Diffuse ×6.
3. P2G `separationDensity`: ClearAccum → Scatter → Normalize → DecayScalar.
4. `ClearVelocityPass`
5. `AddNormalizedVelocityFieldPass` flockVel, weight 0.8
6. `AddNormalizedGradientFieldPass` cohesionDensity, weight +0.6
7. `AddNormalizedGradientFieldPass` separationDensity, weight −1.2
8. `HeadingSteerPass` turn 0.15, cruise 4
9. `IntegratePass`
10. `BoxBoundsPass` **Wrap** (Rivalry wrap; Bounce на kinematic heading ломает курс у стены). Wrap в `BoxBounds` двигает только `position`, `heading` не трогает — так и надо.

Поля: `flockVel` 64×64, cohesion 32, separation как сейчас — не трогать resolution в этом тикете.

---

## 7. Тесты (`Assets/Tests/Editor/`)

Контракт по шаблону `SteerToVelocityFieldPassTests` / `DiffuseFieldPassTests` (reflection, без GPU readback).

| Файл | Проверки |
| --- | --- |
| `ClearVelocityPassTests` | Category Dynamics, KernelName ClearVelocity, Writes Velocity, Reads empty |
| `AddNormalizedVelocityFieldPassTests` | Force, KernelName, default field `flockVel`, weight 0.8, один FieldRead Velocity / 2 канала / Read |
| `AddNormalizedGradientFieldPassTests` | Force, KernelName `AddNormalizedGradient`, default field `density`, weight 0.6, один FieldRead Scalar / 1 канал / Read |
| `HeadingSteerPassTests` | Dynamics, KernelName HeadingSteer, defaults turn 0.15 / cruise 4, Reads+Writes Heading+Velocity |

---

## 8. Документация (в скоуп)

- `DOC/pass-catalog.md` — четыре пасса; явно: accumulator без dt; HeadingSteer пишет cruise `velocity`.
- `DOC/getting-started.md` / `DOC/capabilities.md` / `DOC/status.md` — `Boids_mk1` = kinematic heading + поля, не Ньютон.
- Сноска в ADR-004 / ADR-011: на kinematic-пресете cohesion/align — AddNormalized*, не SampleGradient/Steer.
- `M3DDemoTools` — **не** добавлять Create Boids_mk1 в этом тикете (генератора нет; не плодить второй источник правды).

---

## 9. Ручная проверка

0. `Test1`: `SimulationWorld.Effect` = `Boids_mk1`, Rebuild.
1. Play: частицы летят **примерно с постоянной скоростью** (нет разгона в клубок и остановки в центре).
2. Локальные группы с общим курсом, не 4-fold «снежинка» curl (`CurlNoisePass` в пресете нет).
3. Выключить cohesion (enabled=0): стая не обязана держаться кучей, но не пульсирует ямой ∇.
4. Выключить alignment: меньше общего курса, cohesion всё ещё тянет к сгусткам **доворотом**, не осцилляцией.
5. HybridTouchField не регрессирует (пассы не менялись) — прогон не обязателен, классы не трогали.
6. Если стая есть, но «продавливаются» друг через друга — **не** чинить hash в этом тикете; зафиксировать наблюдение.

Калибровка в Play (числа, не код): TurnSpeed, три Weight, CruiseSpeed. Strength старых G2P не переносить.

---

## Definition of Done

- Четыре пасса + `heading` в коде, EditMode-тесты зелёные.
- `Boids_mk1` переставлен на порядок из §6, Speed=20, Wrap, curl off.
- Живые доки обновлены (§8). Код существующих Hybrid/Gradient/Steer кернелов без диффа.
- В Play: постоянная скорость + локальный общий курс. Не «успех», если снова пульс-клубок или curl-снежинка.

---

## Вне скоупа

Spatial hash · Gray-Scott-* · глобальный saturate в SampleGradient · третий буфер steeringForce · dt clamp · правка CurlNoisePass · unit-direction curl-клон.
