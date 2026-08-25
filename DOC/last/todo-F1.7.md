## ТЗ для программиста — F1.7 (AdvectScalarPass)

Роль этого документа: реализация по [ADR-023](../ADR/ADR-023-Advect-Scalar-Pass.md). Не пересматривать роли A/B, отказ от ADR-019, отказ от TouchInjectScalar и от `FieldSemantic.Dye`. Если COM dye обгоняет носитель как self-advection — остановить и написать числа, не «чинить» порог.

Прочитать ADR-023 целиком **до кода**. Без этого легко скопировать `AdvectVelocityField` (single-role `FieldRead`) или поставить AdvectScalar до второго Wall.

Зафиксировано — не начинать, пока это не ясно:

1. **Пассивный tracer.** `dye_new(x) = dye_old(x − u(x)·dt/Size)`. Velocity Role B только читается.
2. **После второго `SolidWallVelocityPass`.** Не между Advect velocity и второй стеной.
3. **Поле `dye`, semantic Scalar.** `SeedScalarDisk` требует Scalar. Enum `Dye` не использовать.
4. **Одинаковая геометрия** с `velocity` (F0.5 отложено).
5. **ADR-019 не трогать.** Этот тикет — ADR-023. Odd-even на dye — в отчёт, MAC не открывать.

Референсы по месту:

- `AdvectVelocityFieldPass` + кернел `AdvectVelocityField` — backtrace `uv - vel*dt/FieldSize`, `saturate`, `Dissipation`. **Слоты не копировать.**
- `JacobiPhiPass` — образец WritePingPong A + Read B (`FieldReadA`/`FieldWriteA`/`FieldReadB`).
- `FieldPasses.compute` — глобальные `FieldRead`/`FieldWrite` без токенов. Первый `#ifdef` в файле: **слоты и тела** старых кернелов в `#ifndef KERNEL_ADVECTSCALAR` (как `FluidPasses.compute`). Спрятать только декларации — шейдер не соберётся.
- `HarnessAdvectTests.ComX` / `GaussianVx` — скопировать арифметику COM; фон dye = 0, не carrier 1.7. **Не** копировать assert overshoot.
- `SeedScalarDiskPass` — one-shot (`ShouldDispatch` + `hasFired`), кернел в `GrayScottPasses.compute`. В пресете `FieldName = "dye"` (дефолт `"V"`). Не штамповать диск каждый кадр.
- `Fluid2DPresetTests` — расширить тот же файл; метод переименовать в `Preset_MatchesFluid2DComposition`.
- `M3DDemoTools.CreateFluid2DEffect` — добавить поле и два пасса; Create = `DeleteAsset`. После Create — Assign.

`FieldPasses.compute` уже в `PassLibraryPaths`. Путь не дублировать. `FluidPasses.*` / валидаторы / Jacobi / Bias — не менять по смыслу.

Код писать. Документацию из шага 4 — тоже. Краска тачем, MAC, ADR-019 — вне скоупа.

---

### Шаг 1 — `AdvectScalarPass` в `FieldPasses.cs`

```csharp
public sealed class AdvectScalarPass : FieldKernelPass
{
    [SerializeField] private string scalarField = "dye";
    [SerializeField] private string velocityField = "velocity";
    [SerializeField, Min(0f)] private float dissipationRate = 0f;

    public string ScalarField { get => scalarField; set => scalarField = value; }
    public string VelocityField { get => velocityField; set => velocityField = value; }
    public float DissipationRate { get => dissipationRate; set => dissipationRate = value; }

    public override string DisplayName => "Advect Scalar";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "AdvectScalar";
    public override bool RequiresSquareTexel => false;

    public override IReadOnlyList<FieldRequest> FieldReads =>
        FieldRequestSets.Single(
            ref fieldReadsCache, velocityField,
            FieldAccess.Read, FieldSemantic.Velocity, 2, FieldSlotRole.B);

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, scalarField,
            FieldAccess.WritePingPong, FieldSemantic.Scalar, 1, FieldSlotRole.A);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
        SetFloat(context, SimShaderIds.Dissipation, Mathf.Exp(-dissipationRate * deltaTime));
    }
}
```

Кэши `[NonSerialized]` — как у Jacobi. `RepeatCount` не переопределять. Без `DeltaTime` в `SetParams` dye стоит на месте при зелёном контракте ролей.

**Роли строго так.** dye WritePingPong A, velocity Read B. Не наоборот. Не single-role.

---

### Шаг 2 — кернел в `FieldPasses.compute`

1. `#pragma kernel AdvectScalar KERNEL_ADVECTSCALAR` рядом с `AdvectVelocityField`.
2. `include`, `FIELD_THREADS`, `sampler_linear_clamp`, `DeltaTime` / `Dissipation` / `DiffusionRate`, `Touches` / `MaxFieldSpeed` — **снаружи** блоков.
3. `#ifndef KERNEL_ADVECTSCALAR`: `FieldRead`/`FieldWrite` **и все существующие кернелы файла** без смены формул. Образец — `FluidPasses.compute` (слот + тело в одном `#ifdef`). Спрятать только декларации — `AdvectScalar` не соберётся (чужие тела ссылаются на `FieldWrite`).
4. `#ifdef KERNEL_ADVECTSCALAR`: слоты и тело **дословно** ADR-023 §1/§3. `FieldReadA` = dye, `FieldWriteA` = dye Next, `FieldReadB` = velocity. `vel = FieldReadB.SampleLevel(sampler_linear_clamp, uv, 0)`; `backUv = saturate(uv - vel * DeltaTime / FieldSize)`; `FieldWriteA[id.xy] = FieldReadA.SampleLevel(..., backUv, 0) * Dissipation`.
5. Шапка: токен изолирует legacy `FieldRead` (`float2`) от `FieldReadA` (`float`). Комментарий «M2c not yet» заменить одной фразой про `#ifdef`.

`dt` / `Size` в арифметике **есть**. `h` вручную не считать. Новый `.compute` не заводить.

---

### Шаг 3 — тесты `Assets/Tests/Editor/AdvectScalarPassTests.cs`

`[TestFixture]` **без** `[Category("GPU")]` на классе. GPU — только на 3.1–3.4 (как Jacobi). 3.5 без GPU. Compute: `Assets/Shaders/GPU/Passes/FieldPasses.compute`.

Геометрия: `Resolution = 64`, `Size = (64, 64)`, `dt = 1`. Тогда `h = 1`, `u = (1,0)` → `Δuv = dt/Size` → **1 тексель/шаг**, за 8 шагов ожидание 8. Скорость в world, не «1 тексель руками». Поля: `dye` `R32_SFloat`, `velocity` `R32G32_SFloat`.

COM — как `HarnessAdvectTests.ComX`: момент `(x+0.5)` / масса; для dye фон вычесть **0**. Считать и `dCOM_y`.

Не добавлять тест сохранения массы (bilinear ест массу, Techdebt 5).

#### 3.1 — пассивный перенос COM `[Category("GPU")]`

Носитель `velocity = (1, 0)` всюду. Dye — гауссиан **как** `HarnessAdvectTests` (`σ=1.5`, центр `(20.5, 32.5)`), **amp=1**, фон 0 (не carrier 1.7). 8 шагов. Assert:

- `|dCOM_x − 8| < 0.5`
- `|dCOM_y| < 0.5`
- `dCOM_x < 10` (если ~13.7 — self-advection, кернел сломан)

`RelativeTolerance(R32_SFloat)` (=1e-6) как допуск COM **не** использовать. `TestContext.WriteLine`: dCOM_x, dCOM_y, expected 8. Каждый тест — свой `using` харнес.

#### 3.2 — Role B не пишется `[Category("GPU")]`

**Самостоятельный** прогон (тот же сид, 8 шагов), не хвост 3.1: NUnit не шарит GPU-состояние между методами. `velocity` побитово равен сиду (`SingleToInt32Bits` по компонентам).

#### 3.3 — константный dye `[Category("GPU")]`

`dye = 0.4` всюду, `velocity` ненулевой. Один шаг. Dye в допуске R32 равен сиду.

#### 3.4 — `Initialize` mismatch `[Category("GPU")]`

`dye` 32² и `velocity` 64², оба с квадратным текселем → `pass.Initialize` бросает `InvalidOperationException` про matching Resolution / plane. Образец: Jacobi 4.3.

#### 3.5 — контракт ролей (без GPU)

`FieldWrites[0]`: dye, WritePingPong, Scalar, 1, A. `FieldReads[0]`: velocity, Read, Velocity, 2, B. `KernelName == "AdvectScalar"` (reflection, образец `AdvectVelocityFieldPassTests`). `RequiresSquareTexel == false`.

`HarnessAdvectTests` / 3.6 / ZeroMean / SolidWall / Jacobi **не менять**.

---

### Шаг 3b — пресет и EditMode-тест

`CreateFluid2DEffect`:

- четвёртый дескриптор `dye`, Scalar; после патча format **`R16_SFloat`**, res 128, Size 32, XZ, clear 0;
- `new SeedScalarDiskPass { FieldName = "dye", CenterUV = (0.5, 0.5), RadiusUV = 0.08f, Value = 1f }` сразу после Touch;
- `new AdvectScalarPass { ScalarField = "dye", VelocityField = "velocity", DissipationRate = 0f }` **последним**;
- debug quads: прежний velocity `0.125` + `DebugFieldQuadSlot.Density("dye")`.

Iterations=40, Advect velocity `FieldName=velocity`, два SolidWall, dissipation 0, velocity quad `0.125` — явно в Create (F1.6 дефолты). `kind = None`. Create один раз; `DeleteAsset` часто даёт новый guid → **сразу Assign Fluid2D To Scene** и Rebuild. Не вызывать Create из теста.

`Fluid2DPresetTests`: переименовать метод в `Preset_MatchesFluid2DComposition` (проверяет ADR-022+023). Поля 4 (`velocity`, `fluidD`, `fluidPhi`, `dye`); `dye` = R16, не R32; Passes.Count == 10; типы: Touch, SeedScalarDisk, Divergence, ZeroMean, Jacobi, Subtract, SolidWall, AdvectVelocity, SolidWall, AdvectScalar; Seed.FieldName == `dye`; AdvectScalar.ScalarField == `dye`, VelocityField == `velocity`; два debug-слота (velocity + dye). Jacobi по-прежнему `[40, 80]`. Второй test-файл на пресет не писать.

---

### Шаг 4 — документация (часть тикета)

- `DOC/plan-stable-fluid.md`: F1.7 → **Готово**, ссылка на ADR-023.
- `DOC/pass-catalog.md`: раздел **Advect Scalar** после Advect Velocity; цепочка Fluid2D с Seed и AdvectScalar в конце. В таблицу `FieldPasses.compute` добавить AdvectScalar.
- `DOC/getting-started.md`: dye есть; «пока нет dye» убрать.
- `DOC/capabilities.md`: AdvectScalar + dye-quad; F0.5 по-прежнему нет.
- `DOC/status.md`: секция F1.7; итерацию сдвинуть; во «Вне скоупа» F1.7 убрать, оставить **ADR-019**.
- `DOC/ADR/ADR-016-Units-By-Pass-Family.md` §1 таблица fluid-контура: добавить `AdvectScalarPass` рядом с `AdvectVelocityFieldPass` (world). ADR-019 не трогать.
- `DOC/ADR/ADR-014-GPU-Numeric-Test-Harness.md`: строка `ADR-023 | F1.7 | …` уже вписана вместе с ADR; не дублировать. Строка ADR-019 — «после F1.7», не «тикет F1.7».
- `DOC/ADR/ADR-023-Advect-Scalar-Pass.md`: статус **Принято (реализовано)** + факт odd-even (виден / нет).
- `Fluid2DPresetTests` / ADR-022: одна фраза в шапке или catalog, что пресет теперь с dye — не переписывать ADR-022 целиком.

---

### Шаг 5 — visual (после зелёных тестов)

Play, Assign Fluid2D, GroundXZ. Диск dye в центре; тач крутит velocity — blob тянется. **Не вытекает** = нет wrap; масса может **налипать на рамку** (`saturate(UV)`). Скольжение dye вдоль края без налипания не требуется. Inf/NaN нет.

**Odd-even:** стоп только если в **интерьере** после размешивания видна устойчивая шахматка 1 тексель (Nyquist collocated). Грязь рамки / bilinear / полосы — не триггер. MAC не открывать. В отчёт: виден / не виден / спорно.

---

### Отчёт

1. Diff: `FieldPasses.cs`, `FieldPasses.compute` (ifdef + AdvectScalar), `M3DDemoTools.cs`, `Fluid2D.asset`, `AdvectScalarPassTests.cs`, правки `Fluid2DPresetTests.cs`, доки шага 4. `FluidPasses.cs` / `.compute` / Jacobi / Bias / production `velocity` — без изменений смысла. `PassLibraryPaths` не дублировать. ADR-019 не трогать.
2. 3.1: dCOM vs 8. 3.2 bitwise velocity.
3. Visual + odd-even (да/нет).
4. GPU fluid-семейства и EditMode пресета: зелёные.

Если шейдер не компилируется — не выносить AdvectScalar в новый файл без нового решения; чинить `#ifdef` в `FieldPasses.compute`.
