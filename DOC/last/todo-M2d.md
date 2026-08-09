## LUT Palette + HDR Intensity

### Контекст

Прочитать ADR-010. Затрагиваемые файлы: `Assets/Shaders/GPU/FieldDebug.shader`, `Assets/Scripts/Runtime/DebugFieldQuadSlot.cs`, `Assets/Scripts/Runtime/FieldDebugQuadsBinder.cs`. Референс текущей структуры — уже читал их сам, ниже точные diff-точки.

### DoD

#### 1. `DebugFieldQuadSlot.cs` — новые поля

csharp

```csharp
[SerializeField] private Gradient lut = DefaultFireGradient(); // чёрный → тёмно-красный → оранжевый → жёлто-белый
[SerializeField, Min(0f)] private float hdrIntensity = 1f;
```

`DefaultFireGradient()` — статический хелпер, создающий разумный дефолт "пиро"-палитры (чёрный@0 → тёмно-красный@0.3 → оранжевый@0.6 → жёлто-белый@1.0), чтобы из коробки уже выглядело прилично, не требуя ручной настройки для первого теста.

#### 2. `FieldDebug.shader` — правки

hlsl

```hlsl
Properties
{
    _MainTex ("Field", 2D) = "black" {}
    _LutTex ("LUT", 2D) = "white" {}
    _Scale ("Color Scale", Float) = 2
    _HdrIntensity ("HDR Intensity", Float) = 1
    _VisualMode ("Visual Mode", Float) = 0
}
```

hlsl

```hlsl
TEXTURE2D(_LutTex);
SAMPLER(sampler_LutTex);
float _HdrIntensity;

// В frag, ветка ScalarHeatmap:
float d = saturate(max(s.r, 0.0) * _Scale);
float3 lutColor = SAMPLE_TEXTURE2D(_LutTex, sampler_LutTex, float2(d, 0.5)).rgb;
float3 color = lutColor * _HdrIntensity;
float alpha = saturate(d);
return half4(color, alpha * 0.7);
```

Ветку `VectorRg` (else-branch) **не трогать** — вне скоупа, как зафиксировано в ADR.

#### 3. `FieldDebugQuadsBinder.cs` — печь LUT в текстуру, биндить оба новых параметра

- Новый `private static readonly int LutTexId = Shader.PropertyToID("_LutTex");`, `private static readonly int HdrIntensityId = Shader.PropertyToID("_HdrIntensity");`.
- Метод `BakeLutTexture(Gradient gradient)`: создаёт `Texture2D(256, 1, TextureFormat.RGBA32, false)`, заполняет через `gradient.Evaluate(t)` для `t = i/255f`, `Apply()`. Печь **один раз** при создании материала слота (`Setup()`), не в `Update()`/per-frame цикле — это не generated-каждый-кадр ресурс, кэшируется как обычная текстура на весь жизненный цикл материала.
- В месте, где сейчас создаётся `Material` для слота (`new Material(shader) {...}`), добавить `material.SetTexture(LutTexId, BakeLutTexture(slot.lut))` и `material.SetFloat(HdrIntensityId, slot.hdrIntensity)`.
- **Утечка ресурсов**: испечённая `Texture2D` должна быть освобождена (`Object.Destroy`) там же, где сейчас уничтожается `Material` слота (`Dispose`/`Teardown` биндера) — не забудь, у тебя уже есть паттерн для `Material.Destroy` в этом же файле, симметрично добавь для LUT-текстуры.

### Тесты

Формальный ADR/MCP smoke не обязателен — это чисто визуальный/рендер-компонент (тот же уровень риска, что и исходная задача "Generic Field Debug Quad", которая тоже обошлась без формальных тестов). Ручная проверка: применить `DefaultFireGradient()` дефолт к существующему `Gray-Scott.asset`, убедиться, что палитра визуально соответствует "огонь" (не старая жёстко-зашитая warm-формула), прогнать с `hdrIntensity=1` (не должно визуально отличаться от текущего поведения по яркости, только по палитре) и с `hdrIntensity=3-5` (заметно ярче в горячих зонах, если HDR+Bloom включены на камере — можно попросить пользователя подтвердить самостоятельно, раз настройка Volume вне скоупа этого тикета).

### Вне скоупа

LUT/HDR для `VectorRg`-режима. Настройка самого URP Volume (Bloom/Tonemapping/HDR на камере) — это делает пользователь сам, отдельно, никак не через код. Trail/persistence buffer (отдельный, следующий тикет). Additive blend mode вместо текущего alpha-blend (можно поднять отдельно, если alpha-blend будет визуально плохо стыковаться с несколькими горячими quad'ами рядом — не для этого тикета).
