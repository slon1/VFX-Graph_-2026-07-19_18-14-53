## ADR-025: HDR Camera + Bloom + ACES Volume (генерик пост-обработка, не fluid-specific)

**Статус:** Принято (реализовано)
**Дата:** 2026-09-05
**Контекст:** M3D Framework, генерик render-слой (не привязан к фазам fluid `plan-stable-fluid.md`) — применяется к любому `EffectAsset` (Fluid2D, Gray-Scott-Boids, Boids_mk1), не к одному конкретному
**Связано с:** [ADR-010](ADR-010-LUT-Palette+HDR-Intensity.md) (`hdrIntensity` на `DebugFieldQuadSlot` — существующая инфраструктура, до этого тикета никогда не проверенная сквозным Bloom); [Techdebt 9](../last/Techdebt.md) (VFX Graph GPU-бюджет, прецедент <10→60 FPS)
**ТЗ:** [`todo-postfx-layer1.md`](../last/todo-postfx-layer1.md)

### Контекст

`FieldDebug.shader` (ADR-010) умеет выводить heatmap-цвет из LUT с множителем `_HdrIntensity`, специально предназначенным для последующего срабатывания URP Bloom. С момента ADR-010 (2026-08-08) этот множитель существует, но реального Bloom в сцене никогда не было — вся цепочка «hdrIntensity > 1 → яркий тексель → Bloom» не проверялась end-to-end, только через саму LUT-палитру без пост-обработки.

Зафиксированное по факту (не предположение, проверено по файлам проекта на дату ADR) текущее состояние:

- `Assets/Scenes/SampleScene.unity`: Main Camera уже `m_HDR: 1`.
- `Assets/Settings/PC_RPAsset.asset` и `Assets/Settings/Mobile_RPAsset.asset`: у **обоих** уже `m_SupportsHDR: 1`.
- В сцене **нет ни одного** `Volume`-компонента — реальный гэп не в HDR-флагах (они уже включены везде), а в отсутствии `Volume` + профиля с оверрайдами.
- `Assets/Settings/DefaultVolumeProfile.asset` существует, но это **тестовый ассет пакета URP Core** (внутри `CopyPasteTestComponent`, `TestAnimationCurveVolumeComponent`, `OasisFogVolumeComponent`, `OutlineVolumeComponent` — стандартные тестовые компоненты пакета, не проектные). Использовать или дорабатывать этот файл запрещено — переиспользование только по имени создаёт риск сломать тесты пакета Core, которые могут ссылаться на этот ассет.

Мобильный риск-класс — не гипотетический: [Techdebt 9](../last/Techdebt.md) документирует подтверждённый замер (Samsung S10, Vulkan) — отключение только VFX-рендера частиц поднимает FPS с <10 до 60. Переход с VFX Graph не решён. Bloom — известно дорогой GPU-эффект (downsample-цепочка + доп. HDR-буфер). Explicit desktop-first решение по аналогии с §0 `plan-stable-fluid.md`.

### Решение

#### 1. Это композиция существующих флагов + новый `Volume`, не смена рендер-конвейера

`PostProcessingSetup.cs` (новый Editor-скрипт, рядом с `M3DDemoTools.cs`) **проверяет и логирует** текущие `m_HDR`/`m_SupportsHDR`, не перезаписывает их слепо (они уже верны на обоих RP asset и камере — трогать без причины запрещено). Единственное реальное действие скрипта — гарантировать существование `GameObject "M3D Volume"` (`isGlobal = true`) с профилем `Assets/Settings/M3DVolumeProfile.asset`.

`M3DVolumeProfile.asset` — **новый** файл. `DefaultVolumeProfile.asset` не трогать, не читать, не наследовать от него оверрайды.

#### 2. Идемпотентность через `Has<T>`, не безусловный `Add<T>`

`VolumeProfile.Add<T>()` не проверяет дубликаты сам. Обязательный паттерн на каждый компонент:

```csharp
Bloom bloom = profile.Has<Bloom>()
    ? profile.components.OfType<Bloom>().First()
    : profile.Add<Bloom>(overrideState: true);
```

Повторный запуск `MenuItem` после ручной калибровки в инспекторе **не должен** сбрасывать откалиброванные значения (по аналогии с правилом ADR-022 — «Create один раз → калибровать → коммитить», здесь: «Setup один раз → калибровать → коммитить», повторный Setup — no-op, не reset).

#### 3. Стартовые значения (не финальные, калибровка — в Play, вручную)

| Компонент | Поле | Старт |
| --- | --- | --- |
| `Bloom` | `threshold` | `0.8` |
| `Bloom` | `intensity` | `0.4` |
| `Bloom` | `scatter` | `0.65` |
| `Tonemapping` | `mode` | `ACES` |
| `ColorAdjustments` | `postExposure`/`contrast`/`saturation` | `0` (заготовка, не блокирующий пункт DoD) |

#### 4. Мобильный бюджет — explicit decision (a), не (б)

**Принято (a):** HDR+Bloom как продуктовая пара включаются только для desktop-трека. На мобильном билде эффект пост-обработки **выключается runtime-гейтом**, не через несуществующий механизм «отдельных настроек Bloom на Renderer Asset».

Причина отклонения исходного варианта (б) из черновика: `bloom.downscale`/`maxIterations`/`skipIterations` — поля `VolumeComponent` **внутри профиля**, не свойства `UniversalRendererData` (`PC_Renderer.asset`/`Mobile_Renderer.asset`). URP не даёт настроить Bloom «отдельно на Renderer Asset» — Renderer Asset управляет `RendererFeatures` (в проекте сейчас там только `ScreenSpaceAmbientOcclusion`), не параметрами Volume-оверрайдов. Вариант (б) в исходной формулировке нереализуем без **второго** профиля + platform-switch, которого в проекте пока нет ни в каком виде (`grep` по `isMobilePlatform`/`RuntimePlatform`/`QualitySettings.` — ноль совпадений в `.cs`).

Механизм: новый компонент `Assets/Scripts/Runtime/PostFX/M3DVolumeMobileGate.cs`, вешается на `GameObject "M3D Volume"` тем же `PostProcessingSetup.cs`. `Awake()`: `if (Application.isMobilePlatform) GetComponent<Volume>().enabled = false;`. Никакого нового platform-detection слоя не вводим — `Application.isMobilePlatform` уже встроен в Unity, этого достаточно для «выключено до отдельного замера». Desktop/Editor — `Volume` активен как обычно.

Существующие `m_SupportsHDR: 1` на `Mobile_RPAsset` **не трогаем** — сам по себе HDR-буфер без Bloom-оверрайда почти бесплатен относительно overdraw/fill-rate из Techdebt 9; выключать его отдельным флагом не требуется для этого решения и не входит в скоуп (если профилирование позже покажет иначе — отдельный тикет).

#### 5. Связь с `hdrIntensity` (LUT-система, ADR-010) — ручная проверка, не код

После первого `Setup Post-Processing`: Play на любом пресете с `DebugFieldQuadSlot` (Gray-Scott-Boids уже использует heatmap), `hdrIntensity > 1` на слоте, подтвердить визуально, что горячие зоны засвечиваются заметнее холодных именно через реальный Bloom (не только через палитру). Формальный тест не пишем — тот же уровень риска, что у ADR-010 (закрыт без автотеста).

#### 6. Fluid2D разноцветный dye — явно не в этом ADR

Отдельный follow-up (два скалярных dye-поля / кастомный `FieldDebug` вариант / честный multi-channel dye) — не начинать в рамках этого тикета. Зафиксировать как отдельный будущий пункт в `Techdebt.md` группа D (roadmap), не смешивать со слоем HDR/Bloom/Tonemapping.

### Последствия

- (+) Первая реальная сквозная проверка `hdrIntensity` (ADR-010) через настоящий Bloom, не только через LUT.
- (+) Explicit, не молчаливое, решение по мобильному бюджету — соответствует уже принятой в проекте практике (desktop-first `plan-stable-fluid.md` §0, Techdebt 9).
- (+) Не создаёт нового platform-detection слоя сверх необходимого — `Application.isMobilePlatform` достаточно для этого решения.
- (−) `M3DVolumeMobileGate` не профилировался: неизвестно, есть ли смысл выключать Bloom именно так на реальном устройстве, если общий бюджет уже упирается в VFX Graph (Techdebt 9) сильнее. Замер на устройстве — отдельный тикет, не блокирует Слой 1.
- (−) Один общий `Volume`/профиль для всех `EffectAsset` (Fluid2D/Gray-Scott/Boids) — разные «настроения» пресетов не поддерживаются в v1; разделять только если реально понадобится.

### Альтернативы (отклонены)

**Вариант (б) — включить Bloom на обоих профилях с урезанными настройками через Renderer Asset.** Отклонено: технически нереализуемо в описанном виде (Bloom-параметры не живут на Renderer Asset), а честная реализация (второй Volume-профиль + platform-switch) — заметно больший объём работы без замера, оправдывающего его сейчас.

**Переиспользовать `DefaultVolumeProfile.asset`.** Отклонено: это тестовый ассет пакета URP Core, не проектный файл; риск конфликта с тестами пакета и путаницы «какой профиль на самом деле применяется».

**Отдельные профили сразу под каждый `EffectAsset` (Fluid2D/Gray-Scott/Boids).** Отклонено для v1: явный ЗТ-пункт «вне скоупа» — начинать с одного общего профиля, разделять только при реальной необходимости другого «настроения».

### Реализация (2026-09-05)

Рабочая сцена — [`Test1.unity`](../status.md), не шаблон `SampleScene`. MenuItem: `Tools/M3D/Setup Post-Processing (HDR + Bloom + ACES)` ([`PostProcessingSetup.cs`](../../Assets/Scripts/Editor/PostProcessingSetup.cs)). Runtime-гейт: [`M3DVolumeMobileGate.cs`](../../Assets/Scripts/Runtime/PostFX/M3DVolumeMobileGate.cs). Профиль: [`Assets/Settings/M3DVolumeProfile.asset`](../../Assets/Settings/M3DVolumeProfile.asset).

Errata к снимку на дату ADR: формулировка «в сцене нет ни одного Volume» была верна для `Test1` и неверна для `SampleScene` (там уже `Global Volume` + `SampleSceneProfile`: Bloom `1 / 0.25 / 0.5`, Tonemapping Neutral, Vignette `0.2`). Тот же `SampleSceneProfile` висел на `PC_RPAsset` и `Mobile_RPAsset` как `m_VolumeProfile`. В `Test1` у камеры было `renderPostProcessing = 0`.

Следствие: сценический `M3DVolumeMobileGate` не выключил бы Bloom на мобилке, пока quality-профиль несёт Bloom. Setup **снимает** `m_VolumeProfile` с обоих URP asset (`None`) и включает `renderPostProcessing` на камерах открытой сцены. `m_SupportsHDR` и Renderer Asset не пишутся. `SampleScene` / `SampleSceneProfile` / `DefaultVolumeProfile` не трогаются.

`VolumeProfile.Add<T>()` не персистит sub-asset сам: после `Add` Setup вызывает `AssetDatabase.AddObjectToAsset`. Повторный MenuItem идемпотентен (`TryGet` → не сбрасывает калибровку).

Калибровка Play — Fluid2D, слот `dye` (heatmap). Стартовые числа ADR §3 **не сдвигались**: Bloom `threshold=0.8`, `intensity=0.4`, `scatter=0.65`; Tonemapping `ACES` (`mode=2`). `hdrIntensity` на `Fluid2D.asset` остаётся `1`. Сквозная проверка ADR-010: `_HdrIntensity` на debug-quad запекается в `Rebuild`; при множителе > 1 горячий диск dye даёт тёплый Bloom-ореол, которого нет на холодном фоне.

Мобильный гейт на реальном устройстве не гонялся: в Editor `Application.isMobilePlatform == false` даже при Android build target. Подтверждение — по коду и по тому, что quality-профиль больше не несёт Bloom.
