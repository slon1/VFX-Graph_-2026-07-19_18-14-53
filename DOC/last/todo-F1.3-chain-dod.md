## ТЗ — F1.3 follow-up: `fix: chain DoD k=8 ≥3×`

Узкий патч теста. Пасс `SubtractPhiGradientPass` **принят**, 3.1–3.5 зелёные. Follow-up k=12 исчерпан ([`todo-F1.3-harmonic-k.md`](todo-F1.3-harmonic-k.md)): 2.49×, хуже k=8. DoD цепочки переписан в [ADR-020 §3](../ADR/ADR-020-Subtract-Phi-Gradient-Pass.md) — читать errata 1–2 целиком, не пересказывать.

### Не трогать

- `FluidPasses.cs`, `FluidPasses.compute`, любой production-код.
- Тесты 3.1–3.5.
- `Iterations` / `RepeatCount` Jacobi (остаётся 40).
- k. Не 12, не 10, не 1. Рабочее значение — **8**.
- Порог. Не возвращать `/10`. Не ставить `maxAfter < maxBefore` без множителя. Не добавлять второй тест на k=12.
- ADR-020 / ADR-016 / ADR-014 / ADR-018 / Techdebt 8e/8f / plan / исходный `todo-F1.3.md` — **уже обновлены**. Не дублировать errata.

### Сделать

1. `Assets/Tests/Editor/SubtractPhiGradientPassTests.cs`
   - `HarmonicK`: `12` → `8`.
   - Имя теста 3.6: `ProjectionChain_HarmonicK12_ReducesInteriorMaxAbsDivergenceByOrder` → `ProjectionChain_HarmonicK8_ReducesInteriorMaxAbsDivergence` (убрать `ByOrder` — порог больше не «на порядок»).
   - Assert: `maxAfter < maxBefore / 10f` → `maxAfter < maxBefore / 3f`. Сообщение assert — тот же `report`, что уже печатается.
   - Лог 3.6: `k=8` и те же величины (`meanD`, `|mean|/max`, `maxBefore`, `maxAfter`, `ratio`).
   - Гейт `|mean|/max < 0.1`, mean по всем текселям, max по интерьеру, сид, геометрия 64² / Size=32, `PlanePosition`, форматы, порядок Div → Jacobi×40 → Subtract → Div — без изменений.

2. Прогон: `SubtractPhiGradientPassTests` целиком, затем EditMode (регрессия Divergence/Jacobi).

3. Документация **только если 3.6 зелёный:**
   - `DOC/plan-stable-fluid.md`: F1.3 → **Готово**; в сути — k=8, ≥3×, ссылка на ADR-020 §3. Не оставлять «ждать зелёный тест».
   - `DOC/status.md`: заголовок F1.3 — готово; в текст цепочки — фактические числа этого прогона (пять величин) и k=8. Исторические 4.46× / 2.49× можно оставить одной фразой.
   - ADR-020 шапка: статус «Принято (реализовано)», убрать «ждать зелёный тест».

### Если 3.6 всё ещё красный

Стоп. Не менять k, iterations, порог. В отчёт — пять чисел. Это снова архитектура: 3× на k=8 при прошлом замере 4.46× не должен быть красным.

### Отчёт

1. Diff: только тест 3.6 (+ rename метода) и, при зелёном 3.6, plan + status + шапка ADR-020.
2. Подтверждение: `FluidPasses.cs` / `.compute` в diff нет.
3. Числа 3.6 на k=8 (ожидание ratio ≈ 4.5, assert ≥3×).
4. 3.1–3.5 зелёные. Сьют без новых красных.
