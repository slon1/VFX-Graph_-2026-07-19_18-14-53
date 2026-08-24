# Status — M3D Framework (Milestone 2c.1)

**Дата:** 2026-08-23  
**Итерация:** 5.16 — F1.2 JacobiPhiPass  
**Проект:** Unity `6000.4.3f1` / URP / VFX Graph 17.x  
**Сцена:** `Assets/Scenes/Test1.unity`  
**Онбординг:** [`getting-started.md`](getting-started.md) · [`pass-catalog.md`](pass-catalog.md) · [`architecture.md`](architecture.md) · [`capabilities.md`](capabilities.md)  
**ADR / roadmap:** [`adr-001`](adr-001-field-resources-m2a.md) · [`ADR-002`](last/ADR-002-Generic-P2G-Scatter.md) · [`ADR-003`](last/ADR-003-Generic-Field-Slot-Naming.md) · [`ADR-004`](last/ADR-004-Gradient-Sample-Pass.md) · [`ADR-005`](last/ADR-005-Presence-Density-P2G-Scatter.md) · [`ADR-006`](last/ADR-006-Diffuse-Field-Pass.md) · [`ADR-007`](last/ADR-007-Scalar-Field-Decay.md) · [`ADR-008`](last/ADR-008-Multi-Field-Per-Kernel-Binding.md) · [`ADR-009`](last/ADR-009-Gray-Scott-Reaction-Diffusion.md) · [`ADR-011`](last/ADR-011-Boids-Alignment-DeltaTime-And-Blur.md) · [`ADR-012`](last/ADR-012-Kinematic-Heading-Boids.md) · [`ADR-013`](ADR/ADR-013-Sampler-Verification+Velocity-Field-Self-Advection.md) · [`ADR-014`](ADR/ADR-014-GPU-Numeric-Test-Harness.md) · [`ADR-015`](ADR/ADR-015-World-Owned-Repeat-Loop.md) · [`ADR-016`](ADR/ADR-016-Units-By-Pass-Family.md) · [`ADR-017`](ADR/ADR-017-Divergence-Pass-And-Square-Texel-Contract.md) · [`ADR-018`](ADR/ADR-018-Jacobi-Phi-Pass.md) · [`roadmap`](last/roadmap_m2a.md)

---

## Цель

```
EffectAsset (Fields + Passes)
    → ParticleSet + FieldSet (+ FieldAccumBuffer)
    → SimPass pipeline (World-owned ping-pong swap)
    → Render binders (VFX / FieldQuad)
```

Доменные симуляции = композиции Pass, не подсистемы.  
Simulation Resources (`ParticleSet`, `FieldSet`) ≠ services (Input, GPU, binders).

---

## Milestone 2a — foundation (готово)

FieldSet / FieldAccess / ClearField / TouchInject / Decay / SampleVelocity. Policy C.

---

## Milestone 2b.1 / 2b.1.1 — P2G velocity + generic slots (готово)

`FieldAccumBuffer`, Scatter/Normalize **velocity** (average), `FieldRead`/`FieldWrite`. Демо AgentFieldEcho.

---

## Milestone 2b.2 — Gradient sample (готово)

`SampleGradientFieldPass` + `GradientPasses.compute`. Force: `∇ * Strength * dt`. ADR-004.

---

## Milestone 2b.2.1 — Density P2G (готово)

`ScatterDensityToFieldPass` / `NormalizeDensityAccumPass` + `DensityPasses.compute`.  
Sum decode (∝ count), Scalar/`density`. ADR-005.  
Тест: `ScatterDensityFieldPassTests`.

---

## Milestone 2b.3 — Diffuse Field (готово)

`DiffuseFieldPass` + `DiffusePasses.compute`. Explicit 5-point Laplacian, WritePingPong, Scalar.  
CFL: `rate·dt ≲ 0.2–0.25`. ADR-006. Тест: `DiffuseFieldPassTests`.

### Рекомендация: дальнодействие Diffuse-cohesion

Дальнодействие cohesion через Diffuse — компромисс между **rate**, **числом `DiffuseFieldPass` за кадр** и **разрешением поля**.

«Несколько мягких Diffuse лучше одного большого rate» значит именно **несколько пассов подряд в одном EffectAsset / одном кадре**, а не «подождать больше кадров симуляции». Чем крупнее тексель, тем быстрее (в числе текселей) фронт добегает до соседа.

MCP mid-smoke (res=64, rate=0.15, dt=1, пики на texel 10 и 50): mid `grad` до Diffuse ≈0; после N≈40–80 шагов `grad.x` к большему пику, но \|g\| растёт медленно. Если в итоговой cohesion-демке притяжение между далёкими кластерами вялое — это не баг, а следствие этой сходимости. Лечится: несколько последовательных `DiffuseFieldPass` в кадре, более грубое поле, или выше rate в пределах CFL (≲0.25).

---

## Milestone 2b.3.1 — Scalar Decay (готово)

`DecayFieldScalarPass` + `DecayPasses.compute` (Load). `SimShaderIds.DecayFactor` общий с velocity Decay.  
Default rate 1.5. ADR-007. Тест: `DecayFieldScalarPassTests`.

**Replace:** ClearField(density) каждый кадр → ClearAccum → Scatter → Normalize → …  
**Accumulate-onto-decaying:** ClearAccum → Scatter → Normalize → **DecayFieldScalar** → [Diffuse…] → SampleGradient (без ClearField).

---

## Milestone 2c — Multi-field-per-kernel (готово)

`FieldSlotRole` A/B, `FieldRequestSets.Pair`, слоты `FieldReadA/B`/`FieldWriteA/B`.  
Single-role пассы остаются на `FieldRead`/`FieldWrite`. Multi-role: geometry hard error (Resolution + plane).  
Proof: `SwapFieldsPass` + `MultiFieldTestPasses.compute`. ADR-008.  
Тесты: `FieldSlotNamingTests`, `SwapFieldsPassTests`.

---

## Milestone 2c.1 — Gray-Scott (готово)

`GrayScottPass` (U+V multi-role WritePingPong) + `SeedScalarDiskPass` (one-shot via `ShouldDispatch`/`hasFired`).  
`GrayScottPasses.compute`. Defaults Du/Dv/F/k калибровочные. `saturate` на выходе — clamp, не замена CFL. ADR-009.  
Рекомендация: **N=1–4 `GrayScottPass` подряд за кадр** — компромисс скорость реакции vs стабильность; калибровать при Speed=1.  
Тесты: `GrayScottPassTests`, `SeedScalarDiskPassTests`.  
Пресет: `Assets/Effects/Gray-Scott.asset`.

### Field-only: `DataSourceKind.None`

`NoneSource` → `ParticleSet` capacity 0. Авторегистрация particle-атрибутов пропускается; particle-пассы no-op (`Count==0`); VFX `SpawnCount=0`.  
Для RD / grid-only эффектов (Gray-Scott) — **Source Kind = None**, не «куб с 1 частицей».

---

## ADR-012 — Kinematic heading boids (готово)

`ClearVelocityPass`, `AddNormalizedVelocityFieldPass`, `AddNormalizedGradientFieldPass`, `HeadingSteerPass` + атрибут `heading`.  
Kinematic integrator (Rivalry modern): force accumulator без dt → nlerp heading → **snap** `velocity = heading * CruiseSpeed`.  
`Boids_mk1`: P2G (cruise v) → Clear → AddNormalized (align 0.8 / coh +0.6 / sep −1.2) → HeadingSteer (turn 0.15, cruise 4) → Integrate → Wrap. Speed=20, `flockVel` 64×64, 6× DiffuseVelocity. **Без** Curl/Drag/Limit/Steer/SampleGradient.  
Пресет: `Tools/M3D/ADR-012 Reconfigure Boids_mk1`. Тесты: `ClearVelocityPassTests`, `AddNormalizedVelocityFieldPassTests`, `AddNormalizedGradientFieldPassTests`, `HeadingSteerPassTests`.

---

## ADR-011 — Boids alignment Steer + DiffuseVelocity (готово)

`SteerToVelocityFieldPass` (Force, Reynolds) — **остаётся в каталоге**; на `Boids_mk1` заменён ADR-012 AddNormalized*.  
`DiffuseVelocityFieldPass` — Laplacian на Velocity `float2` (`FieldPasses.compute`); по-прежнему на `Boids_mk1` перед G2P.  
Gray-Scott-Boids: Speed=50, blur+Steer. Тесты: `SteerToVelocityFieldPassTests`, `DiffuseVelocityFieldPassTests`.

---

## ADR-013 — Sampler + Advect Velocity (готово)

`sampler_linear_clamp` подтверждён численно (Scalar 64×64, SampleLevel между текселями: obtained = bilinear, Δ=0; |Δ| to nearest = ¼–½ текселя). Переименование не требуется.  
`AdvectVelocityFieldPass` — semi-Lagrangian self-advection, WritePingPong Velocity ×2, `FieldPasses.compute`. `dissipationRate` как Decay: CPU `exp(-rate·dt)`, 0=выкл.  
MCP: uniform `(1,0)` max|Δ|=0; integer bump `vx=2` на фоне `(1,0)` за 8 шагов пик `x: 20→28`, val Δ=0 (backtrace по сетке). Fractional Gaussian extra на carrier `1.7`: амплитуда extra `0.999→0.605` (−39%) — диссипация bilinear. Позиция COM при amp=1 `+14.94` vs passive `13.6` — не ошибка backtrace, а self-advection лишней скорости. amp=0.05: overshoot **0.100** (`R32G32F`, внутри потолка `N·A/2=0.200`); **0.26** на `R16G16F` превышает потолок — half непригоден для измерения. MCP `13.75` не подтверждён. Dye/pressure/vorticity и wiring в пресет — вне скоупа. Контракт: `AdvectVelocityFieldPassTests`. Численно: `HarnessAdvectTests`.

---

## ADR-014 — GPU numeric test harness (готово)

EditMode-харнес `FieldTestHarness`: test-only `HarnessProbes.compute` (не в Pass Library), `GraphicsBuffer.GetData`, цикл Execute + Swap как у World.  
Миграция ручных MCP-чисел: `HarnessSamplerTests` (bilinear `sampler_linear_clamp`), `HarnessDiffuseTests` (Σ / max-principle / CFL / CPU-оракул), `HarnessAdvectTests` (uniform / integer bump / Gaussian COM). Инфра: `HarnessClearTests`.

---

## ADR-015 — World-owned repeat loop (готово)

`SimPass.RepeatCount` (virtual, default 1, без `[SerializeField]` в базе). `SimulationWorld.Update` повторяет `Execute + Swap` N раз внутри одного `ProfilingScope`; проверка `LastExecuteDispatched` — по итерации. `RepeatCount < 1` — ошибка Build (`RepeatCountValidator`, ADR-015 §4). Первый потребитель: `JacobiPhiPass` (дефолт 40). Тесты: `RepeatCountTests`.

---

## ADR-016 — Единицы по семействам пассов (документация)

Три соглашения: texel (Diffuse / DiffuseVelocity / GrayScott, без `/h²`), UV (G2P-градиент, без `/Size`), world (Advect + fluid F1). Существующие texel/UV не меняются. Fluid-проекция: `fluidD` / `fluidPhi`, квадратный тексель (`RequiresSquareTexel`, F1.1). Тест: `HarnessDiffuseTests.Diffuse_OneStepDelta_IsTexelLaplacian_WhenHIsNotOne` (`Size=10`, 32², центр −3).

---

## F1.2 — JacobiPhiPass (готово)

`JacobiPhiPass` в `FluidPasses.compute` (`#ifdef KERNEL_JACOBI`): `ΦC ← (ΦN+ΦS+ΦE+ΦW − D)/4`, WritePingPong Role A на `fluidPhi`, Read Role B на `fluidD`. `RepeatCount = iterations` (дефолт 40). `fluidPhi` = `R32_SFloat`. Тесты: `JacobiPhiPassTests`. Не подключён к demo-пресетам.

### F1.2b — zero-mean projection `fluidD` (открыт, блокер перед F1.6)

Вычитание `mean(D)` до Jacobi, чтобы при `ΣD ≠ 0` не копился дрейф `mean(Φ)` (ADR-018 §5). Подход: reduction через `InterlockedAdd` fixed-point аккумулятор по образцу P2G (ADR-002). Не блокер F1.3/F1.4.

---

## F1.1 — DivergenceFieldPass + RequiresSquareTexel (готово)

`SimPass.RequiresSquareTexel` + `SquareTexelValidator` на Build: (a) квадратный тексель, (b) совпадающее Resolution на всех полях пасса (переопределяет послабление `ValidatePassFieldCoordinates`). `DivergenceFieldPass` в `FluidPasses.compute`: `D = uE.x − uW.x + uN.y − uS.y`, clamp-граница, `fluidD` = `R32_SFloat`. Тесты: `DivergenceFieldPassTests`. Не подключён к demo-пресетам.

---

## F0.4 — точечные фиксы (готово)

`VfxParticleBinder`: `HasGraphicsBuffer("PositionBuffer")` + `LogWarning`, не молчаливый skip. HeadingSteer / BoxBounds wrap: `!(len > eps)` и per-axis wrap при `extents.y=0`. `FieldSet.Release` снимает `RenderTexture.active`. Advect: физическая вилка overshoot, не калиброванный 13.70±0.05.

---

## Файлы (ключевые)

```
Assets/Scripts/Passes/     FieldPasses.cs (AddNormalized*, Steer, DiffuseVelocity, AdvectVelocity, …), FluidPasses.cs (Divergence, Jacobi), DynamicsPasses.cs (ClearVelocity, HeadingSteer), P2GPasses.cs
Assets/Scripts/Runtime/    SimPass.cs (RepeatCount, RequiresSquareTexel, AttrSets.Heading, SimShaderIds.Dissipation), SimulationWorld.cs, RepeatCountValidator.cs, SquareTexelValidator.cs
Assets/Shaders/GPU/Passes/ DynamicsPasses, FieldPasses, FluidPasses, GradientPasses (AddNormalizedGradient)
Assets/Tests/Editor/       JacobiPhiPassTests, DivergenceFieldPassTests, RepeatCountTests, FieldTestHarness, HarnessClearTests, HarnessSamplerTests, HarnessDiffuseTests, HarnessAdvectTests, …
Assets/Scripts/Editor/     Adr012BoidsMk1Setup.cs
```

---

## Вне скоупа (далее)

**F1.2b** zero-mean projection `fluidD` (блокер перед F1.6) · Trail/persistence · dye/tracer advection · SubtractPressureGradient · vorticity confinement · spatial hash · AggregationMode enum · dt clamp (Techdebt 1b)
