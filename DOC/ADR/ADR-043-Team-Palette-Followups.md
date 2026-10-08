## ADR-043: Доделки палитры команд

**Статус:** на проверке. Раздел A1 ниже. «Принято» ставит владелец.
**Дата:** 2026-10-08
**Контекст:** M3D Framework, после принятых [ADR-041](ADR-041-Team-Palette.md) и [ADR-042](ADR-042-Two-Team-Preset.md). ТЗ: [`todo-adr-043-044-p6-fixes.md`](todo-adr-043-044-p6-fixes.md).

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
