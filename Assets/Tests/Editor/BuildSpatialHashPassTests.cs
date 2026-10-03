using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BuildSpatialHashPassTests
{
    private const string ComputePath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";

    [Test]
    public void Contract()
    {
        BuildSpatialHashPass pass = new BuildSpatialHashPass();
        Assert.AreEqual("Build Spatial Hash", pass.DisplayName);
        Assert.AreEqual(PassCategory.Emit, pass.Category);
        Assert.AreEqual(AttrSets.PositionHeadingTeam, pass.Reads);
        Assert.AreEqual(AttrSets.None, pass.Writes);
        Assert.AreEqual(1, pass.RepeatCount);
        Assert.AreEqual(Vector3.zero, pass.Center);
        Assert.AreEqual(new Vector3(16f, 0f, 16f), pass.Extents);
        Assert.AreEqual(2f, pass.MinCellSize);
        Assert.IsTrue(pass.Wrap);
        Assert.IsTrue(pass is IDisposable);
    }

    [Test]
    public void SecondBuilder_OnSameContext_Throws()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (GpuFixture gpu = new GpuFixture(8))
        {
            gpu.Pass.Initialize(gpu.Context);
            BuildSpatialHashPass second = new BuildSpatialHashPass();
            Assert.Throws<InvalidOperationException>(() => second.Initialize(gpu.Context));
            second.Dispose();
        }
    }

    [Test]
    public void Initialize_MissingTeamId_Throws()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        Assume.That(shader != null);
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        particles.EnsureCapacity(4);
        particles.RegisterAttribute(BuiltinAttributes.Position);
        particles.RegisterAttribute(BuiltinAttributes.Heading);
        BuildSpatialHashPass pass = new BuildSpatialHashPass();
        try
        {
            SimContext context = new SimContext(particles, fields, new[] { shader }, null);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => pass.Initialize(context));
            StringAssert.Contains("teamId", exception.Message);
        }
        finally
        {
            pass.Dispose();
            particles.Dispose();
            fields.Dispose();
        }
    }

    [Test]
    public void Dispose_Twice_DoesNotThrow()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (GpuFixture gpu = new GpuFixture(4))
        {
            gpu.Pass.Initialize(gpu.Context);
            Assert.IsNotNull(gpu.Pass.Hash);
            Assert.DoesNotThrow(() => gpu.Pass.Dispose());
            Assert.IsNull(gpu.Pass.Hash);
            Assert.DoesNotThrow(() => gpu.Pass.Dispose());
        }
    }

    private sealed class GpuFixture : IDisposable
    {
        public readonly ParticleSet Particles;
        public readonly FieldSet Fields;
        public readonly BuildSpatialHashPass Pass;
        public readonly SimContext Context;

        public GpuFixture(int count)
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
            Assert.IsNotNull(shader);
            Particles = new ParticleSet();
            Fields = new FieldSet();
            Particles.EnsureCapacity(count);
            Particles.RegisterAttribute(BuiltinAttributes.Position);
            Particles.RegisterAttribute(BuiltinAttributes.Heading);
            Particles.RegisterAttribute(BuiltinAttributes.TeamId);
            Pass = new BuildSpatialHashPass();
            Context = new SimContext(Particles, Fields, new[] { shader }, null);
            Context.Cmd = new CommandBuffer { name = "BuildSpatialHashPass" };
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
