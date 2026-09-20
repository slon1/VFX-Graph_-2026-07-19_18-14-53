## ADR-026: F2 — восстановление мелкомасштабной структуры (скоуп фазы)

**Статус:** Принято. F2.0–F2.3 **закрыты**. Production остаётся `Fluid2D.asset` (Project→Advect). Look F2 не взят.
**Дата:** 2026-09-05
**Контекст:** M3D Framework, после закрытия F1 / [ADR-024 §7](ADR-024-Harris-Order-Experiment.md)
**План:** [`plan-stable-fluid.md`](../plan-stable-fluid.md) §3
**ТЗ F2.0:** [`todo-F2.0.md`](../last/todo-F2.0.md) · **Тач F2.0:** [`play-F2.0-touch.md`](../last/play-F2.0-touch.md)
**ТЗ F2.1:** [`todo-F2.1.md`](../last/todo-F2.1.md) · **Тач F2.1:** [`play-F2.1-touch.md`](../last/play-F2.1-touch.md) · **пасс:** [ADR-027](ADR-027-Vorticity-Confinement-Pass.md)
**ТЗ F2.1b:** [`todo-F2.1b.md`](../last/todo-F2.1b.md) · **Тач F2.1b:** [`play-F2.1b-touch.md`](../last/play-F2.1b-touch.md)
**ТЗ F2.2:** [`todo-F2.2.md`](../last/todo-F2.2.md) · **Тач F2.2:** [`play-F2.2-touch.md`](../last/play-F2.2-touch.md) · **пасс:** [ADR-028](ADR-028-Limited-MacCormack-Dye.md)
**ТЗ F2.3:** [`todo-F2.3.md`](../last/todo-F2.3.md) · **Тач F2.3:** [`play-F2.3-touch.md`](../last/play-F2.3-touch.md)
**Не меняет в этом ADR:** кернелы F1. `Fluid2D.asset` по итогам F2.3 **не** сменяли (отдельный ADR смены не открывали).

### Контекст

F1 закрыл Stam-minimum: Touch → проекция → Advect → dye, desktop, одно разрешение. Известная плата — численная диссипация bilinear semi-Lagrangian ([Techdebt 5](../last/Techdebt.md): −39% пика гаусса за 8 шагов). ADR-024: Harris чуть чище по интерьерному `D` и визуально держит тонкий хребет, порог ≥2× не взят, production остался Project→Advect.

Цель F2 — не «сделать солвер физически точнее» и не поднять Jacobi. Цель: **более тонкие, непрерывные и живые dye-филаменты** за счёт (1) восстановления вихревой энергии и (2) меньшей диссипации tracer, без роста NaN/Inf, без неконтролируемого `D`, без взрыва GPU-кадра.

«Метастабильность» из онбординга = этот визуальный режим, не отсутствие decay.

Урок ADR-024, который F2 применяет явно: порог ≥2× по `max|D|` не решает за художника; visual «хребет vs клубы» был решающим сигналом, хотя в том ADR раздел B был помечен необязательным. F2.3 не повторяет эту случайность. VC (F2.1) добавляет ещё один оператор на пространственных производных и может резонировать с уже известной дырой Jacobi ([ADR-016 §4](ADR-016-Units-By-Pass-Family.md) / errata ADR-020) — поэтому odd-even diagnostic обязателен в F2.0, до калибровки `ε`, а не «когда увидим шахматку».

### Решение

#### Что входит

| # | Тикет | Суть |
| --- | --- | --- |
| F2.0 | Baseline + R16 + smoke + odd-even | Зафиксировать production-профиль **до** новых кернелов. Coverage [Techdebt 8i](../last/Techdebt.md) и [8j](../last/Techdebt.md). Протокол тача как F1.8b (не гейт EditMode). GPU ms — запись без порога. Odd-even diagnostic **обязателен** (velocity и dye). ТЗ: [`todo-F2.0.md`](../last/todo-F2.0.md). |
| F2.1 | Vorticity confinement | Первый look-тикет. Пасс + `Assets/Effects/Fluid2D_Vorticity.asset`. Не `Fluid2D.asset`. Visual без маски: энергия на рамке. [ADR-027](ADR-027-Vorticity-Confinement-Pass.md). |
| F2.1b | Маска силы VC, 2 текселя | Диагностика: торнадо рамки = clamp-curl; `f=0` на 2 текселях их сняла; look интерьера нет. [`play-F2.1b-touch.md`](../last/play-F2.1b-touch.md). |
| F2.2 | Limited MacCormack для **dye** | Меньше смаза tracer. Scratch `dyeMacScratch`, не Role C. Ассет: `Fluid2D_MacCormackDye.asset`. Кернел/слоты: [ADR-028](ADR-028-Limited-MacCormack-Dye.md). Visual: клубы как Vorticity, look не взят. |
| F2.3 | Production decision | Сравнить режимы. Visual A/B/C/D, жест скриптованный. Look не взят; `Fluid2D.asset` оставлен. ≥2× не гейт. |

Реализация F2.2 (пасс) не ждёт «успеха» визуала F2.1 — другой механизм. В экспериментальный пресет limited dye кладётся **после** VC (сначала живое поле, потом острый tracer).

#### Что не входит

- Explicit viscosity / `DiffuseVelocity` как ν — усилит смаз, против цели.
- MAC / Rhie–Chow — нет триггера (odd-even интерьера на dye, F1.7). Diagnostic шахматки — в F2.0, не ветка солвера. Рост odd-even после F2.1 vs **ε=0 той же Harris-цепочки** = «VC»; таблица F2.0 — другой порядок, сноска. Сам факт ненулевой шахматки в F2.0 MAC не открывает.
- F0.5 / F0.6 cross-res dye — вне F2; 128² одно разрешение.
- F0.7 `dt` clamp — Techdebt 1b, не fluid-F2.
- MacCormack/BFECC **velocity** — вне F2. Триггер открыть: после F2.1+F2.2 диссипация **поля скорости** всё ещё главный визуальный потолок. Любая velocity-коррекция только с проекцией после неё.
- `FieldSlotRole.C` / третий bind-слот ради MacCormack ([ADR-008](ADR-008-Multi-Field-Per-Kernel-Binding.md) потолок `{A}` / `{A,B}`).
- Второй Poisson «чтобы оставить Project→Advect и воткнуть VC после Advect».
- Подъём Jacobi iterations; смена Bias; `ClampFieldPass`; цветной dye (Techdebt 10a); мобильная адаптация.
- Жёсткий GPU-порог мс в F2 (нет целевого устройства/FPS). Писать число; потолок — [Techdebt 8k](../last/Techdebt.md).
- Трогать `Fluid2D.asset` / `Fluid2D_HarrisOrder.asset` в F2.0–F2.2. В F2.3 у Harris **только** `radiusUV→0.16` (без Create, без VC, без смены порядка). Production `Fluid2D.asset` по visual F2.3 не меняли.

Конвенция имён экспериментальных ассетов: **техника, не номер фазы** (`Fluid2D_HarrisOrder`, `Fluid2D_Vorticity`, при необходимости `Fluid2D_MacCormackDye`). Не `Fluid2D_F2`.

#### F2.0 — baseline (без новых кернелов)

Production freeze ([ADR-022](ADR-022-Fluid2D-Preset.md)):

```
Touch → Seed(dye) → Divergence → ZeroMean → Jacobi×40 → Subtract
  → SolidWall → Advect velocity → SolidWall → Advect dye
```

128², Size 32, GroundXZ, `velocity` R16G16, `fluidD`/`fluidPhi` R32, dye R16, Jacobi×40, DissipationRate=0, MaxFieldSpeed=20, velocity quad `colorScale=0.125`.

Измерять (не выдумывать CV-метрику толщины нити):

**Visual:** [`play-F2.0-touch.md`](../last/play-F2.0-touch.md) (жест как F1.8b, только production Fluid2D, 30 с). Не гейтит EditMode; закрытие F2.0 — после зелёного GPU **и** отчёта оператора.
Харнес 8i (не Play FPS): `dt = 1`, `A = h = 0.25` → texel-CFL ≈ 1 (как F1.8b). TG: **8 периодов на 128²** (`λ = 16` текселей), не копировать `λ=8` с сетки 64² (это было бы k=16 — дыра Jacobi). Шапка рапорта: `h`, `A`, `dt`, `lambdaTexels`, `periods`, `texelCFL`.
- NaN/Inf; smoke `SimulationWorld` Build (8j);
- GPU: время кадра 128² desktop — **запись** в рапорт и Techdebt 8k, без NUnit-порога;
- **обязательно:** odd-even diagnostic на **velocity и dye** (сид Nyquist). Таблица энергии интерьера до/после цепочки = baseline для F2.1. Красный только NaN/Inf. MAC не открывать.

Автоматическая «толщина филамента» в DoD F2.0 **не** входит.

**EditMode замерено (2026-09-06).** Тесты: `Fluid2DProductionProfileTests`, `Fluid2DWorldSmokeTests`. Харнес: `h=0.25`, `A=0.25`, `dt=1`, `lambdaTexels=16`, 8 периодов, `κ=π/2` (world). NaN/Inf нет.

| Замер | Число |
| --- | --- |
| TG `maxAbsInterior` afterChain кадр 1 / 8 | 0.168 / 0.078 |
| TG `maxAbsBorder` кадр 1 / 8 | 0.159 / 0.084 |
| TG seed interior | 1.37×10⁻⁴ (half) |
| odd-even `E(dye)` u=0 до / после 8 кадров | 1 / 1 |
| odd-even `E(u.x)` Nyquist A=0.1 seed / afterProjection / afterChain | 0.100 / 0.0998 / 0.00891 |
| odd-even `E(D)` на тех же точках | 0 / 0 / ~4×10⁻⁸ |
| wall-clock | `elapsedMs/N=0.208` (`timer=cpu_driver_not_gpu`, Editor 128²) |

Smoke 8j: `Rebuild()` на `Fluid2D.asset` зелёный.

**Visual оператора (2026-09-06).** Только `Fluid2D.asset`, вертикальный мазок ×3, 30 с. Inf/NaN нет. Шахматка интерьера **нет** — MAC не открывать. Create-дефолт `radiusUV=0.08`: к ~30 с dye почти гаснет (bilinear / Techdebt 5). Читаемый freeze: больший диск в инспекторе; это **изменило ассет** (SO, на диске 0.16). F2.1/F2.3 — тот же жест и тот же `radiusUV` на обоих `.asset`, не «сессия без Save».

F2.0 **закрыт**.

#### F2.1 — vorticity confinement

2D collocated, world, `RequiresSquareTexel`. Скалярный ω = ∂v/∂x − ∂u/∂y на том же семействе стенсилей, что Divergence (не новый `/2h` втихую). Сила Fedkiw: `N = ∇|ω| / (|∇|ω||+ε)`, `f = ε_vc · h · (N_⊥) · ω`, вклад `u ← u + f·dt`. `ε_vc = 0` → bitwise как цепочка без пасса (гейт).

**Порядок** ассета `Fluid2D_Vorticity.asset` (Harris-подобный + VC **до** проекции):

```
Touch → Seed(dye) → Advect velocity → VorticityConfinement
  → Divergence → ZeroMean → Jacobi×40 → Subtract → SolidWall → Advect dye
```

Почему не «VC в хвост текущего Fluid2D» (`Project → Advect → VC`): confinement рождает новую `D`, которая до следующего кадра не проецируется — неконтролируемое нарушение контракта. Второй Poisson ради сохранения production-порядка — вне F2. F2.1 **сцеплен** с Harris-подобным порядком на экспериментальном ассете; это новый аргумент, не отмена ADR-024 §7.

Не переписывать `Fluid2D_HarrisOrder.asset`. Меню Create/Assign отдельно от Demo Effects.

Кернел, слоты, early-out `ε_vc=0`, пресет — [ADR-027](ADR-027-Vorticity-Confinement-Pass.md). ТЗ: [`todo-F2.1.md`](../last/todo-F2.1.md). Visual: [`play-F2.1-touch.md`](../last/play-F2.1-touch.md) (A=Fluid2D, B=Vorticity, тот же увеличенный диск, что F2.0 readable freeze).

**EditMode готово (2026-09-06).** Таблица D / odd-even / ms — [ADR-027 §7](ADR-027-Vorticity-Confinement-Pass.md). Кадр 8 потолок 10× (2× ложный: none затухает).

**Visual оператора (2026-09-16).** [`play-F2.1-touch.md`](../last/play-F2.1-touch.md): A vs B, `radiusUV=0.16`, `ε=1`. Inf/шахматки нет. Интерьер dye B не тоньше и не «живее серединой» — клубы как у A. Velocity B: постоянные потоки / торнадо **у рамки** (не успех VC). Production не менять. F2.3 сравнит Harris без VC.

F2.1 **закрыт**. Look-DoD «тонкие интерьерные филаменты» **на ассете без маски не выполнен**. F2.1b **закрыт**: торнадо = clamp-curl, маска их сняла; интерьер не стал живее.

#### F2.2 — limited MacCormack, только dye

Имя метода: **limited MacCormack**. Binding (не переоткрывать): три значения без `FieldSlotRole.C`. Forward/backward — существующий `AdvectScalarPass` (`reverse` на backward). Copy φ0 в `dyeMacScratch` + combine/limiter — отдельные пассы. Один combine с `FieldReadC` — отклонён.

Цепочка, слоты, UAV-Load φ_f, ассет — [ADR-028](ADR-028-Limited-MacCormack-Dye.md). ТЗ: [`todo-F2.2.md`](../last/todo-F2.2.md). Visual: [`play-F2.2-touch.md`](../last/play-F2.2-touch.md) (B=Vorticity bilinear dye, C=MacCormack; Fluid2D не переснимать).

Не переписывать `Fluid2D_Vorticity.asset`. Create/Assign отдельно. Production — F2.3.

**Visual оператора (2026-09-18).** B vs C, `radiusUV=0.16`, ε=1, m=2, ×3 × 30 с. Inf/шахматки/новых колец нет. Dye C — клубы как B; нить не тоньше. Look F2 **не взят**. Триггер velocity-MacCormack («после F2.1+F2.2 потолок всё ещё скорость») **наблюдается**, но схема velocity остаётся вне F2.

F2.2 **закрыт**.

#### F2.3 — решение production

Закрытие **невозможно** без живого visual A/B/C/D. Числа (`D`, NaN, GPU, R16) рядом, не вместо глаз: брать **уже закрытые** таблицы F1.8b / F2.0 / F2.1, не новый гейт ≥2× и не перегон D/KE как DoD. Шаблон оператора — часть DoD.

**Жест:** скриптованный `ScriptedTouchStroke`, не мышь. Вертикаль UV `(0.5, 0.25)→(0.5, 0.75)`, Duration **0.5 с инжекта с первого Sample**. Протокол писал wait 30 с; парные кадры `_4` сняты на **10 с** (пересъёмка 30 с не гейт: силуэт к 10 с уже гриб). World из UV — формула `TouchInjectVelocity`. `Radius`/`Strength` пишет InputRouter. ТЗ: [`todo-F2.3.md`](../last/todo-F2.3.md). Тач: [`play-F2.3-touch.md`](../last/play-F2.3-touch.md).

Нового ADR на смену production **нет** — visual не велел.

Режимы на одном R16-профиле, все `radiusUV=0.16`:

1. **A** production Project→Advect (`Fluid2D`);
2. **B** Harris без VC (`Fluid2D_HarrisOrder`) — патч `radiusUV=0.16` без Create;
3. **C** F2.1b (`Fluid2D_Vorticity`, маска m=2);
4. **D** F2.1b + limited dye (`Fluid2D_MacCormackDye`).

Пары: A vs B = порядок; B vs C = VC на Harris; C vs D = dye.

**Visual оператора (2026-09-20).** [`play-F2.3-touch.md`](../last/play-F2.3-touch.md): скрипт вертикаль, `_4` wait **10 с**, оба квада. Inf/шахматки/новых колец нет. A/B/C/D — макро-клубы (гриб/сердце); хребет F1.8b не воспроизвёлся (другой жест). C vs B: частичный эффект VC — жилковатый интерьер на dye-only, не филамент; на `_4` силуэт как B. D vs C: MacCormack силуэт не сменил. Look F2 **не взят**. `Fluid2D.asset` оставить. Мелкий сид не гейт ([Techdebt 8n](../last/Techdebt.md)).

F2.3 **закрыт**. Фаза F2 закрыта без смены production.

### Отклонённые варианты

**F2 = точнее Poisson / больше Jacobi / MAC.** Не лечит Techdebt 5 и не даёт живых завитков.

**VC на текущем Project→Advect без второй проекции.** См. F2.1.

**MacCormack сразу на velocity.** Расходится, создаёт `D`, half уже врал overshoot (Techdebt 5). Только после F2 и по триггеру.

**Role C / bindless ради трёх скаляров в одном кернеле F2.2.** См. binding F2.2.

**F0.5 вместо F2.2.** Острее картинка через разрешение, не через схему; сознательно вне этой фазы (128²).

**Автоматическая метрика толщины нити в F2.0.** Протокол оператора; иначе тикет раздувается в CV.

**Жёсткий GPU-порог +N мс в F2.0.** Нет целевого устройства. Тот же класс ошибки, что ≥2× в ADR-024.

**Visual F2.3 как optional / best-effort.** Повторяет структурную случайность ADR-024 §3.

### Последствия

- (+) F2 имеет DoD по тикетам и явный out-of-scope до кода.
- (+) VC не маскируется под «починить порядок»; сцепка с Harris названа.
- (+) Odd-even baseline до `ε`; MacCormack не ломает ADR-008.
- (−) Production остаётся Project→Advect: F2.3 не взял look (клубы на жирном сиде).
- (−) Velocity MacCormack и мелкий `radiusUV` не закрывают «тонкую нить» внутри этой фазы: после F2.3 потолок — поле скорости **и** авторский сид 0.16 ([Techdebt 8n](../last/Techdebt.md)).

**Вне скоупа документа:** текст кернелов F2.1 (это [ADR-027](ADR-027-Vorticity-Confinement-Pass.md)); подбор `ε_vc` в Play (оператор, не этот файл).
