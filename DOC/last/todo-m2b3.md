# Todo — M2b.3 Diffuse Field Pass

**Статус:** реализовано (см. ADR-006, status.md)

DoD закрыт:

1. `DiffuseFieldPass` — WritePingPong, Scalar/density, rate=0.15, SetParams(dt + DiffusionRate)
2. `DiffusePasses.compute` — 5-point Load + clamp; Pass Library
3. EditMode: `DiffuseFieldPassTests`
4. MCP smoke: peak mass/max/area; midpoint grad before≈0 / after→B
5. Docs: ADR-006, roadmap M2b.3 ✅ + M2b.3.1 pending

Следующий шаг: **M2b.3.1 Scalar Decay**.
