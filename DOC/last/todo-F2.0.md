## ТЗ для программиста — F2.0 (baseline R16, smoke, odd-even)

**Закрыто 2026-09-06.** EditMode + visual оператора. Вердикт: [ADR-026 § F2.0](../ADR/ADR-026-F2-Small-Scale-Structure.md). Это ТЗ не переоткрывать.

Роль: инфраструктура baseline **до** VC/MacCormack. Новых кернелов нет. По [ADR-026](../ADR/ADR-026-F2-Small-Scale-Structure.md) § F2.0.

Прочитать ADR-026 (скоуп дожат 2026-09-06) **до кода**. Без этого легко перевести R32-оракулы F1 на half, открыть MAC из ненулевой шахматки или воткнуть assert GPU-порога.

Зафиксировано — не пересматривать:

1. **`Fluid2D.asset` / `Fluid2D_HarrisOrder.asset` не трогать.** Не создавать `Fluid2D_Vorticity` в этом тикете. Сериализованные пассы ассета **не** `Initialize` в тесте.
2. **`FluidPasses.*` / `FieldPasses.*` / GPU-тесты F1.1–F1.8b не менять.** R32-оракулы не переводить на R16.
3. **Новых кернелов и Role C нет.** `M3DDemoTools.EnsurePassLibrary` / `PassLibraryPaths` **не** делать public — массив путей **дублировать** в тесте.
4. **EditMode GPU зелёный ≠ F2.0 закрыт.** Visual оператора — [`play-F2.0-touch.md`](play-F2.0-touch.md), не гейтит NUnit. Статус «Готово» в plan — после **обоих**.
5. **GPU ms — запись, не порог.** Нет `Assert.Less(ms, N)`. Это CPU/driver wall-clock вокруг `ExecuteCommandBuffer`, не GPU timer — в рапорте так и писать.
6. **Odd-even не открывает MAC.** Ненулевая шахматка ожидаема (ADR-016 §4). Красный только NaN/Inf.
7. **COM dye / `Σdye` / `dye>1` не ассертить** (Techdebt 5).

**CFL харнеса (вариант B, заморожен):** геометрия пресета `h = Size/Res = 32/128 = 0.25`. Не копировать `A=1`, `dt=1` с F1.8b (`h=1`) — получится **4 текселя/шаг**. Фикс: `dt = 1`, **`A = h = 0.25`** → `|u|·dt/h ≈ 1` тексель/шаг.

**Периоды TG (не копировать λ текселей с 64²):** F1.8b — 8 периодов на 64² при `λ=8` текселей. На 128² `λ=8` текселей = **16 периодов = Jacobi k=16** (дыра F1.8). Для 8i взять **8 периодов на 128²** → `lambdaTexels = 16`, `κ = 2π / (16·h) = π/2`.

Референсы: `FieldTestHarness` + `HarrisOrderExperimentTests` (хелперы, **не** форматы); `Fluid2DPresetTests` (состав, не GPU); `SimulationWorldDisabledPassInitializeTests` (`Rebuild`, `VisualEffect`). Diagnostic `fluidD_diag` R32 — как Harris: не читать рабочий `fluidD` в середине кадра.

---

### Шаг 1 — 8i: Stam-цепочка на форматах пресета

Новый файл `Assets/Tests/Editor/Fluid2DProductionProfileTests.cs`, `[Category("GPU")]`.

| | Значение |
| --- | --- |
| Resolution / Size | **128² / 32** (`h=0.25`) |
| `velocity` | `R16G16_SFloat` |
| `fluidD` / `fluidPhi` / `fluidD_diag` | `R32_SFloat` |
| `dye` | `R16_SFloat` |
| Jacobi | ×40, `DissipationRate=0` |
| `dt` | **1** |
| `A` | **0.25** (`= h`) |
| TG | 8 периодов, `lambdaTexels=16`, `κ=π/2` |
| texel-CFL | ≈ 1 |
| порядок | production: Div → ZeroMean → Jacobi×40 → Subtract → Wall → Advect u → Wall → Advect dye |

Без Touch/Seed в этом методе. Dye можно нулевой. Новые экземпляры пассов, не объекты из ассета.

Гейты: нет NaN/Inf на velocity / D / dye. `maxAbsInterior` / `maxAbsBorder` после полной цепочки (кадр 1 и 8) — в отчёт, без assert «лучше R32» и без ≥2×.

Шапка рапорта **обязана** содержать: `profile=Fluid2D formats R16G16/R32/R16 res=128 size=32 h=0.25 A=0.25 dt=1 lambdaTexels=16 periods=8 texelCFL=1 Jacobi=40 DissipationRate=0`.

`Assert.Pass(report)`.

---

### Шаг 2 — odd-even diagnostic (обязателен)

Тот же файл. Интерьер = без рамки 1 тексель (как Harris). Считать `E` на CPU после readback:

```
s(i,j) = ((i+j) % 2 == 0) ? 1 : -1
E(q) = abs( mean_{interior} q[i,j] * s(i,j) )
```

1. **Dye, u = 0.** Сид dye = `s` на интерьере. N=8 кадров. `E(dye)` до и после. При Dissipation=0 ожидание «E почти не падает» — **в лог, не assert**.
2. **Velocity Nyquist.** `u.x = 0.1 * s`, `u.y = 0`, dye = 0. `E(u.x)` и `E(D)` до/после проекции и после полной цепочки. `A_nyquist=0.1` — в шапку.

Таблица: `case, point, E`. Красный только NaN/Inf. Не ассертить «E не растёт». MAC не открывать.

---

### Шаг 3 — wall-clock ms (запись)

Харнес шага 1. Один **warmup-кадр вне** таймера (компиляция кернелов). Затем `Stopwatch` вокруг **N≥8** кадров `Execute` **без** `Read*` в timed loop. В рапорт: `elapsedMs/N`, `timer=cpu_driver_not_gpu`, Editor, 128². NUnit — только `Assert.Pass`. После зелёного — строка в Techdebt 8k без порога.

---

### Шаг 4 — два разных теста, не один

**4a. 8j World smoke** (`Fluid2DWorldSmokeTests.cs`): неактивный GO, `VisualEffect` + `SimulationWorld`, `effect` = `Assets/Effects/Fluid2D.asset` (не `CreateInstance` копия). `passLibrary` — **дубль** `PassLibraryPaths` строками в тесте. `Rebuild()` не бросает; `world.enabled == true`. **Без** N кадров, без readback, без touch, без reflection `Update`. `Build()` не делать public.

**4b. Dye seed smoke (харнес):** тот же профиль шага 1, новые пассы, один `SeedScalarDisk` (`FieldName=dye`), 8 кадров, `max(dye) > 0`, нет NaN. Это не замена 4a: 4a ловит kernel lookup боевого ассета, 4b — что seed+цепочка оставляют ненулевой dye.

---

### Шаг 5 — доки после зелёного EditMode

- ADR-026 § F2.0: «EditMode замерено» + таблица odd-even / D / ms. Visual — отдельно, когда оператор сдаст [`play-F2.0-touch.md`](play-F2.0-touch.md).
- `plan-stable-fluid.md` F2.0: **EditMode готово** / **Готово** только после visual.
- `status.md` — коротко.
- Techdebt 8i / 8j / 8k — факт замера; 8k без порога.

---

### Вне скоупа

VC; `Fluid2D_Vorticity`; MacCormack; Role C; смена `Fluid2D.asset`; MAC; F0.5; GPU-порог; перевод F1-тестов на R16; вариант C (два CFL); `dt=0.25` при `A=1` (эквивалент B, не нужен второй способ).
