# ТЗ: привязка пустых LUT-ресурсов в PrimitiveParticleBinder

**Статус:** EditMode: путь без `value` зелёный (2026-10-03). Тест с `value` может быть красным из-за чужих `M3D_ParticleBillboard_LUT`. Замер S10 с видимыми квадами в [`status.md`](../status.md).
**Исполнитель:** одна правка биндера. Замер S10 не делать. Пробник, хэш и пассы не менять.
**ADR:** [`ADR-035-Vulkan-Dummy-Lut-Bind.md`](../ADR/ADR-035-Vulkan-Dummy-Lut-Bind.md). Решения не переписывать.

## Зачем

На Samsung S10 (Vulkan) сцена `HashProbe_30k` собирает мир (`29791` точек, 5 пассов) и рисует пустой кадр. В logcat одно предупреждение: `Attempting to draw with missing bindings`.

`M3D/ParticleBillboard` всегда объявляет `StructuredBuffer<float> _Values` и `Texture2D _LutTex`. У пробника атрибута `value` нет, ветка `else` в `Execute` ставит только `_UseLut = 0` и эти два ресурса не привязывает. Direct3D 11 в редакторе такой вызов всё равно рисует. Vulkan вызов выбрасывает.

Шейдер в сборке есть: иначе было бы `shader 'M3D/ParticleBillboard' not found`. `shader_holder_tmp` из `Test1` сюда не копировать. Он держит `FieldDebug`, не билборд.

## Сделать

Файл `Assets/Scripts/Runtime/PrimitiveParticleBinder.cs`, только путь без атрибута `value`.

В `Initialize`, после того как шейдер найден и `hasValueAttribute == false`:

- `GraphicsBuffer` на 1 элемент, `GraphicsBuffer.Target.Structured`, stride `sizeof(float)`.
- `Texture2D` 1×1, `TextureFormat.RGBA32`, без мипмапов. Имя `M3D_ParticleBillboard_DummyLut`. Не `M3D_ParticleBillboard_LUT`: тест ищет это имя по всему редактору. `hideFlags = HideAndDontSave`. Сразу после создания вызвать `Apply(false, true)`, как у настоящего LUT. Без `Apply` нативный указатель может остаться нулевым, и Vulkan снова увидит пустой слот. Цвет текселя не важен: при `_UseLut = 0` фрагментный шейдер текстуру не читает.

При `hasValueAttribute == true` эти объекты не создавать. Настоящий LUT и его имя не менять.

В `Execute`, в существующей ветке `else`, до `RenderPrimitives`:

```csharp
props.SetBuffer(ValuesId, dummyValues);
props.SetTexture(LutTexId, dummyLut);
props.SetFloat(UseLutId, 0f);
```

`_UseLut` остаётся 0. Фрагментный шейдер в эту ветку не входит, читать заглушки он не должен. `SamplerState sampler_LutTex` — статический сэмплер рядом с `_LutTex`, отдельную привязку сэмплера не добавлять.

Поля заглушки приватные. Тест читает их отражением, как уже читает `lutTexture` и `props`.

`Dispose` освобождает только буфер, который создал сам биндер (`Dispose` или `Release`, один раз), и уничтожает текстуру-заглушку тем же путём, что и настоящий LUT. Буфер `value` из `ParticleSet` не освобождать. Повторный `Dispose` не бросает.

## Не делать

- Не менять `ParticleBillboard.shader`. Keyword вырежет вариант с LUT из плеера: материал создаётся в рантайме.
- Не добавлять `HeadingToValue` в пробник и не править `HashProbe_*`, `Test1`, `shader_holder_tmp`.
- Не чинить шесть уже красных EditMode-тестов. Четыре ждут предупреждение `PositionBuffer` от `VfxParticleBinder`. Два сканируют весь редактор на чужие материал и LUT.

## Тест

Дописать `PrimitiveParticleBinderTests.InitializeExecuteDispose_DoesNotThrowAndDestroysMaterial`. Атрибута `value` там нет.

После `Execute`: `_UseLut == 0`, заглушечная текстура не null и 1×1, буфер не null и `count == 1`. После `Dispose` текстуры с именем `M3D_ParticleBillboard_DummyLut` в `Resources.FindObjectsOfTypeAll<Texture2D>()` нет.

`Execute_WithValueAttribute_EnablesLutAndKeepsTexture` не менять по смыслу: `_UseLut == 1`, жива одна и та же `M3D_ParticleBillboard_LUT`, заглушки нет.

Если эти два теста красные из-за чужих объектов в редакторе, в сдаче написать имена. Порог и поиск по всему редактору не ослаблять. Новая заглушка в этот список попасть не должна: у неё другое имя, и `Dispose` её уничтожает.

## Документы

Одна фраза в `DOC/pass-catalog.md`, в абзаце Particle Primitive render: без атрибута `value` биндер всё равно привязывает буфер из одного float и текстуру 1×1, иначе Vulkan пропускает кадр. ADR-031, ADR-034 и план не переписывать.

## Сдача

- Дифф биндера и теста.
- Результат `PrimitiveParticleBinderTests`.
- Коммит не создавать.

Замер на S10 делает владелец после этой правки: новый APK со сценой `HashProbe_30k`.
