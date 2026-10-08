## ADR-043: Доделки палитры команд

**Статус:** на проверке. Разделы A1 и A2 ниже. «Принято» ставит владелец.
**Дата:** 2026-10-08
**Контекст:** M3D Framework, после принятых [ADR-041](ADR-041-Team-Palette.md) и [ADR-042](ADR-042-Two-Team-Preset.md). ТЗ: [`todo-adr-043-044-p6-fixes.md`](../last/todo-adr-043-044-p6-fixes.md).

---

### A1. Хелпер выборки

Формула цвета из `frag` шейдера `M3D/ParticleBillboard` вынесена в `Assets/Shaders/GPU/Includes/ParticlePalette.hlsl`. Include без `Core.hlsl` и без URP. `ParticlePaletteColor` получает уже прочитанные `value` и `teamId`, флаги, `teamCount`, `scale`, весь `float4 color`, `Texture2D<float4>` и `SamplerState`. Внутри те же формулы: `min(teamId, teamCount-1)`, строка `(t+0.5)/max(teamCount,1)`, ветка без выборки возвращает `color` целиком, альфа `lut.a * color.a`.

`ParticleBillboard.shader` только вызывает хелпер. Буферы остаются во фрагменте. `PrimitiveParticleBinder` не менялся.

Единственное отклонение от дословного тернарника `useValue ? saturate(_Values[instanceID] * _Scale) : 0.0`: чтение буфера стоит в `if`. Заглушки `_Values` и `_TeamIds` в биндере длиной 1. Безусловный `_TeamIds[instanceID]` при выключенных командах ушёл бы за границу. По смыслу то же: буфер читается только когда его флаг включён. В A2 то же правило перейдёт в вершину.

Проверено compute-шейдером `Assets/Tests/Editor/ParticlePaletteAddressing.compute`. Свои `Texture2D<float4> _LutTex` и `SamplerState sampler_LutTex`, не inline-сэмплер. Несколько случаев за диспатч, `RenderTexture` не понадобился: EditMode compute на этой машине запускается. LUT печётся `PrimitiveParticleBinder`, `teamLut` читается рефлексией. Допуск `1/255`. Середина строки — среднее текселей 127 и 128; GPU уложился в `1/255`, допуск `2/255` не открывался.

Случаи: концы и середина строк 0 и 1; `teamId == teamCount` и `teamId == 9` берут последнюю строку; `_UseLut = 0` при командах даёт `x = 0` строки; без команд выборка `float2(d, 0.5)` и альфа `lut.a * color.a` при `color.a = 0.4` (текстура — одна строка team LUT, потому что обычный LUT 256×1 после `Apply(false, true)` не читается с CPU, а `v = 0.5` попадает в единственную строку); оба флага выключены — результат равен `_Color`; две сплошные строки, красная и синяя, на `value = 0.5` не смешиваются; три сплошных цвета; `TeamProfile.MaxTeams` (8) сплошных строк; `scale = 2` при `value = 0.75` насыщается до 1; `scale = 0.5` при `value = 1` попадает в середину; `value = -1` зажимается в 0.

EditMode до первой правки: 298/298. После: 307/307. Старые тесты не правились.

Player Settings: Windows (auto) Direct3D12 и Direct3D11, Android только Vulkan. GLES3 в списке нет, отдельная сборка под него не запускалась. `CompileShaderVariant` для D3D и Vulkan, вершина и фрагмент: 0 warning до правки и 0 после. Число не выросло.

Снимки до и после, кадр около 1 с и около 15 с, сцены `Boids_Hash` и `Boids_Hash_2Teams`. Play недетерминирован. На 15 с сравнивалась палитра, не положения: огонь остался огнём, у двух стай есть оба цвета, чёрного и крапа не добавилось. На 1 с раскладка та же: один огненный клуб и два диска, оранжевый и синий.

### A2. Цвет в вершинном шейдере

Цвет частицы считается в `vert` тем же `ParticlePaletteColor`. `Varyings` несёт `nointerpolation float4 color`, `instanceID` убран, `frag` возвращает `(half4)color`. Условное чтение `_Values` и `_TeamIds` перенесено в вершину один в один: заглушки по-прежнему длиной 1. `ParticlePalette.hlsl` и `PrimitiveParticleBinder.cs` не менялись. `#pragma target 4.5` не добавлялся: сборка прошла, новых warning нет.

Тест стадии — новый файл `PrimitiveParticleBinderTeamPaletteStageTests.cs`. Он зелёный на фрагментном шейдере A1 и остался зелёным после переноса без правок. Рисует реальный `M3D/ParticleBillboard` через биндер: `RenderParams.camera` выставлен рефлексией, затем `Execute` и `Camera.Render()` в RT 256×256, `ARGB32`, `RenderTextureReadWrite.Linear`. Камера ортографическая, size 4, без HDR, MSAA и постобработки, маска объёмов 0, очистка непрозрачным чёрным. Три квада размера 1.6: команда 0 и команда 1 при `value = 0.5`, и команда 0 при `value = 0`. Центр квада сравнивается с compute-хелпером A1 при тех же входах, допуск `2/255` (8-бит RT и `half4` на выходе). Три пикселя внутри квада, ближе к углам, совпадают с центром в допуске `1/255`: это проверка `nointerpolation`. Тест проверяет эквивалентность стадии, не скорость. Выигрыш на S10 не измерен и не заявляется; A/B — переключением коммитов.

`CompileShaderVariant`, вершина и фрагмент, до переноса и после, числа совпали: D3D вершина 0, D3D фрагмент 1, Vulkan вершина 1, Vulkan фрагмент 0. Текст единственного warning: `use of potentially uninitialized variable (ParticlePaletteColor)`. Он был до переноса вызова. GLES3 в Player Settings нет.

Снимки до правки шейдера и после: `play-a2-hash-1s-*`, `play-a2-hash-15s-*`, `play-a2-2teams-1s-*`, `play-a2-2teams-15s-*`. На ~1 с раскладка та же (один огненный клуб, два диска). На ~15 с только палитра: огонь остался огнём, у двух стай оба цвета, нет чёрного, крапа и сплошной заливки одним цветом. Положения разошлись, как и ожидалось.

EditMode после фазы: 308/308. Тесты A1 не правились.
