## Ручной тач F3.0 — cross-resolution dye (скриптованный жест)

**Закрыто 2026-09-20.** Visual сдан; F3.0 в плане **Готово**. Production остаётся `Fluid2D.asset` (Project→Advect, dye 128²). Вердикт: [ADR-029](../ADR/ADR-029-Cross-Resolution-Dye.md). Ниже — исторический протокол.

Visual **не** гейтит NUnit. Без отчёта F3.0 в плане было только «EditMode готово». Смена `Fluid2D.asset` в этой сессии **не делалась**.

Не путать с [`play-F2.0-touch.md`](play-F2.0-touch.md) (только production, рука) и [`play-F2.3-touch.md`](play-F2.3-touch.md) (четыре ассета A/B/C/D). Здесь **два** ассета, тот же скрипт F2.3.

**A** = `Fluid2D.asset` (не Create; dye 128²).  
**B** = `Fluid2D_HighResDye.asset` (не Create; dye 512², velocity 128², Size 32, без VC/MacCormack).

`radiusUV=0.16` на обоих. `SimulationSpeed=1`. GroundXZ. Мышь в Play **не трогать**.

### Прогон

1. Stop. `ScriptedTouchStroke` с F2.3: StartUV `(0.5, 0.25)`, EndUV `(0.5, 0.75)`, Duration `0.5`.
2. `Tools/M3D/Assign Fluid2D To Scene`. Play → кадр (оба квада). Stop.
3. `Tools/M3D/Assign Fluid2D HighResDye Experiment To Scene`. Тот же Play → те же кадры. Stop.
4. После сессии выключить `ScriptedTouchStroke`.

Create на Fluid2D / Harris / Vorticity / MacCormackDye **не** запускать.

### Отчёт оператора (2026-09-20)

Жест скриптованный, тот же IC, что F2.3. `radiusUV=0.16`. Парные кадры A/B (velocity слева, dye справа). Wait явно не фиксировали — макро уже зрелый гриб. Оператор: «результат безусловно более четкий».

```
F3.0 visual
radiusUV: 0.16  SimulationSpeed: 1  Duration: 0.5
жест: ScriptedTouchStroke вертикаль (0.5,0.25)→(0.5,0.75)
A Fluid2D dye: клубы (разрезной гриб/сердце с ножкой; край мягкий)
B HighResDye vs A: тот же макро-силуэт; край шляпки резче; прорезь нож; ножка — тонкая светлая нить (на 128² её съедал bilinear); нижние пятна плотнее
Inf/шахматка/взрыв: нет
```

**Вердикт.** Гипотеза F3.0 (dye выше разрешением меньше диссипирует при том же переносе) **взята визуально**. B острее A; топология velocity не сменилась — клубы на месте. Нить ножки — выживший след жеста, не look F2 «схема режет пятно в хребет». `Fluid2D.asset` оставить (dye 128²). `Fluid2D_HighResDye.asset` — эксперимент, не silent promote. F3.1 (краска вдоль курсора) разблокирован: тонкий след на 512² живёт. F3.3 из этих кадров не открывать.
