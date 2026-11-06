using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// ADR-062 §2.6. Snapshots come from the field preset run at K = 1.
/// The asset stays at RefreshInterval 3; only the in-memory copy is set to 1.
/// Force error is the whole Scatter path against the uncapped walk. Nothing is subtracted.
/// </summary>
[TestFixture]
public class FieldForceVsTraversalTests
{
    private const string FieldPath = "Assets/Effects/Boids_hash_2teams_field.asset";
    private const string WalkPath = "Assets/Effects/Boids_hash_2teams.asset";
    private const string HashShaderPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string FieldShaderPath = "Assets/Shaders/GPU/Passes/FieldNeighborPasses.compute";
    private const string DynamicsShaderPath = "Assets/Shaders/GPU/Passes/DynamicsPasses.compute";
    private const string BoidShaderPath = "Assets/Shaders/GPU/Passes/BoidsPasses.compute";
    private const double MagnitudeFloor = 1e-3;
    private const double AngleLengthFloor = 1e-8;
    private const double CohDisplacementFloor = 0.1;
    private const int Frames = 900;

    private static readonly string[] Computers = { "field", "cap48", "cap192" };
    private static readonly string[] Groups = { "all", "inside", "edge" };

    [Test]
    [Timeout(600000)]
    public void Snapshots_AreFinite_GroupsOccupied_ReferenceHasNeighbors()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(FieldPath);
        Assert.IsNotNull(asset, FieldPath);
        FieldNeighborCollectPass assetCollect = FindCollect(asset.Passes);
        Assert.AreEqual(3, assetCollect.RefreshInterval);

        Shot[] shots = RunSnapshots();
        Assert.AreEqual(3, assetCollect.RefreshInterval, "the field asset RefreshInterval was changed");

        EffectAsset walk = AssetDatabase.LoadAssetAtPath<EffectAsset>(WalkPath);
        BuildSpatialHashPass walkHash = FindHash(walk.Passes);
        BoidNeighborForcePass walkForce = FindWalk(walk.Passes);
        Assert.AreEqual(3f, walkHash.MinCellSize);
        Assert.AreEqual(1, walkForce.WalkMode);
        Assert.IsFalse(walkForce.AlignFromVelocity);

        FieldNeighborCollectPass fieldCollect = FindCollect(asset.Passes);
        Vector3 center = fieldCollect.Center;
        Vector3 extents = fieldCollect.Extents;
        float texel = fieldCollect.TexelSize;
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(center, extents, texel);
        Vector2 world = new Vector2(extents.x * 2f, extents.z * 2f);
        Vector3[] weights = Weights(asset);
        double[] inter = Inter(asset);
        double radius = asset.Teams[0].SeparationRadius;

        var rows = new List<Row>();
        var notes = new List<string>();
        using (FieldEval field = new FieldEval(shots[0], center, extents, texel, asset))
        using (WalkEval cap48 = new WalkEval(shots[0], walkHash, 48, walkForce.WalkMode, asset))
        using (WalkEval cap192 = new WalkEval(shots[0], walkHash, 192, walkForce.WalkMode, asset))
        {
            for (int s = 0; s < shots.Length; s++)
            {
                Shot shot = shots[s];
                FieldForceReference.Sample[] reference = FieldForceReference.FullTraversal(
                    shot.Position, shot.Heading, shot.Team, weights, inter, radius, world);
                int withNeighbor = 0;
                for (int i = 0; i < reference.Length; i++)
                {
                    if (reference[i].NgRef >= 1)
                    {
                        withNeighbor++;
                    }
                }

                Assert.Greater(withNeighbor, reference.Length / 2, "t=" + shot.Time + " Ng_ref >= 1");
                double[] p90 = PercentileByTeam(reference, shot.Team, 90.0);
                int[][] members = GroupMembers(reference, shot.Team, p90);
                for (int g = 0; g < Groups.Length; g++)
                {
                    Assert.Greater(members[g].Length, 0, "t=" + shot.Time + " group " + Groups[g]);
                }

                notes.Add(
                    "t=" + shot.Time.ToString("0.#", CultureInfo.InvariantCulture) +
                    " n=" + reference.Length +
                    " Ng>=1 " + withNeighbor +
                    " P90 team0 " + p90[0].ToString("G6", CultureInfo.InvariantCulture) +
                    " team1 " + p90[1].ToString("G6", CultureInfo.InvariantCulture) +
                    " all " + members[0].Length +
                    " inside " + members[1].Length +
                    " edge " + members[2].Length);

                Vector2[] fieldForce = field.Force(shot);
                Vector4[] fieldSlots = field.ReadField();
                FieldForceReference.Sample[] fieldParts = FieldForceReference.FieldRead(
                    Pack(fieldSlots), shot.Position, shot.Heading, shot.Team, weights, layout);
                Vector2[] cap48Force = cap48.Force(shot);
                Vector2[] cap192Force = cap192.Force(shot);
                Vector2[][] forces = { fieldForce, cap48Force, cap192Force };
                FieldForceReference.Sample[][] parts = { fieldParts, null, null };
                for (int c = 0; c < Computers.Length; c++)
                {
                    for (int g = 0; g < Groups.Length; g++)
                    {
                        rows.Add(Measure(
                            shot.Time,
                            Computers[c],
                            Groups[g],
                            members[g],
                            reference,
                            forces[c],
                            parts[c]));
                    }
                }
            }
        }

        string dir = MeasureOutput.DirectoryPath();
        Directory.CreateDirectory(dir);
        string csvPath = Path.Combine(dir, "error-91e5602.csv");
        string mdPath = Path.Combine(dir, "error-91e5602.md");
        File.WriteAllText(csvPath, ToCsv(rows), new UTF8Encoding(false));
        File.WriteAllText(mdPath, ToMarkdown(rows, notes), new UTF8Encoding(false));

        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            AssertFinite(row.AngleMedian, row.Key + " angle median");
            AssertFinite(row.AngleP95, row.Key + " angle p95");
            AssertFinite(row.MagMedian, row.Key + " mag median");
            AssertFinite(row.MagP95, row.Key + " mag p95");
            if (row.Computer == "field")
            {
                AssertFinite(row.SepMedian, row.Key + " sep");
                AssertFinite(row.AlignMedian, row.Key + " align");
                AssertFinite(row.CohMedian, row.Key + " coh");
            }
        }
    }

    private static Shot[] RunSnapshots()
    {
        EffectAsset copy = UnityEngine.Object.Instantiate(
            AssetDatabase.LoadAssetAtPath<EffectAsset>(FieldPath));
        copy.name = "Boids_hash_2teams_field_K1";
        FieldNeighborCollectPass collect = FindCollect(copy.Passes);
        collect.RefreshInterval = 1;
        ParticleSet particles = new ParticleSet();
        FieldSet fields = new FieldSet();
        CommandBuffer cmd = new CommandBuffer { name = "FieldForceSnapshots" };
        GraphicsBuffer teams = null;
        try
        {
            SwarmSource swarm = (SwarmSource)copy.ResolveSource();
            swarm.Seed = 1;
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

            float dt = (1f / 60f) * copy.SimulationSpeed;
            var shots = new Shot[3];
            shots[0] = Capture(particles, 0f);
            int written = 1;
            for (int frame = 1; frame <= Frames; frame++)
            {
                cmd.Clear();
                for (int i = 0; i < passes.Count; i++)
                {
                    passes[i].Execute(context, dt);
                }

                Graphics.ExecuteCommandBuffer(cmd);
                if (frame == 300 || frame == Frames)
                {
                    shots[written] = Capture(particles, frame / 60f);
                    written++;
                }
            }

            return shots;
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

    private static Shot Capture(ParticleSet particles, float time)
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
            Time = time,
            Position = new Vector2[count],
            Heading = new Vector2[count],
            Team = new int[count],
        };
        for (int i = 0; i < count; i++)
        {
            AssertFinite(position[i].x, "position " + i);
            AssertFinite(position[i].y, "position " + i);
            AssertFinite(position[i].z, "position " + i);
            AssertFinite(heading[i].x, "heading " + i);
            AssertFinite(heading[i].z, "heading " + i);
            shot.Position[i] = new Vector2(position[i].x, position[i].z);
            shot.Heading[i] = new Vector2(heading[i].x, heading[i].z);
            shot.Team[i] = (int)team[i];
        }

        return shot;
    }

    private static Row Measure(
        float time,
        string computer,
        string group,
        int[] members,
        FieldForceReference.Sample[] reference,
        Vector2[] force,
        FieldForceReference.Sample[] parts)
    {
        var angle = new List<double>();
        var mag = new List<double>();
        var sep = new List<double>();
        var align = new List<double>();
        var coh = new List<double>();
        for (int n = 0; n < members.Length; n++)
        {
            int i = members[n];
            FieldForceReference.Sample expected = reference[i];
            double rx = expected.Fx;
            double ry = expected.Fy;
            double fx = force[i].x;
            double fy = force[i].y;
            AddAngle(angle, fx, fy, rx, ry);
            double refLen = Math.Sqrt(rx * rx + ry * ry);
            if (refLen >= MagnitudeFloor)
            {
                double len = Math.Sqrt(fx * fx + fy * fy);
                mag.Add(Math.Abs(len - refLen) / refLen);
            }

            if (parts == null)
            {
                continue;
            }

            FieldForceReference.Sample actual = parts[i];
            sep.Add(Distance(actual.SepX, actual.SepY, expected.SepX, expected.SepY));
            AddAngle(align, actual.AlignX, actual.AlignY, expected.AlignX, expected.AlignY);
            if (expected.MeanDisplacement >= CohDisplacementFloor)
            {
                AddAngle(coh, actual.CohX, actual.CohY, expected.CohX, expected.CohY);
            }
        }

        return new Row
        {
            Time = time,
            Computer = computer,
            Group = group,
            Count = members.Length,
            AngleCount = angle.Count,
            AngleMedian = Percentile(angle, 50.0),
            AngleP95 = Percentile(angle, 95.0),
            MagCount = mag.Count,
            MagMedian = Percentile(mag, 50.0),
            MagP95 = Percentile(mag, 95.0),
            SepCount = sep.Count,
            SepMedian = parts == null ? double.NaN : Percentile(sep, 50.0),
            SepP95 = parts == null ? double.NaN : Percentile(sep, 95.0),
            AlignCount = align.Count,
            AlignMedian = parts == null ? double.NaN : Percentile(align, 50.0),
            AlignP95 = parts == null ? double.NaN : Percentile(align, 95.0),
            CohCount = coh.Count,
            CohMedian = parts == null ? double.NaN : Percentile(coh, 50.0),
            CohP95 = parts == null ? double.NaN : Percentile(coh, 95.0),
        };
    }

    private static void AddAngle(List<double> sink, double ax, double ay, double bx, double by)
    {
        double a2 = ax * ax + ay * ay;
        double b2 = bx * bx + by * by;
        if (a2 < AngleLengthFloor || b2 < AngleLengthFloor)
        {
            return;
        }

        double cross = ax * by - ay * bx;
        double dot = ax * bx + ay * by;
        double degrees = Math.Atan2(Math.Abs(cross), dot) * (180.0 / Math.PI);
        sink.Add(degrees);
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx;
        double dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static int[][] GroupMembers(
        FieldForceReference.Sample[] reference, int[] team, double[] p90)
    {
        var all = new List<int>();
        var inside = new List<int>();
        var edge = new List<int>();
        for (int i = 0; i < reference.Length; i++)
        {
            int id = team[i];
            if (id != 0 && id != 1)
            {
                continue;
            }

            int neighbors = reference[i].NgRef;
            if (neighbors >= 1)
            {
                all.Add(i);
            }

            if (neighbors >= 0.75 * p90[id])
            {
                inside.Add(i);
            }

            if (neighbors >= 1 && neighbors <= 0.5 * p90[id])
            {
                edge.Add(i);
            }
        }

        return new[] { all.ToArray(), inside.ToArray(), edge.ToArray() };
    }

    private static double[] PercentileByTeam(
        FieldForceReference.Sample[] reference, int[] team, double percentile)
    {
        var own = new List<double>[2];
        own[0] = new List<double>();
        own[1] = new List<double>();
        for (int i = 0; i < reference.Length; i++)
        {
            if (team[i] == 0 || team[i] == 1)
            {
                own[team[i]].Add(reference[i].NgRef);
            }
        }

        return new[] { Percentile(own[0], percentile), Percentile(own[1], percentile) };
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values == null || values.Count == 0)
        {
            return double.NaN;
        }

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

    private static string ToCsv(List<Row> rows)
    {
        var text = new StringBuilder();
        text.AppendLine(
            "t,computer,group,n,angle_n,angle_median_deg,angle_p95_deg,mag_n,mag_median,mag_p95," +
            "sep_n,sep_median,sep_p95,align_n,align_median_deg,align_p95_deg,coh_n,coh_median_deg,coh_p95_deg");
        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            text.Append(row.Time.ToString("0.###", CultureInfo.InvariantCulture));
            text.Append(',').Append(row.Computer);
            text.Append(',').Append(row.Group);
            text.Append(',').Append(row.Count.ToString(CultureInfo.InvariantCulture));
            Append(text, row.AngleCount, row.AngleMedian, row.AngleP95);
            Append(text, row.MagCount, row.MagMedian, row.MagP95);
            Append(text, row.SepCount, row.SepMedian, row.SepP95);
            Append(text, row.AlignCount, row.AlignMedian, row.AlignP95);
            Append(text, row.CohCount, row.CohMedian, row.CohP95);
            text.AppendLine();
        }

        return text.ToString();
    }

    private static void Append(StringBuilder text, int count, double median, double p95)
    {
        text.Append(',').Append(count.ToString(CultureInfo.InvariantCulture));
        text.Append(',').Append(Num(median));
        text.Append(',').Append(Num(p95));
    }

    private static string Num(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "";
        }

        return value.ToString("G6", CultureInfo.InvariantCulture);
    }

    private static string ToMarkdown(List<Row> rows, List<string> notes)
    {
        var text = new StringBuilder();
        text.AppendLine("# Сила поля против обхода");
        text.AppendLine();
        text.AppendLine(
            "Снимки: `Boids_hash_2teams_field`, экземпляр с `RefreshInterval = 1`, 60 кадров/с, seed 1. " +
            "Ассет остаётся с `RefreshInterval = 3`. " +
            "Ошибка — весь путь Scatter против `FullTraversal`, без вычитания квантования. " +
            "Модуль не считается при `|F_ref| < 1e-3`. " +
            "Угол не считается, если длина одного из векторов меньше 1e-8. " +
            "Процентиль линейный по рангу `(n−1)`. " +
            "Компоненты есть только у поля: они сняты с `Field`, которое цепочка записала. " +
            "Кап 48 и 192 пишет в `velocity` только сумму.");
        text.AppendLine();
        for (int i = 0; i < notes.Count; i++)
        {
            text.AppendLine("- " + notes[i]);
        }

        text.AppendLine();
        text.AppendLine("| t, с | вычислитель | группа | n | угол медиана | угол p95 | модуль медиана | модуль p95 |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            text.Append("| ").Append(row.Time.ToString("0.#", CultureInfo.InvariantCulture));
            text.Append(" | ").Append(row.Computer);
            text.Append(" | ").Append(row.Group);
            text.Append(" | ").Append(row.Count.ToString(CultureInfo.InvariantCulture));
            text.Append(" | ").Append(Comma(row.AngleMedian));
            text.Append(" | ").Append(Comma(row.AngleP95));
            text.Append(" | ").Append(Comma(row.MagMedian));
            text.Append(" | ").Append(Comma(row.MagP95));
            text.AppendLine(" |");
        }

        text.AppendLine();
        text.AppendLine("Компоненты поля. `F_coh` только при `|mean displacement_ref| ≥ 0,1`.");
        text.AppendLine();
        text.AppendLine("| t, с | группа | sep медиана | sep p95 | align медиана | align p95 | coh медиана | coh p95 |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            if (row.Computer != "field")
            {
                continue;
            }

            text.Append("| ").Append(row.Time.ToString("0.#", CultureInfo.InvariantCulture));
            text.Append(" | ").Append(row.Group);
            text.Append(" | ").Append(Comma(row.SepMedian));
            text.Append(" | ").Append(Comma(row.SepP95));
            text.Append(" | ").Append(Comma(row.AlignMedian));
            text.Append(" | ").Append(Comma(row.AlignP95));
            text.Append(" | ").Append(Comma(row.CohMedian));
            text.Append(" | ").Append(Comma(row.CohP95));
            text.AppendLine(" |");
        }

        text.AppendLine();
        text.AppendLine("Числа критерия ADR-062 §2.6. Вердикта нет.");
        text.AppendLine();
        text.AppendLine("| t, с | группа | величина | поле | кап 48 | поле / кап 48 | потолок отношения |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        AppendCriterion(text, rows);
        return text.ToString();
    }

    private static void AppendCriterion(StringBuilder text, List<Row> rows)
    {
        string[] groups = { "all", "inside", "edge" };
        for (int s = 0; s < 3; s++)
        {
            float time = s == 0 ? 0f : (s == 1 ? 5f : 15f);
            for (int g = 0; g < groups.Length; g++)
            {
                Row field = Find(rows, time, "field", groups[g]);
                Row cap = Find(rows, time, "cap48", groups[g]);
                double ceiling = groups[g] == "all" ? 0.5 : 1.0;
                AppendRatio(text, time, groups[g], "угол медиана", field.AngleMedian, cap.AngleMedian, ceiling);
                AppendRatio(text, time, groups[g], "угол p95", field.AngleP95, cap.AngleP95, ceiling);
                AppendRatio(text, time, groups[g], "модуль медиана", field.MagMedian, cap.MagMedian, ceiling);
                AppendRatio(text, time, groups[g], "модуль p95", field.MagP95, cap.MagP95, ceiling);
            }
        }

        text.AppendLine();
        text.AppendLine("Потолки группы «все»: медиана угла 25°, медиана относительного модуля 0,25.");
        text.AppendLine();
        text.AppendLine("| t, с | медиана угла | медиана модуля |");
        text.AppendLine("| --- | --- | --- |");
        for (int s = 0; s < 3; s++)
        {
            float time = s == 0 ? 0f : (s == 1 ? 5f : 15f);
            Row field = Find(rows, time, "field", "all");
            text.Append("| ").Append(time.ToString("0.#", CultureInfo.InvariantCulture));
            text.Append(" | ").Append(Comma(field.AngleMedian));
            text.Append(" | ").Append(Comma(field.MagMedian));
            text.AppendLine(" |");
        }
    }

    private static void AppendRatio(
        StringBuilder text, float time, string group, string name, double field, double cap, double ceiling)
    {
        text.Append("| ").Append(time.ToString("0.#", CultureInfo.InvariantCulture));
        text.Append(" | ").Append(group);
        text.Append(" | ").Append(name);
        text.Append(" | ").Append(Comma(field));
        text.Append(" | ").Append(Comma(cap));
        text.Append(" | ").Append(Comma(cap == 0.0 || double.IsNaN(cap) ? double.NaN : field / cap));
        text.Append(" | ").Append(ceiling.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ','));
        text.AppendLine(" |");
    }

    private static Row Find(List<Row> rows, float time, string computer, string group)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Computer == computer && rows[i].Group == group && Math.Abs(rows[i].Time - time) < 0.01f)
            {
                return rows[i];
            }
        }

        throw new InvalidOperationException(computer + " " + group + " t=" + time);
    }

    private static string Comma(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "";
        }

        return value.ToString("G6", CultureInfo.InvariantCulture).Replace('.', ',');
    }

    private static double[] Pack(Vector4[] slots)
    {
        var field = new double[slots.Length * 4];
        for (int i = 0; i < slots.Length; i++)
        {
            field[i * 4] = slots[i].x;
            field[i * 4 + 1] = slots[i].y;
            field[i * 4 + 2] = slots[i].z;
            field[i * 4 + 3] = slots[i].w;
        }

        return field;
    }

    private static Vector3[] Weights(EffectAsset asset)
    {
        var weights = new Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            TeamProfile team = asset.Teams[i];
            weights[i] = new Vector3(team.SeparationWeight, team.AlignmentWeight, team.CohesionWeight);
        }

        return weights;
    }

    private static double[] Inter(EffectAsset asset)
    {
        return new double[]
        {
            asset.Teams[0].InterGroupSeparationMultiplier,
            asset.Teams[1].InterGroupSeparationMultiplier,
        };
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

    private static BuildSpatialHashPass FindHash(IReadOnlyList<SimPass> passes)
    {
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i] is BuildSpatialHashPass hash)
            {
                return hash;
            }
        }

        Assert.Fail("hash pass missing");
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
            HashShaderPath,
            "Assets/Shaders/GPU/Passes/HashDebugPasses.compute",
            DynamicsShaderPath,
            BoidShaderPath,
            FieldShaderPath,
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

    private static void AssertFinite(double value, string label)
    {
        Assert.IsFalse(double.IsNaN(value) || double.IsInfinity(value), label + "=" + value);
    }

    private static void AssertFinite(float value, string label)
    {
        Assert.IsFalse(float.IsNaN(value) || float.IsInfinity(value), label);
    }

    private struct Shot
    {
        public float Time;
        public Vector2[] Position;
        public Vector2[] Heading;
        public int[] Team;
    }

    private struct Row
    {
        public float Time;
        public string Computer;
        public string Group;
        public int Count;
        public int AngleCount;
        public double AngleMedian;
        public double AngleP95;
        public int MagCount;
        public double MagMedian;
        public double MagP95;
        public int SepCount;
        public double SepMedian;
        public double SepP95;
        public int AlignCount;
        public double AlignMedian;
        public double AlignP95;
        public int CohCount;
        public double CohMedian;
        public double CohP95;
        public string Key => Time.ToString("0.#", CultureInfo.InvariantCulture) + " " + Computer + " " + Group;
    }

    private sealed class FieldEval : IDisposable
    {
        private readonly ParticleSet particles;
        private readonly FieldSet fields;
        private readonly GraphicsBuffer teams;
        private readonly SimContext context;
        private readonly FieldNeighborCollectPass collect;
        private readonly FieldNeighborConvolvePass convolve;
        private readonly ClearVelocityPass clear;
        private readonly FieldNeighborForcePass force;

        public FieldEval(Shot shot, Vector3 center, Vector3 extents, float texel, EffectAsset asset)
        {
            int count = shot.Position.Length;
            particles = new ParticleSet();
            fields = new FieldSet();
            particles.EnsureCapacity(count);
            particles.RegisterAttribute(BuiltinAttributes.Position);
            particles.RegisterAttribute(BuiltinAttributes.Heading);
            particles.RegisterAttribute(BuiltinAttributes.TeamId);
            particles.RegisterAttribute(BuiltinAttributes.Velocity);
            teams = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            TeamParams.Upload(teams, asset.Teams);
            var shaders = new[]
            {
                AssetDatabase.LoadAssetAtPath<ComputeShader>(FieldShaderPath),
                AssetDatabase.LoadAssetAtPath<ComputeShader>(DynamicsShaderPath),
            };
            context = new SimContext(particles, fields, shaders, null);
            context.Cmd = new CommandBuffer { name = "FieldSnapshotForce" };
            context.Teams = teams;
            collect = new FieldNeighborCollectPass
            {
                Mode = FieldCollectMode.Scatter,
                Center = center,
                Extents = extents,
                TexelSize = texel,
                RefreshInterval = 1,
            };
            convolve = new FieldNeighborConvolvePass();
            clear = new ClearVelocityPass();
            force = new FieldNeighborForcePass
            {
                Center = center,
                Extents = extents,
                TexelSize = texel,
            };
            Write(shot);
            collect.Initialize(context);
            convolve.Initialize(context);
            clear.Initialize(context);
            force.Initialize(context);
        }

        public Vector2[] Force(Shot shot)
        {
            Write(shot);
            context.Cmd.Clear();
            collect.Execute(context, 1f / 60f);
            convolve.Execute(context, 1f / 60f);
            clear.Execute(context, 1f / 60f);
            force.Execute(context, 1f / 60f);
            Graphics.ExecuteCommandBuffer(context.Cmd);
            context.Cmd.Clear();
            var velocity = new Vector3[shot.Position.Length];
            particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
            var xz = new Vector2[velocity.Length];
            for (int i = 0; i < velocity.Length; i++)
            {
                xz[i] = new Vector2(velocity[i].x, velocity[i].z);
            }

            return xz;
        }

        public Vector4[] ReadField()
        {
            var slots = new Vector4[context.FieldNeighbor.Layout.CellCount * 4];
            context.FieldNeighbor.Field.GetData(slots);
            return slots;
        }

        public void Dispose()
        {
            force.Dispose();
            convolve.Dispose();
            collect.Dispose();
            context.Cmd.Release();
            teams.Dispose();
            fields.Dispose();
            particles.Dispose();
        }

        private void Write(Shot shot)
        {
            int count = shot.Position.Length;
            var position = new Vector3[count];
            var heading = new Vector3[count];
            var team = new uint[count];
            var zeros = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                position[i] = new Vector3(shot.Position[i].x, 0f, shot.Position[i].y);
                heading[i] = new Vector3(shot.Heading[i].x, 0f, shot.Heading[i].y);
                team[i] = (uint)shot.Team[i];
            }

            particles.Get(BuiltinAttributes.Position).SetData(position);
            particles.Get(BuiltinAttributes.Heading).SetData(heading);
            particles.Get(BuiltinAttributes.TeamId).SetData(team);
            particles.Get(BuiltinAttributes.Velocity).SetData(zeros);
        }
    }

    private sealed class WalkEval : IDisposable
    {
        private readonly ParticleSet particles;
        private readonly FieldSet fields;
        private readonly GraphicsBuffer teams;
        private readonly SimContext context;
        private readonly BuildSpatialHashPass hash;
        private readonly ClearVelocityPass clear;
        private readonly BoidNeighborForcePass force;

        public WalkEval(Shot shot, BuildSpatialHashPass sourceHash, int cap, int walkMode, EffectAsset asset)
        {
            int count = shot.Position.Length;
            particles = new ParticleSet();
            fields = new FieldSet();
            particles.EnsureCapacity(count);
            particles.RegisterAttribute(BuiltinAttributes.Position);
            particles.RegisterAttribute(BuiltinAttributes.Heading);
            particles.RegisterAttribute(BuiltinAttributes.TeamId);
            particles.RegisterAttribute(BuiltinAttributes.Velocity);
            teams = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            TeamParams.Upload(teams, asset.Teams);
            var shaders = new[]
            {
                AssetDatabase.LoadAssetAtPath<ComputeShader>(HashShaderPath),
                AssetDatabase.LoadAssetAtPath<ComputeShader>(DynamicsShaderPath),
                AssetDatabase.LoadAssetAtPath<ComputeShader>(BoidShaderPath),
            };
            context = new SimContext(particles, fields, shaders, null);
            context.Cmd = new CommandBuffer { name = "WalkSnapshotForce" };
            context.Teams = teams;
            hash = new BuildSpatialHashPass
            {
                Center = sourceHash.Center,
                Extents = sourceHash.Extents,
                MinCellSize = sourceHash.MinCellSize,
                Wrap = sourceHash.Wrap,
            };
            clear = new ClearVelocityPass();
            force = new BoidNeighborForcePass
            {
                MaxNeighbors = cap,
                WalkMode = walkMode,
                CountCapHits = false,
            };
            Write(shot);
            hash.Initialize(context);
            clear.Initialize(context);
            force.Initialize(context);
        }

        public Vector2[] Force(Shot shot)
        {
            Write(shot);
            context.Cmd.Clear();
            hash.Execute(context, 1f / 60f);
            clear.Execute(context, 1f / 60f);
            force.Execute(context, 1f / 60f);
            Graphics.ExecuteCommandBuffer(context.Cmd);
            context.Cmd.Clear();
            var velocity = new Vector3[shot.Position.Length];
            particles.Get(BuiltinAttributes.Velocity).GetData(velocity);
            var xz = new Vector2[velocity.Length];
            for (int i = 0; i < velocity.Length; i++)
            {
                xz[i] = new Vector2(velocity[i].x, velocity[i].z);
            }

            return xz;
        }

        public void Dispose()
        {
            force.Dispose();
            hash.Dispose();
            context.Cmd.Release();
            teams.Dispose();
            fields.Dispose();
            particles.Dispose();
        }

        private void Write(Shot shot)
        {
            int count = shot.Position.Length;
            var position = new Vector3[count];
            var heading = new Vector3[count];
            var team = new uint[count];
            var zeros = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                position[i] = new Vector3(shot.Position[i].x, 0f, shot.Position[i].y);
                heading[i] = new Vector3(shot.Heading[i].x, 0f, shot.Heading[i].y);
                team[i] = (uint)shot.Team[i];
            }

            particles.Get(BuiltinAttributes.Position).SetData(position);
            particles.Get(BuiltinAttributes.Heading).SetData(heading);
            particles.Get(BuiltinAttributes.TeamId).SetData(team);
            particles.Get(BuiltinAttributes.Velocity).SetData(zeros);
        }
    }
}
