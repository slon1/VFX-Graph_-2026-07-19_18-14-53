# Todo — M2b.2 Sample Gradient Field Pass

**Статус:** реализовано (см. ADR-004, status.md)

DoD закрыт:

1. `SampleGradientFieldPass` — Force, `* Strength * dt`, Scalar/`density`, Channels=1, Position/Velocity
2. `GradientPasses.compute` + `FieldUvGradientToWorld`; Pass Library registration
3. EditMode contract: `SampleGradientFieldPassTests`
4. Docs: ADR-004 механика, roadmap M2b.2 ✅

Ручной PR-чеклист (не коммитить Paint/Effect): радиал length(uv-0.5) — направление от центра, |grad|≈const; вне поля → 0; у края без взрыва.

Следующий шаг roadmap: M2b.3 Diffuse.
