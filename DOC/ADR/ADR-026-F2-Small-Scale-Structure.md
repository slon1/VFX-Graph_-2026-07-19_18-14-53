## ADR-026: F2 — восстановление мелкомасштабной структуры (скоуп фазы)

**Статус:** Принято. F2.0 **закрыт**. F2.1 **закрыт** (EditMode + visual 2026-09-16: look интерьера не взят). F2.2+ не начаты.
**Дата:** 2026-09-05
**Контекст:** M3D Framework, после закрытия F1 / [ADR-024 §7](ADR-024-Harris-Order-Experiment.md)
**План:** [`plan-stable-fluid.md`](../plan-stable-fluid.md) §3
**ТЗ F2.0:** [`todo-F2.0.md`](../last/todo-F2.0.md) · **Тач F2.0:** [`play-F2.0-touch.md`](../last/play-F2.0-touch.md)
**ТЗ F2.1:** [`todo-F2.1.md`](../last/todo-F2.1.md) · **Тач F2.1:** [`play-F2.1-touch.md`](../last/play-F2.1-touch.md) · **пасс:** [ADR-027](ADR-027-Vorticity-Confinement-Pass.md)
**Не меняет в этом ADR:** `Assets/Effects/Fluid2D.asset`, кернелы F1. Смена production — только тикет F2.3.

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
| F2.1 | Vorticity confinement | Первый look-тикет. Пасс + `Assets/Effects/Fluid2D_Vorticity.asset`. Не `Fluid2D.asset`, не переписывать `Fluid2D_HarrisOrder.asset`. Кернел/слоты: [ADR-027](ADR-027-Vorticity-Confinement-Pass.md). |
| F2.2 | Limited MacCormack для **dye** | Меньше смаза tracer. Имя метода одно. Velocity-correction **вне F2**. Архитектура буфера — § F2.2 ниже (scratch, не Role C). При отдельном ассете: `Fluid2D_MacCormackDye.asset`. |
| F2.3 | Production decision | Сравнить режимы. Visual A/B/C/(D) — **основной** критерий закрытия, наравне с числами, не довесок. **Можно** сменить `Fluid2D.asset` отдельным ADR. Порог ≥2× из ADR-024 **не** гейт. |

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
- Трогать `Fluid2D.asset` / `Fluid2D_HarrisOrder.asset` в F2.0–F2.2. HarrisOrder остаётся эталоном F1.8b.

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

F2.1 **закрыт**. Look-DoD «тонкие интерьерные филаменты» **не выполнен**; пасс и пресет остаются экспериментом.

#### F2.2 — limited MacCormack, только dye

Имя метода в коде и ТЗ: **limited MacCormack** (семья BFECC, одну схему). Forward + backward, `φ* = φ_f + 0.5(φ0 − φ_b)`, затем clamp к локальному min/max соседей `φ0`. Без limiter — overshoot, отрицательный dye, ореолы у saturate-рамки.

**Binding (зафиксировано здесь, ADR F2.2 реализует, не переоткрывает):** три значения (оригинал, forward, backward) **не** требуют `FieldSlotRole.C`. Потолок [ADR-008](ADR-008-Multi-Field-Per-Kernel-Binding.md) `{A}` / `{A,B}` не трогать.

- Forward и backward — повтор существующего `AdvectScalarPass` (dye WritePingPong A, velocity Read B).
- Коррекция + limiter — отдельный кернел (или два маленьких диспатча) плюс **именованное scratch-поле** на `EffectAsset` (`dyeMacScratch` или аналог): обычный `FieldDescriptor`, не третий role-слот в guard-матрице.
- Один combine-dispatch, который одновременно читает три скаляра через новые `FieldReadC` — **отклонён**.

Тесты: constant остаётся constant; integer translation; Gaussian на носителе теряет амплитуду медленнее bilinear-baseline (Techdebt 5); dye ≥ 0; COM на `(1,0)` в допуске F1.7; отдельный прогон R16; рамка не даёт новых ярких колец.

При отдельном экспериментальном ассете — `Fluid2D_MacCormackDye.asset`, не `Fluid2D_F2b`.

#### F2.3 — смена production

Закрытие тикета **невозможно** без живого visual A/B/C (и D, если F2.2 готов). Числа (`D`, NaN, GPU, R16) обязательны рядом, не вместо глаз. Протокол как [`play-F1.8b-touch.md`](../last/play-F1.8b-touch.md): один жест, 30 с (при необходимости 30–120 с на распад), по очереди, один World. Шаблон отчёта оператора — часть DoD, не «заодно».

Режимы на одном R16-профиле:

1. production Project→Advect (`Fluid2D`);
2. Harris без VC (`Fluid2D_HarrisOrder`);
3. F2.1 (`Fluid2D_Vorticity`);
4. F2.1 + limited dye, если F2.2 готов.

Критерии (все): непрерывность/толщина нити глазом; жизнь мелких завитков; клубы vs хребет; интерьер/рамка `D`; odd-even vs F2.0; NaN/Inf; GPU ms (запись); стабильность R16. ≥2× по `max|D|` **не** блокер. Если победитель ≠ production — отдельный ADR, правка `Fluid2D.asset`, `Fluid2DPresetTests`, pass-catalog, ADR-022/019 одной фразой-ссылкой.

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
- (−) Production до F2.3 мороз; экспериментальных ассетов станет больше.
- (−) Velocity MacCormack и cross-res dye не закрывают «тонкую нить», если после F2.2 её всё ещё мало.

**Вне скоупа документа:** текст кернелов F2.1 (это [ADR-027](ADR-027-Vorticity-Confinement-Pass.md)); подбор `ε_vc` в Play (оператор, не этот файл).
