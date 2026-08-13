## ADR-011: Boids Alignment — Steering Force Pass + Velocity Field Blur

**Статус:** Предложено (rev. 3 — после второго круга вопросов Grok)
**Дата:** 2026-08-13
**Контекст:** M3D Framework, тонкая настройка боидов (см. `DOC/tmp/Boidshandoff.md`)

**Rev. 2 меняет исходное решение по существу** (не только детали) — см. "Изменения после ревью" в конце документа. Главное: **не трогаем** `SampleVelocityFieldPass` (используется в `HybridTouchField`/`AgentFieldEcho` как Transport-без-dt, глобальный `*dt` их сломал бы), вместо этого — **новый** пасс для alignment. Плюс третий дефект, найденный при ревью: `Boids_mk1.simulationSpeed=0.2` делает Diffuse (и не только на flockVel — на `cohesionDensity` тоже) численно почти нулевым независимо от resolution/числа пассов.

### Контекст

Расследование "боиды выглядят как псевдо-случайный шум, а не как стая" (`Boids_mk1.asset`, `Gray-Scott-Boids.asset`) нашло два конкретных, численно подтверждённых дефекта в текущей реализации alignment/cohesion/separation через поля (не архитектурная ошибка выбора метода — сам field-based подход валиден, см. `architecture.md` раздел "Дешёвая альтернатива для flocking").

#### Дефект 1 — `SampleVelocityFieldPass` не масштабируется на `dt`

```246:253:Assets/Scripts/Passes/FieldPasses.cs
protected override void SetParams(SimContext context, float deltaTime)
{
    SimField field = context.Fields.Get(velocityFieldName);
    context.Cmd.SetComputeTextureParam(Kernel.Shader, Kernel.Index, velocityReadId, field.Current);
    FieldShaderParams.Push(context.Cmd, Kernel.Shader, fieldDescriptor);
    SetFloat(context, SampleStrengthId, strength);
}
```

```79:101:Assets/Shaders/GPU/Passes/FieldPasses.compute
// --- SampleVelocityField (hybrid: field Read + particle velocity write) ---
...
float SampleStrength;
...
velocity[id.x] += FieldUVToWorldVelocity(fieldVel) * SampleStrength;
```

Параметр `deltaTime` не пушится в шейдер, кернел не объявляет `DeltaTime` вообще. Все остальные силы кадра (`CurlNoiseForce`, `SampleGradientField`, `DragPass` через `DragFactor=exp(-drag*dt)`) масштабируются на `dt = Time.deltaTime * SimulationSpeed` (`SimulationWorld.cs:62`). На `Boids_mk1.asset` (`simulationSpeed: 0.2`) это даёт alignment (`strength: 0.6`) на несколько порядков сильнее cohesion/separation (`strength: 1` / `-0.9`, оба `×dt`) за кадр, при сопоставимых величинах поля и editor FPS (~60-200) — точный множитель зависит от FPS/`|flockVel|`, порядок величины 10²-10³. На `Gray-Scott-Boids.asset` (`simulationSpeed: 50`) разрыв меньше, но того же знака — баг общий, не специфичный для одного ассета.

**Важно (уточнено при ревью):** `SampleVelocityFieldPass` — не только boids-пасс. Он же используется в `HybridTouchField(.1).asset` и `AgentFieldEcho 1.asset` (оба `simulationSpeed: 1`, `strength: 1`) как основной механизм "частицы едут по полю" — это рабочие, проверенные демки, не имеющие отношения к alignment. Категория `PassCategory.Transport` (по ADR-004 — осознанное архитектурное разграничение "перенос значения, не сила") для них корректна и не является багом: они не суммируют силу, а как бы "телепортируют" частицу вдоль поля каждый кадр, это устоявшийся паттерн. Отсутствие `dt` в этом пассе — баг **только в контексте boids-alignment**, где `SampleVelocityFieldPass` используется не по первоначальному назначению (не hybrid-transport, а approximation Reynolds alignment). Добавлять `dt` глобально в существующий пасс — регрессия для Hybrid/Echo (их импульс упадёт в ~1/dt раз, эффект станет незаметен). Решение — не трогать существующий пасс (см. "Решение", п.1 ниже, изменено при ревью).

#### Дефект 2 — `flockVel` не блюрится, alignment читает не "среднюю скорость стаи"

```29:39:Assets/Effects/Boids_mk1.asset
- id: {name: flockVel, semantic: 0}
  resolution: {x: 128, y: 128}
  size: {x: 50, y: 50}
```

В пайплайне `flockVel` есть только `ClearFieldAccum → ScatterVelocity → NormalizeVelocity → DecayField` — **без** `DiffuseFieldPass`. Для сравнения, `cohesionDensity` (32×32 + 6× Diffuse) специально сглаживается. `architecture.md` явно рецептирует:

```162:166:DOC/architecture.md
Точные соседи для boids на мобилке часто не нужны: splat velocity/density частиц в грубое
поле (64×64), blur, каждая частица читает «среднюю скорость стаи»...
```

При тексель ≈0.39 юнита (128 res / 50 size) и типичной плотности частиц в ассетах P2G-депозит nearest-cell усредняет ~1 частицу на тексель — alignment фактически читает почти собственную скорость частицы (без сглаживания по окрестности), а не консенсус направления локальной группы.

`DiffuseFieldPass` не может быть применён к `flockVel` напрямую: он объявлен только для `Texture2D<float>` (Scalar, 1 канал), а `flockVel` — `Texture2D<float2>` (Velocity, 2 канала, `RG16F`). Нужен отдельный кернел/пасс.

#### Дефект 3 (найден при ревью) — `Boids_mk1.simulationSpeed=0.2` делает Diffuse численно пустым операцией, независимо от resolution

`DiffuseFieldPass`/новый `DiffuseVelocityFieldPass` продвигают поле как `c + rate * dt * laplacian` за проход. При `simulationSpeed=0.2` и editor FPS≈60: `dt ≈ 0.0033`. При `rate=0.15-0.18`: `rate*dt ≈ 0.0005-0.0006` **за один проход**. У `cohesionDensity` уже есть 6× `DiffuseFieldPass` в пайплайне — суммарный эффект ≈`0.003-0.004`, то есть даже существующий, уже закоммиченный Diffuse на cohesion почти не диффундирует при этом `Speed`; видимая гладкость `cohesionDensity` в текущем результате — в основном следствие грубого `32×32` (P2G сам по себе кладёт крупными текселями), не работы Diffuse-пасса. Диффузионное "расползание" на N проходов ~`sqrt(N·rate·dt)` текселей — при таком `rate·dt` нужны тысячи проходов, чтобы дойти до соседнего текселя. Понижение resolution `flockVel` (64×64 вместо 128×128, как в `architecture.md`) **не решает эту проблему** — она в масштабе времени (`dt`), не в размере текселя.

Следствие: фикс дефекта 2 (Diffuse на flockVel) физически не будет работать на `Boids_mk1` без отдельного решения по `simulationSpeed` — это не опция, а обязательная часть фикса.

**Уточнено при втором ревью — не копировать `Speed=50` с `Gray-Scott-Boids` бездумно.** У `Gray-Scott-Boids` `Speed=50` калиброван под CFL реакции (`Du=0.16`), это не универсально безопасный `dt` для любого пасса. Для steering (п.1 выше) `k=strength*dt` при `Speed=50`/60 FPS уже `≈0.83` — близко к границе устойчивости explicit Euler; безопасный старт для `Boids_mk1` — **`Speed≈20`** (`dt≈0.33`, `k≈0.33` при `strength=1`), не 50. `saturate(k)` в кернеле (п.1) — защита от разгона, а не оправдание держать `k` близко к границе намеренно.

**Диффузионный радиус — числа надо проверить, не считать «Diffuse есть → значит blur работает».** Даже после подъёма `Speed`: при 6 проходах, `rate*dt≈0.05-0.15`/проход, диффузионное расползание ~`sqrt(2·N·rate·dt)` текселей — на `128×128` это доли текселя, соседний тексель почти не смешивается ("Diffuse есть, но радиус эффективно нулевой"). Рабочий рецепт — **сочетание** понижения resolution `flockVel` до `64×64` (как рекомендует `architecture.md`) **и** увеличения числа проходов до `6×` (как у `cohesionDensity`), не одно из двух. Опустить только resolution или добавить только больше проходов — недостаточно по отдельности при доступном бюджете диспатчей (~10-20/кадр). Финальный `diffusionRate`/число проходов — калибровать в Play по видимой гладкости `flockVel` quad, стартовая точка — `diffusionRate≈0.15` (ближе к CFL-потолку `cohesionDensity`, не консервативные `0.1` из rev.2 — на умеренном `Speed` `0.1` даёт слишком малый `rate*dt`, чтобы набрать радиус за разумное число проходов).

### Решение

#### 1. Новый пасс `SteerToVelocityFieldPass` (Force, alignment) — `SampleVelocityFieldPass` не трогаем

`SampleVelocityFieldPass` остаётся как есть (Transport, без `dt`) — контракт `HybridTouchField(.1)`/`AgentFieldEcho 1` не меняется, регрессии нет.

Для alignment — **новый** пасс, физически отдельный от "sample field as transport":

- Категория `PassCategory.Force`, кернел `SteerToVelocityField`, читает то же `FieldRequest` (`Velocity`, 2 канала), что и `SampleVelocityFieldPass`.
- Формула — Reynolds-steering, не накопление (по прецеденту `ShapePasses.SpringToRest`, `v += (target-v)*stiffness*dt`), не "сырое" `v += fieldVel*strength*dt`:

  ```hlsl
  float k = saturate(SteerStrength * DeltaTime);
  velocity[id.x] += (target - velocity[id.x]) * k;
  ```

  **`saturate(strength*dt)` — в скоупе, не опционально** (уточнено при втором ревью). Без него explicit Euler на низком FPS/хитче (`k>1`) перелетает target, `k>2` — колебания; при `Speed=50`/60 FPS `k=strength*dt≈0.83` уже близко к границе, на 30 FPS/хитче `k` легко превысит 1 — тот же класс проблемы, что Techdebt 1b (dt clamp), только теперь локально устранённый прямо в кернеле, без системного фикса. Прецедент — `saturate` у `damping` в `SpringToRest`.
  - **Early-out по UV** (частица вне `[0,1]` поля) — пропустить, как в `SampleVelocityField`, **не** трактовать как `target=0`. Иначе `v += (0-v)*k` тормозит частицу до нуля за пределами поля — не "нет alignment", а "принудительное торможение", неверная физика.
  - Отдельный shader-параметр `SteerDeltaTime` **не нужен** — `SetParams` вызывается непосредственно перед dispatch того же кернела в том же `CommandBuffer`, общий `SimShaderIds.DeltaTime` не может быть затёрт чужим пассом между двумя вызовами (так уже работает `SpringToRest`/`SampleGradient`/`DiffuseField`). Нужен только уникальный `SteerStrength` (чтобы не пересечься с `SampleStrength` в этом же файле).
- Раз это новый пасс — старые `strength: 0.6` из `Boids_mk1`/`Gray-Scott-Boids` **не переносятся**, калибруются заново под новую формулу и новый `Speed` (см. Дефект 3 / ТЗ).

#### 2. Новый пасс `DiffuseVelocityFieldPass` + кернел `DiffuseVelocityField`

- Тот же явный 5-point Laplacian, что и `DiffuseFieldPass` (ADR-006), но на `float2` вместо `float`.
- Кернел размещается в `Assets/Shaders/GPU/Passes/FieldPasses.compute` (не в `DiffusePasses.compute` — там уже глобально объявлен `Texture2D<float> FieldRead/FieldWrite`, типовой конфликт same-name-different-type в одном файле; `FieldPasses.compute` уже объявляет `Texture2D<float2> FieldRead; RWTexture2D<float2> FieldWrite;` на уровне файла — переиспользуем эти же глобальные слоты, без нового файла).
- `DiffuseVelocityFieldPass : FieldKernelPass`, `FieldWrites = Single(fieldName, WritePingPong, Velocity, 2)`. `SetParams` пушит `SimShaderIds.DeltaTime` + `SimShaderIds.DiffusionRate` (переиспользуем существующие ID, как в `DiffuseFieldPass`).
- CFL-ограничение то же, что у `DiffuseFieldPass`: `diffusionRate * dt ≲ 0.2–0.25` (проверяется покомпонентно, X/Y раздельно — Laplacian на векторном поле separable по компонентам, никакой связи между каналами не возникает). Дефолт `0.15` (как у `cohesionDensity`, не заниженный `0.1` из rev.2 — заниженный rate не даёт набрать радиус усреднения за разумное число проходов при умеренном `Speed`, см. Дефект 3 выше).
- Место в пайплайне: после `DecayFieldPass(flockVel)`, перед блоком `cohesionDensity` — заменяет "пустое место", где сейчас нет ни одного Diffuse-пасса на flockVel.

#### 3. `Boids_mk1.asset` — поднять `simulationSpeed` (умеренно, не как Gray-Scott-Boids) + `flockVel` 64×64 + 6× Diffuse

`simulationSpeed=0.2` делает любой Diffuse-based blur (не только новый на flockVel — существующий на `cohesionDensity` тоже) численно нулевым (Дефект 3). Решение — поднять `simulationSpeed` **до `≈20`** (не `50` — это калибровка Gray-Scott-реакции, не универсальный ориентир; при `Speed=20` steering `k=strength*dt` остаётся в безопасной зоне даже без `saturate`, а с ним — тем более). Вслед за этим пересчитать: `CurlNoisePass.Amplitude`, `DragPass.Drag`, все G2P `strength` — калибровка начинается **с `Speed`**, потом остальные константы, не наоборот. `flockVel` — понизить resolution до `64×64` и добавить `6×DiffuseVelocityFieldPass` (rate≈0.15), не `2-3` — иначе радиус усреднения остаётся эффективно нулевым даже после подъёма `Speed` (см. Дефект 3, расчёт диффузионной длины).

### Последствия

- Alignment (новый пасс), cohesion, separation становятся однородно `dt`-масштабируемыми — дальше можно калибровать их относительную силу через `strength`, не борясь со скрытым множителем в порядки величины.
- `flockVel` получает управляемый "радиус" усреднения (через `resolution` + число/rate Diffuse-пассов), как и `cohesionDensity` — теперь можно осознанно выбрать: alignment радиус должен быть шире separation, уже cohesion.
- `HybridTouchField(.1)` / `AgentFieldEcho 1` — **не затрагиваются**, `SampleVelocityFieldPass` не меняется.
- Требуется перекалибровка **с нуля** (`Speed` → все `*dt`-константы → `strength` нового пасса) на `Boids_mk1.asset`; на `Gray-Scott-Boids.asset` — только новый пасс + `strength` alignment/cohesion/separation, `Speed=50` уже в рабочем порядке.
- **Вне скоупа этого ADR** (сознательно не смешиваем с этим тикетом):
  - Ограничение по ускорению/повороту (`maxForce`/turn-rate clamp) — сейчас есть только `SpeedLimitPass` (clamp модуля, не направления). Steering-формула (п.1) снижает потребность в этом, но не заменяет полностью. Если после фикса поведение всё ещё дёргается по направлению — отдельный тикет.
  - Переход на spatial hash / честных соседей — критерий перехода (`Techdebt.md`, группа E, п.11) требует сначала честной проверки field-подхода; этот ADR — ровно эта честная проверка.
  - Квантование P2G fixed-point encode (`ScatterVelocity`/`Normalize`, `uint`-буфер) как источник дополнительного шума — не трогаем, отдельная гипотеза, не подтверждена.
  - Systemic `deltaTime` clamp в `SimulationWorld` (Techdebt 1b) — не делаем здесь, хотя новый Diffuse-пасс на flockVel увеличивает экспозицию к тому же классу бага на мобильном.

### Изменения после ревью (Grok, до реализации)

**Rev. 1 → rev. 2:** исходная rev.1 предлагала менять `SampleVelocityFieldPass` глобально (добавить `dt` прямо в существующий пасс). Grok указал, что это ломает `HybridTouchField(.1)`/`AgentFieldEcho 1` (тот же пасс, `strength=1`, `Speed=1`, живой рабочий контракт) и что накопительная формула `v += fieldVel*s*dt` — положительная обратная связь, не steering. Оба замечания приняты: alignment получает **новый** пасс (`SteerToVelocityFieldPass`, steering-формула), старый пасс не трогаем. Также найден и включён в скоуп Дефект 3 (`Speed=0.2` → Diffuse не работает независимо от resolution).

**Rev. 2 → rev. 3:** второй круг вопросов нашёл фактическую ошибку в ТЗ (`FieldPasses.compute` **не** объявляет `float DeltaTime` — надо добавить на уровне файла, не переиспользовать несуществующее) и указал на два содержательных риска: (а) explicit Euler у steering без ограничения `k=strength*dt` расходится на низком FPS — добавлен `saturate(k)` в кернел как часть фикса, не факультативно; (б) расчёт диффузионной длины показал, что и предложенный `Speed=50`(как у Gray-Scott-Boids), и заниженный `diffusionRate=0.1`/2-3 прохода из rev.2 **не дают** реального радиуса усреднения на `Boids_mk1` — переработана рекомендация: `Speed≈20` (не 50, это калибровка реакции GS, не универсальная), `flockVel` 64×64 (не 128×128), `6×DiffuseVelocityFieldPass` rate≈0.15 (не 2-3× при 0.1).
