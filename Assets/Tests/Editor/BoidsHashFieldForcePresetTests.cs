using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class BoidsHashFieldForcePresetTests
{
    private const string TwoTeamsWalkPath = "Assets/Effects/Boids_hash_2teams.asset";
    private const string TwoTeamsPath = "Assets/Effects/Boids_hash_2teams_field.asset";
    private const string P400cPath = "Assets/Effects/Boids_hash_P400c_field.asset";
    private const string TwoTeamsScene = "Assets/Scenes/Boids_Hash_2Teams_Field.unity";
    private const string P400cScene = "Assets/Scenes/Boids_Hash_P400c_Field.unity";
    private const float DeltaTime = 1f / 60f;
    private const int Frames = 30;

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
    public void TwoTeamsPreset_MatchesFieldForceConstants()
    {
        AssertChain(
            Load(TwoTeamsPath),
            particlesPerTeam: 1500,
            spawnRadius: 12f,
            leftCenter: new Vector2(-22f, 0f),
            rightCenter: new Vector2(22f, 0f),
            worldExtent: 45f);
    }

    [Test]
    public void P400cPreset_MatchesFieldForceConstants()
    {
        AssertChain(
            Load(P400cPath),
            particlesPerTeam: 200000,
            spawnRadius: 45f,
            leftCenter: new Vector2(-55f, 0f),
            rightCenter: new Vector2(55f, 0f),
            worldExtent: 112f);
    }

    [Test]
    public void TwoTeamsField_MatchesWalkSourcesTeamsWorldAndSpeed()
    {
        EffectAsset walk = Load(TwoTeamsWalkPath);
        EffectAsset field = Load(TwoTeamsPath);
        Assert.AreEqual(walk.SimulationSpeed, field.SimulationSpeed);
        Assert.AreEqual(walk.ParticleSize, field.ParticleSize);
        SwarmSource walkSwarm = (SwarmSource)walk.ResolveSource();
        SwarmSource fieldSwarm = (SwarmSource)field.ResolveSource();
        Assert.AreEqual(walkSwarm.Seed, fieldSwarm.Seed);
        Assert.AreEqual(walkSwarm.JitterDegrees, fieldSwarm.JitterDegrees);
        Assert.AreEqual(walkSwarm.Spawns.Count, fieldSwarm.Spawns.Count);
        for (int i = 0; i < walkSwarm.Spawns.Count; i++)
        {
            AssertSpawnEqual(walkSwarm.Spawns[i], fieldSwarm.Spawns[i]);
        }

        Assert.AreEqual(walk.Teams.Count, field.Teams.Count);
        for (int i = 0; i < walk.Teams.Count; i++)
        {
            AssertTeamEqual(walk.Teams[i], field.Teams[i]);
        }

        BoxBoundsPass walkBox = FindBox(walk.Passes);
        BoxBoundsPass fieldBox = FindBox(field.Passes);
        Assert.AreEqual(walkBox.Center, fieldBox.Center);
        Assert.AreEqual(walkBox.Extents, fieldBox.Extents);
        Assert.AreEqual(walkBox.Behaviour, fieldBox.Behaviour);
    }

    [Test]
    public void Validator_AcceptsBothPresetsWithoutWarnings()
    {
        AssertAccepted(Load(TwoTeamsPath));
        AssertAccepted(Load(P400cPath));
    }

    [Test]
    public void Scenes_AreEnabledInTheBuild()
    {
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(TwoTeamsScene), TwoTeamsScene);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<SceneAsset>(P400cScene), P400cScene);
        Assert.IsTrue(IsEnabledInBuild(TwoTeamsScene), TwoTeamsScene);
        Assert.IsTrue(IsEnabledInBuild(P400cScene), P400cScene);
    }

    [Test]
    [Timeout(180000)]
    public void TwoTeamsChain_ThirtyFrames_StaysFiniteAndConservesN()
    {
        RunChain(TwoTeamsPath, particlesPerTeam: 1500, worldExtent: 45f);
    }

    [Test]
    [Timeout(180000)]
    public void P400cChain_ThirtyFrames_StaysFiniteAndConservesN()
    {
        RunChain(P400cPath, particlesPerTeam: 200000, worldExtent: 112f);
    }

    private void RunChain(string path, int particlesPerTeam, float worldExtent)
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = InstantiateAsset(path);
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "BoidsHashFieldForceChain" };
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
                    passes[i].Execute(context, DeltaTime);
                }

                Graphics.ExecuteCommandBuffer(cmd);
            }

            int count = particles.Count;
            Vector3[] position = new Vector3[count];
            Vector3[] heading = new Vector3[count];
            uint[] teamId = new uint[count];
            particles.Get(BuiltinAttributes.Position).GetData(position);
            particles.Get(BuiltinAttributes.Heading).GetData(heading);
            particles.Get(BuiltinAttributes.TeamId).GetData(teamId);
            int fire = 0;
            int ice = 0;
            for (int i = 0; i < count; i++)
            {
                AssertFinite(position[i]);
                AssertFinite(heading[i]);
                Assert.GreaterOrEqual(position[i].x, -worldExtent - 1e-3f);
                Assert.LessOrEqual(position[i].x, worldExtent + 1e-3f);
                Assert.GreaterOrEqual(position[i].z, -worldExtent - 1e-3f);
                Assert.LessOrEqual(position[i].z, worldExtent + 1e-3f);
                if (teamId[i] == 0)
                {
                    fire++;
                }
                else if (teamId[i] == 1)
                {
                    ice++;
                }
            }

            Assert.AreEqual(particlesPerTeam, fire);
            Assert.AreEqual(particlesPerTeam, ice);
            FieldNeighborCollectPass collect = FindCollect(passes);
            Assert.AreEqual(particlesPerTeam, SumN(collect, 0), 0.1d);
            Assert.AreEqual(particlesPerTeam, SumN(collect, 1), 0.1d);
        }
        finally
        {
            teams?.Dispose();
            cmd.Release();
            fields.Dispose();
            particles.Dispose();
        }
    }

    private static void AssertChain(
        EffectAsset asset,
        int particlesPerTeam,
        float spawnRadius,
        Vector2 leftCenter,
        Vector2 rightCenter,
        float worldExtent)
    {
        Assert.IsInstanceOf<SwarmSource>(asset.ResolveSource());
        Assert.AreEqual(1f, asset.SimulationSpeed);
        Assert.AreEqual(0.5f, asset.ParticleSize);
        SwarmSource swarm = (SwarmSource)asset.ResolveSource();
        Assert.AreEqual(1, swarm.Seed);
        Assert.AreEqual(0f, swarm.JitterDegrees);
        Assert.AreEqual(2, swarm.Spawns.Count);
        AssertSpawn(swarm.Spawns[0], 0, particlesPerTeam, leftCenter, spawnRadius, new Vector2(1f, 0f));
        AssertSpawn(swarm.Spawns[1], 1, particlesPerTeam, rightCenter, spawnRadius, new Vector2(-1f, 0f));
        Assert.AreEqual(2, asset.Teams.Count);
        AssertTeam(asset.Teams[0], "RedBlue");
        AssertTeam(asset.Teams[1], "YellowGreen");

        IReadOnlyList<SimPass> passes = asset.Passes;
        Assert.AreEqual(8, passes.Count);
        FieldNeighborCollectPass collect = (FieldNeighborCollectPass)passes[0];
        Assert.AreEqual(FieldCollectMode.Scatter, collect.Mode);
        Assert.AreEqual(1f, collect.TexelSize);
        Assert.AreEqual(3, collect.RefreshInterval);
        Assert.AreEqual(Vector3.zero, collect.Center);
        Assert.AreEqual(new Vector3(worldExtent, 0f, worldExtent), collect.Extents);
        Assert.IsInstanceOf<FieldNeighborConvolvePass>(passes[1]);
        Assert.IsInstanceOf<ClearVelocityPass>(passes[2]);
        FieldNeighborForcePass force = (FieldNeighborForcePass)passes[3];
        Assert.AreEqual(collect.Center, force.Center);
        Assert.AreEqual(collect.Extents, force.Extents);
        Assert.AreEqual(collect.TexelSize, force.TexelSize);
        Assert.IsInstanceOf<TeamHeadingSteerPass>(passes[4]);
        Assert.IsInstanceOf<IntegratePass>(passes[5]);
        BoxBoundsPass box = (BoxBoundsPass)passes[6];
        Assert.AreEqual(BoundsBehaviour.Wrap, box.Behaviour);
        Assert.AreEqual(collect.Center, box.Center);
        Assert.AreEqual(collect.Extents, box.Extents);
        Assert.IsInstanceOf<HeadingToValuePass>(passes[7]);
        for (int i = 0; i < passes.Count; i++)
        {
            Assert.IsFalse(passes[i] is BoidNeighborForcePass);
            Assert.IsFalse(passes[i] is BuildSpatialHashPass);
        }
    }

    private static void AssertSpawn(
        SwarmSource.Spawn spawn, int team, int count, Vector2 center, float radius, Vector2 direction)
    {
        Assert.AreEqual(team, spawn.TeamIndex);
        Assert.AreEqual(count, spawn.Count);
        Assert.AreEqual(center, spawn.Center);
        Assert.AreEqual(radius, spawn.Radius);
        Assert.AreEqual(direction, spawn.InitialDirection);
    }

    private static void AssertSpawnEqual(SwarmSource.Spawn left, SwarmSource.Spawn right)
    {
        Assert.AreEqual(left.TeamIndex, right.TeamIndex);
        Assert.AreEqual(left.Count, right.Count);
        Assert.AreEqual(left.Center, right.Center);
        Assert.AreEqual(left.Radius, right.Radius);
        Assert.AreEqual(left.InitialDirection, right.InitialDirection);
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

    private static void AssertTeamEqual(TeamProfile left, TeamProfile right)
    {
        Assert.AreEqual(left.Name, right.Name);
        Assert.AreEqual(left.SeparationRadius, right.SeparationRadius);
        Assert.AreEqual(left.AlignmentRadius, right.AlignmentRadius);
        Assert.AreEqual(left.CohesionRadius, right.CohesionRadius);
        Assert.AreEqual(left.InterGroupSeparationMultiplier, right.InterGroupSeparationMultiplier);
        Assert.AreEqual(left.SeparationWeight, right.SeparationWeight);
        Assert.AreEqual(left.AlignmentWeight, right.AlignmentWeight);
        Assert.AreEqual(left.CohesionWeight, right.CohesionWeight);
        Assert.AreEqual(left.Cruise, right.Cruise);
        Assert.AreEqual(left.Turn, right.Turn);
        Assert.AreEqual(left.Color.Evaluate(0f), right.Color.Evaluate(0f));
        Assert.AreEqual(left.Color.Evaluate(1f), right.Color.Evaluate(1f));
    }

    private static void AssertAccepted(EffectAsset asset)
    {
        IReadOnlyList<string> warnings = null;
        Assert.DoesNotThrow(() => warnings = SpatialHashValidator.Validate(asset.Passes, asset));
        Assert.AreEqual(0, warnings.Count);
    }

    private static bool IsEnabledInBuild(string scenePath)
    {
        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
        for (int i = 0; i < scenes.Length; i++)
        {
            if (scenes[i].path == scenePath && scenes[i].enabled)
            {
                return true;
            }
        }

        return false;
    }

    private static double SumN(FieldNeighborCollectPass collect, int team)
    {
        int cells = collect.Field.Layout.CellCount;
        int channels = FieldNeighborSet.ChannelsPerTexel;
        uint[] bits = new uint[cells * channels];
        collect.Field.Accum.GetData(bits);
        double sum = 0d;
        for (int i = team * 3; i < bits.Length; i += channels)
        {
            sum += unchecked((int)bits[i]) / (double)FieldNeighborSet.FixedPointScale;
        }

        return sum;
    }

    private static FieldNeighborCollectPass FindCollect(IReadOnlyList<SimPass> passes)
    {
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is FieldNeighborCollectPass collect)
            {
                return collect;
            }
        }

        Assert.Fail("collect pass missing");
        return null;
    }

    private static BoxBoundsPass FindBox(IReadOnlyList<SimPass> passes)
    {
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is BoxBoundsPass box)
            {
                return box;
            }
        }

        Assert.Fail("box pass missing");
        return null;
    }

    private static EffectAsset Load(string path)
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        Assert.IsNotNull(asset, path + " must exist. Create it with Tools/M3D/Create Boids Hash Field Force Effects.");
        return asset;
    }

    private EffectAsset InstantiateAsset(string path)
    {
        effectCopy = UnityEngine.Object.Instantiate(Load(path));
        effectCopy.name = "Boids_hash_field_force_TestCopy";
        return effectCopy;
    }

    private static ComputeShader[] LoadLibrary()
    {
        string[] paths =
        {
            "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute",
            "Assets/Shaders/GPU/Passes/HashDebugPasses.compute",
            "Assets/Shaders/GPU/Passes/DynamicsPasses.compute",
            "Assets/Shaders/GPU/Passes/BoidsPasses.compute",
            "Assets/Shaders/GPU/Passes/FieldNeighborPasses.compute",
        };
        ComputeShader[] shaders = new ComputeShader[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            shaders[i] = AssetDatabase.LoadAssetAtPath<ComputeShader>(paths[i]);
            Assert.IsNotNull(shaders[i], paths[i]);
        }

        return shaders;
    }

    private static void RegisterLikeWorld(ParticleSet particles, EffectAsset asset)
    {
        IReadOnlyList<SimPass> passes = asset.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            RegisterMissing(particles, passes[i].Reads);
            RegisterMissing(particles, passes[i].Writes);
        }
    }

    private static void RegisterMissing(ParticleSet particles, IReadOnlyList<AttributeId> ids)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (!particles.Schema.Has(ids[i]))
            {
                GraphicsBuffer buffer = particles.RegisterAttribute(ids[i]);
                buffer.SetData(new byte[buffer.count * buffer.stride]);
            }
        }
    }

    private static void AssertFinite(Vector3 value)
    {
        Assert.IsFalse(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z));
        Assert.IsFalse(float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
    }
}
