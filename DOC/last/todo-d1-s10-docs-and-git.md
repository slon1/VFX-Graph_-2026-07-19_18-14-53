# ТЗ D1: замеры S10, документация и git

Тип: правки только в `DOC/`. Код, ассеты и сцены не менять. Одна фаза, остановка на гейте. Явные `git add`, без `git add -A`, без stash/checkout/restore/reset на чужих файлах. Не пушить.

Сборка базы: `df9748d`, прогоны 2026-10-10, Samsung SM-G973F, Vulkan, Mali-G76, Unity 6000.5.9f1. Сцена `Assets/Scenes/Boids_Hash.unity`, эффект `Assets/Effects/Boids_hash100k.asset`. Разрешение — системная настройка Samsung. Харнесс `Assets/Scripts/FPSDisplay.cs`.

Сделано двумя коммитами.

1. Одна копия лога на коммит, индекс `DOC/MEASURE/README.md`, ссылки в ADR-041 и ADR-043 ведут в папки `d9bba9c/` и `27c7c4c/`. Плоские дубли логов A2 удалены после сверки: logcat совпали побайтно, строки `M3D_PERF` сводного CSV совпали с объединением двух папок (32 = 16 + 16). В сводном файле были подписи коммитов, в папках — пустая строка после шапки. Старые `logcat*.txt` и `m3d_perf_2.csv` не переименовывались.
2. ADR-040 errata 5, предложение в замер A2, ореол A4 как квад `hashCount`, ссылка §5 ADR-041 на errata 5, абзац S10 в `getting-started.md`, строки 10d–10f в `Techdebt.md`, строка итерации в `status.md`.

Гейт: `git status` и `git log` в сдаче. Дальше не идти.
