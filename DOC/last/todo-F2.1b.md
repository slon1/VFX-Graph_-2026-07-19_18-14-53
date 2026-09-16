## ТЗ для программиста — F2.1b (маска силы VC, 2 текселя)

**Закрыто 2026-09-16.** EditMode + visual. Вердикт: торнадо рамки = clamp-curl, `m=2` их сняла; look интерьера не взят. [ADR-027 § F2.1b](../ADR/ADR-027-Vorticity-Confinement-Pass.md). Это ТЗ не переоткрывать.

Диагностика к закрытому F2.1, не новый солвер. A vs Fluid2D **не переигрывать**.

Зачем: visual F2.1 — энергия на рамке. Не смешивать «8g» и clamp-curl. Вопрос: если не прикладывать силу там, где стенсиль `∇|ω|` достаёт до `Load`+clamp, останется ли look в **интерьере**?

Зафиксировано:

1. **`Fluid2D.asset` / HarrisOrder не трогать.** Не Create Fluid2D / Harris.
2. **Формула ω / N / f как F2.1.** Не `/2h`, не маскировать чтение ω для интерьера.
3. Маска **только силы**, ширина **2** (не 1: сила в `x=1` всё ещё видит `ω` рамки).
4. `BorderMargin=0` — bitwise как кернел F2.1 (гейт). Дефолт класса **0**; `[Min(0)]` (отрицательный `m` ломает `x >= N-m`). На живом ассете и в Create — **2**.
5. `ε_vc=1` не крутить. Jacobi не поднимать. Role C / scratch / F2.2 — нет.
6. EditMode зелёный ≠ закрыто. Visual — [`play-F2.1b-touch.md`](play-F2.1b-touch.md).
7. **`radiusUV` не трогать.** Create по-прежнему 0.08. Живой `Fluid2D_Vorticity.asset` — **0.16** (visual F2.1). **Create не вызывать** (`DeleteAsset` сотрёт 0.16). `BorderMargin=2` дописать на существующий YAML / `Load`+`SetDirty` без Delete.
8. Assign в сцену после EditMode не делать. SetInt на `FieldKernelPass` уже есть.

### Кернел (после early-out `EpsilonVc==0`)

`int BorderMargin;` из `SimShaderIds` + `SetInt`. Если `p` в рамке ширины `BorderMargin` → `FieldWrite[p]=u; return;` (как ε=0). Иначе тело F2.1 без правок.

```
p.x < m || p.y < m || p.x >= FieldResolution.x - m || p.y >= FieldResolution.y - m
```

`m=0`: условие на валидном id не срабатывает.

### Тесты (дописать `VorticityConfinementPassTests`)

Геометрия кольца: **64² / Size=64 / R32**, как identity F2.1 (bitwise без half). Существующий **3.3 не трогать** (регрессия «маска не всегда включена»).

- `BorderMargin=0`, ε=0: старый identity не краснеет.
- `BorderMargin=2`, ε=1, сид **с curl** (TG/шум, не uniform): после одного VC
  - кольцо 2 текселя **bitwise** сид (сила не писалась);
  - интерьер **вне кольца** `max|Δu| > 0` (иначе «всегда `FieldWrite=u`» зелёный).
- Inf/NaN — красный.
- Preset на **живом** ассете: `BorderMargin==2`, `EpsilonVc==1`, **`radiusUV==0.16`**. Не требовать 0.08 — Create-дефолт, его не гоняли. Create в коде пишет `BorderMargin=2` и по-прежнему `radiusUV=0.08` (factory; если кто вызовет Create, пресет покраснеет — сигнал «стёрли look»).

Цепочку D/KE/odd-even F2.1 не перегонять как гейт.

### Visual

Только B с маской. Fluid2D заново не снимать. Сравнивать с уже закрытым [`play-F2.1-touch.md`](play-F2.1-touch.md).

### Доки после зелёного EditMode

Сделано: ADR-027 § F2.1b + visual; plan/status **Готово**. Preset/catalog: `borderMargin`.

### Вне скоупа

Маска чтения ω; второй Poisson; ε/Jacobi; production; F2.2; Harris без VC (F2.3). Если торнадо уедет на 3-й тексель — **стоп**, числа/скрины, не расширять маску молча.
