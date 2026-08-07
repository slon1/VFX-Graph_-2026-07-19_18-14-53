# ADR-006: Diffuse Field Pass

**Статус:** Реализовано (M2b.3)  
**Дата:** 2026-08-03  
**Контекст:** M3D Framework, Milestone 2b.3

### Контекст

Последний из трёх G2P/P2G примитивов (P2G scatter + Gradient + Diffuse). Мотивация:

1. **Gradient mid ≈0** между далёкими density-пиками без blur (M2b.2.1 smoke).
2. **«Снежинка»** от nearest-cell P2G — Diffuse сглаживает анизотропию сетки.
3. Задел под Gray-Scott (`Du·∇²U`).

### Решение

`DiffuseFieldPass : FieldKernelPass`, `WritePingPong`, Scalar / Channels=1.  
Отдельный [`DiffusePasses.compute`](../../Assets/Shaders/GPU/Passes/DiffusePasses.compute) с `Texture2D<float>` / `RWTexture2D<float>`.

```
laplacian = N + S + E + W − 4·C
new = C + DiffusionRate · DeltaTime · laplacian
```

Соседи через **`Load` + `clamp(q, 0, Resolution−1)`** (Neumann). `Load` без `.r` (`Texture2D<float>` → `float`). OOB `Load` без clamp вернул бы 0 — не использовать.

Default `diffusionRate = 0.15`. Empirically **`DiffusionRate · DeltaTime ≲ 0.2–0.25`**; выше — checkerboard. Несколько мягких Diffuse лучше одного большого rate.

UV-index scheme (без `/h²`) — rate как quality knob (как Gradient ADR-004).

### Scalar Decay

Реализовано в **M2b.3.1** — см. [ADR-007](ADR-007-Scalar-Field-Decay.md).

### Последствия

Переиспользует FieldKernelPass + generic slots. Vector diffuse / Gaussian / CFL runtime guard — вне скоупа.
