# Streamline B, отдельный заход, 2026-10-16

Сцена `Boids_Hash_test`, эффект `Boids_hash100k`, 100000 частиц, maxNeighbors 48, камера 46.
Сборка `6ac1cad`, dirty=1, dirtyhash `f3017014`, powersave=0, экран 1520×720.
Старт при `mWakefulness=Dozing`. Это не сессия C и D из `20261016-2051`.

| режим | что включено | frameAvg | long | wakefulness | GPU active, МГц | циклы на кадр | циклы / 100000 |
|---|---|---:|---:|---|---:|---:|---:|
| B | сила 1, рендер 0 | 17.68 | 0.057 | Dozing | 689.7 | 12193413 | 121.93 |

DONE: frameP95=33.42, frameMax=33.61, hist=0/1334/0/81/0/0, res=1520×720.
`clockMHz`, `utilization` и `computePerFrame` в отчёте `na`: шаблон `with-metrics` не пишет колонки часов и execution core.
