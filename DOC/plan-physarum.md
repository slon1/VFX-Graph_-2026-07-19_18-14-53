# План реализации: Physarum Polycephalum (Slime Mold / Слизевик)

**Дата:** 2026-09-26  
**Статус документа:** черновик идеи. Сенсор и пресет — [ADR-032](ADR/ADR-032-Physarum-Steer.md). Перенос следа скоростью — [ADR-033](ADR/ADR-033-Physarum-Trail-Advect.md) и [ТЗ](last/todo-adr-033-physarum-trail-advect.md). Раздел 5 этого черновика (полный Stam внутри пресета) не исполнять.  
**Стек:** Unity 6 (`6000.5.9f1`) · URP · Compute Shaders · UniTask  
**Связанные документы:** [`architecture.md`](architecture.md) · [`status.md`](status.md) · [`capabilities.md`](capabilities.md) · [`pass-catalog.md`](pass-catalog.md) · [`plan-stable-fluid.md`](plan-stable-fluid.md) · [`plan-m2d-lut-trail.md`](plan-m2d-lut-trail.md) · [`ADR-012`](ADR/ADR-012-Kinematic-Heading-Boids.md) · [`ADR-031`](ADR/ADR-031-Primitive-Only-And-Value-Palette.md) · [`ADR-032`](ADR/ADR-032-Physarum-Steer.md) · [`ADR-033`](ADR/ADR-033-Physarum-Trail-Advect.md)

Тикет программиста P1–P2 закрыт ADR-032. P3 (три режима Джонса) не открыт. Урезанный P4 закрыт [ADR-033](ADR/ADR-033-Physarum-Trail-Advect.md): EditMode зелёный, Play пальцем не закрыт. Раздел «Фаза P4» ниже — полный Stam, его не исполнять.

Расхождения черновика с кодом, закрытые в ADR-032:

- `CubeSource` считает `resolution³`. Решётки `200×200×1` нет. В пресете `resolution = 32` (32768 агентов).
- `NormalizeDensityAccum` **прибавляет** след. `ClearField(trail)` в кадре стирает сеть. Его нет.
- Сэмпл сенсора — `sampler_linear_repeat`, не clamp. Clamp при `BoxBounds` Wrap сажает агентов на рамку.
- Правило поворота — Джонс: оба боковых выше переднего → случайный знак. Черновик в этом случае поворачивал к большему боковому.
- `turnAngle` — градусы за вызов, без `dt`. `moveSpeed` — единицы в секунду через `Integrate`.

---

## 0. Концепция и цель: «Поиск завораживающего»

Фреймворк M3D создавался как исследовательский инструмент для быстрого перебора эмерджентных алгоритмов методом проб. 
После закрытия базового контура Stam-флюида (F1/F2/F3) и быстрого Primitive-рендера с LUT-палитрами (ADR-030, ADR-031), фокус смещается с доказательства физических теорем на **высокоэффектную визуальную самоорганизацию**.

**Physarum polycephalum (модель Джеффа Джонса, Jeff Jones, 2010)** — один из эталонных алгоритмов в генеративной графике.
Из сотен тысяч примитивных агентов, взаимодействующих исключительно через одно общее диффундирующее скалярное поле (хемоаттрактант/след), спонтанно возникают:
* Самоорганизующиеся транспортные сети и мицелиальные структуры.
* Пульсирующие древовидные русла и органические лабиринты.
* Динамическое затягивание повреждений и поиск оптимальных путей в обход преград.

В сочетании с уже реализованной в M3D гидродинамикой (**Fluid2D**) Physarum открывает уникальный гибридный режим: **«живая колония в турбулентном потоке жидкости»**, где макро-течение размывает и сносит сеть, а микро-агенты непрерывно борются за восстановление связности.

---

## 1. Математическая модель (Jeff Jones, 2010)

Симуляция дискретна во времени и состоит из двух циклических фаз: фазы агентов и фазы поля.

```
       ┌────────────────────────────────────────────────────────┐
       │             ПОЛЕ СЛЕДА (Scalar Field, R16F)            │
       │   1. Decay (испарение)   2. Diffuse (сглаживание)      │
       └──────────────┬──────────────────────────▲──────────────┘
                      │                          │
        Сэмплирование │ (3 сенсора:              │ Осаждение следа
         концентрации │  F, FL, FR)              │ (P2G Accum Sum)
                      ▼                          │
       ┌─────────────────────────────────────────┴──────────────┐
       │                  АГЕНТЫ (Particles)                    │
       │   3. Сенсоры -> Руление -> Смещение (Heading / Step)   │
       └────────────────────────────────────────────────────────┘
```

### 1.1. Агенты (Sensory & Motor Phase)
Каждый агент обладает положением в мировом пространстве $\mathbf{p} \in \mathbb{R}^3$ (на плоскости $XZ$) и единичным вектором курса $\mathbf{h} \in \mathbb{R}^3$ ($|\mathbf{h}| = 1, h_y = 0$).

1. **Сенсорный опрос (Sensing):**  
   Агент считывает концентрацию поля $T$ в трёх точках на фиксированном расстоянии $d_{sensor}$ и угле разброса $\alpha$:
   * **Центральный сенсор (F):** $\mathbf{p}_F = \mathbf{p} + \mathbf{h} \cdot d_{sensor}$
   * **Левый сенсор (FL):** $\mathbf{p}_L = \mathbf{p} + \mathbf{R}(+\alpha) \mathbf{h} \cdot d_{sensor}$
   * **Правый сенсор (FR):** $\mathbf{p}_R = \mathbf{p} + \mathbf{R}(-\alpha) \mathbf{h} \cdot d_{sensor}$  
   где $\mathbf{R}(\theta)$ — поворот вокруг нормали плоскости поля ($\mathbf{n} = (0,1,0)$).

2. **Руление (Steering):**  
   Концентрации $C_F = T(\mathbf{p}_F)$, $C_L = T(\mathbf{p}_L)$, $C_R = T(\mathbf{p}_R)$ определяют поворот на угол $\beta$ (`turnAngle`):
   * Если $C_F > C_L$ и $C_F > C_R$ $\implies$ сохранять курс (поворот $0$).
   * Если $C_L > C_R$ $\implies$ поворот влево на $+\beta$.
   * Если $C_R > C_L$ $\implies$ поворот вправо на $-\beta$.
   * Если $C_L == C_R$ (или впереди тупик / ровное поле) $\implies$ случайный выбор знака поворота или сохранение курса с добавлением стохастического шума $w$ (`randomWiggle`).

3. **Моторная фаза (Motor step):**  
   Агент обновляет скорость: $\mathbf{v} = \mathbf{h} \cdot \text{speed}$.  
   Позиция интегрируется: $\mathbf{p} \leftarrow \mathbf{p} + \mathbf{v} \cdot \Delta t$.

4. **Осаждение (Deposit):**  
   Агент вносит фиксированную порцию аттрактанта в поле под собой: $\Delta T$.

### 1.2. Поле следа (Chemoattractant Field)
* **Затухание (Decay):** $T \leftarrow T \cdot \exp(-\text{decayRate} \cdot \Delta t)$.
* **Диффузия (Diffusion):** $T \leftarrow T + \text{diffRate} \cdot \nabla^2 T$ (5-точечный дискретный лапласиан).

---

## 2. Архитектурный маппинг на M3D Framework

Physarum **на 85% собирается из уже готовых, протестированных примитивов**. Требуется написать только один специализированный сенсорный пасс руления.

| Компонент Physarum | Существующий примитив M3D | Роль / Статус |
| :--- | :--- | :--- |
| **Поле следа** | `FieldSet` (`Scalar`, `R16_SFloat`, XZ) | Готово (`FieldDescriptor`) |
| **Осаждение следа (P2G)** | `ClearFieldAccumPass` + `ScatterDensityToFieldPass` + `NormalizeDensityAccumPass` | Готово (Sum-decode режим M2b.2.1) |
| **Затухание следа** | `DecayFieldScalarPass` | Готово (ADR-007) |
| **Размытие следа** | `DiffuseFieldPass` | Готово (ADR-006, 5-point Laplacian) |
| **Сенсоры и поворот** | **`PhysarumSteerPass`** | **НОВЫЙ ПАСС (цель плана)** |
| **Движение** | `IntegratePass` | Готово (`DynamicsPasses.compute`) |
| **Зацикливание границ** | `BoxBoundsPass` (режим `Wrap`) | Готово (`DynamicsPasses.compute`) |
| **Отрисовка частиц** | `PrimitiveParticleBinder` + `HeadingToValuePass` | Готово (ADR-031, палитра по курсу) |
| **Отрисовка сети следов** | `FieldDebugQuadsBinder` (`ScalarHeatmap` + LUT + HDR) | Готово (ADR-010, ADR-025) |

---

## 3. Спецификация нового пасса: `PhysarumSteerPass`

### 3.1. C# класс `PhysarumSteerPass`
Наследуется от `ParticleKernelPass`.

```csharp
[Serializable]
public sealed class PhysarumSteerPass : ParticleKernelPass
{
    [SerializeField] private string trailFieldName = "trail";
    
    // Параметры сенсоров
    [SerializeField, Range(5f, 90f)] private float sensorAngle = 22.5f;   // Угол разброса сенсоров (градусы)
    [SerializeField, Min(0.01f)] private float sensorDistance = 1.5f;     // Дистанция сенсоров (мировые единицы)
    
    // Параметры моторики
    [SerializeField, Range(0f, 180f)] private float turnAngle = 45f;      // Скорость поворота
    [SerializeField, Min(0f)] private float moveSpeed = 3f;               // Линейная скорость
    [SerializeField, Range(0f, 1f)] private float randomWiggle = 0.1f;    // Амплитуда случайного рыскания
    
    // Reads: Position, Heading
    // Writes: Heading, Velocity
    // FieldReads: trail (FieldAccess.Read, FieldSemantic.Scalar, 1)
}
```

### 3.2. HLSL-ядро (`PhysarumPasses.compute` или секция в `DynamicsPasses.compute`)
1. **Входные данные:**
   * Буферы: `RWStructuredBuffer<float3> position`, `RWStructuredBuffer<float3> heading`, `RWStructuredBuffer<float3> velocity`.
   * Текстура: `Texture2D<float> FieldRead`, сэмплер `sampler_linear_clamp`.
   * Униформы плоскости из `FieldSampling.hlsl` (`FieldOrigin`, `FieldAxisU`, `FieldAxisV`, `FieldSize`).
2. **Логика ядра:**
   * Проверка выхода за границы `id.x >= ParticleCount`.
   * Инициализационный guard: если `dot(h, h) < 1e-4`, генерируется начальный случайный единичный вектор направления через PCG-хеш от `id.x`.
   * Вычисление базиса поворота в плоскости (для XZ: нормаль $(0,1,0)$, поворот вектора через $\cos/\sin$).
   * Проецирование трёх сенсорных позиций в UV через `WorldToFieldUV()`.
   * Сэмплирование трёх точек:
     ```hlsl
     float cF = FieldRead.SampleLevel(sampler_linear_clamp, uvF, 0);
     float cL = FieldRead.SampleLevel(sampler_linear_clamp, uvL, 0);
     float cR = FieldRead.SampleLevel(sampler_linear_clamp, uvR, 0);
     ```
   * Принятие решения:
     * Если $cF > cL$ и $cF > cR \implies$ угол не меняем.
     * Если $cL > cR \implies$ поворот на $+\Delta \theta$.
     * Если $cR > cL \implies$ поворот на $-\Delta \theta$.
     * Иначе $\implies$ псевдослучайный поворот $\pm \Delta \theta$ на основе хеша.
   * Добавление стохастического шума `randomWiggle`.
   * Запись:
     ```hlsl
     heading[id.x] = newHeading;
     velocity[id.x] = newHeading * moveSpeed;
     ```

---

## 4. Пайплайн кадра в `Physarum.asset`

Цепочка пассов в `EffectAsset` формирует строгий замкнутый цикл:

```
1. ClearFieldAccum (trail, channels=1)
2. ScatterDensityToField (trail, sum-decode)
3. NormalizeDensityAccum (trail)              --> [P2G: агенты оставили след]
4. DecayFieldScalar (trail, rate ~ 0.5..2.0)  --> [След затухает во времени]
5. DiffuseField (trail, rate ~ 0.15)          --> [След расплывается к соседям]
6. DiffuseField (trail, rate ~ 0.15)          --> [2-й проход для радиуса связи]
7. PhysarumSteer (trail, angle/dist/turn)     --> [Агенты считывают и рулят]
8. Integrate (dt)                             --> [Смещение агентов вперед]
9. BoxBounds (Wrap на XZ)                     --> [Зацикливание тороидальной геометрии]
10. HeadingToValue                            --> [Окраска частиц по направлению для LUT]
```

---

## 5. Интеграция с Fluid2D (`fluid_hybrid`)

Связка со `Stable Fluid` превращает Physarum из замкнутой абстракции в интерактивную живую экосистему.

### Схема гибрида: Fluid как несущая среда следа (Carrier)
Вместо изолированного поля `trail`, след слизевика **сносится течением жидкости**:

```
[Агенты Physarum] ──(P2G Deposit)──> [Поле Trail]
                                         │
                                         ▼
                               [AdvectScalarPass] <── [Поле Velocity от Fluid2D]
                                         │
                                         ▼
                               [Decay & Diffuse]
                                         │
                                         ▼
[Агенты Physarum] <──(Sensors)────── [Поле Trail]
```

### Визуальный эффект:
* Пользователь проводит пальцем/мышкой по экрану $\implies$ `TouchInjectVelocityFieldPass` создаёт вихри в `velocity`.
* `AdvectScalarPass` закручивает поле феромонов `trail` в спирали и потоки.
* Агенты слизевика теряют устойчивые мосты, следуют за уплывающим следом, вытягиваются вдоль линий тока и затем динамически **срастаются заново**, создавая эффект самовосстанавливающейся био-ткани.

---

## 6. Фазы реализации и Definition of Done (DoD)

### Фаза P1: Базовый сенсорный пасс и шейдер (Ядро)
* **Цель:** Реализовать кернел `PhysarumSteer` и C# класс `PhysarumSteerPass`.
* **Тесты (EditMode):**
  * `PhysarumSteerPassTests`: проверка корректности регистрации reads (`Position`, `Heading`), writes (`Heading`, `Velocity`) и `FieldReads` (`trail`).
  * `HarnessPhysarumTests` (GPU Harness):
    * Агент перед пятном следа слева поворачивает влево.
    * Агент перед пятном следа справа поворачивает вправо.
    * При симметричном следе впереди агент сохраняет прямой курс.

### Фаза P2: Сборка пресета `Physarum.asset`
* **Цель:** Создать готовый ScriptableObject пресет `Assets/Effects/Physarum.asset` и меню `Tools/M3D/Create Physarum Effect`.
* **Конфигурация:**
  * Частицы: `CubeSource` (разрешение $200 \times 200 \times 1$ на плоскости или куб, сплюснутый по Y), 40k–100k частиц.
  * Поле `trail`: `Scalar`, `R16_SFloat`, $128 \times 128$ или $256 \times 256$, плоскость XZ, размер $50 \times 50$.
  * Рендер: `PrimitiveParticleBinder` для светлячков + `FieldDebugQuadsBinder` (LUT-градиент теплокарты сети).
* **Смоук-тест:** `SimulationWorld.Build()` с ассетом `Physarum.asset` компилируется без ошибок и предупреждений.

### Фаза P3: Визуальная калибровка в Play Mode (Finding the Wow)
* **Цель:** Подобрать три фундаментальных режима паттернов Джонса в Play Mode:
  1. *Filament/Networks (Тонкая паутина):* малый сенсорный угол ($22.5^\circ$), умеренная дистанция.
  2. *Spirals/Vortices (Био-вихри):* средний угол ($45^\circ$), высокий `turnAngle`.
  3. *Cellular/Pores (Лабиринты и ячейки):* большой угол ($60^\circ - 80^\circ$), высокая диффузия.
* **Результат:** Зафиксировать удачные дефолтные параметры в ассете, подключить Bloom/HDR палитру.

### Фаза P4: Эксперимент `Physarum_Fluid.asset` (Гибрид)

Этот раздел не исполнялся. В коде другой объём: [ADR-033](ADR/ADR-033-Physarum-Trail-Advect.md) переносит `trail` уже существующим `AdvectScalar` (`wrapUv`, dissipation 0) и гасит скорость через `DecayField`. Проекции, вихря и восстановления мостов там нет.

* **Цель черновика:** добавить `AdvectScalarPass(trail, velocity)` в пресет с активным Fluid2D солвером.
* **DoD черновика:** тач деформирует сеть слизевика; после затухания вихря сеть восстанавливает мосты.

---

## 7. Риски и краевые случаи

1. **Коллапс всех агентов в сингулярность (Clustering collapse):**
   * *Причина:* Если диффузия слабая, а затухание медленное, агенты сбиваются в плотные круглые капли и застревают.
   * *Решение:* Баланс между `DiffuseFieldPass` (минимум 1-2 прохода лапласиана за кадр) и `DecayRate`.
2. **Анизотропные осевые лучи (Сетка):**
   * *Причина:* Дискретная сетка поля может притягивать углы к кратным $0^\circ, 90^\circ, 45^\circ$.
   * *Решение:* Билинейный сэмплинг `sampler_linear_clamp` (уже проверен в ADR-013) + стохастический `randomWiggle`.
3. **Граничные артефакты:**
   * *Причина:* Агенты улетают за пределы поля и получают clamp-концентрацию на границе.
   * *Решение:* `BoxBoundsPass` в режиме `Wrap` с размерами, точно совпадающими с `FieldSize`.
4. **Переполнение Fixed-Point uint аккумулятора при P2G:**
   * *Причина:* Тысячи агентов скапливаются в одном текселе.
   * *Решение:* Стандартная защита P2G-подсистемы M3D (`ValueBias` и `ValueScale` калибруются под sum-decode).
