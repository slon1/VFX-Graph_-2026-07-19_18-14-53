## ТЗ для программиста — F1.3 (SubtractPhiGradientPass)

**Шаг 3.6 / «на порядок» заменён.** Пасс и 3.1–3.5 закрыты этим ТЗ. Актуальный DoD цепочки — [ADR-020 §3](../ADR/ADR-020-Subtract-Phi-Gradient-Pass.md), патч теста — [`todo-F1.3-chain-dod.md`](todo-F1.3-chain-dod.md). Не исполнять assert `/10` и не крутить k из этого файла.

Роль этого документа: реализация пасса по принятому ADR. Не пересматривать имена и роли. Исторический допуск «на порядок» в шаге 3.6 оставлен как был на момент первой реализации.

Прочитать целиком [ADR-020](../ADR/ADR-020-Subtract-Phi-Gradient-Pass.md) и ADR-016 §2 (три формулы). ADR-018 § про роли Subtract — предупреждение «не копировать Divergence»; здесь оно исполняется.

**Класс — `SubtractPhiGradientPass`, кернел — `SubtractPhiGradient`.** Не Pressure, не JacobiPressure, не SubtractGradient без Phi.

Референсы по месту (читать до кода):

- `Assets/Scripts/Passes/FluidPasses.cs`, `Assets/Shaders/GPU/Passes/FluidPasses.compute` — расширить оба, новые файлы пассов/compute не создавать.
- `FieldKernelPass.Execute` (`SimPass.cs`): `WriteInPlace` биндит только `WriteId` → `Current`. Кернел **обязан** читать `u*` как `FieldWriteA[p]`. `FieldReadA` в блоке Subtract не объявлять.
- `#ifdef KERNEL_*` уже есть для Divergence/Jacobi. Третий блок — та же форма. Конфликт: Jacobi `RWTexture2D<float> FieldWriteA` vs Subtract `RWTexture2D<float2> FieldWriteA`.
- `JacobiPhiPass` — образец Role A write + Role B read-only. Отличие: там `WritePingPong` на скаляре, здесь `WriteInPlace` на velocity.
- `DivergenceFieldPassTests.PlanePosition` — **скопировать** в новый тест для сида цепочки. Не изобретать UV→world.
- `JacobiPhiPassTests` — образец `SquareTexelValidator` / `Initialize` mismatch / bitwise read-only Role B.
- `FieldTestHarness.RunPass` уже гоняет несколько пассов подряд на одном наборе полей (как Diffuse/Advect). Цепочка (б) — четыре `RunPass` в одном `using`, не новый харнес.

`FluidPasses.compute` уже в `M3DDemoTools.PassLibraryPaths`. Путь не добавлять второй раз.

Код писать. Документацию из шага 4 — тоже. Demo-пресеты, F1.2b, F1.4, смена production-формата `velocity` — вне скоупа.

---

### Шаг 1 — `SubtractPhiGradientPass` в `FluidPasses.cs`

```csharp
public sealed class SubtractPhiGradientPass : FieldKernelPass
{
    [SerializeField] private string velocityField = "velocity";
    [SerializeField] private string phiField = "fluidPhi";

    public string VelocityField { get => velocityField; set => velocityField = value; }
    public string PhiField { get => phiField; set => phiField = value; }

    public override string DisplayName => "Subtract Phi Gradient";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "SubtractPhiGradient";
    public override bool RequiresSquareTexel => true;
    // RepeatCount не переопределять — остаётся 1.

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, phiField,
            FieldAccess.Read, FieldSemantic.Scalar, 1, FieldSlotRole.B);

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, velocityField,
            FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2, FieldSlotRole.A);
}
```

Кэши `[NonSerialized]` — как у Divergence/Jacobi.

**Роли строго так.** `velocity` WriteInPlace Role A (dispatch-домен). `fluidPhi` Read Role B. Не наоборот и не Read+WriteInPlace на одном имени через два слота (SRV+UAV на Current запрещён).

---

### Шаг 2 — кернел в `FluidPasses.compute`

1. Добавить `#pragma kernel SubtractPhiGradient KERNEL_SUBTRACT` рядом с двумя существующими.
2. Обновить шапку файла: токен изолирует ещё и тип `FieldWriteA` (`float` Jacobi vs `float2` Subtract).
3. Существующие `#ifdef KERNEL_DIVERGENCE` / `KERNEL_JACOBI` не трогать по смыслу.
4. Новый блок — **дословно** ADR-020 §2 (LoadClampedPhi, `u - float2((e-w)*0.25, (n-s)*0.25)`, чтение `FieldWriteA[p]`).

`#include` и `#define FIELD_THREADS` остаются снаружи блоков. Хелпер LoadClampedPhi живёт внутри `KERNEL_SUBTRACT` (иначе снова конфликт типов на глобалах).

`dt` в кернел не пушить. `h` / `Size` / `Resolution` в арифметике не использовать.

---

### Шаг 3 — тесты `Assets/Tests/Editor/SubtractPhiGradientPassTests.cs`

`[TestFixture]`, `[Category("GPU")]`. Compute path: `Assets/Shaders/GPU/Passes/FluidPasses.compute`.

Константы геометрии цепочки — как F1.1: `Resolution = 64`, `SizeWorld = 32`.

Формат velocity во **всех** численных тестах этого файла: `GraphicsFormat.R32G32_SFloat`. `fluidD` / `fluidPhi`: `R32_SFloat`. Production-дескрипторы не менять.

Скопировать `PlanePosition` из `DivergenceFieldPassTests` один в один.

#### 3.1 — константный Φ: velocity не меняется

Залить `velocity` ненулевым полем (например `(1.25, -0.4)` всюду), `fluidPhi` константой (например `3`). Прогнать Subtract. Каждая компонента `velocity` совпадает с сидом в допуске `RelativeTolerance(R32G32_SFloat)`. Ловит перепутанный знак, лишний `h`, чтение нуля вместо Φ.

#### 3.2 — линейный Φ: CPU clamp-оракул

`velocity` = `(1, 0)` всюду. `fluidPhi[i,j] = 4 * i` (колонка, float). Ожидание интерьера: `(ΦE−ΦW)/4 = 2` → `u' = (-1, 0)`. На **всей** сетке, включая рамку, сравнить с CPU-оракулом той же clamp-логики, что кернел (`LoadClamped` по образцу `JacobiPhiPassTests.LoadClamped`). Допуск — `AssertApproximately` харнеса для `R32G32_SFloat`. Ловит `FieldReadA` вместо `FieldWriteA`, перепутанные оси, `/2` вместо `/4`.

#### 3.3 — Role B не пишется

После 3.1 или отдельным прогоном: `fluidPhi` побитово равен сиду (`SingleToInt32Bits`), как Jacobi 4.2 для `fluidD`.

#### 3.4 — `Initialize` mismatch геометрии

`velocity` 32² и `fluidPhi` 64², оба с квадратным текселем → прямой `pass.Initialize(context)` (не `Build`) бросает `InvalidOperationException` с `"matching Resolution and plane"`. Сообщение **не** содержит `"ADR-016 §2.1"`. Образец: Jacobi 4.3.

#### 3.5 — `SquareTexelValidator`

Оба поля 32², `Size = (10, 20)` → прямой `SquareTexelValidator.Validate` падает с `"ADR-016 §2.1"`, именем пасса, `hx=` / `hy=`. Образец: Jacobi 4.4.

#### 3.6 — цепочка проекции (главный DoD)

Один харнес, три поля: `velocity` R32G32_SFloat, `fluidD` R32_SFloat, `fluidPhi` R32_SFloat. `Size = (32, 32)`, `res = 64`. Φ не сидировать (clear 0).

Сид velocity, `k = 8` (не 1 — ADR-020 §3):

```
L = SizeWorld
plane = PlanePosition(velocityDesc, x, y)
u = (sin(2π k plane.x / L), sin(2π k plane.y / L))
```

Прогон:

1. `DivergenceFieldPass.Initialize` + `RunPass`.
2. Снять `D`. Посчитать `mean(D)` по **всем** текселям и `max|D|` по интерьеру (`1..res-2`).
3. Assert `max|D| > 0`. Гейт: `Mathf.Abs(mean) / maxAbs < 0.1f`. Если падает — сломан сид/`PlanePosition`, не идти дальше.
4. `JacobiPhiPass { Iterations = 40 }`, проверить `RepeatCount == 40`, `Initialize`, `RunPass` (двухаргументный, чтобы участвовал `RepeatCount`).
5. `SubtractPhiGradientPass.Initialize` + `RunPass`.
6. Повторно тот же (или новый) `DivergenceFieldPass` + `RunPass`. Снять `D'`, `max|D'|_interior`.
7. Assert `maxAfter < maxBefore / 10f`.

В `TestContext.WriteLine` / `Debug.Log` обязательно вывести: `meanD`, `maxBefore`, `maxAfter`, `ratio`, `meanAbs/maxBefore`. Если `ratio < 10` — **не** менять k/N/допуск; написать числа в отчёте как находку и оставить assert как в ADR (тест красный, это сигнал архитектуре, не повод править ТЗ на месте).

Не подключать F1.2b. Не требовать `max|D'| ≈ 0`. Не сидировать `u = (x, y)`.

---

### Шаг 4 — документация (часть тикета, не «потом»)

- `DOC/pass-catalog.md`:
  - В таблицу Pass Library добавить строку `FluidPasses.compute` | Divergence, Jacobi, **Subtract Phi Gradient** (долг F1.1/F1.2: файла в чеклисте нет, хотя путь в `PassLibraryPaths` уже есть).
  - Новый раздел **Subtract Phi Gradient** по форме Divergence/Jacobi: роли WriteInPlace A / Read B, формула, dt нет, `RequiresSquareTexel`, граница clamp, «не в demo, пресет — F1.6», ссылка на F1.2b как блокер пресета (не этого пасса).
- `DOC/status.md`: секция F1.3 по образцу F1.2; в «Вне скоупа» убрать `SubtractPressureGradient`, оставить F1.2b / F1.4 / F1.6.
- `DOC/capabilities.md`: список field-пассов + строка про проекцию (Subtract есть, пресета ещё нет).
- `DOC/plan-stable-fluid.md`: F1.3 → **Готово**, имя `SubtractPhiGradientPass`, ссылка на ADR-020.
- `DOC/ADR/ADR-018-Jacobi-Phi-Pass.md`: в абзаце про будущий Subtract заменить `SubtractPressureGradientPass` на `SubtractPhiGradientPass` (ADR-020).
- `DOC/ADR/ADR-014-GPU-Numeric-Test-Harness.md`: строка `ADR-020 | F1.3 | SubtractPhiGradientPass` в таблице очереди **уже стоит** (вписана вместе с ADR-020). Проверить, что она на месте и текст совпадает; не добавлять вторую и не переписывать. ADR-019 не трогать — он F1.7.
- `DOC/getting-started.md`: если там ещё написано «pressure projection нет» — одна фраза, что кернелы проекции есть, пресета Fluid2D нет (F1.6).

---

### Отчёт

1. Diff production: `FluidPasses.cs`, `FluidPasses.compute` (только добавление блока Subtract и pragma). Ничего в `SimPass.cs` / валидаторах / `PassLibraryPaths` / EffectAsset / формате velocity.
2. 3.1–3.6: зелёные или, для 3.6, точные `maxBefore` / `maxAfter` / `ratio`, если assert на порядок не сошёлся.
3. Гейт 3.6: фактическое `|mean|/max`.
4. Тексты исключений 3.4 и 3.5.
5. Подтверждение: `fluidPhi` bitwise; production `R16G16_SFloat` у velocity в пресетах не изменён.
6. Сьют целиком — ноль новых красных. Существующие Jacobi/Divergence не регрессируют (третий `#ifdef` не сломал их компиляцию).
7. Док-список шага 4 закрыт.

### Вне скоупа

- F1.2b, F1.4, F1.6 / EffectAsset / touch / VFX.
- Смена боевого формата velocity.
- MacCormack, vorticity, явная вязкость.
- Калибровка `iterations = 40` под картинку.
- Менять k с 8 на 1 «как в чате» — спектральная причина в ADR-020 §3, это не опечатка.
