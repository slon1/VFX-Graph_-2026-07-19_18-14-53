using NUnit.Framework;
using UnityEngine;

public class FieldForceReferenceTests
{
    // On texel centres CIC is a delta, and ox^2+oz^2 < R^2 is the same neighbour
    // set as r^2 < R^2. A full periodic lattice makes Phi and K constant, so
    // separation and cohesion are zero on both sides and alignment is the same
    // unit heading. The residual is double rounding of that identity.
    private const double LatticeTolerance = 1e-9;

    private static readonly Vector3[] Weights =
    {
        new Vector3(1.2f, 0.8f, 0.6f),
        new Vector3(1.2f, 0.8f, 0.6f),
    };

    private static readonly double[] Inter = { 4.0, 4.0 };

    [Test]
    public void OneParticle_FieldReadCancelsSelf_AndTraversalIsZero()
    {
        SpatialHashLayout layout = Layout(16f);
        Vector2[] positions = { layout.Origin + new Vector2(3.3f, 4.7f) };
        Vector2[] headings = { new Vector2(0.6f, 0.8f) };
        int[] teams = { 0 };
        double[] field = BuildField(positions, headings, teams, layout, 3.0);
        FieldForceReference.Sample read = FieldForceReference.FieldRead(
            field, positions, headings, teams, Weights, layout)[0];
        FieldForceReference.Sample walk = FieldForceReference.FullTraversal(
            positions, headings, teams, Weights, Inter, 3.0, layout.Size)[0];

        AssertNear(read, 0, 0, 1e-9);
        AssertNear(walk, 0, 0, 1e-12);
        Assert.AreEqual(0, walk.NgRef);
        Assert.AreEqual(0.0, walk.MeanDisplacement, 0.0);
    }

    [Test]
    public void SameTeamPair_Repels_AlignsWithNeighbour_CohereTowardNeighbour()
    {
        Vector2[] positions = { new Vector2(4f, 4f), new Vector2(5f, 4f) };
        Vector2[] headings = { new Vector2(1f, 0f), new Vector2(0f, 1f) };
        int[] teams = { 0, 0 };
        FieldForceReference.Sample[] force = FieldForceReference.FullTraversal(
            positions, headings, teams, Weights, Inter, 3.0, new Vector2(16f, 16f));

        Assert.Less(force[0].SepX, 0.0);
        Assert.AreEqual(0.0, force[0].SepY, 1e-12);
        Assert.AreEqual(0.0, force[0].AlignX, 1e-12);
        Assert.Greater(force[0].AlignY, 0.0);
        Assert.Greater(force[0].CohX, 0.0);
        Assert.AreEqual(0.0, force[0].CohY, 1e-12);
        Assert.Greater(force[1].SepX, 0.0);
        Assert.AreEqual(1, force[0].NgRef);
        Assert.AreEqual(1.0, force[0].MeanDisplacement, 1e-12);
    }

    [Test]
    public void OtherTeamPair_ScalesSeparation_AndZerosAlignCoh()
    {
        Vector2[] positions = { new Vector2(4f, 4f), new Vector2(5f, 4f) };
        Vector2[] headings = { new Vector2(1f, 0f), new Vector2(0f, 1f) };
        FieldForceReference.Sample own = FieldForceReference.FullTraversal(
            positions, headings, new[] { 0, 0 }, Weights, Inter, 3.0, new Vector2(16f, 16f))[0];
        FieldForceReference.Sample other = FieldForceReference.FullTraversal(
            positions, headings, new[] { 0, 1 }, Weights, Inter, 3.0, new Vector2(16f, 16f))[0];

        Assert.AreEqual(own.SepX * Inter[0], other.SepX, 1e-12);
        Assert.AreEqual(0.0, other.SepY, 1e-12);
        Assert.AreEqual(0.0, other.AlignX, 1e-12);
        Assert.AreEqual(0.0, other.AlignY, 1e-12);
        Assert.AreEqual(0.0, other.CohX, 1e-12);
        Assert.AreEqual(0.0, other.CohY, 1e-12);
        Assert.AreEqual(0, other.NgRef);
        Assert.AreEqual(0.0, other.MeanDisplacement, 0.0);
    }

    [Test]
    public void TorusSeam_MatchesAnInteriorPairWithTheSameMinImage()
    {
        Vector2 world = new Vector2(16f, 16f);
        Vector2[] seam =
        {
            new Vector2(0.25f, 4f),
            new Vector2(15.25f, 4f),
        };
        Vector2[] interior =
        {
            new Vector2(4.25f, 4f),
            new Vector2(3.25f, 4f),
        };
        Vector2[] headings = { new Vector2(1f, 0f), new Vector2(0f, 1f) };
        int[] teams = { 0, 0 };
        FieldForceReference.Sample across = FieldForceReference.FullTraversal(
            seam, headings, teams, Weights, Inter, 3.0, world)[0];
        FieldForceReference.Sample near = FieldForceReference.FullTraversal(
            interior, headings, teams, Weights, Inter, 3.0, world)[0];

        Assert.AreEqual(near.SepX, across.SepX, 1e-12);
        Assert.AreEqual(near.SepY, across.SepY, 1e-12);
        Assert.AreEqual(near.AlignX, across.AlignX, 1e-12);
        Assert.AreEqual(near.AlignY, across.AlignY, 1e-12);
        Assert.AreEqual(near.CohX, across.CohX, 1e-12);
        Assert.AreEqual(near.CohY, across.CohY, 1e-12);
        Assert.Greater(across.SepX, 0.0);
        Assert.AreEqual(1, across.NgRef);
    }

    [Test]
    public void LatticeOnTexels_FieldReadMatchesFullTraversal()
    {
        SpatialHashLayout layout = Layout(12f);
        int resX = layout.Resolution.x;
        int resY = layout.Resolution.y;
        int count = resX * resY;
        var positions = new Vector2[count];
        var headings = new Vector2[count];
        var teams = new int[count];
        for (int y = 0; y < resY; y++)
        {
            for (int x = 0; x < resX; x++)
            {
                int i = y * resX + x;
                positions[i] = layout.Origin + new Vector2(x, y);
                headings[i] = new Vector2(1f, 0f);
                teams[i] = 0;
            }
        }

        double[] field = BuildField(positions, headings, teams, layout, 3.0);
        FieldForceReference.Sample[] read = FieldForceReference.FieldRead(
            field, positions, headings, teams, Weights, layout);
        FieldForceReference.Sample[] walk = FieldForceReference.FullTraversal(
            positions, headings, teams, Weights, Inter, 3.0, layout.Size);
        for (int i = 0; i < count; i++)
        {
            Assert.AreEqual(walk[i].Fx, read[i].Fx, LatticeTolerance, "Fx " + i);
            Assert.AreEqual(walk[i].Fy, read[i].Fy, LatticeTolerance, "Fy " + i);
            Assert.AreEqual(walk[i].SepX, read[i].SepX, LatticeTolerance, "sep " + i);
            Assert.AreEqual(walk[i].AlignX, read[i].AlignX, LatticeTolerance, "align " + i);
            Assert.AreEqual(walk[i].CohX, read[i].CohX, LatticeTolerance, "coh " + i);
        }
    }

    private static double[] BuildField(
        Vector2[] positions,
        Vector2[] headings,
        int[] teams,
        SpatialHashLayout layout,
        double radius)
    {
        double[] raw = FieldNeighborReference.Collect(positions, headings, teams, layout);
        FieldNeighborReference.Convolve(raw, layout, radius, Inter, out double[] phi, out double[] field);
        FieldNeighborReference.Gradient(phi, layout, field);
        return field;
    }

    private static SpatialHashLayout Layout(float size)
    {
        float half = size * 0.5f;
        return SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(half, 0f, half), 1f);
    }

    private static void AssertNear(FieldForceReference.Sample sample, double x, double y, double tolerance)
    {
        Assert.AreEqual(x, sample.Fx, tolerance);
        Assert.AreEqual(y, sample.Fy, tolerance);
        Assert.AreEqual(x, sample.SepX, tolerance);
        Assert.AreEqual(y, sample.SepY, tolerance);
        Assert.AreEqual(x, sample.AlignX, tolerance);
        Assert.AreEqual(y, sample.AlignY, tolerance);
        Assert.AreEqual(x, sample.CohX, tolerance);
        Assert.AreEqual(y, sample.CohY, tolerance);
    }
}
