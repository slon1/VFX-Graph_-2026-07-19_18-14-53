## ТЗ для программиста — F2.3 (production decision)

**Закрыто 2026-09-20.** Хелпер + visual. Вердикт: production остаётся `Fluid2D.asset`; look F2 не взят; частичный эффект VC — интерьер жирного сида, не макро-нить. [ADR-026 § F2.3](../ADR/ADR-026-F2-Small-Scale-Structure.md). Это ТЗ не переоткрывать.

Решение по production, **не** новый солвер. Четыре уже существующих ассета, один скриптованный жест, visual — основной критерий. По [ADR-026 § F2.3](../ADR/ADR-026-F2-Small-Scale-Structure.md). Тач: [`play-F2.3-touch.md`](play-F2.3-touch.md).

Прочитать ADR-026 § F2.3 **до кода**. Без этого легко смешать порядок Harris с VC, крутануть Create или открыть ADR смены `Fluid2D.asset` до глаз.

Зафиксировано — не пересматривать:

1. **Новых кернелов / пассов fluid нет.** Не Role C, не MacCormack velocity, не BFECC, не второй Poisson, не ε/Jacobi, не MAC, не F0.5.
2. **`Fluid2D.asset` не менять** (ни порядок, ни `radiusUV`, ни Create). Смена production — **после** visual, отдельным ADR, не этот тикет.
3. **Create не вызывать** ни на одном из четырёх путей (`DeleteAsset` сотрёт look / factory `radiusUV=0.08`).
4. **Живой `radiusUV=0.16` на всех четырёх.** На git Harris = **0.08**; в грязной сессии на диске может быть **0.38**. Цель **0.16**, как F2.0–F2.2. `Load`+`SetDirty` без Create. **0.38 не оставлять.**
5. **Vorticity / MacCormackDye не переписывать.** `ε_vc=1`, `BorderMargin=2`, reverse на втором AdvectScalar — как закрыто.
6. **Жест — скриптованный**, не мышь. Тот же класс, что freeze F2: вертикаль через центр. Не точка в середине, не новый радиус кисти, не другой wait, не палитра, не `SimulationSpeed`.
7. **×3 одного скрипта не делать** (останется только джиттер `dt`). Один прогон на ассет.
8. **EditMode зелёный ≠ F2.3 закрыт.** Visual — [`play-F2.3-touch.md`](play-F2.3-touch.md). ≥2× по `max|D|` из ADR-024 **не** гейт. Цепочку D/KE F1.8b / F2.1 **не** перегонять как гейт.
9. **`SimulationSpeed` остаётся 1** на всех четырёх. Часы жеста — сумма `Time.deltaTime` **с первого `Sample`**, не с Play (Build съедает кадры до `built`).
10. Assign в сцену после EditMode не делать. Visual — оператор.

Референсы: `InputRouter.Sample` + `TouchForce`; формула world в `TouchInjectVelocity` (`FieldPasses.compute`); `SimulationWorld.Update` (уже зовёт Sample → `TouchBuffer`); Assign-меню Fluid2D / Harris / Vorticity / MacCormackDye.

Имена: компонент `ScriptedTouchStroke`; меню `Tools/M3D/Add F2.3 Scripted Stroke To Scene`. Не `Fluid2D_F2`, не Input System replay.

Код писать. Документацию из шага «доки» — тоже.

---

### Режимы (A/B/C/D)

Один R16-профиль, 128², Size 32, GroundXZ, Jacobi×40, DissipationRate=0, MaxFieldSpeed=20, velocity quad `colorScale=0.125`.

| | Ассет | Что изолирует |
| --- | --- | --- |
| **A** | `Fluid2D.asset` | production Project→Advect |
| **B** | `Fluid2D_HarrisOrder.asset` | Advect→Project, **без** VC |
| **C** | `Fluid2D_Vorticity.asset` | Harris + VC `m=2` |
| **D** | `Fluid2D_MacCormackDye.asset` | C + limited MacCormack dye |

C и D **не** сравнивать с A как «эффект VC»: в них уже порядок Harris. Вопрос «порядок vs VC» — только **B vs C**. Dye-схема — **C vs D**. Production vs Harris — **A vs B**.

---

### Шаг 1 — `ScriptedTouchStroke`

Runtime-компонент (Play), не Editor-only окно. На том же GO, что `InputRouter`.

Путь в **UV поля**, не луч камеры (иначе Game-view сдвигает жест):

```
world = FieldOrigin
      + FieldAxisU * ((uv.x - 0.5) * FieldSize.x)
      + FieldAxisV * ((uv.y - 0.5) * FieldSize.y)
```

Дословно как `TouchInjectVelocity`. Origin / AxisU / AxisV / Size — с **dye** (или velocity: у Fluid2D они совпадают) текущего `EffectAsset` на World. Не хардкодить Size=32 в мире отдельно от ассета.

Константы (сериализованные дефолты, не крутить в Play ради «точности»):

```
StartUV = (0.5, 0.25)
EndUV   = (0.5, 0.75)
Duration = 0.5   // секунды инжекта после первого Sample
```

Компонент отдаёт только **Position / Delta / count**. `Radius` / `Strength` пишет сам `InputRouter` (`touchRadius` / `touchStrength`) — поля на стрелке не дублировать.

Плоскость: дескриптор **dye**, иначе **velocity**, с `EffectAsset` на World. Не хардкодить Size=32.

Часы (блокер 1, принято):

- `t=0` на **первом** `Sample` после `built`. Дальше `t += Time.deltaTime`. Не `Time.time` с Play.
- Первый вызов: count=1, Position=start, **Delta=0**.
- Stop→Play: сброс в `OnEnable`.
- Кэш `GetComponent` в `OnEnable`, не каждый кадр.

Поведение по `t`:

- `0 < t < Duration`: count=1, Position = lerp(start, end, t/Duration), Delta = pos − prev.
- `t ≥ Duration`: count=0 до Stop. Мышь **игнорировать**, пока компонент **включён** (и во время жеста, и после). После visual оператор **выключает** компонент — иначе следующий Play снова скрипт.

Хук `InputRouter.Sample` (блокер 2, принято): **скриптованная ветка до проверки камеры**. `CollectPointers` не звать. Без Main Camera жест всё равно жив. Если скрипт выключен — нынешний путь (камера обязательна). `SimulationWorld.Update` не переписывать.

Меню `Tools/M3D/Add F2.3 Scripted Stroke To Scene` (блокер 3, принято): компонент на GO **роутера**; если `world.inputRouter == null` — прописать ссылку. Не Assign-эффект. Не Create Fluid2D. В логе/меню явно: после visual выключить `ScriptedTouchStroke`.

---

### Шаг 2 — Harris `radiusUV=0.16`

`AssetDatabase.LoadAssetAtPath` `Fluid2D_HarrisOrder.asset`, `SeedScalarDiskPass.RadiusUV = 0.16`, `SetDirty`, Save. **Не** `Create Fluid2D HarrisOrder Experiment`. Порядок пассов Harris не трогать. VC на этот ассет не ставить.

---

### Шаг 3 — тесты

`[TestFixture]` без `[Category("GPU")]` на классе драйвера. Fluid GPU не гонять.

**`ScriptedTouchStrokeTests`:** чистая функция UV→world + семпл по времени (можно package static/helper, чтобы не поднимать Play).

- Центр `(0.5,0.5)`, Origin=0, AxisU=right, AxisV=forward, Size=32 → world **0**.
- `(0.5, 0.25)` → `(0, 0, -8)`; `(0.5, 0.75)` → `(0, 0, +8)`.
- `t=0`: Delta=0, count=1, Position=start.
- `t=Duration/2`: Position на середине отрезка.
- `t≥Duration`: count=0.
- Две сетки `dt`: два вызова `Evaluate` с **одним и тем же `t`**, не симуляция кадров. Position совпадает.

**Пресеты (не GPU):**

- `Fluid2DPresetTests`: дописать `seed.RadiusUV == 0.16` и сообщение как у Harris («не Create — сотрёт 0.16»). Create Fluid2D из теста **нет**.
- `Fluid2DHarrisOrderPresetTests` (новый, образец Vorticity без VC): путь Harris, **нет** `VorticityConfinementPass`, одна стена, Advect velocity **до** Divergence, `radiusUV==0.16`, `SimulationSpeed==1`, quads velocity+dye. Сообщение «не Create — сотрёт 0.16». Create Harris в тесте **нет**.
- Живые Vorticity / MacCormackDye тесты не краснеют (`0.16`, ε=1, m=2).

Smoke `Rebuild()` четырёх ассетов не расширять составом Fluid2D. Если нет метода на Harris — один `Rebuild()` на Harris, как MacCormack smoke.

---

### Доки после зелёного EditMode

ADR-026 § F2.3: «хелпер готов», visual ещё открыт. Plan/status — **не** «Готово». Catalog / getting-started: одна строка про скриптованный жест F2.3, Harris на диске 0.16. `Fluid2D.asset` в catalog не переписывать.

---

### Visual

Оператор: [`play-F2.3-touch.md`](play-F2.3-touch.md). Программист Play не назначает и не снимает.

---

### Вне скоупа

Правка `Fluid2D.asset`; Create любого Fluid2D*; MacCormack velocity; Role C; ручной мазок как DoD; ×3 скрипта; шире диск / дольше 30 с / палитра / `dt` clamp; новый кернел; запись Input System; гейт ≥2×; перегон D/KE F1.8b как закрытие. Если visual скажет «брать Harris в production» — **стоп**, числа/скрины архитектору, не править `Fluid2D.asset` в этом тикете.
