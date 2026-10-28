using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class CurlNoise2DPassTests
{
    private const string ForcePath = "Assets/Shaders/GPU/Passes/ForcePasses.compute";
    private const string AssetPath = "Assets/Effects/CurlNoise2D.asset";
    private const float Tolerance = 1e-4f;

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
    public void Kernel_IsUnique_AndAssignsVelocity()
    {
        string source = File.ReadAllText(Path.Combine(Application.dataPath, "Shaders/GPU/Passes/ForcePasses.compute"));
        Assert.AreEqual(1, Regex.Matches(source, @"#pragma kernel CurlNoise2DForce\b").Count);

        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ForcePath);
        Assert.IsNotNull(shader);
        Assert.IsTrue(shader.HasKernel("CurlNoise2DForce"));

        CurlNoise2DPass pass = new CurlNoise2DPass();
        Assert.AreEqual(1, pass.RepeatCount);
        Assert.AreEqual("Curl Noise 2D", pass.DisplayName);
        Assert.AreEqual(PassCategory.Force, pass.Category);
        Assert.AreEqual("position", pass.Reads[0].Name);
        Assert.AreEqual(1, pass.Reads.Count);
        Assert.AreEqual("velocity", pass.Writes[0].Name);
        Assert.AreEqual(1, pass.Writes.Count);
    }

    [Test]
    public void Preset_IsSwarmCurlOnXZ()
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        Assert.IsNotNull(asset, AssetPath);
        Assert.AreEqual(1f, asset.SimulationSpeed);
        Assert.AreEqual(0.2f, asset.ParticleSize);
        Assert.AreEqual(ParticleColorSource.Direction, asset.ParticleColorSource);

        SwarmSource swarm = asset.ResolveSource() as SwarmSource;
        Assert.IsNotNull(swarm);
        Assert.AreEqual(1, swarm.Spawns.Count);
        Assert.AreEqual(50000, swarm.Spawns[0].Count);
        Assert.AreEqual(0, swarm.Spawns[0].TeamIndex);
        Assert.Greater(swarm.Spawns[0].Radius, 0f);
        Assert.AreEqual(1, asset.Teams.Count);

        IReadOnlyList<SimPass> passes = asset.Passes;
        Assert.AreEqual(3, passes.Count);
        CurlNoise2DPass curl = passes[0] as CurlNoise2DPass;
        Assert.IsNotNull(curl);
        Assert.AreEqual(0.15f, curl.Scale);
        Assert.AreEqual(6f, curl.Amplitude);
        Assert.AreEqual(0.2f, curl.Speed);
        Assert.IsInstanceOf<IntegratePass>(passes[1]);
        BoxBoundsPass box = passes[2] as BoxBoundsPass;
        Assert.IsNotNull(box);
        Assert.AreEqual(BoundsBehaviour.Wrap, box.Behaviour);
        Assert.AreEqual(0f, box.Extents.y);
        Assert.Greater(box.Extents.x, 0f);
        Assert.Greater(box.Extents.z, 0f);
    }

    [Test]
    public void AssignsCurl_IgnoresPreviousVelocity_AndTimeWhenSpeedIsZero()
    {
        Assume.That(SystemInfo.supportsComputeShaders);

        const int count = 3;
        ParticleSet particles = new ParticleSet();
        owned.Add(particles);
        FieldSet fields = new FieldSet();
        owned.Add(fields);
        particles.EnsureCapacity(count);
        particles.RegisterAttribute(BuiltinAttributes.Position);
        particles.RegisterAttribute(BuiltinAttributes.Velocity);

        Vector3[] position =
        {
            new Vector3(3.25f, 1.5f, 7.5f),
            new Vector3(3.25f, 9f, 7.5f),
            new Vector3(8f, 0f, 1.25f),
        };
        Vector3[] velocity =
        {
            new Vector3(40f, 40f, 40f),
            new Vector3(-7f, 3f, 11f),
            new Vector3(1f, 2f, 3f),
        };
        particles.Get(BuiltinAttributes.Position).SetData(position);
        particles.Get(BuiltinAttributes.Velocity).SetData(velocity);

        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ForcePath);
        CommandBuffer cmd = new CommandBuffer { name = "CurlNoise2D" };
        owned.Add(new CommandBufferDisposable(cmd));
        SimContext context = new SimContext(particles, fields, new[] { shader }, null);
        context.Cmd = cmd;

        CurlNoise2DPass pass = new CurlNoise2DPass
        {
            Scale = 0.35f,
            Amplitude = 2.5f,
            Speed = 0f,
        };
        pass.Initialize(context);

        const float timeA = 1.7f;
        const float timeB = 9.2f;
        context.Time = timeA;
        pass.Execute(context, 1f / 60f);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();

        Vector3[] after = new Vector3[count];
        particles.Get(BuiltinAttributes.Velocity).GetData(after);
        Vector3[] stayed = new Vector3[count];
        particles.Get(BuiltinAttributes.Position).GetData(stayed);

        Vector3 expected0 = Expected(position[0], 0.35f, 2.5f, timeA, 0f);
        AssertClose(after[0], expected0);
        AssertClose(after[1], expected0);
        Assert.AreEqual(0f, after[0].y, Tolerance);
        Assert.AreEqual(0f, after[1].y, Tolerance);
        Assert.AreEqual(0f, after[2].y, Tolerance);
        AssertClose(stayed[0], position[0]);
        AssertClose(stayed[1], position[1]);

        context.Time = timeB;
        pass.Execute(context, 1f / 60f);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        particles.Get(BuiltinAttributes.Velocity).GetData(after);
        AssertClose(after[0], expected0);

        pass.Speed = 0.4f;
        pass.Amplitude = 5f;
        context.Time = timeA;
        pass.Execute(context, 1f / 60f);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        particles.Get(BuiltinAttributes.Velocity).GetData(after);
        Vector3 expectedFast = Expected(position[2], 0.35f, 5f, timeA, 0.4f);
        AssertClose(after[2], expectedFast);
        Assert.Greater(Mathf.Abs(after[2].x) + Mathf.Abs(after[2].z), 1e-3f);

        pass.Amplitude = 0f;
        pass.Execute(context, 1f / 60f);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        particles.Get(BuiltinAttributes.Velocity).GetData(after);
        AssertClose(after[2], Vector3.zero);
    }

    private static Vector3 Expected(Vector3 position, float scale, float amplitude, float time, float speed)
    {
        float2 curl = Curl(position.x * scale, position.z * scale, time * speed);
        return new Vector3(curl.X * amplitude, 0f, curl.Y * amplitude);
    }

    private static float2 Curl(float x, float y, float time)
    {
        const float e = 0.1f;
        const float inv2e = 1f / (2f * e);
        float ddy = (Scalar(x, y + e, time) - Scalar(x, y - e, time)) * inv2e;
        float ddx = (Scalar(x + e, y, time) - Scalar(x - e, y, time)) * inv2e;
        return new float2(ddy, -ddx);
    }

    private static float Scalar(float x, float y, float z)
    {
        return ValueNoise(x, y, z);
    }

    private static float ValueNoise(float x, float y, float z)
    {
        float ix = Mathf.Floor(x);
        float iy = Mathf.Floor(y);
        float iz = Mathf.Floor(z);
        float fx = x - ix;
        float fy = y - iy;
        float fz = z - iz;
        float ux = fx * fx * (3f - 2f * fx);
        float uy = fy * fy * (3f - 2f * fy);
        float uz = fz * fz * (3f - 2f * fz);

        float x00 = Mathf.Lerp(Hash(ix, iy, iz), Hash(ix + 1f, iy, iz), ux);
        float x10 = Mathf.Lerp(Hash(ix, iy + 1f, iz), Hash(ix + 1f, iy + 1f, iz), ux);
        float x01 = Mathf.Lerp(Hash(ix, iy, iz + 1f), Hash(ix + 1f, iy, iz + 1f), ux);
        float x11 = Mathf.Lerp(Hash(ix, iy + 1f, iz + 1f), Hash(ix + 1f, iy + 1f, iz + 1f), ux);
        float y0 = Mathf.Lerp(x00, x10, uy);
        float y1 = Mathf.Lerp(x01, x11, uy);
        return Mathf.Lerp(y0, y1, uz);
    }

    private static float Hash(float x, float y, float z)
    {
        int ix = (int)x;
        int iy = (int)y;
        int iz = (int)z;
        uint hx = Pcg((uint)(ix + 32768), (uint)(iy + 32768), (uint)(iz + 32768), 0);
        return hx * (1f / 4294967295f);
    }

    private static uint Pcg(uint x, uint y, uint z, int lane)
    {
        x = x * 1664525u + 1013904223u;
        y = y * 1664525u + 1013904223u;
        z = z * 1664525u + 1013904223u;
        x += y * z;
        y += z * x;
        z += x * y;
        x ^= x >> 16;
        y ^= y >> 16;
        z ^= z >> 16;
        x += y * z;
        y += z * x;
        z += x * y;
        if (lane == 0)
        {
            return x;
        }

        if (lane == 1)
        {
            return y;
        }

        return z;
    }

    private void AssertClose(Vector3 gpu, Vector3 expected)
    {
        Assert.AreEqual(expected.x, gpu.x, Tolerance, "x");
        Assert.AreEqual(expected.y, gpu.y, Tolerance, "y");
        Assert.AreEqual(expected.z, gpu.z, Tolerance, "z");
    }

    private readonly struct float2
    {
        public readonly float X;
        public readonly float Y;

        public float2(float x, float y)
        {
            X = x;
            Y = y;
        }
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
