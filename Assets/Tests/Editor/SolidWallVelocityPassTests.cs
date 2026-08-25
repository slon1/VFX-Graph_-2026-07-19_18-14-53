using System;
using System.Globalization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class SolidWallVelocityPassTests
{
    private const string Velocity = "velocity";
    private const string FluidCompute = "Assets/Shaders/GPU/Passes/FluidPasses.compute";
    private const int SizeWorld = 32;
    private const int Resolution = 64;
    private const float DeltaTime = 1f;
    private static readonly Vector2 SeedValue = new Vector2(1.25f, -0.4f);

    [Test]
    [Category("GPU")]
    public void UniformSeed_InteriorUnchanged_FrameIsFreeSlip()
    {
        using (FieldTestHarness harness = CreateVelocityHarness())
        {
            Vector2[] seed = FillVelocity(SeedValue);
            harness.SeedVelocity(Velocity, seed);

            SolidWallVelocityPass pass = new SolidWallVelocityPass();
            pass.Initialize(harness.Context);
            harness.RunPass(pass, DeltaTime);

            Vector2[] obtained = harness.ReadVelocity(Velocity);
            int interiorCount = 0;
            int edgeCount = 0;
            int cornerCount = 0;
            int n = Resolution;
            int last = n - 1;
            int seedXBits = BitConverter.SingleToInt32Bits(SeedValue.x);
            int seedYBits = BitConverter.SingleToInt32Bits(SeedValue.y);

            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    int ox = BitConverter.SingleToInt32Bits(obtained[i].x);
                    int oy = BitConverter.SingleToInt32Bits(obtained[i].y);
                    bool onX = x == 0 || x == last;
                    bool onY = y == 0 || y == last;

                    if (onX && onY)
                    {
                        cornerCount++;
                        Assert.AreEqual(0, ox, $"3.1 corner ({x},{y}) u.x bits={ox} obtained={obtained[i].x:G9}");
                        Assert.AreEqual(0, oy, $"3.1 corner ({x},{y}) u.y bits={oy} obtained={obtained[i].y:G9}");
                    }
                    else if (onX)
                    {
                        edgeCount++;
                        Assert.AreEqual(0, ox, $"3.1 x-edge ({x},{y}) u.x bits={ox} obtained={obtained[i].x:G9}");
                        Assert.AreEqual(seedYBits, oy, $"3.1 x-edge ({x},{y}) u.y obtained={obtained[i].y:G9} seed={SeedValue.y:G9}");
                    }
                    else if (onY)
                    {
                        edgeCount++;
                        Assert.AreEqual(seedXBits, ox, $"3.1 y-edge ({x},{y}) u.x obtained={obtained[i].x:G9} seed={SeedValue.x:G9}");
                        Assert.AreEqual(0, oy, $"3.1 y-edge ({x},{y}) u.y bits={oy} obtained={obtained[i].y:G9}");
                    }
                    else
                    {
                        interiorCount++;
                        Assert.AreEqual(seedXBits, ox, $"3.1 interior ({x},{y}) u.x obtained={obtained[i].x:G9} seed={SeedValue.x:G9}");
                        Assert.AreEqual(seedYBits, oy, $"3.1 interior ({x},{y}) u.y obtained={obtained[i].y:G9} seed={SeedValue.y:G9}");
                    }
                }
            }

            string report =
                $"3.1 N={n} seed=({SeedValue.x.ToString("G9", CultureInfo.InvariantCulture)}, " +
                $"{SeedValue.y.ToString("G9", CultureInfo.InvariantCulture)}) " +
                $"interior={interiorCount} edges={edgeCount} corners={cornerCount}";
            TestContext.WriteLine(report);
            Debug.Log(report);

            Assert.AreEqual((n - 2) * (n - 2), interiorCount);
            Assert.AreEqual(4 * (n - 2), edgeCount);
            Assert.AreEqual(4, cornerCount);
        }
    }

    [Test]
    [Category("GPU")]
    public void Validator_NonSquareTexel_ThrowsWithAdr016()
    {
        FieldDescriptor velocity = VelocityDescriptor(
            new Vector2Int(32, 32), new Vector2(10f, 20f));

        using (FieldTestHarness harness = new FieldTestHarness(new[] { velocity }))
        {
            SolidWallVelocityPass pass = new SolidWallVelocityPass();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => SquareTexelValidator.Validate(new SimPass[] { pass }, harness.Context.Fields));
            TestContext.WriteLine(ex.Message);
            StringAssert.Contains(pass.DisplayName, ex.Message);
            StringAssert.Contains("ADR-016 §2.1", ex.Message);
            StringAssert.Contains("hx=", ex.Message);
            StringAssert.Contains("hy=", ex.Message);
        }
    }

    [Test]
    [Category("GPU")]
    public void SecondRun_MatchesFirstRunBitwise()
    {
        using (FieldTestHarness harness = CreateVelocityHarness())
        {
            harness.SeedVelocity(Velocity, FillVelocity(SeedValue));

            SolidWallVelocityPass pass = new SolidWallVelocityPass();
            pass.Initialize(harness.Context);
            harness.RunPass(pass, DeltaTime);
            Vector2[] afterFirst = harness.ReadVelocity(Velocity);

            harness.RunPass(pass, DeltaTime);
            Vector2[] afterSecond = harness.ReadVelocity(Velocity);

            Assert.AreEqual(afterFirst.Length, afterSecond.Length);
            for (int i = 0; i < afterFirst.Length; i++)
            {
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(afterFirst[i].x),
                    BitConverter.SingleToInt32Bits(afterSecond[i].x),
                    $"3.3 [{i}].x first={afterFirst[i].x:G9} second={afterSecond[i].x:G9}");
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(afterFirst[i].y),
                    BitConverter.SingleToInt32Bits(afterSecond[i].y),
                    $"3.3 [{i}].y first={afterFirst[i].y:G9} second={afterSecond[i].y:G9}");
            }
        }
    }

    private static FieldTestHarness CreateVelocityHarness()
    {
        FieldDescriptor velocity = VelocityDescriptor(
            new Vector2Int(Resolution, Resolution), new Vector2(SizeWorld, SizeWorld));
        Assert.AreEqual(GraphicsFormat.R32G32_SFloat, velocity.Format);
        return new FieldTestHarness(new[] { velocity }, FluidCompute);
    }

    private static Vector2[] FillVelocity(Vector2 value)
    {
        Vector2[] values = new Vector2[Resolution * Resolution];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = value;
        }

        return values;
    }

    private static FieldDescriptor VelocityDescriptor(Vector2Int resolution, Vector2 size)
    {
        return FieldTestHarness.Descriptor(
            Velocity, FieldSemantic.Velocity, GraphicsFormat.R32G32_SFloat,
            resolution, size, Color.clear);
    }
}
