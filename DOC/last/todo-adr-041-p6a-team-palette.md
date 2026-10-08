# ТЗ: ADR-041 P6a — палитра по командам в билборде

**Статус:** закрыт (2026-10-08). EditMode 295/295 дважды. Вид огня совпал. Замер S10 не входил.
**Исполнитель:** одна приёмка. Начинать после коммита 5c и моего «принято» по 5c. Если 5c не закоммичена, остановиться и написать об этом.

Прочитать [`ADR-041`](../ADR/ADR-041-Team-Palette.md) перед кодом. План §P6a короче этого файла; где они расходятся, делать по ТЗ и написать об этом в сдаче.

**Менять:**

- `Assets/Scripts/Runtime/PrimitiveParticleBinder.cs`
- `Assets/Shaders/GPU/ParticleBillboard.shader`
- `Assets/Scripts/Runtime/SimulationWorld.cs` — только вызов `new PrimitiveParticleBinder(...)` в `SetupBinders`: добавить пятым аргументом `effect.Teams`. Больше ничего в мире не менять.
- `Assets/Scripts/Editor/M3DDemoTools.cs` — только `CreateBoidsHashAsset`: цвет единственной команды.
- `Assets/Settings`-ассеты не трогать. `Assets/Effects/Boids_hash.asset` пересоздаётся меню (п. 6).
- Новый файл `Assets/Tests/Editor/PrimitiveParticleBinderTeamPaletteTests.cs`.
- Документы: `DOC/status.md`, `DOC/capabilities.md`, `DOC/getting-started.md`.

**Не трогать:** кернелы и классы хэша, силы, steer, `TeamParams` (раскладку и `Upload`), `SwarmSource`, `FieldDebugQuadsBinder`, `FPSDisplay.cs`, `VfxParticleBinder`, `HashProbe*`, `Test1.unity`, Physarum, fluid, `EditorBuildSettings`, `SimulationWorldTestCleanup`. Существующие тесты `PrimitiveParticleBinder*Tests` не править. `BuildLutPixels` не менять и не переименовывать. План не править.

**Git:** не коммитить. Не делать `git add -A`, `git add .`, `git commit -a`, `stash`, `checkout` / `restore` / `reset`. Чужие незакоммиченные правки не трогать.

---

## 1. Конструктор

В `PrimitiveParticleBinder`:

```csharp
public PrimitiveParticleBinder(float size, Color color, Gradient gradient, float valueScale)
    : this(size, color, gradient, valueScale, null) { }

public PrimitiveParticleBinder(float size, Color color, Gradient gradient, float valueScale,
    IReadOnlyList<TeamProfile> teams) { ... }
```

Команды копируются в массив градиентов внутри `Initialize` (не держать ссылку на изменяемый список дольше `Initialize`).

## 2. Initialize

Режим команд (`UsesTeamPalette`, `internal` для тестов) включается, если одновременно: `teams` не `null` и не пуст, и `context.Particles.TryGet(BuiltinAttributes.TeamId, out teamBuffer)`. Иначе все ветки как сейчас.

В режиме команд:

- `teamCount = min(teams.Count, TeamProfile.MaxTeams)`.
- Текстура `256 × teamCount`, `RGBA32`, linear, `Clamp`, `Bilinear`, имя `M3D_ParticleBillboard_TeamLUT`, `HideAndDontSave`. Строка `i` = `BuildLutPixels(градиент команды i, 256)` без дублирования кода. Градиент `null` → градиент эффекта (аргумент `gradient`), если и он `null` → `DebugFieldQuadSlot.DefaultFireGradient()`.
- `lutTexture` старого формата (`256 × 1`, `M3D_ParticleBillboard_LUT`) в этом режиме не создаётся, чтобы не было двух LUT.
- `hasValueAttribute` определяется как сейчас (`TryGet` по `Value`). Если значения нет, в режиме команд создаётся заглушка `dummyValues` (один `float`), `dummyLut` (1×1) не создаётся.
- Если режим команд выключен, создаётся заглушка `dummyTeamIds` (`GraphicsBuffer.Target.Structured`, 1 элемент, `sizeof(uint)`).

## 3. Execute

Кроме уже существующих биндов:

- в режиме команд: `_TeamIds` = `teamBuffer`, `_UseTeams = 1`, `_TeamCount = teamCount`, `_LutTex` = `TeamLUT`; `_UseLut = 1`, если есть `value`, иначе `0`;
- иначе: `_TeamIds` = `dummyTeamIds`, `_UseTeams = 0`, `_TeamCount = 1`.

Имена свойств кэшировать через `Shader.PropertyToID`, как остальные.

## 4. Dispose

Уничтожить `TeamLUT` и `dummyTeamIds` (`Release`), не оставляя объектов. Повторный `Dispose` не бросает исключение. Использовать существующий `DestroyObject` (Play: `Destroy`, Edit: `DestroyImmediate`).

## 5. Шейдер

В `ParticleBillboard.shader` добавить `StructuredBuffer<uint> _TeamIds;`, `float _UseTeams;`, `float _TeamCount;`. Фрагмент:

```hlsl
bool useValue = _UseLut > 0.5;
bool useTeam = _UseTeams > 0.5;
if (useValue || useTeam)
{
    float d = useValue ? saturate(_Values[input.instanceID] * _Scale) : 0.0;
    float row = 0.5;
    if (useTeam)
    {
        uint t = min(_TeamIds[input.instanceID], (uint)max(_TeamCount - 1.0, 0.0));
        row = (t + 0.5) / max(_TeamCount, 1.0);
    }

    float4 lut = _LutTex.SampleLevel(sampler_LutTex, float2(d, row), 0);
    return half4(lut.rgb, lut.a * _Color.a);
}

return half4(_Color.rgb, _Color.a);
```

Поведение без команд бит-в-бит как раньше (строка 0.5 и `_UseTeams = 0`). Вершинный шейдер не менять. Предупреждения компилятора шейдера не вводить.

## 6. Создатель `Boids_hash`

В `M3DDemoTools.CreateBoidsHashAsset` в `TeamProfile` добавить `Color = DebugFieldQuadSlot.DefaultFireGradient()` (тот же градиент, что у `particleGradient`). Больше ничего в создателе не менять (числа 3000, 48, поле и пассы остаются). После правки запустить меню `Tools/M3D/Create Boids Hash Effect`: `git diff Assets/Effects/Boids_hash.asset` показывает только цвет команды и то, что неизбежно перезаписывается.

## 7. Тесты

Новый файл `PrimitiveParticleBinderTeamPaletteTests.cs`. Шаблон: `PrimitiveParticleBinderTests` (свой `ParticleSet`, `SimContext`, подсчёт `CountAlive`). Все тесты, требующие шейдер, начинаются с `Assume.That(Shader.Find("M3D/ParticleBillboard") != null, ...)`.

1. **Строки LUT.** Две команды с разными градиентами (красный и синий концы). После `Initialize`: текстура `256×2`; пиксели `(0, row)` и `(255, row)` совпадают с `Evaluate(0)` и `Evaluate(1)` соответствующего градиента с допуском `1/255`; строки различаются.
2. **Лимит команд.** Девять команд → высота `8`.
3. **`null`-градиент команды.** Строка совпадает с градиентом эффекта; если и он `null`, с `DefaultFireGradient`.
4. **Конструкторы.** Четырёхаргументный даёт `UsesTeamPalette == false`, а с `teams == null` и пустым списком тоже `false`.
5. **Нет `teamId`.** Команды переданы, атрибут не зарегистрирован → `UsesTeamPalette == false`, LUT старого формата, как раньше.
6. **Нет `value`, но есть команды и `teamId`.** `UsesTeamPalette == true`; `_UseLut == 0` и `_UseTeams == 1` после `Execute`; `_LutTex` — `TeamLUT`.
7. **Binds.** С командами и `value`: после `Execute` `_UseLut == 1`, `_UseTeams == 1`, `_TeamCount` равно числу команд.
8. **Утечки.** До и после `Initialize` + `Dispose` число живых `M3D_ParticleBillboard_TeamLUT`, `M3D_ParticleBillboard` (материал) и `M3D_ParticleBillboard_DummyLut` не меняется (сравнение «было/стало», как в `PrimitiveParticleBinderTests`). Повторный `Dispose` не бросает.
9. **Мир.** Один тест на `SimulationWorld` с эффектом из двух команд и `SwarmSource` (малое число частиц): после `Rebuild` в `binders` есть `PrimitiveParticleBinder` с `UsesTeamPalette == true`. Хост разбирается через `SimulationWorldTestCleanup.DestroyHost` в `TearDown`. Доступ к списку binders допустим через рефлексию в тесте.

Рендеринг в пиксели в EditMode не проверять; его проверяет гейт ниже.

Существующие `PrimitiveParticleBinderTests` и `PrimitiveParticleBinderLutTests` зелёные без единой правки.

## 8. Гейт

1. Весь EditMode зелёный (база 281/281 после 5c плюс новые тесты), дважды подряд.
2. Счётчики объектов (`M3D_ParticleBillboard`, `_LUT`, `_DummyLut`, `_TeamLUT`, `M3D_FieldDebug_LUT`) после полного прогона не выросли.
3. **Визуальная проверка через MCP.** Scene `Boids_Hash.unity`: `editor_play`, снимок на 15-й секунде до правки (сделать до начала работы, сохранить в `DOC/last/play-6a-before.png`) и после (`play-6a-after.png`). Цвет стаи (огонь по курсу) должен совпадать по виду. Владелец подтверждает.
4. Консоль без ошибок и предупреждений шейдера.

Что в гейт не входит: пресет двух команд (P6b), замеры S10 (§5 ADR-041, делает владелец после «принято»).

## 9. Документы

Коротко, ADR и план не переписывать:

- `status.md` — итерация 6a: палитра по командам в `PrimitiveParticleBinder`, пресет `Boids_hash` красится через команду.
- `capabilities.md` — одна строка про цвет по команде; строку про `TeamProfile.Color` (хранится, не читается) привести к фактам.
- `getting-started.md` — одно предложение: цвет команды задаётся `TeamProfile.Color`.

## Готово, когда

- Все тесты п. 7 написаны и зелёные, существующие без правок.
- Весь EditMode зелёный дважды подряд, счётчики объектов не выросли.
- Снимки до и после лежат в `DOC/last/`, вид совпадает.
- В `git diff` изменены только файлы из списка «Менять» и пересозданный `Boids_hash.asset`.
- Коммита нет.
- В сдаче перечислено: расхождения с планом, если были; число новых тестов; результаты двух полных прогонов; строка, на которой правится `SimulationWorld`.
