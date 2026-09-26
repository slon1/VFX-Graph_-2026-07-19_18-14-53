## ADR-032: Physarum — сенсорный пасс и пресет следа

**Статус:** EditMode зелёный (2026-09-26). Play-сеть на `Physarum.asset` — оператор, ещё не закрыта.
**Дата:** 2026-09-26
**Контекст:** M3D Framework, после [ADR-031](ADR-031-Primitive-Only-And-Value-Palette.md). Исходный черновик — [`plan-physarum.md`](../plan-physarum.md). Этот документ фиксирует фазы P1 и P2: один новый пасс `PhysarumSteerPass` и пресет `Physarum.asset`. P3 (подбор паутины / вихрей / ячеек в Play) и P4 (гибрид с Fluid2D) не открываются.
**Не меняет:** ядра decay / diffuse / density P2G / integrate / bounds; `Boids_mk1`; fluid-пресеты; `ParticleBillboard`; spatial hash.
**ТЗ:** [`todo-adr-032-physarum.md`](../last/todo-adr-032-physarum.md)

---

### Контекст

Модель Джеффа Джонса (2010): агент на плоскости читает одно скалярное поле в трёх точках, поворачивает курс на фиксированный угол и оставляет след. Поле затухает и диффундирует. Сеть возникает из следа, не из рисунка самих агентов.

В M3D уже есть все куски поля и шага:

- осаждение — `ScatterDensityToFieldPass` + `NormalizeDensityAccumPass` (sum, прибавляет в текстуру);
- затухание — `DecayFieldScalarPass`;
- диффузия — `DiffuseFieldPass`;
- шаг — `IntegratePass`;
- тор — `BoxBoundsPass` в режиме `Wrap`;
- рисунок поля — `FieldDebugQuadsBinder`; частицы — `PrimitiveParticleBinder`.

Нет только моторного правила «три сенсора → угол курса». `HeadingSteerPass` для этого не подходит: он nlerp к сумме сил и умножает поворот на `dt`.

### Решение

#### 1. Один новый кернел, отдельный файл

`Assets/Shaders/GPU/Passes/PhysarumPasses.compute`, кернел `PhysarumSteer`. Не в `DynamicsPasses.compute`: тот файл не сэмплирует поля, а этот обязан включить `FieldSampling.hlsl` и объявить `Texture2D<float> FieldRead`.

Класс `PhysarumSteerPass : ParticleKernelPass` в `Assets/Scripts/Passes/DynamicsPasses.cs` (рядом с `HeadingSteerPass`). Категория `Dynamics`. Имя в инспекторе `Physarum Steer`.

Чтения частиц: `position`, `heading`. Записи: `heading`, `velocity`. Позицию кернел не пишет. Поле: один `FieldReads` на `trail`, `FieldAccess.Read`, `FieldSemantic.Scalar`, 1 канал. Образец биндинга — `SampleVelocityFieldPass.SetParams` (`FieldRead` + `FieldShaderParams.Push`). Набор атрибутов — новый статический `AttrSets.PositionHeading`, не `new[]` в геттере.

#### 2. Правило Джонса, углы за шаг

Обозначения: `cF`, `cL`, `cR` — концентрации впереди, слева, справа. `turnAngle` и `sensorAngle` на пассе хранятся в градусах. В шейдер уходят радианы. `randomWiggle` тоже в градусах (шум ±wiggle вокруг выбранного угла).

```
если cF > cL и cF > cR → поворот 0
иначе если cF < cL и cF < cR → поворот ±turnAngle, знак от хеша id и шага
иначе если cL > cR → поворот +turnAngle   // влево, против часовой с +Y
иначе если cR > cL → поворот −turnAngle
иначе → 0
затем прибавить (hash01 * 2 − 1) * wiggle
```

Ветка «оба боковых выше переднего» обязана быть случайным знаком. Черновик плана в этом случае уходил в `cL > cR` и всегда выбирал больший боковой. Это другое правило: агент охотнее влипает в край следа.

`turnAngle` не умножается на `dt`. Один `Execute` — один поворот, как одна итерация у Джонса. `moveSpeed` записывается в скорость (`velocity = heading * moveSpeed`), а сдвиг делает уже `Integrate` (`position += velocity * dt`). Поэтому `moveSpeed` — мировые единицы в секунду, а угол — градусы за кадр. `SimulationSpeed` удлиняет шаг и не увеличивает угол за кадр. Это не баг и не повод «для симметрии» умножить угол на `dt`.

Поворот в плоскости XZ, вид сверху (+Y):

```
x' = x cos θ − z sin θ
z' = x sin θ + z cos θ
```

`+θ` переводит курс +Z в −X (влево). `heading.y = 0` каждый шаг. Если `dot(heading, heading) < 1e-4`, до сенсоров курс заменяется единичным вектором в XZ из хеша `id` (атрибут после `Build` нулевой: `RegisterZeroed`).

Сенсоры на расстоянии `sensorDistance` (мировые единицы). Передний — по курсу. Левый и правый — поворот курса на `±sensorAngle`, не голый перпендикуляр. Перпендикуляр `(-h.z, 0, h.x)` — только базис: направление сенсора `normalize(h) * cos(α) + left * sin(α)`. При `α = 90°` это совпадает с перпендикуляром, при `22.5°` смотрит вперёд-влево. UV — `WorldToFieldUV`. Y позиции на UV не влияет: плоскость поля XZ.

Сэмплер — `sampler_linear_repeat`. UV не прогонять через `saturate`. `sampler_linear_clamp`, как у `SampleVelocityField`, при `BoxBounds` Wrap повторяет крайний тексель и собирает агентов на рамке. Тор поля и тор агентов должны совпасть.

Шум и случайный знак берут соль шага. Счётчик шага — поле пасса `[NonSerialized]`: сериализовать его нельзя, иначе значение уедет в `.asset`. При `randomWiggle = 0` соль на угол не влияет.

#### 3. Кадр пресета — след копится, не затирается

`NormalizeDensityAccum` пишет `FieldWrite += decoded` (`DensityPasses.compute`). Комментарий класса про `ClearField` относится к режиму Replace. Для слизевика это accumulate-onto-decaying:

```
ClearFieldAccum (trail, channels = 1)
ScatterDensityToField (trail)
NormalizeDensityAccum (trail)
DecayFieldScalar (trail)
DiffuseField (trail)
DiffuseField (trail)
PhysarumSteer (trail)
Integrate
BoxBounds (Wrap)
HeadingToValue
```

`ClearField(trail)` в этом списке запрещён: он обнуляет текстуру каждый кадр, и диффузия с затуханием не из чего строить сеть. `CopyRest` тоже запрещён: он каждый кадр возвращает агентов в решётку. Начальная позиция и так копируется один раз в `SimulationWorld.InitializePositionFromRest`.

Два `DiffuseField` — две записи в списке, не `RepeatCount`. Так обе ручки видны в инспекторе. Rate остаётся дефолтным `0.15` (CFL при `SimulationSpeed = 1` далеко от `0.25`).

`ClearFieldAccum.channels` по дефолту равен 2 (скорость). Для скаляра обязательно `1`, иначе раскладка accum не совпадёт со `ScatterDensity`.

#### 4. Числа пресета `Assets/Effects/Physarum.asset`

Это стартовая точка, не обещание паутины. P3 их крутит.

| Параметр | Значение | Почему |
| --- | --- | --- |
| `CubeSource.resolution` | 32 | `resolution³` = 32768. Отдельного «200×200×1» у куба нет |
| `cubeSize` | 32 | след и куб покрывают один квадрат `[-16, 16]` по XZ |
| `simulationSpeed` | 1 | угол за кадр не разгоняется вместе с шагом |
| поле `trail` | Scalar, `R16_SFloat`, 128², size `(32, 32)`, origin 0, axisU X, axisV Z, clear 0 | плоскость по умолчанию у `FieldDescriptor` уже XZ |
| scatter / normalize | scale 4096, bias 0 | дефолт density; один агент декодируется как `1` |
| decay | 0.8 | внутри вилки плана `0.5…2` |
| diffuse ×2 | 0.15 и 0.15 | дефолт пасса |
| sensor angle / distance | 22.5°, 1 | ~4 текселя при шаге сетки `32/128 = 0.25` |
| turn angle | 45° | шаг Джонса, не скорость |
| wiggle | 10° | шум поверх выбранного угла |
| moveSpeed | 15 | ~`15 * dt` ≈ один тексель за кадр при 60 FPS |
| `BoxBounds` | center 0, extents `(16, 0, 16)`, Wrap | Y не заворачивается (`size.y = 0` в кернеле Wrap) |
| `particleSize` | 0.2 | `0.05` на весь кадр уходит ниже пикселя и мигает (ADR-031, замер 2026-09-25) |
| частицы | `HeadingToValue`, fire-градиент, value scale 1 | цвет по курсу; картинка сети — квад следа |
| квад `trail` | ScalarHeatmap, fire LUT, `colorScale` 0.03, `hdrIntensity` 1 | старт решётки кладёт в тексель до 32 агентов (`1 * 32 * 0.03 ≈ 1`) |

Куб заполняет Y тем же шагом. Поле Y игнорирует, `velocity.y` остаётся 0, поэтому столбик из 32 агентов стартует в одной клетке XZ и расходится только по курсам. Отдельный источник «плоскость» не пишем.

Переполнение uint accum при scale 4096 наступает около миллиона агентов в одном текселе. На 32768 это не гейт и не повод добавлять защиту. Если в Play появится Inf, крутится scale, не код.

Меню `Tools/M3D/Create Physarum Effect` создаёт ассет, вешает его на `SimulationWorld` открытой сцены и переписывает Pass Library из `M3DDemoTools.PassLibraryPaths`. В этот массив входит `PhysarumPasses.compute`. Без шейдера в библиотеке `FindKernel` бросает, `Build` ловит исключение и выключает мир.

#### 5. DoD

| Что | Как |
| --- | --- |
| Контракт | `PhysarumSteerPass`: Dynamics, кернел `PhysarumSteer`, reads position+heading, writes heading+velocity, один FieldRead `trail` Scalar/1. Угол не читает `dt` |
| GPU, влево | Один агент, курс +Z, пятно только на левом сенсоре, wiggle 0 → `heading.x < -0.9` |
| GPU, вправо | Зеркало → `heading.x > 0.9` |
| GPU, прямо | Пятно только впереди → курс остаётся +Z |
| GPU, оба боковых | Оба боковых выше переднего, передний пустой → курс ушёл на ±90°, знак не фиксируем |
| GPU, нулевой курс | `heading == 0` → после шага длина ≈ 1, `y == 0` |
| Пресет | `PhysarumPresetTests`: порядок пассов, нет `ClearField` и `CopyRest`, куб 32 / size 32, поле 128² size 32, bounds Wrap `(16,0,16)`, channels accum = 1 |
| Смоук | `Rebuild()` на `Physarum.asset` без `VisualEffect` не бросает и не ставит `enabled = false` |
| Доки | `pass-catalog`, `status`, `capabilities`, `getting-started` — пресет есть, Play-сеть не закрыта |

Геометрия GPU-тестов: поле 8², size 8, агент в `(0.5, 0, 0.5)` — центр текселя `(4, 4)`, курс `(0,0,1)`, `sensorAngle = 90°`, `sensorDistance = 1` (ровно один тексель), `turnAngle = 90°`, `randomWiggle = 0`. Левый сенсор — мир `(-0.5, 0, 0.5)`, тексель `(3, 4)`, индекс 35. Правый — `(1.5, 0, 0.5)`, тексель `(5, 4)`, индекс 37. Передний — `(0.5, 0, 1.5)`, тексель `(4, 5)`, индекс 44. Индекс `y * 8 + x`. Координата `uv * 8` у этих точек равна `n + 0.5`. Агент в нуле и дистанция `1.5` сажают сенсоры на рёбра (`2.5` / `4.0`) — так не делать. Заливка через `FieldTestHarness.SeedScalar`. Харнес частиц не расширять: у него `ParticleSet == null`. Тест собирает свой `SimContext` на том же `FieldSet` и сам ставит `Cmd`. Угол 90° не отличает поворот на `sensorAngle` от голого перпендикуляра; на пресете 22.5° обязана работать формула с `cos`/`sin`.

#### 6. Что не входит

- P3: три именованных режима (паутина / вихри / ячейки) и перепись дефолтов после Play.
- P4: `AdvectScalar` следа полем скорости, `TouchInject`, `Physarum_Fluid.asset`.
- Новый `DataSource`, сплющивание Y, spatial hash, правка `ParticleBillboard` (Opaque / AlphaTest).
- Смена `Boids_mk1`, Fluid2D и ядер density/decay/diffuse.

### Отклонённые варианты

**Поворот к большему боковому, когда оба боковых выше переднего.** Так написан черновик плана. Джонс в этой точке берёт случайный знак, иначе толстый след не отпускает курс.

**Умножить `turnAngle` на `dt`.** Смешивает шаг Джонса с `HeadingSteer`. При `SimulationSpeed > 1` агент начинал бы крутиться быстрее и шагать дальше одновременно, и ручки перестают быть независимыми.

**`sampler_linear_clamp`.** Уже стоит на G2P-пассах. Для тора со Wrap это стенка.

**`ClearField(trail)` раз в кадр.** Сеть не накапливается. Комментарий `NormalizeDensityAccumPass` описывает Replace и к этому пресету не относится.

**Отдельный источник «плоскость 200×200».** `CubeSource` — куб. 32³ уже даёт порядок десятков тысяч агентов. Новый источник не нужен, чтобы увидеть след.

### Последствия

- (+) Новый алгоритм — один пасс и один пресет. Поле, P2G, decay, diffuse, integrate, bounds, present не форкаются.
- (+) Картинка сети — квад `trail`. Агенты вторичны.
- (−) Стартовые числа не проверены в Play. Квадратный стартовый след и столбик по Y — известный IC, не дефект пасса.
- (−) Угол за кадр и шаг в секунду живут в разных единицах. Это надо помнить при калибровке P3.
