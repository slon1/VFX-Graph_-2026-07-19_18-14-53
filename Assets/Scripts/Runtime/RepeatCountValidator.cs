using System;
using System.Collections.Generic;

/// <summary>
/// Build-time check that every enabled pass has RepeatCount ≥ 1 (ADR-015 §4).
/// Disabled passes are skipped, matching Update / Initialize / accum allocation.
/// Revisit when F0.4 initializes disabled passes — validation of disabled
/// RepeatCount becomes meaningful only after that.
/// </summary>
internal static class RepeatCountValidator
{
    public static void Validate(IReadOnlyList<SimPass> passes)
    {
        if (passes == null)
        {
            return;
        }

        for (int i = 0; i < passes.Count; i++)
        {
            SimPass pass = passes[i];
            if (pass == null || !pass.Enabled)
            {
                continue;
            }

            int repeat = pass.RepeatCount;
            if (repeat < 1)
            {
                throw new InvalidOperationException(
                    $"SimulationWorld: pass '{pass.DisplayName}' has RepeatCount={repeat}. " +
                    "RepeatCount must be >= 1 (ADR-015 §4).");
            }
        }
    }
}
