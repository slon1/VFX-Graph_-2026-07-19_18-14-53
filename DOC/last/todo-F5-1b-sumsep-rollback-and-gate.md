# ТЗ F5.1b: вынести SeparationSum из ветки, закрыть гейт F5.1 на чистом дереве, закрепить тесты повтора

**Исполнитель:** Grok. **Ревью:** Claude и владелец.
**Куда положить:** `DOC/last/todo-F5-1b-sumsep-rollback-and-gate.md`.
**Основа:** ТЗ F5 редакция 2 (`DOC/last/todo-F5-accepted-cap-and-neighbors.md`, §4 и §7), [ADR-052](../ADR/ADR-052-Accepted-Neighbor-Cap.md), [ADR-053](../ADR/ADR-053-Sum-Separation.md). Общие правила §0 из `DOC/last/todo-adr-043-044-p6-fixes.md` действуют.
**Состояние (2026-10-23).** Код F5.1 (`c545586`) принят ревью. Гейт F5.1 не закрыт: EditMode шёл на грязном дереве (419 тестов, 7 упали), прогон «до» не снимался. Коммит `96164fe` (суммарное разделение, ADR-053) правит замороженные кернелы `walk 0`, `walk 1`, `walk 2` и ставит переключатель `SeparationSum != 0` внутрь цикла по соседям; F5 мерит цену именно этого цикла. Решение владельца: SeparationSum удачный эксперимент на будущее, сейчас не нужен, в рабочей ветке не остаётся, в эффектах выключен, документируется.
**Фазы и коммиты:** F5.1b-1 (откат, один коммит), F5.1b-2 (EditMode на чистом дереве, тесты, документы, один коммит). Каждая следующая фаза начинается после гейта предыдущей. F5.2 и F5.3 не начинать.

## 1. Зачем

После отката кернелы снова совпадают с базой, с которой снята известная точка «около 41 цикла» (`0290088`), и F5.3 сравнивает чистые сборки. Чистый прогон EditMode отвечает на вопрос, настоящие ли семь падений.

## 2. Что сохранить буквально

1. **История.** `96164fe` и `c545586` не переписывать: без `rebase`, `reset --hard` по общей истории и `push --force`. Откат это новый коммит (`git revert`). Иначе ветка `experiment/sum-separation` теряет опору.
2. **Код F5.1** (`ForceRepeatCount`, `DispatchForceRepeats`, скрэтч, `PerfArgs`, `FPSDisplay`, `Tools/s10`) в F5.1b-1 не менять.
3. **Несохранённые правки владельца в основном рабочем дереве** (в том числе `Boids_hash_2teams.asset`, `TeamProfile.cs` и всё, что покажет `git status`) не трогать и не терять. Запрещено в основном дереве: `git stash`, `git checkout -- .`, `git clean`, `git reset --hard`. Всю работу делать в **отдельном чистом worktree или клоне** (`git worktree add ../vfx-clean <коммит>`), владелец потом сделает `git pull`.
4. Значения по умолчанию, ассеты, сцены, пресеты (кроме удаляемого `Boids_hash_2teamsSumSep.asset`), `walk 2` и кернелы вне отката не менять.

## 3. Фаза F5.1b-1. Откат SeparationSum (один коммит)

1. **Сохранить эксперимент.** Создать ветку `git branch experiment/sum-separation 96164fe` и отправить: `git push origin experiment/sum-separation`. Проверить: `git ls-tree -r experiment/sum-separation --name-only | grep SumSep` показывает ассет, а `git rev-parse experiment/sum-separation` равен `96164fe…`. Без этого шага дальше не идти: ветка единственный носитель кода.
2. **Откат** в чистом worktree на `c545586` (или на текущей голове ветки, если после неё есть коммиты): `git revert --no-commit 96164fe`. Ожидаемый конфликт один, в `Assets/Scripts/Passes/BoidsPasses.cs` (блок рядом со свойствами `CountCapHits`): оставить **свойства `ForceRepeatCount` и `ForceScratch`**, убрать только `SumSeparation`. Остальные файлы откатываются без конфликтов: `BoidsPasses.compute`, `BoidNeighborForceTests.cs`, оба файла `Boids_hash_2teamsSumSep.asset(.meta)`.
3. **ADR-053 сохранить.** Откат его удаляет: восстановить `git checkout 96164fe -- DOC/ADR/ADR-053-Sum-Separation.md` и внести правки:
   - строку статуса заменить на:

     ```
     **Статус:** отложен (2026-10-23). Эксперимент удачный, в P7 не используется. Код снят с `feature/reynolds-boids` и хранится в ветке `experiment/sum-separation` (коммит `96164fe`). Исходная запись: по наблюдению владельца 2026-10-22.
     ```
   - в конец файла добавить три раздела:

     ```
     ## 4. Где код

     Ветка `experiment/sum-separation`, коммит `96164fe`:

     - ассет `Assets/Effects/Boids_hash_2teamsSumSep.asset` (параметры наблюдения из §3, `maxNeighbors` 0, Sum Separation включена);
     - поле `sumSeparation` и свойство `SumSeparation` на `BoidNeighborForcePass`, uniform `SeparationSum` каждый кадр;
     - помощники `AccumulateSep` и `SeparationTerm` в `BoidsPasses.compute` и их вызовы в `BoidNeighborForce` (walk 0), `AccumulateSortedRange` (walk 1) и `AccumulatePackedRange` (walk 2);
     - тест `SumSeparation_SumsWithWiderEpsilon_DefaultStaysAverage` в `BoidNeighborForceTests.cs` (только walk 0).

     В `feature/reynolds-boids` этого кода нет: кернелы и существующие тесты возвращены к состоянию до эксперимента, пресеты и сцены поля не содержали.

     ## 5. Почему снят с ветки

     При выключенной галке формулы совпадают со старым кодом, но переключатель `SeparationSum != 0` стоит внутри цикла по соседям всех трёх обходов. Его цена в циклах и регистрах не измерена. Фаза F5 мерит цену именно этого цикла, а известная точка около 41 цикла снята без переключателя, поэтому сравнение после него было бы смешано. Тест суммы шёл только на walk 0. Для 200k эксперимент сейчас не нужен.

     ## 6. Как вернуться

     1. Перенести код из `experiment/sum-separation`. Ветка отходит от коммита до F5.1, поэтому `BoidsPasses.cs` переносится вручную: рядом лежит код `forcerep`.
     2. Вносить как отдельный вариант кернела (define или дубликат, выбор в `SelectKernel`, по образцу дубликатов F3a и F3b), а не как uniform в цикле: выключенный вариант тогда не содержит ветки.
     3. Тест суммы на walk 1 и walk 2, затем захват Streamline выключенного и включённого вариантов.
     4. Учесть §3: эффект держится при `maxNeighbors = 0`, то есть при полном обходе, а на 200k он недопустимо дорог. Изучать вместе с вопросом, как получить такое же удержание стаи при ограниченном обходе (кап по принятым в ADR-052 отложен, а сумма при положительном капе рвёт стаю вдоль решётки).
     ```
4. `DOC/last/Techdebt.md`: после пункта `10x` добавить `10z. **Сумма separation (ADR-053) снята с ветки.** Эксперимент удачный, нужен позже. Код в ветке `experiment/sum-separation` (`96164fe`), порядок возврата в ADR-053 §6.`
5. Коммит одним сообщением: `Move the summed-separation experiment out of the main branch so the force kernels match the pre-F5 baseline.` (тело: переключатель стоял внутри цикла трёх обходов, F5 мерит этот цикл; эксперимент в `experiment/sum-separation`; `forcerep` не тронут).

**Гейт F5.1b-1** (вывод каждой команды приложить в отчёт):

```
git diff 0290088 HEAD -- Assets/Shaders/GPU/Passes/BoidsPasses.compute           # пусто
git diff 0290088 HEAD -- Assets/Tests/Editor/BoidNeighborForceTests.cs           # пусто
git diff --numstat c545586 HEAD -- Assets/Scripts/Passes/BoidsPasses.cs          # 0  15
git grep -nE "SumSeparation|SeparationSum|sumSeparation|AccumulateSep|SeparationTerm" -- Assets   # пусто
git grep -n "2b2aa8c491554e64d87a1eb984ea8870" -- Assets ProjectSettings Packages  # пусто (GUID удалённого ассета)
git ls-tree -r experiment/sum-separation --name-only | grep SumSep               # ассет на месте
```

Результат `0  15` значит, что в `BoidsPasses.cs` только 15 удалённых строк эксперимента и ни одной добавленной или изменённой, то есть код `forcerep` цел. Любое расхождение: остановиться и приложить вывод.

## 4. Фаза F5.1b-2. EditMode на чистом дереве, тесты, документы (один коммит)

### 4.1. Прогон EditMode

Прогон **полный**, на чистых worktree (без чужих правок), тем же способом, что в F5.1. Для каждой строки записать: всего, прошло, упало, пропущено (`Assume`), имена упавших и их сообщения.

| Коммит | Смысл |
|---|---|
| `b9a70fb` | «до»: после F5.0b, без `forcerep` и без SeparationSum |
| голова ветки после F5.1b-1 | «после»: F5.1 без SeparationSum |

Если на `b9a70fb` что-то падает, прогнать ещё `0290088` и для каждого упавшего теста записать, падал ли он там. **Упавшие тесты не чинить и не править**: только описать и остановиться. Если «после» падает то, что не падает «до», это регрессия F5.1: остановиться, приложить сообщения.

### 4.2. Закрепление тестов повтора

Только файл `Assets/Tests/Editor/BoidNeighborForceRepeatTests.cs` (свой, из F5.1; существующие тесты не править):

1. В `AssertRepeatMatches` после первого прогона проверить `Assert.AreEqual(walkMode, prepared.Force.EffectiveWalkMode, "walk " + walkMode)`. Иначе тест walk 2 не заметит молчаливого отката на walk 1.
2. **Обнуление скрэтча каждый кадр.** Новый тест: `Rerun(prepared, 4)`, прочитать `ForceScratch` (значения S1); затолкнуть в скрэтч ненулевые значения (`SetData` с 7f), снова `Rerun(prepared, 4)`, прочитать S2. `AssertBits(S2, S1)` доказывает, что скрэтч обнуляется перед повторами, а не накапливается.
3. **Смена `particleCount` без `Initialize`.** Существующий тест проходит через `Initialize` (`ReleaseScratch`), а не через ветку пересоздания в `EnsureScratch`. Добавить тест, где тот же пасс выполняется с другим числом частиц без повторного `Initialize`, и проверить новое `ForceScratch.count` и `AreNotSame`. Если в харнессе это невозможно, записать причину в отчёт, ветку не выдумывать.
4. (Необязательно.) Строку `args += ",forcerep="` в `LogDeviceInfo` вынести в `internal static` помощник по образцу `ForceRepLogField` и покрыть тестом. Если правка выходит больше пяти строк в `FPSDisplay.cs`, пропустить и записать в отчёт.

### 4.3. Документы

- `DOC/ADR/ADR-052-Accepted-Neighbor-Cap.md` §6: добавить блок **F5.1b** (ветка `experiment/sum-separation`, коммит отката, таблица §4.1 с числами, результат пункта 4.2, отклонения). В блоке F5.1 строку «`git diff` по `BoidsPasses.compute` пуст» уточнить: «в коммите F5.1; после отката пуст на всей ветке относительно `0290088`». Отклонение «полный EditMode не зелёный из-за чужого грязного дерева» заменить фактом из §4.1.
- `DOC/status.md`: одна строка: SeparationSum вынесен в `experiment/sum-separation`, ADR-053 отложен.

**Гейт F5.1b-2:** таблица §4.1 заполнена числами; каждое упавшее имя объяснено («падает и на `b9a70fb`/`0290088`» или регрессия); новые тесты §4.2 проходят; все тесты `BoidNeighborForceRepeatTests` и `PerfArgsTests` зелёные; остановка.

## 5. Не делать

Не править кернелы и `NeighborWalk` (после отката они совпадают с `0290088`), не писать кернел `Accepted`, не менять значения по умолчанию, не менять `forcerep`, кроме тестов §4.2, и не править существующие тесты. Не удалять ADR-053 и ветку `experiment/sum-separation`. Не переписывать историю (§2.1). Не чинить упавшие чужие тесты. Не менять `EditorBuildSettings.asset`, корневой `.gitignore`, `TeamProfile.cs` и другие чужие несохранённые файлы. Не собирать APK. Не начинать F5.2 и F5.3.

## 6. Гейты

- **F5.1b-1:** все проверки §3 дали указанный результат, ветка `experiment/sum-separation` отправлена; остановка.
- **F5.1b-2:** условия §4.3; остановка. После него владелец решает, закрыт ли F5.1 и можно ли начинать F5.2.
