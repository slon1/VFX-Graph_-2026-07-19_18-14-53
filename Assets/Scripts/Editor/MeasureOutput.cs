using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Measurement files stay in Temp unless this editor session, or P8_WRITE_MEASURE=1, asks for DOC.
/// SessionState survives a domain reload and clears when the editor process quits.
/// </summary>
internal static class MeasureOutput
{
    internal const string SessionKey = "M3D.P8.WriteMeasure";

    internal static bool WriteToDoc
    {
        get
        {
            if (SessionState.GetBool(SessionKey, false))
            {
                return true;
            }

            return string.Equals(
                Environment.GetEnvironmentVariable("P8_WRITE_MEASURE"),
                "1",
                StringComparison.Ordinal);
        }
    }

    internal static string DirectoryPath()
    {
        string folder = WriteToDoc
            ? Path.Combine("DOC", "MEASURE", "p8-field-force")
            : Path.Combine("Temp", "p8-field-force");
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", folder));
    }

    [MenuItem("Tools/M3D/Measure/Write to DOC on")]
    private static void EnableDoc()
    {
        SessionState.SetBool(SessionKey, true);
    }

    [MenuItem("Tools/M3D/Measure/Write to DOC off")]
    private static void DisableDoc()
    {
        SessionState.SetBool(SessionKey, false);
    }
}
