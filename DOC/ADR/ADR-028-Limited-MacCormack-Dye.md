## ADR-028: Limited MacCormack для dye (F2.2)

**Статус:** Принято. F2.2 **закрыт** (EditMode + visual). Look F2 не взят.
**Дата:** 2026-09-16
**Контекст:** M3D Framework, F2.2 — меньше смаза пассивного tracer
**Скоуп фазы:** [ADR-026](ADR-026-F2-Small-Scale-Structure.md) § F2.2 (binding здесь не переоткрывать)
**ТЗ:** [`todo-F2.2.md`](../last/todo-F2.2.md) · **Тач:** [`play-F2.2-touch.md`](../last/play-F2.2-touch.md)
**Не меняет:** `Fluid2D.asset`, `Fluid2D_HarrisOrder.asset`, `Fluid2D_Vorticity.asset`, кернелы проекции F1, Role C, MacCormack velocity

### Контекст

F2.1/F2.1b закрыты: VC не дал интерьерных филаментов; торнадо рамки оказались clamp-curl. Цель F2.2 — **другой механизм**: semi-Lagrangian bilinear жрёт dye ([Techdebt 5](../last/Techdebt.md)). Limited MacCormack на **скаляре** уменьшает этот смаз. Velocity-correction вне F2.

Имя метода одно: **limited MacCormack**, не BFECC.

### Решение

#### 1. Три значения, два имени поля, без `FieldReadC`

φ0, φ_f, φ_b **не** живут в одном dispatch через Role C. Потолок [ADR-008](ADR-008-Multi-Field-Per-Kernel-Binding.md) `{A}` / `{A,B}` не трогать.

Именованное scratch-поле на `EffectAsset`: **`dyeMacScratch`**. Обычный `FieldDescriptor` (Scalar, та же геометрия и **тот же format**, что `dye`), не третий role-слот.

Мир после каждого `Execute` делает swap всех `WritePingPong` этого пасса. Этим пользуемся:

| Шаг | Пасс | После Execute+Swap |
| --- | --- | --- |
| 0 | — | `dye.Current = φ0` |
| 1 | `CopyScalar` scratch ← dye | `dyeMacScratch = φ0` (WriteInPlace, без swap) |
| 2 | `AdvectScalar` +dt | `dye.Current = φ_f`, `dye.Next` = старый φ0 |
| 3 | `AdvectScalar` reverse (−dt) | `dye.Current = φ_b`, `dye.Next = φ_f` |
| 4 | `LimitedMacCormackCombine` | читает φ_b с Current, **Load** φ_f с Next (UAV), φ0 со scratch; пишет φ* в Next; swap → Current = φ* |

Прецедент Load с UAV: [ADR-020](ADR-020-Subtract-Phi-Gradient-Pass.md) (`u*` из `FieldWriteA`). Если UAV-Load φ_f на Editor GPU незаконен — **стоп**, не заводить `FieldReadC`.

Один `SimPass` с тремя `FieldRequest` именами (`dye` + `velocity` + scratch) **запрещён**. `Fields.Get` мимо списка запросов — тоже. `Execute` как у ZeroMean (свой буфер) — нет: scratch виден на ассете.

#### 2. Формула

```
φ* = φ_f + 0.5 (φ0 − φ_b)
φ* ← clamp(φ*, min(φ0, φ_f, φ_b), max(φ0, φ_f, φ_b))
φ* ← max(φ*, 0)
```

Все три значения — **тот же тексель `p`**. `φ_f` — `Load`/`[]` **только в `p`** с `FieldWriteA`. Соседей UAV и соседей φ0 **не** читать.

5-точка φ0 в `p` **отклонена (замер 2026-09-16):** на off-grid `(1.7,0)` COM 9.20 vs bilinear 13.59, пик 0.45 vs 0.74 — clamp к носителю φ0 в точке назначения запрещает перенос (CFL ≲ 1 тексель/шаг). Классический limiter по 4 углам backUv в combine не берём: нужен `velocity` (третье имя) или упаковка min/max. Точечный clamp — осознанное упрощение ([Techdebt 8m](../last/Techdebt.md)): оракул = равномерный перенос гаусса; сдвиг/столкновения пятен не покрыты.

`u = 0`: φ_f = φ_b = φ0 → φ* = φ0 (гейт identity). Limiter всегда включён, слайдера нет.

Dissipation: на forward как сейчас (`exp(−rate·dt)`). На reverse: **сначала** `Dissipation = 1`, потом `DeltaTime = −dt`. Не `exp(−rate·(−dt))`. В пресете `DissipationRate = 0` на обоих Advect.

#### 3. Reverse — флаг на существующем `AdvectScalarPass`

Не клонировать класс. `[SerializeField] bool reverse` (default **false**). `SetParams`: `DeltaTime = reverse ? −dt : dt`; при `reverse` слать `Dissipation = 1`. Fluid2D / Vorticity не ставят флаг — существующие тесты не краснеют.

Кернел `AdvectScalar` не менять по формуле.

#### 4. Два новых кернела в `FieldPasses.compute`

Не новый `.compute`. `PassLibraryPaths` не расширять. Не `FluidPasses.compute` (это проекция / VC).

`#pragma` рядом с `AdvectScalar`. Условие legacy-блока расширить:

```
#if !defined(KERNEL_ADVECTSCALAR) && !defined(KERNEL_COPYSCALAR) && !defined(KERNEL_MACCORMACKCOMBINE)
```

Иначе Copy/Combine скомпилируют тела `AdvectVelocity` без слотов `float2`. Образец изоляции — уже `KERNEL_ADVECTSCALAR`.

**CopyScalar** (`KERNEL_COPYSCALAR`), multi-role: `FieldWriteA` scratch, `FieldReadB` dye. `Load`, не sample.

**MacCormackCombine** (`KERNEL_MACCORMACKCOMBINE`), multi-role:

```
Texture2D<float> FieldReadA;      // φ_b  dye Current
RWTexture2D<float> FieldWriteA;   // φ_f  dye Next → φ*
Texture2D<float> FieldReadB;      // φ0   dyeMacScratch
```

Классы в `FieldPasses.cs` рядом с `AdvectScalarPass`: `CopyScalarPass`, `LimitedMacCormackCombinePass`. Оба `FieldKernelPass`, `Execute` не переопределять. `RequiresSquareTexel => false` (как AdvectScalar). `RepeatCount` не трогать.

```
CopyScalar:
  FieldWrites = (dyeMacScratch, WriteInPlace, Scalar, Role A)
  FieldReads  = (dye,           Read,         Scalar, Role B)

Combine:
  FieldWrites = (dye,           WritePingPong, Scalar, Role A)
  FieldReads  = (dyeMacScratch, Read,          Scalar, Role B)
```

Имена по умолчанию: `dye` / `dyeMacScratch`.

#### 5. Экспериментальный ассет, не production

`Assets/Effects/Fluid2D_MacCormackDye.asset`. Не `Fluid2D_F2b`. **Не** переписывать `Fluid2D_Vorticity.asset` (F2.1b freeze: `radiusUV=0.16`, `ε=1`, `m=2`).

Клон Vorticity (Harris + VC до проекции, одна стена) с заменой хвоста `AdvectScalar` на шаги 1–4:

```
Touch → Seed(dye) → Advect velocity → VorticityConfinement
  → Divergence → ZeroMean → Jacobi×40 → Subtract → SolidWall
  → CopyScalar(dye → dyeMacScratch)
  → AdvectScalar(+dt)
  → AdvectScalar(reverse)
  → LimitedMacCormackCombine
```

Пять полей: velocity / fluidD / fluidPhi / dye / **dyeMacScratch**. `dyeMacScratch`: Scalar, **R16_SFloat** (как dye, не else-ветка R32 в Create), 128², Size 32, XZ, не в debug quads.

VC: `ε_vc=1`, `BorderMargin=2`. Jacobi×40. `DissipationRate=0`.

Create/Assign отдельно, не Demo Effects. Create = `DeleteAsset` **только этого** пути. Не вызывать Create Fluid2D / Harris / Vorticity.

Create factory `radiusUV=0.08`. Живой look — **0.16** (как F2.0/F2.1): после Create `Load`+`SetDirty`, не второй Create. Пресет-тест на диске: `0.16`.

#### 6. Численные гейты (детали в ТЗ)

Геометрия oracles: 64² / Size=64 / R32, как AdvectScalar F1.7 (`dt=1`, h=1).

- `u=0`: цепочка 1–4 bitwise seed (канаррейка UAV-Load).
- constant dye, любой `u`: constant (допуск R32 как F1.7 §3.3).
- integer `u=(1,0)` один forward+один reverse: bitwise **интерьер** (рамка ≥1; `saturate` край).
- integer translation `(1,0)`, 8 шагов: COM как nearest; velocity bitwise; пик не гейт (nearest = amp).
- Gaussian COM на `(1,0)`, 8 шагов: допуск F1.7; пик на этом носителе не гейт.
- **Пик vs bilinear:** носитель **`(1.7, 0)`** (off-grid, Techdebt 5), 8 шагов; `dCOM_x ≈ 13.6`; max интерьера строго выше bilinear — **assert**. Красный → стоп, limiter не крутить.
- dye ≥ 0; Inf/NaN — красный.
- отдельный прогон R16: Inf нет, dye ≥ 0; пик не гейтить (half врёт overshoot).
- `reverse=false` на Fluid2D / Vorticity не краснеет.

D/KE/odd-even F2.1 не гонять как гейт. GPU ms — запись, не порог. Velocity цепочка не трогает: после MacCormack-цепочки `u` bitwise seed.

#### 7. Замер EditMode (2026-09-16)

UAV-Load `u=0` bitwise зелёный. Пик-гейт off-grid `(1.7,0)`, гаусс F1.7, 8 шагов, R32, `dt=h=1`:

| | dCOM_x | max интерьера |
| --- | --- | --- |
| bilinear `AdvectScalar` | 13.594 | 0.74387 |
| limited MacCormack (точечный clamp) | 13.261 | **0.75639** |

`|dCOM_x−13.6|=0.339 < 0.5`. `macPeak > bilinearPeak`. `min=0`. `elapsedMs/N=0.137` `timer=cpu_driver_not_gpu`.

5-точка φ0 в `p` на том же сиде: COM 9.20 / пик 0.45 — отклонена.

### Visual оператора (2026-09-18)

[`play-F2.2-touch.md`](../last/play-F2.2-touch.md): B=`Fluid2D_Vorticity`, C=`Fluid2D_MacCormackDye`, `radiusUV=0.16`, `ε_vc=1`, `BorderMargin=2`, ×3 × 30 с, оба квада. Inf/шахматки/новых колец рамки нет. Тело dye C — клубы того же класса, что B / F2.1b; нить целиком не тоньше. Velocity почти мёртв в обоих рядах (база маски). Look F2 **не взят**. Схема корректна (EditMode зелёный); на Stam-клубах ~1.7% пика не другой силуэт. Dye не меняет топологию velocity — крутить ширину мазка / wait / палитру / `dt` после кадра не стали. Production нет. F2.3: Harris без VC.

### Отклонённые варианты

**`FieldReadC` / один combine на три Texture2D SRV.** ADR-026 / ADR-008.

**Один `SimPass.Execute` на dye+velocity+scratch.** Три имени в guard.

**BFECC** (лишний forward после коррекции). Другая схема.

**MacCormack velocity.** Вне F2; half уже врал overshoot.

**Класть схему в `Fluid2D.asset` / в живой Vorticity.** F2.3.

**Limiter 5-точка φ0 в `p`.** Запрещает адвекцию; замер F2.2 off-grid.

**Limiter по 4 углам backUv в combine.** Нужен velocity (Role C / третье имя) или отдельный min/max-буфер.

**Выключить limiter слайдером.** Нет. Точечный clamp к `{φ0,φ_f,φ_b}` + `φ*≥0`.

### Последствия

- (+) Смаз tracer лечится без Role C и без правки солвера скорости.
- (+) Vorticity остаётся эталоном bilinear dye для visual F2.2.
- (−) +2 диспатча Advect + copy + combine на кадр; scratch = ещё одно R16 128².
- (−) Look F2 не взят: VC интерьер не оживил; MacCormack на Stam-клубах мало виден (~1.7% пика).
