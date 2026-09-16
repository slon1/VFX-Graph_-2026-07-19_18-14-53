## ADR-027: VorticityConfinementPass (F2.1)

**Статус:** Реализовано. F2.1 **закрыт**. F2.1b **закрыт** (EditMode + visual 2026-09-16): торнадо рамки сняты маской; look интерьера не взят.
**Дата:** 2026-09-06
**Контекст:** M3D Framework, F2.1 — look-тикет мелкомасштабной структуры
**Скоуп фазы:** [ADR-026](ADR-026-F2-Small-Scale-Structure.md) § F2.1
**ТЗ:** [`todo-F2.1.md`](../last/todo-F2.1.md) · **Тач:** [`play-F2.1-touch.md`](../last/play-F2.1-touch.md)
**Не меняет:** `Assets/Effects/Fluid2D.asset`, `Fluid2D_HarrisOrder.asset`, кернелы F1, Role C

### Контекст

F2.0 зафиксировал production: bilinear съедает dye за десятки секунд; на читаемом freeze (больший диск в Play) плюмы толстые, клубы, не хребет Harris. Цель пасса — Fedkiw vorticity confinement: вернуть мелкие завитки в `velocity` **до** проекции, чтобы dye жил тоньше и дольше. Это не «починить Stam» и не отмена [ADR-024 §7](ADR-024-Harris-Order-Experiment.md).

VC рождает новую `D`. Если поставить его в хвост production (`Project → Advect → VC`), дивергенция висит до следующего кадра. Второй Poisson вне F2. Поэтому экспериментальный пресет — Harris-подобный порядок + VC **между** Advect velocity и Divergence.

### Решение

#### 1. Один кернел, одно поле, без scratch

`VorticityConfinementPass : FieldKernelPass`. ω и сила в одном dispatch. Именованное scratch-поле и Role C — нет (это F2.2-история, сюда не тащить).

`RequiresSquareTexel => true`. `RepeatCount` не переопределять. Default field `velocity`, не `flockVel`. `dt` есть: `u ← u + f·Δt`.

#### 2. Стенсиль как Divergence, без нового `/2h`

Семейство [ADR-017](ADR-017-Divergence-Pass-And-Square-Texel-Contract.md): `Load` + clamp индекса. Divergence пишет `D = uE.x − uW.x + uN.y − uS.y` **без** `/2h`. Curl на том же шаблоне:

```
ω(p) = (uE.y − uW.y) − (uN.x − uS.x)
```

`|ω|` в соседях — тем же `ω(·)` на `p±e_x`, `p±e_y` (нужны соседи ±2 от центра; clamp как у Divergence). Градиент `|ω|`:

```
∇|ω| = (|ω|_E − |ω|_W, |ω|_N − |ω|_S)   // тоже без /2h
N = ∇|ω| / (|∇|ω|| + ε_grad)
```

`ε_grad` — константа кернела `1e-5`, не слайдер. Ноль в знаменателе → NaN → `0·NaN` ломает гейт `ε_vc=0`.

2D сила Fedkiw (`N × ωẑ`):

```
h = FieldSize.x / FieldResolution.x
f = ε_vc · h · ω · (N.y, −N.x)
u_out = u_in + f · DeltaTime
```

`h` считать в кернеле из `FieldSize`/`FieldResolution`, не слать с CPU. `ε_vc < 0` запрещён (`[Min(0)]`).

Это не ε из бумаги Fedkiw: ω здесь в «счётах» Divergence, не ω_world. Калибровка — ТЗ / Play, не «0.3 из статьи».

#### 3. Гейт `ε_vc = 0` — early-out, не надежда на IEEE

```
if (EpsilonVc == 0) { FieldWrite[p] = u; return; }
```

Без early-out `0 * NaN` даёт NaN. С early-out цепочка с пассом при `ε_vc=0` побитово совпадает с той же цепочкой без пасса (гейт DoD).

Равномерное `u`: ω=0 → f=0 при любом `ε_vc` (интерьер).

#### 4. Роли: single-role WritePingPong, legacy-слоты

Читаем соседей `u`, пишем новое `u` → `WritePingPong`. Одно поле → `{A}` без B → слоты **`FieldRead` / `FieldWrite`**, не `FieldReadA`/`FieldWriteA` ([ADR-008](ADR-008-Multi-Field-Per-Kernel-Binding.md), как `AdvectVelocityFieldPass`). Subtract копировать нельзя.

#### 5. Кернел в `FluidPasses.compute`, `#ifdef KERNEL_VORTICITY`

World-семейство. Новый `.compute` не заводить. `PassLibraryPaths` не расширять.

```hlsl
#pragma kernel VorticityConfinement KERNEL_VORTICITY

#ifdef KERNEL_VORTICITY
Texture2D<float2> FieldRead;
RWTexture2D<float2> FieldWrite;

float EpsilonVc;
float DeltaTime;
int BorderMargin;

float2 LoadClampedVelocity(int2 q)
{
    int2 maxP = FieldResolution - 1;
    q = clamp(q, int2(0, 0), maxP);
    return FieldRead.Load(int3(q, 0));
}

float VorticityAt(int2 q)
{
    float2 uE = LoadClampedVelocity(q + int2( 1, 0));
    float2 uW = LoadClampedVelocity(q + int2(-1, 0));
    float2 uN = LoadClampedVelocity(q + int2( 0, 1));
    float2 uS = LoadClampedVelocity(q + int2( 0,-1));
    return (uE.y - uW.y) - (uN.x - uS.x);
}

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void VorticityConfinement(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y)
        return;

    int2 p = int2(id.xy);
    float2 u = LoadClampedVelocity(p);
    if (EpsilonVc == 0)
    {
        FieldWrite[p] = u;
        return;
    }

    int m = BorderMargin;
    if (p.x < m || p.y < m || p.x >= FieldResolution.x - m || p.y >= FieldResolution.y - m)
    {
        FieldWrite[p] = u;
        return;
    }

    float omega = VorticityAt(p);
    float absE = abs(VorticityAt(p + int2( 1, 0)));
    float absW = abs(VorticityAt(p + int2(-1, 0)));
    float absN = abs(VorticityAt(p + int2( 0, 1)));
    float absS = abs(VorticityAt(p + int2( 0,-1)));
    float2 grad = float2(absE - absW, absN - absS);
    float2 N = grad / (length(grad) + 1e-5);
    float h = FieldSize.x / FieldResolution.x;
    float2 f = EpsilonVc * h * omega * float2(N.y, -N.x);
    FieldWrite[p] = u + f * DeltaTime;
}
#endif
```

`DeltaTime` / `EpsilonVc` / `BorderMargin` объявить **внутри** токена (как слоты). Глобальный `DeltaTime` из адвекции в `FieldPasses.compute` сюда не копировать — другой файл.

#### 6. Экспериментальный ассет, не production

`Assets/Effects/Fluid2D_Vorticity.asset`. Порядок (ADR-026, здесь фиксируется состав):

```
Touch → Seed(dye) → Advect velocity → VorticityConfinement
  → Divergence → ZeroMean → Jacobi×40 → Subtract → SolidWall → Advect dye
```

Одна стена после проекции (как Harris), не две как Fluid2D. Поля/форматы/128²/Size 32/GroundXZ/quads — как Fluid2D. Create: `SeedScalarDisk.radiusUV = 0.08`, `ε_vc=1`, `BorderMargin=2`. Живой диск: `radiusUV=0.16`, `m=2`. Меню Create/Assign отдельно, не Demo Effects. Create = `DeleteAsset` этого пути — **не вызывать** (сотрёт look). **Не** вызывать Create Fluid2D / Create HarrisOrder. `DissipationRate=0`. Jacobi×40.

`Fluid2DPresetTests` не расширять под этот ассет — отдельный composition-тест.

#### 7. Численные утверждения (детали в ТЗ)

| Гейт | Что |
| --- | --- |
| Identity | `ε_vc=0`: velocity после VC побитово = GPU-readback после Seed (R32 и R16), не CPU vs half. Цепочка: interior `max\|D\|` ε=0 vs без пасса — assert, `rel=floor=1e-3` (R16), кадры 1 и 8. Bitwise всей цепочки нет. |
| Uniform | `u=const`, `ε_vc≠0`: интерьер bitwise сид. |
| D | R16, TG как F2.0 (`h=A=0.25`, `λ=16`): кадр 1 afterChain interior `max\|D\|` при `ε=1` ≤ **2×** none. Кадр 8 ≤ **10×** none (знаменатель затухает без VC — 2× на кадре 8 ложный красный). Сравнивать с **Harris-порядком без VC**, не с таблицей production F2.0. Border `max\|D\|` — лог (8g), без порога. |
| NaN/Inf | Нет на identity, uniform, TG 8 кадров, Nyquist. |
| Odd-even | `E(u.x)` Nyquist: ε=1 vs **ε=0 той же** Harris-цепочки в одном прогоне. Таблица F2.0 — сноска другого порядка, не порог. Отдельно dye-шахматка при `u=0`: `E(dye)` остаётся 1. MAC не открывать. |
| KE | Лог; медленнее затухание — цель visual, не «KE больше». Inf — красный. Кадр 8: `KE_ε1 / max(KE_ε0, floor) ≤ 10` (assert в тесте, не параметр ассета). |
| GPU ms | Запись vs F2.0 `0.208`, warmup вне таймера, без порога (Techdebt 8k). |

Visual — [`play-F2.1-touch.md`](../last/play-F2.1-touch.md). Не гейтило NUnit. «Готово» в плане — после обоих.

**EditMode замерено (2026-09-06).** Харнес: `h=A=0.25`, `dt=1`, `λ=16`, 8 периодов, R16, Harris-порядок ± VC, `ε=1`. NaN/Inf нет. Потолок кадра 8: ≤10× none (2× снят: none затухает).

| Замер | Число |
| --- | --- |
| TG interior `max\|D\|` кадр 1 none / ε=0 / ε=1 | 0.104 / 0.104 / 0.102 (**0.98×**) |
| TG interior `max\|D\|` кадр 8 none / ε=0 / ε=1 | 0.0334 / 0.0334 / 0.273 (**8.16×** none; **2.63×** vs none кадр 1) |
| TG border `max\|D\|` кадр 1 / 8 (ε=1) | 0.082 / 0.045 (лог 8g) |
| odd-even `E(u.x)` Nyquist seed / afterChain ε=0 | 0.100 / 0.00886 |
| odd-even `E(u.x)` Nyquist seed / afterChain ε=1 | 0.100 / 0.00895 |
| odd-even `E(dye)` u=0 до / после 8 кадров ε=1 | 1 / 1 |
| KE interior кадр 8 ε=0 / ε=1 | 195 / 692 (**3.54×**, потолок 10×) |
| wall-clock | `elapsedMs/N=0.272` (`timer=cpu_driver_not_gpu`, Editor 128²; F2.0 = 0.208) |

Smoke: `Rebuild()` на `Fluid2D_Vorticity.asset` зелёный. `ε=1` и Jacobi×40 не крутили.

**Visual оператора (2026-09-16).** A=`Fluid2D`, B=`Fluid2D_Vorticity`, `radiusUV=0.16`, `ε_vc=1`, мазок ×3, 30 с, оба квада. Inf/шахматки/взрыва нет. A: velocity к 30 с почти мёртв; dye — клубы/грибы. B: интерьер dye **того же класса**, нить не тоньше; velocity держит постоянные потоки / «торнадо» **вдоль рамки**, dye снизу подкручивается краем. Look-цель F2 (тонкие интерьерные филаменты) **не взята**. Энергия VC села на рамку (8g + одна стена + curl у края), не в середину. `Fluid2D.asset` не менять. F2.3: обязательно Harris **без** VC, иначе порядок и VC смешаны.

F2.1 **закрыт**. Look-цель на **этом** ассете без маски не взята. Диагноз рамки — § F2.1b, не переигрывание A/B.

#### F2.1b — маска силы, 2 текселя (диагностика)

Не новый ADR. Гипотезы не смешивать с 8g: VC в кадре **до** стены; 8g — после. Clamp-`Load` в `VorticityAt` / `∇|ω|` у края — отдельный сигнал (раздутый curl).

Маска **только `f`**, не чтение ω для интерьера. Ширина **2**: сила в текселе `p` берёт `|ω|(p±1)`, тот ω достаёт `u` через clamp. `BorderMargin=0` = кернел F2.1. На экспериментальном ассете look — **2**. Create пишет `m=2` и **`radiusUV=0.08`** (factory); живой диск **0.16** — не вызывать Create. `m` — `[Min(0)]`.

После early-out `ε_vc=0`: если `p` в рамке `m` текселей → записать `u`, не добавлять `f`.

DoD: `m=0` не ломает identity F2.1; при `m=2`, `ε=1` кольцо 2 текселя bitwise сид + интерьер `max|Δu|>0`; Inf нет. Успех маски ≠ production. F2.2 не стопорить. Visual 2026-09-16: торнадо сняты, на 3-й тексель не съехали, `m` не ширить.

ТЗ: [`todo-F2.1b.md`](../last/todo-F2.1b.md).

**EditMode замерено (2026-09-16).** Create не вызывали (живой `radiusUV=0.16`). `BorderMargin=2` дописан на существующий ассет. Гейты: identity `m=0` ε=0 R32 зелёный; `m=2` ε=1 кольцо 2 текселя bitwise сид, интерьер вне кольца `max|Δu| > 0`; Inf нет; 3.3 без правок зелёный; пресет `BorderMargin==2`, `ε=1`, `radiusUV==0.16`. D/KE не гоняли как гейт.

**Visual оператора (2026-09-16).** [`play-F2.1b-touch.md`](../last/play-F2.1b-touch.md): только B, `radiusUV=0.16`, `m=2`, ε=1, ×3 × 30 с. Торнадо рамки **нет**, на 3-й тексель не съехали. Dye — клубы того же класса, что F2.1. Inf/шахматки нет. Гипотеза clamp-curl **подтверждена**; look F2 маска не взяла. `m` не ширить. На экспериментальном ассете `m=2` оставить (иначе снова смотрим артефакт). Production нет. F2.1b **закрыт**.

### Отклонённые варианты

**VC после Advect в текущем Fluid2D без второй проекции.** Контракт `D` до следующего кадра.

**Второй Poisson, чтобы оставить Project→Advect.** Вне F2.

**ω с `/2h` «как в учебнике», отдельно от Divergence.** Два немых масштаба в одном солвере.

**Scratch ω / Role C.** Лишний слот; F2.2 не открывать отсюда.

**ShouldDispatch при `ε_vc=0` вместо early-out.** Гейт identity тогда не тестирует кернел.

**Класть кернел в `FieldPasses.compute`.** texel/UV-файл; VC — world/fluid.

**Писать `ε_vc` в `Fluid2D.asset`.** F2.3.

**Маска чтения ω / ширина 1.** Интерьерный curl должен остаться как F2.1. `m=1` не закрывает стенсиль `∇|ω|` (сила в `x=1` всё ещё видит clamp). F2.1b — только `f=0` при `m=2`.

### Последствия

- (+) Look-пресет не маскируется под «починить порядок»; сцепка с Harris названа.
- (+) Identity гейт не зависит от IEEE.
- (−) Production до F2.3 мороз; ещё один экспериментальный ассет.
- (−) Unnormalized ω: ε из литературы не переносится.

**Вне скоупа:** MacCormack; смена `Fluid2D.asset`; MAC; viscosity; калибровка ε под мобильный FPS.
