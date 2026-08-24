# План: Stable Fluid (Stam) для M3D Framework

**Дата создания:** 2026-08-24
**Статус документа:** живой план фазы F1 (+ хвосты F0, черновик F2). Обновлять по факту закрытия каждого пункта — не переписывать историю, дописывать.
**Связанные документы:** [`status.md`](status.md) · [`capabilities.md`](capabilities.md) · [`pass-catalog.md`](pass-catalog.md) · [`last/Techdebt.md`](last/Techdebt.md)

---

## 0. Что понимается под «stable fluid»

Согласовано с пользователем: «stable fluid» = алгоритм Stam (Stable Fluids, semi-Lagrangian advection + Jacobi-проекция давления на несжимаемость), **не** полноценный Navier-Stokes solver с явной вязкостью. Метастабильность (визуально интересные, но не строго физически точные режимы — vorticity confinement и т.п.) — осознанная цель **после** того, как базовый Stam-контур работает предсказуемо, не раньше.

Разработка — **desktop-first**: ограничения мобильного бюджета (bandwidth, dispatch budget, Vulkan/Metal) для fluid-контура сознательно отложены; приоритет — численная корректность и предсказуемость на десктопе. Мобильная адаптация fluid — отдельный будущий тикет, не часть F1.

Единицы измерения по семействам пассов зафиксированы в [ADR-016](ADR/ADR-016-Units-By-Pass-Family.md): fluid-контур (`fluidD`, `fluidPhi`, и далее `velocity` в advect/projection) — **world**-домен, с обязательным квадратным текселем (`RequiresSquareTexel`, [ADR-017](ADR/ADR-017-Divergence-Pass-And-Square-Texel-Contract.md)). Existing texel/UV-соглашения (Diffuse, GrayScott, G2P-градиент) этим не затрагиваются и не приводятся к world — сломало бы калибровку существующих пресетов.

---

## 1. Фаза F0 — фундамент (для всего фреймворка, не только fluid)

| # | Тикет | ADR | Статус | Суть |
| --- | --- | --- | --- | --- |
| F0.1 | World-owned repeat loop | [ADR-015](ADR/ADR-015-World-Owned-Repeat-Loop.md) | **Готово** | `SimPass.RepeatCount` (virtual, default 1); `SimulationWorld.Update` гоняет `Execute + Swap` N раз в одном `ProfilingScope`. Первый потребитель — F1.2 (`JacobiPhiPass`). |
| F0.2 | GPU numeric test harness | [ADR-014](ADR/ADR-014-GPU-Numeric-Test-Harness.md) | **Готово** | `FieldTestHarness`, `HarnessProbes.compute` (test-only), миграция ручных MCP-замеров в автотесты (`HarnessSamplerTests`, `HarnessDiffuseTests`, `HarnessAdvectTests`). Обязательная инфраструктура для всех численных DoD фазы F1. |
| F0.3 | Units by pass family | [ADR-016](ADR/ADR-016-Units-By-Pass-Family.md) | **Готово** | Формализация texel/UV/world конвенций; `fluidD`/`fluidPhi` объявлены как `Scalar` `world/s`, `R32_SFloat`; введён `RequiresSquareTexel`. |
| F0.4 | Точечные фиксы | — | **Готово** | Четыре красных теста (`VfxParticleBinder`), NaN в `HeadingSteer`/`BoxBounds`, `RenderTexture.active` warning, физическая вилка Advect-теста. Не fluid-специфично, но разблокировало чистый прогон сьюта перед стартом F1. |
| F0.5 | Снять matching-resolution для read в multi-role (cross-res dye/velocity) | — | **Отложено до после F1** | Цель: dye на высоком разрешении под velocity на грубом. Требует раздельного push `Resolution`/`TexelSize` по ролям A/B в кернел, не общего дескриптора — не тривиальный «убрать проверку равенства». |
| F0.6 | Cross-resolution G2P | — | **Отложено до после F1** | Связано с F0.5; не блокирует Stam-контур на одном разрешении. |
| F0.7 | `deltaTime` clamp в `SimulationWorld` | — | **Открыто, низкий приоритет** ([Techdebt 1b](last/Techdebt.md)) | Не блокер fluid: semi-Lagrangian advection безусловно устойчив к `dt` (в отличие от явного Laplacian у Diffuse/GrayScott, где это реальный CFL-риск на мобиле). |

---

## 2. Фаза F1 — Stam-minimum (ядро fluid-контура)

Цель фазы: минимальный, но полностью корректный проходной Stam-solver на одном разрешении, без явной вязкости, без true boundary conditions поверх clamp-границы (F1.4 вводит их отдельно), готовый к сборке в демо-пресет.

| # | Тикет | ADR | Статус | Суть |
| --- | --- | --- | --- | --- |
| F1.1 | `DivergenceFieldPass` + `RequiresSquareTexel` | [ADR-017](ADR/ADR-017-Divergence-Pass-And-Square-Texel-Contract.md) | **Готово** | `D = uE.x − uW.x + uN.y − uS.y` (raw, не `/2h`), clamp-граница. `SquareTexelValidator`: (a) квадратный тексель на каждом поле, (b) совпадающее `Resolution` по всем полям пасса — Build-time, до `Initialize`. `fluidD`: `Scalar`, `R32_SFloat`. Multi-role: `velocity` Read Role A, `fluidD` WriteInPlace Role B. |
| F1.2 | `JacobiPhiPass` | [ADR-018](ADR/ADR-018-Jacobi-Phi-Pass.md) | **Готово** | `ΦC ← (ΦN+ΦS+ΦE+ΦW − D)/4`. Multi-role: `fluidPhi` WritePingPong Role A, `fluidD` Read Role B (первое чисто read-only использование Role B). `RepeatCount = iterations` (дефолт 40) — первый реальный потребитель F0.1. `fluidPhi`: `Scalar`, `R32_SFloat`. Кернел разведён с `Divergence` через `#ifdef KERNEL_*` в одном `FluidPasses.compute` (типовой конфликт `FieldReadA`: `float2` vs `float`). Класс назван `JacobiPhiPass`, не `JacobiPressurePass` — ADR-016 запрещает называть `Φ` давлением, правило распространено и на имя типа. |
| F1.2b | Zero-mean projection `fluidD` | (в составе ADR-018 §5) | **Открыто — блокер перед F1.6, не перед F1.3/F1.4** | При `ΣD ≠ 0` (типичный случай для несимметричного впрыска через касание) `mean(Φ)` дрейфует линейно: `mean(Φ_{k+1}) = mean(Φ_k) − mean(D)/4`. За `RepeatCount=40` это `10·mean(D)` за кадр; с warm-start (`fluidPhi` не очищается между кадрами) накопление продолжается кадр за кадром до исчерпания мантиссы `R32_SFloat`. Решение: reduction по `D` через `InterlockedAdd` fixed-point аккумулятор (по образцу P2G, ADR-002), вычесть `mean(D)` перед Jacobi. Для F1.2 тесты используют точно zero-mean синтетические источники — дрейф не наблюдается искусственно, но реальный touch-впрыск (F1.6) без этой правки накопит дрейф. |
| F1.3 | `SubtractPhiGradientPass` | [ADR-020](ADR/ADR-020-Subtract-Phi-Gradient-Pass.md) | **Готово** | `u = u* − ((ΦE−ΦW)/4, (ΦN−ΦS)/4)`. WriteInPlace Role A `velocity`, Read Role B `fluidPhi`, `u*` из `FieldWriteA`. Цепочка 3.6: k=8, ≥3× ([ADR-020 §3](ADR/ADR-020-Subtract-Phi-Gradient-Pass.md)). |
| F1.4 | Boundary Conditions | — план | **После F1.3** | Сейчас граница — clamp continuation (эффективно Neumann-подобная, не истинная физическая BC). F1.4 вводит явное обнуление нормальной компоненты `velocity` на границе после проекции (F1.3) — то, что делает границу непроницаемой, а не просто «продолжение соседней ячейки». Взаимодействует с F1.2b: истинная BC меняет null-space задачи Пуассона, разбор зависимости — часть DoD этого тикета, не предполагается заранее. |
| F1.6 | `Fluid2D` пресет | — план | **Блокирован F1.2b** | Сборка `Divergence → Jacobi ×N → SubtractPhiGradient → (Advect velocity, уже есть с ADR-013)` в один `EffectAsset` + touch-инъекция скорости (`TouchInjectVelocityFieldPass`, уже существует). Здесь калибруется реальный дефолт `iterations` у `JacobiPhiPass` (сейчас `40` — предварительная оценка). Калибровать по **осевым** модам Jacobi как худшему случаю (touch широкополосный), не по диагонали — [Techdebt 8e](last/Techdebt.md); помнить, что `λ^40` по Φ не есть отношение `max|D|` ([Techdebt 8f](last/Techdebt.md), errata 2 [ADR-020 §3](ADR/ADR-020-Subtract-Phi-Gradient-Pass.md)). F1.2b — жёсткий блокер: без zero-mean projection удержание касания копит дрейф `mean(Φ)`. |
| F1.7 | `AdvectScalarPass` (dye/tracer) | — план | **После F1.6** | Перенос пассивного скаляра (`dye`) полем `velocity` тем же semi-Lagrangian механизмом, что `AdvectVelocityFieldPass` (ADR-013), но без self-advection (скаляр не влияет на несущее поле). Визуальная проверка результата всего Stam-контура — до этого тикета фактическая корректность fluid-контура наблюдается только по числам харнеса, не глазами. |
| ADR-019 | Fluid2D solver, постфактум | — план (номер зарезервирован в [ADR-014 §таблица](ADR/ADR-014-GPU-Numeric-Test-Harness.md)) | **После F1.7** | Итоговый суммирующий ADR по всему Stam-контуру: коллокейтед cell-centered сетка, число итераций Jacobi, точность (`R32_SFloat` по всей цепочке), контракт границы, шахматная мода коллокейтед-решётки как **known limitation** (не баг) — четный/нечетный checkerboard-паттерн, характерный для non-staggered MAC-схемы, осознанно принят, не устраняется в рамках F1. |

---

## 3. Фаза F2 — качество / метастабильность (черновик, не детализировано)

Не начинать до закрытия всей фазы F1 (включая F1.2b и ADR-019). Список ниже — фиксация направления, не тикеты с DoD.

- **Vorticity confinement** — восстановление энергии высоких частот, потерянной из-за численной диссипации semi-Lagrangian advection (см. [Techdebt 5](last/Techdebt.md): −39% амплитуды гауссова пика за 8 шагов на carrier `1.7`). Это и есть путь к «метастабильности» — визуально живым, закрученным структурам, а не строгой физической точности.
- **MacCormack / BFECC advection** — снижение численной диссипации основного advect-пасса (альтернатива или дополнение vorticity confinement). Явно отложено при закрытии F0.4/ADR-013 как «отдельный тикет, не точечный фикс».
- **Explicit viscosity** — рассмотрено и отклонено для v1 (числово неэффективно на используемых разрешениях); `dissipationRate` (уже есть, ADR-013/ADR-016) остаётся художественным рычагом вместо него. Возврат к теме — только если после живого пресета (F1.6) `dissipationRate` окажется недостаточным художественно.
- **Мобильная адаптация fluid-контура** — desktop-first решение (см. §0) означает, что бюджет на мобиле для этого контура пока не оценивался и не оптимизировался.

---

## 4. Известные, осознанно принятые ограничения (не блокеры, см. Techdebt)

Полный список — [`last/Techdebt.md`](last/Techdebt.md) группа C. Кратко, применительно к fluid:

- **Texel/UV-зависимость параметров** ([Techdebt 8](last/Techdebt.md)) — существующие Diffuse/GrayScott не приводятся к world; fluid — отдельное world-семейство, живёт по своим правилам (ADR-016).
- **Численная диссипация semi-Lagrangian advect** ([Techdebt 5](last/Techdebt.md)) — не баг, задокументированная плата за unconditional stability; путь устранения — F2 (vorticity confinement / MacCormack).
- **Линейный дрейф `mean(Φ)`** ([Techdebt 8d](last/Techdebt.md)) — см. F1.2b выше, единственный из этого списка, что реально блокирует следующий тикет фазы F1 (F1.6).
- **Пол `max|D|` после проекции** ([Techdebt 8e](last/Techdebt.md), [8f](last/Techdebt.md)) — осевые моды Jacobi и несогласованность collocated `div∘grad`; не блокер F1.3/F1.4.

---

## 5. Как читать этот план

- Столбец «Статус» — источник истины по факту закрытия; при закрытии тикета обновлять здесь **и** в [`status.md`](status.md) (там — журнал по датам, здесь — план по порядку зависимостей).
- Порядок F1.1 → F1.2 (+F1.2b) → F1.3 → F1.4 → F1.6 → F1.7 → ADR-019 — это зависимости, не даты; F1.2b может закрываться параллельно с F1.3/F1.4 (не блокирует их), но обязан закрыться строго до F1.6.
- Каждый закрытый пункт фазы F1 сопровождается собственным ADR и `todo-*.md` в [`ADR/`](ADR/) и [`last/`](last/) — этот документ не заменяет их, только даёт карту целиком.
