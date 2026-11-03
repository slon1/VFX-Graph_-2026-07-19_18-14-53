using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class FieldNeighborRefreshTests
{
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string FieldPath = "Assets/Shaders/GPU/Passes/FieldNeighborPasses.compute";

    [Test]
    public void RefreshIntervalOne_SortedPositionsMatch()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector3[] positions = { new Vector3(1.25f, 0f, -2.5f), new Vector3(-3f, 0f, 4.5f) };
        Vector2[] left = Snapshot(positions, 1);
        Vector2[] right = Snapshot(positions, 1);
        Assert.AreEqual(left.Length, right.Length);
        for (int i = 0; i < left.Length; i++)
        {
            Assert.AreEqual(left[i].x, right[i].x);
            Assert.AreEqual(left[i].y, right[i].y);
        }
    }

    [Test]
    public void Gather_K3_UpdatesOnFramesZeroAndThree()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (Chain chain = new Chain(FieldCollectMode.Gather, 3))
        {
            float[] held = null;
            for (int frame = 0; frame < 6; frame++)
            {
                if (frame > 0)
                {
                    chain.Shift(1f);
                }

                chain.Step();
                bool due = frame % 3 == 0;
                Assert.AreEqual(due, chain.HashDispatched, "hash " + frame);
                Assert.AreEqual(due, chain.CollectDispatched, "collect " + frame);
                Assert.AreEqual(due, chain.ConvolveDispatched, "convolve " + frame);
                float[] field = chain.ReadField();
                if (frame == 0)
                {
                    held = field;
                }
                else if (!due)
                {
                    AssertSame(held, field, frame);
                }
                else
                {
                    Assert.IsFalse(Same(held, field));
                    held = field;
                }
            }
        }
    }

    [Test]
    public void RepeatFour_DoesNotChangeTheRealField()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (Chain once = new Chain(FieldCollectMode.Gather, 1))
        using (Chain extra = new Chain(FieldCollectMode.Gather, 1))
        {
            extra.SetRepeat(FieldRepeatStage.All, 4);
            once.Step();
            extra.Step();
            AssertSame(once.ReadField(), extra.ReadField(), 0);
            Assert.IsNotNull(extra.CollectScratch);
            Assert.IsNotNull(extra.ConvolveScratch);
            Assert.AreEqual(1, extra.PassRepeatCount);
        }
    }

    [Test]
    public void Off_SkipsCollectAndConvolve_AndKeepsTheHash()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (Chain chain = new Chain(FieldCollectMode.Gather, 1))
        {
            chain.SetRepeat(FieldRepeatStage.Off, 1);
            chain.Step();
            Assert.IsTrue(chain.HashDispatched);
            Assert.IsFalse(chain.CollectDispatched);
            Assert.IsFalse(chain.ConvolveDispatched);
            Assert.IsNull(chain.CollectScratch);
        }
    }

    [Test]
    public void ScatterRepeat_ClearsTheScratchEachTime()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (Chain chain = new Chain(FieldCollectMode.Scatter, 1))
        {
            chain.SetRepeat(FieldRepeatStage.Collect, 4);
            chain.Step();
            Assert.AreEqual(2d, chain.ReadScratchSum(), 1e-3d);
            Assert.IsNull(chain.ConvolveScratch);
        }
    }

    [Test]
    public void Scatter_K2_UpdatesOnEvenFrames()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (Chain chain = new Chain(FieldCollectMode.Scatter, 2))
        {
            float[] held = null;
            for (int frame = 0; frame < 4; frame++)
            {
                if (frame > 0)
                {
                    chain.Shift(1f);
                }

                chain.Step();
                bool due = frame % 2 == 0;
                Assert.AreEqual(due, chain.CollectDispatched, "collect " + frame);
                Assert.AreEqual(due, chain.ConvolveDispatched, "convolve " + frame);
                float[] field = chain.ReadField();
                if (!due)
                {
                    AssertSame(held, field, frame);
                }
                else
                {
                    if (held != null)
                    {
                        Assert.IsFalse(Same(held, field), "frame " + frame + " did not change");
                    }

                    held = field;
                }
            }
        }
    }

    private static Vector2[] Snapshot(Vector3[] positions, int interval)
    {
        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(HashPath);
        Assume.That(shader != null);
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer();
        BuildSpatialHashPass pass = new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = new Vector3(8f, 0f, 8f),
            MinCellSize = 1f,
            Wrap = true,
            RefreshInterval = interval,
        };
        try
        {
            particles.EnsureCapacity(positions.Length);
            particles.RegisterAttribute(BuiltinAttributes.Position);
            particles.RegisterAttribute(BuiltinAttributes.Heading);
            particles.RegisterAttribute(BuiltinAttributes.TeamId);
            particles.Get(BuiltinAttributes.Position).SetData(positions);
            particles.Get(BuiltinAttributes.Heading).SetData(new Vector3[positions.Length]);
            particles.Get(BuiltinAttributes.TeamId).SetData(new uint[positions.Length]);
            SimContext context = new SimContext(particles, fields, new[] { shader }, null);
            context.Cmd = cmd;
            pass.Initialize(context);
            pass.Execute(context, 0f);
            Graphics.ExecuteCommandBuffer(cmd);
            Vector2[] sorted = new Vector2[positions.Length];
            pass.Hash.SortedPositions.GetData(sorted);
            return sorted;
        }
        finally
        {
            pass.Dispose();
            cmd.Release();
            particles.Dispose();
            fields.Dispose();
        }
    }

    private static bool Same(float[] left, float[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void AssertSame(float[] left, float[] right, int frame)
    {
        float max = 0f;
        int at = -1;
        int n = left == null || right == null ? 0 : Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            float d = Math.Abs(left[i] - right[i]);
            if (d > max)
            {
                max = d;
                at = i;
            }
        }

        Assert.IsTrue(Same(left, right), "frame " + frame + " max " + max + " at " + at);
    }

    private sealed class Chain : IDisposable
    {
        private readonly BuildSpatialHashPass hash;
        private readonly FieldNeighborCollectPass collect;
        private readonly FieldNeighborConvolvePass convolve;
        private readonly SimContext context;
        private readonly ParticleSet particles;
        private readonly FieldSet fields;
        private readonly GraphicsBuffer teams;
        private readonly Vector3[] positions;
        private readonly bool gather;

        public Chain(FieldCollectMode mode, int interval)
        {
            gather = mode == FieldCollectMode.Gather;
            ComputeShader hashShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(HashPath);
            ComputeShader fieldShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(FieldPath);
            Assert.IsNotNull(hashShader);
            Assert.IsNotNull(fieldShader);
            positions = new[] { new Vector3(1f, 0f, 1f), new Vector3(-2f, 0f, 3f) };
            particles = new ParticleSet();
            fields = new FieldSet();
            particles.EnsureCapacity(positions.Length);
            particles.RegisterAttribute(BuiltinAttributes.Position);
            particles.RegisterAttribute(BuiltinAttributes.Heading);
            particles.RegisterAttribute(BuiltinAttributes.TeamId);
            particles.Get(BuiltinAttributes.Position).SetData(positions);
            Vector3[] headings = { Vector3.right, Vector3.forward };
            particles.Get(BuiltinAttributes.Heading).SetData(headings);
            particles.Get(BuiltinAttributes.TeamId).SetData(new uint[] { 0, 1 });
            teams = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            TeamParams[] slots = new TeamParams[TeamProfile.MaxTeams];
            slots[0].Radii = new Vector4(3f, 3f, 3f, 1f);
            slots[1].Radii = new Vector4(3f, 3f, 3f, 1f);
            teams.SetData(slots);
            context = new SimContext(particles, fields, new[] { hashShader, fieldShader }, null);
            context.Cmd = new CommandBuffer { name = "FieldRefresh" };
            context.Teams = teams;
            hash = new BuildSpatialHashPass
            {
                Center = Vector3.zero,
                Extents = new Vector3(8f, 0f, 8f),
                MinCellSize = 1f,
                Wrap = true,
                RefreshInterval = interval,
            };
            collect = new FieldNeighborCollectPass
            {
                Mode = mode,
                Center = Vector3.zero,
                Extents = new Vector3(8f, 0f, 8f),
                TexelSize = 1f,
                RefreshInterval = interval,
            };
            convolve = new FieldNeighborConvolvePass();
            if (gather)
            {
                hash.Initialize(context);
            }

            collect.Initialize(context);
            convolve.Initialize(context);
        }

        public bool HashDispatched => hash.LastExecuteDispatched;
        public bool CollectDispatched => collect.LastExecuteDispatched;
        public bool ConvolveDispatched => convolve.LastExecuteDispatched;
        public GraphicsBuffer CollectScratch => collect.FieldScratch;
        public GraphicsBuffer ConvolveScratch => convolve.FieldScratch;
        public int PassRepeatCount => collect.RepeatCount;

        public void SetRepeat(FieldRepeatStage stage, int count)
        {
            collect.FieldStage = stage;
            collect.FieldRepeatCount = count;
            convolve.FieldStage = stage;
            convolve.FieldRepeatCount = count;
        }

        public double ReadScratchSum()
        {
            uint[] bits = new uint[context.FieldNeighbor.Layout.CellCount * 6];
            collect.FieldScratch.GetData(bits);
            double sum = 0d;
            for (int i = 0; i < bits.Length; i += 3)
            {
                sum += unchecked((int)bits[i]) / (double)FieldNeighborSet.FixedPointScale;
            }

            return sum;
        }

        public void Shift(float dx)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i].x += dx;
            }

            particles.Get(BuiltinAttributes.Position).SetData(positions);
        }

        public void Step()
        {
            if (gather)
            {
                hash.Execute(context, 0f);
            }

            collect.Execute(context, 0f);
            convolve.Execute(context, 0f);
            Graphics.ExecuteCommandBuffer(context.Cmd);
            context.Cmd.Clear();
        }

        public float[] ReadField()
        {
            Vector4[] slots = new Vector4[context.FieldNeighbor.Layout.CellCount * 4];
            context.FieldNeighbor.Field.GetData(slots);
            float[] flat = new float[slots.Length * 4];
            for (int i = 0; i < slots.Length; i++)
            {
                flat[i * 4] = slots[i].x;
                flat[i * 4 + 1] = slots[i].y;
                flat[i * 4 + 2] = slots[i].z;
                flat[i * 4 + 3] = slots[i].w;
            }

            return flat;
        }

        public void Dispose()
        {
            convolve.Dispose();
            collect.Dispose();
            hash.Dispose();
            context.Cmd?.Release();
            teams?.Release();
            particles?.Dispose();
            fields?.Dispose();
        }
    }
}
