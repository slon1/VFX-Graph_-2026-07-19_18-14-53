## ADR-030: Particle render — `Graphics.RenderPrimitives` binder (M2d / Techdebt 9)

**Статус:** **Реализовано** (2026-09-21). Смоук + desktop visual + mobile запись. Дефолт ассетов остаётся `Vfx`; `Boids_mk1` — opt-in через меню, на диске `Vfx`.
**Дата:** 2026-09-21
**Контекст:** M3D Framework, после закрытия F3.0 (fluid на паузе — [ADR-029](ADR-029-Cross-Resolution-Dye.md)). Скоуп сузили явно: только [Techdebt 9](../last/Techdebt.md) (перформанс рендера частиц), не исходный roadmap-пункт M2d (LUT-палитра + trail/persistence, [Techdebt 10](../last/Techdebt.md)) — тот остаётся отдельным, будущим тикетом.
**Не меняет:** `SimPass`/`FieldKernelPass`/пассы симуляции (эта работа — только слой рендера, после `commandBuffer` симуляции); `FieldDebugQuadsBinder`; fluid-контур ([ADR-029](ADR-029-Cross-Resolution-Dye.md) и весь `plan-stable-fluid.md`) не трогать.

### Контекст

[Techdebt 9](../last/Techdebt.md): на Samsung S10 (Vulkan) отключение только VFX-рендера частиц подняло FPS с <10 до 60 (упор в vsync), без единого изменения в compute-пассах. Причина — не доказана точечно (VFX-инфраструктурный оверхед vs overdraw/fill-rate от числа частиц), но пользователь решил не тратить сессию на диагностику отдельно, а идти прямо к замене рендер-слоя — если новый рендер снимет просадку, вопрос «что именно было виновато» закрывается фактом, если не снимет — тогда разбираться в overdraw отдельно.

Архитектура это уже предвидела (`architecture.md`, «Мобильные ограничения»):

> **RenderBinder — абстракция**: VFX Graph — одна из реализаций, рядом quad-рендер (для 2D fluid dye) и fallback `Graphics.RenderPrimitives` + шейдер, читающий те же буферы.

`IRenderBinder` (`Initialize`/`Execute`) уже отделяет рендер от симуляции; `VfxParticleBinder` — единственная существующая реализация для частиц. Эта ADR добавляет вторую, не трогая первую.

### Решение

#### 1. Новый `IRenderBinder`: `PrimitiveParticleBinder`

Не `RenderMeshIndirect`/`RenderMeshInstanced` (буквальные имена из Techdebt 9) — конкретный выбор API: **`Graphics.RenderPrimitives`** (Unity 6, без `Mesh`-ассета, без `GraphicsBuffer.Target.IndirectArguments`). Причина: у фреймворка сейчас нет emitters/dead-pool ([Techdebt 13](../last/Techdebt.md) — «нужны для тач создаёт новые частицы, не реализовано») — число частиц на кадр детерминировано `ParticleSet.Capacity` из `EffectAsset`, GPU не решает динамически, сколько инстансов рисовать. `RenderMeshIndirect` окупается именно там, где GPU сам меняет instance count (тот самый dead-pool) — сейчас это придуманная сложность. `Graphics.RenderPrimitives(RenderParams, MeshTopology.Triangles, vertexCount: 6, instanceCount: N)` — процедурный quad на инстанс, `N` — CPU-известное число, `RenderParams` даёт `matProps`/`worldBounds`/`layer` без per-instance `Matrix4x4[]` на CPU. Zero readback, совпадает с принципом «никаких readback-ов в кадре» (`architecture.md`).

`RenderMeshIndirect` — явно **не отклонён навсегда**: если/когда Techdebt 13 (emitters) войдёт в скоуп, GPU будет решать `alive`-count, и `RenderMeshIndirect` с compute-заполненным args-буфером станет уместен. Здесь — не нужен, усложнение без выгоды.

#### 2. Шейдер `M3D/ParticleBillboard`

Процедурный camera-facing quad, без входного `Mesh`. Стиль — как `FieldDebug.shader` (URP `HLSLPROGRAM`, `TransformObjectToHClip`, `Packages/.../Core.hlsl`), но вершины строит сам вертекс-шейдер:

```hlsl
StructuredBuffer<float3> _Positions;   // ParticleSet.Position, той же GraphicsBuffer, без копии
float _Size;
float4 _Color;

static const float2 QuadOffsets[6] = {
    float2(-0.5,-0.5), float2( 0.5,-0.5), float2(-0.5, 0.5),
    float2(-0.5, 0.5), float2( 0.5,-0.5), float2( 0.5, 0.5),
};

Varyings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    float3 centerWS = _Positions[instanceID];
    float2 offset = QuadOffsets[vertexID % 6] * _Size;
    // UNITY_MATRIX_I_V (camera-to-world) — столбцы = мировые оси камеры без двусмысленности
    // строка/столбец, в отличие от UNITY_MATRIX_V. Формула одна во всех документах этого
    // тикета — не смешивать с вариантом через V-матрицу (был в первой редакции ТЗ).
    float3 right = UNITY_MATRIX_I_V._m00_m10_m20;
    float3 up    = UNITY_MATRIX_I_V._m01_m11_m21;
    float3 posWS = centerWS + right * offset.x + up * offset.y;
    Varyings o;
    o.positionCS = TransformWorldToHClip(posWS);
    return o;
}
```

`_Positions` биндится через `MaterialPropertyBlock.SetBuffer` на `GraphicsBuffer`, который уже возвращает `context.Particles.Get(BuiltinAttributes.Position)` (тот же буфер, что читает `VfxParticleBinder` сегодня — **не** новый источник данных). Цвет/размер — материальные параметры (`_Size`, `_Color`), не пытаемся воспроизвести узел-графовую логику `ParticleBufferVFX.vfx` бит-в-бит (там нет экспонированных параметров цвета — вручную разбирать граф ради параности не нужно, см. §5).

**`_Size`/`_Color` — поля на `EffectAsset`, не хардкод в конструкторе.** Найдено при разборе ТЗ: если биндер создаётся как `new PrimitiveParticleBinder()` без параметров с ассета, «настройка в инспекторе» ничего не значит и теряется на `Rebuild`. `EffectAsset` получает `[SerializeField] float particleSize = 0.05f` и `[SerializeField] Color particleColor = Color.white` рядом с `particleRenderMode`. **Дефолт размера — `0.05`**, не `0.5`: `Boids_mk1` (`resolution=50`, `cubeSize=2`) даёт шаг решётки `2/50=0.04` на 125 000 частиц — квад `0.5` перекрывает соседей на порядок и сам по себе создаёт fill-rate-нагрузку, которая исказит результат теста (Primitive окажется медленнее не из-за инфраструктуры, а из-за размера квада). **Дефолт цвета — непрозрачный** (`Color.white`, `a=1`), не `default`/`default(Color)` (нулевая альфа — облако невидимо, ложное «точки исчезли» на DoD).

`heading`/`velocity` — не биндить в первой версии (нет ориентации по направлению движения); если понадобится позже, добавить `StructuredBuffer<float3> _Headings` тем же паттерном, отдельным тикетом.

#### 3. `EffectAsset` — новый флаг, не подмена дефолта

```csharp
public enum ParticleRenderMode { Vfx = 0, Primitive = 1 }
[SerializeField] private ParticleRenderMode particleRenderMode = ParticleRenderMode.Vfx;
public ParticleRenderMode ParticleRenderMode => particleRenderMode;
```

Дефолт **`Vfx`** — ни один существующий `.asset` не меняет поведение молча.

**Блокер, найденный при разборе ТЗ: `SetupBinders` при `particles.Count > 0` сегодня безусловно создаёт `VfxParticleBinder`.** Просто добавить `PrimitiveParticleBinder` рядом — получим **два** рисующих облака одновременно; A/B и особенно мобильный FPS-тест окажутся не про «VFX vs Primitive», а про «VFX+Primitive vs VFX». `Build()` при этом жёстко требует `visualEffect != null` (иначе `enabled=false` для всего `SimulationWorld`) — компонент `VisualEffect` на объекте должен остаться, ослаблять это требование **не в этом тикете** (отдельная, более крупная уборка; `Test1` уже держит `VisualEffect` на сцене). Решение: при `Primitive` **не создавать** `VfxParticleBinder`; вместо него — заглушить сам VFX-плейбэк тем же приёмом, что уже есть в field-only ветке (`SpawnCount=0` + `Reinit()`):

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
```

`VfxParticleBinder` не трогать — оба живут рядом, `Primitive` просто глушит VFX-плейбэк, не удаляет компонент.

#### 3a. `SimulationWorld.Teardown` не освобождает generic `binders`

Найдено при разборе: `Teardown()` вызывает `fieldDebugQuadsBinder?.Dispose()` явно (спец-поле), но сам `binders.Clear()` **не** проверяет `IDisposable` — если добавить второй элемент в `binders` (наш `PrimitiveParticleBinder`), созданный runtime `Material`/`MaterialPropertyBlock` не освободится. `VfxParticleBinder` сегодня безопасен (никаких Unity-объектов не создаёт, только ссылки), поэтому дыра раньше не проявлялась.

Фикс — **точечный**, по образцу существующего disposal-цикла у `passes` (`SimulationWorld.cs` тот же метод, чуть выше): добавить в `Teardown()` цикл по `binders`, проверяющий `IDisposable`, **до** `binders.Clear()`. **Уточнение (найдено при разборе ТЗ): `fieldDebugQuadsBinder` уже лежит и в спец-поле, и в списке `binders`** (`binders.Add(fieldDebugQuadsBinder)` в `SetupBinders`) — слепой цикл вызовет его `Dispose()` **дважды** (раз из цикла, раз из существующей явной строки). Цикл обязан пропускать этот конкретный инстанс, не объединяя оба пути в один:

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

`PrimitiveParticleBinder` реализует `IDisposable`, освобождает `Material` (созданный в `Initialize`, `new Material(shader)`, как `FieldDebugQuadsBinder`). Существующую явную пару строк `fieldDebugQuadsBinder?.Dispose(); fieldDebugQuadsBinder = null;` не удалять и не рефакторить в один проход ради этого тикета — только добавить `!ReferenceEquals` в новый цикл.

#### 4. Первый потребитель — `Boids_mk1`, не все демки сразу

`Tools/M3D/Enable Primitive Render On Boids_mk1` (стиль `Adr012BoidsMk1Setup.cs`): ставит `particleRenderMode = Primitive` на `Assets/Effects/Boids_mk1.asset` через `SerializedObject`, не через YAML rid. Остальные VFX-демки (`HybridTouchField`, `AgentFieldEcho`, `Gray-Scott-Boids`, `Gray-Scott-Agents`) остаются на `Vfx` — не переключать без отдельного решения по каждой (разные визуальные ожидания, разный particle count).

#### 5. Визуальная парность — явно приблизительная, не гейт

`ParticleBufferVFX.vfx` экспонирует только `SpawnCount`/`PositionBuffer` — цвет/размер зашиты в граф процедурно, не разбираем узлы ради точного повтора. DoD — «читаемое облако частиц того же порядка размера/яркости», не «пиксель-в-пиксель». Цвет/размер `PrimitiveParticleBinder` — художественная настройка `_Color`/`_Size` в инспекторе, не вычисленное значение.

#### 6. DoD — функциональный + перф, не числовой гейт (в отличие от fluid-тикетов)

Здесь нет «правильной формулы» для assert — DoD:

| Что | Как | Факт (2026-09-21) |
| --- | --- | --- |
| **Смоук** | `Rebuild()` на копии `Boids_mk1` с `Primitive` не бросает; кадр `Execute` не бросает; буфер позиций тот же `GraphicsBuffer` | зелёный (`PrimitiveParticleBinderTests`, `BoidsMk1PrimitiveWorldSmokeTests`) |
| **Функциональный** | рой на экране едет с симуляцией | visual оператора: оба режима рисуют рой |
| **Desktop visual** | `Boids_mk1` Vfx vs Primitive, тот же Speed/N | рой читается; парность приблизительная |
| **Desktop perf (запись)** | fullscreen Vfx vs Primitive, без порога | **оба ~20 FPS** (Windows Editor). Смена рендера не двигает кадр — на десктопе лимитер не VFX. Editor Windows→Android: 20→~100 FPS — смена backend (D3D vs Vulkan), **не** Primitive. |
| **Mobile perf (решающий)** | Samsung S10 player, Vulkan, тот же жест/N в одной сессии | VFX **20–30 FPS**, Primitive **40–50 FPS** (~×2). Гипотеза Techdebt 9 **частично** фактом: оверхед VFX-инфры реален. Не полный потолок «выключили рисунок → 60». Остаток — Transparent fill-rate/overdraw, не откат. Старый замер VFX <10 не сравнивать 1:1 с 20–30 (другая сессия/N). |

Красный на смоуке/функциональном — стоп. Mobile не до 60 — не откат, разбор overdraw отдельно **если** понадобится; тикет ADR-030 закрыт.

#### 7. Что не входит

- LUT-палитра / trail-persistence (исходный roadmap M2d, [Techdebt 10](../last/Techdebt.md)) — отдельный тикет, даже если рендер-слой уже переписан.
- `RenderMeshIndirect` / compute-заполненный indirect-args буфер — до Techdebt 13 (emitters/dead-pool) не нужен.
- Перевод `HybridTouchField`/`AgentFieldEcho`/`Gray-Scott-Boids`/`Gray-Scott-Agents` на `Primitive` — по одному, отдельными решениями после `Boids_mk1`.
- Ориентация по `heading`/`velocity` (facing по направлению движения) — не в этой версии шейдера.
- Диагностика «VFX-оверхед vs overdraw» через снижение particle count в текущем VFX — пропущена осознанно (см. Контекст); если mobile-тест не снимет просадку, открывать отдельно.
- Тени/касты/освещение частиц — `Unlit`-подобный шейдер, как `FieldDebug.shader`; сложное освещение вне скоупа.
- Мобильный рантайм-гейт по образцу `M3DVolumeMobileGate` — не нужен, `Primitive`-режим и есть облегчённый путь.

### Отклонённые варианты

**`RenderMeshIndirect` сразу.** Индирект-буфер решает задачу «GPU знает live-count», которой сейчас нет (нет dead-pool). Усложнение без применения.

**`RenderMeshInstanced` с `Matrix4x4[]` на CPU.** Требует читать позиции на CPU каждый кадр (`GetData`) — прямое нарушение «никаких readback-ов в кадре». `Graphics.RenderPrimitives` с чтением буфера в шейдере — нет.

**Разобрать `ParticleBufferVFX.vfx` и повторить его цвет/размер формулу бит-в-бит.** Хрупко (внутренние узлы графа), не даёт технической выгоды — cel/яркость это художественная настройка, не контракт.

**Глобальный дефолт `particleRenderMode = Primitive`.** Молча меняет поведение всех существующих `.asset`. Дефолт `Vfx`, opt-in по ассету.

**Мобильный gate по аналогии `M3DVolumeMobileGate`.** Не нужен — `Primitive` не хуже `Vfx` на desktop по построению (не GPU-дороже), гейтить нечего.

### Последствия

- (+) S10: Primitive ~×2 против VFX (40–50 vs 20–30). Opt-in на `Boids_mk1`, откат = `Disable` / `particleRenderMode = Vfx`.
- (+) `IRenderBinder` чистый: второй binder не трогает первый.
- (−) Визуальная парность приблизительная — `_Color`/`_Size` на ассете, не копия графа VFX.
- (−) Desktop не выигрывает (оба ~20 FPS Windows Editor). Mobile не до vsync 60 — fill-rate квадов остаётся; LUT/trail и остальные демки не открыты этим тикетом.

**Вне скоупа документа:** LUT/trail (M2d визуальная часть); emitters/dead-pool (Techdebt 13) и `RenderMeshIndirect`, который они бы разблокировали; перевод остальных VFX-демок.
