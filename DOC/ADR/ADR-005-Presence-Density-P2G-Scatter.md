# ADR-005: Density P2G Scatter (sum)

**Статус:** Реализовано (M2b.2.1)  
**Дата:** 2026-08-03  
**Контекст:** M3D Framework, Milestone 2b.2.1

### Контекст

`ScatterVelocityToFieldPass` / `NormalizeVelocityAccumPass` — P2G на 2 канала (velocity, **average**). Для cohesion через `SampleGradientFieldPass` нужно скалярное поле density: частица пишет вклад `1.0`, decode даёт сигнал ∝ числу частиц в текселе.

Абстрактные базы M2b.1 (`Channels` / Scale / Bias) переиспользуются без изменений.

### Решение

- `ScatterDensityToFieldPass` / `NormalizeDensityAccumPass`
- Отдельный [`DensityPasses.compute`](../../Assets/Shaders/GPU/Passes/DensityPasses.compute) с `RWTexture2D<float> FieldWrite` (не смешивать с float2 в P2GPasses)

### Механика

- **Scatter:** `EncodeFixed(1.0, Scale, Bias)` + InterlockedAdd value и count; layout `[value, count]`, `BufferCount = 2`
- **Normalize (sum, не average):**
  ```
  decoded = raw/Scale − count·Bias   // без /count; count==0 → skip
  FieldWrite += decoded
  ```
  Bias сокращается алгебраически при любом Bias для вклада `1.0` (защита от copy-paste Bias≠0).
- **Semantic:** `FieldSemantic.Scalar`, Channels=1; defaults `density`, Scale=4096, Bias=0
- **ADR-002:** average остаётся каноном для **velocity**; sum — осознанное исключение только в Density normalize
- **Replace:** каждый кадр обязателен `ClearField(density)` до Scatter/Normalize; иначе `+=` копит density между кадрами.
- **Accumulate-onto-decaying:** без ClearField; после Normalize — `DecayFieldScalarPass` ([ADR-007](ADR-007-Scalar-Field-Decay.md))
- **Overflow uint:** при Scale=4096 потолок ~1e6 частиц/тексель (как документированный лимит P2G velocity)
- **Pass Library:** `DensityPasses.compute` в `M3DDemoTools.PassLibraryPaths`

### Последствия

Ноль изменений в абстрактных базах, валидаторе, state machine.  
Cohesion Replace: ClearField → ClearAccum → ScatterDensity → NormalizeDensity → [Diffuse…] → SampleGradient.  
Cohesion Accumulate: ClearAccum → Scatter → Normalize → DecayScalar → [Diffuse…] → SampleGradient.
