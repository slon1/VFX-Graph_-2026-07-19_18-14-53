#### Контекст для исполнителя

Продолжение M2b.1. При ревью реализации найдена и подтверждена архитектурная проблема (см. ADR-003 выше — прочитать перед началом). Кратко: HLSL-переменные для биндинга текстуры поля сейчас именуются по конкретному имени поля (`velocityRead`, `agentVelocityWrite`), из-за чего один и тот же кернел не может обслуживать разные поля — что уже привело к дублированию `DecayField`/`DecayAgentVelocity` и к необнаруживаемой ошибке в `NormalizeVelocityAccumPass` при смене `fieldName`.

#### Объём задачи (DoD)

1. Единая пара фиксированных property ID (`FieldRead`/`FieldWrite`) заменяет собой всё именование вида `{fieldName}Read/Write` во всех **single-field** кернелах и во всех местах C#, которые сейчас вычисляют такие ID динамически по имени поля.
2. Кернел `DecayAgentVelocity` удалён; `DecayField` обслуживает оба существующих поля (`velocity`, `agentVelocity`) без изменений в самом кернеле.
3. `DecayFieldPass.KernelName` возвращает константу `"DecayField"` без switch.
4. `NormalizeVelocityAccum`/`NormalizeFieldAccumPass` больше не завязаны на конкретное имя поля в HLSL — `fieldName` в инспекторе можно безопасно менять.
5. `SampleVelocityFieldPass` (та же болезнь: `velocityReadId = Shader.PropertyToID(velocityFieldName + "Read")`) исправлена тем же способом.
6. Все существующие демо (`TwistedCube`, `GalaxySwirl`, `ReactiveDust`, `HybridTouchField`, `AgentFieldEcho`) продолжают работать без изменений сериализованных данных на `EffectAsset` (проверить в Play Mode после рефакторинга — визуальное поведение не должно измениться).
7. Добавлен/расширен EditMode-тест: `NormalizeVelocityAccumPass`/`DecayFieldPass` с произвольным (не `"velocity"`/`"agentVelocity"`) `fieldName`, объявленным в `EffectAsset.Fields`, корректно биндит и пишет в это поле — регрессионный тест именно на найденную дыру.

#### Детальная спецификация

##### 1. `SimShaderIds` — добавить два фиксированных ID

csharp

```csharp
internal static class SimShaderIds
{
    // ... существующие поля без изменений ...

    public static readonly int FieldRead = Shader.PropertyToID("FieldRead");
    public static readonly int FieldWrite = Shader.PropertyToID("FieldWrite");
}
```

##### 2. `FieldKernelPass.CollectFieldBinds` — генерик-биндинг

csharp

```csharp
private void CollectFieldBinds(IReadOnlyList<FieldRequest> requests)
{
    for (int i = 0; i < requests.Count; i++)
    {
        FieldRequest request = requests[i];
        fieldBinds.Add(new FieldBind
        {
            FieldName = request.FieldName,
            Access = request.Access,
            ReadId = SimShaderIds.FieldRead,   // было: Shader.PropertyToID(name + "Read")
            WriteId = SimShaderIds.FieldWrite, // было: Shader.PropertyToID(name + "Write")
        });
    }
}
```

**Важно:** `FieldBind.ReadId`/`WriteId` теперь одинаковы для любого `FieldRequest` — это осознанно (см. ограничение скоупа в ADR). `ValidateAccessConflicts` (уже существует) продолжает защищать от двух разных `FieldAccess` на одно и то же поле в одном пассе — этого достаточно, пока пасс работает с одним полем. Если в будущем (M2c) кто-то объявит **два разных поля** в `FieldReads`/`FieldWrites` одного `FieldKernelPass` — с текущим фиксированным ID оба попытаются забиндиться в один и тот же слот и один перезапишет другой. Это ожидаемое, известное ограничение текущего шага — добавить explicit guard в `Initialize`:

csharp

```csharp
if (FieldReads.Count + FieldWrites.Count > 1 &&
    RequiresDistinctSlots(FieldReads, FieldWrites)) // t.e. более одного уникального FieldName
{
    throw new InvalidOperationException(
        $"{DisplayName}: FieldKernelPass with generic FieldRead/FieldWrite slots supports exactly " +
        "one distinct field name per pass. Multi-field-per-kernel passes need index-based slots " +
        "(M2c, not yet implemented).");
}
```

(Реализовать `RequiresDistinctSlots` как проверку, что все `FieldName` среди `FieldReads`+`FieldWrites` совпадают — это защитит от тихой поломки при будущей ошибке конфигурации, а не только задокументирует ограничение в комментарии.)

##### 3. `NormalizeFieldAccumPass.Initialize` — заменить вычисление `writeId`

csharp

```csharp
// Было:
// writeId = Shader.PropertyToID(FieldName + "Write");

// Стало:
writeId = SimShaderIds.FieldWrite;
```

##### 4. `SampleVelocityFieldPass.Initialize` — тот же фикс

csharp

```csharp
// Было:
// velocityReadId = Shader.PropertyToID(velocityFieldName + "Read");

// Стало:
velocityReadId = SimShaderIds.FieldRead;
```

(Дальше в `SetParams` биндинг остаётся как есть — просто через новый `velocityReadId`.)

##### 5. `DecayFieldPass.KernelName` — убрать switch

csharp

```csharp
public override string DisplayName => "Decay Field";
public override PassCategory Category => PassCategory.Transport;
protected override string KernelName => "DecayField"; // без switch, DecayAgentVelocity удалён
```

##### 6. HLSL: `FieldPasses.compute`

- Переименовать глобальные объявления `velocityRead`/`velocityWrite` → `FieldRead`/`FieldWrite` (сохранить типы: `Texture2D<float2> FieldRead;` и `RWTexture2D<float2> FieldWrite;`).
- Все использования внутри `TouchInjectVelocity`, `DecayField`, `SampleVelocityField` — заменить `velocityRead`/`velocityWrite` на `FieldRead`/`FieldWrite` соответственно.
- **Удалить** кернел `DecayAgentVelocity` целиком и связанные с ним отдельные объявления `agentVelocityRead`/`agentVelocityWrite`.
- Убрать `#pragma kernel DecayAgentVelocity`.

##### 7. HLSL: `P2GPasses.compute`

- В `NormalizeVelocityAccum` переименовать `RWTexture2D<float2> agentVelocityWrite;` → `RWTexture2D<float2> FieldWrite;`, и использование `agentVelocityWrite[id.xy]` → `FieldWrite[id.xy]`.

##### 8. Проверка каналов при generic-биндинге

Раз один и тот же `FieldRead`/`FieldWrite` в HLSL объявлен с конкретным типом (`float2` для velocity-подобных кернелов) — существующая exact-channel валидация (`descriptor.ChannelCount == request.MinChannels`/`Channels`) уже гарантирует, что пасс с `Channels=2` не свяжется с 1- или 4-канальным полем. Никаких дополнительных проверок не требуется — просто явно подтвердить в комментарии над `CollectFieldBinds`, что exact-channel валидация — это то, что делает генерик-биндинг безопасным (иначе `FieldWrite` типа `float2` мог бы получить несовместимую по layout текстуру).

#### Тест-кейсы

1. Все существующие 5 демо-эффектов запускаются в Play Mode без визуальных изменений (регрессия).
2. Новый `EffectAsset` (можно временный, только для теста) с полем `testField` (RG16, 2 канала) + `DecayFieldPass { FieldName = "testField" }` — decay корректно работает без необходимости писать новый кернел.
3. `NormalizeVelocityAccumPass { fieldName = "testField" }` (при соответствующем P2G-пайплайне) — записывает decoded-значение именно в `testField`, не тихо теряет данные.
4. `FieldKernelPass`-наследник, объявляющий два **разных** имени поля одновременно в `FieldReads`/`FieldWrites` (искусственный тестовый пасс) → `Initialize` кидает понятную ошибку (guard из пункта 2), а не тихо ломает биндинг.
5. Прогнать существующие EditMode-тесты `FieldAccumPassValidatorTests` — не должны сломаться (валидация состояний/каналов не зависит от механизма биндинга текстур).

#### Вне скоупа (явно)

- Multi-field-per-kernel биндинг (index/role-based слоты для будущего Gray-Scott U+V в одном dispatch) — отдельная задача M2c.
- Любые изменения в `FieldAccumPassValidator`, `FieldAccumBuffer`, семантике P2G scatter/normalize (encode/decode, state machine) — не трогать, это уже принято и работает корректно.
