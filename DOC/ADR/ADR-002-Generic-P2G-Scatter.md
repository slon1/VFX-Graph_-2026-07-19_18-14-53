# ADR-002: Generic P2G Scatter через Fixed-Point Atomic Accumulation Buffer

**Статус:** Принято (реализовано M2b.1)  
**Дата:** 2026-08-02 (обновлено под average+count / state machine)  
**Контекст:** M3D Framework, Milestone 2b.1

### Контекст

Единственный источник записи в поле до M2b.1 — `TouchInjectVelocityFieldPass` (CPU touches, без коллизий). Частицы не могли писать в `FieldSet`. Нужен generic P2G: N частиц → поле с корректной обработкой коллизий записи.

### Решение (кратко)

Промежуточный `FieldAccumBuffer` (`RWStructuredBuffer<uint>`) + `InterlockedAdd` + Normalize в текстуру поля.

**Layout слота текселя:** `[value0 .. valueChannels-1][count]` — count всегда последний.  
`Channels` в API = value-каналы; `BufferCount = Channels + 1`.

**Агрегация v1 — average, не biased-sum:**

```
encoded = EncodeFixed(v, Scale, Bias)   // NaN-guard, затем max(0,·)
decoded = (raw/Scale − count·Bias) / max(count, 1)   // count==0 → 0
fieldWrite += decoded   // всегда Add; Replace = ClearFieldPass в EffectAsset
```

**Encode (обязательный порядок):**

```hlsl
float x = (value + bias) * scale;
x = (x == x) ? max(x, 0.0) : 0.0;  // NaN first, then clamp
```

Runtime-guard на overflow **суммы** uint — вне скоупа v1 (документированные Scale/Bias + дефолты 4096/32).

### Пассы

| Pass | Роль |
|------|------|
| `ClearFieldAccumPass` | zero accum |
| `ParticleToFieldScatterPass` / `ScatterVelocityToFieldPass` | atomic scatter |
| `NormalizeFieldAccumPass` / `NormalizeVelocityAccumPass` | average → field Add |

Запросы: `FieldAccumClearRequest(name, channels)` и `FieldAccumRequest(name, channels, scale, bias)` — отдельные списки на `SimPass` (`FieldAccumClears` / `Writes` / `Reads`).

### Build-валидация (enabled-only)

- Policy C: поле объявлено на EffectAsset
- `Channels == FieldDescriptor.ChannelCount` (exact)
- Channels согласованы между Clear/Scatter/Normalize; Scale/Bias — между Scatter/Normalize
- State machine (**Normalize → Unclear**):

```
Unclear (init / after Normalize)
Cleared (after ClearAccum)
Scattered (after ≥1 Scatter)

ClearAccum:  * → Cleared
Scatter:     Cleared|Scattered → Scattered; Unclear → ERROR
Normalize:   Scattered → Unclear; else → WARNING
```

### Семантики через композицию

- **Accumulate-onto-decaying** (демо AgentFieldEcho): `ClearAccum → Scatter → Normalize → Decay` (без `ClearFieldPass`)
- **Replace**: `ClearFieldPass → ClearAccum → Scatter → Normalize`

### Проекция velocity

`float2(dot(v, FieldAxisU), dot(v, FieldAxisV))` — как TouchInject, не `.xy`.

### Отклонённые варианты

Прямая запись float UAV / vendor float-atomics / spatial hash — см. исходное обсуждение; принят uint InterlockedAdd.
