## ADR-033: Перенос следа Physarum полем скорости

**Статус:** EditMode зелёный (2026-09-27). Play «палец гнёт сеть» не закрыт.
**Дата:** 2026-09-26
**Контекст:** M3D Framework, после [ADR-032](ADR-032-Physarum-Steer.md). Черновик гибрида в [`plan-physarum.md`](../plan-physarum.md) §5 сюда не переносится: там полный Stam. Этот документ — только снос поля `trail` уже существующим `AdvectScalarPass`.
**Не меняет:** `Physarum.asset` и числа `PhysarumSteer`. Fluid2D и все `Fluid2D_*`. Кернел `AdvectVelocityField`. Проекцию (Divergence / Jacobi / Subtract / SolidWall). Самих агентов скорость не двигает.
**ТЗ:** [`todo-adr-033-physarum-trail-advect.md`](../last/todo-adr-033-physarum-trail-advect.md)

---

### Контекст

`AdvectScalarPass` (ADR-023) уже делает полулагранжев перенос скаляра скоростью:

```
backUv = saturate(uv − velocity · dt / Size)
scalar_next = sample(scalar, backUv) · exp(−dissipationRate · dt)
```

Имена полей — параметры. Отдельный «PhysarumFluidAdvect» дублировал бы этот кернел.

След слизевика живёт на торе: агенты заворачивает `BoxBounds` Wrap. Текущий кернел зажимает `backUv` и сэмплирует `sampler_linear_clamp`. Краска налипает на рамку, агент выходит с другой стороны без своей сети. Fluid2D на clamp опирается (стены у скорости, краска не обязана заворачиваться). Значит wrap — opt-in, не новая политика всех пресетов.

Давление в этот тикет не входит. Палец пишет скорость через `TouchInjectVelocity`, скорость гаснет через `DecayField`. Это толчок, не вихрь. Вихрь потребовал бы проекцию.

### Решение

#### 1. Флаг `wrapUv` на `AdvectScalarPass`

Новое сериализованное поле, дефолт `false`. В шейдер уходит `int AdvectWrap` (`0` / `1`).

Ветка `false` — байт в байт нынешний кернел: `saturate` и `sampler_linear_clamp` и для скорости, и для скаляра.

Ветка `true`:

- обе выборки через `sampler_linear_repeat`;
- `saturate` нет;
- `frac` перед `SampleLevel` нет: он обрезает билинейный след на шве, и тексель с другой стороны в фильтр не попадает.

`dissipationRate` и `reverse` не меняются. При `reverse` знак `dt` по-прежнему переворачивается на CPU; wrap, если включён, действует и там. На Fluid2D флаг не включать.

`AdvectVelocityField` не трогать.

#### 2. Затухание не в адвекции

У `AdvectScalar` на пресете `dissipationRate = 0` (`Dissipation = 1`). Испарение следа — `DecayFieldScalar`. Скорость гасит `DecayField` (двухканальный Velocity). `DecayFieldScalar` на `velocity` не ставить: кернел скалярный.

#### 3. Пресет `Assets/Effects/Physarum_Fluid.asset`

`Physarum.asset` не переписывать. Меню `Tools/M3D/Create Physarum Fluid Effect`.

Поля, плоскость XZ, origin 0:

| Поле | Формат | Resolution | Size |
| --- | --- | --- | --- |
| `trail` | Scalar `R16_SFloat` | 128² | 32×32 |
| `velocity` | Velocity `R16G16_SFloat` | 128² | 32×32 |

Куб как у Physarum: `resolution = 32`, `cubeSize = 32`, `simulationSpeed = 1`, `particleSize = 0.2`, fire-градиент, value scale 1.

Порядок:

1. `TouchInjectVelocity` → `velocity`
2. `DecayField` → `velocity`, `decayRate = 0.4`
3. `ClearFieldAccum` → `trail`, `channels = 1`
4. `ScatterDensity` + `NormalizeDensity` → `trail` (scale 4096, bias 0)
5. `AdvectScalar` → scalar `trail`, velocity `velocity`, `dissipationRate = 0`, `reverse = false`, `wrapUv = true`
6. `DecayFieldScalar` → `trail`, rate `0.8`
7. `DiffuseField` ×2 → `trail`, rate `0.15`
8. `PhysarumSteer` (дефолты ADR-032)
9. `Integrate`
10. `BoxBounds` Wrap, extents `(16, 0, 16)`
11. `HeadingToValue`

`0.4` — стартовая длина мазка, не гейт. Дефолт `DecayField` `1.5` гасит толчок примерно за полсекунды симуляционного времени, сеть не успевает ответить.

Квады: `trail` heatmap, `colorScale = 0.03`, как у Physarum; `velocity` с `colorScale = 0.125`, как у Fluid2D. Оба числа смотровые.

Агенты скорость не читают. Сдвиг сети — только перенос следа и новое осаждение на следующем кадре.

В Play нужен InputRouter **GroundXZ**. Меню роутер не переназначает.

Новый `.compute` не заводить. `AdvectScalar` уже в `FieldPasses.compute`, файл уже в Pass Library.

#### 4. DoD

| Что | Как |
| --- | --- |
| Дефолт | `wrapUv == false`. Существующий GPU-оракул гаусса (`dCOM_x ≈ 8` за 8 шагов) зелёный и флаг не включает. Этот оракул границу не пересекает, wrap им не доказать |
| Wrap | Поле 8², size 8, `dt = 1`, один шаг, скорость `(1, 0)`, скаляр `1` в текселе `(7, 4)`. После шага значение в `(0, 4)` > `0.9`, в `(7, 4)` < `0.1`. Сумму по полю не ассертить: билинейный перенос смазывает пик, это уже известно |
| Clamp не сломан | Тот же сид с `wrapUv = false` не требует переноса на левый край. Достаточно, что старые тесты адвекции зелёные |
| Пресет | `Physarum_Fluid.asset` в порядке §3. `Physarum.asset` без поля `velocity` и без `AdvectScalar` |
| Смоук | `Rebuild()` без `VisualEffect`, `particles.Count == 32768`, мир не выключен |
| Доки | `pass-catalog` (opt-in wrap), `status`, `capabilities`, `getting-started`. ADR-023 не переписывать |

### Отклонённые варианты

**Новый класс пасса.** `AdvectScalarPass` уже переносит произвольный скаляр произвольной скоростью.

**`frac` перед линейным сэмплом.** Шов тора рвётся.

**Поменять clamp на repeat у всех вызовов.** Fluid2D и MacCormack-reverse начнут заворачивать краску.

**`DecayFieldScalar` на скорости.** Не тот тип текстуры.

**Проекция, Jacobi, стены, адвекция самих агентов.** Другой тикет. Без них палец толкает сеть, а не закручивает её в вихрь.

**Жёсткий assert на сохранение суммы следа.** Билинейный backtrace уменьшает пик даже при `Dissipation = 1`.

### Последствия

- (+) Сеть можно снести существующим пассами. Новый код — ветка сэмплера и один пресет.
- (+) Fluid2D остаётся на clamp, пока флаг выключен.
- (−) Картинка — толчок пальцем, не устойчивый вихрь.
- (−) `decayRate = 0.4` не калибровался в Play.
