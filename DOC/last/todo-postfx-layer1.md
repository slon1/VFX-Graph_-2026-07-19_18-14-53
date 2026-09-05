## ТЗ для программиста — Слой 1: HDR Camera + Bloom + ACES Volume

**Закрыто 2026-09-05.** Реализовано: [ADR-025](../ADR/ADR-025-PostFX-HDR-Bloom-ACES.md). Это ТЗ не переоткрывать. Факт относительно черновика: `m_VolumeProfile` снят с PC/Mobile RP asset (иначе гейт не глушил Bloom); `VolumeProfile.Add<T>()` персистится через `AddObjectToAsset`.

Роль этого документа: собрать скриптовый, коммитящийся сетап пост-обработки по [ADR-025](../ADR/ADR-025-PostFX-HDR-Bloom-ACES.md). Применяется к любому `EffectAsset` (Fluid2D, Gray-Scott-Boids, Boids_mk1), не к одному конкретному пресету.

Прочитать ADR-025 целиком **до кода**. Без этого легко: (а) переиспользовать `DefaultVolumeProfile.asset` вместо нового файла, (б) попытаться настроить Bloom «на Renderer Asset» — там этого поля нет, (в) слепо переписать уже верные `m_HDR`/`m_SupportsHDR` флаги.

Зафиксировано — не начинать, пока это не ясно:

1. **HDR-флаги камеры и обоих URP asset уже включены.** Проверено по факту: `SampleScene.unity` Main Camera `m_HDR: 1`; `PC_RPAsset.asset` и `Mobile_RPAsset.asset` оба `m_SupportsHDR: 1`. Реальный гэп — отсутствие `Volume` в сцене, не HDR-флаги. Скрипт **проверяет и логирует** эти флаги, не переписывает их безусловно.
2. **`Assets/Settings/DefaultVolumeProfile.asset` — не наш файл.** Это тестовый ассет пакета URP Core (внутри `CopyPasteTestComponent`, `TestAnimationCurveVolumeComponent`, `OasisFogVolumeComponent` и т.п. — стандартные тесты пакета). Не открывать в инспекторе с намерением править, не ссылаться на него из кода. Новый профиль — `Assets/Settings/M3DVolumeProfile.asset`.
3. **Мобильный вариант — explicit (a).** HDR+Bloom включаются только на desktop-треке. На мобильном рантайм-гейт выключает сам `Volume` (см. шаг 2 ниже). Не пытаться настроить Bloom «отдельно на Renderer Asset» — там нет таких полей (`bloom.downscale`/`maxIterations` — свойства `VolumeComponent`, не `UniversalRendererData`).
4. **`VolumeProfile.Add<T>()` не идемпотентен сам по себе.** Каждый `Add` обязан быть за `Has<T>` проверкой, иначе повторный запуск `MenuItem` даёт дубли оверрайдов и сбрасывает откалиброванные значения.
5. **Разноцветный dye Fluid2D — вне скоупа этого тикета.** Не трогать `AdvectScalarPass`/`FieldDebug.shader` мультиканальность.

Референсы по месту:

- `Assets/Scripts/Editor/M3DDemoTools.cs` — образец `MenuItem` + работа через `AssetDatabase`/`SerializedObject` (для этого тикета `SerializedObject` не нужен — `VolumeProfile`/`Volume` настраиваются через публичный C#-API `UnityEngine.Rendering`, не через сериализованные поля).
- `Assets/Shaders/GPU/FieldDebug.shader` — `_HdrIntensity` уже существует (ADR-010), в этом тикете не меняется.
- `Assets/Scripts/Runtime/DebugFieldQuadSlot.cs` — поле `hdrIntensity`, используется для ручной сквозной проверки (шаг 4).
- `Assets/Settings/PC_RPAsset.asset` / `PC_Renderer.asset`, `Mobile_RPAsset.asset` / `Mobile_Renderer.asset` — существующие ассеты, найти по пути напрямую (`AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>`), **не** искать «текущий через `GraphicsSettings.currentRenderPipeline`» как единственный источник — в Editor «текущий» зависит от активного Quality tier и может не включать второй ассет вообще. Найти оба явно по путям, залогировать `m_SupportsHDR` каждого.

Код кернелов/GPU-пассов не пишет. Формального ADR/автотеста для самого визуального эффекта не требуется — тот же уровень риска, что у ADR-010/Field Debug Quad (оба закрыты без формального ADR-цикла на визуальную часть); но **сам факт мобильного решения** обязан попасть в `Techdebt.md` (шаг 3 ниже) — не молчаливый умолчание.

---

### Шаг 1 — `PostProcessingSetup.cs`

Новый файл `Assets/Scripts/Editor/PostProcessingSetup.cs`, рядом с `M3DDemoTools.cs`.

```csharp
[MenuItem("Tools/M3D/Setup Post-Processing (HDR + Bloom + ACES)")]
public static void SetupPostProcessing()
{
    LogHdrState();               // читает и логирует m_SupportsHDR обоих URP asset, не пишет
    Volume volume = EnsureVolumeObject();
    VolumeProfile profile = EnsureVolumeProfile(volume);
    EnsureBloom(profile);
    EnsureTonemapping(profile);
    EnsureColorAdjustments(profile);
    EnsureMobileGate(volume.gameObject);
    Debug.Log("M3D: post-processing setup done (or already present, idempotent).");
}
```

**`LogHdrState()`.** Загрузить `PC_RPAsset.asset` и `Mobile_RPAsset.asset` по явным путям (`Assets/Settings/PC_RPAsset.asset`, `Assets/Settings/Mobile_RPAsset.asset`), достать `UniversalRenderPipelineAsset` и через публичный API (не `SerializedObject` по приватному имени, если есть публичное свойство поддержки HDR у `RenderPipelineAsset`/`UniversalRenderPipelineAsset` — использовать его; если публичного нет, `SerializedObject` на `m_SupportsHDR` только для чтения, **не для записи**) залогировать текущее значение каждого. Не менять эти ассеты в этом тикете.

**`EnsureVolumeObject()`.** Искать в открытой сцене `GameObject` с именем `"M3D Volume"` и компонентом `Volume`. Если нет — создать новый `GameObject("M3D Volume")`, добавить `Volume`, `isGlobal = true`. Не искать по всей сцене «любой Volume» — конкретно по имени, чтобы не подцепить чужой Volume, если он появится позже по другой причине.

**`EnsureVolumeProfile(Volume volume)`.** Если `volume.sharedProfile != null` — использовать его (идемпотентность: не создавать второй профиль поверх существующего). Иначе: если файл `Assets/Settings/M3DVolumeProfile.asset` уже существует на диске — загрузить (`AssetDatabase.LoadAssetAtPath<VolumeProfile>`) и присвоить `volume.sharedProfile`. Иначе — создать новый `VolumeProfile` через `ScriptableObject.CreateInstance<VolumeProfile>()`, `AssetDatabase.CreateAsset` по этому пути, присвоить.

**`EnsureBloom`/`EnsureTonemapping`/`EnsureColorAdjustments`.** Паттерн `Has<T>` → иначе `Add<T>(overrideState: true)` + стартовые значения (см. ADR-025 §3). Если компонент уже есть (повторный запуск) — **не трогать** его текущие значения, только залогировать, что он уже присутствует. Так решается требование «идемпотентно, не сбрасывает калибровку».

**`EnsureMobileGate(GameObject volumeGo)`.** Если на `volumeGo` уже есть `M3DVolumeMobileGate` — no-op. Иначе `volumeGo.AddComponent<M3DVolumeMobileGate>()`.

### Шаг 2 — `M3DVolumeMobileGate.cs` (runtime, не Editor)

Новый файл `Assets/Scripts/Runtime/PostFX/M3DVolumeMobileGate.cs`:

```csharp
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(Volume))]
public sealed class M3DVolumeMobileGate : MonoBehaviour
{
    private void Awake()
    {
        if (Application.isMobilePlatform)
        {
            GetComponent<Volume>().enabled = false;
        }
    }
}
```

Ничего сложнее не вводить: не искать активный `RenderPipelineAsset`, не проверять `QualitySettings` — `Application.isMobilePlatform` уже встроен и достаточен для решения (a) из ADR-025 §4. В Editor / desktop-плеере `Volume` остаётся включённым.

### Шаг 3 — документация (часть тикета)

- `DOC/last/Techdebt.md`: новая запись в группе C (по образцу 8/8b/…) — «Bloom на мобильном профиле выключен рантайм-гейтом (`M3DVolumeMobileGate`) до отдельного замера; `m_SupportsHDR` на `Mobile_RPAsset` не тронут». Не путать с уже существующим пунктом 9 (VFX Graph) — сослаться на него как на смежный риск, не дублировать текст.
- `DOC/plan-stable-fluid.md`: одна строка-ссылка на этот трек как смежный/параллельный (не fluid-specific, поэтому не встраивается в таблицу F0/F1/F2 по номеру) — формулировка уже добавлена планировщиком, при необходимости актуализировать после факта реализации (проставить «реализовано»).
- `DOC/ADR/ADR-025-PostFX-HDR-Bloom-ACES.md`: статус `Принято, к реализации` → `Принято (реализовано)` после DoD; если калибровка (шаг 4) сдвинула стартовые значения Bloom — дописать фактические числа в ADR (по аналогии с тем, как ADR-022 фиксирует финальные `Iterations`/`DissipationRate`).
- `DOC/capabilities.md` — если там есть раздел про рендер/пост-обработку, добавить строку про доступный Volume/Bloom; если такого раздела нет, не создавать искусственно, пропустить.

### Шаг 4 — ручная проверка (Play, не автотест)

MCP для сцены доступен (`user-unity`) — предпочтительно гонять шаги через MCP-инструменты работы со сценой, а не только руками в редакторе; если по ходу работы MCP окажется недоступен — явно предупредить об этом в отчёте, не тихо переключаться на голый редактор.

1. `Tools/M3D/Setup Post-Processing`. Проверить в Console: лог текущих `m_SupportsHDR` обоих RP asset, лог создания/переиспользования `M3D Volume` + `M3DVolumeProfile.asset`.
2. Повторный запуск того же `MenuItem` — Console должна показать «уже присутствует» на каждом шаге, ассет `M3DVolumeProfile.asset` не должен задваивать оверрайды (открыть в инспекторе, убедиться в одном `Bloom`/`Tonemapping`/`ColorAdjustments`).
3. Play на `Gray-Scott-Boids` (у него уже есть `DebugFieldQuadSlot` heatmap) или на `Fluid2D`: подтвердить визуально свечение в ярких зонах, отсутствие артефактов (перезасветка в общий белый, banding на градиенте LUT).
4. Поставить `hdrIntensity > 1` на debug-quad слоте одного из пресетов (Gray-Scott-Boids, поле `U` или `V`) и подтвердить, что с включённым Bloom горячие зоны засвечиваются заметно ярче, чем при `hdrIntensity = 1` — это первая сквозная проверка ADR-010 через реальный Bloom.
5. Desktop (Editor/Standalone) — Bloom виден. Если есть возможность собрать/эмулировать мобильную платформу (`Application.isMobilePlatform == true`) — подтвердить, что `Volume` выключен, Bloom не рендерится. Если такой возможности нет прямо сейчас — явно написать в отчёте «гейт не проверен на реальном мобильном рантайме, только по чтению кода», не выдавать это за подтверждённый факт.

### Отчёт

1. Diff: `PostProcessingSetup.cs`, `M3DVolumeMobileGate.cs`, `Assets/Settings/M3DVolumeProfile.asset` (+ `.meta`), правка сцены (новый `GameObject "M3D Volume"`), документация из шага 3. Явно подтвердить: `PC_RPAsset.asset`/`Mobile_RPAsset.asset`/`PC_Renderer.asset`/`Mobile_Renderer.asset` — без изменений (только прочитаны).
2. Стартовые значения Bloom/Tonemapping, если калибровка в Play их сдвинула — итоговые числа.
3. Идемпотентность: подтверждение, что повторный запуск `MenuItem` не создаёт дублей.
4. Visual: скриншот/описание свечения на выбранном пресете, до/после `hdrIntensity`.
5. Мобильный гейт: подтверждён на реальном рантайме или только по чтению кода (см. шаг 4.5).

Если после `Setup` в сцене Bloom визуально не виден вообще (не только «слабо») — первым делом проверить `m_RenderPostProcessing` на самой `Camera` (должно быть `1` — в `SampleScene.unity` уже так) и что `Volume.isGlobal = true`, а не порядок `threshold`/`intensity`.
