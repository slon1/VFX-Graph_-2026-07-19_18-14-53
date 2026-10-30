using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BuildSpatialHashVelocityTests
{
    private const string ComputePath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";

    [Test]
    public void Snapshot_VelocityMatchesInertial_AcrossCellsAndTorusSeam()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(8f, 0f, 8f), 2f);
        const int interior = 40;
        const int seam = 4;
        int count = interior + seam;
        Vector3[] positions = new Vector3[count];
        Vector3[] headings = new Vector3[count];
        Vector3[] velocities = new Vector3[count];
        uint[] teams = new uint[count];
        System.Random rng = new System.Random(57);
        for (int i = 0; i < interior; i++)
        {
            positions[i] = new Vector3(
                Interior(rng, layout.Origin.x, layout.CellSize.x, layout.Resolution.x),
                NextSigned(rng),
                Interior(rng, layout.Origin.y, layout.CellSize.y, layout.Resolution.y));
            headings[i] = new Vector3(NextSigned(rng), NextSigned(rng), NextSigned(rng));
            velocities[i] = headings[i] + new Vector3(3f, 5f, -7f);
            teams[i] = (uint)rng.Next(0, 12);
        }

        float upperX = layout.Origin.x + layout.Size.x;
        float upperZ = layout.Origin.y + layout.Size.y;
        float belowX = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(layout.Origin.x) + 1);
        float belowZ = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(layout.Origin.y) + 1);
        positions[interior] = new Vector3(upperX, 1f, layout.Origin.y + layout.CellSize.y * 0.5f);
        positions[interior + 1] = new Vector3(layout.Origin.x + layout.CellSize.x * 0.5f, -2f, upperZ);
        positions[interior + 2] = new Vector3(belowX, 0.5f, 0f);
        positions[interior + 3] = new Vector3(0f, -0.25f, belowZ);
        for (int i = interior; i < count; i++)
        {
            headings[i] = new Vector3(0.2f, 0.3f, 0.4f);
            velocities[i] = new Vector3(-6f, 9f, 8f);
            teams[i] = (uint)(20 + i);
        }

        using (Fixture gpu = new Fixture(count))
        {
            gpu.Upload(positions, headings, velocities, teams);
            gpu.Pass.Center = Vector3.zero;
            gpu.Pass.Extents = new Vector3(8f, 0f, 8f);
            gpu.Pass.MinCellSize = 2f;
            gpu.Pass.Wrap = true;
            gpu.Pass.Initialize(gpu.Context);

            gpu.Pass.Hash.VelocityRequested = true;
            gpu.Dispatch();
            Assert.IsTrue(gpu.Pass.Hash.VelocityWritten);
            Assert.Greater(DistinctCells(gpu.Pass.Hash, count), 1);

            Dictionary<uint, Sample> velocity = Read(gpu.Pass.Hash, count);
            for (int i = 0; i < count; i++)
            {
                Assert.IsTrue(velocity.ContainsKey((uint)i));
                Sample sample = velocity[(uint)i];
                AssertBits(sample.Position.x, positions[i].x);
                AssertBits(sample.Position.y, positions[i].z);
                AssertBits(sample.Direction.x, velocities[i].x);
                AssertBits(sample.Direction.y, velocities[i].z);
                Assert.AreEqual(teams[i], sample.Team);
            }

            gpu.Pass.Hash.VelocityRequested = false;
            gpu.Dispatch();
            Assert.IsFalse(gpu.Pass.Hash.VelocityWritten);

            Dictionary<uint, Sample> heading = Read(gpu.Pass.Hash, count);
            for (int i = 0; i < count; i++)
            {
                Sample fromVelocity = velocity[(uint)i];
                Sample fromHeading = heading[(uint)i];
                AssertBits(fromHeading.Position.x, fromVelocity.Position.x);
                AssertBits(fromHeading.Position.y, fromVelocity.Position.y);
                Assert.AreEqual(fromVelocity.Team, fromHeading.Team);
                AssertBits(fromHeading.Direction.x, headings[i].x);
                AssertBits(fromHeading.Direction.y, headings[i].z);
            }
        }
    }

    [Test]
    public void VelocityRequested_WithoutKernel_Throws()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (Fixture gpu = new Fixture(4))
        {
            gpu.Pass.Initialize(gpu.Context);
            FieldInfo field = typeof(BuildSpatialHashPass).GetField(
                "velocityScatterKernel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field.SetValue(gpu.Pass, default(KernelHandle));
            gpu.Pass.Hash.VelocityRequested = true;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => gpu.Pass.Execute(gpu.Context, 0f));
            StringAssert.Contains("velocity snapshot kernel not found", exception.Message);
            Assert.IsFalse(gpu.Pass.Hash.VelocityWritten);
        }
    }

    [Test]
    public void VelocityAndPackedRequested_Throws()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (Fixture gpu = new Fixture(4))
        {
            gpu.Pass.Initialize(gpu.Context);
            gpu.Pass.Hash.VelocityRequested = true;
            gpu.Pass.Hash.PackedRequested = true;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => gpu.Pass.Execute(gpu.Context, 0f));
            StringAssert.Contains("packed", exception.Message);
            Assert.IsFalse(gpu.Pass.Hash.VelocityWritten);
            Assert.IsFalse(gpu.Pass.Hash.PackedWritten);
        }
    }

    private static Dictionary<uint, Sample> Read(SpatialHashSet hash, int count)
    {
        uint[] indices = new uint[count];
        Vector2[] positions = new Vector2[count];
        Vector2[] directions = new Vector2[count];
        uint[] teams = new uint[count];
        hash.SortedIndices.GetData(indices);
        hash.SortedPositions.GetData(positions);
        hash.SortedDirections.GetData(directions);
        hash.SortedTeams.GetData(teams);

        var map = new Dictionary<uint, Sample>(count);
        for (int slot = 0; slot < count; slot++)
        {
            uint source = indices[slot];
            Assert.IsFalse(map.ContainsKey(source), "SortedIndices is not a permutation.");
            map.Add(source, new Sample
            {
                Position = positions[slot],
                Direction = directions[slot],
                Team = teams[slot],
            });
        }

        Assert.AreEqual(count, map.Count);
        return map;
    }

    private static int DistinctCells(SpatialHashSet hash, int count)
    {
        uint[] cells = new uint[count];
        hash.ParticleCells.GetData(cells);
        var seen = new HashSet<uint>();
        for (int i = 0; i < count; i++)
        {
            seen.Add(cells[i]);
        }

        return seen.Count;
    }

    private static float Interior(System.Random rng, float origin, float cell, int resolution)
    {
        int c = rng.Next(resolution);
        float margin = 1e-3f * cell;
        float lo = origin + c * cell + margin;
        float hi = origin + (c + 1f) * cell - margin;
        return lo + (float)rng.NextDouble() * (hi - lo);
    }

    private static float NextSigned(System.Random rng)
    {
        return (float)(rng.NextDouble() * 2.0 - 1.0);
    }

    private static void AssertBits(float actual, float expected)
    {
        Assert.AreEqual(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
    }

    private struct Sample
    {
        public Vector2 Position;
        public Vector2 Direction;
        public uint Team;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ParticleSet Particles;
        public readonly FieldSet Fields;
        public readonly BuildSpatialHashPass Pass;
        public readonly SimContext Context;

        public Fixture(int count)
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
            Assert.IsNotNull(shader);
            Particles = new ParticleSet();
            Fields = new FieldSet();
            Particles.EnsureCapacity(count);
            Particles.RegisterAttribute(BuiltinAttributes.Position);
            Particles.RegisterAttribute(BuiltinAttributes.Heading);
            Particles.RegisterAttribute(BuiltinAttributes.TeamId);
            Particles.RegisterAttribute(BuiltinAttributes.InertialVelocity);
            Pass = new BuildSpatialHashPass();
            Context = new SimContext(Particles, Fields, new[] { shader }, null);
            Context.Cmd = new CommandBuffer { name = "BuildSpatialHashVelocity" };
        }

        public void Upload(Vector3[] positions, Vector3[] headings, Vector3[] velocities, uint[] teams)
        {
            Particles.Get(BuiltinAttributes.Position).SetData(positions);
            Particles.Get(BuiltinAttributes.Heading).SetData(headings);
            Particles.Get(BuiltinAttributes.InertialVelocity).SetData(velocities);
            Particles.Get(BuiltinAttributes.TeamId).SetData(teams);
        }

        public void Dispatch()
        {
            Pass.Execute(Context, 0f);
            Graphics.ExecuteCommandBuffer(Context.Cmd);
            Context.Cmd.Clear();
        }

        public void Dispose()
        {
            Pass.Dispose();
            Context.Cmd?.Release();
            Particles.Dispose();
            Fields.Dispose();
        }
    }
}
