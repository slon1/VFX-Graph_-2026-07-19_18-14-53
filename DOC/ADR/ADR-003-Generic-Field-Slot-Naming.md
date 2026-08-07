### ADR-003: Generic Field Slot Naming вместо `{fieldName}Read/Write`

**Статус:** Реализовано (M2b.1.1)  
**Дата:** 2026-08-02  
**Контекст:** M3D Framework, после M2b.1

#### Контекст

`FieldKernelPass.CollectFieldBinds` (и, отдельно, `NormalizeFieldAccumPass.Initialize`, `SampleVelocityFieldPass.Initialize`) вычисляют property ID для биндинга текстуры поля как `Shader.PropertyToID(fieldName + "Read")` / `Shader.PropertyToID(fieldName + "Write")` — то есть **имя HLSL-переменной в кернеле обязано буквально совпадать с именем конкретного поля** (`velocityRead`, `agentVelocityWrite` и т.д.).

Пока существовало одно поле (`velocity`), это было незаметно. С появлением `agentVelocity` в M2b.1 это уже дало реальные последствия:

- `FieldPasses.compute` получил дублирующий кернел `DecayAgentVelocity` — копию `DecayField` с переименованными переменными (`agentVelocityRead`/`agentVelocityWrite` вместо `velocityRead`/`velocityWrite`), потому что один и тот же кернел не может обслуживать поле с другим именем.
- `DecayFieldPass.KernelName` получил string-switch (`fieldName == "agentVelocity" ? "DecayAgentVelocity" : "DecayField"`), не масштабируемый на третье/четвёртое поле.
- `NormalizeFieldAccumPass`/`NormalizeVelocityAccumPass` **не получили** аналогичного switch — `KernelName` захардкожен константой, `fieldName` при этом остаётся editable-полем в инспекторе. Это реальная, не гипотетическая дыра: смена `fieldName` на что-либо кроме `"agentVelocity"` даёт молчаливый no-op биндинг (Unity не ошибается на неизвестное имя property) — normalize-пасс отработает, ничего не запишет, без единого сообщения об ошибке.

При добавлении Gray-Scott (2 новых поля) или Lenia эта схема потребовала бы ещё 2-4 дублирующих кернела и растущие switch-конструкции в каждом C#-классе, ссылающемся на поле по имени.

#### Решение

Заменить именование HLSL-переменных с `{fieldName}Read`/`{fieldName}Write` на **фиксированные, не зависящие от имени поля** идентификаторы: `FieldRead` / `FieldWrite`. Один и тот же скомпилированный кернел (`DecayField`, `NormalizeVelocityAccum`, `SampleVelocityField` и т.д.) становится применим к **любому** полю с совместимым числом каналов, без копий и без string-switch на стороне C#.

**Явное ограничение скоупа**: это решение покрывает только **single-field-per-kernel** пассы — ровно то, что существует в проекте сегодня (у каждого существующего пасса ровно один `FieldRequest` на кернел). Пассы, которым в будущем понадобится **одновременно** несколько разных полей в одном dispatch (например, будущая Gray-Scott-реакция U+V в одном кернеле) — это отдельная, более сложная задача (index/role-based слоты типа `Field0Read`/`Field1Read`), явно вне скоупа этого ADR и roadmap-пункта M2c ("Multi-field validation в FieldKernelPass"). Не пытаться решить обе задачи одним PR.

#### Механика

`FieldAccess` уже определяет, какие слоты нужны:

- `Read` → только `FieldRead` (SRV).
- `WriteInPlace` → только `FieldWrite` (UAV, читается/пишется как один и тот же `Current`).
- `WritePingPong` → оба: `FieldRead` (SRV на `Current`) и `FieldWrite` (UAV на `Next`).

Это уже ровно то, что делает `FieldKernelPass.Execute` (switch по `bind.Access` из `FieldBind`) — меняется только то, **чему** равны `ReadId`/`WriteId`: не `Shader.PropertyToID(name+"Read"/"Write")`, а фиксированные `SimShaderIds.FieldRead`/`SimShaderIds.FieldWrite`.

#### Последствия

**Плюсы:**

- Удаляется дублирующий кернел `DecayAgentVelocity` и string-switch в `DecayFieldPass.KernelName`.
- Закрывается реальная дыра в `NormalizeFieldAccumPass` (смена `fieldName` больше не может тихо сломать биндинг).
- Любой новый field-пасс (Diffuse из M2b.3, будущий Gray-Scott per-U/per-V single-field шаг) автоматически переиспользует существующие кернелы для новых полей без единой строчки нового compute-кода.

**Минусы / явно принятые ограничения:**

- Не решает multi-field-per-kernel сценарий (см. выше) — сознательно.
- Требует правки во всех местах, где сейчас вычисляется `name + "Read"/"Write"` (перечислены в ТЗ ниже) — единоразовая, но затрагивает несколько файлов.
- Публичный API (сериализуемые классы пассов, их поля в инспекторе) не меняется — миграции существующих `EffectAsset` не требуется.
