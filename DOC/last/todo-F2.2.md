## ТЗ для программиста — F2.2 (limited MacCormack dye)

**Закрыто 2026-09-18.** EditMode + visual. Вердикт: схема на месте, look F2 не взят (клубы как Vorticity). [ADR-028](../ADR/ADR-028-Limited-MacCormack-Dye.md). Это ТЗ не переоткрывать.

Роль: меньше смаза **dye**, не новый солвер скорости. Пасс-цепочка + `Assets/Effects/Fluid2D_MacCormackDye.asset`. По [ADR-028](../ADR/ADR-028-Limited-MacCormack-Dye.md) и [ADR-026 § F2.2](../ADR/ADR-026-F2-Small-Scale-Structure.md).

Прочитать **оба** ADR **до кода**. Без этого легко открыть Role C, склеить dye+velocity+scratch в один пасс или переписать живой Vorticity.

Зафиксировано — не пересматривать:

1. **`Fluid2D.asset` / HarrisOrder / Vorticity не трогать.** Не Create Fluid2D / Harris / Vorticity. Vorticity на диске: `radiusUV=0.16`, `ε=1`, `m=2`.
2. **Кернелы проекции F1 и GPU-тесты F1 / F2.0 / F2.1 не менять по составу.** `AdvectScalarPassTests` остаются зелёными (`reverse` default false).
3. **Имя метода — limited MacCormack.** Не BFECC. Не MacCormack velocity.
4. **ADR-008: максимум 2 имени поля на пасс.** Scratch — `FieldDescriptor` `dyeMacScratch`, не `FieldReadC`. Не `Fields.Get` мимо `FieldReads`/`FieldWrites`. Не ZeroMean-`Execute` со скрытым RT.
5. **Цепочка World** (после SolidWall): `CopyScalar` → `AdvectScalar(+dt)` → `AdvectScalar(reverse)` → `LimitedMacCormackCombine`. Порядок и ping-pong — ADR-028 §1.
6. **Формула combine дословно ADR-028 §2.** Точечный clamp к `{φ0,φ_f,φ_b}` + `φ*≥0`. Не 5-точка φ0.
7. **Кернелы в `FieldPasses.compute`.** Новый `.compute` нет. `PassLibraryPaths` не расширять. Legacy `#ifndef` расширить токенами Copy и Combine (иначе в вариант Copy попадут тела `float2`).
8. **`Execute` у `FieldKernelPass` не переопределять.** Copy и Combine — обычные пассы.
9. **GPU ms — запись, не порог.** D/KE F2.1 не гонять.
10. **EditMode зелёный ≠ F2.2 закрыт.** Visual — [`play-F2.2-touch.md`](play-F2.2-touch.md).
11. **`radiusUV`:** Create пишет **0.08**. После Create на новом ассете `SetDirty` **0.16** (как look F2.0/F2.1). Пресет-тест: `0.16`. Create Vorticity не вызывать.
12. Assign в сцену после EditMode не делать.

Референсы: `AdvectScalarPass` + `#ifdef KERNEL_ADVECTSCALAR`; `SubtractPhiGradientPass` (Load с `FieldWriteA`); `CreateFluid2DVorticityExperiment` (клон полей/меню — **новый путь**, вставить 4 хвоста вместо одного Advect); `AdvectScalarPassTests` 3.1–3.3.

Имена: `CopyScalarPass` / кернел `CopyScalar` / `KERNEL_COPYSCALAR`; `LimitedMacCormackCombinePass` / `MacCormackCombine` / `KERNEL_MACCORMACKCOMBINE`; флаг `AdvectScalarPass.Reverse`; ассет `Fluid2D_MacCormackDye.asset`. Не `Fluid2D_F2`.

Код писать. Документацию из шага «доки» — тоже.

---

### Шаг 1 — `Reverse` на `AdvectScalarPass`

```csharp
[SerializeField] private bool reverse;
public bool Reverse { get => reverse; set => reverse = value; }
```

`SetParams`: если `reverse` — **сначала** `Dissipation = 1f`, затем `DeltaTime = -deltaTime`. Не считать `exp(-rate * отрицательный dt)` (это усилитель). Иначе как сейчас `exp(-rate*dt)` и `+dt`. Default false. Fluid2D Create не трогать.

---

### Шаг 2 — `CopyScalarPass`

`FieldPasses.cs`, рядом с AdvectScalar. DisplayName `"Copy Scalar"`. Category Transport. `KernelName => "CopyScalar"`.

```
FieldWrites: dyeMacScratch WriteInPlace Scalar Role A  (default name "dyeMacScratch")
FieldReads:  dye           Read         Scalar Role B  (default "dye")
```

`SetParams` пустой. `RequiresSquareTexel => false`.

Кернел: `FieldWriteA[p] = FieldReadB.Load(int3(p,0))`. Слоты только `FieldWriteA` / `FieldReadB` (`float`).

---

### Шаг 3 — `LimitedMacCormackCombinePass`

DisplayName `"Limited MacCormack Combine"`. `KernelName => "MacCormackCombine"`.

```
FieldWrites: dye           WritePingPong Scalar Role A
FieldReads:  dyeMacScratch Read          Scalar Role B
```

Кернел дословно ADR-028 §2/§4. `φ_f` — `Load` **только в `p`** с `FieldWriteA`. Clamp к `min/max(φ0,φ_f,φ_b)` в том же `p`, затем `φ*≥0`. Соседей UAV и φ0 не читать.

---

### Шаг 4 — `FieldPasses.compute`

1. `#pragma kernel CopyScalar KERNEL_COPYSCALAR`
2. `#pragma kernel MacCormackCombine KERNEL_MACCORMACKCOMBINE`
3. Legacy: `#if !defined(KERNEL_ADVECTSCALAR) && !defined(KERNEL_COPYSCALAR) && !defined(KERNEL_MACCORMACKCOMBINE)` вместо голого `#ifndef KERNEL_ADVECTSCALAR`.
4. Два `#ifdef` с слотами и телами. Существующие формулы AdvectScalar / AdvectVelocity не менять.

---

### Шаг 5 — тесты

`[TestFixture]` без `[Category("GPU")]` на классе. GPU — на численных методах. Compute: `FieldPasses.compute`.

**`CopyScalarPassTests`:** bitwise dye → scratch; dye не меняется.

**`AdvectScalarPassTests`:** существующие зелёные. Добавить: `Reverse` default false; integer `u=(1,0)` **один** forward + **один** reverse → bitwise **интерьер** (рамка ≥1 тексель; `saturate(uv)` край не восстанавливает). Два экземпляра пасса, не flip `Reverse` после `Initialize`.

**`LimitedMacCormackDyeTests`:** харнес dye + velocity + `dyeMacScratch`. Гонять **цепочку** Copy+Fwd+Back+Combine (как World), не один combine на мусоре.

- `u=0`: bitwise seed (R32) — канаррейка UAV-Load φ_f. Красный → стоп, не `FieldReadC`.
- constant 0.4, `u` косой: constant, допуск как AdvectScalar 3.3.
- integer `(1,0)`, 8 шагов: `|dCOM_x−8|<0.5`, velocity bitwise. **Пик не сравнивать** (nearest, оба = amp).
- Gaussian F1.7 (`σ=1.5`, amp=1, центр `(20.5,32.5)`, носитель `(1,0)`, 8 шагов, R32): `|dCOM_x−8|<0.5`, `dCOM_x<10`. Пик на этом носителе **не** гейт.
- **Пик vs bilinear — отдельный прогон, носитель `(1.7, 0)`** (Techdebt 5 / ADR-013, off-grid). Та же гауссиана, 8 шагов, R32, `dt=h=1`. `|dCOM_x−13.6|<0.5`, `dCOM_x<15`. **max интерьера строго >** того же сида на одном `AdvectScalar` ×8. **Assert, не лог.** Красный → стоп, числа; limiter не крутить. Шапка: `elapsedMs/N`, `timer=cpu_driver_not_gpu`. Без D/KE.
- min(dye) ≥ 0.
- Inf/NaN — красный.
- R16: Inf нет, min ≥ 0; пик vs bilinear **не** гейт.
- 3.3 AdvectScalar / identity F2.1 не трогать.

Цепочку D/KE Vorticity не копировать.

---

### Шаг 6 — пресет

`Tools/M3D/Create Fluid2D MacCormackDye Experiment` — клон `CreateFluid2DVorticityExperiment` **нового пути**. Поля: + `dyeMacScratch` (в цикле format: **R16**, как dye, не else R32). VC `{ EpsilonVc=1, BorderMargin=2 }`. Хвост: Copy / Advect / Advect `{ Reverse=true }` / Combine. Seed Create `radiusUV=0.08`. Create = Delete только `Fluid2D_MacCormackDye.asset`.

Сразу после Create, без второго Create: `seed.RadiusUV = 0.16`, `SetDirty`.

`Assign Fluid2D MacCormackDye Experiment To Scene` — как Vorticity, GroundXZ. **Не вызывать из тестов.**

`Fluid2DMacCormackDyePresetTests` (не GPU): 5 полей, **13 пассов** (9 до стены включительно + Copy/Fwd/Back/Combine), типы по порядку ADR-028 §5, одна стена, VC `ε==1` `m==2`, два AdvectScalar (`Reverse` false затем true), Combine после, scratch R16, seed `0.16`, quads только velocity+dye. Сообщение «run Create Fluid2D MacCormackDye Experiment». Create MacCormack — да. Create Vorticity / Fluid2D / Harris — нет.

Smoke: `Rebuild()` на этом ассете зелёный (`Fluid2DWorldSmokeTests` не расширять составом Fluid2D — отдельный метод / класс).

---

### Доки после зелёного EditMode

ADR-028: «EditMode готово» + таблица чисел. Plan/status — **не** «Готово» без visual. Catalog: Copy Scalar, MacCormack Combine, пресет. `getting-started` / capabilities — одна строка, не production.

---

### Visual

[`play-F2.2-touch.md`](play-F2.2-touch.md). Только B=Vorticity (уже закрыт, не переснимать Fluid2D) vs C=MacCormackDye. `radiusUV=0.16`.

---

### Вне скоупа

Role C; MacCormack velocity; BFECC; правка Fluid2D / Harris / Vorticity; второй Poisson; ε/Jacobi; F0.5; MAC; GPU-порог; вернуть 5-точку φ0; 4 угла backUv в combine; выключить limiter. Если UAV-Load φ_f падает — стоп, числа, не `FieldReadC`. После смены limiter — снова только пик-гейт `(1.7,0)` + identity `u=0`; остальное зелёное не переигрывать.
