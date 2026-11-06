using System;
using NUnit.Framework;
using UnityEditor;

public class MeasureOutputTests
{
    [Test]
    public void WriteSwitch_DefaultsToTemp_AndEitherSourceSelectsDoc()
    {
        bool previous = SessionState.GetBool(MeasureOutput.SessionKey, false);
        string previousVariable = Environment.GetEnvironmentVariable("P8_WRITE_MEASURE");
        try
        {
            SessionState.SetBool(MeasureOutput.SessionKey, false);
            Environment.SetEnvironmentVariable("P8_WRITE_MEASURE", null);
            StringAssert.Contains(
                "Temp" + System.IO.Path.DirectorySeparatorChar + "p8-field-force",
                MeasureOutput.DirectoryPath());

            SessionState.SetBool(MeasureOutput.SessionKey, true);
            StringAssert.Contains(
                "DOC" + System.IO.Path.DirectorySeparatorChar + "MEASURE",
                MeasureOutput.DirectoryPath());

            SessionState.SetBool(MeasureOutput.SessionKey, false);
            Environment.SetEnvironmentVariable("P8_WRITE_MEASURE", "1");
            StringAssert.Contains(
                "DOC" + System.IO.Path.DirectorySeparatorChar + "MEASURE",
                MeasureOutput.DirectoryPath());
        }
        finally
        {
            SessionState.SetBool(MeasureOutput.SessionKey, previous);
            Environment.SetEnvironmentVariable("P8_WRITE_MEASURE", previousVariable);
        }
    }
}
