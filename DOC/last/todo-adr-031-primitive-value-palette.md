# ТЗ: ADR-031 Primitive-only present + LUT-палитра по `value`

**Статус:** код и EditMode зелёные (2026-09-25). Play-проверка разноцветности по курсу — оператор.

Прочитать [`ADR-031-Primitive-Only-And-Value-Palette.md`](../ADR/ADR-031-Primitive-Only-And-Value-Palette.md) перед кодом.

**Не трогать:** `VfxParticleBinder` (класс остаётся, только не вызывается из `SetupBinders`), `ParticleRenderMode` enum и его меню (`Adr030PrimitiveRenderSetup.cs`) — не удалять, не помечать Obsolete. `Boids_mk1` порядок пассов/калибровку (ADR-012). Fluid/Gray-Scott пассы и пресеты. `FieldDebugQuadsBinder`. Spatial hash / `teamId` — не заводить.

---

## 1. `SimulationWorld` — Primitive безусловно + null-guard

### `Assets/Scripts/Runtime/SimulationWorld.cs`

**1a. `Build()` — `VisualEffect` необязателен.**

Сейчас (~строка 146):

```csharp
if (effect == null || visualEffect == null || passLibrary == null || passLibrary.Length == 0)
{
    Debug.LogError(
        "SimulationWorld: EffectAsset, VisualEffect and pass library compute shaders must be assigned.",
        this);
    enabled = false;
    return;
}
```

Убрать `visualEffect == null` из условия, обновить текст сообщения:

```csharp
if (effect == null || passLibrary == null || passLibrary.Length == 0)
{
    Debug.LogError(
        "SimulationWorld: EffectAsset and pass library compute shaders must be assigned.",
        this);
    enabled = false;
    return;
}
```

`GetComponent<VisualEffect>()`-фолбэк перед этой проверкой не трогать — если компонент есть на объекте, `visualEffect` всё равно заполнится.

**1b. `SetupBinders()` — всегда `PrimitiveParticleBinder`, null-guard на `visualEffect`.**

Текущий код (~строка 536):

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
    // Field-only: stop spawning leftover VFX points from a previous effect.
    if (visualEffect.HasFloat("SpawnCount"))
    {
        visualEffect.SetFloat("SpawnCount", 0f);
    }

    visualEffect.Reinit();
}
```

Заменить на:

```csharp
if (particles.Count > 0)
{
    PrimitiveParticleBinder primitiveBinder = new PrimitiveParticleBinder(
        effect.ParticleSize, effect.ParticleColor, effect.ParticleGradient, effect.ParticleValueScale);
    primitiveBinder.Initialize(context);
    binders.Add(primitiveBinder);

    StopVfxPlayback();
}
else
{
    // Field-only: stop spawning leftover VFX points from a previous effect.
    StopVfxPlayback();
}

IReadOnlyList<DebugFieldQuadSlot> debugQuads = effect.DebugFieldQuads;
// ... (остальной метод не меняется)
```

Добавить приватный метод в том же классе:

```csharp
private void StopVfxPlayback()
{
    if (visualEffect == null)
    {
        return;
    }

    if (visualEffect.HasFloat("SpawnCount"))
    {
        visualEffect.SetFloat("SpawnCount", 0f);
    }

    visualEffect.Reinit();
}
```

`VfxParticleBinder`-ветка из `SetupBinders` полностью убирается — класс `VfxParticleBinder.cs` не трогать, он просто больше не инстанцируется World-ом. `using UnityEngine.VFX;` в `SimulationWorld.cs` может оказаться неиспользуемым — не удалять, если `visualEffect` (тип `VisualEffect`) всё ещё используется как поле.

---

## 2. `EffectAsset` — `particleGradient` / `particleValueScale`

### `Assets/Scripts/Runtime/EffectAsset.cs`

Рядом с `particleSize`/`particleColor` (~строка 28):

```csharp
[SerializeField] private float particleSize = 0.05f;
[SerializeField] private Color particleColor = Color.white;
[SerializeField] private Gradient particleGradient;
[SerializeField, Min(0f)] private float particleValueScale = 1f;
```

Публичные аксессоры рядом с существующими:

```csharp
public float ParticleSize => particleSize;
public Color ParticleColor => particleColor;
public Gradient ParticleGradient => particleGradient;
public float ParticleValueScale => particleValueScale;
```

`particleGradient == null` по умолчанию на всех старых ассетах — обрабатывается в биндере (см. §3), не здесь.

---

## 3. `PrimitiveParticleBinder` — LUT bake + `value`-буфер

### `Assets/Scripts/Runtime/PrimitiveParticleBinder.cs`

Новый конструктор с двумя доп. параметрами. Существующий вызов `new PrimitiveParticleBinder(0.05f, Color.white)` в `Assets/Tests/Editor/PrimitiveParticleBinderTests.cs` нужно обновить **в том же изменении** — иначе тест не компилируется:

```csharp
private const int LutWidth = 256;

private static readonly int PositionsId = Shader.PropertyToID("_Positions");
private static readonly int SizeId = Shader.PropertyToID("_Size");
private static readonly int ColorId = Shader.PropertyToID("_Color");
private static readonly int ValuesId = Shader.PropertyToID("_Values");
private static readonly int LutTexId = Shader.PropertyToID("_LutTex");
private static readonly int ScaleId = Shader.PropertyToID("_Scale");
private static readonly int UseLutId = Shader.PropertyToID("_UseLut");

private readonly float size;
private readonly Color color;
private readonly Gradient gradient;
private readonly float valueScale;
private Texture2D lutTexture;
private GraphicsBuffer valuesBuffer; // может быть null — тогда _UseLut=0
private bool hasValueAttribute;

public PrimitiveParticleBinder(float size, Color color, Gradient gradient, float valueScale)
{
    this.size = size;
    this.color = color;
    this.gradient = gradient;
    this.valueScale = valueScale;
}
```

В `Initialize` — после создания `material`/`props`, до `renderParams`:

```csharp
hasValueAttribute = context.Particles.TryGet(BuiltinAttributes.Value, out valuesBuffer);
if (hasValueAttribute)
{
    lutTexture = BakeLutTexture(gradient ?? DebugFieldQuadSlot.DefaultFireGradient());
}
```

`BakeLutTexture` — скопировать приём из `FieldDebugQuadsBinder.BakeLutTexture` (256×1, `RGBA32`, `wrapMode=Clamp`, `filterMode=Bilinear`, `hideFlags=HideAndDontSave`), сделать `private static` методом в этом файле (не выносить в общий класс — дублирование двух одинаковых 15-строчных методов дешевле лишней связи между `Runtime`-классами, как и решено при ADR-010 vs ADR-030).

**Важно для тестируемости.** `FieldDebugQuadsBinder.BakeLutTexture` заканчивается `texture.Apply(false, true)` — `makeNoLongerReadable=true`, после этого пиксели `Texture2D` **не читаются** с CPU (`GetPixels` бросит). Тест на «0 и 1 попадают в разные концы градиента» (см. §8) не может сверяться с самой текстурой. Поэтому построение пикселей выносится в отдельный `internal static` метод, testable без текстуры:

```csharp
internal static Color[] BuildLutPixels(Gradient gradient, int width)
{
    Color[] pixels = new Color[width];
    float inv = 1f / (width - 1);
    for (int i = 0; i < width; i++)
    {
        pixels[i] = gradient.Evaluate(i * inv);
    }

    return pixels;
}

private static Texture2D BakeLutTexture(Gradient gradient)
{
    Texture2D texture = new Texture2D(LutWidth, 1, TextureFormat.RGBA32, false, true)
    {
        name = "M3D_ParticleBillboard_LUT",
        wrapMode = TextureWrapMode.Clamp,
        filterMode = FilterMode.Bilinear,
        hideFlags = HideFlags.HideAndDontSave,
    };

    texture.SetPixels(BuildLutPixels(gradient, LutWidth));
    texture.Apply(false, true);
    return texture;
}
```

`internal` — доступно тесту без доп. настройки: в `Assets/Tests/Editor` нет своего `.asmdef`, папка компилируется в стандартный `Assembly-CSharp-Editor`, а `Assets/Scripts/AssemblyInfo.cs` уже объявляет `[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]`.

В `Execute` — после существующих `props.SetBuffer(PositionsId, positions)` / `SetFloat(SizeId, ...)` / `SetColor(ColorId, ...)`:

```csharp
if (hasValueAttribute)
{
    props.SetBuffer(ValuesId, valuesBuffer);
    props.SetTexture(LutTexId, lutTexture);
    props.SetFloat(ScaleId, valueScale);
    props.SetFloat(UseLutId, 1f);
}
else
{
    props.SetFloat(UseLutId, 0f);
}
```

`valuesBuffer` не переприсваивать в `Execute` — атрибут не может появиться посреди жизни мира без `Rebuild`, буфер стабилен, как у `VfxParticleBinder.Execute` (комментарий "Particle GraphicsBuffers are stable until Teardown").

`Dispose()` — добавить освобождение `lutTexture` рядом с `material` (тот же `Application.isPlaying` ветвление):

```csharp
public void Dispose()
{
    if (material != null)
    {
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

    if (lutTexture != null)
    {
        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(lutTexture);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(lutTexture);
        }

        lutTexture = null;
    }
}
```

`valuesBuffer` — **не** освобождать здесь: это тот же `GraphicsBuffer`, которым владеет `ParticleSet` (как `positions`), не копия.

---

## 4. `ParticleBillboard.shader` — сэмплинг LUT

### `Assets/Shaders/GPU/ParticleBillboard.shader`

Добавить свойства и буфер/текстуру:

```hlsl
Properties
{
    _Size ("Size", Float) = 0.05
    _Color ("Color", Color) = (1,1,1,1)
    _Scale ("Value Scale", Float) = 1
}
```

(`_Values`, `_LutTex`, `_UseLut` — не выставлять в `Properties`, они программные, как `_Positions` сегодня.)

```hlsl
StructuredBuffer<float3> _Positions;
StructuredBuffer<float> _Values;
Texture2D<float4> _LutTex;
SamplerState sampler_LutTex;
float _Size;
float4 _Color;
float _Scale;
float _UseLut;
```

`Varyings` — добавить `instanceID`:

```hlsl
struct Varyings
{
    float4 positionCS : SV_POSITION;
    nointerpolation uint instanceID : TEXCOORD0;
};
```

`nointerpolation` обязателен: у всех шести вершин одного квада `instanceID` одинаковый, но без этого квалификатора часть компиляторов интерполирует/размазывает `uint` по треугольнику вместо честного flat-passthrough.

`vert` — прокинуть `instanceID` в `output`:

```hlsl
Varyings output;
output.positionCS = TransformWorldToHClip(posWS);
output.instanceID = instanceID;
return output;
```

`frag`:

```hlsl
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

Если `_UseLut=0`, `_Values`/`_LutTex` не биндятся `PrimitiveParticleBinder`-ом — на GPU это ок (Unity подставит дефолтный пустой буфер/текстуру для несвязанных слотов, ветка `_UseLut > 0.5` их не читает).

---

## 5. Писатели `value`: `SpeedToValuePass` / `HeadingToValuePass`

### `Assets/Shaders/GPU/Passes/DynamicsPasses.compute`

Добавить в шапку файла:

```hlsl
#pragma kernel SpeedToValue
#pragma kernel HeadingToValue
```

Буфер (рядом с `heading`):

```hlsl
RWStructuredBuffer<float> value;
```

Уникальный uniform для `SpeedToValue`:

```hlsl
float SpeedRef;
```

Кернелы (в конец файла):

```hlsl
[numthreads(THREADS, 1, 1)]
void SpeedToValue(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount)
    {
        return;
    }

    float speed = length(velocity[id.x]);
    value[id.x] = saturate(speed / max(SpeedRef, 1e-6));
}

#define TWO_PI 6.28318530718

[numthreads(THREADS, 1, 1)]
void HeadingToValue(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount)
    {
        return;
    }

    float3 h = heading[id.x];
    float angle = atan2(h.z, h.x); // [-pi, pi]
    value[id.x] = angle / TWO_PI + 0.5; // [0, 1]
}
```

### `Assets/Scripts/Passes/DynamicsPasses.cs`

Добавить в `AttrSets` (`Assets/Scripts/Runtime/SimPass.cs`, рядом с `Heading`/`HeadingVelocity`):

```csharp
public static readonly AttributeId[] Value = { BuiltinAttributes.Value };
public static readonly AttributeId[] VelocityValue = { BuiltinAttributes.Velocity, BuiltinAttributes.Value };
public static readonly AttributeId[] HeadingValue = { BuiltinAttributes.Heading, BuiltinAttributes.Value };
```

Классы (в `DynamicsPasses.cs`, рядом с `HeadingSteerPass`):

```csharp
/// <summary>Present writer: value = saturate(|velocity| / SpeedRef). No dt.</summary>
[Serializable]
public sealed class SpeedToValuePass : ParticleKernelPass
{
    private static readonly int SpeedRefId = Shader.PropertyToID("SpeedRef");

    [SerializeField, Min(1e-4f)] private float speedRef = 1f;

    public float SpeedRef
    {
        get => speedRef;
        set => speedRef = value;
    }

    public override string DisplayName => "Speed To Value";
    public override PassCategory Category => PassCategory.Dynamics;
    protected override string KernelName => "SpeedToValue";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Velocity;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Value;

    protected override void SetParams(SimContext context, float deltaTime)
    {
        SetFloat(context, SpeedRefId, speedRef);
    }
}

/// <summary>Present writer: value = heading angle in XZ, mapped to [0,1]. No dt.</summary>
[Serializable]
public sealed class HeadingToValuePass : ParticleKernelPass
{
    public override string DisplayName => "Heading To Value";
    public override PassCategory Category => PassCategory.Dynamics;
    protected override string KernelName => "HeadingToValue";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.Heading;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Value;
}
```

`speedRef` дефолт `1f` — плейсхолдер. **Этот пасс не идёт на `Boids_mk1`** (см. §7) — калибровать `speedRef`, когда появится эффект с реально разной скоростью частиц.

---

## 6. `TeamToValuePass` — класс без подключения

Добавить в `DynamicsPasses.cs`, **без** kernel в `.compute` и без ссылок из какого-либо `.asset`:

```csharp
/// <summary>
/// Reserved for a future teamId ticket (spatial hash / neighbor flock, not opened here).
/// No compute kernel yet by design. Reads is empty — this does NOT fail Build via an
/// undeclared-attribute error. The real guard: ParticleKernelPass.Initialize calls
/// SimContext.FindKernel(KernelName) unconditionally, which throws
/// InvalidOperationException when no .compute in the pass library has this kernel.
/// SimulationWorld.Build wraps pass.Initialize in try/catch, logs the error, tears
/// down and disables the world — so attaching this pass to a real asset fails Build
/// loudly ("Kernel 'TeamToValue' not found ..."), not silently.
/// </summary>
[Serializable]
public sealed class TeamToValuePass : ParticleKernelPass
{
    public override string DisplayName => "Team To Value";
    public override PassCategory Category => PassCategory.Dynamics;
    protected override string KernelName => "TeamToValue";
    public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
    public override IReadOnlyList<AttributeId> Writes => AttrSets.Value;
}
```

Не добавлять `#pragma kernel TeamToValue` в `.compute`. Класс существует для будущего тикета (`teamId`), никуда не подключать. **Не** писать тест, вызывающий `Initialize` с настоящим `SimContext`, содержащим этот пасс в списке — он обязан бросить, это и есть контракт (см. §8).

---

## 7. Пресет `Boids_mk1` — opt-in `HeadingToValuePass`

Через Unity (`SerializedObject`/Inspector), не YAML rid, по образцу `Adr030PrimitiveRenderSetup.cs` / `Adr012BoidsMk1Setup.cs`.

Добавить в конец списка пассов `Assets/Effects/Boids_mk1.asset` (после `BoxBoundsPass`): **`HeadingToValuePass`** — не `SpeedToValuePass`. Причина: после `HeadingSteer` (ADR-012) `|velocity|` у всех частиц равно `CruiseSpeed=4` — `SpeedToValuePass` дал бы почти константный `value` и однотонную палитру, а `heading` у частиц реально разный (align/cohesion/separation расходятся по направлению). `SpeedToValuePass` остаётся в каталоге (см. §5) для будущих эффектов с настоящим разбросом скорости — на `Boids_mk1` не подключается вовсе, не только "по умолчанию выключен".

`particleGradient`/`particleValueScale` на `Boids_mk1.asset` — оставить дефолт (`null`→fire-gradient, `1f`), либо явно выставить в инспекторе при ручной проверке.

**Не создавать** новый Create-пункт меню для этого — правка существующего ассета, не фабрика.

---

## 8. Тесты (`Assets/Tests/Editor/`)

| Файл | Проверки |
| --- | --- |
| `SpeedToValuePassTests` | Category Dynamics, KernelName `SpeedToValue`, default `speedRef=1`, Reads Velocity, Writes Value — только reflection (`new SpeedToValuePass()`, без `Initialize`), по шаблону `HeadingSteerPassTests` |
| `HeadingToValuePassTests` | Category Dynamics, KernelName `HeadingToValue`, Reads Heading, Writes Value — только reflection |
| `TeamToValuePassTests` | Category Dynamics, KernelName `TeamToValue`, Writes Value, Reads empty (`AttrSets.None`) — **только** reflection-контракт, как `HeadingSteerPassTests`: создать `new TeamToValuePass()` и проверить свойства/`KernelName` через `typeof(ParticleKernelPass).GetProperty("KernelName", ...)`. **Не** вызывать `pass.Initialize(context)` с настоящим `SimContext` — это по контракту бросит `InvalidOperationException` (`SimContext.FindKernel`), а не молча оставит `kernel.IsValid == false`. Если нужен тест на «случайное подключение ломает Build» — писать как `Assert.Throws<InvalidOperationException>(() => pass.Initialize(context))` явно, отдельным тестом, не смешивая с контрактной проверкой |
| `PrimitiveParticleBinderTests` (расширить существующий) | Обновить существующий вызов конструктора на 4 аргумента (см. §3). Без `value`-атрибута: `_UseLut` ставится в **0** (не «не устанавливается» — материал получает явный `0f`), биндер не падает. С `value`-атрибутом: `_UseLut=1`, LUT bake происходит один раз в `Initialize`, не повторяется в `Execute` (сверить, что ссылка на `Texture2D` не меняется между двумя вызовами `Execute`) |
| `PrimitiveParticleBinderLutTests` (новый, без GPU/Texture2D) | `PrimitiveParticleBinder.BuildLutPixels(gradient, 256)` (internal, см. §3): пиксель `[0]` соответствует `gradient.Evaluate(0)`, пиксель `[255]` — `gradient.Evaluate(1)`, и эти два цвета отличаются для градиента с разными концами (например, `DebugFieldQuadSlot.DefaultFireGradient()`: чёрный на 0, жёлто-белый на 1) |
| `SimulationWorldBuildTests` (новый или расширить существующий smoke) | `Build()` без `VisualEffect`-компонента на объекте не бросает и не даёт `enabled=false`, при наличии `EffectAsset` с частицами > 0 и валидной pass library |

Контракт пассов — по шаблону `HeadingSteerPassTests`/`ClearVelocityPassTests` (reflection на свежесозданном экземпляре, без вызова `Initialize`/`Execute` с реальным `SimContext`, без GPU readback).

---

## 9. Документация

- `DOC/status.md` — новая запись (после ADR-030): ADR-031 Primitive-only + value palette.
- `DOC/pass-catalog.md` — `SpeedToValuePass`/`HeadingToValuePass` в раздел Dynamics; явно: без `dt`, писатель `value`, present их не считает. `TeamToValuePass` — отдельная строка с пометкой "не подключён, нет kernel".
- `DOC/capabilities.md` — Primitive теперь безусловный путь present (не opt-in); `value`-палитра как общий механизм.
- `DOC/last/Techdebt.md` — пункт 10 (M2d LUT/trail): статус "LUT-часть закрыта ADR-031, trail (M2d.2) остаётся".
- `DOC/getting-started.md` — если там упоминается обязательность `VisualEffect` на `SimulationWorld`, обновить.

---

## 10. Ручная проверка

0. `Test1`: `SimulationWorld.Effect` = `Boids_mk1` (с добавленным `HeadingToValuePass`), Rebuild.
1. Play: рой рисуется квадами (Primitive), без второго VFX-облака поверх.
2. Частицы с разным курсом (`heading`) читаются разным цветом — не однотонное облако.
3. Временно снять `VisualEffect`-компонент с объекта в сцене (или создать тестовый `GameObject` без него) — `SimulationWorld` собирается и рисует Primitive без ошибок в консоли.
4. `HybridTouchField`/`AgentFieldEcho`/`Gray-Scott-Boids`/`Gray-Scott-Agents` — проверить, что рисуются (теперь всегда Primitive), даже без `value`-атрибута (плоский `_Color`, как раньше визуально был VFX-цвет — парность не гейт, см. ADR-031 §5 «Отклонённые варианты»).
5. Попытаться (вручную, в Play, не в продовом ассете) добавить `TeamToValuePass` в список пассов какой-нибудь копии эффекта и нажать Rebuild — ожидается явная ошибка Build с текстом `Kernel 'TeamToValue' not found ...`, мир выключается (`enabled=false`), не тихий сбой.

`SpeedToValuePass` в ручной проверке не участвует — он не подключён ни к одному ассету в этом тикете (см. §5/§7).

---

## Definition of Done

- `SimulationWorld.Build` без `VisualEffect` не ошибка; `SetupBinders` всегда `PrimitiveParticleBinder`, обе ветки за null-guard на `visualEffect`.
- `VfxParticleBinder`/`ParticleRenderMode`/меню Enable-Disable — код не тронут, просто не вызывается.
- `EffectAsset.ParticleGradient`/`ParticleValueScale`; `PrimitiveParticleBinder` печёт LUT один раз, сэмплит `value`, fallback на `_Color` без атрибута.
- `SpeedToValuePass`/`HeadingToValuePass` в коде и в Pass Library; `TeamToValuePass` — класс без kernel, никуда не подключён, случайное подключение валит Build явной ошибкой (не тихо).
- `Boids_mk1` — `HeadingToValuePass` добавлен opt-in, читаемая разноцветность по курсу в Play. `SpeedToValuePass` не подключён ни к одному ассету (остаётся в каталоге).
- EditMode-тесты §8 зелёные. Живые доки (§9) обновлены.
- Существующие демки (`HybridTouchField` и др.) рисуются Primitive без ошибок (плоским цветом, если без `value`).

---

## Вне скоупа

Spatial hash · `teamId` (реальное подключение `TeamToValuePass`) · `BoidNeighborForcePass` / `SeedBoidGroupsPass` / `Boids_hash.asset` · trail/persistence (M2d.2) · `hdrIntensity` на частицах · ориентация квада по `heading` (facing) · мобильный Bloom-гейт для Primitive · физическое удаление `VfxParticleBinder`/`ParticleRenderMode` · перевод остальных демок на `value`-палитру.
