using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidNeighborForceTests
{
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";

    private readonly List<IDisposable> owned = new List<IDisposable>();
    private readonly List<UnityEngine.Object> destroy = new List<UnityEngine.Object>();

    [TearDown]
    public void TearDown()
    {
        for (int i = owned.Count - 1; i >= 0; i--)
        {
            owned[i]?.Dispose();
        }

        owned.Clear();
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
    public void Layout_Slot0_MatchesUploadBitwise()
    {
        AssumeCompute();
        ComputeShader boids = Load(BoidPath);
        TeamProfile profile = new TeamProfile
        {
            SeparationRadius = 1.25f,
            AlignmentRadius = 2.5f,
            CohesionRadius = 3.75f,
            InterGroupSeparationMultiplier = 4.5f,
            SeparationWeight = -0.25f,
            AlignmentWeight = 0.5f,
            CohesionWeight = 8f,
            Cruise = 6f,
            Turn = 4f,
        };
        GraphicsBuffer teams = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
        owned.Add(teams);
        TeamParams.Upload(teams, new[] { profile });

        GraphicsBuffer read = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, sizeof(float) * 4);
        owned.Add(read);
        CommandBuffer cmd = new CommandBuffer { name = "BoidLayout" };
        owned.Add(new CommandBufferDisposable(cmd));
        int kernel = boids.FindKernel(BoidNeighborForcePass.ReadTeamParamsKernelName);
        cmd.SetComputeBufferParam(boids, kernel, "Teams", teams);
        cmd.SetComputeBufferParam(boids, kernel, "TeamParamsRead", read);
        cmd.DispatchCompute(boids, kernel, 1, 1, 1);
        Graphics.ExecuteCommandBuffer(cmd);

        Vector4[] data = new Vector4[3];
        read.GetData(data);
        AssertBits(data[0].x, profile.SeparationRadius);
        AssertBits(data[0].y, profile.AlignmentRadius);
        AssertBits(data[0].z, profile.CohesionRadius);
        AssertBits(data[0].w, profile.InterGroupSeparationMultiplier);
        AssertBits(data[1].x, profile.SeparationWeight);
        AssertBits(data[1].y, profile.AlignmentWeight);
        AssertBits(data[1].z, profile.CohesionWeight);
        AssertBits(data[1].w, 0f);
        AssertBits(data[2].x, profile.Cruise);
        AssertBits(data[2].y, profile.Turn);
        AssertBits(data[2].z, 0f);
        AssertBits(data[2].w, 0f);
    }

    [Test]
    public void Oracle_TwoTeams_MatchesGpu()
    {
        const int side = 14;
        const float step = 0.8f;
        int count = side * side;
        Vector3[] positions = new Vector3[count];
        Vector3[] headings = new Vector3[count];
        uint[] teamIds = new uint[count];
        for (int z = 0; z < side; z++)
        {
            for (int x = 0; x < side; x++)
            {
                int i = z * side + x;
                positions[i] = new Vector3(-12f + x * step, 0f, -12f + z * step);
                float angle = i * 0.37f;
                headings[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                teamIds[i] = (uint)((x + z) & 1);
            }
        }

        TeamParams[] slots = Slots(
            Profile(1.5f, 1.35f, 1.25f, 2.5f, 1.2f, 0.8f, 0.6f, 6f, 4f),
            Profile(1.5f, 1.35f, 1.25f, 0.35f, 0.4f, 0.2f, 0.9f, 3f, 1f));
        Vector3[] velocity = RunForce(
            positions, headings, teamIds, slots, wrap: true,
            new Vector3(16f, 0f, 16f), 4f, maxNeighbors: 0, countCapHits: false);
        AssertMatchesOracle(velocity, positions, headings, teamIds, slots, wrap: true, new Vector3(16f, 0f, 16f), 4f);
        Assert.AreNotEqual(1f, slots[0].Radii.w);
        Assert.AreNotEqual(teamIds[0], teamIds[1]);
    }

    [Test]
    public void ZeroCap_NeighborInsideRadius_ForceIsNonZero()
    {
        Vector3[] positions = { Vector3.zero, new Vector3(0.4f, 0f, 0f) };
        Vector3[] headings = { Vector3.right, Vector3.right };
        uint[] teamIds = { 0u, 0u };
        TeamParams[] slots = Slots(Profile(1f, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 0f));
        Vector3[] velocity = RunForce(
            positions, headings, teamIds, slots, false,
            new Vector3(8f, 0f, 8f), 2f, 0, false);
        Assert.Greater(velocity[0].sqrMagnitude, 1e-6f);
    }

    [Test]
    public void Seam_Wrap_MatchesMinImageOracle()
    {
        Vector3[] positions = { new Vector3(-7.7f, 0f, 0f), new Vector3(7.7f, 0f, 0f) };
        Vector3[] headings = { Vector3.right, Vector3.left };
        uint[] teamIds = { 0u, 0u };
        TeamParams[] slots = Slots(Profile(1f, 1f, 1f, 1f, 1.2f, 0.5f, 0.4f, 6f, 4f));
        Vector3 extents = new Vector3(8f, 0f, 8f);
        Vector3[] velocity = RunForce(positions, headings, teamIds, slots, true, extents, 2f, 0, false);
        AssertMatchesOracle(velocity, positions, headings, teamIds, slots, true, extents, 2f);
        Assert.Greater(velocity[0].sqrMagnitude, 1e-6f);
    }

    [Test]
    public void TeamId_Above7_UsesSlot7_NotSlot0()
    {
        Vector3[] positions = { Vector3.zero, new Vector3(0.5f, 0f, 0f) };
        Vector3[] headings = { Vector3.right, Vector3.right };
        uint[] teamIds = { 100u, 0u };
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        slots[0] = Params(2f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f);
        slots[7] = Params(2f, 0f, 0f, 1f, 5f, 0f, 0f, 9f, 8f);
        Vector3 extents = new Vector3(8f, 0f, 8f);
        Vector3[] velocity = RunForce(
            positions, headings, teamIds, slots, false, extents, 2f, 0, false, directSlot7: true);
        AssertMatchesOracle(velocity, positions, headings, teamIds, slots, false, extents, 2f);
        Assert.Greater(velocity[0].sqrMagnitude, 1e-6f);

        uint[] asTeam0 = { 0u, 0u };
        Vector2 slot0Force = OracleForce(0, To2(positions), To2(headings), asTeam0, slots, false, Size(extents, 2f));
        Assert.Greater(
            Mathf.Abs(velocity[0].x - slot0Force.x) + Mathf.Abs(velocity[0].z - slot0Force.y),
            1e-3f);
    }

    [Test]
    public void NoWrap_OppositeEdge_HasNoForce_InteriorMatchesOracle()
    {
        Vector3[] far = { new Vector3(-2.9f, 0f, 0f), new Vector3(2.9f, 0f, 0f) };
        Vector3[] headings = { Vector3.right, Vector3.right };
        uint[] teamIds = { 0u, 0u };
        TeamParams[] slots = Slots(Profile(6f, 6f, 6f, 1f, 1f, 0f, 0f, 0f, 0f));
        Vector3[] farVelocity = RunForce(
            far, headings, teamIds, slots, false, new Vector3(3f, 0f, 3f), 2f, 0, false);
        Assert.Less(farVelocity[0].sqrMagnitude, 1e-8f);
        Assert.Less(farVelocity[1].sqrMagnitude, 1e-8f);

        Vector3[] near = { Vector3.zero, new Vector3(0.4f, 0f, 0.1f) };
        Vector3 extents = new Vector3(8f, 0f, 8f);
        Vector3[] nearVelocity = RunForce(near, headings, teamIds, slots, false, extents, 2f, 0, false);
        AssertMatchesOracle(nearVelocity, near, headings, teamIds, slots, false, extents, 2f);
    }

    [Test]
    public void KernelNames_AreFoundByReflection()
    {
        PropertyInfo kernelName = typeof(ParticleKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("BoidNeighborForce", kernelName.GetValue(new BoidNeighborForcePass()));
        FieldInfo read = typeof(BoidNeighborForcePass).GetField(
            "ReadTeamParamsKernelName", BindingFlags.Public | BindingFlags.Static);
        Assert.IsNotNull(read);
        Assert.AreEqual("BoidReadTeamParams", read.GetValue(null));
    }

    [Test]
    public void Cap_Symmetric_MeanIsShorterThanIndividuals()
    {
        const int count = 32;
        Vector3[] positions = new Vector3[count];
        Vector3[] headings = new Vector3[count];
        uint[] teamIds = new uint[count];
        for (int i = 0; i < count; i++)
        {
            float angle = i * Mathf.PI * 2f / count;
            positions[i] = new Vector3(Mathf.Cos(angle) * 0.4f, 0f, Mathf.Sin(angle) * 0.4f);
            headings[i] = Vector3.right;
        }

        TeamParams[] slots = Slots(Profile(2f, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f));
        Vector3[] velocity = RunForce(
            positions, headings, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f, 1, false);
        Vector3 mean = Vector3.zero;
        float lengthSum = 0f;
        for (int i = 0; i < count; i++)
        {
            Assert.IsTrue(float.IsFinite(velocity[i].x) && float.IsFinite(velocity[i].y) && float.IsFinite(velocity[i].z));
            mean += velocity[i];
            lengthSum += velocity[i].magnitude;
        }

        mean /= count;
        float meanLength = lengthSum / count;
        Assert.Greater(meanLength, 1e-4f);
        Assert.Less(mean.magnitude, 0.5f * meanLength);
    }

    [Test]
    public void Dispose_InflightRequest_DoesNotThrowOrChangeWindow()
    {
        BoidNeighborForcePass force = DispatchCap(maxNeighbors: 1, countCapHits: true, particlesInCell: 8, wait: false);
        float before = force.LastCapHitFraction;
        Assert.DoesNotThrow(() => force.Dispose());
        Assert.AreEqual(before, force.LastCapHitFraction);
        Assert.AreEqual(-1f, force.LastCapHitFraction);
    }

    [Test]
    public void CapDisabled_LeavesCounterAndFraction()
    {
        BoidNeighborForcePass force = PrepareCap(maxNeighbors: 1, countCapHits: false, particlesInCell: 8);
        force.CapCounter.SetData(new uint[] { 1u });
        ExecutePrepared();
        uint[] data = new uint[1];
        force.CapCounter.GetData(data);
        Assert.AreEqual(1u, data[0]);
        Assert.AreEqual(-1f, force.LastCapHitFraction);
    }

    [Test]
    public void CapZero_ReadbackFractionIsZero()
    {
        BoidNeighborForcePass force = DispatchCap(maxNeighbors: 0, countCapHits: true, particlesInCell: 2, wait: true);
        Assert.AreEqual(0f, force.LastCapHitFraction, 1e-5f);
    }

    [Test]
    public void CapDense_ReadbackFractionIsNearOne()
    {
        BoidNeighborForcePass force = DispatchCap(maxNeighbors: 1, countCapHits: true, particlesInCell: 24, wait: true);
        Assert.Greater(force.LastCapHitFraction, 0.9f);
        Vector3[] velocity = ReadVelocity();
        for (int i = 0; i < velocity.Length; i++)
        {
            Assert.IsTrue(float.IsFinite(velocity[i].x) && float.IsFinite(velocity[i].y) && float.IsFinite(velocity[i].z));
        }
    }

    [Test]
    public void ResetCapHitWindow_DropsInflightSample()
    {
        BoidNeighborForcePass force = DispatchCap(maxNeighbors: 1, countCapHits: true, particlesInCell: 24, wait: false);
        force.ResetCapHitWindow();
        AsyncGPUReadback.WaitAllRequests();
        Assert.AreEqual(-1f, force.LastCapHitFraction);
    }

    [Test]
    public void EmptyTeams_EnabledForceThrows_DisabledDoesNot()
    {
        EffectAsset effect = ScriptableObject.CreateInstance<EffectAsset>();
        destroy.Add(effect);
        effect.EditorConfigure(DataSourceKind.Cube, 1f, new SimPass[] { new IntegratePass() }, null);
        BuildSpatialHashPass builder = new BuildSpatialHashPass
        {
            Wrap = false,
            Extents = new Vector3(8f, 0f, 8f),
            MinCellSize = 2f,
        };
        BoidNeighborForcePass force = new BoidNeighborForcePass();
        SimPass[] list = { builder, force };
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(list, effect));
        StringAssert.Contains("team list", exception.Message);

        force.Enabled = false;
        Assert.DoesNotThrow(() => SpatialHashValidator.Validate(list, effect));

        force.Enabled = true;
        Assert.DoesNotThrow(() => SpatialHashValidator.Validate(list, null));
    }

    [Test]
    public void PlayRadius_WarnsOncePerNewValue()
    {
        TeamRadiusPlayCheck check = new TeamRadiusPlayCheck();
        TeamProfile profile = new TeamProfile();
        TeamProfile[] teams = { profile };
        const float side = 1f;
        float limit = side * (1f + SpatialHashSet.GridEpsilon);

        profile.SeparationRadius = 6f;
        string first = check.Check(teams, side);
        Assert.IsNotNull(first);
        Assert.IsNull(check.Check(teams, side));

        profile.SeparationRadius = 7f;
        Assert.IsNotNull(check.Check(teams, side));
        profile.SeparationRadius = 6f;
        Assert.IsNotNull(check.Check(teams, side));

        profile.SeparationRadius = limit;
        Assert.IsNull(check.Check(teams, side));
        profile.SeparationRadius = 6f;
        Assert.IsNotNull(check.Check(teams, side));

        profile.SeparationRadius = 0.25f;
        profile.AlignmentRadius = 0.25f;
        profile.CohesionRadius = 0.25f;
        Assert.IsNull(check.Check(teams, side));

        profile.SeparationRadius = 6f;
        profile.AlignmentRadius = 7f;
        string both = check.Check(teams, side);
        Assert.IsNotNull(both);
        StringAssert.Contains("separationRadius", both);
        StringAssert.Contains("alignmentRadius", both);
    }

    [Test]
    public void RepeatCount_StaysOne()
    {
        Assert.AreEqual(1, new BoidNeighborForcePass().RepeatCount);
    }

    private ParticleSet particles;
    private FieldSet fields;
    private CommandBuffer cmd;
    private BuildSpatialHashPass hash;
    private BoidNeighborForcePass force;
    private GraphicsBuffer teamBuffer;
    private int preparedCount;

    private Vector3[] RunForce(
        Vector3[] positions,
        Vector3[] headings,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell,
        int maxNeighbors,
        bool countCapHits,
        bool directSlot7 = false)
    {
        Prepare(positions, headings, teamIds, slots, wrap, extents, minCell, maxNeighbors, countCapHits, directSlot7);
        ExecutePrepared();
        return ReadVelocity();
    }

    private void Prepare(
        Vector3[] positions,
        Vector3[] headings,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell,
        int maxNeighbors,
        bool countCapHits,
        bool directSlot7)
    {
        AssumeCompute();
        preparedCount = positions.Length;
        particles = new ParticleSet();
        owned.Add(particles);
        fields = new FieldSet();
        owned.Add(fields);
        particles.EnsureCapacity(preparedCount);
        particles.RegisterAttribute(BuiltinAttributes.Position);
        particles.RegisterAttribute(BuiltinAttributes.Heading);
        particles.RegisterAttribute(BuiltinAttributes.TeamId);
        particles.RegisterAttribute(BuiltinAttributes.Velocity);
        particles.Get(BuiltinAttributes.Position).SetData(positions);
        particles.Get(BuiltinAttributes.Heading).SetData(headings);
        particles.Get(BuiltinAttributes.TeamId).SetData(teamIds);
        particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[preparedCount]);

        teamBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
        owned.Add(teamBuffer);
        if (directSlot7)
        {
            TeamParams[] uploaded = new TeamParams[TeamProfile.MaxTeams];
            uploaded[0] = slots[0];
            teamBuffer.SetData(uploaded);
            teamBuffer.SetData(new[] { slots[7] }, 0, 7, 1);
        }
        else
        {
            teamBuffer.SetData(slots);
        }

        cmd = new CommandBuffer { name = "BoidForce" };
        owned.Add(new CommandBufferDisposable(cmd));
        ComputeShader dynamics = Load(DynamicsPath);
        ComputeShader hashShader = Load(HashPath);
        ComputeShader boids = Load(BoidPath);
        SimContext context = new SimContext(particles, fields, new[] { dynamics, hashShader, boids }, null);
        context.Cmd = cmd;
        context.Teams = teamBuffer;

        ClearVelocityPass clear = new ClearVelocityPass();
        hash = new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = extents,
            MinCellSize = minCell,
            Wrap = wrap,
        };
        owned.Add(hash);
        force = new BoidNeighborForcePass
        {
            MaxNeighbors = maxNeighbors,
            CountCapHits = countCapHits,
        };
        owned.Add(force);
        clear.Initialize(context);
        hash.Initialize(context);
        force.Initialize(context);
        clear.Execute(context, 0f);
        hash.Execute(context, 0f);
        force.Execute(context, 0f);
    }

    private void ExecutePrepared()
    {
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
    }

    private Vector3[] ReadVelocity()
    {
        Vector3[] velocity = new Vector3[preparedCount];
        particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
        return velocity;
    }

    private BoidNeighborForcePass PrepareCap(int maxNeighbors, bool countCapHits, int particlesInCell)
    {
        Vector3[] positions = new Vector3[particlesInCell];
        Vector3[] headings = new Vector3[particlesInCell];
        uint[] teamIds = new uint[particlesInCell];
        for (int i = 0; i < particlesInCell; i++)
        {
            positions[i] = new Vector3(0.02f * i, 0f, 0.01f * (i % 5));
            headings[i] = Vector3.right;
        }

        TeamParams[] slots = Slots(Profile(2f, 2f, 2f, 1f, 1f, 0f, 0f, 0f, 0f));
        Prepare(positions, headings, teamIds, slots, false, new Vector3(8f, 0f, 8f), 2f, maxNeighbors, countCapHits, false);
        return force;
    }

    private BoidNeighborForcePass DispatchCap(int maxNeighbors, bool countCapHits, int particlesInCell, bool wait)
    {
        BoidNeighborForcePass pass = PrepareCap(maxNeighbors, countCapHits, particlesInCell);
        ExecutePrepared();
        if (wait)
        {
            AsyncGPUReadback.WaitAllRequests();
        }

        return pass;
    }

    private static void AssertMatchesOracle(
        Vector3[] velocity,
        Vector3[] positions,
        Vector3[] headings,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector3 extents,
        float minCell)
    {
        Vector2[] pos = To2(positions);
        Vector2[] dir = To2(headings);
        Vector2 size = Size(extents, minCell);
        for (int i = 0; i < velocity.Length; i++)
        {
            Vector2 expected = OracleForce(i, pos, dir, teamIds, slots, wrap, size);
            AssertClose(velocity[i].x, expected.x);
            AssertClose(velocity[i].y, 0f);
            AssertClose(velocity[i].z, expected.y);
        }
    }

    private static Vector2 OracleForce(
        int index,
        Vector2[] pos,
        Vector2[] dir,
        uint[] teamIds,
        TeamParams[] slots,
        bool wrap,
        Vector2 size)
    {
        uint selfTeam = Math.Min(teamIds[index], 7u);
        TeamParams team = slots[selfTeam];
        float sepR2 = team.Radii.x * team.Radii.x;
        float alignR2 = team.Radii.y * team.Radii.y;
        float cohR2 = team.Radii.z * team.Radii.z;
        float maxR2 = Mathf.Max(sepR2, Mathf.Max(alignR2, cohR2));
        Vector2 sep = Vector2.zero;
        Vector2 align = Vector2.zero;
        Vector2 coh = Vector2.zero;
        int sepN = 0;
        int cohN = 0;
        for (int j = 0; j < pos.Length; j++)
        {
            Vector2 d = MinImage(pos[j] - pos[index], size, wrap);
            float r2 = Vector2.Dot(d, d);
            if (r2 < 1e-6f || r2 >= maxR2)
            {
                continue;
            }

            uint neighborTeam = Math.Min(teamIds[j], 7u);
            bool same = neighborTeam == selfTeam;
            if (r2 < sepR2)
            {
                float mul = same ? 1f : team.Radii.w;
                sep += (-d / (r2 + 1e-3f)) * mul;
                sepN++;
            }

            if (same && r2 < alignR2)
            {
                align += dir[j];
            }

            if (same && r2 < cohR2)
            {
                coh += d;
                cohN++;
            }
        }

        Vector2 force = Vector2.zero;
        if (sepN > 0)
        {
            force += (sep / sepN) * team.Weights.x;
        }

        if (Vector2.Dot(align, align) > 1e-6f)
        {
            force += align.normalized * team.Weights.y;
        }

        if (cohN > 0)
        {
            Vector2 mean = coh / cohN;
            if (Vector2.Dot(mean, mean) > 1e-6f)
            {
                force += mean.normalized * team.Weights.z;
            }
        }

        return force;
    }

    private static Vector2 MinImage(Vector2 d, Vector2 size, bool wrap)
    {
        if (!wrap)
        {
            return d;
        }

        return new Vector2(
            d.x - size.x * Mathf.Round(d.x / size.x),
            d.y - size.y * Mathf.Round(d.y / size.y));
    }

    private static Vector2 Size(Vector3 extents, float minCell)
    {
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, extents, minCell);
        return layout.Size;
    }

    private static Vector2[] To2(Vector3[] values)
    {
        Vector2[] flat = new Vector2[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            flat[i] = new Vector2(values[i].x, values[i].z);
        }

        return flat;
    }

    private static TeamParams[] Slots(params TeamProfile[] profiles)
    {
        TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
        for (int i = 0; i < profiles.Length; i++)
        {
            TeamProfile profile = profiles[i];
            slots[i] = Params(
                profile.SeparationRadius,
                profile.AlignmentRadius,
                profile.CohesionRadius,
                profile.InterGroupSeparationMultiplier,
                profile.SeparationWeight,
                profile.AlignmentWeight,
                profile.CohesionWeight,
                profile.Cruise,
                profile.Turn);
        }

        return slots;
    }

    private static TeamParams Params(
        float sep,
        float align,
        float coh,
        float multiplier,
        float wSep,
        float wAlign,
        float wCoh,
        float cruise,
        float turn)
    {
        return new TeamParams
        {
            Radii = new Vector4(sep, align, coh, multiplier),
            Weights = new Vector4(wSep, wAlign, wCoh, 0f),
            Motion = new Vector4(cruise, turn, 0f, 0f),
        };
    }

    private static TeamProfile Profile(
        float sep,
        float align,
        float coh,
        float multiplier,
        float wSep,
        float wAlign,
        float wCoh,
        float cruise,
        float turn)
    {
        return new TeamProfile
        {
            SeparationRadius = sep,
            AlignmentRadius = align,
            CohesionRadius = coh,
            InterGroupSeparationMultiplier = multiplier,
            SeparationWeight = wSep,
            AlignmentWeight = wAlign,
            CohesionWeight = wCoh,
            Cruise = cruise,
            Turn = turn,
        };
    }

    private static void AssertBits(float gpu, float cpu)
    {
        Assert.AreEqual(BitConverter.SingleToInt32Bits(cpu), BitConverter.SingleToInt32Bits(gpu));
    }

    private static void AssertClose(float gpu, float cpu)
    {
        float tolerance = 1e-4f + 1e-4f * Mathf.Abs(cpu);
        Assert.That(Mathf.Abs(gpu - cpu), Is.LessThanOrEqualTo(tolerance), "gpu " + gpu + " cpu " + cpu);
    }

    private void AssumeCompute()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
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
public class BoidNeighborForceWorldTests
{
    private GameObject host;
    private EffectAsset effect;

    [TearDown]
    public void TearDown()
    {
        if (host != null)
        {
            SimulationWorldTestCleanup.DestroyHost(host);
            host = null;
        }

        if (effect != null)
        {
            UnityEngine.Object.DestroyImmediate(effect);
            effect = null;
        }
    }

    [Test]
    public void Rebuild_WithNeighborForce_KeepsFourParticles()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        host = new GameObject("BoidNeighborForce_World");
        host.SetActive(false);

        BuildSpatialHashPass hash = new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = new Vector3(16f, 0f, 16f),
            MinCellSize = 2f,
            Wrap = true,
        };
        BoxBoundsPass bounds = new BoxBoundsPass
        {
            Center = Vector3.zero,
            Extents = new Vector3(16f, 0f, 16f),
            Behaviour = BoundsBehaviour.Wrap,
        };
        effect = ScriptableObject.CreateInstance<EffectAsset>();
        effect.EditorConfigure(
            DataSourceKind.Swarm,
            1f,
            new SimPass[] { hash, new ClearVelocityPass(), new BoidNeighborForcePass(), bounds },
            null);
        effect.SetTeams(new[]
        {
            new TeamProfile
            {
                SeparationRadius = 1f,
                AlignmentRadius = 1f,
                CohesionRadius = 1f,
            },
        });
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
        library.arraySize = 3;
        library.GetArrayElementAtIndex(0).objectReferenceValue =
            AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/GPU/Passes/DynamicsPasses.compute");
        library.GetArrayElementAtIndex(1).objectReferenceValue =
            AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/GPU/Passes/SpatialHashPasses.compute");
        library.GetArrayElementAtIndex(2).objectReferenceValue =
            AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/GPU/Passes/BoidsPasses.compute");
        worldSo.ApplyModifiedPropertiesWithoutUndo();

        Assert.DoesNotThrow(() => world.Rebuild());
        FieldInfo particlesField = typeof(SimulationWorld).GetField(
            "particles", BindingFlags.Instance | BindingFlags.NonPublic);
        ParticleSet set = (ParticleSet)particlesField.GetValue(world);
        Assert.AreEqual(4, set.Count);
    }
}
