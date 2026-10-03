using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class TeamHeadingSteerTests
{
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const float Cruise = 4f;

    private readonly List<IDisposable> owned = new List<IDisposable>();
    private readonly List<UnityEngine.Object> destroy = new List<UnityEngine.Object>();

    private ParticleSet particles;
    private FieldSet fields;
    private CommandBuffer cmd;
    private GraphicsBuffer teams;
    private HeadingSteerPass classic;
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
        teams = null;
        classic = null;
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
    public void NegativeCruiseAndTurn_ThrowFromSetterAndValidator()
    {
        TeamProfile profile = new TeamProfile();
        ArgumentOutOfRangeException cruiseSetter = Assert.Throws<ArgumentOutOfRangeException>(
            () => profile.Cruise = -1f);
        Assert.AreEqual("value", cruiseSetter.ParamName);
        ArgumentOutOfRangeException turnSetter = Assert.Throws<ArgumentOutOfRangeException>(
            () => profile.Turn = -1f);
        Assert.AreEqual("value", turnSetter.ParamName);
        Assert.DoesNotThrow(() => profile.Cruise = 0f);
        Assert.DoesNotThrow(() => profile.Turn = 0f);

        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        destroy.Add(effect);
        effect.EditorConfigure(DataSourceKind.Cube, 1f, Array.Empty<SimPass>(), null);
        effect.SetTeams(new[] { profile });
        TeamHeadingSteerPass disabled = new TeamHeadingSteerPass { Enabled = false };
        SetPrivate(profile, "cruise", -1f);
        ArgumentOutOfRangeException cruise = Assert.Throws<ArgumentOutOfRangeException>(
            () => SpatialHashValidator.Validate(new SimPass[] { disabled }, effect));
        Assert.AreEqual("cruise", cruise.ParamName);

        SetPrivate(profile, "cruise", 0f);
        SetPrivate(profile, "turn", -2f);
        ArgumentOutOfRangeException turn = Assert.Throws<ArgumentOutOfRangeException>(
            () => SpatialHashValidator.Validate(new SimPass[] { disabled }, effect));
        Assert.AreEqual("turn", turn.ParamName);
    }

    [Test]
    public void EmptyTeams_EnabledSteerThrows_NullEffectDoesNot()
    {
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        destroy.Add(effect);
        effect.EditorConfigure(DataSourceKind.Cube, 1f, new SimPass[] { new IntegratePass() }, null);
        TeamHeadingSteerPass pass = new TeamHeadingSteerPass();
        SimPass[] list = { pass };
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(list, effect));
        StringAssert.Contains("team list", exception.Message);

        pass.Enabled = false;
        Assert.DoesNotThrow(() => SpatialHashValidator.Validate(list, effect));

        pass.Enabled = true;
        Assert.DoesNotThrow(() => SpatialHashValidator.Validate(list, null));
    }

    [Test]
    public void TurnZero_KeepsFlatHeading_AndSetsCruise()
    {
        Vector3[] heading = { Vector3.right };
        Vector3[] velocity = { new Vector3(3f, 1f, -2f) };
        Prepare(heading, velocity, new uint[] { 0u }, cruise: Cruise, turn: 0f);
        Write(heading, velocity, new uint[] { 0u });
        DispatchTeam(0.4f);
        Read(out Vector3[] outHeading, out Vector3[] outVelocity);
        Assert.AreEqual(1f, outHeading[0].x, 1e-5f);
        Assert.AreEqual(0f, outHeading[0].y, 1e-5f);
        Assert.AreEqual(0f, outHeading[0].z, 1e-5f);
        Assert.AreEqual(Cruise, outVelocity[0].x, 1e-5f);
        Assert.AreEqual(0f, outVelocity[0].y, 1e-5f);
        Assert.AreEqual(0f, outVelocity[0].z, 1e-5f);
    }

    [Test]
    public void Isolated_ZeroForce_KeepsHeading_SpeedIsCruise()
    {
        Vector3[] heading = { Vector3.right };
        Vector3[] velocity = { Vector3.zero };
        Prepare(heading, velocity, new uint[] { 0u }, cruise: 3f, turn: 1f);
        Write(heading, velocity, new uint[] { 0u });
        DispatchTeam(0.5f);
        Read(out Vector3[] outHeading, out Vector3[] outVelocity);
        AssertClose(outHeading[0], Vector3.right, 1e-5f);
        Assert.AreEqual(3f, outVelocity[0].magnitude, 1e-5f);
    }

    [Test]
    public void TwoTeams_DifferentCruise_HeadingsStayTogether()
    {
        Vector3[] heading = { Vector3.right, Vector3.right };
        Vector3[] velocity = { new Vector3(1f, 2f, 3f), new Vector3(-4f, 0f, 1f) };
        uint[] teamIds = { 0u, 1u };
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = Slot(2f, 0f);
        slots[1] = Slot(5f, 0f);
        PrepareSlots(heading, velocity, teamIds, slots);
        Write(heading, velocity, teamIds);
        DispatchTeam(0.3f);
        Read(out Vector3[] outHeading, out Vector3[] outVelocity);
        Assert.AreEqual(2f, outVelocity[0].magnitude, 1e-5f);
        Assert.AreEqual(5f, outVelocity[1].magnitude, 1e-5f);
        Assert.AreEqual(outHeading[0].x, outHeading[1].x, 1e-5f);
        Assert.AreEqual(outHeading[0].y, outHeading[1].y, 1e-5f);
        Assert.AreEqual(outHeading[0].z, outHeading[1].z, 1e-5f);
    }

    [Test]
    public void Antiparallel_FlipsAtHalf_AndMatchesHeadingSteer()
    {
        Vector3[] heading = { Vector3.right };
        Vector3[] velocity = { new Vector3(-1f, 0f, 0f) };
        uint[] teamIds = { 0u };
        ExpectAntiparallel(heading, velocity, teamIds, 0.25f, positiveX: true);
        ExpectAntiparallel(heading, velocity, teamIds, 0.5f, positiveX: false);
        ExpectAntiparallel(heading, velocity, teamIds, 0.75f, positiveX: false);
    }

    [Test]
    public void ForceScale_DoesNotChangeHeadingOrSpeed()
    {
        Vector3[] heading = { Vector3.right };
        uint[] teamIds = { 0u };
        Prepare(heading, new[] { new Vector3(0f, 0f, 1f) }, teamIds, Cruise, 1f);
        Write(heading, new[] { new Vector3(0f, 0f, 1f) }, teamIds);
        DispatchTeam(1f);
        Read(out Vector3[] smallHeading, out Vector3[] smallVelocity);

        Write(heading, new[] { new Vector3(0f, 0f, 1000f) }, teamIds);
        DispatchTeam(1f);
        Read(out Vector3[] largeHeading, out Vector3[] largeVelocity);

        AssertClose(smallHeading[0], largeHeading[0], 1e-5f);
        Assert.AreEqual(Cruise, smallVelocity[0].magnitude, 1e-5f);
        Assert.AreEqual(Cruise, largeVelocity[0].magnitude, 1e-5f);
        Assert.Greater(Mathf.Abs(smallVelocity[0].magnitude - 1f), 0.5f);
        Assert.Greater(Mathf.Abs(largeVelocity[0].magnitude - 1000f), 10f);
    }

    [Test]
    public void Parity_DegenerateInputs_MatchHeadingSteer()
    {
        const float turn = 1f;
        AssertParity(new[] { Vector3.right }, new[] { Vector3.zero }, turn, 0.5f);
        AssertParity(new[] { Vector3.zero }, new[] { Vector3.right }, turn, 0.5f);
        AssertParity(new[] { Vector3.right }, new[] { new Vector3(-1f, 0f, 0f) }, turn, 0.5f);
        AssertParity(new[] { Vector3.right }, new[] { new Vector3(0f, 50f, 0f) }, turn, 0.5f);
        AssertParity(new[] { new Vector3(0f, 50f, 0f) }, new[] { Vector3.right }, turn, 0.5f);
        AssertParity(new[] { Vector3.right }, new[] { new Vector3(0f, 0f, 1f) }, turn, 1f);
    }

    [Test]
    public void TeamId100_UsesSlot7Cruise()
    {
        Vector3[] heading = { Vector3.right };
        Vector3[] velocity = { new Vector3(0f, 0f, 1f) };
        uint[] teamIds = { 100u };
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = Slot(2f, 1f);
        slots[7] = Slot(9f, 0f);
        PrepareSlots(heading, velocity, teamIds, slots, directSlot7: true);
        Write(heading, velocity, teamIds);
        DispatchTeam(0.2f);
        Read(out Vector3[] outHeading, out Vector3[] outVelocity);
        Assert.AreEqual(1f, outHeading[0].x, 1e-5f);
        Assert.AreEqual(0f, outHeading[0].y, 1e-5f);
        Assert.AreEqual(0f, outHeading[0].z, 1e-5f);
        Assert.AreEqual(9f, outVelocity[0].magnitude, 1e-5f);
    }

    [Test]
    public void Contract_NameRepeatReadsWritesCategory()
    {
        TeamHeadingSteerPass pass = new TeamHeadingSteerPass();
        PropertyInfo kernelName = typeof(ParticleKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.AreEqual("TeamHeadingSteer", kernelName.GetValue(pass));
        Assert.AreEqual(1, pass.RepeatCount);
        Assert.AreEqual(PassCategory.Dynamics, pass.Category);
        AssertNames(pass.Reads, "heading", "velocity", "teamId");
        AssertNames(pass.Writes, "heading", "velocity");
    }

    [Test]
    public void Initialize_RequiresTeams_NotHash()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        ParticleSet set = new ParticleSet();
        owned.Add(set);
        set.EnsureCapacity(1);
        FieldSet fieldSet = new FieldSet();
        owned.Add(fieldSet);
        ComputeShader boids = Load(BoidPath);
        SimContext context = new SimContext(set, fieldSet, new[] { boids }, null);
        TeamHeadingSteerPass pass = new TeamHeadingSteerPass();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => pass.Initialize(context));
        StringAssert.Contains("team list", exception.Message);

        GraphicsBuffer buffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
        owned.Add(buffer);
        context.Teams = buffer;
        Assert.DoesNotThrow(() => pass.Initialize(context));
        Assert.IsNull(context.SpatialHash);
    }

    private void ExpectAntiparallel(
        Vector3[] heading,
        Vector3[] velocity,
        uint[] teamIds,
        float dt,
        bool positiveX)
    {
        Prepare(heading, velocity, teamIds, Cruise, 1f);
        Write(heading, velocity, teamIds);
        DispatchTeam(dt);
        Read(out Vector3[] teamHeading, out Vector3[] teamVelocity);
        AssertFinite(teamHeading[0]);
        AssertFinite(teamVelocity[0]);
        Assert.AreEqual(1f, teamHeading[0].magnitude, 1e-5f);
        Assert.AreEqual(Cruise, teamVelocity[0].magnitude, 1e-5f);
        if (positiveX)
        {
            Assert.Greater(teamHeading[0].x, 0f);
        }
        else
        {
            Assert.Less(teamHeading[0].x, 0f);
        }

        Write(heading, velocity, teamIds);
        DispatchClassic(dt, 1f, Cruise);
        Read(out Vector3[] classicHeading, out Vector3[] classicVelocity);
        AssertClose(teamHeading[0], classicHeading[0], 1e-6f);
        AssertClose(teamVelocity[0], classicVelocity[0], 1e-6f);
    }

    private void AssertParity(Vector3[] heading, Vector3[] velocity, float turn, float dt)
    {
        uint[] teamIds = { 0u };
        Prepare(heading, velocity, teamIds, Cruise, turn);
        Write(heading, velocity, teamIds);
        DispatchClassic(dt, turn, Cruise);
        Read(out Vector3[] classicHeading, out Vector3[] classicVelocity);
        Write(heading, velocity, teamIds);
        DispatchTeam(dt);
        Read(out Vector3[] teamHeading, out Vector3[] teamVelocity);
        AssertClose(teamHeading[0], classicHeading[0], 1e-6f);
        AssertClose(teamVelocity[0], classicVelocity[0], 1e-6f);
    }

    private void Prepare(Vector3[] heading, Vector3[] velocity, uint[] teamIds, float cruise, float turn)
    {
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = Slot(cruise, turn);
        PrepareSlots(heading, velocity, teamIds, slots, false);
    }

    private void PrepareSlots(
        Vector3[] heading,
        Vector3[] velocity,
        uint[] teamIds,
        TeamParams[] slots,
        bool directSlot7 = false)
    {
        if (particles != null)
        {
            return;
        }

        Assume.That(SystemInfo.supportsComputeShaders);
        count = heading.Length;
        particles = new ParticleSet();
        owned.Add(particles);
        fields = new FieldSet();
        owned.Add(fields);
        particles.EnsureCapacity(count);
        particles.RegisterAttribute(BuiltinAttributes.Heading);
        particles.RegisterAttribute(BuiltinAttributes.Velocity);
        particles.RegisterAttribute(BuiltinAttributes.TeamId);

        teams = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
        owned.Add(teams);
        if (directSlot7)
        {
            TeamParams[] uploaded = new TeamParams[TeamProfile.MaxTeams];
            uploaded[0] = slots[0];
            teams.SetData(uploaded);
            teams.SetData(new[] { slots[7] }, 0, 7, 1);
        }
        else
        {
            teams.SetData(slots);
        }

        cmd = new CommandBuffer { name = "TeamHeadingSteer" };
        owned.Add(new CommandBufferDisposable(cmd));
        ComputeShader dynamics = Load(DynamicsPath);
        ComputeShader boids = Load(BoidPath);
        SimContext context = new SimContext(particles, fields, new[] { dynamics, boids }, null);
        context.Cmd = cmd;
        context.Teams = teams;
        classic = new HeadingSteerPass();
        steer = new TeamHeadingSteerPass();
        classic.Initialize(context);
        steer.Initialize(context);
    }

    private void Write(Vector3[] heading, Vector3[] velocity, uint[] teamIds)
    {
        particles.Get(BuiltinAttributes.Heading).SetData(heading);
        particles.Get(BuiltinAttributes.Velocity).SetData(velocity);
        particles.Get(BuiltinAttributes.TeamId).SetData(teamIds);
    }

    private void DispatchTeam(float dt)
    {
        steer.Execute(Context(), dt);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
    }

    private void DispatchClassic(float dt, float turn, float cruise)
    {
        classic.TurnSpeed = turn;
        classic.CruiseSpeed = cruise;
        classic.Execute(Context(), dt);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
    }

    private SimContext Context()
    {
        ComputeShader dynamics = Load(DynamicsPath);
        ComputeShader boids = Load(BoidPath);
        SimContext context = new SimContext(particles, fields, new[] { dynamics, boids }, null);
        context.Cmd = cmd;
        context.Teams = teams;
        return context;
    }

    private void Read(out Vector3[] heading, out Vector3[] velocity)
    {
        heading = new Vector3[count];
        velocity = new Vector3[count];
        particles.Get(BuiltinAttributes.Heading).GetData(heading);
        particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
    }

    private static TeamParams Slot(float cruise, float turn)
    {
        return new TeamParams
        {
            Motion = new Vector4(cruise, turn, 0f, 0f),
        };
    }

    private static void SetPrivate(TeamProfile profile, string fieldName, float value)
    {
        FieldInfo field = typeof(TeamProfile).GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        field.SetValue(profile, value);
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

    private static void AssertClose(Vector3 gpu, Vector3 cpu, float tolerance)
    {
        Assert.AreEqual(cpu.x, gpu.x, tolerance);
        Assert.AreEqual(cpu.y, gpu.y, tolerance);
        Assert.AreEqual(cpu.z, gpu.z, tolerance);
    }

    private ComputeShader Load(string path)
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
            buffer?.Release();
        }
    }
}

[TestFixture]
public class TeamHeadingSteerWorldTests
{
    private GameObject host;
    private EffectAsset effect;

    [TearDown]
    public void TearDown()
    {
        if (host != null)
        {
            UnityEngine.Object.DestroyImmediate(host);
            host = null;
        }

        if (effect != null)
        {
            UnityEngine.Object.DestroyImmediate(effect);
            effect = null;
        }
    }

    [Test]
    public void Rebuild_SteerOnly_KeepsFourParticles()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        host = new GameObject("TeamHeadingSteer_World");
        host.SetActive(false);

        effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(
            DataSourceKind.Swarm,
            1f,
            new SimPass[] { new TeamHeadingSteerPass() },
            null);
        effect.SetTeams(new[] { new TeamProfile { Cruise = 2f, Turn = 1f } });
        SwarmSource swarm = (SwarmSource)effect.ResolveSource();
        swarm.Spawns = new List<SwarmSource.Spawn>
        {
            new SwarmSource.Spawn
            {
                TeamIndex = 0,
                Count = 4,
                Radius = 0.5f,
                InitialDirection = Vector2.right,
            },
        };

        SimulationWorld world = host.AddComponent<SimulationWorld>();
        SerializedObject worldSo = new SerializedObject(world);
        worldSo.FindProperty("effect").objectReferenceValue = effect;
        worldSo.FindProperty("visualEffect").objectReferenceValue = null;
        SerializedProperty library = worldSo.FindProperty("passLibrary");
        library.arraySize = 1;
        library.GetArrayElementAtIndex(0).objectReferenceValue =
            AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/Shaders/GPU/Passes/BoidsPasses.compute");
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        Assert.DoesNotThrow(() => world.Rebuild());
        FieldInfo particlesField = typeof(SimulationWorld).GetField(
            "particles", BindingFlags.Instance | BindingFlags.NonPublic);
        ParticleSet set = (ParticleSet)particlesField.GetValue(world);
        Assert.AreEqual(4, set.Count);
    }
}
