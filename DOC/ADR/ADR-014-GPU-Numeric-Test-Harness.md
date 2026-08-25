## ADR-014: GPU Numeric Test Harness

**Статус:** Принято (реализовано)
**Дата:** 2026-08-23
**Контекст:** M3D Framework, фаза 0 подготовки к Stable Fluids (F0.2)
**ТЗ:** [`todo-ADR-014.md`](../last/todo-ADR-014.md)

### Контекст

Численная верификация ядер в проекте не автоматизирована. 21 тестовый файл в `Assets/Tests/Editor` проверяет **декларации**: `DisplayName`, `Category`, `KernelName` через reflection, `FieldAccess`/`Semantic`/`Channels`/`Role`, дефолты сериализованных полей. Это полезный слой — он закрывает класс молчаливых no-op биндингов, найденный в ADR-003. Но `grep` по `AsyncGPUReadback|GetData|ReadPixels|[UnityTest]` в `Assets/Tests` даёт нулевой результат: ни один тест не делает `DispatchCompute` и не читает результат обратно.

Вся физика проверена вручную через MCP и зафиксирована в markdown: bilinear-семплер (ADR-013 шаг 0, числа в `Techdebt.md`), сходимость `DiffuseField` (ADR-006), sum-декод density (ADR-005, `ratio=5.000`), COM адвекции (ADR-013). `ProjectSummary.md` урок 7 сам формулирует вывод: «численный MCP-readback тест ловит баги лучше визуальной проверки». Ручной прогон подтверждает факт **один раз**; регрессию в нём никто не заметит.

Прямая мотивация закрыть это сейчас, а не «когда-нибудь»: единственный осмысленный критерий корректности проекции — машинное сравнение `max|D|` до и после цепочки Divergence→Jacobi→Subtract. Исходная формулировка «на порядок» была стремлением F1; после замера калиброванный DoD — [ADR-020 §3](ADR-020-Subtract-Phi-Gradient-Pass.md) (k=8, ≥3×). Это не проверяется глазами по debug-quad и не проверяется однократным ручным прогоном: и число итераций, и разрешение, и precision полей будут меняться при калибровке пресета, а вместе с ними — и результат. Без харнеса F1.3 (`SubtractPhiGradientPass`) — тикет без DoD.

Вторичная мотивация: DoD следующего тикета (ADR-015, World-owned repeat loop) сформулирован как «пасс с `RepeatCount=6` численно совпадает с 6 экземплярами в списке». Это тоже требует readback.

Задача не «построить тест-инфраструктуру с нуля», а **вынести и обобщить то, что в тестах уже есть**: `FieldSlotNamingTests` уже собирает реальный `FieldSet` и реальный `SimContext`, `SeedScalarDiskPassTests` уже грузит реальный `.compute` через `AssetDatabase` и вызывает `Initialize`. Не хватает ровно трёх вещей: заливки поля известным паттерном, чтения поля обратно и цикла «Execute + Swap», идентичного World.

### Решение

#### 1. EditMode, без `.asmdef`

Тесты остаются в `Assembly-CSharp-Editor`. `Assets/Scripts/AssemblyInfo.cs` содержит:

```csharp
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
```

а `FieldSlotNamingTests` обращается к `internal`-типу `SimShaderIds`. Добавление `.asmdef` в `Assets/Tests` перевело бы тесты в отдельную сборку и **молча** отрезало доступ к internals — харнесу они нужны (`SimShaderIds`, `FieldShaderParams`). PlayMode-тесты не нужны: compute-диспатч и readback работают в EditMode, а `[UnityTest]` добавил бы domain reload и отдельную тестовую сборку без выигрыша.

#### 2. Отдельный test-only compute, не в Pass Library

`Assets/Tests/Editor/Shaders/HarnessProbes.compute` — **не добавляется** в `M3DDemoTools.PassLibraryPaths`. Причина: production-библиотека компилируется в билд и является контрактом (`pass-catalog.md` перечисляет её как чеклист); debug-ядра в ней — мусор на мобильном билде и лишняя строка в чеклисте онбординга.

Ядра разделены по числу каналов, а не параметризованы. Это следует уже принятому в проекте правилу (ADR-004, ADR-005: «не смешивать `float` и `float2` в одном файле — typed-bind UB»); типизированный UAV обязан соответствовать объявлению кернела:

| Кернел | Назначение |
| --- | --- |
| `FillScalarFromBuffer` | `StructuredBuffer<float>` → `RWTexture2D<float>` — заливка Scalar-поля |
| `FillVelocityFromBuffer` | `StructuredBuffer<float2>` → `RWTexture2D<float2>` — заливка Velocity-поля |
| `ReadScalarToBuffer` | `Texture2D<float>` (SRV, `Load`) → `RWStructuredBuffer<float>` |
| `ReadVelocityToBuffer` | `Texture2D<float2>` (SRV, `Load`) → `RWStructuredBuffer<float2>` |
| `ProbeSampleLevelScalar` | `SampleLevel(sampler_linear_clamp, uv[i], 0)` по списку UV → буфер |

Чтение идёт через **SRV + `Load`**, не через UAV: это снимает зависимость от опциональной поддержки типизированной UAV-загрузки для не-`R32` форматов (тот самый риск, на который сейчас опирается `NormalizeVelocityAccum`).

`ProbeSampleLevelScalar` существует ровно для того, чтобы шаг 0 ADR-013 стал автотестом: измерить билинейную интерполяцию в точке **между** текселями иначе нельзя — ни `Load`, ни readback текстуры этого не дают.

#### 3. Readback через `GraphicsBuffer.GetData`, не `AsyncGPUReadback` и не `ReadPixels`

`GetData<T>` синхронен по контракту, форматно-агностичен (тип элемента буфера выбираем мы) и не трогает `RenderTexture.active`. Последнее принципиально: `Texture2D.ReadPixels` требует установки `RenderTexture.active` — ровно тот механизм, который в `SimField.ClearOne` порождает предупреждение `Releasing render texture that is set to be RenderTexture.active!` (Techdebt A1, фикс в F0.4). Тестовый харнес не должен воспроизводить диагностируемый им же баг.

`AsyncGPUReadback` + `WaitAllRequests` работоспособен, но требует матрицы конверсий «формат поля → формат readback» и даёт то же самое. Стоимость `GetData` (полный GPU-sync) в тестах нерелевантна.

#### 4. Харнес исполняет CommandBuffer явно

Существующий хелпер `FieldSlotNamingTests.AllocateFields` создаёт `CommandBuffer`, передаёт его в `FieldSet.Allocate` (который пишет туда `ClearBoth`) и релизит **не исполнив**. Для контрактных тестов это безвредно, для численных — нет: поля содержат неопределённое содержимое, и любое утверждение про `ClearValue` бессмысленно. Харнес владеет одним `CommandBuffer` и имеет явный `Flush()`; `GetData` дополнительно форсирует синхронизацию.

#### 5. Цикл «Execute + Swap» повторяет контракт World дословно

```
for i in 0 .. repeat-1:
    pass.Execute(context, deltaTime)
    if pass.LastExecuteDispatched:
        foreach w in pass.FieldWrites where w.Access == WritePingPong:
            fields.Swap(w.FieldName)
```

Это буквальная копия `SimulationWorld.Update` + `SwapPingPongFields`. Если харнес свапает иначе, чем World, тесты валидируют фикцию. Побочная польза: цикл, который вводит ADR-015, здесь уже написан и протестирован — DoD ADR-015 сводится к «World делает то же, что харнес».

#### 6. Допуски — константы харнеса по формату, не магические числа в тестах

| Формат | Мантисса | Относительный допуск |
| --- | --- | --- |
| `R16_SFloat` / `R16G16_SFloat` | 10 бит | `1e-3` |
| `R32_SFloat` / `R32G32_SFloat` | 23 бита | `1e-6` |

Билинейная фильтрация и порядок операций с плавающей точкой зависят от вендора GPU, поэтому допуск обязан выживать смену устройства. Тест, которому нужен допуск строже пола формата, спроектирован неверно — это сигнал сменить формат поля в тесте, а не ужать допуск.

#### 7. Что утверждается: инварианты и CPU-эталон, не непрерывное аналитическое решение

Для `DiffuseField` проверяются: сохранение суммы (Нейман не имеет потока через границу), максимум-принцип (новых экстремумов не возникает), симметрия отклика на симметричный вход, и совпадение с CPU-реализацией **того же дискретного шаблона**.

Сравнение дискретного ядра с непрерывным решением смешивает две разные ошибки — баг в ядре и ошибку дискретизации — и даёт допуск, который невозможно защитить. Числа из ручных MCP-прогонов (ADR-013, `Techdebt`) используются как **чеклист миграции** (три именованных теста), инварианты — как постоянная сеть.

### Отклонённые варианты

**PlayMode-тесты** (`[UnityTest]` + yield). Требуют `.asmdef` + отдельную PlayMode-сборку, ломают доступ к internals (см. §1), добавляют domain reload. Compute-диспатч в EditMode работает — выигрыша нет.

**Debug-ядра в production `.compute`-файлах.** Засоряют Pass Library, попадают в мобильный билд, добавляют строки в чеклист `pass-catalog.md`.

**Только сверка с записанными MCP-числами.** Числа из ADR-013 — цель миграции, но как единственный оракул они делают тесты неподдерживаемыми: при смене разрешения или параметров пресета все константы разъезжаются, и тест становится «перезапиши ожидание». Отсюда §7.

**Параметризованные по каналам fill/read-ядра.** Типизированный UAV/SRV обязан совпадать с объявлением; обход через `RWTexture2D<float4>` на RG16F — это UB, от которого проект уже дважды уходил (ADR-004, ADR-005).

### Последствия

- (+) Закрывает разрыв, из-за которого F1.3 (`SubtractPressureGradientPass`) не имеет проверяемого DoD, а вместе с ним — вся проекция давления.
- (+) DoD ADR-015 (repeat loop) становится машинно-проверяемым.
- (+) Три уже подтверждённых вручную факта перестают зависеть от того, что их однажды измерили.
- (−) Тесты начинают трогать GPU: медленнее и в принципе могут падать на драйверных особенностях. Смягчено допусками от формата (§6) и инвариантами вместо точных значений (§7).
- (−) Один test-only `.compute`-файл, который обязан остаться вне Pass Library. Риск — что его туда добавят «за компанию»; поэтому запрет вынесен в текст ADR и в `pass-catalog.md`.
- Харнес намеренно **не** покрывает particle-пассы (readback SoA-буферов, P2G). Отдельный тикет по факту потребности.

### Очередь после этого тикета

| ADR | Тикет | Скоуп одной строкой |
| --- | --- | --- |
| ADR-015 | F0.1 | World-owned repeat loop: `SimPass.RepeatCount` (virtual, default 1), цикл `Execute + Swap` в World, `Execute` остаётся `sealed`. Дополнение к ADR-001 §3 |
| ADR-016 | F0.3 | Единицы по семействам пассов: RD/boids — texel-Laplacian, Gradient — UV, fluid-контур — world; масштабирование `D = div·h`, `Q = q/h` |
| ADR-018 | F1.2 | JacobiPhiPass: multi-role (fluidPhi/fluidD), RepeatCount-решатель, DoD на невязке |
| ADR-020 | F1.3 | SubtractPhiGradientPass: WriteInPlace velocity − ∇Φ; цепочка Divergence→Jacobi→Subtract, DoD k=8 ≥3× |
| ADR-021 | F1.4 | SolidWallVelocityPass: u·n = 0 на рамке после Subtract, free-slip |
| ADR-022 | F1.6 | Fluid2D EffectAsset: Touch → project → wall → advect → wall; калибровка Jacobi/Bias |
| ADR-023 | F1.7 | AdvectScalarPass: пассивный dye по velocity; Seed + проводка Fluid2D |
| ADR-019 | F1 сводка | Fluid2D solver, постфактум: collocated, Jacobi×40, precision, BC; known limitation — **несогласованность дискретных операторов div/grad/Jacobi** ([ADR-019](ADR-019-Fluid2D-Solver.md); замер ADR-020 §3) |
