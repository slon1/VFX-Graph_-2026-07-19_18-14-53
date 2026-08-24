## ADR-020: SubtractPhiGradientPass

**Статус:** Принято (реализовано)
**Дата:** 2026-08-24 (errata §3: 2026-08-25)
**Контекст:** M3D Framework, фаза F1 (F1.3) — третий кернел fluid-контура, замыкание проекции Stam
**Реализует:** [ADR-016](ADR-016-Units-By-Pass-Family.md) §2 (формула Subtract); роли — по указанию [ADR-018](ADR-018-Jacobi-Phi-Pass.md); квадратный тексель — [ADR-017](ADR-017-Divergence-Pass-And-Square-Texel-Contract.md); харнес — [ADR-014](ADR-014-GPU-Numeric-Test-Harness.md)
**ТЗ пасса:** [`todo-F1.3.md`](../last/todo-F1.3.md) (3.1–3.5 закрыты; шаг 3.6 там заменён)
**ТЗ DoD цепочки:** [`todo-F1.3-chain-dod.md`](../last/todo-F1.3-chain-dod.md)
**Исчерпанный follow-up k:** [`todo-F1.3-harmonic-k.md`](../last/todo-F1.3-harmonic-k.md)

### Контекст

Проекция Stam — три кернела без параметров (ADR-016 §2):

```
Divergence:  D  = uE.x − uW.x + uN.y − uS.y
Jacobi:      ΦC ← (ΦE + ΦW + ΦN + ΦS − D) / 4
Subtract:    u  = u* − ( (ΦE − ΦW) / 4,  (ΦN − ΦS) / 4 )
```

F1.1 и F1.2 закрыты. Без Subtract контур считает `D` и `Φ`, но не корректирует `u*` — утверждение «поле после проекции менее дивергентно» ещё нигде не проверялось. Цепочка `Divergence → Jacobi×N → Subtract → Divergence` — центральный численный факт всего Stam-алгоритма; откладывать её до пресета F1.6 нельзя: там поверх появятся EffectAsset, touch и VFX, и локализация знака градиента / ролей / clamp будет на порядок дороже.

**Имя.** Класс — `SubtractPhiGradientPass`, кернел — `SubtractPhiGradient`. Не `SubtractPressureGradientPass`: ADR-016 §2.2 запрещает называть `Φ` давлением; то же правило уже применено к `JacobiPhiPass`.

**Роли — не копировать Divergence.** ADR-018 явно предупредил: Subtract ближе к Jacobi, чем к Divergence. `velocity` читается и переписывается, но это не self-ping-pong (коррекция идёт от другого поля) → `WriteInPlace` Role A. `fluidPhi` — чистый `Read` Role B. Форма Divergence (Read A / WriteInPlace B на двух разных полях) здесь другая комбинация и даст неверный биндинг.

`WriteInPlace` биндит только `WriteId` на `Current`, не `ReadId` (`FieldKernelPass.Execute`). Значит кернел читает `u*` из `RWTexture2D<float2> FieldWriteA[p]`, не из `FieldReadA`. Прецедент: `TouchInjectVelocityField`. Объявлять `FieldReadA` в этом кернеле нельзя: слот не биндится, плюс конфликт типа с Jacobi (`float` vs `float2`) — как раз то, из-за чего F1.2 ввёл `#ifdef KERNEL_*`.

**Четвёртое сочетание ролей fluid-семейства:**

| Пасс | Role A | Role B |
| --- | --- | --- |
| Divergence | Read velocity | WriteInPlace fluidD |
| GrayScott | WritePingPong U | WritePingPong V |
| JacobiPhi | WritePingPong fluidPhi | Read fluidD |
| SubtractPhiGradient | WriteInPlace velocity | Read fluidPhi |

### Решение

#### 1. Роли и слоты

```
FieldWrites = [ (velocity, WriteInPlace, Velocity, 2, Role A) ]
FieldReads  = [ (fluidPhi, Read,         Scalar,   1, Role B) ]
```

`velocity` — Role A, потому что `PrimaryFieldName` при multi-role ищет Role A среди **Write**, и dispatch идёт по записываемому полю. `RequiresSquareTexel => true` — в формуле нет `h` по той же причине, что у Divergence/Jacobi: `/4` уже содержит допущение `hx = hy` (ADR-016 §2.1). `RepeatCount` не переопределяется (остаётся 1). `dt` кернелом не используется.

Имена по умолчанию: `velocity` (не `flockVel`), `fluidPhi`. Production-формат `velocity` **не меняется** (`R16G16_SFloat`). Тестовый оракул цепочки — `R32G32_SFloat` (см. §4).

`ValidateMatchingFieldGeometry` срабатывает бесплатно (обе роли). `SquareTexelValidator` — раньше, на Build. Обе проверки остаются.

#### 2. Кернел в `FluidPasses.compute`, `#ifdef KERNEL_SUBTRACT`

Новый файл не заводится (ADR-017: world-семейство в одном compute). Токен после `#pragma kernel`, как у Divergence/Jacobi.

Конфликт, который токен изолирует: `FieldWriteA` у Jacobi — `RWTexture2D<float>`, у Subtract — `RWTexture2D<float2>`. Без `#ifdef` — ошибка компиляции HLSL на каждый kernel-entry.

```hlsl
#pragma kernel SubtractPhiGradient KERNEL_SUBTRACT

#ifdef KERNEL_SUBTRACT
RWTexture2D<float2> FieldWriteA; // velocity Current, read + write
Texture2D<float> FieldReadB;     // fluidPhi

float LoadClampedPhi(int2 q)
{
    int2 maxP = FieldResolution - 1;
    q = clamp(q, int2(0, 0), maxP);
    return FieldReadB.Load(int3(q, 0));
}

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void SubtractPhiGradient(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y)
        return;

    int2 p = int2(id.xy);
    float n = LoadClampedPhi(p + int2( 0, 1));
    float s = LoadClampedPhi(p + int2( 0,-1));
    float e = LoadClampedPhi(p + int2( 1, 0));
    float w = LoadClampedPhi(p + int2(-1, 0));
    float2 u = FieldWriteA[p];
    FieldWriteA[p] = u - float2((e - w) * 0.25, (n - s) * 0.25);
}
#endif
```

Соседи `Φ` — `Load` + clamp индекса, та же Neumann-подобная граница, что у Divergence и Jacobi. Без этого OOB `Load` даёт 0 и вносит ложный градиент по рамке, который проекция разгоняет. Истинные непроницаемые BC — F1.4, не этот тикет.

`FieldReadA` в блоке **нет**. `dt` / `h` / `Size` в кернеле **нет**.

#### 3. Два независимых численных утверждения

**(а) Изолированный CPU-оракул.** Проверяет только арифметику `u ← u* − ∇Φ` при заданном `Φ`. Не говорит, уменьшает ли проекция дивергенцию.

- Константный `Φ` → `u` не меняется (в том числе на рамке: clamp соседа = сам тексель, разность 0).
- Линейный `Φ` в индексах (`Φ[i,j] = 4i`) → на интерьере `(ΦE−ΦW)/4 = 2`, CPU-оракул с той же clamp-логикой на всей сетке, включая рамку.
- `fluidPhi` после прогона побитово равен сиду (Role B read-only).

**(б) Цепочка проекции.** Проверяет, что Stam-проекция с дефолтным Jacobi уменьшает реальную дивергенцию. Не интеграционный тест пресета: три уже существующих пасса + новый, один `FieldTestHarness`, без EffectAsset / VFX / touch.

Геометрия — **та же, что F1.1**: `Size = 32`, `res = 64`, та же плоскость, тот же `PlanePosition` (тексель-центр → плоскостные координаты относительно Origin). Формулу `D = 4h` и сид `u = (x, y)` **не переиспользовать**: линейное расширение даёт `D = 4h` почти всюду, `ΣD ≠ 0`, задача Пуассона при чистом Neumann несовместна. Такой тест мерил бы патологию источника, не Subtract.

Сид цепочки — семейство A (синусоида, zero-mean, та же геометрия), **гармоника k = 8**:

```
L = Size.x   // = Size.y, квадратный тексель
u = ( sin(2π k x / L), sin(2π k y / L) ),  k = 8
```

`x, y` — компоненты `PlanePosition` из `DivergenceFieldPassTests` (диапазон ≈ `[-L/2, L/2]`). Integer periods на домен: `ΣD` близка к нулю (рамка clamp портит ~1 тексель, не ломает совместность).

Почему не буквальный `k = 1`. `sin(2πx/L)` — самая медленная мода Jacobi на 64²: за 40 итераций редукция ошибки единицы процентов. Тест мерил бы спектр решателя, не Subtract. Это всё ещё семейство A (синусоида, integer periods, zero-mean, геометрия F1.1), не новый вид источника.

**Errata 1 (спектр 2D-Jacobi, осевые моды).** Ошибка в исходной оценке: для мод (k,0)/(0,k) (осевых, не диагональных (k,k)) собственное число 2D-Jacobi — `λ = (1+cos(kπ/N))/2`, не `cos(kπ/N)`. Источник `u=(sin(2πkx/L), sin(2πky/L))` раскладывается именно на осевые моды. Для k=8 это даёт `λ≈0.962`, `λ^40≈0.21` (~4.7×) — измерено **4.46×**, подтверждает исправленную формулу.

**Errata 2 (`λ^40` ≠ отношение `max|D|`; k не вытягивает 10×).** Follow-up поднял k → 12, оставив порог 10× и Jacobi×40. Гейт совместности зелёный (`|mean|/max≈0.019`). `max|D|` 3.625 → 1.453, отношение **2.49×** — хуже, чем на k=8. `λ^40` на k=12 ≈ 0.030 (~33×) описывает затухание ошибки Jacobi по **Φ**, не `max|D|` после `Divergence ∘ Subtract`.

Два режима на одной схеме:

| k | узкое место | теория `λ^40` (Φ) | замер `max|D|` |
| --- | --- | --- | --- |
| 8 | Jacobi (осевые) | ~4.7× | **4.46×** |
| 12 | несогласованность `div_{2h}∘grad_{2h}` (ADR-016 §4) | ~33× | **2.49×** |

Выше k — не лучше D: компактный 5-точечный лапласиан Jacobi не есть композиция Divergence/Subtract (шаг `2h`). k второй раз не крутить. Порог «на порядок» (`/10`) на collocated + clamp + 40 Jacobi **недостижим выбором k** — снят. Калиброванный DoD: **k = 8**, `max|D'|_interior < max|D|_interior / 3`. 3× отличает проекцию от «чуть шевельнулось» (~1.2×), не требует невозможных 10×; теория ~4.7× и замер 4.46× оставляют запас под GPU/рамку. k=12 — контрпример в этой errata, не второй тест. `iterations = 40` не трогать. F1.4 / MAC из этого замера не открывать (триггер — видимый odd-even на dye, ADR-016 §4). Калибровка `iterations` под визуал и широкополосный touch — F1.6 (осевые моды как худший случай, [Techdebt 8e](../last/Techdebt.md), [8f](../last/Techdebt.md)).

Порядок прогона, один харнес, три поля (`velocity` R32G32_SFloat, `fluidD` R32_SFloat, `fluidPhi` R32_SFloat, Φ стартует с clear = 0):

1. Seed velocity.
2. `Divergence` → снять `D`.
3. **Гейт совместности (до Jacobi):** `|mean(D)| / max|D|_interior < 0.1`. `mean` — по **всем** текселям (дискретное условие разрешимости Neumann). `max|D|` — по интерьеру, без рамки в 1 тексель (как оракулы F1.1/F1.2). Если гейт падает — сломан сид или `PlanePosition`, чинить источник, не Subtract.
4. `JacobiPhiPass.Iterations = 40` (дефолт, `RepeatCount`).
5. `SubtractPhiGradient`.
6. `Divergence` повторно → `D'`.
7. Утверждение: `max|D'|_interior < max|D|_interior / 3`. Не «до нуля», не «на порядок», не «просто меньше». Коллокейтед сетка оставляет шахматную моду (ADR-016 §4, в ADR-019 постфактум как known limitation).

#### 4. Формат velocity в тесте vs production

Цепочка (б) меряет малый остаток после вычитания двух близких величин (`u* − ∇Φ`). Half (10 бит мантиссы) на этом сценарии уже дал качественно неверный результат: `HarnessAdvectTests` / ADR-013 / F0.4 — overshoot 0.26 при физическом потолке 0.200. Тот же паттерн: оракул на `R32G32_SFloat`, боевой `velocity` остаётся `R16G16_SFloat`. Этот тикет **не** меняет production-формат ни у одного поля.

Прецеденты: Advect (half соврал качественно); `fluidD`/`fluidPhi` — боевой уже R32, вопрос не вставал. F1.3 — первый случай, где в измерении участвует боевое half-поле.

#### 5. Что остаётся в других тикетах

- F1.2b (zero-mean `D`) — не смешивать. Сид (б) подобран совместно; реальный touch — нет. Блокер F1.6, не F1.3.
- F1.4 — явная непроницаемая граница после Subtract. Здесь граница = тот же clamp, что у Divergence/Jacobi.
- F1.6 — EffectAsset, touch, калибровка iterations, visual. Не начинать без F1.2b.
- ADR-019 — итоговый Fluid2D после F1.7; номер не занимаем.

### Отклонённые варианты

**Имя `SubtractPressureGradientPass`.** Ломает запрет ADR-016 §2.2, который уже принудил `JacobiPhiPass`.

**Форма ролей Divergence (Read A velocity / Write B phi или наоборот).** Velocity нужно и читать, и писать в ту же ячейку без ping-pong. Read+WriteInPlace на одном имени через два слота (FieldReadA + FieldWriteA на Current) — UAV+SRV на один ресурс, запрещено. WritePingPong на velocity — лишний swap и ложная самоссылка.

**Второй `.compute` файл.** Ломает ADR-017 (одно место world-семейства). `#ifdef KERNEL_SUBTRACT` — установленный паттерн F1.2.

**Сид `u = (x, y)` из F1.1 для цепочки (б).** `D = 4h` почти константа, `ΣD ≠ 0`, Neumann-Пуассон несовместен. Оракул F1.1 остаётся верным **только** для формулы Divergence.

**Буквальный k = 1 в семействе A.** Zero-mean и совместно, но 40 итераций Jacobi почти не гасят фундаментальную моду. Тест перестал бы проверять Subtract.

**k = 12 как рабочее значение 3.6.** Follow-up исчерпан: отношение упало (2.49×), теория Φ и метрика D разъехались. k не рычаг к 10×.

**Оставить порог `/10`.** Недостижим на этой схеме при 40 Jacobi ни на k=8, ни на k=12.

**`maxAfter < maxBefore` без множителя.** Пропустит регресс «чуть шевельнулось» (~1.2×). 3× — нижняя планка, не «любое уменьшение».

**Второй тест на k=12.** Контрпример живёт в errata §3; отдельный assert либо красный навсегда, либо слабее 3.6 без новой информации.

**Поднимать k ещё раз / крутить `iterations` в тесте.** Запрещено. Следующий рычаг — F1.6 (калибровка) или MAC/Rhie–Chow по триггеру dye.

**Пин угла / zero-mean D в этом тикете.** F1.2b.

**Менять production `velocity` на R32G32.** Отдельное решение, не через тестовый харнес.

**Подключать пасс к demo-пресетам.** F1.6.

### Последствия

- (+) Замыкается дискретный оператор проекции ADR-016 §2; появляется машинная проверка «проекция уменьшает `max|D|` сильнее, чем шевеление», ради которой писался харнес ADR-014.
- (+) Четвёртая комбинация ролей (WriteInPlace A + Read B) проверяет биндинг WriteInPlace без ReadId на Role A.
- (+) Имя и формула согласованы с запретом называть `Φ` давлением.
- (−) `FluidPasses.compute` — третий `#ifdef`-блок; следующий кернел с конфликтующим типом обязан копировать форму.
- (−) DoD цепочки калиброван по осевой моде k=8 / 40 Jacobi (~4.5×), не «на порядок». Высокие k упираются в пол collocated `div∘grad` (errata 2). Не упрощать сид к k=1.

### DoD

1. `SubtractPhiGradientPass` (`PassCategory.Transport`) в `FluidPasses.cs`; кернел `SubtractPhiGradient` в `FluidPasses.compute` за `#ifdef KERNEL_SUBTRACT`. Путь compute уже в `M3DDemoTools.PassLibraryPaths` с F1.1 — не дублировать. В чеклисте `pass-catalog.md` (таблица Pass Library) строки `FluidPasses.compute` до сих пор нет — **добавить** (долг F1.1/F1.2).
2. Роли как §1. `RequiresSquareTexel => true`. Публичные `VelocityField` / `PhiField`. Кернел читает `u*` из `FieldWriteA`, не `FieldReadA`.
3. Тесты `[Category("GPU")]` в `SubtractPhiGradientPassTests.cs`: (а) константный Φ; линейный Φ vs CPU clamp-оракул; bitwise `fluidPhi`; (б) цепочка на сиде k = 8, гейт `|mean(D)|/max|D| < 0.1`, затем `max|D'|_interior < max|D|_interior / 3`. Velocity в (а) и (б) — `R32G32_SFloat`. `SquareTexelValidator` + `Initialize` на расхождении геометрии — по образцу Jacobi 4.3/4.4.
4. Production-формат `velocity` не изменён. Пасс не добавлен ни в один `EffectAsset`.
5. `pass-catalog.md` — раздел Subtract; `status.md`, `capabilities.md`, `plan-stable-fluid.md` — F1.3 закрыт. Forward-ссылка в ADR-018 («SubtractPressureGradientPass») поправлена на фактическое имя.
