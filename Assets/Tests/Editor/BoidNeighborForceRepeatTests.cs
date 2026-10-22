using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidNeighborForceRepeatTests
{
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";

    private readonly List<IDisposable> owned = new List<IDisposable>();

    [TearDown]
    public void TearDown()
    {
        for (int i = owned.Count - 1; i >= 0; i--)
        {
            owned[i]?.Dispose();
        }

        owned.Clear();
    }

    [Test]
    public void RepeatFour_MatchesOnce_OnOneHash_DirectAndThroughWorld()
    {
        Prepared prepared = Prepare(ClosePair(), walkMode: 1, wrap: false, maxNeighbors: 48, countCapHits: false);
        Vector3[] once = Run(prepared, 1);
        Vector3[] repeated = Rerun(prepared, 4);
        Vector3[] throughWorld = RerunThroughWorld(prepared, 4);

        Assert.AreEqual(1, prepared.Force.RepeatCount);
        AssertBits(repeated, once);
        AssertBits(throughWorld, once);
        AssertNonZero(once);
    }

    [Test]
    public void RepeatFour_MatchesOnce_ForWalksCapTorusAndOddCount()
    {
        AssertRepeatMatches(0, wrap: false, maxNeighbors: 48, countCapHits: false, torus: false);
        AssertRepeatMatches(1, wrap: false, maxNeighbors: 48, countCapHits: false, torus: false);
        AssertRepeatMatches(2, wrap: false, maxNeighbors: 48, countCapHits: false, torus: false);
        AssertRepeatMatches(1, wrap: false, maxNeighbors: 48, countCapHits: true, torus: false);
        AssertRepeatMatches(1, wrap: false, maxNeighbors: 0, countCapHits: false, torus: false);
        AssertRepeatMatches(1, wrap: true, maxNeighbors: 48, countCapHits: false, torus: true);
    }

    [Test]
    public void CapHits_AreNotMultipliedByRepeats()
    {
        Prepared prepared = Prepare(CapTriple(), walkMode: 1, wrap: false, maxNeighbors: 1, countCapHits: true);
        Run(prepared, 1);
        uint once = ReadCap(prepared);
        Rerun(prepared, 4);
        uint repeated = ReadCap(prepared);

        Assert.That(once, Is.GreaterThan(0u));
        Assert.That(repeated, Is.EqualTo(once));
    }

    [Test]
    public void Scratch_IsAbsentAtOne_AndNonZeroAfterFour()
    {
        Prepared prepared = Prepare(ClosePair(), walkMode: 1, wrap: false, maxNeighbors: 48, countCapHits: false);
        Run(prepared, 1);
        Assert.IsNull(prepared.Force.ForceScratch);

        Rerun(prepared, 4);
        GraphicsBuffer scratch = prepared.Force.ForceScratch;
        Assert.IsNotNull(scratch);
        var values = new Vector3[prepared.Count];
        scratch.GetData(values);
        AssertNonZero(values);
    }

    [Test]
    public void Scratch_IsRecreatedWhenParticleCountChanges()
    {
        Prepared first = Prepare(ClosePair(), walkMode: 1, wrap: false, maxNeighbors: 48, countCapHits: false);
        first.Force.ForceRepeatCount = 4;
        first.Force.Execute(first.Context, 0f);
        Graphics.ExecuteCommandBuffer(first.Cmd);
        GraphicsBuffer before = first.Force.ForceScratch;
        Assert.AreEqual(3, before.count);

        Prepared second = Prepare(SixParticles(), walkMode: 1, wrap: false, maxNeighbors: 48, countCapHits: false, first.Force);
        second.Force.ForceRepeatCount = 4;
        second.Force.Execute(second.Context, 0f);
        Graphics.ExecuteCommandBuffer(second.Cmd);
        Assert.AreEqual(6, second.Force.ForceScratch.count);
        Assert.AreNotSame(before, second.Force.ForceScratch);
    }

    [Test]
    public void SwitchOneToFourToOne_RestoresTheSingleResult()
    {
        Prepared prepared = Prepare(ClosePair(), walkMode: 1, wrap: false, maxNeighbors: 48, countCapHits: false);
        Vector3[] before = Run(prepared, 1);
        Rerun(prepared, 4);
        Vector3[] after = Rerun(prepared, 1);
        AssertBits(after, before);
    }

    private void AssertRepeatMatches(int walkMode, bool wrap, int maxNeighbors, bool countCapHits, bool torus)
    {
        Layout layout = torus ? TorusSeam() : ClosePair();
        Prepared prepared = Prepare(layout, walkMode, wrap, maxNeighbors, countCapHits);
        Vector3[] once = Run(prepared, 1);
        Vector3[] repeated = Rerun(prepared, 4);
        Assert.AreEqual(1, prepared.Force.RepeatCount, "walk " + walkMode);
        AssertBits(repeated, once);
        AssertNonZero(once);
        Assert.AreNotEqual(0, prepared.Count % 64);
    }

    private Vector3[] Run(Prepared prepared, int repeats)
    {
        prepared.Force.ForceRepeatCount = repeats;
        prepared.Force.Execute(prepared.Context, 0f);
        Graphics.ExecuteCommandBuffer(prepared.Cmd);
        return ReadVelocity(prepared);
    }

    private Vector3[] Rerun(Prepared prepared, int repeats)
    {
        prepared.Cmd.Clear();
        prepared.Particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[prepared.Count]);
        prepared.Force.ForceRepeatCount = repeats;
        prepared.Force.Execute(prepared.Context, 0f);
        Graphics.ExecuteCommandBuffer(prepared.Cmd);
        return ReadVelocity(prepared);
    }

    private Vector3[] RerunThroughWorld(Prepared prepared, int repeats)
    {
        prepared.Cmd.Clear();
        prepared.Particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[prepared.Count]);
        prepared.Force.ForceRepeatCount = repeats;
        Assert.AreEqual(1, prepared.Force.RepeatCount);
        for (int repeat = 0; repeat < prepared.Force.RepeatCount; repeat++)
        {
            prepared.Force.Execute(prepared.Context, 0f);
        }

        Graphics.ExecuteCommandBuffer(prepared.Cmd);
        return ReadVelocity(prepared);
    }

    private Prepared Prepare(
        Layout layout,
        int walkMode,
        bool wrap,
        int maxNeighbors,
        bool countCapHits,
        BoidNeighborForcePass reuse = null)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        var prepared = new Prepared { Count = layout.Positions.Length };
        prepared.Particles = new ParticleSet();
        owned.Add(prepared.Particles);
        var fields = new FieldSet();
        owned.Add(fields);
        prepared.Particles.EnsureCapacity(prepared.Count);
        prepared.Particles.RegisterAttribute(BuiltinAttributes.Position);
        prepared.Particles.RegisterAttribute(BuiltinAttributes.Heading);
        prepared.Particles.RegisterAttribute(BuiltinAttributes.TeamId);
        prepared.Particles.RegisterAttribute(BuiltinAttributes.Velocity);
        prepared.Particles.Get(BuiltinAttributes.Position).SetData(layout.Positions);
        prepared.Particles.Get(BuiltinAttributes.Heading).SetData(layout.Headings);
        prepared.Particles.Get(BuiltinAttributes.TeamId).SetData(layout.Teams);
        prepared.Particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[prepared.Count]);

        var teamBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
        owned.Add(teamBuffer);
        teamBuffer.SetData(Slots(Profile(2f, 2f, 2f, 1f, 1.2f, 0.8f, 0.6f, 1f, 2f), Profile(2f, 2f, 2f, 1f, 1.2f, 0.8f, 0.6f, 1f, 2f)));

        prepared.Cmd = new CommandBuffer { name = "ForceRepeat" };
        owned.Add(prepared.Cmd);
        ComputeShader dynamics = Load(DynamicsPath);
        ComputeShader hashShader = Load(HashPath);
        ComputeShader boids = Load(BoidPath);
        prepared.Context = new SimContext(prepared.Particles, fields, new[] { dynamics, hashShader, boids }, null);
        prepared.Context.Cmd = prepared.Cmd;
        prepared.Context.Teams = teamBuffer;
        prepared.Context.TeamMaxRadius = 2f;

        var hash = new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = layout.Extents,
            MinCellSize = 3f,
            Wrap = wrap,
        };
        owned.Add(hash);
        prepared.Force = reuse ?? new BoidNeighborForcePass();
        if (reuse == null)
        {
            owned.Add(prepared.Force);
        }

        prepared.Force.MaxNeighbors = maxNeighbors;
        prepared.Force.CountCapHits = countCapHits;
        var clear = new ClearVelocityPass();
        clear.Initialize(prepared.Context);
        hash.Initialize(prepared.Context);
        prepared.Force.Initialize(prepared.Context);
        prepared.Force.WalkMode = walkMode;
        clear.Execute(prepared.Context, 0f);
        hash.Execute(prepared.Context, 0f);
        return prepared;
    }

    private static Layout ClosePair()
    {
        return new Layout
        {
            Positions = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0.5f, 0f, 0f),
                new Vector3(30f, 0f, 0f),
            },
            Headings = new[] { Vector3.right, Vector3.forward, Vector3.right },
            Teams = new uint[] { 0, 1, 0 },
            Extents = new Vector3(40f, 0f, 40f),
        };
    }

    private static Layout TorusSeam()
    {
        return new Layout
        {
            Positions = new[]
            {
                new Vector3(-7.2f, 0f, 0f),
                new Vector3(7.2f, 0f, 0f),
                new Vector3(0f, 0f, 30f),
            },
            Headings = new[] { Vector3.right, Vector3.left, Vector3.right },
            Teams = new uint[] { 0, 1, 0 },
            Extents = new Vector3(8f, 0f, 8f),
        };
    }

    private static Layout CapTriple()
    {
        return new Layout
        {
            Positions = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0.4f, 0f, 0f),
                new Vector3(0.2f, 0f, 0.3f),
            },
            Headings = new[] { Vector3.right, Vector3.forward, Vector3.right },
            Teams = new uint[] { 0, 1, 0 },
            Extents = new Vector3(40f, 0f, 40f),
        };
    }

    private static uint ReadCap(Prepared prepared)
    {
        var hits = new uint[1];
        prepared.Force.CapCounter.GetData(hits);
        return hits[0];
    }

    private static Layout SixParticles()
    {
        var positions = new Vector3[6];
        var headings = new Vector3[6];
        var teams = new uint[6];
        for (int i = 0; i < 6; i++)
        {
            positions[i] = new Vector3(i * 0.4f, 0f, 0f);
            headings[i] = Vector3.right;
            teams[i] = (uint)(i % 2);
        }

        return new Layout
        {
            Positions = positions,
            Headings = headings,
            Teams = teams,
            Extents = new Vector3(40f, 0f, 40f),
        };
    }

    private static Vector3[] ReadVelocity(Prepared prepared)
    {
        var velocity = new Vector3[prepared.Count];
        prepared.Particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
        return velocity;
    }

    private static void AssertBits(Vector3[] actual, Vector3[] expected)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(ToBits(expected[i].x), ToBits(actual[i].x), "x " + i);
            Assert.AreEqual(ToBits(expected[i].y), ToBits(actual[i].y), "y " + i);
            Assert.AreEqual(ToBits(expected[i].z), ToBits(actual[i].z), "z " + i);
        }
    }

    private static void AssertNonZero(Vector3[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].x != 0f || values[i].y != 0f || values[i].z != 0f)
            {
                return;
            }
        }

        Assert.Fail("expected a non-zero force");
    }

    private static uint ToBits(float value)
    {
        return unchecked((uint)BitConverter.SingleToInt32Bits(value));
    }

    private static TeamParams[] Slots(params TeamProfile[] profiles)
    {
        var slots = new TeamParams[TeamProfile.MaxTeams];
        for (int i = 0; i < profiles.Length; i++)
        {
            TeamProfile profile = profiles[i];
            slots[i] = new TeamParams
            {
                Radii = new Vector4(
                    profile.SeparationRadius,
                    profile.AlignmentRadius,
                    profile.CohesionRadius,
                    profile.InterGroupSeparationMultiplier),
                Weights = new Vector4(profile.SeparationWeight, profile.AlignmentWeight, profile.CohesionWeight, 0f),
                Motion = new Vector4(profile.Cruise, profile.Turn, 0f, 0f),
            };
        }

        return slots;
    }

    private static TeamProfile Profile(
        float sep, float align, float coh, float multiplier,
        float wSep, float wAlign, float wCoh, float cruise, float turn)
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

    private ComputeShader Load(string path)
    {
        var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
        Assert.IsNotNull(shader, path);
        return shader;
    }

    private sealed class Prepared
    {
        public int Count;
        public ParticleSet Particles;
        public CommandBuffer Cmd;
        public SimContext Context;
        public BoidNeighborForcePass Force;
    }

    private sealed class Layout
    {
        public Vector3[] Positions;
        public Vector3[] Headings;
        public uint[] Teams;
        public Vector3 Extents;
    }
}
