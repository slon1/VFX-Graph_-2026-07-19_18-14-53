using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// ADR-062 §2.7. Metrics match DOC/MEASURE/p8-field-model README §6 and the model loop:
/// periodic distance on a 90 m world, R = 3, r² &lt; R². K = 1 sets RefreshInterval on the
/// in-memory copy. K = 3 leaves the asset value. The assets themselves are not saved.
/// </summary>
[TestFixture]
public class BehaviorProbe
{
    private const string FieldPath = "Assets/Effects/Boids_hash_2teams_field.asset";
    private const string WalkPath = "Assets/Effects/Boids_hash_2teams.asset";
    private const float World = 90f;
    private const float Radius = 3f;
    private const int FlockSize = 30;

    [Test]
    public void MixedFlocks_ContactIsComplete()
    {
        const int count = 20;
        var position = new Vector2[count];
        var heading = new Vector2[count];
        var team = new int[count];
        for (int i = 0; i < count; i++)
        {
            position[i] = new Vector2(0.01f * i, 0f);
            heading[i] = new Vector2(1f, 0f);
            team[i] = i % 2;
        }

        Metrics metrics = Measure(position, heading, null, team);
        Assert.AreEqual(100.0, metrics.Contact, 1e-6);
    }

    [Test]
    public void SeparatedFlocks_HaveNoContactAndNoEnemy()
    {
        var position = new Vector2[4];
        var heading = new Vector2[4];
        var team = new int[4];
        for (int i = 0; i < 4; i++)
        {
            position[i] = new Vector2(i < 2 ? 0f : 20f, 0f);
            heading[i] = new Vector2(1f, 0f);
            team[i] = i < 2 ? 0 : 1;
        }

        Metrics metrics = Measure(position, heading, null, team);
        Assert.AreEqual(0.0, metrics.Contact, 0.0);
        Assert.AreEqual(0.0, metrics.Enemy, 0.0);
    }

    [Test]
    public void TorusSeam_CountsAsNeighbors()
    {
        var position = new[] { new Vector2(44.8f, 0f), new Vector2(-44.8f, 0f) };
        var heading = new[] { new Vector2(1f, 0f), new Vector2(-1f, 0f) };
        var team = new[] { 0, 1 };
        Metrics metrics = Measure(position, heading, null, team);
        Assert.AreEqual(100.0, metrics.Contact, 1e-6);
        Assert.AreEqual(100.0, metrics.Enemy, 1e-6);
    }

    [Test]
    public void Flocks_CountAComponentOf30_AndSkip29()
    {
        Metrics thirty = Measure(Chain(30), ChainHeading(30), null, ChainTeam(30));
        Metrics twentyNine = Measure(Chain(29), ChainHeading(29), null, ChainTeam(29));
        Assert.AreEqual(1, thirty.Flocks0);
        Assert.AreEqual(0, thirty.Flocks1);
        Assert.AreEqual(1, thirty.Flocks);
        Assert.AreEqual(0, twentyNine.Flocks0);
        Assert.AreEqual(0, twentyNine.Flocks);
    }

    [Test]
    public void Turn_UsesTheAngleBetweenGivenHeadings()
    {
        var position = new[] { Vector2.zero, new Vector2(10f, 0f) };
        var previous = new[] { new Vector2(1f, 0f), new Vector2(1f, 0f) };
        var heading = new[] { new Vector2(0f, 1f), new Vector2(1f, 0f) };
        var team = new[] { 0, 0 };
        Metrics metrics = Measure(position, heading, previous, team);
        double quarter = Math.PI / 4.0;
        Assert.AreEqual(quarter, metrics.Turn, 1e-9);
        Assert.AreEqual(quarter, metrics.TurnSd, 1e-9);
    }

    [Test]
    [Timeout(180000)]
    public void Smoke_FieldK1_60Fps_Seed1_ToFiveSeconds()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        List<Sample> samples = Run(Variant.FieldK1, 60, 1, 5.0, new[] { 5.0 });
        Assert.AreEqual(1, samples.Count);
        Metrics metrics = samples[0].Metrics;
        AssertFinite(metrics);
        Assert.GreaterOrEqual(metrics.Contact, 0.0);
        Assert.LessOrEqual(metrics.Contact, 100.0);
        Assert.AreEqual(3, AssetCollect().RefreshInterval);
    }

    [Test]
    [Explicit]
    [Timeout(1800000)]
    public void Matrix_FiveSeeds_TwoRates_FourVariants()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        var watch = Stopwatch.StartNew();
        int[] rates = { 30, 60 };
        var variants = new[] { Variant.Uncapped, Variant.Cap48, Variant.FieldK1, Variant.FieldK3 };
        double[] times = { 1.0, 5.0, 15.0 };
        var samples = new List<Sample>();
        for (int r = 0; r < rates.Length; r++)
        {
            for (int v = 0; v < variants.Length; v++)
            {
                for (int seed = 1; seed <= 5; seed++)
                {
                    samples.AddRange(Run(variants[v], rates[r], seed, 15.1, times));
                }
            }
        }

        watch.Stop();
        Assert.AreEqual(3, AssetCollect().RefreshInterval);
        BoidNeighborForcePass walk = AssetWalk();
        Assert.AreEqual(48, walk.MaxNeighbors);

        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DOC", "MEASURE", "p8-field-force"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "probe-60984cc.csv"), ToCsv(samples), new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(dir, "probe-60984cc.md"),
            ToMarkdown(samples, watch.Elapsed.TotalSeconds),
            new UTF8Encoding(false));
        for (int i = 0; i < samples.Count; i++)
        {
            AssertFinite(samples[i].Metrics);
        }
    }

    private static List<Sample> Run(Variant variant, int fps, int seed, double seconds, double[] times)
    {
        bool field = variant == Variant.FieldK1 || variant == Variant.FieldK3;
        string path = field ? FieldPath : WalkPath;
        EffectAsset copy = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<EffectAsset>(path));
        copy.name = "BehaviorProbe_" + variant + "_" + fps + "_" + seed;
        if (variant == Variant.FieldK1)
        {
            FindCollect(copy.Passes).RefreshInterval = 1;
        }
        else if (variant == Variant.FieldK3)
        {
            Assert.AreEqual(3, FindCollect(copy.Passes).RefreshInterval);
        }
        else if (variant == Variant.Uncapped)
        {
            FindWalk(copy.Passes).MaxNeighbors = 0;
        }

        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "BehaviorProbe" };
        GraphicsBuffer teams = null;
        try
        {
            SwarmSource swarm = (SwarmSource)copy.ResolveSource();
            swarm.Seed = seed;
            swarm.Setup(particles);
            RegisterLikeWorld(particles, copy);
            Graphics.CopyBuffer(
                particles.Get(BuiltinAttributes.RestPosition),
                particles.Get(BuiltinAttributes.Position));
            fields.Allocate(copy.Fields, cmd);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            teams = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            TeamParams.Upload(teams, copy.Teams);
            SimContext context = new SimContext(particles, fields, LoadLibrary(), null);
            context.Cmd = cmd;
            context.Teams = teams;
            IReadOnlyList<SimPass> passes = copy.Passes;
            for (int i = 0; i < passes.Count; i++)
            {
                passes[i].Initialize(context);
            }

            int last = (int)Math.Round(seconds * fps, MidpointRounding.AwayFromZero);
            var wanted = new HashSet<int>();
            for (int i = 0; i < times.Length; i++)
            {
                int frame = (int)Math.Round(times[i] * fps, MidpointRounding.AwayFromZero);
                wanted.Add(frame);
                if (frame > 0)
                {
                    wanted.Add(frame - 1);
                }
            }

            var captured = new Dictionary<int, Shot>();
            float dt = copy.SimulationSpeed / fps;
            for (int frame = 1; frame <= last; frame++)
            {
                cmd.Clear();
                for (int i = 0; i < passes.Count; i++)
                {
                    passes[i].Execute(context, dt);
                }

                Graphics.ExecuteCommandBuffer(cmd);
                if (wanted.Contains(frame))
                {
                    captured[frame] = Capture(particles);
                }
            }

            var samples = new List<Sample>();
            for (int i = 0; i < times.Length; i++)
            {
                int frame = (int)Math.Round(times[i] * fps, MidpointRounding.AwayFromZero);
                Shot now = captured[frame];
                Shot before = captured[frame - 1];
                samples.Add(new Sample
                {
                    Fps = fps,
                    Seed = seed,
                    Variant = variant,
                    Time = times[i],
                    Metrics = Measure(now.Position, now.Heading, before.Heading, now.Team),
                });
            }

            return samples;
        }
        finally
        {
            teams?.Dispose();
            cmd.Release();
            fields.Dispose();
            particles.Dispose();
            IReadOnlyList<SimPass> passes = copy.Passes;
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i] is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            UnityEngine.Object.DestroyImmediate(copy);
        }
    }

    internal static Metrics Measure(Vector2[] position, Vector2[] heading, Vector2[] previous, int[] team)
    {
        int count = position.Length;
        double radius2 = Radius * Radius;
        int contact = 0;
        long enemy = 0;
        long pairs = 0;
        double polSum = 0.0;
        int polCount = 0;
        var parent = new int[count];
        for (int i = 0; i < count; i++)
        {
            parent[i] = i;
        }

        for (int i = 0; i < count; i++)
        {
            bool hasEnemy = false;
            double headingX = 0.0;
            double headingY = 0.0;
            int own = 0;
            for (int j = 0; j < count; j++)
            {
                if (j == i)
                {
                    continue;
                }

                double dx = MinImage(position[j].x - position[i].x);
                double dy = MinImage(position[j].y - position[i].y);
                if (dx * dx + dy * dy >= radius2)
                {
                    continue;
                }

                pairs++;
                if (team[j] != team[i])
                {
                    enemy++;
                    hasEnemy = true;
                    continue;
                }

                Link(parent, i, j);
                headingX += heading[j].x;
                headingY += heading[j].y;
                own++;
            }

            if (hasEnemy)
            {
                contact++;
            }

            if (own > 0)
            {
                polSum += Math.Sqrt(headingX * headingX + headingY * headingY) / own;
                polCount++;
            }
        }

        var size = new int[count];
        for (int i = 0; i < count; i++)
        {
            size[Find(parent, i)]++;
        }

        int flocks0 = 0;
        int flocks1 = 0;
        for (int i = 0; i < count; i++)
        {
            if (Find(parent, i) != i || size[i] < FlockSize)
            {
                continue;
            }

            if (team[i] == 0)
            {
                flocks0++;
            }
            else if (team[i] == 1)
            {
                flocks1++;
            }
        }

        double turn = 0.0;
        double turnSd = 0.0;
        if (previous != null)
        {
            double sum = 0.0;
            double square = 0.0;
            for (int i = 0; i < count; i++)
            {
                double cross = previous[i].x * heading[i].y - previous[i].y * heading[i].x;
                double dot = previous[i].x * heading[i].x + previous[i].y * heading[i].y;
                double angle = Math.Abs(Math.Atan2(cross, dot));
                sum += angle;
                square += angle * angle;
            }

            turn = sum / count;
            turnSd = Math.Sqrt(Math.Max(0.0, square / count - turn * turn));
        }

        return new Metrics
        {
            Contact = 100.0 * contact / count,
            Enemy = pairs == 0 ? 0.0 : 100.0 * enemy / pairs,
            Flocks0 = flocks0,
            Flocks1 = flocks1,
            Flocks = flocks0 + flocks1,
            Pol = polCount == 0 ? 0.0 : polSum / polCount,
            Turn = turn,
            TurnSd = turnSd,
        };
    }

    private static double MinImage(double delta)
    {
        return delta - World * Math.Round(delta / World, MidpointRounding.AwayFromZero);
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Link(int[] parent, int left, int right)
    {
        int a = Find(parent, left);
        int b = Find(parent, right);
        if (a != b)
        {
            parent[a] = b;
        }
    }

    private static Vector2[] Chain(int count)
    {
        var position = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            position[i] = new Vector2(i, 0f);
        }

        return position;
    }

    private static Vector2[] ChainHeading(int count)
    {
        var heading = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            heading[i] = new Vector2(1f, 0f);
        }

        return heading;
    }

    private static int[] ChainTeam(int count)
    {
        return new int[count];
    }

    private static Shot Capture(ParticleSet particles)
    {
        int count = particles.Count;
        var position = new Vector3[count];
        var heading = new Vector3[count];
        var team = new uint[count];
        particles.Get(BuiltinAttributes.Position).GetData(position);
        particles.Get(BuiltinAttributes.Heading).GetData(heading);
        particles.Get(BuiltinAttributes.TeamId).GetData(team);
        var shot = new Shot
        {
            Position = new Vector2[count],
            Heading = new Vector2[count],
            Team = new int[count],
        };
        for (int i = 0; i < count; i++)
        {
            shot.Position[i] = new Vector2(position[i].x, position[i].z);
            shot.Heading[i] = new Vector2(heading[i].x, heading[i].z);
            shot.Team[i] = (int)team[i];
        }

        return shot;
    }

    private static string ToCsv(List<Sample> samples)
    {
        var text = new StringBuilder();
        text.AppendLine("fps,seed,variant,t,contact,enemy,flocks0,flocks1,flocks,pol,turn,turnSd");
        for (int i = 0; i < samples.Count; i++)
        {
            Sample sample = samples[i];
            Metrics metrics = sample.Metrics;
            text.Append(sample.Fps.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(sample.Seed.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(Name(sample.Variant));
            text.Append(',').Append(sample.Time.ToString("0.#", CultureInfo.InvariantCulture));
            text.Append(',').Append(Num(metrics.Contact));
            text.Append(',').Append(Num(metrics.Enemy));
            text.Append(',').Append(metrics.Flocks0.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(metrics.Flocks1.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(metrics.Flocks.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(Num(metrics.Pol));
            text.Append(',').Append(Num(metrics.Turn));
            text.Append(',').Append(Num(metrics.TurnSd));
            text.AppendLine();
        }

        return text.ToString();
    }

    private static string ToMarkdown(List<Sample> samples, double seconds)
    {
        var text = new StringBuilder();
        text.AppendLine("# Зонд поведения");
        text.AppendLine();
        text.AppendLine(
            "Длительность прогона 15,1 с. K = 1 выставляет `RefreshInterval = 1` на экземпляре. " +
            "K = 3 оставляет значение ассета. Полная матрица заняла " +
            seconds.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " с.");
        text.AppendLine();
        text.AppendLine("Сводка по 5 seed: среднее, минимум, максимум.");
        text.AppendLine();
        text.AppendLine("| вариант | FPS | t, с | contact | enemy | flocks | pol | turn | turnSd |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        var variants = new[] { Variant.Uncapped, Variant.Cap48, Variant.FieldK1, Variant.FieldK3 };
        int[] rates = { 30, 60 };
        double[] stamps = { 1.0, 5.0, 15.0 };
        for (int v = 0; v < variants.Length; v++)
        {
            for (int r = 0; r < rates.Length; r++)
            {
                for (int s = 0; s < stamps.Length; s++)
                {
                    AppendSummary(text, samples, variants[v], rates[r], stamps[s]);
                }
            }
        }

        text.AppendLine();
        text.AppendLine("Строки по seed лежат в CSV.");
        text.AppendLine();
        text.AppendLine("Шум курса к эталону без капа при том же FPS, среднее `turn`.");
        text.AppendLine();
        text.AppendLine("| вариант | FPS | t, с | turn | эталон | отношение |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- |");
        for (int v = 0; v < variants.Length; v++)
        {
            if (variants[v] == Variant.Uncapped)
            {
                continue;
            }

            for (int r = 0; r < rates.Length; r++)
            {
                for (int s = 0; s < stamps.Length; s++)
                {
                    double turn = Mean(samples, variants[v], rates[r], stamps[s], static m => m.Turn);
                    double reference = Mean(samples, Variant.Uncapped, rates[r], stamps[s], static m => m.Turn);
                    text.Append("| ").Append(Name(variants[v]));
                    text.Append(" | ").Append(rates[r].ToString(CultureInfo.InvariantCulture));
                    text.Append(" | ").Append(stamps[s].ToString("0.#", CultureInfo.InvariantCulture));
                    text.Append(" | ").Append(Comma(turn));
                    text.Append(" | ").Append(Comma(reference));
                    text.Append(" | ").Append(reference == 0.0 ? "" : Comma(turn / reference));
                    text.AppendLine(" |");
                }
            }
        }

        text.AppendLine();
        text.AppendLine("Таблица гейта ADR-062 §2.7, t = 5 с, среднее по 5 seed. Вердикта нет.");
        text.AppendLine();
        text.AppendLine("| условие | число |");
        text.AppendLine("| --- | --- |");
        AppendGate(text, samples, Variant.FieldK1, 30, "contact");
        AppendGate(text, samples, Variant.FieldK1, 60, "contact");
        AppendGate(text, samples, Variant.FieldK1, 30, "enemy");
        AppendGate(text, samples, Variant.FieldK1, 60, "enemy");
        AppendGate(text, samples, Variant.FieldK1, 30, "flocks");
        AppendGate(text, samples, Variant.FieldK1, 60, "flocks");
        AppendGate(text, samples, Variant.Cap48, 30, "contact");
        AppendGate(text, samples, Variant.Cap48, 60, "contact");
        text.AppendLine();
        text.AppendLine("Те же величины для K = 3 и для эталона без капа.");
        text.AppendLine();
        text.AppendLine("| условие | число |");
        text.AppendLine("| --- | --- |");
        AppendGate(text, samples, Variant.FieldK3, 30, "contact");
        AppendGate(text, samples, Variant.FieldK3, 60, "contact");
        AppendGate(text, samples, Variant.FieldK3, 30, "enemy");
        AppendGate(text, samples, Variant.FieldK3, 60, "enemy");
        AppendGate(text, samples, Variant.FieldK3, 30, "flocks");
        AppendGate(text, samples, Variant.FieldK3, 60, "flocks");
        AppendGate(text, samples, Variant.Uncapped, 30, "contact");
        AppendGate(text, samples, Variant.Uncapped, 60, "contact");
        return text.ToString();
    }

    private static void AppendSummary(StringBuilder text, List<Sample> samples, Variant variant, int fps, double time)
    {
        text.Append("| ").Append(Name(variant));
        text.Append(" | ").Append(fps.ToString(CultureInfo.InvariantCulture));
        text.Append(" | ").Append(time.ToString("0.#", CultureInfo.InvariantCulture));
        text.Append(" | ").Append(Band(samples, variant, fps, time, static m => m.Contact));
        text.Append(" | ").Append(Band(samples, variant, fps, time, static m => m.Enemy));
        text.Append(" | ").Append(Band(samples, variant, fps, time, static m => m.Flocks));
        text.Append(" | ").Append(Band(samples, variant, fps, time, static m => m.Pol));
        text.Append(" | ").Append(Band(samples, variant, fps, time, static m => m.Turn));
        text.Append(" | ").Append(Band(samples, variant, fps, time, static m => m.TurnSd));
        text.AppendLine(" |");
    }

    private static string Band(
        List<Sample> samples, Variant variant, int fps, double time, Func<Metrics, double> pick)
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            Sample sample = samples[i];
            if (sample.Variant != variant || sample.Fps != fps || Math.Abs(sample.Time - time) > 1e-6)
            {
                continue;
            }

            double value = pick(sample.Metrics);
            sum += value;
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }

            count++;
        }

        if (count == 0)
        {
            return "";
        }

        return Comma(sum / count) + " (" + Comma(min) + "…" + Comma(max) + ")";
    }

    private static double Mean(
        List<Sample> samples, Variant variant, int fps, double time, Func<Metrics, double> pick)
    {
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            Sample sample = samples[i];
            if (sample.Variant != variant || sample.Fps != fps || Math.Abs(sample.Time - time) > 1e-6)
            {
                continue;
            }

            sum += pick(sample.Metrics);
            count++;
        }

        return count == 0 ? double.NaN : sum / count;
    }

    private static void AppendGate(StringBuilder text, List<Sample> samples, Variant variant, int fps, string metric)
    {
        Func<Metrics, double> pick = metric == "contact"
            ? static m => m.Contact
            : metric == "enemy"
                ? static m => m.Enemy
                : static m => m.Flocks;
        text.Append("| ").Append(Name(variant)).Append(" ").Append(fps.ToString(CultureInfo.InvariantCulture));
        text.Append(" FPS ").Append(metric);
        text.Append(" | ").Append(Comma(Mean(samples, variant, fps, 5.0, pick)));
        text.AppendLine(" |");
    }

    private static string Name(Variant variant)
    {
        switch (variant)
        {
            case Variant.Uncapped:
                return "uncapped";
            case Variant.Cap48:
                return "cap48";
            case Variant.FieldK1:
                return "field-K1";
            default:
                return "field-K3";
        }
    }

    private static string Num(double value)
    {
        return value.ToString("G8", CultureInfo.InvariantCulture);
    }

    private static string Comma(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "";
        }

        return value.ToString("G6", CultureInfo.InvariantCulture).Replace('.', ',');
    }

    private static void AssertFinite(Metrics metrics)
    {
        AssertFinite(metrics.Contact, "contact");
        AssertFinite(metrics.Enemy, "enemy");
        AssertFinite(metrics.Pol, "pol");
        AssertFinite(metrics.Turn, "turn");
        AssertFinite(metrics.TurnSd, "turnSd");
    }

    private static void AssertFinite(double value, string label)
    {
        Assert.IsFalse(double.IsNaN(value) || double.IsInfinity(value), label);
    }

    private static FieldNeighborCollectPass AssetCollect()
    {
        return FindCollect(AssetDatabase.LoadAssetAtPath<EffectAsset>(FieldPath).Passes);
    }

    private static BoidNeighborForcePass AssetWalk()
    {
        return FindWalk(AssetDatabase.LoadAssetAtPath<EffectAsset>(WalkPath).Passes);
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

    private static BoidNeighborForcePass FindWalk(IReadOnlyList<SimPass> passes)
    {
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is BoidNeighborForcePass force)
            {
                return force;
            }
        }

        Assert.Fail("walk pass missing");
        return null;
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
        var shaders = new ComputeShader[paths.Length];
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
        if (ids == null)
        {
            return;
        }

        for (int i = 0; i < ids.Count; i++)
        {
            if (!particles.Schema.Has(ids[i]))
            {
                GraphicsBuffer buffer = particles.RegisterAttribute(ids[i]);
                buffer.SetData(new byte[buffer.count * buffer.stride]);
            }
        }
    }

    private enum Variant
    {
        Uncapped,
        Cap48,
        FieldK1,
        FieldK3,
    }

    private struct Shot
    {
        public Vector2[] Position;
        public Vector2[] Heading;
        public int[] Team;
    }

    internal struct Metrics
    {
        public double Contact;
        public double Enemy;
        public int Flocks0;
        public int Flocks1;
        public int Flocks;
        public double Pol;
        public double Turn;
        public double TurnSd;
    }

    private struct Sample
    {
        public int Fps;
        public int Seed;
        public Variant Variant;
        public double Time;
        public Metrics Metrics;
    }
}
