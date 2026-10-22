---
name: s10-measure
description: >-
  Runs the Galaxy S10 boids measurement harness and Streamline profiles for
  modes A, B, C, and D. Use when the user asks for a замер, серия ABABC,
  Streamline capture, preflight, gpu-metrics, S10 frame times, or cycles per particle.
---

# Замеры S10

Команды только из корня репозитория, скриптами `Tools/s10`. Новый парсер CSV не писать. Телефон запускать только по явной просьбе. Скрипт не меняет разрешение, яркость и режим питания.

Протокол и таблица базы: [DOC/MEASURE/README.md](../../../DOC/MEASURE/README.md). План: [DOC/plan-P7-perf-200k.md](../../../DOC/plan-P7-perf-200k.md).

## Режимы

| режим | сила | рендер | где |
|---|---:|---:|---|
| A | 1 | 1 | харнесс |
| B | 1 | 0 | харнесс и Streamline |
| C | 0 | 1 | харнесс и Streamline |
| D | 0 | 0 | только Streamline |

Харнесс по умолчанию `ABABC`. Строка без пробелов и без цифр разбирается по символам. Иначе токены через пробел: `A` оставляет walk ассета, `A0` передаёт `-m3d_walk 0`, `A2` и `B2` передают `-m3d_walk 2`. Слитный токен `A0A1` — ошибка. D в серию не добавлять. В `M3D_PERF` поле `walkeff` — реально выполненный обход; если оно есть и не равно `walk`, сводка помечает прогон `walk fallback`. `-Cam 46` у `run-series.ps1` и `capture-streamline.ps1` передаёт `-m3d_cam 46`. У захвата Streamline walk задаётся `-Walk`. `-Cap 1` передаёт `-m3d_cap 1`: сводка пишет `cap counting` и не берёт прогон в сравнение времён. `-HashRep 4` передаёт `-m3d_hashrep 4`; при значении больше 1 причина `diagnostic`. `-ForceRep 4` передаёт `-m3d_forcerep 4`: повтор выбранного кернела силы в скрэтч, `RepeatCount` остаётся 1, в ключ сводки попадает только вместе с полем в строке `DONE`. `-MaxN 128` передаёт `-m3d_maxn 128`.

D — кадр без обхода соседей и без рендера частиц. Хэш, руление, интеграция и кадр URP продолжаются.

## Перед запуском

1. `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/s10/preflight.ps1`
2. `low_power` должен быть `0`. Если `1`, замер не начинать и сказать об этом.
3. `mWakefulness` печатается и пишется в лог. `Dozing` и `Asleep` точку не отменяют. Пустое поле останавливает серию.
4. Короткая сторона берётся из `res` лога. `Override size 720x1520` — режим 720, даже если `Physical size` печатает `1440x3040`. Для профиля, который сравнивают с базой 720, короткая сторона должна быть 720.
5. Сцена по умолчанию — `Assets/Scenes/Boids_Hash_test.unity` с `Assets/Effects/Boids_hash100k.asset`. Для 200 000 частиц — `Boids_Hash_test200k` и `Boids_hash200k` (камера около 380). Для плотной — `Boids_Hash_testx4` и `Boids_hash100k_x4` (камера около 135). Другую сцену запускать параметром `-Scene` у `run-series.ps1` и `capture-streamline.ps1` (`-m3d_scene`). Все три сцены должны быть в Build Settings; скрипт их туда не добавляет.

Пакет `com.slonInc.vfx`, activity `com.unity3d.player.UnityPlayerGameActivity`, serial по умолчанию `RF8N31SLV7N`. Прогрев 60 с, окно 25 с, пауза между точками серии 60 с. adb: `-Adb`, иначе `M3D_ADB`, иначе `%LOCALAPPDATA%/Android/Sdk/platform-tools/adb.exe`. Streamline: `-Streamline`, иначе `M3D_STREAMLINE`, иначе `PATH`. Имя пользователя не зашивать. При `adb server version doesn't match` остановиться.

## Куда пишется результат

Харнесс сам кладёт логи и `summary.md` в `DOC/MEASURE/<build>-<dirtyhash>/<yyyyMMdd-HHmm>/`. Имя лога `NN-<режим>-<короткая сторона>.txt`, первая строка `mWakefulness=...`.

Streamline: пользователь даёт только имя файла `Capture_<режим>.apc`. Каталог `.apc` — `Документы/Streamline`. В git захват не класть. CSV отчёта копировать в папку захода через `-FixtureOut`. Дубли в `Tools/s10/out/` не коммитить.

После захвата вызвать `gpu-metrics.ps1` с `-FrameAvgMs` из DONE этого же лога и `-ParticleCount`. Свой `summary.md` в папке захода, с колонкой `wakefulness`.

`clockMHz`, `utilization` и `computePerFrame` на шаблоне `with-metrics` равны `na`: в CSV нет `Mali Clock:Frequency (Device #0)` и `Mali Shader Core Cycles:Execution core active`. Колонки не удалять. Фикстуры `DOC/MEASURE/1519383/s10` и `s10_` не удалять.

Окно отчёта: `-scale 100 -start <warmup> -stop <warmup+measure>`. Длительность захвата: warmup + measure + 30. Успех захвата — фраза `Created Streamline capture` и существующий каталог `.apc`.

## Команды

```
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/s10/preflight.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/s10/run-series.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/s10/capture-streamline.ps1 -Mode B -ApcName Capture_B.apc -FixtureOut DOC/MEASURE/<папка>/Capture_B.csv
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/s10/gpu-metrics.ps1 -Timeline DOC/MEASURE/<папка>/Capture_B.csv -FrameAvgMs <DONE> -ParticleCount 100000 -Out DOC/MEASURE/<папка>/Capture_B-gpu-metrics.csv
```

Режимы C и D — те же команды с `-Mode C` или `-Mode D` и своим именем `.apc`. Не перезаписывать чужой захват.

## Не коммитить

`.apc`, APK, `Tools/s10/out/`, `*.tmp`, `quote-check.cmd`, включённый `countCapHits`. Долю капа снимать в редакторе и вернуть флаг в 0 до выхода из Play, не сохраняя сцену.
