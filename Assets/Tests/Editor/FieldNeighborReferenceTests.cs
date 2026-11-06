using NUnit.Framework;
using UnityEngine;

public class FieldNeighborReferenceTests
{
    [Test]
    public void Disk_KeepsTwentyFiveOffsetsAtThree_AndGrowsWithRt()
    {
        Assert.AreEqual(25, FieldNeighborReference.DiskOffsetCount(3.0));
        Assert.AreEqual(69, FieldNeighborReference.DiskOffsetCount(5.0));
        Assert.AreEqual(109, FieldNeighborReference.DiskOffsetCount(6.0));
    }

    [Test]
    public void CenterParticle_PutsAllMassOnOneTexel()
    {
        SpatialHashLayout layout = Layout(8f);
        Vector2 origin = layout.Origin;
        Vector2[] positions = { origin + new Vector2(3f, 4f) };
        Vector2[] headings = { new Vector2(1f, 0f) };
        int[] teams = { 0 };
        double[] raw = FieldNeighborReference.Collect(positions, headings, teams, layout);
        int cell = 4 * layout.Resolution.x + 3;
        Assert.AreEqual(1.0, raw[cell * 6], 1e-12);
        Assert.AreEqual(1.0, raw[cell * 6 + 1], 1e-12);
        Assert.AreEqual(0.0, SumExcept(raw, cell, 0), 1e-12);
    }

    [Test]
    public void CellMiddle_SplitsQuarterWeights()
    {
        SpatialHashLayout layout = Layout(8f);
        Vector2 p = layout.Origin + new Vector2(2.5f, 3.5f);
        double[] raw = FieldNeighborReference.Collect(
            new[] { p },
            new[] { Vector2.up },
            new[] { 1 },
            layout);
        Assert.AreEqual(0.25, Channel(raw, layout, 2, 3, 1), 1e-12);
        Assert.AreEqual(0.25, Channel(raw, layout, 3, 3, 1), 1e-12);
        Assert.AreEqual(0.25, Channel(raw, layout, 2, 4, 1), 1e-12);
        Assert.AreEqual(0.25, Channel(raw, layout, 3, 4, 1), 1e-12);
    }

    [Test]
    public void Seam_SendsWeightToTheOppositeSide()
    {
        SpatialHashLayout layout = Layout(4f);
        Vector2 p = layout.Origin + new Vector2(layout.Size.x - 0.25f, 1.5f);
        double[] raw = FieldNeighborReference.Collect(
            new[] { p },
            new[] { new Vector2(0f, 1f) },
            new[] { 0 },
            layout);
        Assert.Greater(Column(raw, layout, 0, 0), 0.5);
        Assert.Greater(Column(raw, layout, layout.Resolution.x - 1, 0), 0.2);
        Assert.AreEqual(1.0, SumChannel(raw, 0), 1e-12);
    }

    [Test]
    public void SumN_EqualsParticleCount_AndMomentIsPreserved()
    {
        SpatialHashLayout layout = Layout(6f);
        var random = new System.Random(7);
        Vector2[] positions = new Vector2[20];
        Vector2[] headings = new Vector2[20];
        int[] teams = new int[20];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = layout.Origin + new Vector2(
                (float)random.NextDouble() * layout.Size.x,
                (float)random.NextDouble() * layout.Size.y);
            headings[i] = new Vector2(1f, 0f);
            teams[i] = i % 2;
        }

        double[] raw = FieldNeighborReference.Collect(positions, headings, teams, layout);
        Assert.AreEqual(10.0, SumChannel(raw, 0), 1e-9);
        Assert.AreEqual(10.0, SumChannel(raw, 1), 1e-9);
        for (int i = 0; i < positions.Length; i++)
        {
            Vector2 moment = Moment(layout, positions[i]);
            Assert.AreEqual(positions[i].x, moment.x, 1e-5f);
            Assert.AreEqual(positions[i].y, moment.y, 1e-5f);
        }
    }

    [Test]
    public void Disk_Has25Offsets_AndPsiIsFlatInsideR0()
    {
        Assert.AreEqual(25, FieldNeighborReference.DiskOffsetCount(3.0));
        Assert.AreEqual(0.0, FieldNeighborReference.Psi(3.0, 3.0, 1.0), 1e-12);
        double inside = FieldNeighborReference.Psi(0.2, 3.0, 1.0);
        double edge = FieldNeighborReference.Psi(1.0, 3.0, 1.0);
        Assert.AreEqual(edge, inside, 1e-12);
        Assert.Less(inside, 0.0);
    }

    private static SpatialHashLayout Layout(float size)
    {
        float half = size * 0.5f;
        return SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(half, 0f, half), 1f);
    }

    private static double Channel(double[] raw, SpatialHashLayout layout, int x, int y, int team)
    {
        int resX = layout.Resolution.x;
        int xx = x % resX;
        if (xx < 0)
        {
            xx += resX;
        }

        int yy = y % layout.Resolution.y;
        if (yy < 0)
        {
            yy += layout.Resolution.y;
        }

        return raw[(yy * resX + xx) * 6 + team * 3];
    }

    private static double Column(double[] raw, SpatialHashLayout layout, int x, int team)
    {
        double sum = 0;
        for (int y = 0; y < layout.Resolution.y; y++)
        {
            sum += Channel(raw, layout, x, y, team);
        }

        return sum;
    }

    private static double SumChannel(double[] raw, int team)
    {
        double sum = 0;
        for (int i = team * 3; i < raw.Length; i += 6)
        {
            sum += raw[i];
        }

        return sum;
    }

    private static double SumExcept(double[] raw, int cell, int team)
    {
        double sum = 0;
        for (int i = 0; i < raw.Length / 6; i++)
        {
            if (i == cell)
            {
                continue;
            }

            sum += raw[i * 6 + team * 3];
        }

        return sum;
    }

    private static Vector2 Moment(SpatialHashLayout layout, Vector2 particle)
    {
        Vector2 u = (particle - layout.Origin) / layout.CellSize;
        int ix = (int)Mathf.Floor(u.x);
        int iy = (int)Mathf.Floor(u.y);
        double fx = u.x - ix;
        double fy = u.y - iy;
        Vector2 sum = Vector2.zero;
        sum += (float)((1.0 - fx) * (1.0 - fy)) * FieldNeighborReference.UnwrappedCenter(layout, ix, iy);
        sum += (float)(fx * (1.0 - fy)) * FieldNeighborReference.UnwrappedCenter(layout, ix + 1, iy);
        sum += (float)((1.0 - fx) * fy) * FieldNeighborReference.UnwrappedCenter(layout, ix, iy + 1);
        sum += (float)(fx * fy) * FieldNeighborReference.UnwrappedCenter(layout, ix + 1, iy + 1);
        return sum;
    }
}
