# ТЗ: закрыть шесть старых красных EditMode

**Статус:** закрыт, EditMode 274/274 (2026-10-03).
**Исполнитель:** одна приёмка. Это тикет только про тесты. Код проекта не менять.
**Нужен до:** 5c ([`todo-adr-034-p5c-boids-hash-preset.md`](todo-adr-034-p5c-boids-hash-preset.md)): гейт «весь EditMode зелёный» ни на чём не держится, пока шесть тестов красные.

**Перед стартом:** Unity должен быть в Edit Mode. Если `editor_status` показывает `playing`, `run_tests` отвечает 400 и тесты не запускаются; остановить Play или попросить владельца.

**Не трогать:** всё, кроме тестов. В частности `SimulationWorld`, `PrimitiveParticleBinder`, шейдеры, пассы, ассеты, сцены. Проверки `Dispose` в тестах биндера не ослаблять. Чужие незакоммиченные правки (`Boids_mk1.asset`, `Physarum_Fluid.asset`, `Test1.unity`, `Mobile_RPAsset.asset`, `UniversalRenderPipelineGlobalSettings.asset`) не трогать.

**Git:** не коммитить. Не делать `git add -A`, `git add .`, `git commit -a`, `stash`, `checkout` / `restore` / `reset`.

---

## 1. Что красное и почему

Шесть тестов красные с ADR-031 (коммит `d58f237`), их не ломали P1–P5.

| Тест | Сообщение | Причина |
| --- | --- | --- |
| `SimulationWorldDisabledPassInitializeTests`: `Build_AllP2GPassesDisabled_DoesNotFail`, `Build_AllP2GPassesEnabled_Succeeds`, `Build_NormalizeDisabled_ClearAndScatterEnabled_Succeeds`, `Build_OnlyClearEnabled_Succeeds` | `Expected log did not appear: [Warning] Regex: PositionBuffer` | Тест ждёт предупреждение `PositionBuffer`. Его писал `VfxParticleBinder`, который `SetupBinders` с ADR-031 больше не создаёт. Предупреждения нет, а ожидание осталось |
| `PrimitiveParticleBinderTests`: `InitializeExecuteDispose_DoesNotThrowAndDestroysMaterial` | `Dispose left M3D_ParticleBillboard material alive` | Тест ищет по всему редактору материал с именем `M3D_ParticleBillboard` и требует, чтобы такого не осталось. Находит чужие |
| `PrimitiveParticleBinderTests`: `Execute_WithValueAttribute_EnablesLutAndKeepsTexture` | `Dispose left M3D_ParticleBillboard_LUT alive` | То же для текстуры `M3D_ParticleBillboard_LUT` |

**Гипотеза чужих объектов.** Тесты мира создают `SimulationWorld` на неактивном объекте и зовут `Rebuild()`. Мир собирает биндер: материал и LUT. Потом тест делает `DestroyImmediate(host)`. Unity вызывает `OnDestroy` только у объектов, которые хоть раз были активны, а неактивный хост не был. `Teardown` не выполняется, материал, текстура и `GraphicsBuffer` мира остаются в памяти. Такие тесты есть в 15 файлах. Тесты биндера идут после части этих фикстур (Boid, Fluid, Physarum) и до других (SimulationWorld, SpatialHash, Swarm, Team), поэтому первый полный прогон может покраснеть от фикстур до биндера, а второй от остатков тех, что идут после. Отсюда одиночный прогон и два полных подряд.

Это гипотеза. Первый шаг её проверяет.

## 2. Шаг 0 — проверить гипотезу

Порядок обязателен, потому что одиночный прогон имеет смысл только на чистом домене:

1. Принудительно перезагрузить домен без правок скриптов (например `EditorUtility.RequestScriptReload()` через `eval`; обычный `recompile` без изменений домен может не перезагрузить). Дождаться `editor_status` без `compiling` и `domainReloadInProgress`.
2. Посчитать через `eval` базу: живые (`!= null`) объекты с именами `M3D_ParticleBillboard` (Material), `M3D_ParticleBillboard_LUT` и `M3D_ParticleBillboard_DummyLut` (Texture2D). Записать числа.
3. Запустить `PrimitiveParticleBinderTests` отдельно. Если оба зелёные, чужие объекты подтверждены. Если красные и на чистом домене — гипотеза неверна: **части 2 и 3 не делать, остановиться и написать в сдаче** (причина тогда в самом `Dispose`, вопрос к коду). Часть 1 делается в любом случае: её причина уже в таблице §1.
4. Прогнать весь EditMode, посчитать объекты снова. Если число выросло, утечка подтверждена.

## 3. Часть 1. Четыре теста мира

Файл `Assets/Tests/Editor/SimulationWorldDisabledPassInitializeTests.cs`.

- В `AssertBuildSucceeds` убрать `LogAssert.Expect(LogType.Warning, new Regex("PositionBuffer"))`.
- Убрать ставшие ненужными `using System.Text.RegularExpressions;` и `using UnityEngine.TestTools;`, если ими больше ничего не пользуются.
- Остальное в тесте оставить: `Assert.DoesNotThrow(() => world.Rebuild())` и `Assert.IsTrue(world.enabled, …)`. Смысл теста — выключенные пассы не ломают сборку, и он не меняется.
- Не добавлять замену ожиданию (новое предупреждение, новый лог). Теста на то, чего не должно быть, не нужно.

## 4. Часть 2. Два теста биндера

Файл `Assets/Tests/Editor/PrimitiveParticleBinderTests.cs`.

Проверка «такого объекта нет во всём редакторе» заменяется на «число таких объектов после `Dispose` равно числу до создания биндера». Так тест видит только то, что создал сам, и остаётся строгим: если `Dispose` забыл уничтожить объект, число вырастет.

- Добавить в файл небольшой приватный помощник: сколько живых объектов типа `T` с заданным именем (`Resources.FindObjectsOfTypeAll<T>()`, `!= null`, `name ==`).
- В `InitializeExecuteDispose_DoesNotThrowAndDestroysMaterial`: взять число материалов `M3D_ParticleBillboard` и текстур `M3D_ParticleBillboard_DummyLut` **до** `new PrimitiveParticleBinder`. После `Dispose` оба числа равны взятым. Прежний цикл с `Assert.Fail` убрать.
- В `Execute_WithValueAttribute_EnablesLutAndKeepsTexture`: то же для текстур `M3D_ParticleBillboard_LUT` и для материалов `M3D_ParticleBillboard` (на этом пути материал тоже создаётся). `M3D_ParticleBillboard_DummyLut` здесь не считать: на пути с `value` её нет.
- В сообщения проверок включить «было» и «стало».
- Все остальные проверки тестов не менять (`_UseLut`, размеры `dummyLut` и `dummyValues`, `AreSame` и так далее).

## 5. Часть 3. Утечка в тестах мира

Корень — гипотеза из §1. Лечится в тестах, не в мире.

**Помощник.** Новый `Assets/Tests/Editor/SimulationWorldTestCleanup.cs`, статический класс:

```csharp
public static void DestroyHost(GameObject host)
```

Для каждого `SimulationWorld` на хосте вызывает закрытый `Teardown` через рефлексию (`BindingFlags.Instance | BindingFlags.NonPublic`), затем `Object.DestroyImmediate(host)`. Если метод не найден, тест падает с понятным сообщением (метод переименовали, помощник нужно обновить), а не молчит. `host == null` — ничего не делает. `GetMethod` с пустым списком параметров. Уничтожение хоста стоит в `finally`: если `Teardown` бросил, хост всё равно уничтожается, исключение идёт наружу. Повторный `Teardown` безопасен: в рабочем коде мира он уже вызывается дважды (`OnDisable` и `OnDestroy`), поэтому идемпотентность есть.

**Применение.** В каждом `TearDown`, где разрушается хост с `SimulationWorld`, заменить `Object.DestroyImmediate(host)` (или эквивалент) на `SimulationWorldTestCleanup.DestroyHost(host)`. Больше в этих файлах ничего не менять. Файлы (15), найденные по `Rebuild()`:

- `Fluid2DMacCormackDyeWorldSmokeTests.cs`
- `SimulationWorldWithoutVisualEffectTests.cs`
- `PhysarumFluidWorldSmokeTests.cs`
- `PhysarumWorldSmokeTests.cs`
- `Fluid2DHighResDyeWorldSmokeTests.cs`
- `SimulationWorldDisabledPassInitializeTests.cs`
- `BoidNeighborForceTests.cs`
- `Fluid2DWorldSmokeTests.cs`
- `Fluid2DVorticityWorldSmokeTests.cs`
- `TeamProfileTests.cs`
- `Fluid2DHarrisOrderWorldSmokeTests.cs`
- `BoidsMk1PrimitiveWorldSmokeTests.cs`
- `SpatialHashWorldTests.cs`
- `TeamHeadingSteerTests.cs`
- `SwarmSourceTests.cs`

Если в каком-то файле хост создаётся иначе и разрушается не в `TearDown` (внутри теста, в `try/finally`), заменить там же. Если у файла вообще нет разрушения хоста, написать об этом в сдаче, не добавлять.

Меняется только разрушение хоста с миром. Уничтожение `effect` и прочего (`DestroyImmediate(effect)`, списки уничтожения GPU-фикстур `BoidNeighborForceTests`, `TeamHeadingSteerTests`, `SwarmSourceTests`) не трогать. Тест, который хост создаёт, но `Rebuild()` не зовёт (например `RenderParticles_DefaultTrue` в `SpatialHashWorldTests`), тоже переводится на `DestroyHost`: там это безопасно.

Тесты, которые никогда не звали `Rebuild()` или `Teardown` и не создают хост (чистые GPU-тесты со своим `SimContext`), не трогать.

## 6. Проверка

1. `PrimitiveParticleBinderTests` зелёные и в одиночном прогоне, и в общем.
2. `SimulationWorldDisabledPassInitializeTests` зелёные.
3. Весь EditMode: **ни одного красного**. Ожидаемо 274 теста и 274 зелёных на момент ТЗ (число может измениться, если 5c уже добавила тесты).
4. Весь EditMode запустить **дважды подряд** в одной сессии. Оба прогона зелёные: так видно зависимость от порядка и от остатков прошлого прогона.
5. База утечки из шага 0: после нового полного прогона число объектов `M3D_ParticleBillboard`, `M3D_ParticleBillboard_LUT` и `M3D_ParticleBillboard_DummyLut` равно числу до него (после перезагрузки домена). Если выросло, назвать файл, где осталась утечка.

## 7. Документы

Только `DOC/status.md`: одна новая строка про закрытый тикет (какие шесть тестов, причина, что код не менялся). Строки «6 красных» в `status.md` нет; формулировки в плане и в ТЗ 5c этот тикет не переписывает, их поправит ревьюер после приёмки. ADR и план не переписывать.

## 8. Готово, когда

- Шаг 0 выполнен, результат записан в сдаче.
- Шесть тестов зелёные, весь EditMode зелёный дважды подряд.
- Изменены только файлы в `Assets/Tests/Editor/` и `DOC/status.md`.
- Коммита нет.
