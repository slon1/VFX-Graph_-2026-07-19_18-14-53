# Todo — M2b.3.1 Scalar Field Decay

**Статус:** реализовано (см. ADR-007, status.md)

DoD закрыт:

1. `DecayFieldScalarPass` + `SimShaderIds.DecayFactor` (velocity Decay на общий id)
2. `DecayPasses.compute` — Load; Pass Library
3. EditMode: `DecayFieldScalarPassTests`
4. Docs: ADR-007, roadmap ✅, status / capabilities / getting-started

Ручной чеклист (не коммитить Effect):  
`ClearAccum → Scatter → Normalize → DecayScalar → [Diffuse] → Gradient` без ClearField — density тает после ухода частиц.
