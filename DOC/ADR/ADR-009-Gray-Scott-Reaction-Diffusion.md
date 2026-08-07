## ADR-009: Gray-Scott Reaction-Diffusion

**Статус:** Реализовано (M2c.1)  
**Дата:** 2026-08-07  
**Контекст:** M3D Framework, Milestone 2c.1

### Контекст

Первая полноценная система из исходного вдохновляющего списка (наравне с Lenia), не сводимая к комбинации уже существующих однопольных примитивов — реакция явно связывает оба поля в одной формуле:

```
∂U/∂t = Du·∇²U − U·V² + F·(1−U)
∂V/∂t = Dv·∇²V + U·V² − (F+k)·V
```

M2c (`SwapFieldsPass`) — прямая архитектурная предпосылка: multi-role WritePingPong, та же декларация Pair Role A/B.

### Решение

#### Биндинг

`GrayScottPass : FieldKernelPass`, `FieldWrites = Pair(U RoleA, V RoleB, WritePingPong, Scalar, 1)`, `FieldReads` пуст. Без изменений guard/geometry M2c.

#### Численная схема

5-point Laplacian (как Diffuse / ADR-006) для U и V в одном кернеле; Neumann clamp; UV-index rate (без `/h²`).

#### Параметры

`Du`, `Dv`, `F`, `k` + `DeltaTime`. CFL: `rate·dt ≲ 0.2–0.25` для каждого из Du/Dv. Defaults калибровочные (Du=0.16, Dv=0.08, F=0.035, k=0.06), не канон Pearson.

#### Seeding

`SeedScalarDiskPass` (WriteInPlace, Scalar): диск в UV. One-shot через `FieldKernelPass.ShouldDispatch` + `hasFired` (сброс в `Initialize` / Rebuild). Фон: U clear=1, V clear=0 на дескрипторах.

#### saturate

Выход `saturate` в `[0,1]` — clamp диапазона концентраций, **не замена CFL**.

### Последствия

Аддитивно к M2c. Kernel IDs Du/Dv/F/k — private в классе (не `SimShaderIds`). N=1–4 `GrayScottPass` за кадр — doc-рекомендация при Speed=1.

**Field-only:** пресет `Assets/Effects/Gray-Scott.asset` использует `DataSourceKind.None` (`NoneSource`, capacity 0) — частицы для RD не нужны. Вне скоупа: agent→V P2G, wraparound, runtime CFL guard, LUT/trail (M2d).
