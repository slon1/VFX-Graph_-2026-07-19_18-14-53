Проект Unity 6 M3D: J:\work\VFX Graph_ (или путь на новом устройстве).
Стек: Unity 6000.4.x + UniTask, URP, VFX Graph. Отвечай по-русски.
Роли: Sonnet=архитектор ADR/DoD, Grok=исполнитель.
HEAD: 667c5e0 — LUT+HDR ScalarHeatmap (ADR-010). Перед этим: Gray-Scott, TouchInject, AgentBoost/Erode, пресеты Gray-Scott-Boids / Gray-Scott-Agents, Techdebt 1b про clamp dt (не сделан).
Продолжаем с рендера M2d: LUT уже влит. Следующий тикет — по согласованию (trail buffer / dt-clamp / VFX>indirect).
Свертка и контекст — в предыдущем сообщении пользователя / DOC/last/*.