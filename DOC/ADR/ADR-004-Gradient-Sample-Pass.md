# ADR-004: Gradient Sample Pass

**Статус:** Реализовано (M2b.2)  
**Дата:** 2026-08-03  
**Контекст:** M3D Framework, Milestone 2b.2

### Контекст

G2P сейчас покрывает только "значение поля в точке" (`SampleVelocityFieldPass`). Нужен второй G2P-примитив — направление изменения скалярного поля (градиент), необходимый для любого поведения "двигаться по потенциалу" (cohesion/separation через density-field, будущий Gray-Scott/Lenia growth-driven steering).

### Решение

Гибридный `SampleGradientFieldPass : ParticleKernelPass` с `FieldReads` (зеркало `SampleVelocityFieldPass` по Reads/Writes = Position/Velocity). Central differences по 4 соседним сэмплам вокруг UV частицы; `Channels=1`, `FieldSemantic.Scalar`; UV-градиент → мир через `FieldUvGradientToWorld` (без `/FieldSize`).

Пасс читает поле как есть. На raw/резких полях градиент шумный — ожидаемо; сглаживание — композиция с `DiffuseFieldPass` ([ADR-006](ADR-006-Diffuse-Field-Pass.md)).

### Механика

- **Интеграция:** `velocity += direction * Strength * DeltaTime` — Force (ускорение), не транспорт как SampleVelocity.
- **Категория:** `PassCategory.Force`. Defaults: `fieldName = "density"`, `strength = 1` (signed; отрицательный = против градиента).
- **HLSL:** отдельный [`GradientPasses.compute`](../../Assets/Shaders/GPU/Passes/GradientPasses.compute) с `Texture2D<float> FieldRead` (не делить файл с `float2` FieldPasses — typed-bind UB).
- **Слоты:** `SimShaderIds.FieldRead` + `FieldShaderParams.Push` (M2b.1.1).
- **Границы:** центральный UV вне `[0,1]` → вклад 0; соседи у края — `saturate` UV перед SampleLevel.
- **Без** normalize direction; только `.r` (Channels ≥ 1 на read).

### Последствия

Переиспользует инфраструктуру M2b.1.1 без нового ресурса. Pass Library обязан включать `GradientPasses.compute`.

### Сноска (ADR-011)

С ADR-011 в фреймворке **два** G2P-инструмента для velocity-поля — не путать:

| Пасс | Категория | Формула | dt | Потребители |
| --- | --- | --- | --- | --- |
| `SampleVelocityFieldPass` | Transport | `v += fieldVel * strength` | нет | Hybrid / Echo |
| `SteerToVelocityFieldPass` | Force | `v += (fieldVel − v) * strength * dt` (`saturate`) | да | Boids alignment |

Blur velocity-поля: `DiffuseVelocityFieldPass` (аналог `DiffuseFieldPass` на `float2`).

### Сноска (ADR-012)

На kinematic-пресете `Boids_mk1` cohesion/alignment/separation — **AddNormalizedVelocityField** / **AddNormalizedGradientField** (unit direction, без dt), не `SampleGradientFieldPass` / `SteerToVelocityFieldPass`. В окно `ClearVelocity → HeadingSteer` не класть dt-scaled Force (CurlNoise и т.п.).
