# ТЗ S1: нейтральный коммит ассетов и пружинный пасс `SwarmSpringForce` (ADR-055)

**Исполнитель:** Grok (код, тесты, документы). **Ревью:** Claude. Play и вид не проверяются, устройство не нужно.
**Куда положить:** `DOC/last/todo-adr-055-swarm-spring.md`.
**Основа:** [ADR-055](../ADR/ADR-055-Swarm-Spring-Force.md) и [ADR-049](../ADR/ADR-049-Swarm-Point-Forces.md). Где это ТЗ короче ADR, делать по ADR. Если ТЗ и ADR расходятся или код противоречит им, остановиться и спросить владельца.
**Коммиты, в таком порядке:** (N) нейтральный коммит ассетов; (0) ADR-055 и это ТЗ; (S1) код и тесты; (S2) документы. В S1 и S2 EditMode не хуже, чем после N, и красных ровно четыре из части 1.

## Часть 0. Git

1. Работа идёт на `feature/classic-boids`. Показать `git status` и хеш головы (ожидается `a2dce99f25aacf33bda9fd3809bf1479dbc4426c` или потомок, если владелец успел что-то добавить; записать в сдачу).
2. Коммитить только пути из этого ТЗ, явно, включая парные `.meta` новых файлов. Запрещены `git add -A`, `git add .`, `git commit -a`, `git stash`, `git reset`, `git restore` и `git checkout` на файлах вне ТЗ. Перед каждым коммитом показать `git status` и `git diff --cached --stat`.
3. Без `merge`, `rebase`, `push`. Публикует ветку владелец.

## Часть 1. Нейтральный коммит ассетов (N)

Владелец решил сохранить свои незакоммиченные правки пресетов отдельным нейтральным коммитом, не связанным с P9.

1. Показать `git status`. Ожидается: изменены `Assets/Effects/Boids_hash_2teams.asset`, `Assets/Effects/Fluid2D_DyeInject.asset`, `Assets/Effects/Fluid2D_DyeInjectVC.asset` (и их `.meta`, если тоже изменены), не отслеживается `DOC/MEASURE/nabuild-na/`. Показать `git diff --stat` по трём ассетам. Если в рабочем дереве есть что-то ещё, перечислить и не добавлять без ответа владельца.
2. Закоммитить **только эти три ассета** (и `.meta`, если они изменены), содержимое не менять. Сообщение в стиле владельца: `Record the leftover preset edits so builds from this branch report dirty=0.` `DOC/MEASURE/nabuild-na/` не добавлять.
3. Сам коммит красные тесты не исправит: файлы те же, меняется только то, что они записаны в историю. Прогнать EditMode и записать число «после N» и список красных. Ожидается десять красных (шесть `SwarmPointForceTests` и четыре теста пресетов).
4. **Диагноз четырёх тестов пресетов (ничего не чинить):** `BoidsHashTwoTeamsPresetTests.Chain_ThreeHundredFrames_StaysFiniteInsideBox`, `BoidsHashTwoTeamsPresetTests.Preset_MatchesTwoTeams`, `Fluid2DDyeInjectPresetTests.Preset_MatchesDyeInjectComposition`, `Fluid2DDyeInjectVcPresetTests.Preset_MatchesDyeInjectVcComposition`. Для каждого в сдаче: текст падения, что сравнивает тест (ассет, фабрику `M3DDemoTools`, константы) и где расхождение: в ассете, в фабрике или в ожидании теста. Подсказка по двум `BoidsHashTwoTeams`: коммит `521a98b` поменял их ожидания (радиусы 9, `InterGroupSeparationMultiplier` 2, веса 0,5 / 3 / 2 и 0,75 / 6 / 1 для двух команд, `MinCellSize` 9, поле 10 × 10). Сверить с этим закоммиченным ассетом и с фабрикой `CreateBoidsHashTwoTeamsAsset`. Результат это таблица «тест, причина, чьё расхождение» без правок.

## Часть 2. Разведка (ответом в сдаче, до кода)

1. `SimulationWorld` (строки около 104, 279, 759) строит буфер `SwarmPoints` и `SwarmPointCount` из `effect.SwarmPoints` независимо от типа пасса. Подтвердить. Перечислить все места, где `SwarmPointForcePass` упоминается по типу (известны: `SwarmPointValidator`, `SwarmPointForceTests`, два ассета `Boids_hash_points.asset`, `Boids_hash200k_points.asset`). Если нужно научить новый пасс ещё чему-то, остановиться и перечислить до правки.
2. Подтвердить, что `Assets/Tests/Editor/SwarmPointForceTests.cs` не менялся после `521a98b` (`git log --oneline -- Assets/Tests/Editor/SwarmPointForceTests.cs`).
3. Подтвердить, что ядро `SwarmPointForce` в `BoidsPasses.compute` содержит `f += (d / dist) * (a.z * prof);`, и что `SwarmPoints`, `SwarmPointCount`, `MinImage` видны в конце файла.
4. Записать число EditMode «до кода» (после коммита N).

## Часть 3. Реализация (коммит S1)

**Б1. Ядро.** В `BoidsPasses.compute`: строка `#pragma kernel SwarmSpringForce` рядом с остальными и блок **в конце файла** (после `TeamClassicSteer`). Ядро это копия действующего `SwarmPointForce` с HEAD, в которой меняется **одна строка**. Для ориентира (если действующий текст отличается, брать действующий):

```hlsl
[numthreads(THREADS, 1, 1)]
void SwarmSpringForce(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount) return;
    float2 x = position[id.x].xz;
    float2 f = 0;
    [loop] for (uint k = 0; k < SwarmPointCount; k++)
    {
        float4 a = SwarmPoints[2 * k];
        float4 b = SwarmPoints[2 * k + 1];
        float2 d = MinImage(a.xy - x);
        float dist = length(d);
        if (dist <= 1e-5) continue;
        float prof = 1;
        if (a.w > 0)
            prof = (b.x > 0) ? 1 - smoothstep(a.w * (1 - b.x), a.w, dist) : (dist < a.w ? 1 : 0);
        f += d * (a.z * prof);        // единственное отличие от SwarmPointForce
    }
    velocity[id.x] += float3(f.x, 0, f.y);
}
```

Больше ничего в файле не менять. Текст `SwarmPointForce` и остальных ядер, структуры и `#pragma` других ядер не затрагиваются.

**Б2. Пасс.** `SwarmSpringForcePass : ParticleKernelPass` в `BoidsPasses.cs` после `SwarmPointForcePass`, `[Serializable]`. Копия `SwarmPointForcePass` с `DisplayName` «Swarm Spring Force», `KernelName` «SwarmSpringForce», `Category` `PassCategory.Force`, теми же `Reads`, `Writes`, `SelectKernel`, `SetParams`. Общий базовый класс не выносить, `SwarmPointForcePass` не менять.

**Б3. Валидатор** (`SwarmPointValidator.cs`, ADR-055 §2.7):
- Оба типа, `SwarmPointForcePass` и `SwarmSpringForcePass`, считаются пассом точек: увеличивают `pointPasses`, первый задаёт `pointIndex` и `pointLabel` («Swarm Point Force» или «Swarm Spring Force»); если встретился `SwarmSpringForcePass`, запомнить это.
- «Больше одного пасса»: если пружинного пасса в списке нет, текст **побайтно прежний**. Если есть, текст: `SimulationWorld: more than one swarm point pass (Swarm Point Force and Swarm Spring Force apply the same point list). Each pass applies every point; extra passes repeat the sum.`
- «Стоит до Clear Velocity» и «стоит после шага»: те же тексты, но имя пасса берётся из `pointLabel`; для старого пасса строки получаются побайтно прежними.
- `WarnPointData` и остальное не менять. Комментарий к классу обновить (оба пасса).

**Б4. Тесты.** Порядок важен:
1. **Сначала** скопировать текущий `Assets/Tests/Editor/SwarmPointForceTests.cs` (HEAD, пружинные ожидания) в новый `Assets/Tests/Editor/SwarmSpringForceTests.cs`. В копии: класс `SwarmSpringForceTests`, поле `pointPass` и фабрика в каркасе целятся в `SwarmSpringForcePass`. Оставить кернел-тесты: `DeltaTime_DoesNotChangeTheAddition`, `Weight_ScalesSpring_AndFlipsSign`, `Wrap_UsesShortestVector_WrapOffUsesStraightDifference`, `Profile_PlateauFalloffAndUnbounded`, `Sum_AddsPoints_AndDropsTheSeventeenth`, `VerticalPosition_DoesNotChangeVelocityY`, `AddsToExistingVelocity`, `CoincidentPoint_LeavesVelocity_AndIsFinite`, `ZeroPoints_DoesNotDispatch`, `Heading_TurnsTowardThePoint`. Убрать из копии `Validator_WarnsOnEachBadCase_AndIsQuietWhenOrdered`, `Layout_Stride32_AndSlotsMatchUpload`, `ExistingEffect_HasNoPointsAndNoPass` (они остаются только в старом файле).
2. В новый файл добавить валидаторные тесты пружины: предупреждение «после шага» для `TeamHeadingSteerPass` и для `TeamClassicSteerPass` и тишина при правильном порядке; предупреждение «до Clear Velocity»; два пружинных пасса; смесь старого и пружинного (проверка нового текста «more than one swarm point pass»).
3. **Затем** вернуть старый файл к версии до `521a98b`: записать в `Assets/Tests/Editor/SwarmPointForceTests.cs` содержимое `git show 521a98b^:Assets/Tests/Editor/SwarmPointForceTests.cs` (файл в ТЗ, правка допустима). Проверка: `git diff 521a98b^ -- Assets/Tests/Editor/SwarmPointForceTests.cs` пуст. В старый файл ничего не добавлять.
4. Парный `.meta` нового файла закоммитить.

## Часть 4. Приёмка

1. **EditMode:** красных ровно четыре из части 1 (имена названы). Записать число «до» (после N) и «после» и имена новых тестов. Шесть ранее красных `SwarmPointForceTests` зелёные (на старом файле с нормализованными ожиданиями), пружинные в `SwarmSpringForceTests` зелёные.
2. **`git diff --stat` коммита S1** содержит только: `Assets/Scripts/Passes/BoidsPasses.cs`, `Assets/Shaders/GPU/Passes/BoidsPasses.compute`, `Assets/Scripts/Runtime/SwarmPointValidator.cs`, `Assets/Tests/Editor/SwarmPointForceTests.cs` (возврат к `521a98b^`), `Assets/Tests/Editor/SwarmSpringForceTests.cs` (+ `.meta`). Другой путь остановка и вопрос.
3. **`BoidsPasses.compute`:** `git diff` показывает только новую строку `#pragma kernel SwarmSpringForce` и блок в конце файла. Строки ядра `SwarmPointForce`, `TeamClassicSteer` и остальных не затронуты.
4. `git diff` по `Assets/Effects` и `Assets/Scenes` в коммитах S1 и S2 пуст. Коммит N свою часть `Assets/Effects` уже содержит.
5. Тест `Validator_WarnsOnEachBadCase_AndIsQuietWhenOrdered` в старом файле зелёный без правок.
6. Все остальные тесты (обходы, оракул, тор, кап, P9.1) зелёные без правок ожиданий.

## Часть 5. Документы (коммит S2)

- **ADR-055 §6 «Результат»:** разведка, хеши коммитов, число EditMode до и после, список красных, таблица диагноза из части 1, отклонения.
- **ADR-049:** после пункта 2.1 и после пункта 2.9 добавить цитату: `> Примечание 2026-10-28 ([ADR-055](ADR-055-Swarm-Spring-Force.md)): пружина реализована отдельным пассом SwarmSpringForce. SwarmPointForcePass остаётся нормализованным (как в ядре на 7da0e66 и в пресетах Boids_hash_points, Boids_hash200k_points). Шкала весов этого пункта относится к пружинному пассу.` Остальной текст ADR-049 не править.
- **ADR-054:** добавить цитату после пункта о точке ADR-049 в §1 и после строки «Отталкивающая точка ADR-049 как препятствие» в §3: `> Примечание 2026-10-28 ([ADR-055](ADR-055-Swarm-Spring-Force.md)): сказанное относится к пружинному пассу. У нормализованного SwarmPointForce длина силы постоянна на плато, и отталкивающая точка с большим отрицательным весом может служить мягким препятствием; нужен ли отдельный пасс препятствий, решается в Play P9.2.` Остальной текст ADR-054 не править.
- **`DOC/pass-catalog.md`:** запись «Swarm Spring Force» (отличие от `Swarm Point Force`: вес это жёсткость, длина силы `расстояние · |weight| · профиль`; общий список точек; оба вместе нельзя). В записи «Swarm Point Force» одна фраза: вес это длина силы (нормализованное направление), пружина это другой пасс. `DOC/capabilities.md`, `DOC/status.md` (строка «Итерация»).
- **`DOC/last/Techdebt.md`:** (а) четыре красных теста пресетов с диагнозом из части 1; (б) оба пасса точек читают один список, смешивание по точкам (режим на `SwarmPoint`) отложено.

## Что присылает Grok

Хеш головы перед работой; хеши коммитов N, 0, S1, S2; ответы разведки; число EditMode после N и после S1; таблица диагноза четырёх тестов; список красных в конце; `git diff --stat` по S1; подтверждение `git diff 521a98b^ -- …/SwarmPointForceTests.cs` пуст; расхождения с ТЗ; вопросы владельцу.

## Не делать

- Не менять ядро `SwarmPointForce`, класс `SwarmPointForcePass`, структуру `SwarmPoint`, `SimulationWorld`, `SimContext`, ядра соседей и хэша, код P9.1.
- Не создавать пресеты, сцены, меню; не менять существующие `.asset`, кроме самого факта коммита N.
- Не чинить четыре красных теста пресетов и не менять содержимое трёх ассетов: только записать в коммите N и описать диагноз.
- Не добавлять `DOC/MEASURE/nabuild-na/`.
- Не менять ожидания существующих тестов, кроме возврата `SwarmPointForceTests.cs` к `521a98b^`.
- Не делать `merge`, `rebase`, `push`.
