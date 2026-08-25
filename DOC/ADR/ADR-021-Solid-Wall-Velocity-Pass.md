## ADR-021: SolidWallVelocityPass

**Статус:** Принято (реализовано)
**Дата:** 2026-08-25
**Контекст:** M3D Framework, фаза F1 (F1.4) — непроницаемая рамка Stam-проекции
**Реализует:** [ADR-016](ADR-016-Units-By-Pass-Family.md) §2.3 (отложенные истинные BC); clamp соседей — [ADR-017](ADR-017-Divergence-Pass-And-Square-Texel-Contract.md) / [ADR-018](ADR-018-Jacobi-Phi-Pass.md); проекция — [ADR-020](ADR-020-Subtract-Phi-Gradient-Pass.md); zero-mean — [ADR-018 §5.1](ADR-018-Jacobi-Phi-Pass.md)
**ТЗ:** [`todo-F1.4.md`](../last/todo-F1.4.md)

### Контекст

Divergence / Jacobi / Subtract читают соседей через `Load` + clamp индекса. Это Neumann-подобное **продолжение поля**, не непроницаемая стенка: нормальная компонента `u` на рамке после проекции не обязана быть нулём, и advect может вынести массу «сквозь» край.

План фазы: обнулить нормаль `velocity` **после** Subtract. F1.2b уже закрыт: `ZeroMeanScalarPass` вычитает `mean(D)` по всем текселям под clamp-Neumann Пуассон. F1.4 обязан явно сказать, меняет ли стенка это условие — иначе следующий автор «починит» ZeroMean под интерьер.

### Решение

#### 1. Стенка — пост-обработка `u`, не новая задача Пуассона

`SolidWallVelocityPass` пишет только `velocity`. Кернелы Divergence / Jacobi / Subtract / ZeroMean **не меняются**. Clamp соседей Φ и `D` остаётся.

Стенка не задаёт `∂Φ/∂n = u*·n` и не сужает область ZeroMean. Совместимость Пуассона по-прежнему `ΣD = 0` по всем текселям (ADR-018 §5.1).

Следствие, которое принимаем: если после стен снова снять `D`, на рамке в 1 тексель появится лишняя дивергенция (обнулили `u·n` у уже спроецированного поля). **Второй проход проекции в этом тикете нет.** Метрика F1.3 3.6 (интерьер, до стен) не переписывается. F1.6 может поставить тот же пасс ещё раз после Advect — адвекция тоже рождает нормаль на краю; это композиция пресета, не второй кернел.

#### 2. Free-slip, ось поля = нормаль рамки

Сетка — прямоугольник текселей. `velocity` — `float2` в базисе плоскости (`AxisU`, `AxisV`), как у Divergence. Нормаль левого/правого края — компонента `.x`, верх/низ — `.y`.

```
x = 0 или x = N−1  →  u.x = 0   (касательная u.y не трогаем)
y = 0 или y = N−1  →  u.y = 0   (касательная u.x не трогаем)
угол               →  u = (0, 0)
интерьер           →  без изменений
```

No-slip (обнулить обе компоненты на всём периметре) — не этот тикет: убивает скольжение вдоль стенки.

#### 3. Роли и слот: single-role `FieldWrite`

Одно поле, `WriteInPlace`, Velocity ×2, Role A. `FieldKernelPass` (один кернел, в отличие от ZeroMean).

Single-role биндит **`FieldWrite`**, не `FieldWriteA` (гайд ADR-008: `{A}` без B → legacy-имена). Копировать Subtract (`FieldWriteA`) нельзя — слот не привяжется.

`RequiresSquareTexel => true` — тот же fluid-грид, что проекция; в формуле `h` нет, но неквадратный `velocity` в этой цепочке нелегален. `RepeatCount` = 1. `dt` нет. Default name `velocity`, не `flockVel`.

#### 4. Кернел в `FluidPasses.compute`, `#ifdef KERNEL_SOLIDWALL`

Новый файл не заводится. Токен изолирует `RWTexture2D<float2> FieldWrite` от Jacobi (`FieldWriteA` как `float`) и от Subtract (`FieldWriteA` как `float2` — другое имя, но токен держит декларации по кернелам единообразно).

```hlsl
#pragma kernel SolidWallVelocity KERNEL_SOLIDWALL

#ifdef KERNEL_SOLIDWALL
RWTexture2D<float2> FieldWrite;

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void SolidWallVelocity(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y)
        return;

    int2 p = int2(id.xy);
    float2 u = FieldWrite[p];
    if (p.x == 0 || p.x == FieldResolution.x - 1)
        u.x = 0;
    if (p.y == 0 || p.y == FieldResolution.y - 1)
        u.y = 0;
    FieldWrite[p] = u;
}
#endif
```

`FieldRead` / `FieldReadA` нет. `h` / `Size` / `dt` в арифметике нет.

#### 5. Численные утверждения

Геометрия как F1.1/F1.3: `Size = 32`, `res = 64`. Тестовый `velocity` — `R32G32_SFloat`. Рамка = тексели с `x∈{0,N−1}` или `y∈{0,N−1}`. Интерьер = `1..N−2`.

**(а)** Равномерное поле `(1.25, −0.4)`: интерьер побитово равен сиду. Ребро (не угол): обнулённая компонента — `SingleToInt32Bits == 0` (литерал `+0` из кернела, не `== 0f` и не `RelativeTolerance`); касательная — bitwise сид. Углы: обе компоненты bitwise 0. Не снимать `D` после стен (Techdebt 8g).

**(б)** `SquareTexelValidator` на одном поле `velocity` 32², `Size = (10, 20)` — как Jacobi 4.4 / Subtract 3.5. Второй дескриптор не поднимать.

**(в)** Идемпотентность: второй `RunPass` на том же поле побитово равен первому (свойство пасса, не пресета F1.6).

Не вставлять пасс в `SubtractPhiGradientPassTests` 3.6. Не требовать `max|D|` после стен.

### Отклонённые варианты

**Встроить обнуление в Subtract.** Смешивает «проекция уменьшает D» и «стенка непроницаема».

**No-slip на всём периметре.** Не Stam-коробка v1.

**Менять clamp Φ / область ZeroMean / второй Jacobi.** Пуассон тот же; стенка не BC для Φ.

**Стенка до Divergence как единственное место.** После Subtract нормаль всё равно испортится. До Divergence — опция F1.6 (второй экземпляр того же пасса), не этот тикет.

**Periodic / inflow.** Другой пресет.

**`FieldWriteA` как у Subtract.** Single-role так не биндится.

**MAC / staggered walls.** Триггер — odd-even на dye (ADR-016 §4).

**Подключать к demo.** F1.6.

### Последствия

- (+) После проекции рамка непроницаема для следующего advect.
- (+) ZeroMean и 3.6 не переписываются.
- (−) Рамка в 1 тексель снова дивергентна, если снять `D` после стен. Принято.
- (−) Advect без второго экземпляра пасса снова портит нормаль — F1.6 ставит пасс дважды.

### DoD

1. `SolidWallVelocityPass` в `FluidPasses.cs`; кернел `SolidWallVelocity` за `#ifdef KERNEL_SOLIDWALL`. Путь compute не дублировать в `PassLibraryPaths`.
2. Роли §3. Чтение/запись `FieldWrite`, не `FieldWriteA`.
3. Тесты `[Category("GPU")]` в `SolidWallVelocityPassTests.cs`: (а) bitwise free-slip; (б) SquareTexel; (в) идемпотентность. Velocity — `R32G32_SFloat`.
4. Production-формат `velocity` не изменён. Пасс не в EffectAsset. ZeroMean / Jacobi / Subtract / 3.6 не менять по смыслу.
5. `pass-catalog.md`, `status.md`, `capabilities.md`, `getting-started.md`, `plan-stable-fluid.md` — F1.4 закрыт.
