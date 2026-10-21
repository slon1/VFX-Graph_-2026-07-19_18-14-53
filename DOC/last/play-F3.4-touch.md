## Ручной тач F3.4 — VC поверх DyeInject

**Принято владельцем 2026-10-21.** EditMode зелёный при `ε=1`. На пресете `EpsilonVc = 25`. Production остаётся `Fluid2D.asset`. [ADR-051](../ADR/ADR-051-Vorticity-On-Dye-Injection.md).

Картинка:

- появляются завитки вдоль нити;
- нить распадается на клубы / жилки;
- нет кольца торнадо у рамки;
- вихревая структура после жеста зависит от `DissipationRate`.

Visual **не** гейтит NUnit. «Готово» в плане ставит владелец после этого протокола.

Активная сцена должна содержать `SimulationWorld`. Меню `Assign Fluid2D DyeInjectVC Experiment To Scene` пишет эффект только в неё. Существующие `Fluid2D*.asset` не Create.

- **A** = `Fluid2D_DyeInject.asset` — принятая база F3.1.
- **B** = `Fluid2D_DyeInjectVC.asset`, `EpsilonVc = 25`, `BorderMargin = 2`, `DissipationRate = 0`.
- **B-ε** = B с `EpsilonVc` 0.5, затем 2.
- **B-fade** = B с `AdvectScalar.DissipationRate` 0.15.

`SimulationSpeed=1`. GroundXZ. Мышь в Play **не трогать**.

После B-ε и B-fade вернуть `EpsilonVc = 25` и `DissipationRate = 0` и не сохранять ассет с крутилкой.

### Прогон

1. Stop. `ScriptedTouchStroke` с F2.3: StartUV `(0.5, 0.25)`, EndUV `(0.5, 0.75)`, Duration `0.5`.
2. Назначить **A**. Play → кадр. Stop.
3. `Tools/M3D/Assign Fluid2D DyeInjectVC Experiment To Scene`. Это **B**. Play → те же кадры. Stop.
4. **B-ε:** `Vorticity Confinement → Epsilon Vc` сначала `0.5`, Play → кадр, Stop; затем `2`, Play → кадр, Stop. Вернуть `25`.
5. **B-fade:** `Advect Scalar → Dissipation Rate` = `0.15`, Play → кадр, Stop. Вернуть `0`.
6. Выключить `ScriptedTouchStroke`.

Смотреть:

1. Появляются ли завитки вдоль нити, а не просто утолщение или шум.
2. Нить остаётся непрерывной или распадается на клубы / жилки.
3. Нет ли кольца торнадо у рамки.
4. Как долго живёт вихревая структура после жеста.

Пустой кадр (нет завитков) — находка с тремя кандидатами, следующий тикет заранее не выбран:

1. velocity слабая или быстро затухает (Techdebt 5);
2. проекция съела силу VC;
3. вихрь на velocity 128² не доезжает до нити 512².

Если `max|ω|` у V1 почти равен V0, это кандидаты 1 или 2. Если вырос, а завитков нет, это кандидат 3. F3.2 здесь ни при чём: это стенсиль рамки, не интерьер.
