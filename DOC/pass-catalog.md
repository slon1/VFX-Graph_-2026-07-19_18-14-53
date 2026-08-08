# Каталог пассов M3D

Справочник для онбординга: что делает пасс, какие данные читает/пишет, использует ли `dt`, какой `.compute` нужен в **Pass Library** на `SimulationWorld`.

Связанные доки: [`getting-started.md`](getting-started.md) · [`capabilities.md`](capabilities.md) · [`architecture.md`](architecture.md)

**Снимок:** 2026-08-08 (M2c.1 Gray-Scott + `DataSourceKind.None`)

---

## Как читать таблицу

| Колонка | Смысл |
|--------|--------|
| **Категория** | `Shape` / `Force` / `Dynamics` / `Emit` / `Transport` — роль в кадре, не «тип данных» |
| **Библиотека** | Файл в `Assets/Shaders/GPU/Passes/` → слот Pass Library |
| **Particles R/W** | SoA-атрибуты: `position`, `velocity`, `restPosition` |
| **Fields** | Имя поля + `FieldAccess` + semantic |
| **dt** | Да = масштабируется `Time.deltaTime × SimulationSpeed`. Нет = раз за кадр (или разово) |

**FieldAccess:**

- `Read` — SRV Current  
- `WriteInPlace` — UAV Current (без swap)  
- `WritePingPong` — SRV Current + UAV Next → World делает `Swap` после dispatch  

**Слоты текстур:** single-field → `FieldRead`/`FieldWrite`; multi-field (Role A/B) → `FieldReadA/B`/`FieldWriteA/B`.

**Порядок кадра (типично):** Shape → Force → Dynamics (Integrate → Bounds) → Emit/Transport (fields, P2G, G2P). G2P после Integrate = лаг силы на 1 кадр (осознанно в boids-пресетах).

---

## Pass Library (чеклист)

На `SimulationWorld` должны быть подключены нужные compute (см. `M3DDemoTools.PassLibraryPaths`):

| Файл | Пассы |
|------|--------|
| `ShapePasses.compute` | CopyRest, Twist, SpringToRest |
| `ForcePasses.compute` | Gravity, Drag, Vortex, Attractor/Repulsor, Noise, CurlNoise, Turbulence, TouchForce |
| `DynamicsPasses.compute` | Integrate, SpeedLimit, Plane/Sphere/BoxBounds |
| `FieldPasses.compute` | TouchInjectVelocity, DecayField (velocity), SampleVelocityField |
| `P2GPasses.compute` | ClearUintBuffer, ScatterVelocity, NormalizeVelocityAccum |
| `DensityPasses.compute` | ScatterDensity, NormalizeDensityAccum |
| `GradientPasses.compute` | SampleGradient |
| `DiffusePasses.compute` | DiffuseField |
| `DecayPasses.compute` | DecayFieldScalar |
| `MultiFieldTestPasses.compute` | SwapFields (тест M2c) |
| `GrayScottPasses.compute` | GrayScottReact, SeedScalarDisk |
| `TouchGrayScottPasses.compute` | TouchInjectGrayScott |

`ClearFieldPass` и `ClearFieldAccumPass` — **без** своих `.compute` (Clear RT / ClearUintBuffer из P2G).

---

## Shape

### CopyRest
| | |
|--|--|
| **Назначение** | Каждый кадр `position = restPosition`. Старт shape-цепочки без feedback. |
| **Библиотека / kernel** | `ShapePasses` / `CopyRest` |
| **Particles** | R: `restPosition` → W: `position` |
| **Fields** | — |
| **Параметры** | — |
| **dt** | Нет |
| **Хорошо для** | Morph / twist / любой «пересобрать форму из rest» |

### Twist
| | |
|--|--|
| **Назначение** | Скрутка вокруг Y; фаза на CPU `+= strength * dt`. |
| **Библиотека / kernel** | `ShapePasses` / `Twist` |
| **Particles** | R/W: `position` |
| **Параметры** | `strength` (default 1) |
| **dt** | Да (фаза) |
| **Хорошо для** | TwistedCube, «живой» twist |

### SpringToRest
| | |
|--|--|
| **Назначение** | Пружина к rest → пишет **velocity** (не position). |
| **Библиотека / kernel** | `ShapePasses` / `SpringToRest` |
| **Particles** | R: `position`, `restPosition` → W: `velocity` |
| **Параметры** | `stiffness` (10), `damping` (2) |
| **dt** | Да |
| **Хорошо для** | Возврат к форме после деформации; ставить **до** Integrate |

---

## Force (частицы)

### Gravity
| | |
|--|--|
| **Назначение** | `v += g * dt` |
| **Библиотека / kernel** | `ForcePasses` / `Gravity` |
| **Particles** | W: `velocity` |
| **Параметры** | `gravity` (0,−9.81,0) |
| **dt** | Да |
| **Хорошо для** | Падение, пыль |

### Drag
| | |
|--|--|
| **Назначение** | `v *= exp(-drag * dt)` |
| **Библиотека / kernel** | `ForcePasses` / `Drag` |
| **Particles** | W: `velocity` |
| **Параметры** | `drag` (1) |
| **dt** | Да (через factor на CPU) |
| **Хорошо для** | Стабилизация роя / шума |

### Vortex
| | |
|--|--|
| **Назначение** | Тангенциальная сила вокруг оси |
| **Библиотека / kernel** | `ForcePasses` / `Vortex` |
| **Particles** | R: `position` → W: `velocity` |
| **Параметры** | `center`, `axis`, `strength` (5), `radius` (5) |
| **dt** | Да |
| **Хорошо для** | GalaxySwirl, водовороты |

### Attractor / Repulsor
| | |
|--|--|
| **Назначение** | Точечная сила к/от центра (один kernel `PointForce`, знак strength) |
| **Библиотека / kernel** | `ForcePasses` / `PointForce` |
| **Particles** | R: `position` → W: `velocity` |
| **Параметры** | `center`, `strength`, `radius` |
| **dt** | Да |
| **Хорошо для** | Реактивные сгустки, отталкивание |

### NoiseForce / CurlNoise / Turbulence
| | |
|--|--|
| **Назначение** | Процедурный шум / curl / FBM → добавка к velocity |
| **Библиотека / kernel** | `ForcePasses` / `NoiseForce`, `CurlNoiseForce`, `TurbulenceForce` |
| **Particles** | R: `position` → W: `velocity` |
| **Параметры** | `frequency`, `amplitude`, `speed` (+ `octaves` у Turbulence) |
| **dt** | Да (`* amplitude * dt`); время семпла — `SimTime` |
| **Хорошо для** | Seed движения (boids), «органика»; Curl — бездивергентный фон |

### TouchForce
| | |
|--|--|
| **Назначение** | Тач → импульс на частицы (drag + push) |
| **Библиотека / kernel** | `ForcePasses` / `TouchImpulse` |
| **Particles** | R: `position` → W: `velocity` |
| **Параметры** | `dragStrength`, `pushStrength` |
| **dt** | Частично (push × dt; drag от `touch.delta` per-frame) |
| **Хорошо для** | ReactiveDust, тач по частицам (не по полю) |

---

## Dynamics

### Integrate
| | |
|--|--|
| **Назначение** | `position += velocity * dt` |
| **Библиотека / kernel** | `DynamicsPasses` / `Integrate` |
| **Particles** | R: `velocity` → W: `position` |
| **dt** | Да |
| **Хорошо для** | Обязательный шаг после сил. Один раз за кадр обычно |

### SpeedLimit
| | |
|--|--|
| **Назначение** | Clamp \|v\| ≤ maxSpeed |
| **Библиотека / kernel** | `DynamicsPasses` / `SpeedLimit` |
| **Particles** | W: `velocity` |
| **Параметры** | `maxSpeed` (10) |
| **dt** | Нет |
| **Хорошо для** | Антиразлёт; обычно перед Integrate |

### PlaneCollider / SphereCollider / BoxBounds
| | |
|--|--|
| **Назначение** | Столкновения / границы домена |
| **Библиотека / kernel** | `DynamicsPasses` / соответствующие |
| **Particles** | R/W: `position` (+ `velocity` у коллайдеров) |
| **Параметры** | plane: point/normal/bounce/friction; sphere: center/radius/…; box: center/extents/`BoundsBehaviour` (Clamp/Wrap/…)/bounce |
| **dt** | Нет (мгновенная коррекция) |
| **Хорошо для** | Удержание в боксе; Wrap — тороид для роя |

---

## Fields — Emit / Transport

### ClearField
| | |
|--|--|
| **Назначение** | Current = `FieldDescriptor.ClearValue` (без compute) |
| **Библиотека** | — |
| **Fields** | WriteInPlace (любое имя/semantic/channels по запросу) |
| **dt** | Нет |
| **Хорошо для** | Replace-семантика density каждый кадр; сброс velocity field |

### TouchInjectVelocityField
| | |
|--|--|
| **Назначение** | Тач → splat в velocity-поле (плоскость поля) |
| **Библиотека / kernel** | `FieldPasses` / `TouchInjectVelocity` |
| **Fields** | WriteInPlace Velocity ×2 (default `velocity`) |
| **Параметры** | `maxFieldSpeed` (20) |
| **dt** | Нет (от touch delta) |
| **Хорошо для** | HybridTouchField |

### DecayField (velocity RG)
| | |
|--|--|
| **Назначение** | `v *= exp(-rate * dt)` на velocity-поле |
| **Библиотека / kernel** | `FieldPasses` / `DecayField` |
| **Fields** | WritePingPong Velocity ×2 |
| **Параметры** | `fieldName`, `decayRate` (1.5) |
| **dt** | Да |
| **Хорошо для** | Accumulate-onto-decaying для flockVel / agentVelocity |

### DecayFieldScalar
| | |
|--|--|
| **Назначение** | То же для Scalar R16 |
| **Библиотека / kernel** | `DecayPasses` / `DecayFieldScalar` |
| **Fields** | WritePingPong Scalar ×1 |
| **Параметры** | `fieldName` (`density`), `decayRate` (1.5) |
| **dt** | Да |
| **Хорошо для** | Density accumulate без ClearField |

### DiffuseField
| | |
|--|--|
| **Назначение** | Explicit 5-point Laplacian на Scalar |
| **Библиотека / kernel** | `DiffusePasses` / `DiffuseField` |
| **Fields** | WritePingPong Scalar ×1 |
| **Параметры** | `fieldName`, `diffusionRate` (0.15) |
| **dt** | Да; держи **rate·dt ≲ 0.2–0.25** |
| **Хорошо для** | Cohesion blur, анти-«снежинка»; лучше **несколько мягких** пассов/кадр |

### SampleVelocityField (G2P)
| | |
|--|--|
| **Назначение** | `v += sample(velocityField) * strength` |
| **Библиотека / kernel** | `FieldPasses` / `SampleVelocityField` |
| **Particles** | R: `position` → W: `velocity` |
| **Fields** | Read Velocity ×2 |
| **Параметры** | `velocityFieldName`, `strength` (1) |
| **dt** | **Нет** (раз за кадр) — баланс с силами `*dt` плывёт от FPS/Speed |
| **Хорошо для** | Alignment / hybrid field→particles |

### SampleGradientField (G2P)
| | |
|--|--|
| **Назначение** | `v += ∇φ * strength * dt` (отрицательный strength = descent) |
| **Библиотека / kernel** | `GradientPasses` / `SampleGradient` |
| **Particles** | R: `position` → W: `velocity` |
| **Fields** | Read Scalar ×1 |
| **Параметры** | `fieldName`, `strength` |
| **dt** | Да |
| **Хорошо для** | Cohesion (+) / separation (−) через density |

### SwapFields
| | |
|--|--|
| **Назначение** | `A↔B` — proof multi-field binding (M2c) |
| **Библиотека / kernel** | `MultiFieldTestPasses` / `SwapFields` |
| **Fields** | WritePingPong Pair Role A/B Scalar |
| **dt** | Нет |
| **Хорошо для** | Тесты/smoke; не продакшен-эффект |

### SeedScalarDisk
| | |
|--|--|
| **Назначение** | One-shot диск в Scalar (UV); `ShouldDispatch` + `hasFired`, сброс на Initialize/Rebuild |
| **Библиотека / kernel** | `GrayScottPasses` / `SeedScalarDisk` |
| **Fields** | WriteInPlace Scalar (default `V`) |
| **Параметры** | `centerUV` (0.5,0.5), `radiusUV` (0.06), `value` (1) |
| **dt** | Нет (один dispatch за Rebuild) |
| **Хорошо для** | Старт Gray-Scott; фон через ClearValue (U=1, V=0) |

### GrayScottPass
| | |
|--|--|
| **Назначение** | Реакция-диффузия U/V в одном кернеле |
| **Библиотека / kernel** | `GrayScottPasses` / `GrayScottReact` |
| **Fields** | WritePingPong Pair: U RoleA, V RoleB, Scalar; одинаковые res+plane |
| **Параметры** | `Du` 0.16, `Dv` 0.08, `F` 0.035, `k` 0.06 (калибровать) |
| **dt** | Да; CFL как Diffuse для Du и Dv; выход `saturate` |
| **Хорошо для** | Узоры RD; **N=1–4** пассов/кадр при Speed≈1…40; F/k шагать по ±0.001 |

### TouchInjectGrayScott
| | |
|--|--|
| **Назначение** | Тач → поднять V≈1 и погасить U в радиусе (инъекция катализатора) |
| **Библиотека / kernel** | `TouchGrayScottPasses` / `TouchInjectGrayScott` |
| **Fields** | WriteInPlace Pair: U RoleA, V RoleB, Scalar |
| **Параметры** | имена U/V; радиус/сила — из `TouchForce` (InputRouter), не с пасса |
| **dt** | Нет; `max` по тачам (не `+=`); `delta` не используется |
| **Хорошо для** | Интерактивный Gray-Scott; **после** последнего `GrayScottPass` в кадре |
| **Кисть** | `touchStrength≈1` — мягкий falloff; дефолт роутера 10 ≈ жёсткий диск |

---

## P2G (частица → поле)

Общий рецепт: `ClearFieldAccum` → `Scatter*` → `Normalize*` → (Decay / Diffuse / …).

Normalize делает **`FieldWrite += decoded`** (не replace) — без Decay/ClearField поле растёт.

### ClearFieldAccum
| | |
|--|--|
| **Назначение** | Обнулить uint accum буфер поля |
| **Библиотека / kernel** | `P2GPasses` / `ClearUintBuffer` |
| **Параметры** | `fieldName`, `channels` (value-каналы; count = +1 внутри) |
| **dt** | Нет |
| **Хорошо для** | Перед каждым Scatter |

### ScatterVelocityToField / NormalizeVelocityAccum
| | |
|--|--|
| **Назначение** | P2G средней скорости в тексель (average) |
| **Библиотека / kernel** | `P2GPasses` / `ScatterVelocity`, `NormalizeVelocityAccum` |
| **Particles** | Scatter R: `position`+`velocity` |
| **Fields** | Velocity ×2; Normalize WriteInPlace += |
| **Параметры** | `targetFieldName`/`fieldName`, `valueScale` 4096, `valueBias` 32 |
| **dt** | Нет |
| **Хорошо для** | Alignment field (flockVel), AgentFieldEcho |

### ScatterDensityToField / NormalizeDensityAccum
| | |
|--|--|
| **Назначение** | P2G presence: сумма ∝ числу частиц |
| **Библиотека / kernel** | `DensityPasses` / `ScatterDensity`, `NormalizeDensityAccum` |
| **Particles** | Scatter R: `position` |
| **Fields** | Scalar ×1; Normalize WriteInPlace += |
| **Параметры** | scale 4096, bias 0 |
| **dt** | Нет |
| **Хорошо для** | Cohesion/separation density; Replace = ClearField каждый кадр |

---

## Типовые цепочки

**Shape:** `CopyRest → Twist`  

**Particles + touch:** `… Force → TouchForce → Drag → SpeedLimit → Integrate → Bounds`  

**Hybrid touch field:** `TouchInjectVelocity → DecayField → SampleVelocity → Integrate`  

**P2G velocity echo:** `ClearAccum → ScatterVelocity → NormalizeVelocity → DecayField` (+ опц. SampleVelocity)  

**Density cohesion:**  
`ClearAccum → ScatterDensity → NormalizeDensity → DecayScalar → Diffuse×N → SampleGradient(+)`  

**Boids-field:** Curl/Drag/Limit/Integrate/Bounds → alignment P2G+Decay → cohesion density+Diffuse → separation density → SampleVel / SampleGrad±  

**Gray-Scott:** `Source Kind = None` → `SeedScalarDisk(V)` → `GrayScott×N` → `TouchInjectGrayScott` (поля на **XZ**, как Hybrid; U clear=1, V clear=0)

---

## Источники частиц (`DataSourceKind`)

| Kind | Назначение |
|------|------------|
| Cube / Mesh / Bitmap | Заполняют `restPosition` (и capacity) |
| **None** | 0 частиц — field-only (Gray-Scott и т.п.); particle-пассы no-op; VFX SpawnCount=0 |

---

## Частые ловушки

1. Нет compute в Pass Library → kernel not found / silent skip.  
2. Имя поля в пассе ≠ `FieldDescriptor` → ошибка Build.  
3. Multi-field: разный Resolution/plane → hard error (M2c).  
4. `SimulationSpeed` меняет «громкость» у пассов с dt; P2G и SampleVelocity **без** dt — баланс плывёт.  
5. Seed срабатывает **один раз** до Rebuild.  
6. Diffuse/GrayScott: превышение CFL → каша (saturate маскирует NaN у GS, не чинит схему).

---

## Где код

| C# | Compute |
|----|---------|
| `Assets/Scripts/Passes/ShapePasses.cs` | `ShapePasses.compute` |
| `ForcePasses.cs` | `ForcePasses.compute` |
| `DynamicsPasses.cs` | `DynamicsPasses.compute` |
| `FieldPasses.cs` | `FieldPasses`, `Diffuse`, `Decay`, `MultiFieldTest`, `GrayScott`, `TouchGrayScott` |
| `P2GPasses.cs` | `P2GPasses`, `DensityPasses` |
| G2P gradient | `GradientPasses.compute` |

Базы: `SimPass` / `ParticleKernelPass` / `FieldKernelPass` в `Assets/Scripts/Runtime/SimPass.cs`.
