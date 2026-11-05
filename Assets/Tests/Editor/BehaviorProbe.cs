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
/// Shape metrics (rho, thin, cells, axis) are a separate pass and do not enter the §2.7 numbers.
/// </summary>
[TestFixture]
public class BehaviorProbe
{
    private const string FieldPath = "Assets/Effects/Boids_hash_2teams_field.asset";
    private const string WalkPath = "Assets/Effects/Boids_hash_2teams.asset";
    private const float World = 90f;
    private const float Radius = 3f;
    private const int FlockSize = 30;
    private const string ShapeStem = "shape-9696694";

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
    public void Shape_VogelDisk_ThinMedianStaysRound()
    {
        const int count = 200;
        var position = new Vector2[count];
        var heading = new Vector2[count];
        var team = new int[count];
        double golden = Math.PI * (3.0 - Math.Sqrt(5.0));
        for (int k = 0; k < count; k++)
        {
            double radius = 2.0 * Math.Sqrt((k + 0.5) / count);
            double angle = k * golden;
            position[k] = new Vector2((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)));
            heading[k] = new Vector2(1f, 0f);
        }

        ShapeMetrics shape = MeasureShape(position, heading, team);
        Assert.AreEqual(count, shape.NThin);
        Assert.IsTrue(shape.ThinMed.HasValue);
        Assert.GreaterOrEqual(shape.ThinMed.Value, 0.8);
        AssertDefined(shape);
    }

    [Test]
    public void Shape_StraightChain_IsThinAlongX_AndNotAt45Degrees()
    {
        Vector2[] heading = LineHeading(60);
        int[] team = LineTeam(60);
        ShapeMetrics alongX = MeasureShape(Line(60, 0.0), heading, team);
        Assert.AreEqual(60, alongX.NThin);
        Assert.AreEqual(0.0, alongX.ThinMed.Value, 1e-6);
        Assert.AreEqual(1.0, alongX.FracThin.Value, 0.0);
        Assert.AreEqual(1.0, alongX.AxisLine.Value, 0.0);

        ShapeMetrics diagonal = MeasureShape(Line(60, Math.PI / 4.0), heading, team);
        Assert.AreEqual(60, diagonal.NThin);
        Assert.AreEqual(1.0, diagonal.FracThin.Value, 0.0);
        Assert.AreEqual(0.0, diagonal.AxisLine.Value, 0.0);
        AssertDefined(alongX);
        AssertDefined(diagonal);
    }

    [Test]
    public void Shape_LatticeCenter_MatchesHandDensity()
    {
        const int width = 15;
        var position = new Vector2[width * width];
        var heading = new Vector2[width * width];
        var team = new int[width * width];
        int center = 0;
        int cursor = 0;
        for (int ix = -7; ix <= 7; ix++)
        {
            for (int iz = -7; iz <= 7; iz++)
            {
                if (ix == 0 && iz == 0)
                {
                    center = cursor;
                }

                position[cursor] = new Vector2(ix * 0.5f, iz * 0.5f);
                heading[cursor] = new Vector2(1f, 0f);
                cursor++;
            }
        }

        var rho = new double[width * width];
        MeasureShape(position, heading, team, rho);
        Assert.AreEqual(108.0 / (9.0 * Math.PI), rho[center], 1e-9);
    }

    [Test]
    public void Shape_Cells_CountDistinctMeters()
    {
        var piled = new Vector2[100];
        var heading = new Vector2[100];
        var team = new int[100];
        for (int i = 0; i < piled.Length; i++)
        {
            piled[i] = new Vector2(0.2f * (i % 3), 0.2f * (i % 2));
            heading[i] = new Vector2(1f, 0f);
        }

        Assert.AreEqual(1, MeasureShape(piled, heading, team).Cells);

        var grid = new Vector2[100];
        var gridHeading = new Vector2[100];
        var gridTeam = new int[100];
        for (int i = 0; i < 10; i++)
        {
            for (int j = 0; j < 10; j++)
            {
                grid[i * 10 + j] = new Vector2(i, j);
                gridHeading[i * 10 + j] = new Vector2(1f, 0f);
            }
        }

        Assert.AreEqual(100, MeasureShape(grid, gridHeading, gridTeam).Cells);
    }

    [Test]
    public void Shape_TorusSeam_KeepsTheChain()
    {
        Vector2[] plain = Line(60, 0.0);
        var wrapped = new Vector2[plain.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            wrapped[i] = new Vector2((float)WrapAxis(plain[i].x + 44.0), plain[i].y);
        }

        Vector2[] heading = LineHeading(plain.Length);
        int[] team = LineTeam(plain.Length);
        ShapeMetrics open = MeasureShape(plain, heading, team);
        ShapeMetrics seam = MeasureShape(wrapped, heading, team);
        Assert.AreEqual(open.ThinMed.Value, seam.ThinMed.Value, 1e-9);
        Assert.AreEqual(open.NThin, seam.NThin);
    }

    [Test]
    public void Shape_AxisHead_CountsCoursesNearTheWorldAxes()
    {
        Assert.AreEqual(1.0, HeadingsAt(0.0).AxisHead, 0.0);
        Assert.AreEqual(1.0, HeadingsAt(3.0).AxisHead, 0.0);
        Assert.AreEqual(1.0, HeadingsAt(85.0).AxisHead, 0.0);
        Assert.AreEqual(1.0, HeadingsAt(90.0).AxisHead, 0.0);
        Assert.AreEqual(0.0, HeadingsAt(20.0).AxisHead, 0.0);
        Assert.AreEqual(0.0, HeadingsAt(45.0).AxisHead, 0.0);
    }

    [Test]
    public void Shape_TooFewPoints_LeaveThinBlank()
    {
        var onePosition = new[] { Vector2.zero };
        var oneHeading = new[] { new Vector2(1f, 0f) };
        var oneTeam = new[] { 0 };
        ShapeMetrics one = MeasureShape(onePosition, oneHeading, oneTeam);
        Assert.AreEqual(0, one.NThin);
        Assert.IsFalse(one.ThinMed.HasValue);
        Assert.IsFalse(one.FracThin.HasValue);
        Assert.IsFalse(one.AxisLine.HasValue);
        Assert.AreEqual(0.0, one.RhoMed, 0.0);
        Assert.AreEqual(1, one.Cells);
        AssertDefined(one);

        var fewPosition = new Vector2[5];
        var fewHeading = new Vector2[5];
        var fewTeam = new int[5];
        for (int i = 0; i < fewPosition.Length; i++)
        {
            fewPosition[i] = new Vector2(0.2f * i, 0f);
            fewHeading[i] = new Vector2(1f, 0f);
        }

        ShapeMetrics few = MeasureShape(fewPosition, fewHeading, fewTeam);
        Assert.AreEqual(0, few.NThin);
        Assert.IsFalse(few.ThinMed.HasValue);
        Assert.IsFalse(few.FracThin.HasValue);
        Assert.IsFalse(few.AxisLine.HasValue);
        AssertDefined(few);

        var otherTeam = new[] { 1, 1, 1, 1, 1 };
        ShapeMetrics emptyTeam = MeasureShape(fewPosition, fewHeading, otherTeam);
        Assert.AreEqual(0, emptyTeam.NThin);
        Assert.IsFalse(emptyTeam.ThinMed.HasValue);
        AssertDefined(emptyTeam);

        ShapeMetrics six = MeasureShape(Line(6, 0.0), LineHeading(6), LineTeam(6));
        Assert.AreEqual(6, six.NThin);
        Assert.AreEqual(0.0, six.ThinMed.Value, 1e-6);
        AssertDefined(six);
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

    [Test]
    [Explicit]
    [Timeout(1800000)]
    public void ShapeMatrix_FiveSeeds_TwoRates_FourVariants()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        var watch = Stopwatch.StartNew();
        int[] rates = { 30, 60 };
        var variants = new[] { Variant.Uncapped, Variant.Cap48, Variant.FieldK1, Variant.FieldK3 };
        double[] times = { 2.0, 5.0, 10.0, 15.0 };
        var samples = new List<ShapeSample>();
        for (int r = 0; r < rates.Length; r++)
        {
            for (int v = 0; v < variants.Length; v++)
            {
                for (int seed = 1; seed <= 5; seed++)
                {
                    samples.AddRange(RunShape(variants[v], rates[r], seed, 15.1, times));
                }
            }
        }

        watch.Stop();
        Assert.AreEqual(3, AssetCollect().RefreshInterval);
        Assert.AreEqual(48, AssetWalk().MaxNeighbors);
        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DOC", "MEASURE", "p8-field-force"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ShapeStem + ".csv"), ShapeToCsv(samples), new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(dir, ShapeStem + ".md"),
            ShapeToMarkdown(samples, watch.Elapsed.TotalSeconds),
            new UTF8Encoding(false));
        for (int i = 0; i < samples.Count; i++)
        {
            AssertDefined(samples[i].Shape);
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

    private static List<ShapeSample> RunShape(Variant variant, int fps, int seed, double seconds, double[] times)
    {
        bool field = variant == Variant.FieldK1 || variant == Variant.FieldK3;
        string path = field ? FieldPath : WalkPath;
        EffectAsset copy = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<EffectAsset>(path));
        copy.name = "BehaviorProbeShape_" + variant + "_" + fps + "_" + seed;
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
        CommandBuffer cmd = new CommandBuffer { name = "BehaviorProbeShape" };
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
                wanted.Add((int)Math.Round(times[i] * fps, MidpointRounding.AwayFromZero));
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

            var samples = new List<ShapeSample>();
            for (int i = 0; i < times.Length; i++)
            {
                int frame = (int)Math.Round(times[i] * fps, MidpointRounding.AwayFromZero);
                Shot now = captured[frame];
                samples.Add(new ShapeSample
                {
                    Fps = fps,
                    Seed = seed,
                    Variant = variant,
                    Time = times[i],
                    Shape = MeasureShape(now.Position, now.Heading, now.Team),
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

    internal static ShapeMetrics MeasureShape(
        Vector2[] position, Vector2[] heading, int[] team, double[] rhoOut = null)
    {
        int count = position.Length;
        double radius2 = Radius * Radius;
        double area = Math.PI * radius2;
        var rho = new List<double>(count);
        var thin = new List<double>();
        int lineCount = 0;
        int lineHits = 0;
        int headHits = 0;
        var cells = new HashSet<long>();
        var dxs = new List<double>(64);
        var dys = new List<double>(64);
        for (int i = 0; i < count; i++)
        {
            dxs.Clear();
            dys.Clear();
            for (int j = 0; j < count; j++)
            {
                if (j == i || team[j] != team[i])
                {
                    continue;
                }

                double dx = MinImage(position[j].x - position[i].x);
                double dy = MinImage(position[j].y - position[i].y);
                if (dx * dx + dy * dy >= radius2)
                {
                    continue;
                }

                dxs.Add(dx);
                dys.Add(dy);
            }

            double density = dxs.Count / area;
            rho.Add(density);
            if (rhoOut != null)
            {
                rhoOut[i] = density;
            }

            int sample = dxs.Count + 1;
            if (sample >= 6)
            {
                double sumX = 0.0;
                double sumY = 0.0;
                for (int k = 0; k < dxs.Count; k++)
                {
                    sumX += dxs[k];
                    sumY += dys[k];
                }

                double meanX = sumX / sample;
                double meanY = sumY / sample;
                double cxx = meanX * meanX;
                double cyy = meanY * meanY;
                double cxy = meanX * meanY;
                for (int k = 0; k < dxs.Count; k++)
                {
                    double ex = dxs[k] - meanX;
                    double ey = dys[k] - meanY;
                    cxx += ex * ex;
                    cyy += ey * ey;
                    cxy += ex * ey;
                }

                double inv = 1.0 / (sample - 1);
                cxx *= inv;
                cyy *= inv;
                cxy *= inv;
                double diff = cxx - cyy;
                double disc = Math.Sqrt(Math.Max(0.0, diff * diff + 4.0 * cxy * cxy));
                double lMax = (cxx + cyy + disc) * 0.5;
                double lMin = (cxx + cyy - disc) * 0.5;
                if (lMin < 0.0)
                {
                    lMin = 0.0;
                }

                double value = lMax <= 1e-12 ? 1.0 : Math.Sqrt(lMin / lMax);
                thin.Add(value);
                if (value < 0.25)
                {
                    PrincipalAxis(cxx, cyy, cxy, lMax, out double axisX, out double axisY);
                    lineCount++;
                    if (NearWorldAxis(axisX, axisY, 10.0))
                    {
                        lineHits++;
                    }
                }
            }

            if (NearWorldAxis(heading[i].x, heading[i].y, 5.0))
            {
                headHits++;
            }

            int cellX = (int)Math.Floor(position[i].x + 45.0);
            int cellZ = (int)Math.Floor(position[i].y + 45.0);
            cells.Add(((long)cellX << 32) | (uint)cellZ);
        }

        return new ShapeMetrics
        {
            RhoMed = ShapePercentile(rho, 50.0),
            RhoP90 = ShapePercentile(rho, 90.0),
            ThinMed = thin.Count == 0 ? (double?)null : ShapePercentile(thin, 50.0),
            FracThin = thin.Count == 0 ? (double?)null : (double)lineCount / thin.Count,
            NThin = thin.Count,
            Cells = cells.Count,
            AxisHead = count == 0 ? 0.0 : (double)headHits / count,
            AxisLine = lineCount == 0 ? (double?)null : (double)lineHits / lineCount,
        };
    }

    private static void PrincipalAxis(
        double cxx, double cyy, double cxy, double lMax, out double axisX, out double axisY)
    {
        if (Math.Abs(cxy) <= 1e-15)
        {
            if (cxx >= cyy)
            {
                axisX = 1.0;
                axisY = 0.0;
            }
            else
            {
                axisX = 0.0;
                axisY = 1.0;
            }

            return;
        }

        axisX = cxy;
        axisY = lMax - cxx;
    }

    private static bool NearWorldAxis(double axisX, double axisY, double limitDegrees)
    {
        double degrees = Math.Atan2(axisY, axisX) * (180.0 / Math.PI);
        double folded = Math.Abs(degrees) % 90.0;
        double distance = Math.Min(folded, 90.0 - folded);
        return distance <= limitDegrees;
    }

    private static double ShapePercentile(List<double> values, double percentile)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        double rank = percentile / 100.0 * (sorted.Count - 1);
        int low = (int)Math.Floor(rank);
        int high = (int)Math.Ceiling(rank);
        double t = rank - low;
        return sorted[low] * (1.0 - t) + sorted[high] * t;
    }

    private static double WrapAxis(double value)
    {
        double local = value + 45.0;
        double wrapped = local - World * Math.Floor(local / World);
        return wrapped - 45.0;
    }

    private static Vector2[] Line(int count, double angle)
    {
        var position = new Vector2[count];
        float along = (float)Math.Cos(angle);
        float across = (float)Math.Sin(angle);
        if (Math.Abs(angle - Math.PI / 4.0) < 1e-12)
        {
            float step = 0.1f * (float)Math.Sqrt(0.5);
            along = step;
            across = step;
            for (int i = 0; i < count; i++)
            {
                position[i] = new Vector2(along * i, across * i);
            }

            return position;
        }

        for (int i = 0; i < count; i++)
        {
            float distance = 0.1f * i;
            position[i] = new Vector2(distance * along, distance * across);
        }

        return position;
    }

    private static Vector2[] LineHeading(int count)
    {
        var heading = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            heading[i] = new Vector2(1f, 0f);
        }

        return heading;
    }

    private static int[] LineTeam(int count)
    {
        return new int[count];
    }

    private static ShapeMetrics HeadingsAt(params double[] degrees)
    {
        var position = new Vector2[degrees.Length];
        var heading = new Vector2[degrees.Length];
        var team = new int[degrees.Length];
        for (int i = 0; i < degrees.Length; i++)
        {
            position[i] = new Vector2(i * 10f, 0f);
            double radians = degrees[i] * Math.PI / 180.0;
            heading[i] = new Vector2((float)Math.Cos(radians), (float)Math.Sin(radians));
        }

        return MeasureShape(position, heading, team);
    }

    private static string ShapeToCsv(List<ShapeSample> samples)
    {
        var text = new StringBuilder();
        text.AppendLine("fps,seed,variant,t,rhoMed,rhoP90,thinMed,fracThin,nThin,cells,axisHead,axisLine");
        for (int i = 0; i < samples.Count; i++)
        {
            ShapeSample sample = samples[i];
            ShapeMetrics shape = sample.Shape;
            text.Append(sample.Fps.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(sample.Seed.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(Name(sample.Variant));
            text.Append(',').Append(sample.Time.ToString("0.#", CultureInfo.InvariantCulture));
            text.Append(',').Append(Num(shape.RhoMed));
            text.Append(',').Append(Num(shape.RhoP90));
            text.Append(',').Append(NumOpt(shape.ThinMed));
            text.Append(',').Append(NumOpt(shape.FracThin));
            text.Append(',').Append(shape.NThin.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(shape.Cells.ToString(CultureInfo.InvariantCulture));
            text.Append(',').Append(Num(shape.AxisHead));
            text.Append(',').Append(NumOpt(shape.AxisLine));
            text.AppendLine();
        }

        return text.ToString();
    }

    private static string ShapeToMarkdown(List<ShapeSample> samples, double seconds)
    {
        var text = new StringBuilder();
        text.AppendLine("# Метрики формы");
        text.AppendLine();
        text.AppendLine(
            "Длительность прогона 15,1 с. K = 1 выставляет `RefreshInterval = 1` на экземпляре. " +
            "K = 3 оставляет значение ассета. Полная матрица заняла " +
            seconds.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " с.");
        text.AppendLine();
        text.AppendLine(
            "Сводка по seed, у которых метрика определена. `seedsWithData` — их число. " +
            "Пустая сводка — прочерк. `nThin` и `cells` определены всегда.");
        text.AppendLine();
        text.AppendLine("| вариант | FPS | t, с | метрика | среднее | минимум | максимум | seedsWithData |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        var variants = new[] { Variant.Uncapped, Variant.Cap48, Variant.FieldK1, Variant.FieldK3 };
        int[] rates = { 30, 60 };
        double[] stamps = { 2.0, 5.0, 10.0, 15.0 };
        string[] metrics = { "rhoMed", "rhoP90", "thinMed", "fracThin", "nThin", "cells", "axisHead", "axisLine" };
        for (int v = 0; v < variants.Length; v++)
        {
            for (int r = 0; r < rates.Length; r++)
            {
                for (int s = 0; s < stamps.Length; s++)
                {
                    for (int m = 0; m < metrics.Length; m++)
                    {
                        AppendShapeSummary(text, samples, variants[v], rates[r], stamps[s], metrics[m]);
                    }
                }
            }
        }

        text.AppendLine();
        text.AppendLine("Строки по seed лежат в CSV. Вердикта нет.");
        return text.ToString();
    }

    private static void AppendShapeSummary(
        StringBuilder text, List<ShapeSample> samples, Variant variant, int fps, double time, string metric)
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            ShapeSample sample = samples[i];
            if (sample.Variant != variant || sample.Fps != fps || Math.Abs(sample.Time - time) > 1e-6)
            {
                continue;
            }

            if (!TryShape(sample.Shape, metric, out double value))
            {
                continue;
            }

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

        text.Append("| ").Append(Name(variant));
        text.Append(" | ").Append(fps.ToString(CultureInfo.InvariantCulture));
        text.Append(" | ").Append(time.ToString("0.#", CultureInfo.InvariantCulture));
        text.Append(" | ").Append(metric);
        if (count == 0)
        {
            text.Append(" | — | — | — | 0 |");
        }
        else
        {
            text.Append(" | ").Append(Comma(sum / count));
            text.Append(" | ").Append(Comma(min));
            text.Append(" | ").Append(Comma(max));
            text.Append(" | ").Append(count.ToString(CultureInfo.InvariantCulture));
            text.Append(" |");
        }

        text.AppendLine();
    }

    private static bool TryShape(ShapeMetrics shape, string metric, out double value)
    {
        switch (metric)
        {
            case "rhoMed":
                value = shape.RhoMed;
                return true;
            case "rhoP90":
                value = shape.RhoP90;
                return true;
            case "thinMed":
                value = shape.ThinMed ?? 0.0;
                return shape.ThinMed.HasValue;
            case "fracThin":
                value = shape.FracThin ?? 0.0;
                return shape.FracThin.HasValue;
            case "nThin":
                value = shape.NThin;
                return true;
            case "cells":
                value = shape.Cells;
                return true;
            case "axisHead":
                value = shape.AxisHead;
                return true;
            default:
                value = shape.AxisLine ?? 0.0;
                return shape.AxisLine.HasValue;
        }
    }

    private static string NumOpt(double? value)
    {
        return value.HasValue ? Num(value.Value) : "";
    }

    private static void AssertDefined(ShapeMetrics shape)
    {
        AssertFinite(shape.RhoMed, "rhoMed");
        AssertFinite(shape.RhoP90, "rhoP90");
        AssertFinite(shape.AxisHead, "axisHead");
        Assert.GreaterOrEqual(shape.NThin, 0);
        Assert.GreaterOrEqual(shape.Cells, 0);
        if (shape.ThinMed.HasValue)
        {
            AssertFinite(shape.ThinMed.Value, "thinMed");
        }

        if (shape.FracThin.HasValue)
        {
            AssertFinite(shape.FracThin.Value, "fracThin");
        }

        if (shape.AxisLine.HasValue)
        {
            AssertFinite(shape.AxisLine.Value, "axisLine");
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

    internal struct ShapeMetrics
    {
        public double RhoMed;
        public double RhoP90;
        public double? ThinMed;
        public double? FracThin;
        public int NThin;
        public int Cells;
        public double AxisHead;
        public double? AxisLine;
    }

    private struct ShapeSample
    {
        public int Fps;
        public int Seed;
        public Variant Variant;
        public double Time;
        public ShapeMetrics Shape;
    }
}
