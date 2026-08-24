## ADR-012: Kinematic Heading Boids (field-based, Rivalry modern)

**Статус:** Принято  
**Дата:** 2026-08-13  
**Ветка:** `adr11-boids-test`  
**ТЗ:** [`todo-adr-012-kinematic-heading.md`](todo-adr-012-kinematic-heading.md)  
**Контекст:** M3D Framework. Референс — [Boids_Rivalry](https://github.com/slon1/Boids_Rivalry.git) (`BoidsSimulation.compute`, `IntegrateMotion` modern). Сравнение с `Boids_mk1` после ADR-011.

### Контекст

После ADR-011 (`SteerToVelocityField` + `DiffuseVelocityField`, Speed≈20, `flockVel` 64×64) картинка всё ещё не стая: CurlNoise даёт когерентное «полотно»; без curl cohesion (`SampleGradient`, `v += ∇·dt`) схлопывает в пульсирующий клубок; без cohesion частицы растекаются слоем.

Сверка с Rivalry показала: **поля vs spatial hash — не главная причина.** Старый проект (дефолт `useLegacyIntegration=false`, `useLegacyForces=false`) — kinematic heading:

```
ClearForces → grid neighbors → force += dir*weight →
desiredDir = normalize(force)
heading = lerp(heading, desiredDir, saturate(dt * maxTurnSpeed))
pos += heading * maxSpeed * dt
```

Скорость всегда `maxSpeed`. Сила каждый кадр с нуля. Шум — слагаемое **force**, не импульс в persistent `velocity`.

Текущий `Boids_mk1` — Ньютон: persistent `velocity`, `v += F·dt`, `pos += v·dt`, Drag/SpeedLimit, G2P **после** Integrate. Это другой солвер. Калибровка strength / Reynolds-G2P его не превратит в Rivalry modern.

Грид в Rivalry — поиск соседей с тремя радиусами. Поля уже умеют дешёвый суррогат (P2G + Diffuse, `architecture.md`). Для визуала «стая животных» сначала нужен **тот же интегратор**, не возврат hash.

### Решение

Kinematic heading **только на пресете `Boids_mk1`**. Существующие force-пассы и Hybrid/Gray-Scott не меняем.

#### 1. Буферы — без третьего «steeringForce»

SoA уже generic (`ParticleSet.RegisterAttribute`, биндинг `HLSL-имя == AttributeId.Name`).

| Атрибут | Роль |
| --- | --- |
| `heading` (новый builtin `float3`) | persistent unit-вектор курса |
| `velocity` | **два режима в одном кадре:** до `HeadingSteerPass` — аккумулятор desired-force (как Rivalry `_AccumulatedForce`); после — `heading * CruiseSpeed` для Integrate и следующего кадра P2G |

Третий буфер не нужен. Curl/Gradient кернелы по-прежнему пишут `velocity`; на boids-пресете мы **не используем** их как Ньютон после HeadingSteer.

Инициализация: World `RegisterZeroed`. Первый кадр `heading≈0` — `HeadingSteerPass` снапает heading на `normalize(force)` (fallback `(1,0,0)` если сила тоже ноль). Спавна нет (`CubeSource` → `restPosition`).

#### 2. Новые пассы

**`ClearVelocityPass`** (Dynamics) — `velocity = 0`. Аналога particle-clear нет (только `ClearField` / `ClearFieldAccum`). GPU fill, не CPU `SetData`.

**`AddNormalizedVelocityFieldPass`** (Force) — alignment как Rivalry modern `force += normalize(avgVel) * w`:

```
if (|fieldVel| > eps) velocity += normalize(FieldUVToWorldVelocity(fieldVel)) * Weight
```

Без `dt`. Не `SampleVelocityFieldPass` (Hybrid, без normalize). Не `SteerToVelocityFieldPass` (Reynolds к persistent v — после Clear вырождается и смешивает единицы).

**`AddNormalizedGradientFieldPass`** (Force) — cohesion/separation как `force += normalize(centroid−pos) * w`:

```
if (|grad| > eps) velocity += normalize(FieldUvGradientToWorld(grad)) * Weight
```

`Weight` signed (separation < 0). Без `dt`. Не патч `SampleGradientFieldPass` (ADR-004 сырая сила, Hybrid/cohesion-playtests).

**`HeadingSteerPass`** (Dynamics) — порт `IntegrateMotion` modern **без** сдвига позиции (Integrate уже есть):

```
force = velocity                    // аккумулятор этого кадра
desired = |force|>eps ? normalize(force) : heading
h = |heading|>eps ? normalize(heading) : desired
k = saturate(TurnSpeed * DeltaTime)
h = normalize(lerp(h, desired, k))
h.y = 0; h = normalize(h)           // боиды живут в XZ (плоскость полей)
heading = h
velocity = h * CruiseSpeed
```

`|force|≈0` → курс не менять (не тормозить в ноль). Flatten Y — поля XZ; Curl иначе утащит по вертикали.

#### 3. Порядок кадра на `Boids_mk1`

Сейчас: Curl/Drag/Limit → **Integrate** → P2G → G2P. Нужно:

```
P2G flockVel / cohesion / separation (+ Diffuse, Decay)   // velocity ещё cruise прошлого кадра
ClearVelocity
AddNormalizedVelocityField(flockVel)                      // align
AddNormalizedGradient(cohesionDensity, +w)                // coh
AddNormalizedGradient(separationDensity, −w)              // sep
HeadingSteer                                              // heading + velocity=dir*cruise
Integrate
BoxBounds
```

P2G **до** Clear: splat — крейсерская скорость стаи, не force. G2P **до** HeadingSteer и **до** Integrate.

**Выкинуть из пресета:** `DragPass`, `SpeedLimitPass`, `SteerToVelocityFieldPass`, оба `SampleGradientFieldPass`, **`CurlNoisePass`**. Классы в фреймворке оставить. Выключенный слот в окне `ClearVelocity → HeadingSteer` не держать — галка `enabled=1` на текущем `CurlNoisePass` (`v += curl*amp*dt`) ломает unit-веса. Если после heading стая слишком жёсткая — отдельный unit-direction / no-dt клон (как AddNormalized*), отдельным тикетом, не заранее. То же для любого другого существующего dt-scaled Force: в это окно не класть.

**BoxBounds:** Rivalry wrap. Сменить Bounce → Wrap.

#### 4. `SimulationSpeed` vs `TurnSpeed`

Rivalry: `turnRate = saturate(Time.deltaTime * maxTurnSpeed)`, `maxTurnSpeed≈3` → ~0.05/кадр при 60 FPS.

У нас `dt = Time.deltaTime * SimulationSpeed`. При Speed=20 и `TurnSpeed=3` → `k≈1` (мгновенный доворот).

Оставить **Speed≈20** (иначе Diffuse снова ноль, ADR-011 дефект 3). Калибровать **TurnSpeed ≈ 3/Speed ≈ 0.15**, чтобы `Speed * TurnSpeed ≈ 3`. `CruiseSpeed = 4` (как Rivalry `maxSpeed` и бывший SpeedLimit).

Веса стартовые с `BoidGroupConfig`: align 0.8, cohesion 0.6, separation 1.2. Каждая сила — **unit direction × weight**; `HeadingSteer` нормализует сумму — единицы совместимы.

### Последствия

- `Boids_mk1` перестаёт быть ньютоновским playground и становится kinematic-пресетом. Другие эффекты не затронуты.
- Поля остаются суррогатом соседей (нет точного R_sep ≠ R_align, нет «не считать себя»). Если после heading стая есть, но sep «продавливается» — тогда spatial hash (Techdebt E/11), не откат интегратора.
- ADR-011 примитивы остаются в каталоге; на этом пресете alignment/cohesion/separation идут через AddNormalized*, не Steer/SampleGradient.
- Диспатчи: −4 (Drag, Limit, Steer, 2×Gradient) +3 (Clear, AddVel, 2×GradDir, Heading) ≈ тот же порядок; 6+6 Diffuse по-прежнему главные.

### Вне скоупа

- Gray-Scott-Boids / Agents / Hybrid / Echo.
- Правка `CurlNoisePass` / `SampleGradientFieldPass` / `SteerToVelocityFieldPass` глобально.
- Unit-direction / no-dt клон curl (если понадобится — отдельный тикет).
- Spatial hash, emitters, `maxForce` как отдельный примитив.
- `steeringForce` третий атрибут.
- dt clamp (Techdebt 1b).
- `Boids_mk1 1.asset` / `Boids_mk1 2.asset`.
