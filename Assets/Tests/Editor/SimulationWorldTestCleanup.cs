using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public static class SimulationWorldTestCleanup
{
    public static void DestroyHost(GameObject host)
    {
        if (host == null)
        {
            return;
        }

        try
        {
            MethodInfo teardown = typeof(SimulationWorld).GetMethod(
                "Teardown",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (teardown == null)
            {
                Assert.Fail(
                    "SimulationWorld.Teardown was not found. The method was renamed; update SimulationWorldTestCleanup.");
                return;
            }

            SimulationWorld[] worlds = host.GetComponents<SimulationWorld>();
            for (int i = 0; i < worlds.Length; i++)
            {
                teardown.Invoke(worlds[i], null);
            }
        }
        finally
        {
            if (host != null)
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }
}
