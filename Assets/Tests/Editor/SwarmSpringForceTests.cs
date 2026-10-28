using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class SwarmSpringForceTests
{
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const float UnitTolerance = 1e-6f;
    private const string SharedListWarning =
        "SimulationWorld: more than one swarm point pass (Swarm Point Force and Swarm Spring Force apply the same point list). " +
        "Each pass applies every point; extra passes repeat the sum.";

    private readonly List<IDisposable> owned = new List<IDisposable>();
    private readonly List<UnityEngine.Object> destroy = new List<UnityEngine.Object>();

    private ParticleSet particles;
    private FieldSet fields;
    private CommandBuffer cmd;
    private GraphicsBuffer pointBuffer;
    private GraphicsBuffer teams;
    private SwarmSpringForcePass pointPass;
    private TeamHeadingSteerPass steer;
    private int count;

    [TearDown]
    public void TearDown()
    {
        for (int i = owned.Count - 1; i >= 0; i--)
        {
            owned[i]?.Dispose();
        }

        owned.Clear();
        particles = null;
        fields = null;
        cmd = null;
        pointBuffer = null;
        teams = null;
        pointPass = null;
        steer = null;
        for (int i = 0; i < destroy.Count; i++)
        {
            if (destroy[i] != null)
            {
                UnityEngine.Object.DestroyImmediate(destroy[i]);
            }
        }

        destroy.Clear();
    }

    [Test]
    public void DeltaTime_DoesNotChangeTheAddition()
    {
        Vector3[] position = { new Vector3(1f, 4f, 2f) };
        Vector3[] velocity = { new Vector3(0.25f, -3f, 0.5f) };
        SwarmPoint point = Point(new Vector2(4f, 6f), 0.6f, 0f, 0f);
        Prepare(position.Length);
        Vector3[] slow = Run(position, velocity, new[] { point }, 0.016f, null);
        Vector3[] fast = Run(position, velocity, new[] { point }, 1.5f, null);
        AssertBits(slow[0], fast[0]);
    }

    [Test]
    public void Weight_ScalesSpring_AndFlipsSign()
    {
        Vector3[] position = { Vector3.zero };
        Vector3[] velocity = { Vector3.zero };
        Prepare(1);
        Vector3[] pull = Run(position, velocity, new[] { Point(new Vector2(3f, 0f), 0.6f, 0f, 0f) }, 0.2f, null);
        AssertClose(pull[0], new Vector3(1.8f, 0f, 0f), UnitTolerance);

        Vector3[] push = Run(position, velocity, new[] { Point(new Vector2(3f, 0f), -0.6f, 0f, 0f) }, 0.2f, null);
        AssertClose(push[0], new Vector3(-1.8f, 0f, 0f), UnitTolerance);
    }

    [Test]
    public void Wrap_UsesShortestVector_WrapOffUsesStraightDifference()
    {
        Vector3[] position = { new Vector3(-7f, 2f, 0f) };
        Vector3[] velocity = { Vector3.zero };
        SwarmPoint point = Point(new Vector2(7f, 0f), 0.6f, 0f, 0f);
        Prepare(1);
        SpatialHashSet wrapped = Hash(new Vector3(8f, 0f, 8f), 2f, true);
        Vector3[] throughSeam = Run(position, velocity, new[] { point }, 0.1f, wrapped);
        AssertClose(throughSeam[0], new Vector3(-1.2f, 0f, 0f), UnitTolerance);

        SpatialHashSet open = Hash(new Vector3(8f, 0f, 8f), 2f, false);
        Vector3[] straight = Run(position, velocity, new[] { point }, 0.1f, open);
        AssertClose(straight[0], new Vector3(8.4f, 0f, 0f), UnitTolerance);
    }

    [Test]
    public void Profile_PlateauFalloffAndUnbounded()
    {
        Prepare(1);
        AssertClose(ForceAt(4f, 10f, 0.5f)[0], new Vector3(2.4f, 0f, 0f), UnitTolerance);
        AssertClose(ForceAt(7.5f, 10f, 0.5f)[0], new Vector3(2.25f, 0f, 0f), 1e-4f);
        AssertClose(ForceAt(10f, 10f, 0.5f)[0], Vector3.zero, UnitTolerance);
        AssertClose(ForceAt(12f, 10f, 0.5f)[0], Vector3.zero, UnitTolerance);
        AssertClose(ForceAt(9.9f, 10f, 0f)[0], new Vector3(5.94f, 0f, 0f), UnitTolerance);
        AssertClose(ForceAt(10.1f, 10f, 0f)[0], Vector3.zero, UnitTolerance);
        AssertClose(ForceAt(1000f, 0f, 0f)[0], new Vector3(600f, 0f, 0f), UnitTolerance);
    }

    [Test]
    public void Sum_AddsPoints_AndDropsTheSeventeenth()
    {
        Prepare(1);
        SwarmPoint[] two =
        {
            Point(new Vector2(3f, 0f), 0.6f, 0f, 0f),
            Point(new Vector2(0f, 4f), 0.6f, 0f, 0f),
        };
        Vector3[] summed = Run(new[] { Vector3.zero }, new[] { Vector3.zero }, two, 0.1f, null);
        AssertClose(summed[0], new Vector3(1.8f, 0f, 2.4f), UnitTolerance);

        SwarmPoint[] many = new SwarmPoint[17];
        for (int i = 0; i < many.Length; i++)
        {
            many[i] = Point(new Vector2(1f, 0f), i == 16 ? 9f : 0.1f, 0f, 0f);
        }

        Vector4[] scratch = new Vector4[SwarmPointParams.Float4Count];
        SwarmPointParams.Upload(pointBuffer, many, scratch);
        Vector4[] read = new Vector4[SwarmPointParams.Float4Count];
        pointBuffer.GetData(read);
        Assert.AreEqual(0.1f, read[0].z, UnitTolerance);
        Assert.AreEqual(0.1f, read[30].z, UnitTolerance);
        Assert.AreNotEqual(9f, read[0].z);

        Vector3[] gpu = Run(new[] { Vector3.zero }, new[] { Vector3.zero }, many, 0.1f, null);
        AssertClose(gpu[0], new Vector3(1.6f, 0f, 0f), 1e-5f);
    }

    [Test]
    public void VerticalPosition_DoesNotChangeVelocityY()
    {
        Prepare(1);
        Vector3[] position = { new Vector3(0f, 7f, 4f) };
        Vector3[] velocity = { new Vector3(1f, 2f, 3f) };
        Vector3[] result = Run(position, velocity, new[] { Point(new Vector2(0f, 0f), 0.6f, 0f, 0f) }, 0.3f, null);
        Assert.AreEqual(2f, result[0].y, UnitTolerance);
        AssertClose(result[0], new Vector3(1f, 2f, 0.6f), UnitTolerance);
    }

    [Test]
    public void AddsToExistingVelocity()
    {
        Prepare(1);
        Vector3[] result = Run(
            new[] { Vector3.zero },
            new[] { Vector3.right },
            new[] { Point(new Vector2(4f, 0f), 0.6f, 0f, 0f) },
            0.2f,
            null);
        AssertClose(result[0], new Vector3(3.4f, 0f, 0f), UnitTolerance);
    }

    [Test]
    public void CoincidentPoint_LeavesVelocity_AndIsFinite()
    {
        Prepare(1);
        Vector3[] velocity = { new Vector3(1f, 2f, 3f) };
        Vector3[] result = Run(
            new[] { new Vector3(3f, 5f, 9f) },
            velocity,
            new[] { Point(new Vector2(3f, 9f), 1.5f, 0f, 0f) },
            0.2f,
            null);
        AssertClose(result[0], velocity[0], UnitTolerance);
        AssertFinite(result[0]);
    }

    [Test]
    public void ZeroPoints_DoesNotDispatch()
    {
        Prepare(1);
        particles.Get(BuiltinAttributes.Position).SetData(new[] { Vector3.zero });
        particles.Get(BuiltinAttributes.Velocity).SetData(new[] { Vector3.right });
        SimContext context = Context(null);
        context.SwarmPoints = pointBuffer;
        context.SwarmPointCount = 0;
        pointPass.Execute(context, 0.2f);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        Assert.IsFalse(pointPass.LastExecuteDispatched);

        context.SwarmPoints = null;
        context.SwarmPointCount = 4;
        pointPass.Execute(context, 0.2f);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        Assert.IsFalse(pointPass.LastExecuteDispatched);

        Vector3[] velocity = new Vector3[1];
        particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
        AssertClose(velocity[0], Vector3.right, UnitTolerance);
    }

    [Test]
    public void Heading_TurnsTowardThePoint()
    {
        PrepareSteer();
        Vector3 toward = Turn(5f, 1f);
        Assert.Greater(toward.z, 0f);

        Vector3 none = Turn(0f, 1f);
        Assert.AreEqual(1f, none.x, 1e-4f);
        Assert.AreEqual(0f, none.z, 1e-4f);

        Vector3 away = Turn(-5f, 1f);
        Assert.Less(away.z, 0f);
    }

    [Test]
    public void Validator_AfterHeading_Warns_AndIsQuietWhenOrdered()
    {
        SimPass[] ordered =
        {
            new ClearVelocityPass(),
            new SwarmSpringForcePass(),
            new TeamHeadingSteerPass(),
        };
        Assert.AreEqual(0, SwarmPointValidator.Validate(ordered).Count);

        SimPass[] late =
        {
            new ClearVelocityPass(),
            new TeamHeadingSteerPass(),
            new SwarmSpringForcePass(),
        };
        IReadOnlyList<string> warnings = SwarmPointValidator.Validate(late);
        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual(AfterSteer("Team Heading Steer"), warnings[0]);
    }

    [Test]
    public void Validator_AfterClassic_Warns_AndIsQuietWhenOrdered()
    {
        SimPass[] ordered =
        {
            new ClearVelocityPass(),
            new SwarmSpringForcePass(),
            new TeamClassicSteerPass(),
        };
        Assert.AreEqual(0, SwarmPointValidator.Validate(ordered).Count);

        SimPass[] late =
        {
            new ClearVelocityPass(),
            new TeamClassicSteerPass(),
            new SwarmSpringForcePass(),
        };
        IReadOnlyList<string> warnings = SwarmPointValidator.Validate(late);
        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual(AfterSteer("Team Classic Steer"), warnings[0]);
    }

    [Test]
    public void Validator_BeforeClear_Warns()
    {
        SimPass[] early =
        {
            new SwarmSpringForcePass(),
            new ClearVelocityPass(),
            new TeamHeadingSteerPass(),
        };
        IReadOnlyList<string> warnings = SwarmPointValidator.Validate(early);
        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual(
            "SimulationWorld: Swarm Spring Force stands before Clear Velocity. " +
            "Clear Velocity wipes the point contribution before the heading reads it.",
            warnings[0]);
    }

    [Test]
    public void Validator_TwoSprings_UsesTheSharedListText()
    {
        SimPass[] twice =
        {
            new ClearVelocityPass(),
            new SwarmSpringForcePass(),
            new SwarmSpringForcePass(),
            new TeamHeadingSteerPass(),
        };
        IReadOnlyList<string> warnings = SwarmPointValidator.Validate(twice);
        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual(SharedListWarning, warnings[0]);
    }

    [Test]
    public void Validator_MixedPointPasses_UsesTheSharedListText()
    {
        SimPass[] mixed =
        {
            new ClearVelocityPass(),
            new SwarmPointForcePass(),
            new SwarmSpringForcePass(),
            new TeamHeadingSteerPass(),
        };
        IReadOnlyList<string> warnings = SwarmPointValidator.Validate(mixed);
        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual(SharedListWarning, warnings[0]);
    }

    private Vector3[] ForceAt(float distance, float radius, float edge)
    {
        return Run(
            new[] { Vector3.zero },
            new[] { Vector3.zero },
            new[] { Point(new Vector2(distance, 0f), 0.6f, radius, edge) },
            0.2f,
            null);
    }

    private Vector3 Turn(float weight, float dt)
    {
        Vector3[] position = { Vector3.zero };
        Vector3[] velocity = { Vector3.zero };
        Vector3[] heading = { Vector3.right };
        particles.Get(BuiltinAttributes.Position).SetData(position);
        particles.Get(BuiltinAttributes.Velocity).SetData(velocity);
        particles.Get(BuiltinAttributes.Heading).SetData(heading);
        particles.Get(BuiltinAttributes.TeamId).SetData(new uint[] { 0u });
        SwarmPointParams.Upload(pointBuffer, new[] { Point(new Vector2(0f, 10f), weight, 0f, 0f) });
        SimContext context = Context(null);
        context.SwarmPoints = pointBuffer;
        context.SwarmPointCount = 1;
        context.Teams = teams;
        pointPass.Execute(context, dt);
        steer.Execute(context, dt);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        Vector3[] outHeading = new Vector3[1];
        particles.Get(BuiltinAttributes.Heading).GetData(outHeading);
        return outHeading[0];
    }

    private void PrepareSteer()
    {
        Prepare(1);
        particles.RegisterAttribute(BuiltinAttributes.Heading);
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        teams = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
        owned.Add(teams);
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = new TeamParams
        {
            Motion = new Vector4(4f, 4f, 0f, 0f),
        };
        teams.SetData(slots);
        ComputeShader boids = Load(BoidPath);
        SimContext context = new SimContext(particles, fields, new[] { boids }, null);
        context.Cmd = cmd;
        context.Teams = teams;
        steer = new TeamHeadingSteerPass();
        steer.Initialize(context);
    }

    private Vector3[] Run(
        Vector3[] position,
        Vector3[] velocity,
        IReadOnlyList<SwarmPoint> points,
        float dt,
        SpatialHashSet hash)
    {
        particles.Get(BuiltinAttributes.Position).SetData(position);
        particles.Get(BuiltinAttributes.Velocity).SetData(velocity);
        SwarmPointParams.Upload(pointBuffer, points);
        SimContext context = Context(hash);
        context.SwarmPoints = pointBuffer;
        int live = points.Count < SwarmPoint.MaxPoints ? points.Count : SwarmPoint.MaxPoints;
        context.SwarmPointCount = live;
        pointPass.Execute(context, dt);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        Assert.IsTrue(pointPass.LastExecuteDispatched);
        Vector3[] result = new Vector3[count];
        particles.Get(BuiltinAttributes.Velocity).GetData(result);
        return result;
    }

    private void Prepare(int particleCount)
    {
        if (particles != null)
        {
            return;
        }

        Assume.That(SystemInfo.supportsComputeShaders);
        count = particleCount;
        particles = new ParticleSet();
        owned.Add(particles);
        fields = new FieldSet();
        owned.Add(fields);
        particles.EnsureCapacity(count);
        particles.RegisterAttribute(BuiltinAttributes.Position);
        particles.RegisterAttribute(BuiltinAttributes.Velocity);
        pointBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, SwarmPointParams.Float4Count, sizeof(float) * 4);
        owned.Add(pointBuffer);
        cmd = new CommandBuffer { name = "SwarmPointForce" };
        owned.Add(new CommandBufferDisposable(cmd));
        ComputeShader boids = Load(BoidPath);
        SimContext context = new SimContext(particles, fields, new[] { boids }, null);
        context.Cmd = cmd;
        pointPass = new SwarmSpringForcePass();
        pointPass.Initialize(context);
    }

    private SimContext Context(SpatialHashSet hash)
    {
        ComputeShader boids = Load(BoidPath);
        SimContext context = new SimContext(particles, fields, new[] { boids }, null);
        context.Cmd = cmd;
        context.SpatialHash = hash;
        return context;
    }

    private SpatialHashSet Hash(Vector3 extents, float minCell, bool wrap)
    {
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, extents, minCell);
        SpatialHashSet hash = new SpatialHashSet(layout, wrap, 1);
        owned.Add(hash);
        return hash;
    }

    private static SwarmPoint Point(Vector2 xz, float weight, float radius, float edge)
    {
        return new SwarmPoint
        {
            PositionXZ = xz,
            Weight = weight,
            Radius = radius,
            Edge = edge,
        };
    }

    private static string AfterSteer(string steerLabel)
    {
        return "SimulationWorld: Swarm Spring Force stands after " + steerLabel + ". " +
            "The heading will not turn toward the point.";
    }

    private static void AssertClose(Vector3 gpu, Vector3 expected, float tolerance)
    {
        Assert.AreEqual(expected.x, gpu.x, tolerance);
        Assert.AreEqual(expected.y, gpu.y, tolerance);
        Assert.AreEqual(expected.z, gpu.z, tolerance);
    }

    private static void AssertBits(Vector3 a, Vector3 b)
    {
        Assert.AreEqual(BitConverter.SingleToInt32Bits(a.x), BitConverter.SingleToInt32Bits(b.x));
        Assert.AreEqual(BitConverter.SingleToInt32Bits(a.y), BitConverter.SingleToInt32Bits(b.y));
        Assert.AreEqual(BitConverter.SingleToInt32Bits(a.z), BitConverter.SingleToInt32Bits(b.z));
    }

    private static void AssertFinite(Vector3 value)
    {
        Assert.IsFalse(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z));
        Assert.IsFalse(float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
    }

    private static ComputeShader Load(string path)
    {
        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
        Assume.That(shader != null, path);
        return shader;
    }

    private sealed class CommandBufferDisposable : IDisposable
    {
        private readonly CommandBuffer buffer;

        public CommandBufferDisposable(CommandBuffer buffer)
        {
            this.buffer = buffer;
        }

        public void Dispose()
        {
            buffer.Dispose();
        }
    }
}
