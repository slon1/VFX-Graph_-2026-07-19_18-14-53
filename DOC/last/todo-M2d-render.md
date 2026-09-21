## ТЗ для программиста — M2d рендер частиц (Techdebt 9, `PrimitiveParticleBinder`)

**Закрыто 2026-09-21.** ADR-030 **Реализовано**. Desktop: оба режима ~20 FPS (Windows Editor). S10: VFX 20–30, Primitive 40–50. `Boids_mk1` на диске **Vfx**. Правка 2026-09-21 (разбор ТЗ, код не писали): найден блокер — `SetupBinders` рисовал бы **два** облака одновременно (VFX + Primitive), если просто добавить биндер рядом; найден двойной `Dispose()` у `fieldDebugQuadsBinder`; исправлены дефолты `_Size`/`_Color` (были бы невидимы/переполнены fill-rate'ом); шейдер переведён на `Transparent` (не `Opaque` — иначе перф-сравнение о другом); формула билборда сведена к одной (`UNITY_MATRIX_I_V`, не `UNITY_MATRIX_V`); добавлена защита от stripping шейдера в Player-билде; смоук разбит на два теста. Шаг 5 (Update-loop вызывает `Execute` у всех `binders`) — закрыт чтением, патч не нужен. ADR: [ADR-030](../ADR/ADR-030-Particle-Render-Primitives-Binder.md), читать целиком, особенно §3 (блокер VFX), §3a (двойной Dispose).

Скоуп сужен явно (не путать с исходным roadmap M2d): только производительность рендера частиц. **Не** LUT-палитра, **не** trail/persistence — тот roadmap-пункт остаётся отдельным.

Зафиксировано — не пересматривать:

1. **`VfxParticleBinder` не трогать.** Новый класс рядом, не замена. Существующие `.asset` (default `ParticleRenderMode.Vfx`) не меняют поведение.
2. **API — `Graphics.RenderPrimitives`, не `RenderMeshIndirect`.** Причина в ADR-030 §1: нет emitters/dead-pool, GPU не решает instance count динамически — indirect-args буфер не нужен. Не заводить `GraphicsBuffer.Target.IndirectArguments` в этом тикете.
3. **Ноль CPU-readback в кадре.** Не `GraphicsBuffer.GetData` на позициях. `_Positions` — тот же `GraphicsBuffer`, что уже возвращает `context.Particles.Get(BuiltinAttributes.Position)`, биндится напрямую через `MaterialPropertyBlock.SetBuffer`.
4. **Первый потребитель — только `Boids_mk1.asset`.** `HybridTouchField`/`AgentFieldEcho`/`Gray-Scott-Boids`/`Gray-Scott-Agents` не трогать, не переключать.
5. **Визуальная парность приблизительная.** Не разбирать узлы `ParticleBufferVFX.vfx` ради точного повтора цвета/размера — у него нет экспонированных параметров кроме `SpawnCount`/`PositionBuffer`. `_Color`/`_Size` — поля на `EffectAsset` (Шаг 1), не вычисленные значения.
6. **Не ориентировать по `heading`/`velocity`.** Camera-facing billboard, без направления движения. Отдельный тикет, если понадобится.
7. **При `Primitive` — не создавать `VfxParticleBinder`, глушить VFX-плейбэк.** `SetupBinders` при `particles.Count > 0` сегодня безусловно ставит `VfxParticleBinder` — если просто добавить `PrimitiveParticleBinder` рядом, оба рисуют одновременно, и A/B (и особенно мобильный FPS-тест) окажутся не про «VFX vs Primitive». `Build()` всё равно требует `visualEffect != null` — компонент **не** удалять, `Primitive`-ветка ставит `SpawnCount=0` + `Reinit()` (как в существующей field-only ветке). Ослаблять требование `Build()` к `VisualEffect` — **не в этом тикете**.
8. **`SimulationWorld.Teardown` — точечный фикс disposal, с исключением.** Цикл по `binders` на `IDisposable` **до** `binders.Clear()`, по образцу существующего цикла у `passes` в том же методе. `fieldDebugQuadsBinder` уже лежит и в спец-поле, и в списке `binders` (`binders.Add(fieldDebugQuadsBinder)` в `SetupBinders`) — цикл **обязан** пропускать этот инстанс (`!ReferenceEquals(binders[i], fieldDebugQuadsBinder)`), иначе `Dispose()` вызовется дважды. Существующую пару строк `fieldDebugQuadsBinder?.Dispose(); fieldDebugQuadsBinder = null;` не трогать.
9. **Никакого мобильного gate по образцу `M3DVolumeMobileGate`.** Не нужен — `Primitive`-режим и есть облегчённый путь, не «тяжёлая фича, которую надо выключать на мобиле».
10. **DoD — функциональный/визуальный/перф, не численный assert.** Здесь нет формулы для gate, как в fluid-тикетах — не выдумывать метрику толщины/яркости.
11. **`PrimitiveParticleBinder` реализует `IDisposable`** — освобождает созданный `Material`. В Editor-контексте — `DestroyImmediate`, не `Destroy` (иначе объект живёт до конца кадра, и проверка «материал освобождён» в EditMode-тесте соврёт), как коллайдер в `FieldDebugQuadsBinder.Initialize` (`Application.isPlaying ? Destroy : DestroyImmediate`).
12. **`_Size`/`_Color` — поля на `EffectAsset`, не хардкод-дефолт в конструкторе биндера.** Иначе «настройка в инспекторе» ничего не значит и теряется на `Rebuild`. **Дефолт размера — `0.05`**, не `0.5`: `Boids_mk1` (`resolution=50`, `cubeSize=2`) даёт шаг решётки `2/50=0.04` на 125 000 частиц — квад `0.5` перекрывает соседей на порядок и сам создаёт fill-rate-нагрузку, искажающую перф-сравнение. **Дефолт цвета — `Color.white`** (непрозрачный), не `default(Color)` (нулевая альфа → облако невидимо → ложное «точки исчезли» на DoD).
13. **Шейдер — `Transparent`, не `Opaque`.** `FieldDebug.shader` и типовой VFX Graph рендерят с блендингом; `Opaque` дал бы desktop/mobile FPS-выигрыш частично или полностью за счёт отсутствия alpha-blending и early-Z — другая гипотеза, не та, что тестирует Techdebt 9.
14. **Формула билборда — одна, через `UNITY_MATRIX_I_V`** (camera-to-world; столбцы — мировые оси камеры без двусмысленности строка/столбец, которая была у `UNITY_MATRIX_V`-варианта). Визуальная проверка в Play всё равно обязательна.
15. **Шейдер должен попадать в билд.** `Shader.Find("M3D/ParticleBillboard")` в раннере — на устройстве шейдер, на который нет прямой ссылки от компонента, может быть выстрижен стриппингом. Зарегистрировать в Project Settings → Graphics → **Always Included Shaders** (или через `GraphicsSettings.SetShaderMode`/editor-скрипт, как `PostProcessingSetup.cs` трогает project settings) — сделать это частью Шага 6, не полагаться на ручное действие оператора.
16. **Шаг 5 (Update-loop) закрыт чтением, патч не нужен.** `binders[i].Execute(context)` уже в цикле `SimulationWorld.Update` сразу после `Graphics.ExecuteCommandBuffer(commandBuffer)` — `PrimitiveParticleBinder.Execute` будет вызываться каждый кадр без правок цикла.

Референсы: `FieldDebugQuadsBinder.cs` (стиль материала/шейдера, `Shader.PropertyToID`, `Application.isPlaying ? Destroy : DestroyImmediate`); `FieldDebug.shader` (URP `HLSLPROGRAM`-стиль, `Transparent`-теги, `Blend`/`ZWrite`/`Cull`); `VfxParticleBinder.cs` (текущий контракт `IRenderBinder` для частиц, паттерн `SpawnCount=0`+`Reinit()`); `Adr012BoidsMk1Setup.cs` (стиль one-shot `SerializedObject`-патча ассета); `SimulationWorld.cs` (`SetupBinders` ~строка 530, `Teardown` ~615, `BuiltinAttributes.Position`, `Update` ~110).

Имена: класс `PrimitiveParticleBinder`, шейдер `M3D/ParticleBillboard`, enum `ParticleRenderMode { Vfx, Primitive }` + поля `particleSize`/`particleColor` на `EffectAsset`, меню `Tools/M3D/Enable Primitive Render On Boids_mk1` (+ `Disable...`).

---

### Шаг 1 — `EffectAsset.ParticleRenderMode` + размер/цвет

```csharp
public enum ParticleRenderMode { Vfx = 0, Primitive = 1 }

[SerializeField] private ParticleRenderMode particleRenderMode = ParticleRenderMode.Vfx;
[SerializeField, Min(0f)] private float particleSize = 0.05f;
[SerializeField] private Color particleColor = Color.white;

public ParticleRenderMode ParticleRenderMode => particleRenderMode;
public float ParticleSize => particleSize;
public Color ParticleColor => particleColor;
```

Дефолт **`Vfx`** — обязательно, иначе все существующие ассеты молча переключатся. Дефолт `particleSize=0.05` — порядок шага решётки `Boids_mk1` (`2/50=0.04`), не «средний квад», это конкретная калибровка под конкретную демку; если позже появится вторая `Primitive`-демка с другим particle count/размером поля, значение крутится на её ассете отдельно, не глобальной константой. `particleColor` — непрозрачный дефолт (`Color.white`, `a=1`). `EditorConfigure` (используется `M3DDemoTools`) не обязан принимать новые параметры — дефолты покрывают вызовы без них.

---

### Шаг 2 — шейдер `M3D/ParticleBillboard`

`Assets/Shaders/GPU/ParticleBillboard.shader` (рядом с `FieldDebug.shader`, не внутри `Assets/Shaders/GPU/Passes/` — это render-шейдер, не compute pass). URP `HLSLPROGRAM`, тот же стиль, что `FieldDebug.shader`.

```hlsl
Shader "M3D/ParticleBillboard"
{
    Properties
    {
        _Size ("Size", Float) = 0.05
        _Color ("Color", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float3> _Positions;   // ParticleSet.Position, тот же GraphicsBuffer, без копии
            float _Size;
            float4 _Color;

            static const float2 QuadOffsets[6] = {
                float2(-0.5,-0.5), float2( 0.5,-0.5), float2(-0.5, 0.5),
                float2(-0.5, 0.5), float2( 0.5,-0.5), float2( 0.5, 0.5),
            };

            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                float3 centerWS = _Positions[instanceID];
                float2 offset = QuadOffsets[vertexID % 6] * _Size;
                // UNITY_MATRIX_I_V (camera-to-world) — столбцы = мировые оси камеры,
                // без двусмысленности строка/столбец, которая была бы у UNITY_MATRIX_V.
                float3 right = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 up    = UNITY_MATRIX_I_V._m01_m11_m21;
                float3 posWS = centerWS + right * offset.x + up * offset.y;

                Varyings o;
                o.positionCS = TransformWorldToHClip(posWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                return half4(_Color.rgb, _Color.a);
            }
            ENDHLSL
        }
    }
}
```

**Визуальная проверка в Play всё равно обязательна.** Точный layout `UNITY_MATRIX_I_V` в текущей версии URP-пакета — включить в Play, покрутить камеру вокруг сцены, подтвердить, что quad смотрит на камеру, а не «плоскость лежит». Если не совпало — взять транспонированную пару строк, не гадать по памяти дважды.

---

### Шаг 3 — `PrimitiveParticleBinder`

`Assets/Scripts/Runtime/PrimitiveParticleBinder.cs`, рядом с `VfxParticleBinder.cs`.

```csharp
public sealed class PrimitiveParticleBinder : IRenderBinder, IDisposable
{
    private static readonly int PositionsId = Shader.PropertyToID("_Positions");
    private static readonly int SizeId = Shader.PropertyToID("_Size");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private readonly float size;
    private readonly Color color;
    private Material material;
    private MaterialPropertyBlock props;
    private RenderParams renderParams;
    private int instanceCount;

    public PrimitiveParticleBinder(float size, Color color)
    {
        this.size = size;
        this.color = color;
    }

    public void Initialize(SimContext context)
    {
        Shader shader = Shader.Find("M3D/ParticleBillboard");
        if (shader == null)
        {
            Debug.LogError("PrimitiveParticleBinder: shader 'M3D/ParticleBillboard' not found.");
            return;
        }

        material = new Material(shader) { name = "M3D_ParticleBillboard" };
        props = new MaterialPropertyBlock();
        instanceCount = context.Particles.Count;

        renderParams = new RenderParams(material)
        {
            matProps = props,
            worldBounds = new Bounds(Vector3.zero, Vector3.one * 1e5f), // грубый bound, уточнить по факту — не блокер
            shadowCastingMode = ShadowCastingMode.Off,
        };
    }

    public void Execute(SimContext context)
    {
        if (material == null || instanceCount <= 0)
        {
            return;
        }

        GraphicsBuffer positions = context.Particles.Get(BuiltinAttributes.Position);
        props.SetBuffer(PositionsId, positions);
        props.SetFloat(SizeId, size);
        props.SetColor(ColorId, color);

        Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, instanceCount);
    }

    public void Dispose()
    {
        if (material == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(material);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(material);
        }

        material = null;
    }
}
```

`worldBounds` — грубая оценка, не точная; если камера отсекает частицы на краю (culling), расширить по факту визуальной проверки. `instanceCount` — берётся один раз в `Initialize` (как `VfxParticleBinder.SpawnCount`); нет emitters, число не меняется за жизнь `SimulationWorld` — не обновлять в `Execute`.

---

### Шаг 4 — `SimulationWorld` wiring

`SetupBinders()` — **не** просто «добавить биндер рядом», а заменить ветку `particles.Count > 0` целиком, чтобы `VfxParticleBinder` не создавался при `Primitive` (иначе два облака одновременно, см. п.7 списка выше):

```csharp
if (particles.Count > 0)
{
    if (effect.ParticleRenderMode == ParticleRenderMode.Primitive)
    {
        PrimitiveParticleBinder primitiveBinder =
            new PrimitiveParticleBinder(effect.ParticleSize, effect.ParticleColor);
        primitiveBinder.Initialize(context);
        binders.Add(primitiveBinder);

        if (visualEffect.HasFloat("SpawnCount"))
        {
            visualEffect.SetFloat("SpawnCount", 0f);
        }
        visualEffect.Reinit();
    }
    else
    {
        VfxParticleBinder vfxBinder = new VfxParticleBinder(visualEffect);
        vfxBinder.Initialize(context);
        binders.Add(vfxBinder);
    }
}
else if (visualEffect != null)
{
    // существующая field-only ветка — не трогать
}
```

`Teardown()` (§3a ADR-030) — цикл по `binders` на `IDisposable`, **пропуская** `fieldDebugQuadsBinder` (см. п.8 списка выше), до `binders.Clear()`:

```csharp
for (int i = 0; i < binders.Count; i++)
{
    if (binders[i] is IDisposable disposable && !ReferenceEquals(binders[i], fieldDebugQuadsBinder))
    {
        disposable.Dispose();
    }
}
binders.Clear();

fieldDebugQuadsBinder?.Dispose();
fieldDebugQuadsBinder = null;
```

Проверить, что `VfxParticleBinder` **не** реализует `IDisposable` (не должен — ничего не создаёт) — цикл его молча пропустит, не поломает существующий путь.

---

### Шаг 5 — подтверждено, патч не нужен

`binders[i].Execute(context)` уже в цикле `SimulationWorld.Update` сразу после `Graphics.ExecuteCommandBuffer(commandBuffer)`. `VfxParticleBinder.Execute` сегодня no-op (буферы стабильны), но вызывается тем же циклом — `PrimitiveParticleBinder.Execute` (где реально происходит `Graphics.RenderPrimitives`) будет исполняться каждый кадр без правок. Ничего делать не нужно, оставлено как пункт для истории тикета.

---

### Шаг 6 — шейдер в билде, меню и ассет

**Always Included Shaders.** `Shader.Find("M3D/ParticleBillboard")` в Player-билде может вернуть `null`, если шейдер выстрижен стриппингом (на него нет прямой ссылки от компонента/материала-ассета в сцене — только runtime `Shader.Find` по строке). Добавить `M3D/ParticleBillboard` в Project Settings → Graphics → **Always Included Shaders**, editor-скриптом (по духу `PostProcessingSetup.cs`, который тоже трогает project-level ассеты), не ручным действием оператора, которое забудется при переносе проекта:

```csharp
[MenuItem("Tools/M3D/Register ParticleBillboard Shader")]
public static void RegisterParticleBillboardShader()
{
    Shader shader = Shader.Find("M3D/ParticleBillboard");
    if (shader == null) { Debug.LogError(...); return; }

    var graphicsSettings = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
    SerializedObject so = new SerializedObject(graphicsSettings);
    SerializedProperty shaders = so.FindProperty("m_AlwaysIncludedShaders");
    // проверить, что shader уже не в списке, перед Add — не дублировать при повторном вызове
    ...
    so.ApplyModifiedProperties();
}
```

(Точный путь/API сериализации `GraphicsSettings.asset` — сверить в текущей версии Unity 6000.5.9f1 на месте; это project-wide ассет, редактируется через `SerializedObject`, как `PostProcessingSetup.cs` делает с RP-ассетами. Если найдётся более простой публичный API — использовать его, реализация детали не гейт.) Вызывать этот пункт меню один раз при первой настройке, не при каждом Enable/Disable.

**Демо-меню.** `Tools/M3D/Enable Primitive Render On Boids_mk1` в `M3DDemoTools.cs` (или отдельный `Adr030PrimitiveRenderSetup.cs`, по образцу `Adr012BoidsMk1Setup.cs` — на выбор реализатора, не гейт):

```csharp
[MenuItem("Tools/M3D/Enable Primitive Render On Boids_mk1")]
public static void EnablePrimitiveRenderOnBoidsMk1()
{
    EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>("Assets/Effects/Boids_mk1.asset");
    if (asset == null) { Debug.LogError(...); return; }
    SerializedObject so = new SerializedObject(asset);
    so.FindProperty("particleRenderMode").enumValueIndex = (int)ParticleRenderMode.Primitive;
    so.ApplyModifiedPropertiesWithoutUndo();
    EditorUtility.SetDirty(asset);
    AssetDatabase.SaveAssets();
}
```

`Tools/M3D/Disable Primitive Render On Boids_mk1` — тем же паттерном, ставит `Vfx`, для быстрого A/B на десктопе перед мобильным тестом. **Перед коммитом** `Boids_mk1.asset` должен остаться на `Vfx` (текущий production-дефолт демки), если явно не решено сделать `Primitive` новым дефолтом этой демки — Enable легко забыть отменить после ручного теста.

---

### Шаг 7 — смоук: два теста, не один

**7a. Изолированный тест биндера** (`PrimitiveParticleBinderTests.cs`, EditMode, без полного `SimulationWorld`): создать минимальный `ParticleSet`/`SimContext` (как делают тесты пассов — `FieldTestHarness`-подобный, но для частиц, не полей; если готового харнеса для частиц нет — завести минимальный, локальный для этого теста, не переиспользуемую инфраструктуру ради одного тикета). Проверить: `Initialize` не бросает при валидном shader; `Execute` не бросает; `Dispose()` через `DestroyImmediate` в Editor-контексте реально освобождает `Material` (проверить, например, что повторный `Resources.FindObjectsOfTypeAll<Material>()` не содержит `"M3D_ParticleBillboard"` сразу после `Dispose()`, не после кадра — это и есть разница `Destroy`/`DestroyImmediate`, ради которой п.11 списка выше).

**7b. World-smoke на `Boids_mk1`** (стиль `Fluid2DWorldSmokeTests`): временно выставить `particleRenderMode = Primitive` на **runtime-инстансе** ассета в тесте (не на диске — не `SaveAssets`), `Rebuild()` не бросает, один кадр `Update` не бросает. В `TearDown` теста вернуть `Vfx` (или просто не сохранять изменение — если тест работает с `Instantiate`-копией ассета, а не оригиналом, откат не нужен вовсе; выбрать вариант проще в реализации, не гейт).

Дальше — по DoD (Шаг 8).

---

### Шаг 8 — DoD (см. ADR-030 §6, таблица)

Нет численного гейта. По порядку:

1. **Смоук** (Шаг 7) — зелёный.
2. **Функциональный:** позиции инстансов на экране двигаются согласованно с симуляцией (визуально — рой не рассинхронизирован, не «облако висит на месте, пока частицы двигаются в буфере»).
3. **Desktop visual (оператор):** `Boids_mk1`, `Vfx` vs `Primitive` (через меню Enable/Disable), тот же `SimulationSpeed`/particle count — рой читается тем же роем, цвет/размер видны (не прозрачная пустота из-за забытого дефолта).
4. **Desktop perf (запись):** frame time `Vfx` vs `Primitive` на большом `Capacity` — число в чат/Techdebt, без порога.
5. **Mobile perf (решающий):** тот же жест/particle count, что дал <10 FPS на VFX (Samsung S10 или аналог) — `Primitive` держит кадр или нет. Оператор запускает, не автотест.

Красный на 1–2 — стоп, разбор в чат. Красный на 5 (просадка осталась) — не откат тикета, сигнал открыть отдельный разбор overdraw/fill-rate (ADR-030 §«Открытый вопрос», не молчать про это).

**Закрытие тикета — по стадиям, как fluid-тикеты:** ADR-030 статус → «Реализовано» только после **desktop visual** (п.3) зелёного, не раньше (EditMode-смоук ≠ тикет закрыт, тот же принцип, что у F-серии). Mobile-результат (п.5) — отдельная строка в Techdebt 9 с фактическим числом, **не гейт** на закрытие самого тикета и не повод откатывать код, если просадка осталась — это сигнал на новый, отдельный разбор overdraw/fill-rate.

---

### Шаг 9 — доки после зелёного смоука + desktop visual

- `pass-catalog.md`/`getting-started.md`/`capabilities.md`: новый binder, новый шейдер, новые меню (Enable/Disable/Register Shader) — по одной строке каждый, как остальные меню-пункты.
- `status.md`: короткая запись с датой.
- `Techdebt.md` 9: результат desktop perf; mobile — отдельной строкой с фактическим числом, когда появится (или «просадка осталась — открыт разбор overdraw»).
- ADR-030: статус → «Реализовано» (после desktop visual, см. Шаг 8), таблица DoD с фактическими числами.

---

### Вне скоупа

LUT-палитра/trail (исходный roadmap M2d, Techdebt 10); `RenderMeshIndirect`/indirect-args (до Techdebt 13); перевод `HybridTouchField`/`AgentFieldEcho`/`Gray-Scott-Boids`/`Gray-Scott-Agents` на `Primitive`; ориентация по `heading`/`velocity`; диагностика overdraw через снижение particle count в текущем VFX (пропущена осознанно, ADR-030 Контекст); мобильный gate по образцу `M3DVolumeMobileGate`; тени/освещение частиц; ослабление требования `Build()` к `VisualEffect`; fluid-контур (F3.1+) — отдельный, приостановленный трек.
