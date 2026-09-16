## ТЗ для программиста — F2.1 (VorticityConfinement)

**Закрыто 2026-09-16.** EditMode + visual оператора. Вердикт: look интерьера не взят; энергия VC на рамке. [ADR-027](../ADR/ADR-027-Vorticity-Confinement-Pass.md). Это ТЗ не переоткрывать.

Роль: первый look-тикет F2. Пасс + `Fluid2D_Vorticity.asset`. По [ADR-027](../ADR/ADR-027-Vorticity-Confinement-Pass.md) и [ADR-026 § F2.1](../ADR/ADR-026-F2-Small-Scale-Structure.md).

Прочитать **оба** ADR **до кода**. Без этого легко воткнуть VC в хвост Fluid2D, делить curl на `2h`, взять слоты Subtract или открыть MAC из шахматки Nyquist.

Зафиксировано — не пересматривать:

1. **`Fluid2D.asset` / `Fluid2D_HarrisOrder.asset` не трогать.** Не запускать Create Fluid2D / Create HarrisOrder. HarrisOrder на диске может отличаться от Create (`radiusUV`) — не перезаписывать.
2. **Кернелы F1 и GPU-тесты F1.1–F1.8b / F2.0 не менять.** `Fluid2DProductionProfileTests`, `Fluid2DWorldSmokeTests`, `Fluid2DPresetTests` — без правок состава. Константы CFL дублировать, не рефакторить F2.0 в shared.
3. **Порядок только Harris-подобный + VC до проекции** (ADR-027 §6). Не Project→Advect→VC. Не второй Poisson. Одна стена после Subtract.
4. **Стенсиль как Divergence, без `/2h`.** Формула ADR-027 §2/§5 дословно.
5. **`ε_vc=0` — early-out в кернеле**, не `ShouldDispatch`, не «0*x».
6. **Слоты `FieldRead`/`FieldWrite`**, WritePingPong, single-role. Не `FieldWriteA`.
7. **Кернел в `FluidPasses.compute`.** Новый `.compute` нет. `PassLibraryPaths` не расширять и не делать public.
8. **Role C / scratch ω нет.**
9. **GPU ms — запись, не порог.** Нет `Assert.Less(ms, N)`.
10. **Odd-even не открывает MAC.** Колонка сравнения — **та же** Harris-цепочка `ε=0` vs `ε=1` в одном прогоне. Таблица F2.0 (`E(u.x) 0.100 → 0.00891`) — сноска другого порядка, не порог. Красный только NaN/Inf.
11. **Claims по `D` — R16**, vs Harris-цепочка **без** VC, не vs таблица production F2.0 `0.168`. Потолок afterChain interior при `ε=1`: кадр 1 **≤ 2×** none; кадр 8 **≤ 10×** none (тот же порядок, что KE). Гейт `ε=0` vs без пасса — **assert** (вариант A), не лог. Не крутить ε/Jacobi, если кадр 8 > 2× — это затухающий знаменатель, не баг кернела.
12. **EditMode зелёный ≠ F2.1 закрыт.** Visual — [`play-F2.1-touch.md`](play-F2.1-touch.md). «Готово» в plan — после обоих.
13. **`radiusUV` для visual — значение на ассете.** `EffectAsset` пишется с инспектора сразу (SO, не сцена). Нет «не Save». Create ставит 0.08; look F2.0 оставил Fluid2D на **0.16**. A/B: одно число на оба ассета. Runtime-копия Assign — не этот тикет.

Референсы: `SolidWallVelocityPass` + `#ifdef` в `FluidPasses.compute`; `AdvectVelocityFieldPass` (WritePingPong + legacy-слоты); `Fluid2DProductionProfileTests` (CFL `h=A=0.25`, `λ=16`, `fluidD_diag`); `M3DDemoTools.CreateFluid2DHarrisOrderExperiment` (клон полей/меню, **вставить** VC после Advect velocity); `HarrisOrderExperimentTests` (два харнеса / два порядка — здесь: с VC vs без, не A vs B production).

Имена: класс `VorticityConfinementPass`, кернел `VorticityConfinement`, токен `KERNEL_VORTICITY`, ассет `Fluid2D_Vorticity.asset`. Не `Fluid2D_F2`.

Код писать. Документацию из шага 6 — тоже.

---

### Шаг 1 — пасс

`FluidPasses.cs`, рядом с `SolidWallVelocityPass`:

```csharp
public sealed class VorticityConfinementPass : FieldKernelPass
{
    [SerializeField] private string velocityField = "velocity";
    [SerializeField, Min(0f)] private float epsilonVc = 1f;

    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string VelocityField { get => velocityField; set => velocityField = value; }
    public float EpsilonVc { get => epsilonVc; set => epsilonVc = value; }

    public override string DisplayName => "Vorticity Confinement";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "VorticityConfinement";
    public override bool RequiresSquareTexel => true;

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, velocityField,
            FieldAccess.WritePingPong, FieldSemantic.Velocity, 2);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
        SetFloat(context, SimShaderIds.EpsilonVc, epsilonVc);
    }
}
```

`SimShaderIds.EpsilonVc = Shader.PropertyToID("EpsilonVc")`. `RepeatCount` не трогать. Без `DeltaTime` в `SetParams` сила не масштабируется — Play «почти identity» при зелёном `dt=1` харнесе.

---

### Шаг 2 — кернел

`FluidPasses.compute`: `#pragma kernel VorticityConfinement KERNEL_VORTICITY` в шапке. Тело **дословно** ADR-027 §5. Существующие `#ifdef` не переписывать.

`include` / `FIELD_THREADS` снаружи, как сейчас. Слоты и `EpsilonVc`/`DeltaTime` — внутри `KERNEL_VORTICITY`.

---

### Шаг 3 — тесты пасса `VorticityConfinementPassTests.cs`

`[TestFixture]` **без** `[Category("GPU")]` на классе. GPU — на численных методах (как Jacobi / SolidWall). Compute: только `FluidPasses.compute`.

**3.0 контракт:** без GPU на фикстуре. Свойства пасса (`RequiresSquareTexel`, `KernelName`, WritePingPong Velocity) — без харнеса, без `[Category("GPU")]`. `SquareTexelValidator` на неквадратном дескрипторе — через `FieldTestHarness`, поэтому **этот метод** с `[Category("GPU")]` (как SolidWall `Validator_NonSquareTexel_ThrowsWithAdr016`).

Геометрия численных: для identity/uniform можно `64²`, `Size=64`, `dt=1` (`h=1`), `velocity` `R32G32_SFloat`. Для D/odd-even/ms/KE — **профиль F2.0**: `128²`, `Size=32`, `dt=1`, `A=h=0.25`, `λ=16`, `velocity` **R16G16**, `fluidD`/`fluidPhi` R32, `dye` R16.

**3.1 Identity `ε_vc=0`:** сид шум/TG. Сравнивать GPU-readback **после** `SeedVelocity` с readback после VC (`SingleToInt32Bits`). Не CPU-массив vs R16-текстура — квантование half сломает bits. Повторить на R16.

**3.2 Uniform:** `u=(1.25,−0.4)`, `ε_vc=1`. Интерьер bitwise GPU-readback после Seed (тот же принцип, что 3.1). Рамку не ассертить (clamp-curl).

**3.3 Сила ненулевая:** TG как F2.0, один `RunPass` VC `ε_vc=1` vs `ε_vc=0`. Интерьер `max|Δu| > 0` при ε=1; при ε=0 — 0. Нет NaN/Inf.

**3.4 Цепочка D (R16):** два харнеса, один сид TG F2.0.

Без VC (Harris-порядок, как `CreateFluid2DHarrisOrder`, без ассета):

```
Advect velocity → Div → ZeroMean → Jacobi×40 → Subtract → Wall → Advect dye
```

С VC:

```
Advect velocity → VC(ε) → Div → ZeroMean → Jacobi×40 → Subtract → Wall → Advect dye
```

`fluidD_diag` как F2.0. 8 кадров. Шапка рапорта: `h,A,dt,lambdaTexels,periods,texelCFL,epsilonVc`. В лог ещё border `max|D|` (Techdebt 8g), без порога.

**Гейт `ε=0` (вариант A, assert):** кадры 1 и 8, interior `max|D|`.  
`|D_ε0 − D_без| ≤ max(rel · max(|D_ε0|, |D_без|), floor)`, где `rel = floor = FieldTestHarness.RelativeTolerance(R16G16) = 1e-3`. Bitwise всей цепочки не требовать. Без этого числа identity «пасс выключен» не проверяется.

**Потолок `ε=1`:** кадр 1 afterChain interior ≤ **2×** none того же кадра (проекция глотает один шаг VC). Кадр 8 ≤ **10×** none того же кадра — тот же класс, что KE 10×. 2× на кадре 8 **снят**: none к 8-му кадру затухает (bilinear), VC держит поле живым, отношение → ∞ даже при здоровом кернеле. В лог дополнительно `D_ε1_frame8 / D_none_frame1` (абсолютный масштаб, не гейт). NaN/Inf нет.

Замер EditMode 2026-09-06 (ε=1, профиль F2.0): кадр 1 **0.98×**; кадр 8 **8.16×** vs none (0.273 / 0.0334), vs none кадр 1 ≈ 2.6×. KE 3.54×. ε и Jacobi не крутить.

**3.5 Odd-even velocity:** сид Nyquist `E(u.x)=0.1` как F2.0. **Две** цепочки Harris-порядка в одном тесте: `ε=0` и `ε=1`, 8 кадров. Лог `E(u.x)` / `E(D)` seed / afterChain **обеих**. Рост vs `ε=0` = сигнал VC. Число F2.0 `0.100 → 0.00891` — одной строкой сноски («production Project→Advect, другой порядок, не порог»). `E(dye)` здесь не логировать: при Nyquist-velocity dye=0, энергия ~0. `Assert.Pass` с текстом, без порога шахматки. MAC не открывать.

**3.5b Odd-even dye `u=0`:** как F2.0 `dye_u0`: шахматка dye, velocity ноль, цепочка с VC `ε=1`, 8 кадров. `E(dye)` до/после = 1 (VC при `u=0` не должен портить tracer). NaN/Inf нет.

**3.6 ms:** один warmup-кадр **вне** таймера (как F2.0). Затем `Stopwatch` вокруг N≥8 кадров `Execute` без `Read*` в timed loop. Цепочка с VC, 128², R16. Рапорт: `elapsedMs/N`, `timer=cpu_driver_not_gpu`, рядом `F2.0 elapsedMs/N=0.208` — запись, не порог. NUnit — `Assert.Pass`.

**3.7 KE:** сумма `u·u` интерьера, 8 кадров, ε=0 vs ε=1, тот же Harris-порядок. Не ассертить «KE больше» (медленнее затухание — цель visual). Inf — красный. **Потолок взрыва (assert, не слайдер ассета):** на кадре 8 `KE_ε1 / max(KE_ε0, floor) ≤ 10`, `floor` такой, чтобы не делить на 0 (например `1e-12`). Если красный — шаг 5: стоп, числа в чат, не крутить ε.

---

### Шаг 4 — пресет и меню

`M3DDemoTools`: путь `Assets/Effects/Fluid2D_Vorticity.asset`.

- `Tools/M3D/Create Fluid2D Vorticity Experiment` — **клон** `CreateFluid2DHarrisOrderExperiment` (те же поля, `SerializedProperty` форматов, quads `colorScale=0.125`, seed `radiusUV=0.08`) + вставка `VorticityConfinementPass { EpsilonVc = 1 }` сразу после Advect velocity. Create = Delete **этого** пути. Не вызывать Create Fluid2D / Create HarrisOrder.
- `Tools/M3D/Assign Fluid2D Vorticity Experiment To Scene` — Effect + GroundXZ + `EnsurePassLibrary`. `visualEffect` не трогать.

`Fluid2DVorticityPresetTests.cs` (не GPU): 4 поля, 10 пассов, типы по порядку §6, одна стена, seed `dye` / `0.08`, VC `EpsilonVc==1`, форматы как Fluid2D. Сообщение «run Create Fluid2D Vorticity Experiment».

**World smoke обязателен** (не «по желанию»): `Fluid2DVorticityWorldSmokeTests.cs`, тот же приём что 8j (`Rebuild()` на `Fluid2D_Vorticity.asset`, дубль `PassLibraryPaths`, `DestroyImmediate` для collider). Не подменять `Fluid2DWorldSmokeTests`.

После Create ассет на диске достаточен для EditMode. Assign — для visual оператора ([`play-F2.1-touch.md`](play-F2.1-touch.md)). Не оставлять World на Vorticity после тестов, если оператор не просил.

---

### Шаг 5 — калибровка ε в EditMode, не в ассете наугад

Дефолт сериализации **1**. Если 3.4 красный по **новым** потолкам (кадр 1 > 2× или кадр 8 > 10×), гейт `ε=0`, 3.7 KE>10×, Inf, или 3.3 `max|Δu|` на шуме машины — **остановиться**, прислать числа, не молча крутить ε и не поднимать Jacobi. Кадр 8 в диапазоне 2–10× vs none — не стоп.

Play-подбор 0.25/0.5/2/4 — оператор, после зелёного EditMode. В ассет без команды оператора не писать новое ε.

---

### Шаг 6 — доки после зелёного EditMode

- ADR-027: статус «реализовано»; таблица D / odd-even / ms в конец §7 (как ADR-026 F2.0).
- `plan-stable-fluid.md` F2.1: **EditMode готово** / **Готово** только после visual.
- `status.md` — коротко; в список файлов — пасс, ассет, тесты.
- `pass-catalog.md`: строка в `FluidPasses.compute` + секция как Solid Wall; снимок даты.
- `capabilities.md`: имя пасса в списках.
- `getting-started.md`: VC + меню Assign Vorticity (эксперимент, не production).
- Techdebt 8k: новое число рядом с F2.0, без порога.

Visual в ADR-026 § F2.1 — когда оператор сдаст [`play-F2.1-touch.md`](play-F2.1-touch.md): A=Fluid2D vs B=Vorticity, **тот же `radiusUV` на обоих ассетах** (сейчас Fluid2D 0.16, Vorticity после Create 0.08 — выровнять). Harris не назначать. Не ставить F2.1 «Готово» без этого.

---

### Вне скоупа

MacCormack / `dyeMacScratch`; Role C; смена `Fluid2D.asset`; правка HarrisOrder; MAC / Rhie–Chow; viscosity; F0.5; второй Poisson; больше Jacobi; GPU-порог; перевод F1-оракулов на R16; краска тачем; запись увеличенного `radiusUV` в любой ассет.
