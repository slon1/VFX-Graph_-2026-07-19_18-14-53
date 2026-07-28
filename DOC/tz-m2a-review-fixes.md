# ТЗ: правки M2a по итогам код-ревью (валидация полей, дедупликация, контракты)

**Дата:** 2026-07-28
**Исполнитель:** Grok 4.5
**Контекст:** Milestone 2a (Field foundation) реализован и принят. Ревью выявило
4 правки. Все — малый риск, без изменения архитектуры. Перед началом прочитать
`DOC/architecture.md` (раздел «Resource-oriented principles (M2a)») и `DOC/status.md`.

**Конвенции:** код и комментарии — на английском, в стиле существующего кода
(см. `Assets/Scripts/Runtime/SimPass.cs`). Комментарии — только про неочевидные
намерения, без пересказа кода. Ноль аллокаций в горячем пути кадра.

---

## Задача 1 — Валидация каналов: exact для write, `>=` для read

**Проблема.** `ValidateFieldRequests` проверяет `ChannelCount >= MinChannels`.
Для UAV-записи это опасно: если пользователь переключит поле velocity на
`R16G16B16A16_SFloat` (4 ? 2 — проверка пройдёт), kernel с `RWTexture2D<float2>`
получит несовпадение layout — undefined behavior на Vulkan. Для SRV-чтения
(`SampleLevel`) лишние каналы легальны и отбрасываются.

**Что сделать:**

1. В `Assets/Scripts/Core/FieldDescriptor.cs`: переименовать
   `FieldRequest.MinChannels` ? `FieldRequest.Channels` (поле, свойство, параметр
   конструктора). XML-doc: «For writes the field format must have exactly this
   many channels (UAV layout must match the kernel declaration); for reads this
   is a minimum (extra channels are legal to sample).»
2. Обновить сигнатуру `FieldRequestSets.Single` (`Assets/Scripts/Runtime/SimPass.cs`)
   и все вызовы в `Assets/Scripts/Passes/FieldPasses.cs`.
3. В `SimulationWorld.ValidateFieldRequests` / `ValidateRequestList`
   (`Assets/Scripts/Runtime/SimulationWorld.cs`): передавать признак write/read.
   - Read-запросы: `descriptor.ChannelCount < request.Channels` ? ошибка (как сейчас).
   - Write-запросы (`WriteInPlace` / `WritePingPong`):
     `descriptor.ChannelCount != request.Channels` ? ошибка. Текст: имя пасса,
     имя поля, ожидаемое и фактическое число каналов, пояснение
     «UAV write requires exact channel count; change the field format or the pass».
4. Precision остаётся свободным: R16G16 ? R32G32 — оба валидны для `float2`.
   Ничего дополнительно не проверять.

## Задача 2 — Вынести дублированный push FieldParams в хелпер

**Проблема.** `FieldKernelPass.PushFieldParams` (SimPass.cs) и
`SampleVelocityFieldPass.SetParams` (FieldPasses.cs) — копипаста одного блока
(FieldResolution / FieldTexelSize / FieldOrigin / FieldAxisU / FieldAxisV / FieldSize).

**Что сделать:**

1. В `SimPass.cs` рядом с `SimShaderIds` добавить
   `internal static class FieldShaderParams` с методом
   `Push(CommandBuffer cmd, ComputeShader shader, FieldDescriptor descriptor)`.
   Kernel index не нужен — это uniform-ы шейдера, не текстуры.
2. `FieldKernelPass.PushFieldParams` и `SampleVelocityFieldPass.SetParams`
   переводятся на хелпер; сами блоки SetCompute*Param удалить.
3. Поведение бинарно идентично текущему (те же property id, та же нормализация
   axisU/axisV через `.normalized`).

## Задача 3 — Валидация: один пасс = одна система координат поля

**Проблема.** `FieldKernelPass` пушит FieldParams только primary-поля.
Multi-field пасс с полями на разных плоскостях/разрешениях сейчас отработает
тихо неверно. Per-field uniform-блоки НЕ делаем (преждевременно) — делаем
валидацию + документацию.

**Что сделать:**

1. В `SimulationWorld.ValidateFieldRequests`, per pass:
   - **Plane basis** (origin, axisU, axisV, size) всех полей пасса
     (reads + writes) должен совпадать с plane basis первого поля.
     Сравнение через `Vector3 ==` / `Vector2 ==` (встроенный epsilon Unity — ок).
   - **Resolution** должен совпадать у всех **write**-полей пасса (диспатч
     сайзится по primary). Read-поля могут иметь другое разрешение — чтение
     идёт по нормализованному UV (это канонический сценарий M2b: dye выше
     разрешением, чем velocity). НЕ требовать равенства resolution для read.
   - Ошибки — fail loudly с именем пасса и обоих полей.
2. Задокументировать правило в `DOC/getting-started.md` (раздел «Как добавить
   пасс ? Field») и одной строкой в `DOC/architecture.md`
   (Resource-oriented principles).

## Задача 4 — SimContext: убрать маскирующий null-coalescing

**Проблема.** В конструкторе `SimContext` (`Assets/Scripts/Runtime/SimContext.cs`):
`Fields = fields ?? new FieldSet()` — мёртвая ветка, маскирует ошибку сборки
пустым реестром вместо внятного падения.

**Что сделать:** заменить на
`Fields = fields ?? throw new ArgumentNullException(nameof(fields));`

## Опционально (если останется время) — клэмп в inject-кернеле

Ограничить magnitude в `TouchInjectVelocity` (FieldPasses.compute): uniform
`MaxFieldSpeed` (float, `[SerializeField, Min(0f)]` на
`TouchInjectVelocityFieldPass`, дефолт 20), после аккумуляции
`result = normalize-clamp по длине`. Отдельный `ClampFieldPass` НЕ делать —
это скоуп M2b.

---

## Явно НЕ делать

- **Не трогать** имя семплера `sampler_linear_clamp` — оно корректно
  (keyword-based inline sampler, тот же стиль, что `sampler_LinearClamp`
  в SRP Core `GlobalSamplers.hlsl`).
- Не делать per-field FieldParams (массивы uniform-блоков).
- Не делать отдельный `ClampFieldPass`.
- Не менять формат/семантику `FieldAccess`, механизм Swap, биндеры.

## Документация

Обновить: `DOC/status.md` (правила валидации каналов + координат),
`DOC/getting-started.md` (правило «один пасс — один plane; write-поля — одно
разрешение», exact channels для write), `DOC/architecture.md` (одна строка в
принципах). `DOC/capabilities.md` — только если что-то из видимого поведения
изменилось.

## Приёмка

1. Компиляция без ошибок и warning-ов (проверить `read_console` через Unity MCP).
2. Play: `HybridTouchField` — тач двигает частицы, velocity quad показывает след;
   `TwistedCube` — регрессия без изменений; Rebuild в Play Mode работает.
3. Негативные тесты (руками в инспекторе):
   - velocity ? `R16G16B16A16_SFloat`: Build падает с ошибкой про exact channels
     у Inject/Decay (write), с внятным текстом;
   - второе поле с другим plane basis в одном FieldKernelPass ? ошибка Build;
   - `SimContext` с null fields — недостижимо из World, но конструктор кидает
     `ArgumentNullException`.
4. Ноль новых аллокаций в кадре (не добавлять LINQ/`new` в Update-путь).