# Каталог пассов M3D

Справочник для онбординга: что делает пасс, какие данные читает/пишет, использует ли `dt`, какой `.compute` нужен в **Pass Library** на `SimulationWorld`.

Связанные доки: [`getting-started.md`](getting-started.md) · [`capabilities.md`](capabilities.md) · [`architecture.md`](architecture.md)

**Снимок:** 2026-09-05 (ADR-024 Harris-order закрыт; production Fluid2D без смены)

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

**`RepeatCount` (ADR-015):** World повторяет `Execute + Swap` N раз за кадр. Это **итерации решателя на одном временном слое**, не субшаги по времени: в каждую итерацию идёт один и тот же `deltaTime`. У Jacobi N итераций уточняют одно решение, эффективный шаг остаётся `dt`. У Gray-Scott / Diffuse N повторов продвигают время N раз, эффективный шаг становится `N·dt`, и граница CFL ужимается в N раз (шахматная мода Gray-Scott уже зафиксирована в Techdebt именно от большого `dt`). Поэтому текущая рекомендация «несколько копий `GrayScottPass` / `DiffuseFieldPass` в списке» **не** мигрируется на `RepeatCount` — слайдер «качества» молча ломал бы устойчивость. Субшаги по времени — отдельный механизм, его нет. `JacobiPhiPass` — первый пасс с `RepeatCount ≠ 1` (дефолт 40, `[Range(1,80)]`).

## Единицы (ADR-016)

Во фреймворке три соглашения. Существующее поведение texel- и UV-семейств **не меняется** (калибровка пресетов). Fluid-контур — только world.

| Семейство | Соглашение | Пассы |
| --- | --- | --- |
| Reaction-diffusion, boids-диффузия | **texel**: лапласиан `N+S+E+W−4C` без `/h²` | `DiffuseField`, `DiffuseVelocityField`, `GrayScott` |
| G2P-градиент | **UV**: центральные разности в UV, без деления на `Size` | `SampleGradientField`, `AddNormalizedGradientField` |
| Fluid | **world**: `vel·dt/Size` в адвекции; проекция F1 без `h` при квадратном текселе | `AdvectVelocityField`, `AdvectScalar` + все пассы F1 |

`DiffuseVelocityField` — не вязкость. Поля проекции F1: `fluidD`, `fluidPhi` (Scalar, world/s); Φ не называть давлением. `RequiresSquareTexel` реализован (F1.1 / ADR-017). `fluidD` и `fluidPhi` — `R32_SFloat`.

**Слоты текстур:** single-field → `FieldRead`/`FieldWrite`; multi-field (Role A/B) → `FieldReadA/B`/`FieldWriteA/B`.

**Порядок кадра (типично):** Shape → Force → Dynamics (Integrate → Bounds) → Emit/Transport (fields, P2G, G2P). **Kinematic boids (`Boids_mk1`, ADR-012):** P2G → ClearVelocity → AddNormalized* → HeadingSteer → Integrate → Bounds — G2P **до** Integrate, cruise `velocity` для P2G следующего кадра.

---

## Pass Library (чеклист)

На `SimulationWorld` должны быть подключены нужные compute (см. `M3DDemoTools.PassLibraryPaths`):

| Файл | Пассы |
|------|--------|
| `ShapePasses.compute` | CopyRest, Twist, SpringToRest |
| `ForcePasses.compute` | Gravity, Drag, Vortex, Attractor/Repulsor, Noise, CurlNoise, Turbulence, TouchForce |
| `DynamicsPasses.compute` | Integrate, **ClearVelocity**, **HeadingSteer**, SpeedLimit, Plane/Sphere/BoxBounds |
| `FieldPasses.compute` | TouchInjectVelocity, DecayField (velocity), SampleVelocityField, **SteerToVelocityField**, **AddNormalizedVelocityField**, **DiffuseVelocityField**, **AdvectVelocityField**, **AdvectScalar** |
| `P2GPasses.compute` | ClearUintBuffer, ScatterVelocity, NormalizeVelocityAccum |
| `DensityPasses.compute` | ScatterDensity, NormalizeDensityAccum |
| `GradientPasses.compute` | SampleGradient, **AddNormalizedGradient** |
| `DiffusePasses.compute` | DiffuseField |
| `DecayPasses.compute` | DecayFieldScalar |
| `MultiFieldTestPasses.compute` | SwapFields (тест M2c) |
| `GrayScottPasses.compute` | GrayScottReact, SeedScalarDisk |
| `TouchGrayScottPasses.compute` | TouchInjectGrayScott |
| `AgentFieldFeedbackPasses.compute` | AgentBoostField, AgentErodeField |
| `FluidPasses.compute` | Divergence, Jacobi, **Zero Mean Scalar**, **Subtract Phi Gradient**, **Solid Wall Velocity** |

`ClearFieldPass` и `ClearFieldAccumPass` — **без** своих `.compute` (Clear RT / ClearUintBuffer из P2G).

`HarnessProbes.compute` (`Assets/Tests/Editor/Shaders/`) — test-only GPU numeric harness (ADR-014). **Не** добавлять в `PassLibraryPaths` и не в таблицу выше.

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

### ClearVelocity
| | |
|--|--|
| **Назначение** | GPU `velocity = 0` — сброс force-аккумулятора перед kinematic heading (ADR-012) |
| **Библиотека / kernel** | `DynamicsPasses` / `ClearVelocity` |
| **Particles** | W: `velocity` |
| **dt** | Нет |
| **Хорошо для** | Окно `ClearVelocity → AddNormalized* → HeadingSteer` на `Boids_mk1` |

### HeadingSteer
| | |
|--|--|
| **Назначение** | Kinematic heading (Rivalry modern): nlerp `heading` к `normalize(force)`; flatten Y; **snap** `velocity = heading * CruiseSpeed` |
| **Библиотека / kernel** | `DynamicsPasses` / `HeadingSteer` |
| **Particles** | R/W: `heading`, `velocity` |
| **Параметры** | `turnSpeed` (0.15), `cruiseSpeed` (4); калибровать `Speed * turnSpeed ≈ 3` |
| **dt** | Да (только поворот; скорость — snap, не spring) |
| **Хорошо для** | `Boids_mk1` после AddNormalized*; не SpeedLimit |

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
| **Единицы** | **texel** (ADR-016 §1): лапласиан без `/h²`. `diffusionRate` зависит от разрешения и `Size`; это не fluid-оператор |
| **Хорошо для** | Cohesion blur, анти-«снежинка»; лучше **несколько мягких** пассов/кадр |

### SampleVelocityField (G2P)
| | |
|--|--|
| **Назначение** | `v += sample(velocityField) * strength` — Transport (без dt). Hybrid/Echo «ехать по полю»; **не** alignment |
| **Библиотека / kernel** | `FieldPasses` / `SampleVelocityField` |
| **Particles** | R: `position` → W: `velocity` |
| **Fields** | Read Velocity ×2 |
| **Параметры** | `velocityFieldName`, `strength` (1) |
| **dt** | **Нет** (раз за кадр) — баланс с силами `*dt` плывёт от FPS/Speed |
| **Хорошо для** | Hybrid field→particles (`HybridTouchField`); не путать со `SteerToVelocityField` (ADR-011) |

### SteerToVelocityField (G2P alignment)
| | |
|--|--|
| **Назначение** | Reynolds steering: `v += (fieldVel − v) * strength * dt`, `k = saturate(strength·dt)`. Force, не Transport |
| **Библиотека / kernel** | `FieldPasses` / `SteerToVelocityField` |
| **Particles** | R: `position` → W: `velocity` |
| **Fields** | Read Velocity ×2 |
| **Параметры** | `velocityFieldName` (default `flockVel`), `strength` (1) |
| **dt** | Да; UV вне поля → early-out (не target=0) |
| **Хорошо для** | Boids alignment (Newton/Reynolds); **не** `Boids_mk1` после ADR-012 — там `AddNormalizedVelocityField` |

### AddNormalizedVelocityField (G2P alignment, kinematic)
| | |
|--|--|
| **Назначение** | `v += normalize(fieldVel) * weight` — unit direction, **без dt** (force accumulator) |
| **Библиотека / kernel** | `FieldPasses` / `AddNormalizedVelocityField` |
| **Particles** | R: `position` → W: `velocity` |
| **Fields** | Read Velocity ×2 |
| **Параметры** | `velocityFieldName` (`flockVel`), `weight` (0.8) |
| **dt** | **Нет** |
| **Хорошо для** | ADR-012 `Boids_mk1`; после `ClearVelocity`, до `HeadingSteer` |

### DiffuseVelocityField
| | |
|--|--|
| **Назначение** | Explicit 5-point Laplacian на Velocity `float2` (per-component) |
| **Библиотека / kernel** | `FieldPasses` / `DiffuseVelocityField` |
| **Fields** | WritePingPong Velocity ×2 |
| **Параметры** | `fieldName` (`flockVel`), `diffusionRate` (0.15) |
| **dt** | Да; держи **rate·dt ≲ 0.2–0.25**; несколько мягких пассов/кадр |
| **Единицы** | **texel** (ADR-016 §1): лапласиан без `/h²`, зависит от разрешения/`Size`. **Не вязкость**, не член fluid-контура |
| **Хорошо для** | Радиус усреднения `flockVel` перед Steer (alignment blur) |

### AdvectVelocityField
| | |
|--|--|
| **Назначение** | Semi-Lagrangian self-advection velocity-поля: `sample(uv − vel·dt/Size)` |
| **Библиотека / kernel** | `FieldPasses` / `AdvectVelocityField` |
| **Fields** | WritePingPong Velocity ×2 |
| **Параметры** | `fieldName` (`flockVel`), `dissipationRate` (0 = выкл; CPU `exp(-rate·dt)`, как Decay) |
| **dt** | Да (backtrace и dissipation); UV clamp `saturate` (Neumann-подобная граница, не wrap) |
| **Единицы** | **world** (ADR-016 §1): `backUv = uv − velocity · dt / Size` |
| **Хорошо для** | Первый кирпич Stable Fluids. Компактный сгусток в **нулевом** фоне съедает себя с тыла — для переноса пика нужен несущий поток (фон + bump). Dye/pressure — отдельные пассы |
| **Ограничение** | Semi-Lagrangian bilinear **диссипативен** на off-grid backtrace (не баг). Целочисленный прогон (bump `vx=2` на фоне `1`) peak val Δ=0 — интерполяция вырождается в nearest. **Позиция:** `1.7×8=13.6` — это смещение **пассивного** tracer-а на однородном carrier; bump — лишняя скорость в ту же сторону, поэтому self-advection (Burgers) систематически обгоняет carrier. Бездиссипативный потолок overshoot для 2D-гауссиана: `N·A/2` (amp=0.05, 8 шагов → **0.200**); диссипация может только уменьшать. Геометрия автотеста: `64²`, `Size=64`, `dt=1`, центр `(20.5, 32.5)`, Gaussian `σ=1.5`, 8 шагов. amp=0.05 → overshoot **0.100** на `R32G32F` (внутри (0, 0.200)); на боевом `R16G16F` overshoot **0.26 > 0.200** — half непригоден для этого измерения, жёстче оценки шума `±0.1`. MCP `13.75` не подтверждён. amp=1 → dCom=+14.94 / dPeak=+16 (overshoot +1.3). Пик на широком профиле скачет по целым текселям; COM надёжнее. MacCormack/BFECC — отдельный тикет |

### Advect Scalar
| | |
|--|--|
| **Назначение** | Пассивный tracer: `dye_next = sample(dye, saturate(uv − u·dt/Size)) * Dissipation` (не self-advection) |
| **Библиотека / kernel** | `FieldPasses` / `AdvectScalar` (`#ifdef KERNEL_ADVECTSCALAR`) |
| **Fields** | WritePingPong Scalar ×1 Role A (`dye`); Read Velocity ×2 Role B (`velocity`). Слоты `FieldReadA`/`FieldWriteA`/`FieldReadB` |
| **Параметры** | `scalarField` (`dye`), `velocityField` (`velocity`, не `flockVel`), `dissipationRate` (0 = выкл; CPU `exp(−rate·dt)`) |
| **dt** | Да (backtrace и dissipation); UV clamp `saturate` (нет wrap; масса может налипать на рамку) |
| **Единицы** | **world** (ADR-016 §1): `backUv = uv − velocity · dt / Size`. `RequiresSquareTexel` = false |
| **Хорошо для** | Stam-контур глазами: heatmap dye в пресете Fluid2D после второго SolidWall |
| **Ограничение** | Bilinear смаз (Techdebt 5). Стен на скаляре нет. F0.5 (dye выше res, чем velocity) нет. Краска тачем — вне скоупа |

### Divergence
| | |
|--|--|
| **Назначение** | Сырая центральная дивергенция `D = uE.x − uW.x + uN.y − uS.y` (не `div = D/(2h)`) |
| **Библиотека / kernel** | `FluidPasses` / `Divergence` |
| **Fields** | Read Velocity ×2 Role A (`velocity`); WriteInPlace Scalar ×1 Role B (`fluidD`, **R32_SFloat**). Слоты `FieldReadA`/`FieldWriteB` — два имени поля, ADR-008 |
| **Параметры** | `velocityField` (default `velocity`, не `flockVel`), `divergenceField` (`fluidD`) |
| **dt** | Нет |
| **Единицы** | **world** (ADR-016 §2): `D = 2h·div`; квадратный тексель обязателен (`RequiresSquareTexel`) |
| **Граница** | clamp-продолжение поля через `Load` у Divergence; непроницаемая рамка — `SolidWallVelocityPass` (F1.4), не этот кернел |
| **Хорошо для** | Первый кернел fluid-проекции; в пресете Fluid2D после SeedScalarDisk |

### Jacobi
| | |
|--|--|
| **Назначение** | Итерация Φ: `ΦC ← (ΦN+ΦS+ΦE+ΦW − D) / 4` (ADR-016 §2, ADR-018) |
| **Библиотека / kernel** | `FluidPasses` / `Jacobi` (`#ifdef KERNEL_JACOBI`) |
| **Fields** | WritePingPong Scalar ×1 Role A (`fluidPhi`, **R32_SFloat**); Read Scalar ×1 Role B (`fluidD`). Слоты `FieldReadA`/`FieldWriteA` + `FieldReadB` |
| **Параметры** | `phiField` (default `fluidPhi`), `divergenceField` (`fluidD`), `iterations` (default 40, `[Range(1,80)]`) |
| **RepeatCount** | `iterations` (дефолт 40). Первый пасс, переопределяющий ADR-015 |
| **dt** | Нет (итерации решателя на одном временном слое, не субшаги) |
| **Единицы** | **world** (ADR-016 §2); квадратный тексель обязателен (`RequiresSquareTexel`) |
| **Warm-start** | `fluidPhi` не очищается перед Jacobi, наследует результат предыдущего кадра; дрейф `mean(Φ)` при `ΣD ≠ 0` — **устранено F1.2b** (`ZeroMeanScalarPass` до Jacobi) |
| **Граница** | clamp-продолжение Φ через `Load`; `D` читается без clamp |
| **Хорошо для** | Poisson Φ в fluid-проекции; в пресете Fluid2D после ZeroMean, Iterations=40 |

### Zero Mean Scalar
| | |
|--|--|
| **Назначение** | `D ← D − mean(D)` по всем текселям до Jacobi (условие разрешимости Neumann, ADR-018 §5.1) |
| **Библиотека / kernel** | `FluidPasses` / `ZeroMeanClear`, `ZeroMeanAccum`, `ZeroMeanApply` (`#ifdef KERNEL_ZEROMEAN`) |
| **Fields** | WriteInPlace Scalar ×1 Role A (`fluidD`). Слот `FieldWriteA`; `FieldReadA` нет. Три кернела в одном `Execute` |
| **Параметры** | `scalarField` (default `fluidD`). Bias=256, Scale от N. Не инспекторские |
| **RepeatCount** | 1 (не переопределён) |
| **dt** | Нет |
| **Единицы** | среднее без `h`; `RequiresSquareTexel` = false |
| **Хорошо для** | Совместная правая часть Jacobi при `ΣD ≠ 0`; в пресете Fluid2D между Divergence и Jacobi |

### Subtract Phi Gradient
| | |
|--|--|
| **Назначение** | Коррекция `u ← u* − ((ΦE−ΦW)/4, (ΦN−ΦS)/4)` (ADR-016 §2, ADR-020) |
| **Библиотека / kernel** | `FluidPasses` / `SubtractPhiGradient` (`#ifdef KERNEL_SUBTRACT`) |
| **Fields** | WriteInPlace Velocity ×2 Role A (`velocity`); Read Scalar ×1 Role B (`fluidPhi`, **R32_SFloat**). Слоты `FieldWriteA` / `FieldReadB` — `u*` читается из `FieldWriteA`, `FieldReadA` нет |
| **Параметры** | `velocityField` (default `velocity`, не `flockVel`), `phiField` (`fluidPhi`) |
| **RepeatCount** | 1 (не переопределён) |
| **dt** | Нет |
| **Единицы** | **world** (ADR-016 §2); квадратный тексель обязателен (`RequiresSquareTexel`) |
| **Граница** | clamp-продолжение Φ через `Load`; непроницаемая рамка скорости — `SolidWallVelocityPass` после Subtract (и снова после Advect) |
| **Хорошо для** | Замыкание Stam-проекции; в пресете Fluid2D после Jacobi, перед SolidWall. Zero-mean `fluidD` — `ZeroMeanScalarPass` перед Jacobi |

### Solid Wall Velocity
| | |
|--|--|
| **Назначение** | Free-slip: `u·n = 0` на рамке после Subtract (`x∈{0,N−1}` → `u.x=0`, `y∈{0,N−1}` → `u.y=0`, угол → `(0,0)`; интерьер не трогает) |
| **Библиотека / kernel** | `FluidPasses` / `SolidWallVelocity` (`#ifdef KERNEL_SOLIDWALL`) |
| **Fields** | WriteInPlace Velocity ×2 Role A (`velocity`). Слот `FieldWrite` (single-role), не `FieldWriteA`. `FieldReads` пустой |
| **Параметры** | `velocityField` (default `velocity`, не `flockVel`) |
| **RepeatCount** | 1 (не переопределён) |
| **dt** | Нет |
| **Единицы** | тот же fluid-грид, что проекция; `h` в формуле нет; квадратный тексель обязателен (`RequiresSquareTexel`) |
| **Хорошо для** | Непроницаемая рамка Stam. В пресете Fluid2D — после Subtract и ещё раз после Advect |

### SampleGradientField (G2P)
| | |
|--|--|
| **Назначение** | `v += ∇φ * strength * dt` (отрицательный strength = descent) |
| **Библиотека / kernel** | `GradientPasses` / `SampleGradient` |
| **Particles** | R: `position` → W: `velocity` |
| **Fields** | Read Scalar ×1 |
| **Параметры** | `fieldName`, `strength` |
| **dt** | Да |
| **Единицы** | **UV** (ADR-016 §1): `FieldUvGradientToWorld` не делит на `Size`; сила зависит от `Size` |
| **Хорошо для** | Cohesion (+) / separation (−) через density (Newton); **не** ADR-012 kinematic — там `AddNormalizedGradientField` |

### AddNormalizedGradientField (G2P cohesion/separation, kinematic)
| | |
|--|--|
| **Назначение** | `v += normalize(∇φ) * weight` — unit direction, **без dt**; signed weight |
| **Библиотека / kernel** | `GradientPasses` / `AddNormalizedGradient` |
| **Particles** | R: `position` → W: `velocity` |
| **Fields** | Read Scalar ×1 |
| **Параметры** | `fieldName`, `weight` (±; default 0.6) |
| **dt** | **Нет** |
| **Единицы** | **UV** (ADR-016 §1): тот же градиент без `/Size` |
| **Хорошо для** | ADR-012 `Boids_mk1`; cohesion +0.6 / separation −1.2 |

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
| **Единицы** | **texel** (ADR-016 §1): Du/Dv — коэффициенты texel-лапласиана; зависят от разрешения и `Size` |
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

### AgentBoostField
| | |
|--|--|
| **Назначение** | `V = max(V, saturate(presence * gain))` — агенты как катализатор |
| **Библиотека / kernel** | `AgentFieldFeedbackPasses` / `AgentBoostField` |
| **Fields** | Read RoleA Scalar (`agentPresence`) + WriteInPlace RoleB (`V`) |
| **Параметры** | `gain` (0.3); source/target имена |
| **dt** | Нет |
| **Хорошо для** | После GrayScott; вместе с AgentErode |

### AgentErodeField
| | |
|--|--|
| **Назначение** | `U *= (1 - saturate(presence * gain))` |
| **Библиотека / kernel** | `AgentFieldFeedbackPasses` / `AgentErodeField` |
| **Fields** | Read RoleA Scalar (`agentPresence`) + WriteInPlace RoleB (`U`) |
| **Параметры** | `gain` (0.3); source/target имена |
| **dt** | Нет |
| **Хорошо для** | После Boost; presence — Replace: ClearField→ClearAccum→Scatter→Normalize |

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

**Gray-Scott:** `Source Kind = None` → `SeedScalarDisk(V)` → `GrayScott×N` → `TouchInjectGrayScott` (поля на **XZ**; U clear=1, V clear=0)

**Gray-Scott-Boids:** boids-движение → `ClearField(agentPresence)` → ClearAccum→Scatter→Normalize → Seed → `GrayScott×N` → `AgentBoost` → `AgentErode` → (опц. Touch); U/V/presence **res 128, size 50** как flock plane

**Gray-Scott-Agents:** Curl/Drag/Limit/Integrate/Bounds → presence Replace → Seed → GS×N → Boost/Erode → Touch — **без** flock-полей и SampleVelocity/Gradient (поле не рулит частицами)

**Fluid2D ([ADR-022](ADR/ADR-022-Fluid2D-Preset.md) + [ADR-023](ADR/ADR-023-Advect-Scalar-Pass.md); сводка [ADR-019](ADR/ADR-019-Fluid2D-Solver.md)):** `TouchInjectVelocity → SeedScalarDisk(dye) → Divergence → ZeroMeanScalar → Jacobi×40 → SubtractPhiGradient → SolidWallVelocity → Advect velocity → SolidWallVelocity → AdvectScalar` (`Assets/Effects/Fluid2D.asset`, меню Create/Assign, InputRouter=GroundXZ, quads velocity+dye). Порядок **project → advect** измерен в [ADR-024](ADR/ADR-024-Harris-Order-Experiment.md) §7 (λ=8: Harris ~30–45% чище по `max|D|`, ≥2× нет) — production не меняли. Эталон Harris: `Fluid2D_HarrisOrder.asset` (меню Assign, не Demo Effects).

---

## Источники частиц (`DataSourceKind`)

| Kind | Назначение |
|------|------------|
| Cube / Mesh / Bitmap | Заполняют `restPosition` (и capacity) |
| **None** | 0 частиц — field-only (Gray-Scott, **Fluid2D**); particle-пассы no-op; VFX SpawnCount=0 |

---

## Частые ловушки

1. Нет compute в Pass Library → kernel not found / silent skip.  
2. Имя поля в пассе ≠ `FieldDescriptor` → ошибка Build.  
3. Multi-field: разный Resolution/plane → hard error (M2c).  
4. `SimulationSpeed` меняет «громкость» у пассов с dt; P2G и SampleVelocity **без** dt — баланс плывёт. Alignment — через `SteerToVelocityField` (с dt), не SampleVelocity.  
5. Seed срабатывает **один раз** до Rebuild.  
6. Diffuse/GrayScott: превышение CFL → каша (saturate маскирует NaN у GS, не чинит схему).

---

## Где код

| C# | Compute |
|----|---------|
| `Assets/Scripts/Passes/ShapePasses.cs` | `ShapePasses.compute` |
| `ForcePasses.cs` | `ForcePasses.compute` |
| `DynamicsPasses.cs` | `DynamicsPasses.compute` |
| `FieldPasses.cs` | `FieldPasses`, `Diffuse`, `Decay`, `MultiFieldTest`, `GrayScott`, `TouchGrayScott`, `AgentFieldFeedback` |
| `FluidPasses.cs` | `FluidPasses` (Divergence / ZeroMean / Jacobi / Subtract / SolidWall) |
| `P2GPasses.cs` | `P2GPasses`, `DensityPasses` |
| G2P gradient | `GradientPasses.compute` |

Базы: `SimPass` / `ParticleKernelPass` / `FieldKernelPass` в `Assets/Scripts/Runtime/SimPass.cs`.
