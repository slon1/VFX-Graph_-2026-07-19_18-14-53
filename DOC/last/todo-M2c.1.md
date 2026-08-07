## M2c.1: Gray-Scott Reaction-Diffusion

**Статус:** реализовано (см. ADR-009, status.md)

### DoD (закрыт)

1. `FieldKernelPass.ShouldDispatch` (default true) — early-out в sealed Execute
2. `GrayScottPass` + `GrayScottPasses.compute` (`GrayScottReact`) + Pass Library
3. `SeedScalarDiskPass` + kernel `SeedScalarDisk` (тот же compute): hasFired / ShouldDispatch / Initialize reset
4. EditMode: `GrayScottPassTests`, `SeedScalarDiskPassTests` (в т.ч. re-Initialize)
5. MCP smoke: равновесие (1,0); рост из disk seed
6. Docs: ADR-009, roadmap M2c.1, status / capabilities / getting-started

### Вне скоупа

Demo EffectAsset. 3+ поля. Agent→V P2G. LUT/trail (M2d). Wraparound. Runtime CFL guard.
