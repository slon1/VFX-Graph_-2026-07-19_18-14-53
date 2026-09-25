# План: LUT + trail (M2d, Techdebt 10)

**Дата:** 2026-09-22
**Статус:** план. Код не начат. Это не ТЗ программисту — ТЗ режется по одному тикету после согласия на скоуп.
**Связано:** [ADR-010](ADR/ADR-010-LUT-Palette+HDR-Intensity.md) (LUT debug-квада), [ADR-030](ADR/ADR-030-Particle-Render-Primitives-Binder.md) (present частиц), [Techdebt 10](last/Techdebt.md), [roadmap M2d](last/roadmap_m2a.md).

Цель — «живой» вид роя: цвет из палитры и затухающий след, **независимо от алгоритма** (boids — первый потребитель, не единственный). Два тикета, не один: если смешать, непонятно, что дало картинку.

Perf-слой уже есть (`PrimitiveParticleBinder`, S10: VFX 20–30 → Primitive 40–50). Палитра и шлейф садятся **на него**, не в VFX Graph. Дефолт ассетов остаётся `Vfx`.

---

## 0. Что уже есть и чего не хватает

| Уже есть | Не хватает |
| --- | --- |
| `Gradient` → `256×1` LUT, печётся один раз в `FieldDebugQuadsBinder` (ADR-010). Шейдер `M3D/FieldDebug`, режим `ScalarHeatmap`: `saturate(value * scale)` → UV LUT, отдельно `hdrIntensity`. | Тот же приём на **частицах**. `ParticleBillboard` красит все квады одним `_Color`. |
| `BuiltinAttributes.Value` (`float1`) в каталоге. World регистрирует атрибут, если какой-то пасс его читает или пишет. | Ни один present-биндер `value` не читает. На `Boids_mk1` его сейчас никто не пишет. |
| Шлейф как поле: `ClearFieldAccum` → `ScatterDensity` → `NormalizeDensityAccum` (**прибавляет** сумму в scalar, не затирает) → `DecayFieldScalar` (`× exp(-rate·dt)`). `ClearField` на этом поле **не** ставить — иначе кадр стирает след. Так описан accumulate-onto-decaying. | Отдельное поле `trail`, не cohesion/separation: те поля кормят силы и живут в режиме replace. |
| Debug-квад рисует scalar heatmap. | Квады разложены **панелью** вдоль AxisU, не на плоскости симуляции под роем. |

Новых типов ресурсов нет. Emitters, spatial hash, `RenderMeshIndirect` этому плану не нужны.

---

## 1. M2d.1 — палитра на частицах

Один механизм: квад красится по скаляру частицы, не по константе.

**Present.** `PrimitiveParticleBinder` печёт `Gradient` с `EffectAsset` так же, как debug-квад (один раз в `Initialize`, не в кадре). Шейдер `M3D/ParticleBillboard` сэмплит LUT: `d = saturate(_Values[instanceID] * _Scale)`. Нет атрибута `value` у эффекта — биндер не падает, все квады получают `d = 1` (как сейчас плоский цвет). `VfxParticleBinder` не трогать.

**Кто пишет `value`.** Present не считает скорость. Отдельный маленький пасс, например `SpeedToValue`: `value = saturate(|velocity| / speedRef)`, `speedRef` на пассе. Первый потребитель — `Boids_mk1` (opt-in, меню или явная правка ассета, не silent). Другой эффект подставит свой писатель (`value` уже может быть чем угодно: скорость, плотность, возраст — когда возраст появится).

**Не в этом тикете:** `hdrIntensity` на частицах и мобильный Bloom (ADR-025 гейт на телефоне остаётся); ориентация квада по `heading`; перевод остальных демок на Primitive; смена `resolution` / `cruiseSpeed` у `Boids_mk1`.

**DoD.** EditMode: bake LUT не каждый кадр; `value` 0 и 1 дают разные концы градиента (можно на CPU по текстуре 256×1). Visual: Primitive + `Boids_mk1`, быстрые и медленные читаются разным цветом, не белое облако. Desktop FPS — запись рядом с ADR-030 (~20 на Windows Editor), без порога.

---

## 2. M2d.2 — след полем

Открывать после visual M2d.1. Симуляция и картинка разделены.

**Симуляция.** Новое scalar-поле `trail`, `R16_SFloat`, плоскость та же, что у роя. Порядок кадра:

```
DecayFieldScalar(trail) → ClearFieldAccum(trail) → ScatterDensity(trail) → NormalizeDensityAccum(trail)
```

Decay **до** штампа: след прошлого кадра тускнеет, точка этого кадра остаётся яркой. `ClearField(trail)` нет. Cohesion/separation не переиспользовать как картинку.

`DecayRate` и `valueScale` штампа — крутилки ассета. Стартовые числа подбирает visual, не оракул. Если след «снежит», это CFL явного accumulate, не повод писать новый scatter.

**Present.** Тот же `M3D/FieldDebug` / `ScalarHeatmap` и тот же bake LUT. Отличие от debug-панели — квад лежит **в плоскости поля** (`Origin` / `AxisU` / `AxisV` / `Size`), под роем, а не в ряд с инженерными квадами. Отдельный слот или флаг раскладки на существующем биндере; второй шейдер шлейфа не заводить.

Частицы M2d.1 рисуются поверх. Цвет следа — свой `Gradient` слота, не обязан совпадать с палитрой частиц.

**DoD.** Visual: за роем остаётся тающий след; стоп симуляции (или `DecayRate` большой) гасит его; кадр без `ClearField` не обнуляет поле. Inf/NaN нет. S10 FPS — запись, не гейт: квад + частицы добавят fill-rate к 40–50 Primitive. Разбор overdraw открывать только если этот вид сам станет узким местом.

---

## 3. Порядок и что не открывать

1. M2d.1 (LUT) → visual → потом M2d.2 (trail).
2. ТЗ программисту — на один тикет, после того как этот скоуп принят.
3. `Boids_mk1` на диске остаётся `ParticleRenderMode.Vfx`, пока явно не решим иначе. Грязные `resolution=50` / `cruiseSpeed=0.3` в этот план не входят.

**Вне скоупа:** VFX-палитра; per-particle ribbon и буфер истории позиций; emitters / возраст / `RenderMeshIndirect`; spatial hash; heading-billboard; fluid цветной dye (Techdebt 10a); мобильный Bloom; смена дефолта всех ассетов на Primitive; F3.1 (впрыск dye).
