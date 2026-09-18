## Ручной тач F2.2 — limited MacCormack dye

**Закрыто 2026-09-18.** Visual сдан; F2.2 в плане **Готово**. Вердикт: [ADR-028](../ADR/ADR-028-Limited-MacCormack-Dye.md). Ниже — исторический протокол.

Visual **не** гейтит NUnit. Без отчёта F2.2 в плане не было «Готово».

Не переснимать Fluid2D. База bilinear dye — закрытый [`play-F2.1b-touch.md`](play-F2.1b-touch.md) / [`play-F2.1-touch.md`](play-F2.1-touch.md): Vorticity после маски ≈ клубы, velocity почти мёртв.

**B** = `Fluid2D_Vorticity.asset` (не Create). **C** = `Fluid2D_MacCormackDye.asset`. `radiusUV=0.16` на обоих, `ε_vc=1`, `BorderMargin=2`. EffectAsset пишется с инспектора сразу. Harris / Fluid2D не назначать. Create Vorticity не вызывать.

Один World. Play выключен, пока агент гоняет GPU.

### Прогон

1. Stop. `Tools/M3D/Assign Fluid2D Vorticity Experiment To Scene`. GroundXZ. Проверить 0.16 / ε=1 / m=2. Play. Вертикальный мазок через центр, **30 с**. Оба квада. ×3. (Если кадры F2.1b ещё валидны — можно не снимать B заново, явно написать «B = F2.1b».)
2. Stop. `Tools/M3D/Assign Fluid2D MacCormackDye Experiment To Scene`. Проверить 0.16, reverse на втором AdvectScalar, scratch не на кваде. Play. Тот же жест, 30 с. ×3.
3. Stop. В чат:

```
F2.2 visual
radiusUV: 0.16  ε_vc: 1  BorderMargin: 2
B Vorticity dye: клубы как F2.1b / иначе
C MacCormack dye vs B: нить тоньше? держится дольше? клубы как были?
ореолы / яркое кольцо рамки vs B: нет / да
Inf/шахматка/взрыв: нет / да
```

Решение: C заметно острее/тоньше B, без новых колец → схема помогает look (всё ещё не production). Клубы как B → MacCormack на Stam-клубах мало виден; тикет по коду можно закрывать, look F2 не взят. Новые кольца / Inf / шахматка интерьера — стоп, не BFECC и не velocity-MacCormack в этом тикете.

### Отчёт оператора (2026-09-18)

`radiusUV=0.16`, `ε_vc=1`, `BorderMargin=2`. B и C, ×3 × 30 с, оба квада. Столбцы — независимые жесты, не парный A/B одного мазка.

```
F2.2 visual
radiusUV: 0.16  ε_vc: 1  BorderMargin: 2
B Vorticity dye: клубы как F2.1b
C MacCormack dye vs B: клубы как были (тело того же класса); нить целиком не тоньше и не держится дольше
ореолы / яркое кольцо рамки vs B: нет
Inf/шахматка/взрыв: нет
```

**Вердикт.** Look F2 (тонкая нить целиком) **не взят**. Тело dye на C — клубы/грибы того же класса, что B и закрытый F2.1b. Velocity в обоих рядах почти мёртв (база маски, не артефакт MacCormack). Новых колец рамки, Inf, шахматки нет. EditMode пик ~+1.7% на off-grid гауссе; на Stam-клубах это не другой look — протокольная ветка «клубы как B». Dye — пассивный tracer: силуэт задаёт velocity (Stam+VC+маска), схема скаляра не разворачивает толстый вихрь в филамент. Протокол ×3 × 30 с не крутить (шире диск / дольше ждать / палитра / `dt`) — это не снимает потолок скорости. Скриптованный импульс — метод F2.3, не пересъёмка F2.2. Production не менять. F2.3: Harris **без** VC.
