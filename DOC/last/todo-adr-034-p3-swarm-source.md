# ТЗ: ADR-036 P3 — `SwarmSource`

**Статус:** EditMode зелёный (2026-10-03). Стаи на экране нет.
**Исполнитель:** одна фаза. Хэш, гистограмму, команды, силу и палитру не делать.

Прочитать [`ADR-036-Swarm-Source.md`](../ADR/ADR-036-Swarm-Source.md) перед кодом. План P3 в [`plan-Reynolds-Boids-v2.md`](../plan-Reynolds-Boids-v2.md) короче этого файла. Где они расходятся, делать по ТЗ и написать об этом в сдаче.

**Не трогать:** `SpatialHash*`, `HashProbe*`, `ParticleBillboard`, `PrimitiveParticleBinder`, `HeadingSteer`, `Boids_mk1.asset`, Physarum, fluid-пассы, `Test1.unity`. Не открывать `Test1` на запись. `Boids_Rivalry/` только читать.

**Git:** не коммитить. Не делать `git add -A`, `git add .`, `git commit -a`, `stash`, `checkout` / `restore` / `reset`. Чужие незакоммиченные правки не трогать.

---

## 1. Вид источника

`Assets/Scripts/Sources/DataSourceKind.cs`: добавить `Swarm = 4` в конец. Существующие значения не переставлять.

`Assets/Scripts/Runtime/EffectAsset.cs`:

```csharp
[SerializeField] private SwarmSource swarmSource = new SwarmSource();
```

В `ResolveSource` новый case возвращает `swarmSource`. Если после десериализации поле `null`, подставить `new SwarmSource()` и вернуть его. Инициализатор поля на уже сохранённом YAML не выполняется. Куб и None идут своими case.

`Assets/Scripts/Editor/EffectAssetEditor.cs`: в `OnEnable` найти `swarmSource`. В `OnInspectorGUI` при `DataSourceKind.Swarm` показать `PropertyField(..., true)`. Остальные виды рисуются как сейчас.

## 2. Класс

Новый файл `Assets/Scripts/Sources/SwarmSource.cs`. `[Serializable]`, `IDataSource`. `Name` — `"Swarm"`. `Tick` пустой, как у `CubeSource`.

Вложенный `[Serializable]` класс `Spawn`:

| Поле | Тип | Смысл |
| --- | --- | --- |
| `teamIndex` | `int` | Пишется в `teamId` как `uint`. Отрицательный — ошибка |
| `count` | `int` | Сколько частиц в этом диске. Отрицательный — ошибка. Ноль — спавн пропускается |
| `center` | `Vector2` | XZ. `y` вектора — Z мира |
| `radius` | `float` | Радиус диска. Отрицательный — ошибка. Ноль — все точки в центре |
| `initialDirection` | `Vector2` | Курс на XZ до нормализации. `y` вектора — Z мира |

На самом источнике:

| Поле | Тип | Дефолт |
| --- | --- | --- |
| `spawns` | `List<Spawn>` | пустой список |
| `seed` | `int` | `1` |
| `jitterDegrees` | `float` | `0` |

Публичные get/set, чтобы тест наполнял источник без рефлексии. `jitterDegrees < 0` — `ArgumentOutOfRangeException` и в сеттере, и в `Setup`. Инспектор пишет поле мимо сеттера.

`null`-список считать пустым. `null`-элемент внутри списка — `ArgumentNullException` до `EnsureCapacity`.

## 3. `Setup`

Сначала обойти спавны и проверить `teamIndex`, `count`, `radius`. Потом сложить `count` в `long`. Если сумма не влезает в `int` — `ArgumentOutOfRangeException`.

`particles.EnsureCapacity((int)sum)` ровно один раз за вызов. Не вызывать его на каждый спавн.

Если сумма `0`: выйти. `RegisterAttribute` не звать. При ёмкости 0 он бросает.

Иначе зарегистрировать и залить три буфера:

- `BuiltinAttributes.RestPosition`, `Vector3`, `y = 0`;
- `BuiltinAttributes.Heading`, `Vector3`, длина 1, `y = 0`;
- `BuiltinAttributes.TeamId`, `uint`.

`position` и `velocity` не регистрировать и не писать. Мир копирует rest в position сам. Нули скорости поставит `AutoRegisterAttributes`, если пасс её читает.

Порядок частиц — порядок списка. Спавн `i` занимает полуинтервал после суммы `count` предыдущих. `teamId` частицы равен `teamIndex` её спавна. Два спавна с одним номером разрешены.

Верхнюю границу `teamIndex` не проверять. Списка команд ещё нет, это P4.

## 4. Генератор

Один `new System.Random(seed)` на `Setup`. `UnityEngine.Random` не использовать.

На каждую частицу с `count > 0`, строго в этом порядке, три вызова `NextDouble`:

1. `u` — радиус.
2. `v` — угол на диске.
3. `w` — курс.

Диск:

```
r = radius * sqrt(u)
theta = v * 2π
x = center.x + cos(theta) * r
z = center.y + sin(theta) * r
y = 0
```

`u` и `v` читать даже при `radius == 0`.

Курс:

- Если `initialDirection.sqrMagnitude < 1e-6`: угол = `w * 2π`. Jitter не прибавлять.
- Иначе: `base = atan2(direction.y, direction.x)` у уже нормализованного направления, угол = `base + (w * 2 - 1) * jitterRadians`. `jitterRadians = jitterDegrees * Deg2Rad`.

`heading = (cos(angle), 0, sin(angle))`.

При `jitterDegrees == 0` и ненулевом направлении курс совпадает с нормализованным `initialDirection`, а `w` всё равно расходуется.

## 5. Тесты

Новый `Assets/Tests/Editor/SwarmSourceTests.cs`. GPU не нужен. Свой `ParticleSet`, `Setup`, затем `GetData`. В `TearDown` вызвать `Dispose`.

| Тест | Проверка |
| --- | --- |
| Два спавна, count 3 и 5 | `Count == 8`. Индексы 0..2 — `teamId` первого, 3..7 — второго |
| Диск | У каждой точки `y == 0` и расстояние на XZ до своего центра `<= radius * (1 + 1e-4)`. Радиус больше 0 |
| Курс без разброса | `jitterDegrees == 0`, направление `(0, 1)`. `heading` равен `(0, 0, 1)` с допуском `1e-5`, `y == 0` |
| Курс | В остальных случаях длина `heading` отличается от 1 не больше чем на `1e-5`, `y == 0` |
| Тот же seed | Два новых `ParticleSet`, один и тот же источник по данным. Буферы rest, heading и teamId совпадают побитово |
| Другой seed | Хотя бы одна позиция отличается. Диск с `radius > 0` и `count >= 8` |
| Пустой список | `Count == 0`, исключения нет, `restPosition` не зарегистрирован |
| Ошибки | `count < 0`, `radius < 0`, `teamIndex < 0`, `jitterDegrees < 0` бросают `ArgumentOutOfRangeException` до записи буферов |
| Один `EnsureCapacity` | После успешного `Setup` второй `Setup` на том же `ParticleSet` с большим суммарным count бросает `InvalidOperationException` из `ParticleSet` |

Отдельный смоук мира, по образцу `SpatialHashWorldTests`: `EffectAsset` в памяти, `EditorConfigure(DataSourceKind.Swarm, ...)`, на возвращённом `SwarmSource` два спавна 4 и 6. Пасс — один `IntegratePass`. Библиотека — `DynamicsPasses.compute`. Объект без `VisualEffect`. `Rebuild` не бросает, `particles.Count == 10`. Одна позиция после сборки совпадает с `restPosition` того же индекса: скорость нулевая, кадр ещё не крутился, копирование rest → position уже прошло.

`velocity` в прямом тесте `Setup` отсутствует.

Существующие тесты не править. `Cube` остаётся `ResolveSource` кубом.

## 6. Документы

Коротко:

- `DOC/status.md` — ADR-036, EditMode, стаи на экране нет.
- `DOC/pass-catalog.md` — строка в таблице «Источники частиц»: Swarm спавнит диски и пишет `restPosition`, `heading`, `teamId`. Новый раздел пассов не заводить.
- `DOC/capabilities.md` — строка: `Swarm` спавнит диски с `teamId` и курсом. Силы соседей нет.
- `DOC/getting-started.md` — вид источника Swarm в инспекторе. Меню нет.

ADR-034 и ADR-036 не переписывать. План не расширять.

## 7. Готово, когда

- Тесты из §5 зелёные.
- Старый куб и None не сменили число в enum.
- В эффекте нет нового пасса, нового `.compute` и нового пресета.
- Коммита нет.
