## ТЗ для программиста — F1.2b (ZeroMeanScalarPass)

Роль этого документа: реализация по [ADR-018 §5.1](../ADR/ADR-018-Jacobi-Phi-Pass.md). Не пересматривать имя, роли, трёхкернельный Execute, отказ от `FieldAccumBuffer` и формулу Bias/Scale. Если drift-тест не сойдётся — остановить и написать числа, не крутить Bias/итерации/порог.

Прочитать §5 (закон дрейфа) и §5.1 целиком. P2G (`P2GPasses.compute` `EncodeFixed`, `ClearFieldAccumPass`) — только образец `InterlockedAdd` uint, **не** копировать layout, SM и `max(x,0)` без Bias.

Зафиксировано по вопросам реализации:

1. **Буфер:** пасс `IDisposable`. `Initialize` диспозит предыдущий буфер. `SimulationWorld.Teardown` — цикл по `effect.Passes`, `if (pass is IDisposable d) d.Dispose()`, **до** `fields?.Dispose()`. Не виртуальный Teardown на `SimPass`. Тесты — `Dispose` в `finally`.
2. **Bias = 256** — дефолт продукта. Клип только в Accum. Калибровка под TouchInject — F1.6, не этот тикет.
3. **F1.6 после зелёного F1.2b:** статус **Открыто — F1.4 не блокер**.
4. **Тест 3.5** самодостаточен: два кадра в одном методе, не зависит от порядка NUnit.

**Класс — `ZeroMeanScalarPass`.** Не MeanPressure, не метод на `JacobiPhiPass`.

Референсы по месту:

- `Assets/Scripts/Passes/FluidPasses.cs`, `Assets/Shaders/GPU/Passes/FluidPasses.compute` — расширить оба. Новые файлы пассов/compute не создавать.
- `ClearFieldAccumPass` (`P2GPasses.cs`) — образец `SimPass` с своим `Execute` (не `FieldKernelPass`): `FindKernel`, `SetComputeBufferParam`, `DispatchCompute`, `LastExecuteDispatched = false` в начале Execute.
- `SimulationWorld.Teardown` — добавить dispose `IDisposable` пассов; других крючков в World не делать.
- `FieldKernelPass.Execute` (`SimPass.cs`): WriteInPlace биндит только `WriteId` → `Current`. Кернелы Accum/Apply читают и пишут `FieldWriteA`. `FieldReadA` не объявлять (UAV+SRV на Current запрещён; плюс конфликт типа с Divergence).
- `FieldShaderParams.Push` — для Accum/Apply (нужен `FieldResolution`).
- `SubtractPhiGradientPass` — образец `#ifdef KERNEL_*` и чтения из `FieldWriteA`.
- `JacobiPhiPassTests` — **не менять**. Dipole / bitwise `fluidD` остаются без ZeroMean.
- `FieldTestHarness.RunPass` — один `using`, несколько пассов подряд (как F1.3 цепочка).

`FluidPasses.compute` уже в `M3DDemoTools.PassLibraryPaths`. Путь не добавлять второй раз.

Код писать. Документацию из шага 4 — тоже. Demo-пресеты, F1.4, F1.6, правки Jacobi/Subtract — вне скоупа.

---

### Шаг 1 — `ZeroMeanScalarPass` в `FluidPasses.cs`

Не наследовать `FieldKernelPass` (`Execute` sealed, один кернел). `SimPass`, как ClearAccum.

```csharp
public sealed class ZeroMeanScalarPass : SimPass, IDisposable
{
    [SerializeField] private string scalarField = "fluidD";

    public string ScalarField { get => scalarField; set => scalarField = value; }

    public override string DisplayName => "Zero Mean Scalar";
    public override PassCategory Category => PassCategory.Transport;
    public override bool RequiresSquareTexel => false;
    // RepeatCount не переопределять — остаётся 1.
    public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.None;

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(
            ref fieldWritesCache, scalarField,
            FieldAccess.WriteInPlace, FieldSemantic.Scalar, 1, FieldSlotRole.A);
}
```

`FieldReads` — пустой список базы. Кэши `[NonSerialized]` как у соседних пассов.

Класс также `IDisposable`. `Dispose` идемпотентен (повторный вызов без NRE). Публичный `Scale` для лога тестов — не обязательно поле инспектора.

`Initialize`:

1. `FindKernel("ZeroMeanClear")`, `FindKernel("ZeroMeanAccum")`, `FindKernel("ZeroMeanApply")`. `Shader.PropertyToID` для `MeanAccum`, `MeanBias`, `MeanScale`, `TexelCount` — как ClearAccum, один раз здесь.
2. Взять дескриптор `scalarField` из `context.Fields`. `N = res.x * res.y` (int).
3. `Bias = 256`. Scale **целым** делением: `Scale = max(1, (1 << 30) / (2 * N * 256))` — без `2f` / `floor` по float. На 64² ожидание **512**. Залогировать в тесте.
4. `Dispose()` предыдущего буфера, затем `GraphicsBuffer` на 1 uint (`Target.Structured`, stride 4). На поле не вешать. Не `FieldAccumBuffer`.

`Execute` — три dispatch подряд, один CommandBuffer. **Биндить на каждый kernel.Index отдельно** (типичный баг: текстура только на Accum → Apply молча не пишет).

1. `LastExecuteDispatched = false` в начале.
2. Clear: только `MeanAccum`, `DispatchCompute(..., 1, 1, 1)`.
3. Accum: `FieldWriteA` = `Current` (`SimShaderIds.FieldWriteA`), `FieldShaderParams.Push` на этот kernel, `MeanAccum`, `MeanBias` / `MeanScale`, группы `(res + 7) / 8` как `FieldKernelPass`.
4. Apply: то же `FieldWriteA` + `MeanAccum` + Bias/Scale + `TexelCount` через `SetComputeIntParam` (HLSL `uint`, не float), те же группы.
5. `LastExecuteDispatched = true`.

UAV-барьер не добавлять заранее. `dt` не пушить. `FieldAccumClears` / `Writes` / `Reads` не объявлять.

---

### Шаг 2 — кернелы в `FluidPasses.compute`

1. Добавить три `#pragma kernel … KERNEL_ZEROMEAN` рядом с существующими.
2. Шапка файла: токен изолирует ещё и этот `FieldWriteA` (`float` vs Subtract `float2`).
3. Divergence / Jacobi / Subtract **не** трогать по смыслу.
4. Новый блок — дословно:

```hlsl
#ifdef KERNEL_ZEROMEAN
RWTexture2D<float> FieldWriteA;
RWStructuredBuffer<uint> MeanAccum;
float MeanBias;
float MeanScale;
uint TexelCount;

[numthreads(1, 1, 1)]
void ZeroMeanClear(uint3 id : SV_DispatchThreadID)
{
    MeanAccum[0] = 0;
}

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void ZeroMeanAccum(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y)
        return;

    float d = FieldWriteA[id.xy];
    d = clamp(d, -MeanBias, MeanBias);
    float x = (d + MeanBias) * MeanScale;
    x = (x == x) ? x : MeanBias * MeanScale; // NaN → encode 0
    InterlockedAdd(MeanAccum[0], (uint)(x + 0.5));
}

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void ZeroMeanApply(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y)
        return;

    float mean = (float)MeanAccum[0] / (MeanScale * (float)TexelCount) - MeanBias;
    int2 p = int2(id.xy);
    FieldWriteA[p] = FieldWriteA[p] - mean;
}
#endif
```

`#include` / `FIELD_THREADS` снаружи блоков, как сейчас.

Не копировать `EncodeFixed` из P2G (`max(x, 0)` без сдвига на Bias уничтожит отрицательный `D`).

---

### Шаг 3 — тесты `Assets/Tests/Editor/ZeroMeanScalarPassTests.cs`

`[TestFixture]`, `[Category("GPU")]`. Compute: `Assets/Shaders/GPU/Passes/FluidPasses.compute`.

Геометрия как F1.2: `Resolution = 64`, `Size = 32`. `fluidD` / `fluidPhi`: `R32_SFloat`. `PlanePosition` не нужен (сидировать по индексу текселя).

Допуск на остаток после Apply: `2f / Scale` (квант аккумулятора с запасом), не bitwise zero.

#### 3.1 — константа: mean снимается

Залить `fluidD = 1` всюду. Прогнать ZeroMean. `mean(D')` по всем текселям: `|mean| < 2/Scale`. `max|D'|` того же порядка. Ловит забытый Apply, неверный знак, Scale=1 с переполнением, копию P2G `max(x,0)`.

В лог: `N`, `Scale` (ожидание 512), `Bias`, `meanBefore`, `meanAfter`, `maxAbsAfter`.

#### 3.2 — знаковое: плюс и минус оба входят в сумму

Половина сетки `+1`, половина `−1` (ровно `N/2` и `N/2`). После ZeroMean `|mean| < 2/Scale`. Без Bias отрицательная половина пропала бы, mean остался бы ~0.5.

#### 3.3 — dipole: почти no-op

Сид как Jacobi 4.1: `+1` в `(20,32)`, `−1` в `(44,32)`, остальное 0. После ZeroMean каждая клетка в допуске `AssertApproximately` харнеса к сиду (mean сида = 0, Apply вычитает ~0).

#### 3.4 — главный DoD: дрейф Φ остановлен

Один харнес, `fluidD` + `fluidPhi`. Φ не сидировать (clear 0).

1. `fluidD = 1` всюду.
2. ZeroMean.
3. `JacobiPhiPass { Iterations = 40 }`, `RepeatCount == 40`, двухаргументный `RunPass`.
4. Снять Φ, `mean(Φ)` по всем текселям.

Без ZeroMean закон ADR-018 §5 даёт `mean(Φ) = −10` за кадр. С ZeroMean: `|mean(Φ)| < 0.1`. Порог **только** для этой геометрии (64², Scale=512); квант mean `~2/512`, дрейф Jacobi ×10 ≈ 0.04. Не переносить 0.1 на 256². Не требовать машинный ноль.

Лог: `meanD_after`, `meanPhi`, `Scale`.

#### 3.5 — warm-start не копит

Самодостаточный тест, два кадра **в одном методе**, не «после 3.4». Один харнес: кадр 1 = шаги 3.4; кадр 2 — снова залить `fluidD = 1`, ZeroMean, Jacobi×40 **без** очистки Φ. После кадра 2: `|mean(Φ)| < 0.1`, не ~−20. Лог обоих `meanPhi`.

Не подключать F1.4. Не менять `SubtractPhiGradientPassTests`. Не требовать bitwise `D'=0`. Сиды 3.1–3.3 не заменять случайным шумом (иначе допуск 3.3 не `AssertApproximately`, а `2/Scale`).

---

### Шаг 4 — документация (часть тикета)

- `DOC/pass-catalog.md`: раздел **Zero Mean Scalar** после Jacobi (до Subtract): роли WriteInPlace A, три кернела, dt нет, не `RequiresSquareTexel`, «не в demo, пресет — F1.6, в цепочке перед Jacobi». В таблицу Pass Library в строку `FluidPasses.compute` добавить Zero Mean.
- `DOC/status.md`: секция F1.2b «готово» с числами 3.1/3.4 (`Scale`, `meanPhi`).
- `DOC/plan-stable-fluid.md`: F1.2b → **Готово**. F1.6 → **Открыто — F1.4 не блокер** (не оставлять «Блокирован F1.2b»).
- `DOC/last/Techdebt.md` 8d: статус «устранено F1.2b», ссылка на ADR-018 §5.1. Одна фраза: Bias=256, клип Accum, калибровка F1.6.
- `DOC/capabilities.md`: одна строка — zero-mean `fluidD` до Jacobi.
- `DOC/getting-started.md`: в список кернелов Stam добавить ZeroMean (как F1.3 добавлял Subtract).
- ADR-018: шапка — F1.2b закрыт в §5.1. В «Последствия» пункт (−) про неустранённый F1.2b — снять / заменить на «закрыт §5.1». В `pass-catalog` у Jacobi/Subtract убрать «блокер F1.2b» (у Jacobi — «устранено F1.2b»; у Subtract — ZeroMean перед Jacobi в пресете). ADR-019 не трогать.

---

### Отчёт

1. Diff production: `FluidPasses.cs` (новый класс + `IDisposable`), `FluidPasses.compute` (блок KERNEL_ZEROMEAN), `SimulationWorld.Teardown` (dispose `IDisposable` пассов). Jacobi / Divergence / Subtract без смысловых правок. Нет `FieldAccum*` на этом пассе. Нет второго пути в `PassLibraryPaths`. Нет EffectAsset. Не виртуальный `SimPass.Teardown`.
2. 3.1–3.5: зелёные + числа (`Scale`, mean до/после, `meanPhi` кадр 1 и 2).
3. `JacobiPhiPassTests` / `SubtractPhiGradientPassTests` / `DivergenceFieldPassTests` без новых красных.
4. Док-список шага 4 закрыт.

### Вне скоупа

- F1.4, F1.6 / EffectAsset / touch / VFX.
- Встраивание в `JacobiPhiPass`.
- `FieldAccumBuffer`, пин угла, CPU-readback.
- Менять 3.6 / порог ≥3× / k.
- Калибровка `iterations = 40`.
- MAC / среднее только по интерьеру.
