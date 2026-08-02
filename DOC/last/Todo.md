# Todo — M2b.1 Generic P2G Scatter

**Статус:** реализовано (см. ADR-002, status.md)

DoD закрыт:

1. `FieldAccumBuffer` + lazy alloc в `FieldSet`
2. `ClearFieldAccumPass`, abstract Scatter/Normalize, concrete velocity
3. Демо `AgentFieldEcho` (accumulate-onto-decaying + Drag/SpeedLimit)
4. Build: SM Normalize→Unclear, Channels↔descriptor, Scale/Bias agreement
5. EditMode: `FieldAccumPassValidatorTests`

Следующий шаг roadmap: M2b.2 Gradient sample (Density-пресет Replace — отдельный мини-тикет при желании).
