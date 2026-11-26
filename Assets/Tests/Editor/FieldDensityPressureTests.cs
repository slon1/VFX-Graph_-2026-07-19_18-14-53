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
        Vector2[] positions = DenseSquare(64, 2f, new System.Random(3));
        ForceStats stats = RunForce(positions, Teams(positions.Length, 1), "force");
        Assert.Greater(stats.FracAboveOneMilli, 0.0, "at least one |F| > 1e-3");
        Assert.Pass(
            "maxAbs=" + stats.MaxAbs.ToString("G6")
            + " meanAbs=" + stats.MeanAbs.ToString("G6")
            + " fracAbove1e-3=" + stats.FracAboveOneMilli.ToString("G6"));
    }

    [Test]
    public void Force_DenseCrowd_MatchesGpuCicWeights()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector2[] positions = DenseSquare(5000, 10f, new System.Random(7));
        ForceStats stats = RunForce(positions, Teams(positions.Length, 0), "dense");
        Assert.GreaterOrEqual(stats.FracAboveOneMilli, 0.5, "fraction with |F| > 1e-3");
        Assert.Pass(
            "maxAbs=" + stats.MaxAbs.ToString("G6")
            + " meanAbs=" + stats.MeanAbs.ToString("G6")
            + " fracAbove1e-3=" + stats.FracAboveOneMilli.ToString("G6"));
    }

    [Test]
    public void Force_DenseCrowd_MagnitudeDoesNotExceedWeight()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector2[] positions = DenseSquare(5000, 10f, new System.Random(7));
        int[] teams = Teams(positions.Length, 0);
        float[] weights = { 0.8f, 2.5f, 10f };
        for (int w = 0; w < weights.Length; w++)
        {
            float weight = weights[w];
            using (Rig rig = new Rig(positions, teams, 45f, jitter: false))
            {
                rig.Pass.Weight = weight;
                rig.Pass.G0 = 12f;
                rig.ZeroVelocity();
                rig.Step();
                Vector3[] velocity = rig.ReadVelocity();
                float limit = weight + 1e-5f;
                for (int i = 0; i < velocity.Length; i++)
                {
                    float mag = new Vector2(velocity[i].x, velocity[i].z).magnitude;
                    Assert.LessOrEqual(mag, limit, "weight=" + weight + " i=" + i);
                }
            }
        }
    }

    [Test]
    public void SeamAndCorners_MatchTheGpuWeights()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        const float a = 0.15625f;
        Vector2[] lineInside = { new Vector2(-a, 0f), new Vector2(a, 0f) };
        Vector2[] lineSeam = { new Vector2(45f - a, 0f), new Vector2(-45f + a, 0f) };
        Vector2[] cornerInside = { new Vector2(-a, -a), new Vector2(a, a) };
        Vector2[] cornerSeam = { new Vector2(45f - a, 45f - a), new Vector2(-45f + a, -45f + a) };

        float lineSame = PairMagnitude(lineInside, sameTeam: true);
        float lineOther = PairMagnitude(lineInside, sameTeam: false);
        float cornerSame = PairMagnitude(cornerInside, sameTeam: true);
        float cornerOther = PairMagnitude(cornerInside, sameTeam: false);
        Assert.AreEqual(0.218182f, lineSame, 1e-4f, "line same |F|");
        Assert.AreEqual(0.480000f, lineOther, 1e-4f, "line other |F|");
        Assert.AreEqual(0.055521f, cornerSame, 1e-4f, "corner same |F|");
        Assert.AreEqual(0.183815f, cornerOther, 1e-4f, "corner other |F|");

        double maxDiff = 0.0;
        maxDiff = Math.Max(maxDiff, CompareSeamToInside(lineSeam, lineInside, sameTeam: true, "line same"));
        maxDiff = Math.Max(maxDiff, CompareSeamToInside(lineSeam, lineInside, sameTeam: false, "line other"));
        maxDiff = Math.Max(maxDiff, CompareSeamToInside(cornerSeam, cornerInside, sameTeam: true, "corner same"));
        maxDiff = Math.Max(maxDiff, CompareSeamToInside(cornerSeam, cornerInside, sameTeam: false, "corner other"));

        Assert.Pass(
            "maxAbsDiff=" + maxDiff.ToString("G6")
            + " lineSame=" + lineSame.ToString("G6")
            + " lineOther=" + lineOther.ToString("G6")
            + " cornerSame=" + cornerSame.ToString("G6")
            + " cornerOther=" + cornerOther.ToString("G6"));
    }

    [Test]
    public void SortedOrder_MatchesUnsorted_WhenTheHashRefreshesEveryThirdFrame()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        var positions = new[]
        {
            new Vector2(-1.2f, 0.2f),
            new Vector2(0.4f, 0.1f),
            new Vector2(1.1f, -0.3f),
            new Vector2(-0.2f, 1.4f),
        };
        int[] teams = { 0, 0, 1, 1 };
        Vector3[] plain;
        Vector3[] sorted;
        using (Rig rig = new Rig(positions, teams, 8f, jitter: false, texel: 1f, hashRefresh: 1))
        {
            for (int i = 0; i < 3; i++)
            {
                rig.Step();
            }

            plain = rig.ReadVelocity();
        }

        using (Rig rig = new Rig(positions, teams, 8f, jitter: false, texel: 1f, hashRefresh: 3))
        {
            rig.Pass.SortedOrder = true;
            for (int i = 0; i < 3; i++)
            {
                rig.Step();
            }

            sorted = rig.ReadVelocity();
        }

        for (int i = 0; i < plain.Length; i++)
        {
            Assert.AreEqual(plain[i].x, sorted[i].x, 1e-5f, "x " + i);
            Assert.AreEqual(plain[i].z, sorted[i].z, 1e-5f, "z " + i);
        }
    }

    [Test]
    public void Grid150_WorldCorners_StayInsideTheGrid()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        const float edge = 45f - 1e-3f;
        var positions = new[]
        {
            new Vector2(edge, edge),
            new Vector2(-edge, edge),
            new Vector2(edge, -edge),
            new Vector2(-edge, -edge),
            new Vector2(edge, 0f),
            new Vector2(-edge, 0f),
            new Vector2(0f, edge),
            new Vector2(0f, -edge),
        };
        using (Rig rig = new Rig(positions, Teams(positions.Length, 0), 45f, jitter: false, texel: 0.6f))
        {
            rig.Step();
            Assert.AreEqual(150, rig.Pass.Layout.Resolution.x);
            Assert.AreEqual(150, rig.Pass.Layout.Resolution.y);
            Vector2[] cic = rig.ReadCic();
            int cells = rig.Cells;
            long sum = 0;
            uint[] density = rig.ReadDensity();
            for (int i = 0; i < density.Length; i++)
            {
                sum += density[i];
            }

            Assert.AreEqual(positions.Length * 16384L, sum);
            for (int i = 0; i < cic.Length; i++)
            {
                int cell = (int)cic[i].x;
                Assert.GreaterOrEqual(cell, 0, "corner " + i);
                Assert.Less(cell, cells, "corner " + i);
            }
        }
    }

    [Test]
    public void IntervalsOne_MatchTheDefaultForce()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector2[] positions = DenseSquare(64, 2f, new System.Random(3));
        int[] teams = Teams(positions.Length, 1);
        using (Rig plain = new Rig(positions, teams, 45f, jitter: false))
        using (Rig ones = new Rig(positions, teams, 45f, jitter: false))
        {
            ones.Pass.GridUpdateInterval = 1;
            ones.Pass.ForceInterleave = 1;
            plain.Step();
            ones.Step();
            Vector3[] a = plain.ReadVelocity();
            Vector3[] b = ones.ReadVelocity();
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i].x, b[i].x, 0f, "x " + i);
                Assert.AreEqual(a[i].y, b[i].y, 0f, "y " + i);
                Assert.AreEqual(a[i].z, b[i].z, 0f, "z " + i);
            }
        }
    }

    [Test]
    public void GridUpdateIntervalTwo_BuildsEverySecondFrameFromTheSavedGradient()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector2[] positions = DenseSquare(32, 4f, new System.Random(5));
        using (Rig rig = new Rig(positions, Teams(positions.Length, 0), 45f, jitter: true))
        {
            rig.Pass.GridUpdateInterval = 2;
            Vector2[] builtGrad = null;
            Vector3[] builtVelocity = null;
            Vector2 builtShift = Vector2.zero;
            for (int frame = 0; frame < 4; frame++)
            {
                rig.ZeroVelocity();
                int before = rig.Pass.GridBuilds;
                rig.Step();
                if (frame % 2 == 0)
                {
                    Assert.AreEqual(before + 1, rig.Pass.GridBuilds, "build " + frame);
                    builtGrad = rig.ReadGrad();
                    builtVelocity = rig.ReadVelocity();
                    builtShift = rig.Pass.LastShift;
                    Vector2 expected = FieldDensityPressurePass.ShiftOf(frame, 1, rig.Pass.Layout.CellSize, true);
                    Assert.AreEqual(expected.x, builtShift.x, 0f, "shift x " + frame);
                    Assert.AreEqual(expected.y, builtShift.y, 0f, "shift y " + frame);
                }
                else
                {
                    Assert.AreEqual(before, rig.Pass.GridBuilds, "skip " + frame);
                    Assert.AreEqual(builtShift.x, rig.Pass.LastShift.x, 0f, "held shift x");
                    Assert.AreEqual(builtShift.y, rig.Pass.LastShift.y, 0f, "held shift y");
                    Vector2[] grad = rig.ReadGrad();
                    Vector3[] velocity = rig.ReadVelocity();
                    for (int i = 0; i < grad.Length; i++)
                    {
                        Assert.AreEqual(builtGrad[i].x, grad[i].x, 0f, "grad x " + i);
                        Assert.AreEqual(builtGrad[i].y, grad[i].y, 0f, "grad y " + i);
                    }

                    for (int i = 0; i < velocity.Length; i++)
                    {
                        Assert.AreEqual(builtVelocity[i].x, velocity[i].x, 0f, "vx " + i);
                        Assert.AreEqual(builtVelocity[i].z, velocity[i].z, 0f, "vz " + i);
                    }
                }
            }

            Assert.AreEqual(2, rig.Pass.GridBuilds);
        }
    }

    [Test]
    public void ForceInterleaveTwo_AlternatesGroupsAndKeepsTheStoredForce()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector2[] positions = DenseSquare(32, 4f, new System.Random(9));
        using (Rig rig = new Rig(positions, Teams(positions.Length, 0), 45f, jitter: false))
        {
            rig.Pass.ForceInterleave = 2;
            rig.Step();
            Vector2[] stored = rig.ReadStored();
            for (int i = 0; i < stored.Length; i++)
            {
                Assert.IsFalse(float.IsNaN(stored[i].x), "fresh " + i);
            }

            rig.Pass.Weight = 0.2f;
            rig.ZeroVelocity();
            rig.Step();
            Vector2[] next = rig.ReadStored();
            int skipped = 0;
            int recomputed = 0;
            for (int i = 0; i < next.Length; i++)
            {
                bool due = ((PressureHash((uint)i) + 1u) % 2u) == 0u;
                if (due)
                {
                    if (stored[i].magnitude > 1e-3f)
                    {
                        float delta = (next[i] - stored[i]).magnitude;
                        Assert.Greater(delta, 1e-4f, "recomputed " + i);
                        recomputed++;
                    }
                }
                else
                {
                    Assert.AreEqual(stored[i].x, next[i].x, 0f, "stored x " + i);
                    Assert.AreEqual(stored[i].y, next[i].y, 0f, "stored y " + i);
                    skipped++;
                }
            }

            Assert.Greater(skipped, 0);
            Assert.Greater(recomputed, 0);
        }
    }

    [Test]
    public void LoneParticle_Intervals_StayWithinTolerance()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        int[] grids = { 1, 2, 1, 2 };
        int[] forces = { 1, 1, 2, 2 };
        bool[] jitters = { false, true };
        for (int pair = 0; pair < grids.Length; pair++)
        {
            for (int j = 0; j < jitters.Length; j++)
            {
                using (Rig rig = new Rig(new[] { Vector2.zero }, new[] { 0 }, 45f, jitter: jitters[j]))
                {
                    rig.Pass.GridUpdateInterval = grids[pair];
                    rig.Pass.ForceInterleave = forces[pair];
                for (int step = 0; step < 20; step++)
                {
                    rig.SetPosition(new Vector2((step - 10) * 0.37f, (step % 5) * 0.2f));
                    rig.ZeroVelocity();
                    rig.ResetFrame();
                    rig.Step();
                        Vector3 velocity = rig.ReadVelocity()[0];
                        Assert.LessOrEqual(
                            new Vector2(velocity.x, velocity.z).magnitude,
                            1e-5f,
                            "grid " + grids[pair] + " force " + forces[pair] + " jitter " + jitters[j] + " step " + step);
                    }
                }
            }
        }
    }

    [Test]
    public void DenseCrowd_IntervalsTwo_StaysFiniteInsideWeightAndConservesN()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector2[] positions = DenseSquare(5000, 10f, new System.Random(7));
        int[] teams = Teams(positions.Length, 0);
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

        using (Rig rig = new Rig(positions, teams, 45f, jitter: true))
        {
            rig.Pass.GridUpdateInterval = 2;
            rig.Pass.ForceInterleave = 2;
            rig.Pass.Weight = 0.8f;
            for (int frame = 0; frame < 4; frame++)
            {
                rig.ZeroVelocity();
                rig.Step();
                Vector3[] velocity = rig.ReadVelocity();
                for (int i = 0; i < velocity.Length; i++)
                {
                    Assert.IsTrue(float.IsFinite(velocity[i].x) && float.IsFinite(velocity[i].z), "finite " + i);
                    Assert.LessOrEqual(new Vector2(velocity[i].x, velocity[i].z).magnitude, 0.8f + 1e-5f, "mag " + i);
                }

                uint[] density = rig.ReadDensity();
                Assert.AreEqual(team0 * 16384L, SumTeam(density, 0));
                Assert.AreEqual(team1 * 16384L, SumTeam(density, 1));
            }
        }
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

            rig.Pass.FieldStage = FieldRepeatStage.Press;
            rig.Pass.FieldRepeatCount = 4;
            rig.ZeroVelocity();
            rig.Step();
            Assert.IsNotNull(rig.Pass.ScratchVelocity);
            Assert.AreEqual(3, rig.Pass.RepeatClearDispatches);
            Assert.AreEqual(3, rig.Pass.RepeatScatterDispatches);
            Assert.AreEqual(3, rig.Pass.RepeatGradientDispatches);
            Assert.AreEqual(3, rig.Pass.RepeatForceDispatches);
        }
    }

    [Test]
    public void PressureSubstage_RepeatsOnlyTheChosenKernel()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        var stages = new[]
        {
            FieldRepeatStage.PClear,
            FieldRepeatStage.PScatter,
            FieldRepeatStage.PGrad,
            FieldRepeatStage.PForce,
        };
        using (Rig rig = new Rig(new[] { new Vector2(1f, 2f) }, new[] { 0 }, 45f, jitter: false))
        {
            for (int i = 0; i < stages.Length; i++)
            {
                rig.Pass.FieldStage = stages[i];
                rig.Pass.FieldRepeatCount = 4;
                rig.ZeroVelocity();
                rig.Step();
                int chosen = stages[i] == FieldRepeatStage.PClear ? rig.Pass.RepeatClearDispatches
                    : stages[i] == FieldRepeatStage.PScatter ? rig.Pass.RepeatScatterDispatches
                    : stages[i] == FieldRepeatStage.PGrad ? rig.Pass.RepeatGradientDispatches
                    : rig.Pass.RepeatForceDispatches;
                Assert.AreEqual(3, chosen, stages[i].ToString());
                Assert.AreEqual(stages[i] == FieldRepeatStage.PClear ? 3 : 0, rig.Pass.RepeatClearDispatches);
                Assert.AreEqual(stages[i] == FieldRepeatStage.PScatter ? 3 : 0, rig.Pass.RepeatScatterDispatches);
                Assert.AreEqual(stages[i] == FieldRepeatStage.PGrad ? 3 : 0, rig.Pass.RepeatGradientDispatches);
                Assert.AreEqual(stages[i] == FieldRepeatStage.PForce ? 3 : 0, rig.Pass.RepeatForceDispatches);
            }
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

    [Test]
    public void ScatterSorted_MatchesUnsortedDensity()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Vector2[] positions = Crowd(64, 7);
        int[] teams = Teams(64, 3);
        uint[] plain;
        using (Rig rig = new Rig(positions, teams, 8f, jitter: false, texel: 1f, hashRefresh: 1))
        {
            rig.Step();
            plain = rig.ReadDensity();
        }

        uint[] sorted;
        using (Rig rig = new Rig(positions, teams, 8f, jitter: false, texel: 1f, hashRefresh: 1))
        {
            rig.Pass.ScatterSorted = true;
            rig.Step();
            sorted = rig.ReadDensity();
        }

        Assert.AreEqual(plain.Length, sorted.Length);
        for (int i = 0; i < plain.Length; i++)
        {
            Assert.AreEqual(plain[i], sorted[i], "density " + i);
        }
    }

    private struct ForceStats
    {
        public double MaxAbs;
        public double MeanAbs;
        public double FracAboveOneMilli;
    }

    private static ForceStats RunForce(Vector2[] positions, int[] teams, string label)
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
            double sumAbs = 0.0;
            int counted = 0;
            int above = 0;
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
                double mag = Math.Sqrt(
                    (double)velocity[i].x * velocity[i].x + (double)velocity[i].z * velocity[i].z);
                sumAbs += mag;
                counted++;
                if (mag > 1e-3)
                {
                    above++;
                }
            }

            return new ForceStats
            {
                MaxAbs = max,
                MeanAbs = counted == 0 ? 0.0 : sumAbs / counted,
                FracAboveOneMilli = counted == 0 ? 0.0 : (double)above / counted,
            };
        }
    }

    private static float PairMagnitude(Vector2[] positions, bool sameTeam)
    {
        int[] teams = sameTeam ? new[] { 0, 0 } : new[] { 0, 1 };
        using (Rig rig = new Rig(positions, teams, 45f, jitter: false))
        {
            rig.Step();
            Vector3[] velocity = rig.ReadVelocity();
            return new Vector2(velocity[0].x, velocity[0].z).magnitude;
        }
    }

    private static double CompareSeamToInside(
        Vector2[] seam, Vector2[] inside, bool sameTeam, string label)
    {
        int[] teams = sameTeam ? new[] { 0, 0 } : new[] { 0, 1 };
        Vector3[] seamVel;
        Vector3[] insideVel;
        using (Rig rig = new Rig(seam, teams, 45f, jitter: false))
        {
            rig.Step();
            seamVel = rig.ReadVelocity();
        }

        using (Rig rig = new Rig(inside, teams, 45f, jitter: false))
        {
            rig.Step();
            insideVel = rig.ReadVelocity();
        }

        double max = 0.0;
        for (int i = 0; i < 2; i++)
        {
            Assert.AreEqual(insideVel[i].x, seamVel[i].x, 1e-6f, label + " x " + i);
            Assert.AreEqual(insideVel[i].z, seamVel[i].z, 1e-6f, label + " z " + i);
            Assert.AreEqual(0f, seamVel[i].y, 0f);
            max = Math.Max(max, Math.Abs(seamVel[i].x - insideVel[i].x));
            max = Math.Max(max, Math.Abs(seamVel[i].z - insideVel[i].z));
        }

        return max;
    }

    private static Vector2[] DenseSquare(int count, float side, System.Random random)
    {
        float half = side * 0.5f;
        var positions = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector2(
                (float)(-half + random.NextDouble() * side),
                (float)(-half + random.NextDouble() * side));
        }

        return positions;
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

    private static uint PressureHash(uint index)
    {
        return index * 747796405u + 2891336453u;
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

        private readonly BuildSpatialHashPass hash;

        public Rig(Vector2[] positions, int[] teamIds, float extent, bool jitter, float texel = 0.25f, int hashRefresh = 0)
        {
            Pass = new FieldDensityPressurePass
            {
                Center = Vector3.zero,
                Extents = new Vector3(extent, 0f, extent),
                PressureTexel = texel,
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
            ComputeShader hashShader = null;
            if (hashRefresh > 0)
            {
                hashShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute");
                Assert.IsNotNull(hashShader);
            }

            context = new SimContext(
                particles,
                fields,
                hashShader == null ? new[] { shader } : new[] { hashShader, shader },
                null);
            cmd = new CommandBuffer { name = "DensityPressure" };
            context.Cmd = cmd;
            context.Teams = teams;
            Pass.Initialize(context);
            BuildSpatialHashPass built = null;
            if (hashRefresh > 0)
            {
                built = new BuildSpatialHashPass
                {
                    Center = Vector3.zero,
                    Extents = new Vector3(extent, 0f, extent),
                    MinCellSize = texel,
                    Wrap = true,
                    RefreshInterval = hashRefresh,
                };
                built.Initialize(context);
            }

            hash = built;
        }

        public FieldDensityPressurePass Pass { get; }

        public int Cells => Pass.Layout.CellCount;

        public void ResetFrame()
        {
            Pass.ResetSchedule();
        }

        public void WritePositions(Vector2[] positions)
        {
            var xyz = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                xyz[i] = new Vector3(positions[i].x, 0f, positions[i].y);
            }

            particles.Get(BuiltinAttributes.Position).SetData(xyz);
        }

        public Vector2[] ReadStored()
        {
            var values = new Vector2[Pass.StoredForce.count];
            Pass.StoredForce.GetData(values);
            return values;
        }

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
            hash?.Execute(context, 1f / 60f);
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
            Pass.BindIndexBuffer(cmd, kernel);
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
            hash?.Dispose();
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
