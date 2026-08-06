## M2c: Multi-Field-Per-Kernel Binding

**Статус:** реализовано (см. ADR-008, status.md)

### Контекст

Прочитать ADR-008. Референс для форм классов — `FieldKernelPass`/`DiffuseFieldPass` (`Assets/Scripts/Runtime/SimPass.cs`, `Assets/Scripts/Passes/FieldPasses.cs`).

### DoD (закрыт)

1. `FieldSlotRole` + `FieldRequest.Role` (default A) + equality; `FieldRequestSets.Pair`
2. `SimShaderIds.FieldReadA/B` + `FieldWriteA/B` (legacy `FieldRead`/`FieldWrite` сохранены)
3. `FieldKernelPass`: guard-матрица `{A}` | `{A,B}`; multi → `*A`/`*B`; hard error на mismatch Resolution/plane; `PrimaryFieldName` = Role A
4. `SwapFieldsPass` + `MultiFieldTestPasses.compute` + Pass Library; только `FieldWrites` WritePingPong Pair
5. Тесты: `FieldSlotNamingTests` (roles + geometry), `SwapFieldsPassTests`; MCP smoke swap A↔B

### Вне скоупа

Gray-Scott. 3+ полей. Multi-field `ParticleKernelPass`. `FieldAccumPassValidator`.
