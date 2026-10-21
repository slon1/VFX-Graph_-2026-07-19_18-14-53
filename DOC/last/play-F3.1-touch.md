## Ручной тач F3.1 — продолжительный впрыск dye

**Принято владельцем 2026-10-21.** EditMode зелёный. Production остаётся `Fluid2D.asset`. Вердикт: [ADR-050](../ADR/ADR-050-Continuous-Dye-Injection.md).

Картинка:

- нить читается по всему пути жеста;
- пунктира и шахматки нет;
- нить не статичная.

Visual **не** гейтит NUnit. «Готово» в плане ставит владелец после этого протокола.

Активная сцена должна содержать `SimulationWorld`. Меню `Assign Fluid2D DyeInject Experiment To Scene` пишет эффект только в неё. `Fluid2D.asset`, Harris, Vorticity, MacCormackDye и `Fluid2D_HighResDye` не Create.

- **A** = `Fluid2D_HighResDye.asset` — опорный кадр F3.0, сид `RadiusUV=0.16`.
- **B** = `Fluid2D_DyeInject.asset`, `AdvectScalar.DissipationRate = 0`, сид `RadiusUV=0.04`, `Value = 1`.
- **B0** = B с `SeedScalarDisk.Value = 0` (штрих без сида).
- **B-fade** = B с `DissipationRate` 0.15, затем 0.5.
- **B-rate** (опционально) = B с `injectRate` 20 и 200.

`SimulationSpeed=1`. GroundXZ. Мышь в Play **не трогать**.

`t½ = ln 2 / λ`. Через 10 с остаётся `exp(−10λ)`.

| λ, 1/с | t½, с | через 10 с |
| --- | --- | --- |
| 0 | ∞ | 100 % |
| 0.07 | ≈ 10 | 50 % |
| 0.15 | ≈ 4.6 | 22 % |
| 0.3 | ≈ 2.3 | 5 % |
| 0.5 | ≈ 1.4 | 0.7 % |
| 1.0 | ≈ 0.69 | 0.005 % |

### Прогон

1. Stop. `ScriptedTouchStroke` с F2.3: StartUV `(0.5, 0.25)`, EndUV `(0.5, 0.75)`, Duration `0.5`.
2. Назначить **A**. Play → кадр (velocity и dye). Stop.
3. `Tools/M3D/Assign Fluid2D DyeInject Experiment To Scene`. Это **B**. Play → те же кадры. Stop.
4. В инспекторе эффекта выставить `Seed Scalar Disk → Value = 0`. Это **B0**. Play → кадр. Stop. Вернуть `Value = 1`.
5. **B-fade:** `Advect Scalar → Dissipation Rate` сначала `0.15`, Play → кадр, Stop; затем `0.5`, Play → кадр, Stop. Вернуть `0`.
6. **B-rate**, если нужен: `Touch Inject Scalar Field → Inject Rate` `20`, затем `200`. После прогона вернуть `60`.
7. Выключить `ScriptedTouchStroke`.

Смотреть: нить на всём пути жеста, а не только ножка; пунктир; голова штриха; где штрих бледнее из-за скорости; когда нить гаснет при `DissipationRate > 0`; Inf и шахматка.
