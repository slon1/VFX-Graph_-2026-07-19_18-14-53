## ADR-023: AdvectScalarPass (пассивный dye)

**Статус:** Принято (реализовано)
**Дата:** 2026-08-25
**Контекст:** M3D Framework, фаза F1 (F1.7) — tracer по уже закрытому Stam-контуру F1.6
**Реализует:** semi-Lagrangian backtrace [ADR-013](ADR-013-Sampler-Verification+Velocity-Field-Self-Advection.md) без self-advection; world-единицы [ADR-016](ADR-016-Units-By-Pass-Family.md) §2; multi-role [ADR-008](ADR-008-Multi-Field-Per-Kernel-Binding.md); пресет [ADR-022](ADR-022-Fluid2D-Preset.md)
**ТЗ:** [`todo-F1.7.md`](../last/todo-F1.7.md)

Сводка Stam после F1.7: [ADR-019](ADR-019-Fluid2D-Solver.md). Этот тикет — кернел tracer + проводка в `Fluid2D.asset`.

### Контекст

F1.6 дал живой Stam на velocity-quad. Без пассивного скаляра «глазами» видна только chroma скорости: не видно, уезжает ли масса сквозь рамку, и не виден odd-even collocated-сетки. План фазы: `AdvectScalarPass` — тот же backtrace, что `AdvectVelocityFieldPass`, но скаляр не несёт сам себя.

F0.5 (dye выше разрешением, чем velocity) **отложен до после F1.** В этом тикете `dye` и `velocity` — одна геометрия (128², Size 32, XZ), иначе `ValidateMatchingFieldGeometry` на multi-role и `Load`/`Sample` с чужой сетки.

`FieldSemantic.Dye` в enum есть и ни одним пассом не требуется. `SeedScalarDiskPass` требует **Scalar**. Поле называем `dye`, semantic — **Scalar**. Не переводить Seed на Dye и не заводить второй seed.

### Решение

#### 1. Пассивный tracer, не self-advection

```
vel = sample(velocity, uv)           // несущее поле в текущей точке
backUv = saturate(uv − vel · dt / Size)
dye_next = sample(dye, backUv) * Dissipation
```

`Dissipation` — как у ADR-013: CPU `exp(−rate·dt)`, 1 = выкл. Дефолт `0`. Это не вязкость.

Отличие от `AdvectVelocityField`: там `selfVel` и advected значение — одно поле. Здесь скорость **не** переписывается. Тест-различитель (геометрия `HarnessAdvectTests`: 64², Size=64, dt=1 → 1 тексель/шаг): гауссов dye (`σ=1.5`, центр `(20.5, 32.5)`, amp=1, фон 0) на носителе `u=(1,0)`, 8 шагов. Ожидание `dCOM_x ≈ 8`, `|dCOM_x−8|<0.5`, `|dCOM_y|<0.5`, и жёстко `dCOM_x<10` (13.7 = сломанный self-advection). RelTol R32 (`1e-6`) как допуск COM **не** использовать.

#### 2. Роли и слоты

```
FieldWrites = [ (dye,      WritePingPong, Scalar,   1, Role A) ]
FieldReads  = [ (velocity, Read,          Velocity, 2, Role B) ]
```

Имена по умолчанию: `dye`, `velocity` (не `flockVel`, не `V`). `PrimaryFieldName` / dispatch — Role A (dye). `RepeatCount` = 1. `RequiresSquareTexel` = **false**: в формуле есть `FieldSize`, анизотропия переживается; в пресете тексель всё равно квадратный из-за проекции. Multi-role включает `ValidateMatchingFieldGeometry` (одинаковые Resolution / Origin / оси / Size).

`WritePingPong` биндит `FieldReadA` (Current) + `FieldWriteA` (Next). Velocity — `FieldReadB`. Копировать single-role `FieldRead`/`FieldWrite` у `AdvectVelocityField` нельзя.

#### 3. Кернел в `FieldPasses.compute`, `#ifdef KERNEL_ADVECTSCALAR`

Новый `.compute` не заводить. Файл уже держит `AdvectVelocityField`, `sampler_linear_clamp`, `DeltaTime`, `Dissipation`. Сейчас слоты файла глобальные `Texture2D<float2> FieldRead` / `RWTexture2D<float2> FieldWrite` без токенов — конфликт типа с `FieldReadA` как `float`.

```
#pragma kernel AdvectScalar KERNEL_ADVECTSCALAR
```

Существующие `FieldRead`/`FieldWrite` **и тела всех старых кернелов** обернуть в `#ifndef KERNEL_ADVECTSCALAR` (образец: `FluidPasses.compute` — слот и тело в одном `#ifdef`). Спрятать только декларации слотов нельзя: Unity компилирует весь файл на каждый `#pragma kernel`, и `AdvectScalar` увидит `TouchInjectVelocity`, который пишет в необъявленный `FieldWrite`.

```hlsl
#pragma kernel AdvectScalar KERNEL_ADVECTSCALAR

// include, FIELD_THREADS, sampler_linear_clamp, DeltaTime, Dissipation, FieldSize,
// Touches / MaxFieldSpeed — снаружи обоих блоков.

#ifndef KERNEL_ADVECTSCALAR
Texture2D<float2> FieldRead;
RWTexture2D<float2> FieldWrite;
// … существующие кернелы файла без смены формул …
#endif

#ifdef KERNEL_ADVECTSCALAR
Texture2D<float> FieldReadA;      // dye Current
RWTexture2D<float> FieldWriteA;   // dye Next
Texture2D<float2> FieldReadB;     // velocity

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void AdvectScalar(uint3 id : SV_DispatchThreadID) { /* §1 */ }
#endif
```

Конфликт не «`FieldReadA` float vs float2 у velocity-адвекции» (там слот `FieldRead`). Конфликт — чужие тела без своих слотов. Новый `.compute` не заводить.

Класс — `AdvectScalarPass` в `FieldPasses.cs` рядом с `AdvectVelocityFieldPass`. Не в `FluidPasses.cs`: проекционные кернелы без `dt`/`Size`; этот — адвекция.

#### 4. Пресет Fluid2D

Цепочка за кадр. `SeedScalarDiskPass` — **one-shot**: `ShouldDispatch` + `hasFired`, кернел уже в `GrayScottPasses.compute` (в Pass Library). Не штампует диск каждый кадр; Gray-Scott не трогать. Ставим сразу после Touch:

```
TouchInjectVelocity
SeedScalarDisk(dye)          // FieldName=dye, center 0.5, radiusUV 0.08, value 1
Divergence
ZeroMeanScalar
JacobiPhi ×40
SubtractPhiGradient
SolidWallVelocity
AdvectVelocityField(velocity)
SolidWallVelocity
AdvectScalar(dye ← velocity)
```

Advect dye **после второго SolidWall**: tracer едет на поле с `u·n=0`. На скаляре стен нет — только `saturate(UV)`: масса **складывается на рамке**, не wrap. Это и есть visual «не вытекает из квада». Скольжение dye вдоль края без налипания Stam не обещает (стенки только у `velocity`).

Поле `dye`: Scalar, **`R16_SFloat`** (не R32 — не Φ), 128², Size 32, XZ, clear 0. Четвёртое поле, matching к `velocity`.

Debug quads: `velocity` (`colorScale=0.125`) **и** `dye` (`DebugFieldQuadSlot.Density("dye")`, scale 1). Не `fluidPhi`.

Источник dye — **только** существующий `SeedScalarDiskPass`. Новый TouchInjectScalar / краска пальцем — **вне скоупа**.

`Create Fluid2D Effect` по-прежнему `DeleteAsset` (часто новый guid). F1.6 дефолты (40 / dissipation 0 / colorScale 0.125) вернуть явно в Create. **После Create — Assign Fluid2D To Scene** (слот сцены иначе может смотреть на старый guid) и Rebuild.

#### 5. Odd-even на dye

[ADR-016 §4](ADR-016-Units-By-Pass-Family.md): триггер MAC — **устойчивый checkerboard интерьера** (чёт/нечет тексели, Nyquist-мода collocated `div∘grad`), не грязь рамки от `saturate` и не полосатость bilinear (Techdebt 5).

Стоп и отчёт (MAC не открывать), только если после размешивания в **интерьере** квада видна явная шахматка 1 тексель. Рамка / смаз / шум — написать «не виден» и закрывать. Мода как known limitation — [ADR-019](ADR-019-Fluid2D-Solver.md).

**F1.7 visual (2026-08-25):** odd-even интерьера на dye **не виден** (после swirl диск растянулся в 4-лучевую звезду без 1-тексельной шахматки). MAC не открывали.

### Последствия

- (+) Весь Stam-контур виден на heatmap dye, не только на velocity-quad.
- (+) Численно отличим от self-advection (нет overshoot COM).
- (−) Cross-res dye/velocity (F0.5) нет.
- (−) Краска тачем нет: один диск на Rebuild.
- (−) Dissipation dye — тот же полу-лагранжев смаз, Techdebt 5.

**Вне скоупа:** MAC / Rhie–Chow (даже если odd-even виден — стоп и отчёт); F0.5; `FieldSemantic.Dye`; TouchInjectScalar; второй Poisson; смена Jacobi/Bias/production `velocity`; Harris-порядок; MacCormack.

### Альтернативы (отклонены)

**Занять ADR-019 под tracer.** Номер — сводка после dye, не кернел. Закрыт: [ADR-019](ADR-019-Fluid2D-Solver.md).

**Положить кернел в `FluidPasses.compute`.** Проекция без семплера/`dt`; адвекция уже в `FieldPasses.compute`.

**Self-advection dye (sample dye velocity from dye).** Dye не несёт скорость.

**Dye до второго Wall / до Advect velocity.** Tracer увидел бы нормаль на рамке.

**Краска тачем в этом тикете.** Второй новый кернел; Seed уже даёт видимый blob.
