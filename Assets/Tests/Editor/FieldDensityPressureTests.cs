using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class FieldDensityPressureTests
{
    private const string ShaderPath = "Assets/Shaders/GPU/Passes/FieldDensityPressure.compute";
    private const double Scale = 16384.0;

    [Test]
    public void SumN_PointTwoFive_IsExactOnWorlds90And64()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        CheckSum(45f, 80, false);
        CheckSum(32f, 64, false);
        CheckSum(45f, 40, true);
    }

    [Test]
    public void Gradient_MatchesDoubleOfTheGpuDensity()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector2[] positions = Crowd(16, 2);
        using (Rig rig = new Rig(positions, Teams(positions.Length, 0), 45f, jitter: false))
        {
            rig.Step();
            uint[] density = rig.ReadDensity();
            Vector2[] gpu = rig.ReadGrad();
            double max = 0.0;
            int worst = 0;
            for (int cell = 0; cell < rig.Cells; cell++)
            {
                for (int team = 0; team < 2; team++)
                {
                    Vector2 expected = ReferenceGrad(density, cell, team, rig);
                    Vector2 actual = gpu[cell * 2 + team];
                    max = Worse(max, actual.x, expected.x, ref worst, cell);
                    max = Worse(max, actual.y, expected.y, ref worst, cell);
                    AssertComponent(actual.x, expected.x, "grad x cell " + cell);
                    AssertComponent(actual.y, expected.y, "grad y cell " + cell);
                }
            }

            Assert.Pass("maxAbs=" + max.ToString("G6") + " worstCell=" + worst);
        }
    }

    [Test]
    public void Force_MatchesGpuCicWeights()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector2[] positions = Crowd(24, 3);
        double max = RunForce(positions, Teams(positions.Length, 1), "force");
        Assert.Pass("maxAbs=" + max.ToString("G6"));
    }

    [Test]
    public void SeamAndCorners_MatchTheGpuWeights()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        const float edge = 45f - 1e-3f;
        var positions = new[]
        {
            new Vector2(edge, 0f),
            new Vector2(-edge, 0f),
            new Vector2(0f, edge),
            new Vector2(0f, -edge),
            new Vector2(edge, edge),
            new Vector2(-edge, -edge),
            new Vector2(-edge, edge),
            new Vector2(edge, -edge),
            new Vector2(0f, 0f),
        };
        double max = RunForce(positions, Teams(positions.Length, 4), "seam");
        Assert.Pass("maxAbs=" + max.ToString("G6"));
    }

    [Test]
    public void LoneParticle_JitterOff_TwentyPositions()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        const float edge = 45f - 1e-3f;
        var positions = new List<Vector2>
        {
            new Vector2(edge, 0f),
            new Vector2(-edge, 0f),
            new Vector2(0f, edge),
            new Vector2(0f, -edge),
            new Vector2(edge, edge),
            new Vector2(-edge, edge),
            new Vector2(edge, -edge),
            new Vector2(-edge, -edge),
        };
        var random = new System.Random(1);
        while (positions.Count < 20)
        {
            float x = (float)(-44.0 + random.NextDouble() * 88.0);
            float z = (float)(-44.0 + random.NextDouble() * 88.0);
            positions.Add(new Vector2(x, z));
        }

        double max = 0.0;
        using (Rig rig = new Rig(new[] { positions[0] }, new[] { 0 }, 45f, jitter: false))
        {
            for (int i = 0; i < positions.Count; i++)
            {
                rig.SetPosition(positions[i]);
                rig.ZeroVelocity();
                rig.Step();
                Vector3 velocity = rig.ReadVelocity()[0];
                max = Math.Max(max, Math.Abs(velocity.x));
                max = Math.Max(max, Math.Abs(velocity.z));
                Assert.LessOrEqual(new Vector2(velocity.x, velocity.z).magnitude, 1e-5f, "position " + i);
                Assert.AreEqual(0f, velocity.y, 0f);
            }
        }

        Assert.Pass("maxAbs=" + max.ToString("G6") + " positions=20");
    }

    [Test]
    public void LoneParticle_JitterOn_TwentyShifts()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        double max = 0.0;
        var seen = new List<Vector2>();
        using (Rig rig = new Rig(new[] { Vector2.zero }, new[] { 0 }, 45f, jitter: true))
        {
            for (int i = 0; i < 20; i++)
            {
                rig.ZeroVelocity();
                rig.Step();
                float cell = rig.Pass.Layout.CellSize.x;
                Vector2 shift = rig.Pass.LastShift;
                Assert.GreaterOrEqual(shift.x, 0f);
                Assert.GreaterOrEqual(shift.y, 0f);
                Assert.Less(shift.x, cell);
                Assert.Less(shift.y, cell);
                seen.Add(shift);
                Vector3 velocity = rig.ReadVelocity()[0];
                max = Math.Max(max, Math.Abs(velocity.x));
                max = Math.Max(max, Math.Abs(velocity.z));
                Assert.LessOrEqual(new Vector2(velocity.x, velocity.z).magnitude, 1e-5f, "frame " + i);
            }

            Assert.AreEqual(20, rig.Pass.FrameIndex);
        }

        for (int i = 0; i < seen.Count; i++)
        {
            for (int j = i + 1; j < seen.Count; j++)
            {
                Assert.Greater((seen[i] - seen[j]).sqrMagnitude, 0f);
            }
        }

        Assert.Pass("maxAbs=" + max.ToString("G6") + " shifts=20");
    }

    [Test]
    public void Pair_SameTeam_Repels_OtherTeam_NotWeaker()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        var positions = new[] { new Vector2(-0.15f, 0f), new Vector2(0.15f, 0f) };
        Vector3[] same;
        Vector3[] other;
        using (Rig rig = new Rig(positions, new[] { 0, 0 }, 45f, jitter: false))
        {
            rig.Step();
            same = rig.ReadVelocity();
        }

        using (Rig rig = new Rig(positions, new[] { 0, 1 }, 45f, jitter: false))
        {
            rig.Step();
            other = rig.ReadVelocity();
        }

        float sameMag = new Vector2(same[0].x, same[0].z).magnitude;
        float otherMag = new Vector2(other[0].x, other[0].z).magnitude;
        Assert.AreEqual(sameMag, new Vector2(same[1].x, same[1].z).magnitude, 1e-4f);
        Assert.Less(same[0].x, 0f);
        Assert.Greater(same[1].x, 0f);
        Assert.GreaterOrEqual(otherMag + 1e-4f, sameMag);
        Assert.Less(other[0].x, 0f);
        Assert.Greater(other[1].x, 0f);
        Assert.Pass("same=" + sameMag.ToString("G6") + " other=" + otherMag.ToString("G6"));
    }

    [Test]
    public void Jitter_SameFrameMatches_OffConsumesTheIndex_RepeatDoesNotAllocateOnCollect()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        using (Rig rig = new Rig(new[] { new Vector2(1f, 2f) }, new[] { 0 }, 45f, jitter: true))
        {
            rig.Pass.FieldStage = FieldRepeatStage.Off;
            rig.Step();
            Vector2 first = rig.Pass.LastShift;
            Assert.AreEqual(FieldDensityPressurePass.ShiftOf(0, 1, rig.Pass.Layout.CellSize, true), first);
            rig.Pass.FieldStage = FieldRepeatStage.All;
            rig.Step();
            Vector2 second = rig.Pass.LastShift;
            Assert.AreEqual(FieldDensityPressurePass.ShiftOf(1, 1, rig.Pass.Layout.CellSize, true), second);
            Assert.Greater((first - second).sqrMagnitude, 0f);
            rig.Pass.Jitter = false;
            rig.Step();
            Assert.AreEqual(Vector2.zero, rig.Pass.LastShift);

            rig.Pass.Jitter = true;
            rig.Pass.FieldStage = FieldRepeatStage.Collect;
            rig.Pass.FieldRepeatCount = 4;
            rig.ZeroVelocity();
            rig.Step();
            Assert.IsNull(rig.Pass.ScratchVelocity);
        }
    }

    private static void CheckSum(float extent, int count, bool jitter)
    {
        Vector2[] positions = Crowd(count, extent > 40f ? 7 : 11);
        int[] teams = Teams(count, 3);
        using (Rig rig = new Rig(positions, teams, extent, jitter))
        {
            rig.Step();
            if (extent > 40f)
            {
                Assert.AreEqual(new Vector2Int(360, 360), rig.Pass.Layout.Resolution);
            }
            else
            {
                Assert.AreEqual(new Vector2Int(256, 256), rig.Pass.Layout.Resolution);
            }
            uint[] density = rig.ReadDensity();
            int team0 = 0;
            int team1 = 0;
            for (int i = 0; i < teams.Length; i++)
            {
                if (teams[i] == 0)
                {
                    team0++;
                }
                else
                {
                    team1++;
                }
            }

            Assert.AreEqual(team0 * 16384L, SumTeam(density, 0));
            Assert.AreEqual(team1 * 16384L, SumTeam(density, 1));
        }
    }

    private static double RunForce(Vector2[] positions, int[] teams, string label)
    {
        using (Rig rig = new Rig(positions, teams, 45f, jitter: false))
        {
            rig.Step();
            Assert.AreEqual(360, rig.Pass.Layout.Resolution.x);
            Assert.AreNotEqual(0, rig.Pass.Layout.Resolution.x & (rig.Pass.Layout.Resolution.x - 1));
            Vector2[] cic = rig.ReadCic();
            Vector2[] grad = rig.ReadGrad();
            Vector3[] velocity = rig.ReadVelocity();
            double max = 0.0;
            for (int i = 0; i < positions.Length; i++)
            {
                if (teams[i] > 1)
                {
                    continue;
                }

                Vector2 expected = ReferenceForce(cic, grad, i, teams[i], rig, 0.8, 40.0);
                max = Math.Max(max, Math.Abs(velocity[i].x - expected.x));
                max = Math.Max(max, Math.Abs(velocity[i].z - expected.y));
                Assert.AreEqual(expected.x, velocity[i].x, 1e-4f, label + " x " + i);
                Assert.AreEqual(expected.y, velocity[i].z, 1e-4f, label + " z " + i);
                Assert.AreEqual(0f, velocity[i].y, 0f);
            }

            return max;
        }
    }

    private static Vector2[] Crowd(int count, int seed)
    {
        var random = new System.Random(seed);
        var positions = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector2(
                (float)(-40.0 + random.NextDouble() * 80.0),
                (float)(-40.0 + random.NextDouble() * 80.0));
        }

        return positions;
    }

    private static int[] Teams(int count, int salt)
    {
        var teams = new int[count];
        for (int i = 0; i < count; i++)
        {
            teams[i] = (i + salt) % 2;
        }

        return teams;
    }

    private static long SumTeam(uint[] density, int team)
    {
        long sum = 0;
        for (int cell = 0; cell < density.Length / 2; cell++)
        {
            sum += density[cell * 2 + team];
        }

        return sum;
    }

    private static void AssertComponent(float actual, float expected, string label)
    {
        double tol = Math.Max(1e-3, 1e-5 * Math.Abs(expected));
        Assert.AreEqual(expected, actual, (float)tol, label);
    }

    private static double Worse(double max, float actual, float expected, ref int worst, int cell)
    {
        double error = Math.Abs(actual - expected);
        if (error > max)
        {
            worst = cell;
            return error;
        }

        return max;
    }

    private static Vector2 ReferenceGrad(uint[] density, int cell, int team, Rig rig)
    {
        int rx = rig.Pass.Layout.Resolution.x;
        int ry = rig.Pass.Layout.Resolution.y;
        int x = cell % rx;
        int y = cell / rx;
        double hx = rig.Pass.Layout.CellSize.x;
        double hy = rig.Pass.Layout.CellSize.y;
        double m = 4.0;
        double gx = (Weighted(density, x + 1, y, rx, ry, team, hx, hy, m)
            - Weighted(density, x - 1, y, rx, ry, team, hx, hy, m)) / (2.0 * hx);
        double gz = (Weighted(density, x, y + 1, rx, ry, team, hx, hy, m)
            - Weighted(density, x, y - 1, rx, ry, team, hx, hy, m)) / (2.0 * hy);
        return new Vector2((float)gx, (float)gz);
    }

    private static double Weighted(
        uint[] density, int x, int y, int rx, int ry, int team, double hx, double hy, double m)
    {
        int cell = Wrap(y, ry) * rx + Wrap(x, rx);
        double area = hx * hy;
        double mine = density[cell * 2 + team] / Scale / area;
        double foreign = density[cell * 2 + (1 - team)] / Scale / area;
        return mine + m * foreign;
    }

    private static int Wrap(int value, int res)
    {
        return ((value % res) + res) % res;
    }

    private static Vector2 ReferenceForce(
        Vector2[] cic, Vector2[] grad, int particle, int team, Rig rig, double weight, double floor)
    {
        double hx = rig.Pass.Layout.CellSize.x;
        double hy = rig.Pass.Layout.CellSize.y;
        double area = hx * hy;
        double gx = 0.0;
        double gz = 0.0;
        var q = new double[4];
        for (int k = 0; k < 4; k++)
        {
            q[k] = cic[particle * 4 + k].y;
            int cell = (int)cic[particle * 4 + k].x;
            Vector2 sample = grad[cell * 2 + team];
            gx += q[k] / Scale * sample.x;
            gz += q[k] / Scale * sample.y;
        }

        double inv = 1.0 / (Scale * area);
        double r00 = q[0] * inv;
        double r10 = q[1] * inv;
        double r01 = q[2] * inv;
        double r11 = q[3] * inv;
        double w00 = q[0] / Scale;
        double w10 = q[1] / Scale;
        double w01 = q[2] / Scale;
        double w11 = q[3] / Scale;
        gx -= w00 * (r10 / (2.0 * hx)) + w10 * (-r00 / (2.0 * hx)) + w01 * (r11 / (2.0 * hx)) + w11 * (-r01 / (2.0 * hx));
        gz -= w00 * (r01 / (2.0 * hy)) + w10 * (r11 / (2.0 * hy)) + w01 * (-r00 / (2.0 * hy)) + w11 * (-r10 / (2.0 * hy));
        double mag = Math.Sqrt(gx * gx + gz * gz);
        return new Vector2((float)(-weight * gx / (mag + floor)), (float)(-weight * gz / (mag + floor)));
    }

    private sealed class Rig : IDisposable
    {
        private readonly ParticleSet particles;
        private readonly FieldSet fields;
        private readonly CommandBuffer cmd;
        private readonly GraphicsBuffer teams;
        private readonly SimContext context;
        private GraphicsBuffer cic;

        public Rig(Vector2[] positions, int[] teamIds, float extent, bool jitter)
        {
            Pass = new FieldDensityPressurePass
            {
                Center = Vector3.zero,
                Extents = new Vector3(extent, 0f, extent),
                PressureTexel = 0.25f,
                Weight = 0.8f,
                G0 = 40f,
                Jitter = jitter,
                JitterSeed = 1,
            };
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            Assert.IsNotNull(shader);
            particles = new ParticleSet();
            fields = new FieldSet();
            particles.EnsureCapacity(positions.Length);
            particles.RegisterAttribute(BuiltinAttributes.Position);
            particles.RegisterAttribute(BuiltinAttributes.Heading);
            particles.RegisterAttribute(BuiltinAttributes.TeamId);
            particles.RegisterAttribute(BuiltinAttributes.Velocity);
            Write(positions, teamIds);
            ZeroVelocity();
            teams = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TeamProfile.MaxTeams, TeamParams.Stride);
            var slots = new TeamParams[TeamProfile.MaxTeams];
            slots[0].Radii = new Vector4(3f, 3f, 3f, 4f);
            slots[1].Radii = new Vector4(3f, 3f, 3f, 4f);
            teams.SetData(slots);
            context = new SimContext(particles, fields, new[] { shader }, null);
            cmd = new CommandBuffer { name = "DensityPressure" };
            context.Cmd = cmd;
            context.Teams = teams;
            Pass.Initialize(context);
        }

        public FieldDensityPressurePass Pass { get; }

        public int Cells => Pass.Layout.CellCount;

        public void SetPosition(Vector2 position)
        {
            particles.Get(BuiltinAttributes.Position).SetData(new[] { new Vector3(position.x, 0f, position.y) });
        }

        public void ZeroVelocity()
        {
            particles.Get(BuiltinAttributes.Velocity).SetData(new Vector3[particles.Count]);
        }

        public void Step()
        {
            cmd.Clear();
            Pass.Execute(context, 1f / 60f);
            Graphics.ExecuteCommandBuffer(cmd);
        }

        public uint[] ReadDensity()
        {
            var bits = new uint[Pass.Density.count];
            Pass.Density.GetData(bits);
            return bits;
        }

        public Vector2[] ReadGrad()
        {
            var values = new Vector2[Pass.Grad.count];
            Pass.Grad.GetData(values);
            return values;
        }

        public Vector3[] ReadVelocity()
        {
            var values = new Vector3[particles.Count];
            particles.Get(BuiltinAttributes.Velocity).GetData(values);
            return values;
        }

        public Vector2[] ReadCic()
        {
            cic?.Release();
            cic = new GraphicsBuffer(GraphicsBuffer.Target.Structured, particles.Count * 4, sizeof(float) * 2);
            int kernel = Pass.PressureShader.FindKernel("PressureCicWeights");
            cmd.Clear();
            cmd.SetComputeBufferParam(Pass.PressureShader, kernel, "CicOut", cic);
            cmd.SetComputeBufferParam(Pass.PressureShader, kernel, "position", particles.Get(BuiltinAttributes.Position));
            int groups = (particles.Count + 63) / 64;
            cmd.DispatchCompute(Pass.PressureShader, kernel, groups, 1, 1);
            Graphics.ExecuteCommandBuffer(cmd);
            var values = new Vector2[particles.Count * 4];
            cic.GetData(values);
            return values;
        }

        public void Dispose()
        {
            cic?.Release();
            Pass.Dispose();
            teams.Dispose();
            cmd.Release();
            fields.Dispose();
            particles.Dispose();
        }

        private void Write(Vector2[] positions, int[] teamIds)
        {
            int count = positions.Length;
            var xyz = new Vector3[count];
            var heading = new Vector3[count];
            var ids = new uint[count];
            for (int i = 0; i < count; i++)
            {
                xyz[i] = new Vector3(positions[i].x, 0f, positions[i].y);
                heading[i] = new Vector3(1f, 0f, 0f);
                ids[i] = (uint)teamIds[i];
            }

            particles.Get(BuiltinAttributes.Position).SetData(xyz);
            particles.Get(BuiltinAttributes.Heading).SetData(heading);
            particles.Get(BuiltinAttributes.TeamId).SetData(ids);
        }
    }
}
