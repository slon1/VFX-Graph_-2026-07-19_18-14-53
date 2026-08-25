## ТЗ для программиста — F1.4 (SolidWallVelocityPass)

Роль этого документа: реализация по [ADR-021](../ADR/ADR-021-Solid-Wall-Velocity-Pass.md). Не пересматривать имя, free-slip, слот `FieldWrite`, отказ трогать Пуассон/ZeroMean. Если рамка после пасса имеет ненулевую нормаль — остановить и написать числа, не переходить на no-slip и не вшивать стены в Subtract.

Прочитать ADR-021 целиком. Clamp в Divergence/Jacobi/Subtract — не «почти стены»; этот пасс — отдельное утверждение.

Зафиксировано по вопросам реализации:

1. **Ноль нормали — bitwise.** Обнулённая компонента: `SingleToInt32Bits == 0` (кернел пишет `+0`). Не `== 0f`, не `RelativeTolerance`. Интерьер и касательная — bitwise сид.
2. **Идемпотентность — в этом тикете (3.3), не F1.6.** Второй `RunPass` побитово равен первому.

**Класс — `SolidWallVelocityPass`, кернел — `SolidWallVelocity`.** Не Boundary, не NoSlip, не Pressure.

Референсы по месту:

- `Assets/Scripts/Passes/FluidPasses.cs`, `Assets/Shaders/GPU/Passes/FluidPasses.compute` — расширить оба. Новые файлы не создавать.
- `FieldKernelPass.Execute` (`SimPass.cs`): single-role `{A}` биндит **`FieldWrite`** (не `FieldWriteA`). Subtract — multi-role, его слоты не копировать.
- `TouchInjectVelocity` в `FieldPasses.compute` — образец `RWTexture2D<float2> FieldWrite`.
- `SubtractPhiGradientPass` — образец `#ifdef KERNEL_*` в `FluidPasses.compute`.
- `SubtractPhiGradientPassTests` 3.5 — образец `SquareTexelValidator`.
- `JacobiPhiPassTests` / `ZeroMeanScalarPassTests` / цепочка 3.6 — **не менять**.

`FluidPasses.compute` уже в `PassLibraryPaths`. Путь не дублировать.

Код писать. Документацию из шага 4 — тоже. Demo-пресеты, второй экземпляр после Advect, F1.6 — вне скоупа.

---

### Шаг 1 — `SolidWallVelocityPass` в `FluidPasses.cs`

Наследовать `FieldKernelPass` (один кернел). Не `SimPass` с тремя dispatch.

```csharp
public sealed class SolidWallVelocityPass : FieldKernelPass
{
    [SerializeField] private string velocityField = "velocity";

    [NonSerialized] private FieldRequest[] fieldWritesCache;

    public string VelocityField { get => velocityField; set => velocityField = value; }

    public override string DisplayName => "Solid Wall Velocity";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "SolidWallVelocity";
    public override bool RequiresSquareTexel => true;
    // RepeatCount не переопределять.

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, velocityField,
            FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2, FieldSlotRole.A);
}
```

`FieldReads` — пустой список базы.

**Роли строго так.** Одно поле, WriteInPlace Role A. Не Read+WriteInPlace двумя слотами. Не Role B.

---

### Шаг 2 — кернел в `FluidPasses.compute`

1. `#pragma kernel SolidWallVelocity KERNEL_SOLIDWALL` рядом с остальными.
2. Шапка: токен изолирует `FieldWrite` (`float2`) от объявлений других кернелов.
3. Divergence / Jacobi / Subtract / ZeroMean **не** трогать по смыслу.
4. Блок — **дословно** ADR-021 §4. В HLSL только `RWTexture2D<float2> FieldWrite`. `FieldWriteA` / `FieldRead` / `FieldReadA` не объявлять.

`dt` / `h` / `Size` в арифметике нет.

---

### Шаг 3 — тесты `Assets/Tests/Editor/SolidWallVelocityPassTests.cs`

`[TestFixture]`, `[Category("GPU")]`. Compute: `Assets/Shaders/GPU/Passes/FluidPasses.compute`.

`Resolution = 64`, `SizeWorld = 32`. Velocity: `GraphicsFormat.R32G32_SFloat`. Production-дескрипторы не менять.

Рамка: `x∈{0,N−1}` **или** `y∈{0,N−1}`. Угол — оба условия сразу. Интерьер: `1..N−2`. Ребро «не угол»: четыре угла из проверки касательной **исключить** (на углу обе компоненты 0, касательная ≠ сид).

#### 3.1 — интерьер не меняется, рамка free-slip

Залить `velocity = (1.25, −0.4)` всюду. `pass.Initialize(harness.Context)`, затем двухаргументный `harness.RunPass(pass, dt)` (как F1.3). Не `Execute` в обход `FindKernel`.

Сравнение везде `SingleToInt32Bits`:

- Интерьер: обе компоненты = сид.
- Ребро x=0 и x=N−1, не угол: `u.x` bits `== 0`, `u.y` = сид.
- Ребро y=0 и y=N−1, не угол: `u.y` bits `== 0`, `u.x` = сид.
- Четыре угла: обе компоненты bits `== 0`.

Не `== 0f`, не `RelativeTolerance`. Не снимать `D` после стен (Techdebt 8g) — это не DoD этого теста.

Лог: `N`, сид `(1.25, −0.4)`, сколько текселей интерьера / рёбер / углов проверено.

Ловит `FieldWriteA` вместо `FieldWrite`, перепутанные оси, no-slip (убитый `u.y` на вертикали).

#### 3.2 — `SquareTexelValidator`

Одно поле `velocity` 32², `Size = (10, 20)` — **без** второго дескриптора. Прямой `SquareTexelValidator.Validate` падает с `"ADR-016 §2.1"`, именем пасса, `hx=` / `hy=`. Образец: Subtract 3.5 (там два поля, здесь одно).

#### 3.3 — идемпотентность

Самодостаточный тест, не «после 3.1». Один харнес: сид как 3.1, `Initialize`, `RunPass`, снять поле; второй `RunPass` без повторного `Initialize`. Все тексели побитово равны снимку после первого прогона.

Не подключать F1.6. Не менять 3.6.

---

### Шаг 4 — документация (часть тикета)

- `DOC/pass-catalog.md`: в строку `FluidPasses.compute` добавить Solid Wall Velocity. Раздел **Solid Wall Velocity** после Subtract: WriteInPlace `FieldWrite`, free-slip, dt нет, `RequiresSquareTexel`, «не в demo; в пресете F1.6 — после Subtract и ещё раз после Advect».
- `DOC/status.md`: секция F1.4 «готово» (после зелёных 3.1–3.3).
- `DOC/plan-stable-fluid.md`: F1.4 → **Готово**.
- `DOC/capabilities.md` / `DOC/getting-started.md`: кернел стен в списке Stam (пресета по-прежнему нет).
- ADR-021 шапка: «Принято (реализовано)». ADR-019 не трогать.

---

### Отчёт

1. Diff production: `FluidPasses.cs`, `FluidPasses.compute` (только KERNEL_SOLIDWALL). Нет правок ZeroMean / Jacobi / Subtract / 3.6. Нет `PassLibraryPaths`. Нет EffectAsset. Слот `FieldWrite`, не `FieldWriteA`.
2. 3.1–3.3 зелёные.
3. Тексты исключения 3.2.
4. Сьют без новых красных (третий `#ifdef` не сломал FluidPasses).
5. Док-список шага 4 закрыт.

### Вне скоупа

- F1.6 / EffectAsset / второй экземпляр после Advect (только документировать в catalog).
- No-slip, periodic, inflow.
- Менять ZeroMean, clamp Φ, второй проход проекции, 3.6.
- MAC.
- Калибровка `iterations` / Bias.
