## ADR-019: Fluid2D solver (постфактум)

**Статус:** Принято (документация). Кода нет — кернелы и пресет уже закрыты F1.1–F1.7.
**Дата:** 2026-08-26
**Контекст:** M3D Framework, фаза F1 — итоговая сводка Stam-контура после dye
**Собирает:** единицы [ADR-016](ADR-016-Units-By-Pass-Family.md); `RequiresSquareTexel` [ADR-017](ADR-017-Divergence-Pass-And-Square-Texel-Contract.md); Jacobi / ZeroMean [ADR-018](ADR-018-Jacobi-Phi-Pass.md); Subtract [ADR-020](ADR-020-Subtract-Phi-Gradient-Pass.md); стены [ADR-021](ADR-021-Solid-Wall-Velocity-Pass.md); пресет [ADR-022](ADR-022-Fluid2D-Preset.md); dye [ADR-023](ADR-023-Advect-Scalar-Pass.md); self-advection [ADR-013](ADR-013-Sampler-Verification+Velocity-Field-Self-Advection.md); порядок project vs Harris [ADR-024](ADR-024-Harris-Order-Experiment.md)
**План фазы:** [`plan-stable-fluid.md`](../plan-stable-fluid.md)

Номер держали свободным, пока не было живого пресета с dye. Это не новый пасс и не CFD-ревизия сетки.

### Контекст

F1 собрал Stam (semi-Lagrangian advect + Jacobi-проекция), не Navier–Stokes с явной вязкостью. Desktop-first: мобильный бюджет 128² × Jacobi×40 не оценивали.

Без этой сводки следующий читатель увидит только «шахматную моду» в [ADR-016 §4](ADR-016-Units-By-Pass-Family.md) и не свяжет её с «k=12 хуже k=8». Канон замера остаётся [ADR-020 §3](ADR-020-Subtract-Phi-Gradient-Pass.md); здесь — имя свойства и карта решений.

### Решение

#### 1. Что считается Fluid2D

Пресет `Assets/Effects/Fluid2D.asset` (`Source Kind = None`, GroundXZ, Size 32, 128²):

```
TouchInjectVelocity
SeedScalarDisk(dye)
Divergence
ZeroMeanScalar
JacobiPhi ×40
SubtractPhiGradient
SolidWallVelocity
AdvectVelocityField(velocity, DissipationRate=0)
SolidWallVelocity
AdvectScalar(dye ← velocity)
```

Quads: `velocity` (`colorScale=0.125`) и `dye` (heatmap). Φ не выводим. Порядок **project → advect**, не Harris — замер [ADR-024](ADR-024-Harris-Order-Experiment.md) §7: на λ=8 Harris ~30–45% чище по интерьерному `max|D|`, порог ≥2× не взят; production не меняли. Второго прохода проекции нет.

Это композиция пассов, не подсистема и не `SolverPreset`.

#### 2. Сетка, единицы, поля

| Решение | Значение |
| --- | --- |
| Решётка | collocated, cell-centered: один тексель — `u`, `D`, `Φ`, `dye` |
| Тексель | квадратный (`RequiresSquareTexel`); matching Resolution на полях проекции |
| Единицы | **world** (ADR-016 §2): проекция без `h`/`dt`; адвекция `uv − u·dt/Size` |
| `velocity` (production) | `R16G16_SFloat` |
| `fluidD` / `fluidPhi` | `Scalar`, **`R32_SFloat`**, имена не `pressure` |
| `dye` | `Scalar` (не `FieldSemantic.Dye`), `R16_SFloat`, та же геометрия, что velocity |
| Численные оракулы | `velocity` `R32G32_SFloat`, скаляры `R32` |

Φ — потенциал в единицах скорости, не давление. Cross-res dye/velocity (F0.5) нет.

#### 3. Проекция и граница

Формулы — ADR-016 §2, без повтора. Jacobi `Iterations = 40` (калибровка F1.6: живое на 128², без пульса; осевые моды — худший случай, errata 1 ADR-020 §3 / [Techdebt 8e](../last/Techdebt.md)). `RepeatCount` = итерации на одном `dt` (ADR-015).

`ZeroMeanScalarPass` до Jacobi: `D ← D − mean(D)` по **всем** текселям. `Bias = 256` — `public const` на пассе, `Scale` от него, не от литерала. Хватило на штатный сплеш (`MaxFieldSpeed=20`, ~10 с).

Граница — два слоя, не одна «непроницаемость»:

1. Соседи Φ/`D`/`u*` — `Load` + clamp (Neumann-подобное продолжение).
2. После Subtract и снова после Advect — free-slip `u·n = 0` на рамке ([ADR-021](ADR-021-Solid-Wall-Velocity-Pass.md)).

Пуассон и ZeroMean стенка не переписывает. Рамка `D` после стен снова грязная ([Techdebt 8g](../last/Techdebt.md)) — ожидаемо.

#### 4. Known limitation: несогласованность дискретных операторов div/grad/Jacobi

**Имя.** Компактный 5-точечный лапласиан Jacobi **не есть** композиция выбранных `Divergence` и `SubtractPhiGradient`. Композиция — оператор на шаге `2h` (`div_{2h}∘grad_{2h}`), у которого чётные и нечётные тексели развязаны.

Это **одно** свойство collocated-схемы, не две легенды:

- шахматная нуль-мода Φ / odd-even на dye — [ADR-016 §4](ADR-016-Units-By-Pass-Family.md);
- «k=12 хуже k=8» по `max|D|` при тех же 40 Jacobi — errata 2 [ADR-020 §3](ADR-020-Subtract-Phi-Gradient-Pass.md) (таблица k и числа — там, не здесь).

Следствия, которые F1 принимает:

- `max|D|` после проекции имеет ненулевой пол; DoD цепочки — k=8, ≥3×, не «до нуля» и не «на порядок».
- Поднимать k или Jacobi ради 10× бессмысленно: дырка не лечится итерациями.
- **Не баг реализации.** Не чинить точечным патчем кернела.

**MAC / Rhie–Chow не открываем.** Триггер — устойчивая 1-тексельная шахматка **интерьера** на dye. F1.7: после swirl диск → 4-лучевая звезда, odd-even интерьера **не виден**. Грязь рамки и bilinear-смаз — не триггер.

#### 5. Адвекция

`AdvectVelocityField` — self-advection. `AdvectScalar` — пассивный tracer (dCOM≈8 на носителе `(1,0)`, velocity bitwise). UV `saturate` — нет wrap; налипание dye на рамку ожидаемо. Численная диссипация bilinear — [Techdebt 5](../last/Techdebt.md), путь — F2 (vorticity / MacCormack), не F1.

### Отклонённые варианты

**Занять этот номер под F1.3 / F1.6 / F1.7.** Сводка после dye, не кернел.

**Открыть MAC, потому что схема collocated.** Триггер не сработал.

**Второй Poisson после стен / Harris-порядок / явная вязкость / `DiffuseVelocity` как ν.** Отклонены в тикетах F1. Harris повторно измерен [ADR-024](ADR-024-Harris-Order-Experiment.md) §7: ≥2× нет, production не меняли. Рычаг густоты — `DissipationRate` (сейчас 0).

**Назвать Φ давлением.** ADR-016 §2.2.

### Последствия

- (+) Stam-minimum закрыт: касание → несжимаемое `u` → видимый dye.
- (+) Сетка, precision, итерации, BC и known limitation собраны в одном месте.
- (−) Это не CFD-solver: пол `max|D|`, диссипация advect, рамка после стен — приняты.
- (−) F0.5, краска тачем, мобильный бюджет, F2 — следующие фазы.

**Вне скоупа этого документа:** новый код; смена Jacobi/Bias/production-формата; MAC; F2.
