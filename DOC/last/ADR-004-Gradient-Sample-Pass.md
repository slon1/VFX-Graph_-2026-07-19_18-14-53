# ADR-004: Gradient Sample Pass

**Статус:** Реализовано (M2b.2)  
**Дата:** 2026-08-03  
**Контекст:** M3D Framework, Milestone 2b.2

### Контекст

G2P сейчас покрывает только "значение поля в точке" (`SampleVelocityFieldPass`). Нужен второй G2P-примитив — направление изменения скалярного поля (градиент), необходимый для любого поведения "двигаться по потенциалу" (cohesion/separation через density-field, будущий Gray-Scott/Lenia growth-driven steering).

### Решение

Гибридный `SampleGradientFieldPass : ParticleKernelPass` с `FieldReads` (зеркало `SampleVelocityFieldPass` по Reads/Writes = Position/Velocity). Central differences по 4 соседним сэмплам вокруг UV частицы; `Channels=1`, `FieldSemantic.Scalar`; UV-градиент → мир через `FieldUvGradientToWorld` (без `/FieldSize`).

Пасс **не** предполагает Diffuse (M2b.3): читает поле как есть. На raw/резких полях градиент шумный — ожидаемо; сглаживание — композиция EffectAsset.

### Механика

- **Интеграция:** `velocity += direction * Strength * DeltaTime` — Force (ускорение), не транспорт как SampleVelocity.
- **Категория:** `PassCategory.Force`. Defaults: `fieldName = "density"`, `strength = 1` (signed; отрицательный = против градиента).
- **HLSL:** отдельный [`GradientPasses.compute`](../../Assets/Shaders/GPU/Passes/GradientPasses.compute) с `Texture2D<float> FieldRead` (не делить файл с `float2` FieldPasses — typed-bind UB).
- **Слоты:** `SimShaderIds.FieldRead` + `FieldShaderParams.Push` (M2b.1.1).
- **Границы:** центральный UV вне `[0,1]` → вклад 0; соседи у края — `saturate` UV перед SampleLevel.
- **Без** normalize direction; только `.r` (Channels ≥ 1 на read).

### Последствия

Переиспользует инфраструктуру M2b.1.1 без нового ресурса. Pass Library обязан включать `GradientPasses.compute`.
