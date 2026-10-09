using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashTwoTeamsPresetTests
{
    private const string AssetPath = "Assets/Effects/Boids_hash_2teams.asset";
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string DebugPath = "Assets/Shaders/GPU/Passes/HashDebugPasses.compute";
    private const string DynamicsPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";
    private const string BoidPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const float DeltaTime = 1f / 60f;
    private const int Frames = 300;
    private const float ChannelTolerance = 1f / 255f;

    private EffectAsset effectCopy;

    [TearDown]
    public void TearDown()
    {
        if (effectCopy != null)
        {
            IReadOnlyList<SimPass> passes = effectCopy.Passes;
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i] is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            UnityEngine.Object.DestroyImmediate(effectCopy);
            effectCopy = null;
        }
    }

    [Test]
    public void Preset_MatchesTwoTeams()
    {
        EffectAsset asset = LoadAsset();
        Assert.IsInstanceOf<SwarmSource>(asset.ResolveSource());
        Assert.AreEqual(0.5f, asset.ParticleSize);

        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(1, swarm.Seed);
        Assert.AreEqual(0f, swarm.JitterDegrees);
        Assert.AreEqual(2, swarm.Spawns.Count);
        AssertSpawn(swarm.Spawns[0], 0, 1500, new Vector2(-22f, 0f), new Vector2(1f, 0f));
        AssertSpawn(swarm.Spawns[1], 1, 1500, new Vector2(22f, 0f), new Vector2(-1f, 0f));

        Assert.AreEqual(2, asset.Teams.Count);
        AssertTeam(asset.Teams[0], "Fire");
        AssertTeam(asset.Teams[1], "Ice");
        AssertCyclicKeys(asset.Teams[0].Color, "Fire", FireKeys());
        AssertCyclicKeys(asset.Teams[1].Color, "Ice", IceKeys());

        IReadOnlyList<SimPass> passes = asset.Passes;
        Assert.AreEqual(8, passes.Count);
        Assert.IsInstanceOf<BuildSpatialHashPass>(passes[0]);
        Assert.IsInstanceOf<HashCountsToFieldPass>(passes[1]);
        Assert.IsInstanceOf<ClearVelocityPass>(passes[2]);
        Assert.IsInstanceOf<BoidNeighborForcePass>(passes[3]);
        Assert.IsInstanceOf<TeamHeadingSteerPass>(passes[4]);
        Assert.IsInstanceOf<IntegratePass>(passes[5]);
        Assert.IsInstanceOf<BoxBoundsPass>(passes[6]);
        Assert.IsInstanceOf<HeadingToValuePass>(passes[7]);

        BuildSpatialHashPass hash = (BuildSpatialHashPass)passes[0];
        BoxBoundsPass box = (BoxBoundsPass)passes[6];
        Assert.AreEqual(Vector3.zero, hash.Center);
        Assert.AreEqual(new Vector3(45f, 0f, 45f), hash.Extents);
        Assert.AreEqual(3f, hash.MinCellSize);
        Assert.IsTrue(hash.Wrap);
        Assert.AreEqual(hash.Center, box.Center);
        Assert.AreEqual(hash.Extents, box.Extents);
        Assert.AreEqual(BoundsBehaviour.Wrap, box.Behaviour);

        HashCountsToFieldPass counts = (HashCountsToFieldPass)passes[1];
        Assert.AreEqual("hashCount", counts.FieldName);
        BoidNeighborForcePass force = (BoidNeighborForcePass)passes[3];
        Assert.AreEqual(48, force.MaxNeighbors);
        Assert.IsFalse(force.CountCapHits);

        Assert.AreEqual(1, asset.Fields.Count);
        FieldDescriptor field = asset.Fields[0];
        Assert.AreEqual("hashCount", field.Name);
        Assert.AreEqual(new Vector2Int(30, 30), field.Resolution);
        Assert.AreEqual(new Vector2(90f, 90f), field.Size);
    }

    [Test]
    public void TeamGradients_AreCyclicAndReadable()
    {
        EffectAsset asset = LoadAsset();
        // Снимок Play 2026-10-10, фон (103, 97, 91) в sRGB.
        Color backgroundLinear = new Color(0.136f, 0.120f, 0.105f, 1f);
        AssertCyclicAndReadable(asset.Teams[0].Color, "Fire", backgroundLinear, true);
        AssertCyclicAndReadable(asset.Teams[1].Color, "Ice", backgroundLinear, false);
    }

    [Test]
    public void Validator_AcceptsPresetWithoutWarnings()
    {
        EffectAsset asset = LoadAsset();
        IReadOnlyList<string> warnings = null;
        Assert.DoesNotThrow(() => warnings = SpatialHashValidator.Validate(asset.Passes, asset));
        Assert.IsNotNull(warnings);
        Assert.AreEqual(0, warnings.Count);
    }

    [Test]
    [Timeout(180000)]
    public void Chain_ThreeHundredFrames_StaysFiniteInsideBox()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset();
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "BoidsHashTwoTeamsChain" };
        GraphicsBuffer teams = null;
        try
        {
            SwarmSource swarm = (SwarmSource)asset.ResolveSource();
            swarm.Setup(particles);
            RegisterLikeWorld(particles, asset);
            Graphics.CopyBuffer(
                particles.Get(BuiltinAttributes.RestPosition),
                particles.Get(BuiltinAttributes.Position));

            fields.Allocate(asset.Fields, cmd);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            teams = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            TeamParams.Upload(teams, asset.Teams);

            SimContext context = new SimContext(particles, fields, LoadLibrary(), null);
            context.Cmd = cmd;
            context.Teams = teams;

            IReadOnlyList<SimPass> passes = asset.Passes;
            for (int i = 0; i < passes.Count; i++)
            {
                passes[i].Initialize(context);
            }

            for (int frame = 0; frame < Frames; frame++)
            {
                cmd.Clear();
                for (int i = 0; i < passes.Count; i++)
                {
                    SimPass pass = passes[i];
                    pass.Execute(context, DeltaTime);
                    if (pass.LastExecuteDispatched)
                    {
                        SwapPingPong(fields, pass);
                    }
                }

                Graphics.ExecuteCommandBuffer(cmd);
            }

            int count = particles.Count;
            Vector3[] position = new Vector3[count];
            Vector3[] heading = new Vector3[count];
            Vector3[] velocity = new Vector3[count];
            uint[] teamId = new uint[count];
            particles.Get(BuiltinAttributes.Position).GetData(position);
            particles.Get(BuiltinAttributes.Heading).GetData(heading);
            particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
            particles.Get(BuiltinAttributes.TeamId).GetData(teamId);
            int fire = 0;
            int ice = 0;
            for (int i = 0; i < count; i++)
            {
                AssertFinite(position[i], "position " + i);
                AssertFinite(heading[i], "heading " + i);
                AssertFinite(velocity[i], "velocity " + i);
                Assert.AreEqual(1f, heading[i].magnitude, 1e-4f, "heading " + i);
                Assert.AreEqual(6f, velocity[i].magnitude, 1e-4f, "velocity " + i);
                Assert.GreaterOrEqual(position[i].x, -45f - 1e-3f);
                Assert.LessOrEqual(position[i].x, 45f + 1e-3f);
                Assert.GreaterOrEqual(position[i].z, -45f - 1e-3f);
                Assert.LessOrEqual(position[i].z, 45f + 1e-3f);
                if (teamId[i] == 0)
                {
                    fire++;
                }
                else if (teamId[i] == 1)
                {
                    ice++;
                }
            }

            Assert.AreEqual(1500, fire);
            Assert.AreEqual(1500, ice);

            SimField hashCount = fields.Get("hashCount");
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(hashCount.Current);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError);
            NativeArray<ushort> raw = request.GetData<ushort>();
            int sum = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                sum += Mathf.RoundToInt(Mathf.HalfToFloat(raw[i]));
            }

            Assert.AreEqual(3000, sum);
        }
        finally
        {
            teams?.Dispose();
            cmd.Release();
            fields.Dispose();
            particles.Dispose();
        }
    }

    private static void AssertSpawn(
        SwarmSource.Spawn spawn, int teamIndex, int count, Vector2 center, Vector2 direction)
    {
        Assert.AreEqual(teamIndex, spawn.TeamIndex);
        Assert.AreEqual(count, spawn.Count);
        Assert.AreEqual(center, spawn.Center);
        Assert.AreEqual(12f, spawn.Radius);
        Assert.AreEqual(direction, spawn.InitialDirection);
    }

    private static void AssertTeam(TeamProfile team, string name)
    {
        Assert.AreEqual(name, team.Name);
        Assert.AreEqual(3f, team.SeparationRadius);
        Assert.AreEqual(3f, team.AlignmentRadius);
        Assert.AreEqual(3f, team.CohesionRadius);
        Assert.AreEqual(4f, team.InterGroupSeparationMultiplier);
        Assert.AreEqual(1.2f, team.SeparationWeight);
        Assert.AreEqual(0.8f, team.AlignmentWeight);
        Assert.AreEqual(0.6f, team.CohesionWeight);
        Assert.AreEqual(6f, team.Cruise);
        Assert.AreEqual(4f, team.Turn);
    }

    private static Color[] FireKeys()
    {
        return new[]
        {
            new Color(0.55f, 0.08f, 0.00f, 1f),
            new Color(0.85f, 0.30f, 0.00f, 1f),
            new Color(1.00f, 0.70f, 0.10f, 1f),
            new Color(0.85f, 0.30f, 0.00f, 1f),
            new Color(0.55f, 0.08f, 0.00f, 1f),
        };
    }

    private static Color[] IceKeys()
    {
        return new[]
        {
            new Color(0.30f, 0.80f, 1.00f, 1f),
            new Color(0.15f, 0.50f, 0.95f, 1f),
            new Color(0.05f, 0.15f, 0.55f, 1f),
            new Color(0.15f, 0.50f, 0.95f, 1f),
            new Color(0.30f, 0.80f, 1.00f, 1f),
        };
    }

    private static void AssertCyclicKeys(Gradient gradient, string team, Color[] keys)
    {
        Assert.AreEqual(GradientMode.Blend, gradient.mode, team + " mode");
        float[] times = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        for (int i = 0; i < times.Length; i++)
        {
            AssertColors(keys[i], gradient.Evaluate(times[i]), team + " t=" + times[i]);
        }
    }

    private static void AssertCyclicAndReadable(
        Gradient gradient, string team, Color backgroundLinear, bool fire)
    {
        Color start = gradient.Evaluate(0f);
        Color end = gradient.Evaluate(1f);
        AssertColors(start, end, team + " ends");

        const int sampleCount = 256;
        float maxLuminance = float.NegativeInfinity;
        float[] luminance = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / 255f;
            Color sample = gradient.Evaluate(t);
            string where = team + " sample " + i + " t=" + t.ToString("0.###");
            Color.RGBToHSV(sample, out float hue01, out float saturation, out float value);
            float hue = hue01 * 360f;
            Assert.GreaterOrEqual(saturation, 0.5f, where + " S");
            Assert.GreaterOrEqual(value, 0.4f, where + " V");
            if (fire)
            {
                Assert.That(hue, Is.InRange(0f, 60f), where + " hue");
            }
            else
            {
                Assert.That(hue, Is.InRange(180f, 250f), where + " hue");
            }

            float distance = Mathf.Sqrt(
                (sample.r - backgroundLinear.r) * (sample.r - backgroundLinear.r)
                + (sample.g - backgroundLinear.g) * (sample.g - backgroundLinear.g)
                + (sample.b - backgroundLinear.b) * (sample.b - backgroundLinear.b));
            Assert.GreaterOrEqual(distance, 0.25f, where + " distance");

            luminance[i] = 0.2126f * sample.r + 0.7152f * sample.g + 0.0722f * sample.b;
            if (luminance[i] > maxLuminance)
            {
                maxLuminance = luminance[i];
            }
        }

        for (int i = 0; i < sampleCount; i++)
        {
            if (luminance[i] < maxLuminance - 1e-4f)
            {
                continue;
            }

            float t = i / 255f;
            bool peakInRange = fire
                ? t >= 0.45f && t <= 0.55f
                : t <= 0.05f || t >= 0.95f;
            Assert.IsTrue(
                peakInRange,
                team + " luminance peak t=" + t.ToString("0.###") + " is outside the allowed range");
        }
    }

    private static void AssertColors(Color expected, Color actual, string label)
    {
        Assert.LessOrEqual(Mathf.Abs(expected.r - actual.r), ChannelTolerance, label + " r");
        Assert.LessOrEqual(Mathf.Abs(expected.g - actual.g), ChannelTolerance, label + " g");
        Assert.LessOrEqual(Mathf.Abs(expected.b - actual.b), ChannelTolerance, label + " b");
        Assert.LessOrEqual(Mathf.Abs(expected.a - actual.a), ChannelTolerance, label + " a");
    }

    private EffectAsset LoadAsset()
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(AssetPath);
        Assert.IsNotNull(
            asset, AssetPath + " must exist. Create it with Tools/M3D/Create Boids Hash 2 Teams Effect.");
        return asset;
    }

    private EffectAsset InstantiateAsset()
    {
        effectCopy = UnityEngine.Object.Instantiate(LoadAsset());
        effectCopy.name = "Boids_hash_2teams_TestCopy";
        return effectCopy;
    }

    private static ComputeShader[] LoadLibrary()
    {
        ComputeShader hash = AssetDatabase.LoadAssetAtPath<ComputeShader>(HashPath);
        ComputeShader debug = AssetDatabase.LoadAssetAtPath<ComputeShader>(DebugPath);
        ComputeShader dynamics = AssetDatabase.LoadAssetAtPath<ComputeShader>(DynamicsPath);
        ComputeShader boids = AssetDatabase.LoadAssetAtPath<ComputeShader>(BoidPath);
        Assert.IsNotNull(hash);
        Assert.IsNotNull(debug);
        Assert.IsNotNull(dynamics);
        Assert.IsNotNull(boids);
        return new[] { hash, debug, dynamics, boids };
    }

    private static void RegisterLikeWorld(ParticleSet particles, EffectAsset asset)
    {
        IReadOnlyList<SimPass> passes = asset.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            RegisterMissing(particles, passes[i].Reads);
            RegisterMissing(particles, passes[i].Writes);
        }

        if (!particles.Schema.Has(BuiltinAttributes.Position))
        {
            RegisterZeroed(particles, BuiltinAttributes.Position);
        }
    }

    private static void RegisterMissing(ParticleSet particles, IReadOnlyList<AttributeId> ids)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (!particles.Schema.Has(ids[i]))
            {
                RegisterZeroed(particles, ids[i]);
            }
        }
    }

    private static void RegisterZeroed(ParticleSet particles, AttributeId id)
    {
        GraphicsBuffer buffer = particles.RegisterAttribute(id);
        buffer.SetData(new byte[buffer.count * buffer.stride]);
    }

    private static void SwapPingPong(FieldSet fields, SimPass pass)
    {
        IReadOnlyList<FieldRequest> writes = pass.FieldWrites;
        for (int i = 0; i < writes.Count; i++)
        {
            if (writes[i].Access == FieldAccess.WritePingPong)
            {
                fields.Swap(writes[i].FieldName);
            }
        }
    }

    private static void AssertFinite(Vector3 value, string label)
    {
        Assert.IsFalse(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z), label);
        Assert.IsFalse(float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z), label);
    }
}
