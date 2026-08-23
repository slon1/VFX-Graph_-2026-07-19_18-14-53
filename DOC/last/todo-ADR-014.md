## ТЗ для Grok — ADR-014 (GPU Numeric Test Harness)

### Контекст

Прочитать [ADR-014](../ADR/ADR-014-GPU-Numeric-Test-Harness.md). Это первый тикет фазы 0 подготовки к Stable Fluids: без него у проекции давления не будет проверяемого DoD.

**Структурные референсы в существующем коде — свериться с ними, не писать с нуля:**

- `Assets/Tests/Editor/FieldSlotNamingTests.cs` — уже собирает реальный `FieldSet` (`AllocateFields`) и реальный `SimContext`; там же идиома `SetPrivate` для приватных полей `FieldDescriptor` через reflection.
- `Assets/Tests/Editor/SeedScalarDiskPassTests.cs` — уже грузит реальный `.compute` через `AssetDatabase` + `Assume.That(shader.HasKernel(...))`.
- `Assets/Scripts/Runtime/SimulationWorld.cs` (`Update`, `SwapPingPongFields`) — контракт кадра, который харнес обязан повторить дословно.

**Две ловушки, найденные при ревью — не наступить:**

1. **`.asmdef` в `Assets/Tests` добавлять нельзя.** `Assets/Scripts/AssemblyInfo.cs` содержит `[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]`, и тесты пользуются `internal`-типами (`SimShaderIds` в `FieldSlotNamingTests`). Отдельная сборка молча отрежет доступ. Тесты остаются в `Assembly-CSharp-Editor`.
2. **Существующий `AllocateFields` не исполняет `CommandBuffer`.** Он передаёт `cmd` в `FieldSet.Allocate` (который пишет туда `ClearBoth`) и релизит, не вызвав `Graphics.ExecuteCommandBuffer`. Для контрактных тестов безвредно, для численных — поля содержат мусор. Харнес обязан исполнять.

---

### Шаг 1 — `HarnessProbes.compute` (test-only)

Файл: `Assets/Tests/Editor/Shaders/HarnessProbes.compute`.

**Не добавлять в `M3DDemoTools.PassLibraryPaths`** и не упоминать в чеклисте Pass Library — это тестовая инфраструктура, не production-пасс. Харнес грузит его отдельно, по прямому пути через `AssetDatabase`.

Пять ядер. Каналы **разделены**, не параметризованы: типизированный UAV/SRV обязан совпадать с объявлением кернела (то же правило, по которому ADR-004 и ADR-005 развели `float` и `float2` по разным файлам).

| Кернел | Вход → выход | numthreads |
| --- | --- | --- |
| `FillScalarFromBuffer` | `StructuredBuffer<float>` → `RWTexture2D<float>` | `(8,8,1)` |
| `FillVelocityFromBuffer` | `StructuredBuffer<float2>` → `RWTexture2D<float2>` | `(8,8,1)` |
| `ReadScalarToBuffer` | `Texture2D<float>` (`Load`) → `RWStructuredBuffer<float>` | `(8,8,1)` |
| `ReadVelocityToBuffer` | `Texture2D<float2>` (`Load`) → `RWStructuredBuffer<float2>` | `(8,8,1)` |
| `ProbeSampleLevelScalar` | `Texture2D<float>` + `StructuredBuffer<float2>` UV → `RWStructuredBuffer<float>` | `(64,1,1)` |

Требования:

- Чтение поля — через **SRV + `Load`**, не через UAV. Это снимает зависимость от опциональной поддержки типизированной UAV-загрузки для не-`R32` форматов.
- Линейный индекс буфера: `id.y * Resolution.x + id.x`. Проверить, что совпадает с раскладкой, которую ожидает харнес на C#-стороне (одна конвенция, не две).
- `ProbeSampleLevelScalar` использует **тот же** `SamplerState sampler_linear_clamp`, что production-ядра (`FieldPasses.compute:23`, `GradientPasses.compute:13`) — иначе тест проверяет не то, что работает в бою.
- Bounds-check `if (id.x >= ...) return;` во всех ядрах, как везде в проекте.
- `FieldSampling.hlsl` подключать **не** обязательно; если подключаешь — не заводи собственные униформы с теми же именами.

### Шаг 2 — `FieldTestHarness`

Файл: `Assets/Tests/Editor/FieldTestHarness.cs`, `internal sealed class FieldTestHarness : IDisposable`.

Ориентировочная форма API (точные сигнатуры — уточнить по месту, сверяясь с реальными `FieldSet` / `SimContext` / `SimPass`; **не гадать**):

```csharp
internal sealed class FieldTestHarness : IDisposable
{
    // Allocate + ClearBoth + ExecuteCommandBuffer. Именно здесь закрывается ловушка 2.
    FieldTestHarness(params FieldDescriptor[] descriptors);

    // Дескриптор с произвольными format/resolution/size/clear — через идиому SetPrivate
    // из FieldSlotNamingTests либо через явную editor-only фабрику. Выбрать одно и обосновать.
    static FieldDescriptor Descriptor(
        string name, FieldSemantic semantic, GraphicsFormat format,
        Vector2Int resolution, Vector2 size, Color clear);

    SimContext Context { get; }

    // Production-библиотека для тестируемых пассов (пути как в M3DDemoTools.PassLibraryPaths).
    void LoadPassLibrary(params string[] computeAssetPaths);

    void SeedScalar(string field, float[] values);      // length == res.x * res.y
    void SeedVelocity(string field, Vector2[] values);

    float[] ReadScalar(string field);
    Vector2[] ReadVelocity(string field);
    float[] ProbeSampleLevel(string field, Vector2[] uvs);

    // Дословная копия SimulationWorld.Update + SwapPingPongFields, повторённая repeat раз.
    void RunPass(SimPass pass, float deltaTime, int repeat = 1);

    void Flush();

    // Допуски — константы харнеса по формату, не аргументы тестов (ADR-014 §6).
    static float RelativeTolerance(GraphicsFormat format);   // R16F → 1e-3, R32F → 1e-6
    void AssertApproximately(float[] obtained, float[] expected, GraphicsFormat format, string message);
}
```

`RunPass` обязан быть буквально:

```csharp
for (int i = 0; i < repeat; i++)
{
    pass.Execute(context, deltaTime);
    if (pass.LastExecuteDispatched)
    {
        IReadOnlyList<FieldRequest> writes = pass.FieldWrites;
        for (int w = 0; w < writes.Count; w++)
        {
            if (writes[w].Access == FieldAccess.WritePingPong)
            {
                fields.Swap(writes[w].FieldName);
            }
        }
    }
}
```

Если эта логика разойдётся с `SimulationWorld` — тесты будут валидировать фикцию. Сверить построчно.

`Seed*` заливает **`Current`** (для `WritePingPong` пасс читает Current и полностью перезаписывает Next, так что заливать оба не нужно). Если по ходу выяснится, что какому-то тесту нужны оба — добавить явный параметр, не менять поведение по умолчанию молча.

`Dispose` — освобождает `FieldSet`, все `GraphicsBuffer` харнеса и `CommandBuffer`. Проверить на отсутствие утечек RT между тестами (`FieldSet.Dispose` уже вызывает `SimField.Dispose`).

### Шаг 3 — три миграционных теста

Цель — перевести в автотесты три факта, уже подтверждённых вручную. Отчитываться **числами** (полученное / ожидаемое / Δ), как в ADR-013.

#### 3.1 `HarnessSamplerTests` — билинейный семплер (шаг 0 ADR-013)

Поле: Scalar `64×64`, `R16_SFloat`. Заливка: `value[i] = uv_center.x` для каждого текселя, то есть `((i % 64) + 0.5) / 64`.

Оракул тут особенно чистый: поле линейно по `u`, поэтому **корректная билинейная интерполяция обязана вернуть ровно `u` пробы**. Отдельные константы не нужны — ожидание есть сам аргумент. Point-фильтрация вернула бы центр ближайшего текселя, и это отличается на измеримую величину:

| Проба `u` | Ожидание (bilinear) | Ближайший центр | \|Δ\| до ближайшего |
| --- | --- | --- | --- |
| `16.75 / 64` = `0.26171875` | `0.26171875` | `16.5/64` = `0.2578125` | `3.90625e-3` (¼ текселя) |
| `17 / 64` = `0.265625` | `0.265625` | `16.5/64` и `17.5/64` | `7.8125e-3` (½ текселя) |

`v = 0.5` в обеих пробах. Числа совпадают с записанными в `Techdebt.md` (`0.26171880` / `0.26562500` — там округление отчёта).

`R16_SFloat` для этого теста достаточен и допуск ужимать не придётся: `16.5/64`, `17.5/64` и `16.75/64` представимы в half точно (мантиссы `1+1/32`, `1+3/32`, `1+3/64` — 5–6 бит). Если фактический результат это опровергнет — **сообщить числами**, не менять формат молча.

Дополнительно: прогнать обе пробы при `field.Current.filterMode = FilterMode.Point` **и** при `Bilinear` (`SimField.CreateRt` ставит `Bilinear`; `Techdebt` фиксирует измерение при `Point`). Ожидание — результаты идентичны, потому что интерполяцией управляет inline-семплер, а не `filterMode` текстуры. Это и есть содержательная часть проверки. Если результаты разойдутся — это находка, отчитаться отдельно и **не** править тест под неё.

#### 3.2 `HarnessDiffuseTests` — `DiffuseFieldPass`

Четыре утверждения, три из них — инварианты, не подогнанные числа:

1. **Сохранение суммы.** Neumann-clamp даёт нулевой поток через границу, лапласианы телескопируются в ноль, значит `Σ` сохраняется. Seed: дельта в центре. 10 итераций при `rate*dt = 0.2`. Проверить `Σ` до и после в пределах допуска формата.
2. **Максимум-принцип.** При `rate*dt ≤ 0.25` обновление `new = c(1−4r) + r(n+s+e+w)` — выпуклая комбинация, новых экстремумов не возникает. Проверить: `max` не растёт, `min` не падает.
3. **CFL как автотест.** Прогнать то же при `rate*dt = 0.5` и показать, что максимум-принцип **нарушается** (появляются значения вне исходного диапазона / знакопеременная шахматная мода). Это превращает эмпирическую формулировку ADR-006 («держи `rate·dt ≲ 0.2–0.25`») в проверяемое утверждение.
4. **Совпадение с CPU-эталоном** того же дискретного шаблона (`n+s+e+w−4c`, clamp по границам) на 10 итерациях. Сравнивать с дискретной реализацией, **не** с непрерывным аналитическим решением диффузии.

Симметрию отклика на симметричный вход добавить, если дёшево.

#### 3.3 `HarnessAdvectTests` — `AdvectVelocityFieldPass`

**Критично для воспроизводимости:** числа ADR-013 действительны только при `deltaTime * Resolution / Size == 1` (то есть шаг по времени равен размеру текселя в мире, `dt = h`). Проверка самосогласованности: пассивный baseline `1.7 × 8 = 13.6` — это 1.7 текселя за шаг при carrier `1.7`, что и означает `dt/h = 1`; целочисленный прогон с carrier `1` даёт 1 тексель за шаг → 8 текселей за 8 шагов, что совпадает с записанным `x: 20→28`. Пик переносится скоростью в **клетке-приёмнике** (обратный трейс), поэтому 8, а не 16 — это ожидаемо, не ошибка записи.

Зафиксировать `dt/h = 1` **явным `Assume.That` в setup теста** и записать выбранные `Size`/`Resolution`/`dt` в комментарий — в ADR-013 геометрия не записана, и это единственная причина, по которой числа сейчас невоспроизводимы.

Три случая:

| Случай | Вход | Ожидание |
| --- | --- | --- |
| Однородное поле | `(1,0)` везде | `max\|Δ\| = 0` **точно** (все обратные трассы дают одно и то же значение, `saturate` на границе тоже) |
| Целочисленный | bump `vx=2` на фоне `(1,0)`, 8 шагов | пик `x: 20 → 28`; значение пика `Δ = 0` (интерполяция вырождается в nearest) |
| Дробный | Gaussian `σ=1.5`, `amp=0.05` на carrier `1.7`, 8 шагов | `dCOM = +13.75` при пассивном baseline `13.6` (overshoot ≈1%) |

`dissipationRate = 0` во всех трёх. COM считать по добавке над carrier, а не по всему полю; пик на широком профиле скачет по целым текселям, поэтому оракул — **COM, не позиция пика** (это прямо указано в `pass-catalog.md`).

Случай `amp=1` (`dCom=+14.94`) в автотест **не** тащить: overshoot там 1.3 и зависит от формы профиля — плохой оракул. Достаточно `amp=0.05`.

### Шаг 4 — документация

- `DOC/pass-catalog.md`: одна строка в разделе Pass Library — `HarnessProbes.compute` тестовый, **в библиотеку не добавлять**.
- `DOC/status.md`: пункт про харнес.
- `Techdebt.md`: отметить, что численная проверка семплера и диффузии перешла из «измерено вручную» в автотест.

### Отчёт

1. Таблица obtained / expected / Δ по каждому из трёх тестов.
2. Какой механизм readback выбран (`GetData` по ADR-014 §3) и работает ли он в EditMode без нюансов; если пришлось отступить — что именно и почему.
3. Как решён вопрос создания `FieldDescriptor` с произвольными параметрами (reflection `SetPrivate` или фабрика) и почему.
4. Результат прогона `filterMode = Point` vs `Bilinear` в 3.1 (два числа, одинаковы или нет).
5. Записанная геометрия (`Size`, `Resolution`, `dt`) для 3.3.
6. Время прогона всего тест-сьюта до и после — чтобы понимать цену GPU-тестов.

### Вне скоупа

- PlayMode-тесты, `.asmdef`, CI.
- Харнес для particle-пассов (readback SoA-буферов, P2G) — отдельный тикет по факту потребности.
- Рефакторинг существующих 21 контрактных тестов — не трогать.
- Бенчмарки/перф-тесты.
- Любые правки production-кода. Если в процессе всплывёт баг в ядре — **зафиксировать в отчёте, не править здесь** (фиксы точечных дефектов идут отдельным тикетом F0.4).
