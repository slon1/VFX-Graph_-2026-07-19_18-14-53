# Todo — M2b.2.1 Density P2G Scatter (sum)

**Статус:** реализовано (см. ADR-005, status.md)

DoD закрыт:

1. `ScatterDensityToFieldPass` / `NormalizeDensityAccumPass` — Channels=1, Scalar, Position-only scatter
2. `DensityPasses.compute` — sum decode (`raw/Scale − count·Bias`); Pass Library
3. EditMode: `ScatterDensityFieldPassTests`
4. Docs: ADR-005 механика, roadmap M2b.2.1 ✅

Ручной PR-чеклист (не коммитить Effect): ClearField(density) каждый кадр → ClearAccum(density,1) → Scatter → Normalize → SampleGradient; два кластера — сильнее к более жирному; без ClearField density растёт.

Следующий шаг: M2b.3 Diffuse (+ Scalar Decay).
