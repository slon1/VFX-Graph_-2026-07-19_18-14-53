using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// P2G accum pass order validator (Build-time, enabled passes only).
///
/// Per field name state machine:
///   Unclear (init / after Normalize)
///   Cleared (after ClearAccum)
///   Scattered (after ≥1 Scatter)
///
///   ClearAccum:  * → Cleared
///   Scatter:     Cleared|Scattered → Scattered; Unclear → ERROR
///   Normalize:   Scattered → Unclear; else → WARNING
/// </summary>
internal static class FieldAccumPassValidator
{
    public enum AccumState
    {
        Unclear = 0,
        Cleared = 1,
        Scattered = 2,
    }

    public sealed class Result
    {
        public bool Success { get; set; } = true;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Validates descriptor channels, cross-pass Channels/Scale/Bias agreement, and state machine.
    /// Does not allocate GPU resources.
    /// </summary>
    public static Result Validate(
        IReadOnlyList<SimPass> passes,
        Func<string, FieldDescriptor> tryGetDescriptor)
    {
        Result result = new Result();
        if (passes == null)
        {
            return result;
        }

        Dictionary<string, int> channelsByField = new Dictionary<string, int>(StringComparer.Ordinal);
        Dictionary<string, FieldAccumRequest> codecByField =
            new Dictionary<string, FieldAccumRequest>(StringComparer.Ordinal);
        Dictionary<string, string> channelsOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> codecOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < passes.Count; i++)
        {
            SimPass pass = passes[i];
            if (pass == null || !pass.Enabled)
            {
                continue;
            }

            CollectClear(pass, pass.FieldAccumClears, tryGetDescriptor, channelsByField, channelsOwner, result);
            CollectRequest(
                pass, pass.FieldAccumWrites, tryGetDescriptor,
                channelsByField, channelsOwner, codecByField, codecOwner, result);
            CollectRequest(
                pass, pass.FieldAccumReads, tryGetDescriptor,
                channelsByField, channelsOwner, codecByField, codecOwner, result);
        }

        if (!result.Success)
        {
            return result;
        }

        ValidateStateMachine(passes, result);
        return result;
    }

    private static void CollectClear(
        SimPass pass,
        IReadOnlyList<FieldAccumClearRequest> requests,
        Func<string, FieldDescriptor> tryGetDescriptor,
        Dictionary<string, int> channelsByField,
        Dictionary<string, string> channelsOwner,
        Result result)
    {
        for (int i = 0; i < requests.Count; i++)
        {
            FieldAccumClearRequest request = requests[i];
            if (!ValidatePolicyAndDescriptor(pass, request.FieldName, request.Channels, tryGetDescriptor, result))
            {
                continue;
            }

            AgreeChannels(pass.DisplayName, request.FieldName, request.Channels, channelsByField, channelsOwner, result);
        }
    }

    private static void CollectRequest(
        SimPass pass,
        IReadOnlyList<FieldAccumRequest> requests,
        Func<string, FieldDescriptor> tryGetDescriptor,
        Dictionary<string, int> channelsByField,
        Dictionary<string, string> channelsOwner,
        Dictionary<string, FieldAccumRequest> codecByField,
        Dictionary<string, string> codecOwner,
        Result result)
    {
        for (int i = 0; i < requests.Count; i++)
        {
            FieldAccumRequest request = requests[i];
            if (!ValidatePolicyAndDescriptor(pass, request.FieldName, request.Channels, tryGetDescriptor, result))
            {
                continue;
            }

            AgreeChannels(pass.DisplayName, request.FieldName, request.Channels, channelsByField, channelsOwner, result);

            if (codecByField.TryGetValue(request.FieldName, out FieldAccumRequest existing))
            {
                if (!existing.Scale.Equals(request.Scale) || !existing.Bias.Equals(request.Bias))
                {
                    result.Success = false;
                    result.Errors.Add(
                        $"SimulationWorld: field '{request.FieldName}' P2G Scale/Bias mismatch between " +
                        $"'{codecOwner[request.FieldName]}' (Scale={existing.Scale}, Bias={existing.Bias}) and " +
                        $"'{pass.DisplayName}' (Scale={request.Scale}, Bias={request.Bias}).");
                }
            }
            else
            {
                codecByField[request.FieldName] = request;
                codecOwner[request.FieldName] = pass.DisplayName;
            }
        }
    }

    private static bool ValidatePolicyAndDescriptor(
        SimPass pass,
        string fieldName,
        int channels,
        Func<string, FieldDescriptor> tryGetDescriptor,
        Result result)
    {
        if (string.IsNullOrEmpty(fieldName))
        {
            result.Success = false;
            result.Errors.Add($"SimulationWorld: pass '{pass.DisplayName}' has an empty P2G field name.");
            return false;
        }

        FieldDescriptor descriptor = tryGetDescriptor(fieldName);
        if (descriptor == null)
        {
            result.Success = false;
            result.Errors.Add(
                $"SimulationWorld: pass '{pass.DisplayName}' references undeclared field '{fieldName}' " +
                "for P2G accum. Add it to EffectAsset.Fields (Policy C).");
            return false;
        }

        if (channels != descriptor.ChannelCount)
        {
            result.Success = false;
            result.Errors.Add(
                $"SimulationWorld: pass '{pass.DisplayName}' P2G request for '{fieldName}' has " +
                $"Channels={channels}, but field descriptor format {descriptor.Format} has " +
                $"{descriptor.ChannelCount} channel(s). Exact match required (same rule as UAV Write).");
            return false;
        }

        return true;
    }

    private static void AgreeChannels(
        string passName,
        string fieldName,
        int channels,
        Dictionary<string, int> channelsByField,
        Dictionary<string, string> channelsOwner,
        Result result)
    {
        if (channelsByField.TryGetValue(fieldName, out int existing))
        {
            if (existing != channels)
            {
                result.Success = false;
                result.Errors.Add(
                    $"SimulationWorld: field '{fieldName}' P2G Channels mismatch between " +
                    $"'{channelsOwner[fieldName]}' (Channels={existing}) and '{passName}' (Channels={channels}).");
            }
        }
        else
        {
            channelsByField[fieldName] = channels;
            channelsOwner[fieldName] = passName;
        }
    }

    private static void ValidateStateMachine(IReadOnlyList<SimPass> passes, Result result)
    {
        Dictionary<string, AccumState> state = new Dictionary<string, AccumState>(StringComparer.Ordinal);

        for (int i = 0; i < passes.Count; i++)
        {
            SimPass pass = passes[i];
            if (pass == null || !pass.Enabled)
            {
                continue;
            }

            for (int c = 0; c < pass.FieldAccumClears.Count; c++)
            {
                string name = pass.FieldAccumClears[c].FieldName;
                state[name] = AccumState.Cleared;
            }

            for (int w = 0; w < pass.FieldAccumWrites.Count; w++)
            {
                string name = pass.FieldAccumWrites[w].FieldName;
                if (!state.TryGetValue(name, out AccumState current))
                {
                    current = AccumState.Unclear;
                }

                if (current == AccumState.Unclear)
                {
                    result.Success = false;
                    result.Errors.Add(
                        $"SimulationWorld: pass '{pass.DisplayName}' scatters into '{name}' without a " +
                        "preceding ClearFieldAccum for this round (state Unclear). " +
                        "Insert ClearFieldAccum before Scatter (after Normalize if multi-round).");
                }
                else
                {
                    state[name] = AccumState.Scattered;
                }
            }

            for (int r = 0; r < pass.FieldAccumReads.Count; r++)
            {
                string name = pass.FieldAccumReads[r].FieldName;
                if (!state.TryGetValue(name, out AccumState current))
                {
                    current = AccumState.Unclear;
                }

                if (current != AccumState.Scattered)
                {
                    result.Warnings.Add(
                        $"SimulationWorld: pass '{pass.DisplayName}' normalizes '{name}' without a " +
                        "preceding Scatter in this round (likely misconfiguration).");
                }
                else
                {
                    // Consumed but not zeroed — next Scatter needs a fresh ClearAccum.
                    state[name] = AccumState.Unclear;
                }
            }
        }
    }
}
