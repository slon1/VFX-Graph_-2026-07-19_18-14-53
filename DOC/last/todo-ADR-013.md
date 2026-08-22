## ТЗ для Grok — ADR-013

### Контекст

Прочитать ADR-013. Референсы: методология MCP-smoke уже трижды применялась (Gradient/Density/Diffuse — см. историю тикетов) — тот же формат отчёта (таблица before/after, числа, не общие слова). `DiffuseVelocityFieldPass`/`DiffuseVelocityField`-кернел (`FieldPasses.compute`, ADR-011) — структурный референс для нового `AdvectVelocityFieldPass` (тот же тип ресурса, та же `WritePingPong`-декларация, другая математика внутри).

### DoD

#### Шаг 0 — верификация семплера (сделать и отчитаться первым, до кода шага 1)

1. MCP-тест: `Scalar`-поле, произвольное разрешение (например `64×1` или `64×64`, на твоё усмотрение по удобству), `Clear`+ручная заливка `value = uv.x` (линейный градиент по одной оси).
2. Прочитать через существующий `sampler_linear_clamp` в контрольной точке **строго между** двумя соседними текселями (например, `uv.x = (i + 0.5) / Resolution.x` для текселя `i`, чуть сдвинутое дополнительно на полтекселя от центра, чтобы точно попасть в зону интерполяции, не на центр самого текселя).
3. Сравнить с аналитически ожидаемым линейно интерполированным значением.
4. **Отчитаться числами** (полученное значение, ожидаемое, разница) — не "работает"/"не работает" на словах.

**Если тест провалится**: переименовать `sampler_linear_clamp` → `linear_clamp_sampler` во всех текущих объявлениях (`FieldPasses.compute:22`, `GradientPasses.compute:13`), повторить тест, подтвердить исправление тем же численным способом.

**Если тест пройдёт**: явно отметить в отчёте, что семплер подтверждён корректным, закрыть этот пункт техдолга в `TechDebt.md` как решённый (не оставлять "открытым" молча).

#### Шаг 1 — `AdvectVelocityFieldPass` (только после подтверждения шага 0)

**C#** (`FieldPasses.cs`, рядом с `DiffuseVelocityFieldPass`):

csharp

```csharp
[Serializable]
public sealed class AdvectVelocityFieldPass : FieldKernelPass
{
    [SerializeField] private string fieldName = "flockVel"; // или другое velocity-поле по контексту использования
    [SerializeField, Min(0f)] private float dissipation = 0f; // опциональный множитель <1 для затухания при переносе, 0=выкл

    public override string DisplayName => "Advect Velocity Field";
    public override PassCategory Category => PassCategory.Transport;
    protected override string KernelName => "AdvectVelocityField";

    public override IReadOnlyList<FieldRequest> FieldWrites =>
        FieldRequestSets.Single(ref cache, fieldName, FieldAccess.WritePingPong, FieldSemantic.Velocity, 2);

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
        SetFloat(context, DissipationId, dissipation);
    }
}
```

Уточни по месту точную сигнатуру `SetParams`/кэш-поле по образцу уже существующего `DiffuseVelocityFieldPass` — не гадать, свериться с реальным кодом соседнего класса.

**HLSL** (тот же файл, что `DiffuseVelocityField` — `FieldPasses.compute`, тот же тип ресурса `Texture2D<float2>`/`RWTexture2D<float2>`, конфликта типов нет):

hlsl

```hlsl
#pragma kernel AdvectVelocityField

float Dissipation;

[numthreads(FIELD_THREADS, FIELD_THREADS, 1)]
void AdvectVelocityField(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FieldResolution.x || id.y >= (uint)FieldResolution.y) return;

    float2 uv = (float2(id.xy) + 0.5) * FieldTexelSize;
    float2 selfVel = FieldRead.SampleLevel(sampler_linear_clamp, uv, 0);

    // Backtrace: где была эта "частица жидкости" один шаг dt назад.
    float2 backUv = uv - (selfVel * DeltaTime) / FieldSize;
    backUv = saturate(backUv); // clamp к границе поля, не wraparound (Neumann-подобная граница, как везде в проекте)

    float2 advected = FieldRead.SampleLevel(sampler_linear_clamp, backUv, 0);
    FieldWrite[id.xy] = advected * (1.0 - Dissipation);
}
```

Свериться, что `FieldSize` доступен в этом файле как uniform (должен быть, судя по существующим кернелам, использующим мировые координаты) — не гадать, использовать то, что уже там объявлено.

### Тесты

1. **Контракт-тест** (по образцу `DiffuseVelocityFieldPassTests`) — `Category`, `KernelName`, `WritePingPong`/`Velocity`/`Channels=2`, дефолты.
2. **MCP numeric smoke**, обязателен (это первый advection-кернел в проекте, риск выше среднего, не понижать до "опционально"):
   - **Однородное поле** (`velocity` одинаков везде, например `(1,0)`) → после advection поле должно остаться приблизительно тем же самым (однородный перенос самого себя не должен ничего менять существенно) — проверка на отсутствие вырождения/артефактов на тривиальном случае.
   - **Локальный "сгусток" скорости** (например, вихрь или всплеск в одной области) → после N шагов advection всплеск должен физически **сместиться** в направлении, задаваемом самим полем (не остаться на месте, не размазаться симметрично, как это делал бы Diffuse) — прямая, решающая проверка того, что это именно перенос, а не диффузия под другим именем.
   - Оба сценария — с отчётом чисел (позиция/значение пика до и после), не только "выглядит нормально".

### Вне скоупа

Dye/tracer advection (multi-field). Pressure projection/Jacobi. Vorticity confinement. Wiring в конкретный `EffectAsset` — этот тикет про сам примитив и его корректность, не про демонстрацию.
