using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class TeamClassicSteerTests
{
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const float Tolerance = 1e-4f;

    private readonly List<IDisposable> owned = new List<IDisposable>();

    private ParticleSet particles;
    private FieldSet fields;
    private CommandBuffer cmd;
    private GraphicsBuffer teams;
    private TeamClassicSteerPass steer;
    private TeamHeadingSteerPass heading;
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
        teams = null;
        steer = null;
        heading = null;
    }

    [Test]
    public void Bounds_RandomForces_StayFiniteInsideTheCorridor()
    {
        const int n = 256;
        var random = new System.Random(1);
        Vector3[] headingIn = new Vector3[n];
        Vector3[] force = new Vector3[n];
        Vector3[] inertial = new Vector3[n];
        uint[] teamIds = new uint[n];
        for (int i = 0; i < n; i++)
        {
            headingIn[i] = Vector3.right;
            force[i] = new Vector3(NextSigned(random), NextSigned(random), NextSigned(random));
            float speed = (float)(random.NextDouble() * 20.0);
            float angle = (float)(random.NextDouble() * Math.PI * 2.0);
            inertial[i] = new Vector3(Mathf.Cos(angle) * speed, NextSigned(random), Mathf.Sin(angle) * speed);
        }

        Prepare(n, Slot(4f, 8f, 12f, 5f));
        float[] steps = { 0.016f, 0.1f, 1f, 10f };
        for (int s = 0; s < steps.Length; s++)
        {
            Write(headingIn, force, inertial, teamIds);
            DispatchClassic(steps[s]);
            Read(out Vector3[] outHeading, out Vector3[] outVelocity, out _);
            for (int i = 0; i < n; i++)
            {
                AssertFinite(outHeading[i]);
                AssertFinite(outVelocity[i]);
                float speed = outVelocity[i].magnitude;
                Assert.GreaterOrEqual(speed, 4f - Tolerance);
                Assert.LessOrEqual(speed, 8f + Tolerance);
                Assert.AreEqual(0f, outVelocity[i].y, Tolerance);
                Assert.AreEqual(1f, outHeading[i].magnitude, Tolerance);
            }
        }
    }

    [Test]
    public void Inertia_ForceThenClear_KeepsTheSteppedSpeed()
    {
        Prepare(1, Slot(4f, 8f, 12f, 5f));
        Write(new[] { Vector3.right }, new[] { new Vector3(0f, 0f, 1f) }, new[] { new Vector3(6f, 0f, 0f) }, new[] { 0u });
        DispatchClassic(0.1f);
        Read(out _, out Vector3[] velocity, out Vector3[] inertial);
        AssertClose(velocity[0], new Vector3(6f, 0f, 0.5f), Tolerance);
        AssertClose(inertial[0], new Vector3(6f, 0f, 0.5f), Tolerance);

        particles.Get(BuiltinAttributes.Velocity).SetData(new[] { Vector3.zero });
        DispatchClassic(0.1f);
        Read(out _, out _, out Vector3[] kept);
        AssertClose(kept[0], inertial[0], Tolerance);
    }

    [Test]
    public void Clamp_MaxForceLimitsTheStep_ZeroDoesNot()
    {
        Prepare(1, Slot(4f, 8f, 12f, 5f));
        Vector3[] headingIn = { Vector3.right };
        Vector3[] force = { new Vector3(0f, 0f, 10f) };
        Vector3[] inertial = { new Vector3(6f, 0f, 0f) };
        uint[] teamIds = { 0u };
        Write(headingIn, force, inertial, teamIds);
        DispatchClassic(0.1f);
        Read(out _, out Vector3[] clamped, out _);
        AssertClose(clamped[0], new Vector3(6f, 0f, 1.2f), Tolerance);

        SetSlot(Slot(4f, 8f, 0f, 5f));
        Write(headingIn, force, inertial, teamIds);
        DispatchClassic(0.1f);
        Read(out _, out Vector3[] open, out _);
        AssertClose(open[0], new Vector3(6f, 0f, 5f), Tolerance);
    }

    [Test]
    public void ForceGain_DoubleGainMatchesDoubleForce()
    {
        Prepare(1, Slot(0f, 1000f, 0f, 2f));
        Vector3[] headingIn = { Vector3.right };
        Vector3[] inertial = { new Vector3(6f, 0f, 0f) };
        uint[] teamIds = { 0u };
        Write(headingIn, new[] { new Vector3(0f, 0f, 1f) }, inertial, teamIds);
        DispatchClassic(0.1f);
        Read(out _, out Vector3[] doubledGain, out _);

        SetSlot(Slot(0f, 1000f, 0f, 1f));
        Write(headingIn, new[] { new Vector3(0f, 0f, 2f) }, inertial, teamIds);
        DispatchClassic(0.1f);
        Read(out _, out Vector3[] doubledForce, out _);
        AssertClose(doubledGain[0], doubledForce[0], Tolerance);
    }

    [Test]
    public void DeltaTime_TwoHalfStepsMatchOneStep()
    {
        Prepare(1, Slot(0f, 1000f, 0f, 1f));
        Vector3[] headingIn = { Vector3.right };
        Vector3[] force = { new Vector3(0f, 0f, 1f) };
        Vector3[] inertial = { new Vector3(6f, 0f, 0f) };
        uint[] teamIds = { 0u };
        Write(headingIn, force, inertial, teamIds);
        DispatchClassic(0.2f);
        Read(out _, out Vector3[] once, out _);

        Write(headingIn, force, inertial, teamIds);
        DispatchClassic(0.1f);
        particles.Get(BuiltinAttributes.Velocity).SetData(force);
        DispatchClassic(0.1f);
        Read(out _, out Vector3[] twice, out _);
        AssertClose(once[0], twice[0], Tolerance);
    }

    [Test]
    public void Start_ZeroInertialAndForce_UsesMidSpeedAlongHeading()
    {
        Prepare(1, Slot(4f, 8f, 12f, 5f));
        Write(new[] { new Vector3(0f, 0f, 1f) }, new[] { Vector3.zero }, new[] { Vector3.zero }, new[] { 0u });
        DispatchClassic(0.1f);
        Read(out Vector3[] outHeading, out Vector3[] velocity, out _);
        AssertClose(velocity[0], new Vector3(0f, 0f, 6f), Tolerance);
        AssertClose(outHeading[0], new Vector3(0f, 0f, 1f), Tolerance);
    }

    [Test]
    public void ZeroForce_KeepsSpeed_AndHeadingMatchesIt()
    {
        Prepare(1, Slot(4f, 8f, 12f, 5f));
        Write(new[] { Vector3.right }, new[] { Vector3.zero }, new[] { new Vector3(3f, 0f, 4f) }, new[] { 0u });
        DispatchClassic(0.1f);
        Read(out Vector3[] outHeading, out Vector3[] velocity, out _);
        AssertClose(velocity[0], new Vector3(3f, 0f, 4f), Tolerance);
        AssertClose(outHeading[0], new Vector3(0.6f, 0f, 0.8f), Tolerance);
    }

    [Test]
    public void Antiparallel_StopsAtMinSpeed_AndKeepsDirection()
    {
        Prepare(1, Slot(4f, 8f, 12f, 5f));
        Vector3[] headingIn = { Vector3.right };
        Vector3[] force = { new Vector3(-1000f, 0f, 0f) };
        Vector3[] inertial = { new Vector3(6f, 0f, 0f) };
        uint[] teamIds = { 0u };
        Write(headingIn, force, inertial, teamIds);
        for (int i = 0; i < 5; i++)
        {
            if (i > 0)
            {
                particles.Get(BuiltinAttributes.Velocity).SetData(force);
            }

            DispatchClassic(0.1f);
        }

        Read(out Vector3[] outHeading, out Vector3[] velocity, out _);
        AssertFinite(outHeading[0]);
        AssertFinite(velocity[0]);
        AssertClose(velocity[0], new Vector3(4f, 0f, 0f), Tolerance);
        Assert.Greater(outHeading[0].x, 0f);
        Assert.AreEqual(0f, outHeading[0].z, Tolerance);
    }

    [Test]
    public void VerticalComponents_AreRemoved()
    {
        Prepare(1, Slot(4f, 8f, 12f, 5f));
        Write(
            new[] { new Vector3(0f, 5f, 1f) },
            new[] { new Vector3(0f, 1000f, 1f) },
            new[] { Vector3.zero },
            new[] { 0u });
        DispatchClassic(0.1f);
        Read(out Vector3[] outHeading, out Vector3[] velocity, out _);
        Assert.AreEqual(0f, velocity[0].y, Tolerance);
        Assert.AreEqual(0f, outHeading[0].y, Tolerance);
        Assert.AreEqual(1f, outHeading[0].magnitude, Tolerance);
        float speed = velocity[0].magnitude;
        Assert.GreaterOrEqual(speed, 4f - Tolerance);
        Assert.LessOrEqual(speed, 8f + Tolerance);
    }

    [Test]
    public void Teams_UseTheirCorridor_AndTeamIdAbove7ReadsSlot7()
    {
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = Slot(4f, 8f, 12f, 5f);
        slots[1] = Slot(10f, 12f, 12f, 5f);
        slots[7] = Slot(1f, 2f, 12f, 5f);
        Prepare(3, slots[0], directSlot7: true, slot7: slots[7], slot1: slots[1]);
        Write(
            new[] { Vector3.right, Vector3.right, Vector3.right },
            new[] { Vector3.zero, Vector3.zero, Vector3.zero },
            new[] { new Vector3(6f, 0f, 0f), new Vector3(6f, 0f, 0f), new Vector3(6f, 0f, 0f) },
            new[] { 0u, 1u, 9u });
        DispatchClassic(0.1f);
        Read(out _, out Vector3[] velocity, out _);
        Assert.AreEqual(6f, velocity[0].magnitude, Tolerance);
        Assert.AreEqual(10f, velocity[1].magnitude, Tolerance);
        Assert.AreEqual(2f, velocity[2].magnitude, Tolerance);
    }

    [Test]
    public void Degenerate_ZeroHeadingForceAndSpeed_IsFiniteAlongX()
    {
        Prepare(1, Slot(4f, 8f, 12f, 5f));
        Write(new[] { Vector3.zero }, new[] { Vector3.zero }, new[] { Vector3.zero }, new[] { 0u });
        DispatchClassic(0.1f);
        Read(out Vector3[] outHeading, out Vector3[] velocity, out _);
        AssertFinite(outHeading[0]);
        AssertFinite(velocity[0]);
        AssertClose(outHeading[0], new Vector3(1f, 0f, 0f), Tolerance);
        Assert.AreEqual(6f, velocity[0].magnitude, Tolerance);
    }

    [Test]
    public void Anchor_ZeroInertialAndLargeGain_MatchesTeamHeadingSteer()
    {
        Prepare(1, Slot(5f, 5f, 0f, 1e6f, 5f, 1f));
        Vector3[] forces =
        {
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 0f, 1f),
            new Vector3(-2f, 0f, 0.5f),
            new Vector3(1f, 1000f, 1f),
            new Vector3(3f, -40f, -4f),
        };
        Vector3[] headings =
        {
            Vector3.right,
            new Vector3(0f, 0f, 1f),
            new Vector3(0f, 2f, 1f),
            new Vector3(-1f, 5f, 0.2f),
            new Vector3(0.2f, -3f, 1f),
        };
        uint[] teamIds = { 0u };
        Vector3[] inertial = { Vector3.zero };
        for (int i = 0; i < forces.Length; i++)
        {
            Vector3[] headingIn = { headings[i] };
            Vector3[] force = { forces[i] };
            Assert.GreaterOrEqual(new Vector2(force[0].x, force[0].z).magnitude, 1f);
            Write(headingIn, force, inertial, teamIds);
            DispatchClassic(1f);
            Read(out Vector3[] classicHeading, out Vector3[] classicVelocity, out _);
            Write(headingIn, force, inertial, teamIds);
            DispatchHeading(1f);
            Read(out Vector3[] teamHeading, out Vector3[] teamVelocity, out _);
            AssertClose(classicHeading[0], teamHeading[0], Tolerance);
            Assert.AreEqual(teamVelocity[0].magnitude, classicVelocity[0].magnitude, Tolerance);
        }
    }

    [Test]
    public void Contract_KernelReadsWritesStrideAndUpload()
    {
        ComputeShader boids = Load(BoidPath);
        Assert.GreaterOrEqual(boids.FindKernel("TeamClassicSteer"), 0);

        FieldInfo library = typeof(M3DDemoTools).GetField(
            "PassLibraryPaths", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(library, "PassLibraryPaths");
        string[] paths = library.GetValue(null) as string[];
        Assert.IsNotNull(paths, "PassLibraryPaths");
        Assert.Greater(paths.Length, 0, "PassLibraryPaths");
        int hits = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(paths[i]);
            Assert.IsNotNull(shader, paths[i]);
            if (shader.HasKernel("TeamClassicSteer"))
            {
                hits++;
            }
        }

        Assert.AreEqual(1, hits);
        string source = File.ReadAllText(Path.Combine(Application.dataPath, "Shaders/GPU/Passes/BoidsPasses.compute"));
        Assert.AreEqual(1, Regex.Matches(source, @"#pragma kernel TeamClassicSteer\b").Count);

        TeamClassicSteerPass pass = new TeamClassicSteerPass();
        Assert.AreEqual(1, pass.RepeatCount);
        Assert.AreEqual(PassCategory.Dynamics, pass.Category);
        Assert.AreEqual("Team Classic Steer", pass.DisplayName);
        AssertNames(pass.Reads, "heading", "velocity", "teamId", "inertialVelocity");
        AssertNames(pass.Writes, "heading", "velocity", "inertialVelocity");
        Assert.AreEqual(64, TeamParams.Stride);

        TeamProfile profile = new TeamProfile
        {
            Cruise = 6f,
            Turn = 4f,
            MinSpeed = 4f,
            MaxSpeed = 8f,
            MaxForce = 12f,
            ForceGain = 5f,
        };
        GraphicsBuffer buffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
        owned.Add(buffer);
        TeamParams.Upload(buffer, new[] { profile });
        TeamParams[] data = new TeamParams[TeamProfile.MaxTeams];
        buffer.GetData(data);
        Assert.AreEqual(new Vector4(4f, 8f, 12f, 5f), data[0].Classic);
        Assert.AreEqual(0f, data[0].Motion.z);
        Assert.AreEqual(0f, data[0].Motion.w);

        ParticleSet set = new ParticleSet();
        owned.Add(set);
        set.EnsureCapacity(1);
        FieldSet fieldSet = new FieldSet();
        owned.Add(fieldSet);
        SimContext context = new SimContext(set, fieldSet, new[] { boids }, null);
        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => pass.Initialize(context));
        StringAssert.Contains("team list", missing.Message);
        context.Teams = buffer;
        Assert.DoesNotThrow(() => pass.Initialize(context));
        Assert.IsNull(context.SpatialHash);
    }

    [Test]
    public void Setters_RejectNegativeClassicFields()
    {
        TeamProfile profile = new TeamProfile();
        AssertNegative(() => profile.MinSpeed = -1f);
        AssertNegative(() => profile.MaxSpeed = -1f);
        AssertNegative(() => profile.MaxForce = -1f);
        AssertNegative(() => profile.ForceGain = -1f);
    }

    private void Prepare(
        int particleCount,
        TeamParams slot0,
        bool directSlot7 = false,
        TeamParams slot7 = default,
        TeamParams slot1 = default)
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
        particles.RegisterAttribute(BuiltinAttributes.Heading);
        particles.RegisterAttribute(BuiltinAttributes.Velocity);
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        particles.RegisterAttribute(BuiltinAttributes.InertialVelocity);

        teams = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
        owned.Add(teams);
        if (directSlot7)
        {
            TeamParams[] uploaded = new TeamParams[TeamProfile.MaxTeams];
            uploaded[0] = slot0;
            uploaded[1] = slot1;
            teams.SetData(uploaded);
            teams.SetData(new[] { slot7 }, 0, 7, 1);
        }
        else
        {
            TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
            slots[0] = slot0;
            teams.SetData(slots);
        }

        cmd = new CommandBuffer { name = "TeamClassicSteer" };
        owned.Add(new CommandBufferDisposable(cmd));
        ComputeShader boids = Load(BoidPath);
        SimContext context = new SimContext(particles, fields, new[] { boids }, null);
        context.Cmd = cmd;
        context.Teams = teams;
        steer = new TeamClassicSteerPass();
        heading = new TeamHeadingSteerPass();
        steer.Initialize(context);
        heading.Initialize(context);
    }

    private void SetSlot(TeamParams slot)
    {
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = slot;
        teams.SetData(slots);
    }

    private void Write(Vector3[] headingIn, Vector3[] velocity, Vector3[] inertial, uint[] teamIds)
    {
        particles.Get(BuiltinAttributes.Heading).SetData(headingIn);
        particles.Get(BuiltinAttributes.Velocity).SetData(velocity);
        particles.Get(BuiltinAttributes.InertialVelocity).SetData(inertial);
        particles.Get(BuiltinAttributes.TeamId).SetData(teamIds);
    }

    private void DispatchClassic(float dt)
    {
        steer.Execute(Context(), dt);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
    }

    private void DispatchHeading(float dt)
    {
        heading.Execute(Context(), dt);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
    }

    private SimContext Context()
    {
        ComputeShader boids = Load(BoidPath);
        SimContext context = new SimContext(particles, fields, new[] { boids }, null);
        context.Cmd = cmd;
        context.Teams = teams;
        return context;
    }

    private void Read(out Vector3[] headingOut, out Vector3[] velocity, out Vector3[] inertial)
    {
        headingOut = new Vector3[count];
        velocity = new Vector3[count];
        inertial = new Vector3[count];
        particles.Get(BuiltinAttributes.Heading).GetData(headingOut);
        particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
        particles.Get(BuiltinAttributes.InertialVelocity).GetData(inertial);
    }

    private static TeamParams Slot(
        float min,
        float max,
        float maxForce,
        float gain,
        float cruise = 0f,
        float turn = 0f)
    {
        return new TeamParams
        {
            Motion = new Vector4(cruise, turn, 0f, 0f),
            Classic = new Vector4(min, max, maxForce, gain),
        };
    }

    private static float NextSigned(System.Random random)
    {
        return (float)(random.NextDouble() * 2000.0 - 1000.0);
    }

    private ComputeShader Load(string path)
    {
        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
        Assert.IsNotNull(shader, path);
        return shader;
    }

    private static void AssertNames(IReadOnlyList<AttributeId> ids, params string[] names)
    {
        Assert.AreEqual(names.Length, ids.Count);
        for (int i = 0; i < names.Length; i++)
        {
            Assert.AreEqual(names[i], ids[i].Name);
        }
    }

    private static void AssertFinite(Vector3 value)
    {
        Assert.IsFalse(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z));
        Assert.IsFalse(float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
    }

    private static void AssertClose(Vector3 gpu, Vector3 expected, float tolerance)
    {
        Assert.AreEqual(expected.x, gpu.x, tolerance);
        Assert.AreEqual(expected.y, gpu.y, tolerance);
        Assert.AreEqual(expected.z, gpu.z, tolerance);
    }

    private static void AssertNegative(TestDelegate assign)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(assign);
        Assert.AreEqual("value", exception.ParamName);
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
            buffer?.Release();
        }
    }
}
