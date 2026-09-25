## ADR-031: Primitive-only present + LUT-палитра по `value` (M2d, урезанный скоуп)

**Статус:** EditMode зелёный (2026-09-25). Play-цвета по курсу на `Boids_mk1` — оператор, ещё не закрыто.
**Дата:** 2026-09-25
**Контекст:** M3D Framework, после [ADR-030](ADR-030-Particle-Render-Primitives-Binder.md) (Primitive-рендер частиц, opt-in, закрыто). Исходный roadmap-пункт M2d ([Techdebt 10](../last/Techdebt.md), план [`plan-m2d-lut-trail.md`](../plan-m2d-lut-trail.md)) резался в чате на четыре тикета («Path to hash swarm»); эта ADR фиксирует **два** из них — present и палитра. Spatial hash, `teamId`, `BoidNeighborForcePass`, `SeedBoidGroupsPass`, пресет `Boids_hash` — **не открываются** (гейт [Techdebt 11](../last/Techdebt.md) не снят: field-surrogate соседей на `Boids_mk1` не признан недостаточным на практике). Trail/persistence (M2d.2) — тоже не в этом документе.
**Не меняет:** `SimPass`/`FieldKernelPass`/пассы симуляции fluid/Gray-Scott; `Boids_mk1` порядок пассов и калибровку (ADR-012); `FieldDebugQuadsBinder`; `plan-stable-fluid.md`.

---

### Контекст

ADR-030 сделала `PrimitiveParticleBinder` вторым, **opt-in** биндером (`EffectAsset.ParticleRenderMode`, дефолт `Vfx`), чтобы не менять поведение существующих `.asset` молча и не терять авторский вид VFX Graph, если он есть.

Разбор при подготовке этой ADR показал: авторского вида **нет**. В проекте всего три `.vfx`-файла (`Assets/Vfx/ParticleBufferVFX.vfx`, `New VFX_1.vfx`, `Frac VFX.vfx`), и `VisualEffect`-компонент на `Test1` не переключается между демками (`M3DDemoTools.cs` не переставляет `.vfx` asset при смене `EffectAsset` — один и тот же generic passthrough-граф: буфер позиций → квад одного цвета). `ParticleBufferVFX.vfx` экспонирует только `SpawnCount`/`PositionBuffer` (та же находка уже была зафиксирована в ADR-030 §5). Значит единственная причина держать `VfxParticleBinder` как реальную альтернативу — не визуальная, а платформенная (`architecture.md`: VFX Graph требует compute-capable API, на GLES3 не работает) — а целевые устройства проекта на GLES3 не подтверждены и не отклонены.

Раз содержательного визуального различия нет, а `Graphics.RenderPrimitives` уже измеренно быстрее (ADR-030, S10: 20–30 → 40–50 FPS), решено сделать Primitive **единственным путём в `SetupBinders`**, не трогая код `VfxParticleBinder`/`ParticleRenderMode` — они остаются в проекте как дешёвая, ничего не стоящая страховка на случай будущего GLES3-таргета, но `SetupBinders` их больше не вызывает.

Второй тикет — LUT-палитра по атрибуту `value` (M2d.1 из `plan-m2d-lut-trail.md`), без изменений: она садится на Primitive-биндер независимо от того, opt-in он или единственный путь.

### Решение

#### 1. `SetupBinders` — Primitive безусловно, `ParticleRenderMode` не читается

`SimulationWorld.SetupBinders` (сейчас — ветка `if (effect.ParticleRenderMode == ParticleRenderMode.Primitive) ... else VfxParticleBinder`) переписывается: при `particles.Count > 0` всегда создаётся `PrimitiveParticleBinder`; ветка `VfxParticleBinder` удаляется из этого метода. Заглушка VFX-плейбэка (`SpawnCount=0` + `Reinit()`) остаётся — тем же приёмом, что уже есть для Primitive-ветки и для field-only ветки (`particles.Count == 0`).

`ParticleRenderMode` enum, `EffectAsset.ParticleRenderMode` поле и меню `Tools/M3D/Enable-Disable Primitive Render On Boids_mk1` (`Adr030PrimitiveRenderSetup.cs`) — **не удаляются**. Поле просуществует в инспекторе, ничего не делая (dormant), пока не появится решение по платформам без `Graphics.RenderPrimitives`. Это осознанный компромисс: дешевле держать мёртвый код, чем терять точку возврата.

#### 2. `VisualEffect` — необязателен на `Build`, null-guard

Сейчас `Build()` требует `visualEffect != null` вместе с `effect`/`passLibrary` (иначе `enabled = false` для всего `SimulationWorld`). Условие смягчается: обязательны только `effect` и `passLibrary`. `visualEffect` может остаться `null` (сцена без VFX-компонента вообще).

Обе ветки `SetupBinders`, которые сегодня безусловно трогают `visualEffect` (заглушка `SpawnCount=0`/`Reinit()` — и в Primitive-ветке частиц, и в field-only ветке `particles.Count == 0`), оборачиваются в `if (visualEffect != null)`. Без этого — прямой NRE, как только `Build` перестанет требовать компонент. Найдено при разборе кода этой ADR, не гипотетически:

```545:550:Assets/Scripts/Runtime/SimulationWorld.cs
                if (visualEffect.HasFloat("SpawnCount"))
                {
                    visualEffect.SetFloat("SpawnCount", 0f);
                }

                visualEffect.Reinit();
```

`GetComponent<VisualEffect>()`-фолбэк перед проверкой остаётся — если компонент физически есть на объекте, поле всё равно заполнится.

#### 3. `EffectAsset` — `particleGradient` / `particleValueScale`

Рядом с `particleSize`/`particleColor`:

```csharp
[SerializeField] private Gradient particleGradient;
[SerializeField, Min(0f)] private float particleValueScale = 1f;

public Gradient ParticleGradient => particleGradient;
public float ParticleValueScale => particleValueScale;
```

`particleGradient == null` (старые ассеты) — биндер использует `DebugFieldQuadSlot.DefaultFireGradient()`, как `FieldDebugQuadsBinder`. `particleColor` остаётся плоским цветом — используется, если у частиц нет атрибута `value` (см. §5).

#### 4. `ParticleBillboard.shader` — LUT по `value`

Bake как в `FieldDebugQuadsBinder.BakeLutTexture`: `Gradient` → `Texture2D` `256×1`, один раз в `Initialize`, не в кадре. Шейдер получает буфер значений и переключатель:

```hlsl
StructuredBuffer<float> _Values;
Texture2D<float4> _LutTex;
SamplerState sampler_LutTex;
float _Scale;
float _UseLut; // 0 = плоский _Color, 1 = сэмплить LUT по _Values

half4 frag(Varyings input) : SV_Target
{
    if (_UseLut > 0.5)
    {
        float d = saturate(_Values[input.instanceID] * _Scale);
        float4 lut = _LutTex.SampleLevel(sampler_LutTex, float2(d, 0.5), 0);
        return half4(lut.rgb, lut.a * _Color.a);
    }

    return half4(_Color.rgb, _Color.a);
}
```

`instanceID` нужно протащить из `vert` в `Varyings` (сейчас не передаётся). Нет атрибута `value` у эффекта — `PrimitiveParticleBinder` не биндит `_Values`/`_LutTex`, `_UseLut` остаётся `0` — старое поведение (плоский `_Color`), биндер не падает.

#### 5. Писатели `value`

Present ничего не считает — `value` пишут отдельные `ParticleKernelPass` в `Assets/Shaders/GPU/Passes/DynamicsPasses.compute` (там уже есть `RWStructuredBuffer<float3> velocity`/`heading`, добавляется `RWStructuredBuffer<float> value`):

- **`SpeedToValuePass`**: `value = saturate(length(velocity) / SpeedRef)`, `SpeedRef` — параметр пасса. Остаётся в каталоге, но **не** подключается к `Boids_mk1` — после `HeadingSteer` (ADR-012) скорость каждой частицы равна `CruiseSpeed`, разброса `|velocity|` почти нет, палитра на этом ассете была бы почти константной. Кандидат для будущих эффектов, где скорость действительно разная (Newton-пассы, не kinematic heading).
- **`HeadingToValuePass`**: угол `heading` в плоскости XZ → `0..1` (`atan2(heading.z, heading.x) / (2π) + 0.5`). **Именно этот писатель** идёт на `Boids_mk1` первым — курс у частиц реально разный (align/cohesion/separation дают разные направления), палитра будет содержательной.

Оба — `Dynamics`, без `dt`, ставятся в конец цепочки пресета (после `HeadingSteer`/`Integrate`/`BoxBounds`, перед рендером — порядок пассов не влияет на рендер напрямую, но семантически это «последний штрих перед present»).

Подключение на `Boids_mk1` — opt-in через меню или явную правку ассета (не тихая перезапись). `resolution=50` и `cruiseSpeed` не трогаются.

#### 6. `TeamToValuePass` — класс есть, не подключён

Задел под будущий тикет с `teamId` (`value = teamId / max(teamCount-1, 1)`). Класс существует в коде **без** kernel в `DynamicsPasses.compute` и без места в каком-либо `.asset`. `Reads` у него пуст — `teamId`-атрибут не декларируется и не требуется, ссылка на несуществующий атрибут тут ни при чём. Реальный контракт: `ParticleKernelPass.Initialize` вызывает `SimContext.FindKernel(KernelName)` безусловно, а тот **бросает** `InvalidOperationException`, если кернела нет ни в одном `.compute`-файле pass library. `SimulationWorld.Build` ловит это исключение вокруг `pass.Initialize`, логирует и валит весь мир (`Teardown()` + `enabled=false`). Если кто-то по ошибке добавит `TeamToValuePass` на реальный ассет — эффект **не собирается**, с понятной ошибкой `Kernel 'TeamToValue' not found in any compute shader of the pass library`, а не «эффект собрался, но атрибут не найден». Это и есть требуемая защита от случайного подключения, без attribute-guard.

#### 7. DoD

| Что | Как |
| --- | --- |
| Смоук | `Build()` без `VisualEffect` на объекте не бросает и не даёт `enabled=false`; `Execute` не NRE (обе ветки `SetupBinders` за null-guard) |
| Смоук | `Rebuild()` эффекта без атрибута `value` — `PrimitiveParticleBinder` не падает, рисует плоским `_Color`, как сегодня |
| EditMode | LUT bake не каждый кадр (проверка по количеству вызовов bake/по ссылке на текстуру между кадрами) |
| EditMode | `value=0` и `value=1` попадают в разные концы градиента — сверяется на **той же функции**, что строит пиксели LUT (`gradient.Evaluate(0)` vs `gradient.Evaluate(1)`), не на самой `Texture2D` (та после `Apply(false, true)` не читаема с CPU) |
| Visual | `Boids_mk1` с `HeadingToValuePass` — частицы с разным курсом читаются разным цветом, не однотонное облако |
| Perf (запись, не гейт) | Desktop FPS рядом с ADR-030 (~20 на Windows Editor), без порога |

#### 8. Что не входит

- Spatial hash, `teamId`, `BoidNeighborForcePass`, `SeedBoidGroupsPass`, пресет `Boids_hash` — Techdebt 11 не снят.
- Trail/persistence (M2d.2) — отдельный, более поздний тикет.
- `hdrIntensity` на частицах, мобильный Bloom-гейт для Primitive, ориентация квада по `heading` (facing).
- Физическое удаление `VfxParticleBinder`/`ParticleRenderMode`/меню Enable-Disable — код остаётся, просто не вызывается.
- Перевод `HybridTouchField`/`AgentFieldEcho`/`Gray-Scott-Boids`/`Gray-Scott-Agents` на `value`-палитру — только `Boids_mk1` в этом тикете (хотя рендерятся Primitive все — это следствие §1, не повод сразу писать всем `value`).

### Отклонённые варианты

**Оставить `ParticleRenderMode` как реальный opt-in (не менять `SetupBinders`).** Отклонено — раз авторского VFX-вида нет ни у одной демки (один и тот же generic `.vfx`, см. Контекст), удерживать два реальных пути рендера не даёт ничего, кроме потери перформанса у всех немодифицированных пресетов.

**Удалить `VfxParticleBinder`/`ParticleRenderMode` физически.** Отклонено сейчас — платформенный вопрос (GLES3) не закрыт; дешевле держать мёртвый код, чем терять путь назад без git-археологии.

**Разобрать узлы `ParticleBufferVFX.vfx` и версионировать точную визуальную парность.** Отклонено ещё в ADR-030 §5 — не даёт технической выгоды, парность художественная, не контракт.

### Последствия

- (+) Все существующие пресеты с частицами получают перформанс Primitive «бесплатно», без per-asset миграции.
- (+) `value`-палитра — общий механизм, не привязанный к конкретному алгоритму (следующий писатель — любой).
- (−) `ParticleRenderMode` в инспекторе временно ничего не делает — источник потенциальной путаницы, пока не решится вопрос GLES3.
- (−) Любая демка, которая **действительно** полагалась на форму/motion VFX Graph (если такая появится позже), должна быть найдена и явно исключена — на сегодняшний момент разбора такой не найдено.

**Вне скоупа документа:** spatial hash / neighbor flock (Techdebt 11), trail (M2d.2), перформанс-разбор overdraw (ADR-030, отдельно по факту нужды).
