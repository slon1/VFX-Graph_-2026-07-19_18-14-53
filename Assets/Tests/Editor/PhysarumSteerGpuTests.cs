using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

[TestFixture]
public class PhysarumSteerGpuTests
{
    private const string ComputePath = "Assets/Shaders/GPU/Passes/PhysarumPasses.compute";
    private const int Resolution = 8;

    private FieldTestHarness harness;
    private ParticleSet particles;
    private CommandBuffer cmd;
    private ComputeShader shader;

    [SetUp]
    public void SetUp()
    {
        Assume.That(SystemInfo.supportsComputeShaders);

        shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        Assume.That(shader != null, "PhysarumPasses.compute must be imported.");
        Assume.That(shader.HasKernel("PhysarumSteer"));

        FieldDescriptor trail = FieldTestHarness.Descriptor(
            "trail",
            FieldSemantic.Scalar,
            GraphicsFormat.R32_SFloat,
            new Vector2Int(Resolution, Resolution),
            new Vector2(Resolution, Resolution),
            Color.clear);
        harness = new FieldTestHarness(new[] { trail });

        particles = new ParticleSet();
        particles.EnsureCapacity(1);
        particles.RegisterAttribute(BuiltinAttributes.Position);
        particles.RegisterAttribute(BuiltinAttributes.Heading);
        particles.RegisterAttribute(BuiltinAttributes.Velocity);
        cmd = new CommandBuffer { name = "PhysarumSteerGpu" };
    }

    [TearDown]
    public void TearDown()
    {
        cmd?.Release();
        cmd = null;
        particles?.Dispose();
        particles = null;
        harness?.Dispose();
        harness = null;
    }

    [Test]
    public void LeftSensor_TurnsLeft()
    {
        Vector3 heading = Step(Spot(35), new Vector3(0f, 0f, 1f));
        Assert.Less(heading.x, -0.9f);
        Assert.Less(Mathf.Abs(heading.z), 0.1f);
        Assert.Less(Mathf.Abs(heading.y), 1e-4f);
    }

    [Test]
    public void RightSensor_TurnsRight()
    {
        Vector3 heading = Step(Spot(37), new Vector3(0f, 0f, 1f));
        Assert.Greater(heading.x, 0.9f);
        Assert.Less(Mathf.Abs(heading.z), 0.1f);
        Assert.Less(Mathf.Abs(heading.y), 1e-4f);
    }

    [Test]
    public void FrontSensor_KeepsHeading()
    {
        Vector3 heading = Step(Spot(44), new Vector3(0f, 0f, 1f));
        Assert.Greater(heading.z, 0.9f);
        Assert.Less(Mathf.Abs(heading.x), 0.1f);
    }

    [Test]
    public void BothSides_TurnsToEitherSide()
    {
        float[] field = new float[Resolution * Resolution];
        field[35] = 1f;
        field[37] = 1f;
        Vector3 heading = Step(field, new Vector3(0f, 0f, 1f));
        Assert.Greater(Mathf.Abs(heading.x), 0.9f);
        Assert.Less(Mathf.Abs(heading.z), 0.1f);
    }

    [Test]
    public void ZeroHeading_BecomesUnitInXZ()
    {
        Vector3 heading = Step(new float[Resolution * Resolution], Vector3.zero);
        Assert.That(heading.magnitude, Is.InRange(0.99f, 1.01f));
        Assert.Less(Mathf.Abs(heading.y), 1e-4f);
    }

    private Vector3 Step(float[] seed, Vector3 startHeading)
    {
        harness.SeedScalar("trail", seed);
        particles.Get(BuiltinAttributes.Position).SetData(new[] { new Vector3(0.5f, 0f, 0.5f) });
        particles.Get(BuiltinAttributes.Heading).SetData(new[] { startHeading });
        particles.Get(BuiltinAttributes.Velocity).SetData(new[] { Vector3.zero });

        cmd.Clear();
        PhysarumSteerPass pass = new PhysarumSteerPass
        {
            SensorAngle = 90f,
            SensorDistance = 1f,
            TurnAngle = 90f,
            RandomWiggle = 0f,
            MoveSpeed = 15f,
        };
        SimContext context = new SimContext(particles, harness.Context.Fields, new[] { shader }, null);
        context.Cmd = cmd;
        pass.Initialize(context);
        pass.Execute(context, 1f);
        Graphics.ExecuteCommandBuffer(cmd);

        Vector3[] readback = new Vector3[1];
        particles.Get(BuiltinAttributes.Heading).GetData(readback);
        return readback[0];
    }

    private static float[] Spot(int index)
    {
        float[] field = new float[Resolution * Resolution];
        field[index] = 1f;
        return field;
    }
}
