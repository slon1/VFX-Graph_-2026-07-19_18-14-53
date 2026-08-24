using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Build-time check for fluid passes: square texel on each field, and matching
/// Resolution across the pass (ADR-016 §2.1, ADR-017 §1). Disabled passes skipped.
/// </summary>
internal static class SquareTexelValidator
{
    private const float RelativeTolerance = 1e-4f;

    internal static void Validate(IReadOnlyList<SimPass> passes, FieldSet fields)
    {
        if (passes == null || fields == null)
        {
            return;
        }

        for (int i = 0; i < passes.Count; i++)
        {
            SimPass pass = passes[i];
            if (pass == null || !pass.Enabled || !pass.RequiresSquareTexel)
            {
                continue;
            }

            ValidatePass(pass, fields);
        }
    }

    private static void ValidatePass(SimPass pass, FieldSet fields)
    {
        List<(string name, FieldDescriptor descriptor)> union =
            CollectUniqueDescriptors(pass, fields);
        if (union.Count == 0)
        {
            return;
        }

        string referenceName = union[0].name;
        Vector2Int referenceResolution = union[0].descriptor.Resolution;

        for (int i = 0; i < union.Count; i++)
        {
            string name = union[i].name;
            FieldDescriptor descriptor = union[i].descriptor;
            Vector2Int resolution = descriptor.Resolution;

            if (resolution.x == 0 || resolution.y == 0)
            {
                throw new InvalidOperationException(
                    $"SimulationWorld: pass '{pass.DisplayName}' field '{name}' " +
                    $"has zero Resolution {resolution}.");
            }

            float hx = descriptor.Size.x / resolution.x;
            float hy = descriptor.Size.y / resolution.y;
            float maxH = Mathf.Max(hx, hy);
            if (maxH > 0f && Mathf.Abs(hx - hy) / maxH >= RelativeTolerance)
            {
                string hxText = hx.ToString(CultureInfo.InvariantCulture);
                string hyText = hy.ToString(CultureInfo.InvariantCulture);
                throw new InvalidOperationException(
                    $"SimulationWorld: pass '{pass.DisplayName}' field '{name}' " +
                    $"has non-square texel hx={hxText} hy={hyText} (ADR-016 §2.1).");
            }

            if (resolution != referenceResolution)
            {
                throw new InvalidOperationException(
                    $"SimulationWorld: pass '{pass.DisplayName}' fields '{referenceName}' " +
                    $"({referenceResolution.x}, {referenceResolution.y}) and '{name}' " +
                    $"({resolution.x}, {resolution.y}) have mismatched Resolution (ADR-017 §1).");
            }
        }
    }

    private static List<(string name, FieldDescriptor descriptor)> CollectUniqueDescriptors(
        SimPass pass,
        FieldSet fields)
    {
        List<(string name, FieldDescriptor descriptor)> union =
            new List<(string, FieldDescriptor)>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        AppendRequests(union, seen, pass.FieldReads, fields);
        AppendRequests(union, seen, pass.FieldWrites, fields);
        return union;
    }

    private static void AppendRequests(
        List<(string name, FieldDescriptor descriptor)> union,
        HashSet<string> seen,
        IReadOnlyList<FieldRequest> requests,
        FieldSet fields)
    {
        if (requests == null)
        {
            return;
        }

        for (int i = 0; i < requests.Count; i++)
        {
            string name = requests[i].FieldName;
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
            {
                continue;
            }

            union.Add((name, fields.Get(name).Descriptor));
        }
    }
}
