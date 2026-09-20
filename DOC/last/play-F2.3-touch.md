## Ручной тач F2.3 — production decision (скриптованный жест)

**Закрыто 2026-09-20.** Visual сдан; F2.3 в плане **Готово**. Production остаётся `Fluid2D.asset` (Project→Advect). Вердикт: [ADR-026 § F2.3](../ADR/ADR-026-F2-Small-Scale-Structure.md). Ниже — исторический протокол.

Visual **не** гейтит NUnit. Без отчёта F2.3 в плане не было «Готово». Смена `Fluid2D.asset` в этой сессии **не делалась**.

Не путать с закрытыми [`play-F2.0-touch.md`](play-F2.0-touch.md) / [`play-F2.1b-touch.md`](play-F2.1b-touch.md) / [`play-F2.2-touch.md`](play-F2.2-touch.md): там рука. Здесь **один скрипт** на четыре ассета.

**A** = `Fluid2D.asset` (не Create).  
**B** = `Fluid2D_HarrisOrder.asset` (не Create; без VC).  
**C** = `Fluid2D_Vorticity.asset` (не Create; `ε=1`, `m=2`).  
**D** = `Fluid2D_MacCormackDye.asset` (не Create; reverse на втором AdvectScalar).

`radiusUV=0.16` на всех четырёх. `SimulationSpeed=1`. GroundXZ.

Один World. Мышь в Play **не трогать**.

### Прогон (как планировали)

1. Stop. `Tools/M3D/Add F2.3 Scripted Stroke To Scene`. StartUV `(0.5, 0.25)`, EndUV `(0.5, 0.75)`, Duration `0.5`.
2. Assign A/B/C/D по очереди. Протокол писал **30 с**; парные кадры `_4` (оба квада) сняты на **10 с**. Пересъёмка 30 с не гейт: к 10 с силуэт уже гриб.
3. После сессии выключить `ScriptedTouchStroke`.

### Отчёт оператора (2026-09-20)

Жест скриптованный, симметрия/прорезь по центру. `radiusUV=0.16`. Кадры `_4` — **wait 10 с**, оба квада.

```
F2.3 visual
radiusUV: 0.16  SimulationSpeed: 1  Duration: 0.5  wait: 10 с (_4)
жест: ScriptedTouchStroke вертикаль (0.5,0.25)→(0.5,0.75)
A Fluid2D dye: клубы (симметричный гриб/сердце с ножкой; не хребет)
B Harris (без VC) vs A: клубы как A; нить не тоньше; хребет F1.8b не воспроизвёлся
C Vorticity vs B: макро-силуэт как B; интерьер на dye-only жилковатее (VC); рамка без торнадо F2.1
D MacCormack vs C: клубы как C; нить не тоньше (носитель жилок — VC, не схема dye)
ореолы / кольцо рамки (новое vs F2.1b): нет
Inf/шахматка/взрыв: нет
```

**Вердикт.** Look F2 (тонкая нить целиком) **не взят**. A/B/C/D — один макро-класс клубов. A vs B: порядок Harris на этом IC не дал хребет F1.8b (тот был от кругового мазка ~10 с, не от 0.5 с вертикали + жирный сид). C vs B: частичный эффект **VC** — внутренняя текстура жирного пятна, не филамент; на парных `_4` силуэт снова гриб. D vs C: MacCormack силуэт не сменил. Inf/шахматки/новых колец нет. `Fluid2D.asset` оставить. Мелкий `radiusUV` (0.04–0.08) не гейт: F2.0 уже видел, что 0.08 гаснет к 30 с; это авторский сид, не солвер. Скрипт валиден; ×3 не нужны.
