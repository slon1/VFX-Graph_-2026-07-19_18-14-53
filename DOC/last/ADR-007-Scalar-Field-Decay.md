# ADR-007: Scalar Field Decay

**Статус:** Реализовано (M2b.3.1)  
**Дата:** 2026-08-04  
**Контекст:** M3D Framework, Milestone 2b.3.1

### Контекст

`DecayFieldPass` (M2a) жёстко работает с 2-канальными (velocity) полями — typed `Texture2D<float2>` vs `Texture2D<float>`. Без Scalar Decay density жила только в Replace (`ClearField` каждый кадр); Accumulate-onto-decaying (как AgentFieldEcho) была недоступна.

### Решение

`DecayFieldScalarPass : FieldKernelPass` — зеркало velocity Decay:

- `new = value * DecayFactor`, `DecayFactor = exp(-rate·dt)` на CPU
- WritePingPong, Scalar / Channels=1, default `density`, `decayRate = 1.5`
- Отдельный [`DecayPasses.compute`](../../Assets/Shaders/GPU/Passes/DecayPasses.compute): `Texture2D<float>`, точечный **`Load`** (без `.r`)
- Общий `SimShaderIds.DecayFactor` (velocity Decay переведён на него же)

Accumulate-onto-decaying для density:

```
ClearAccum → ScatterDensity → NormalizeDensity → DecayFieldScalar → [Diffuse…] → SampleGradient
```

Без `ClearField(density)`.

### Последствия

Паритет density с velocity по Decay/Diffuse/P2G/Gradient. Multi-channel (3–4) decay — вне скоупа.
