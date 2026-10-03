using System;
using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class SpatialHashLayoutTests
{
    [Test]
    public void ComputeLayout_CellWithinTolerance()
    {
        ExpectResolution(45f, 3f, 30);
        ExpectResolution(0.15f, 0.1f, 3);
        ExpectResolution(10f, 3f, 6);
        ExpectResolution(16f, 2f, 16);
    }

    [Test]
    public void ComputeLayout_InvalidInputs_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Layout(1f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layout(1f, -1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutAxis(0f, 1f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutAxis(1f, -1f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layout(1f, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layout(float.NaN, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layout(float.PositiveInfinity, 1f));
    }

    [Test]
    public void ValidateLayout_WrapNeedsThreeCells()
    {
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(2f, 0f, 2f), 2f);
        Assert.AreEqual(2, layout.Resolution.x);
        Assert.Throws<InvalidOperationException>(() => SpatialHashSet.ValidateLayout(layout, true));
        Assert.DoesNotThrow(() => SpatialHashSet.ValidateLayout(layout, false));
    }

    [Test]
    public void ValidateLayout_SingleCellWithoutWrap_Ok()
    {
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(1f, 0f, 1f), 2f);
        Assert.AreEqual(1, layout.Resolution.x);
        Assert.DoesNotThrow(() => SpatialHashSet.ValidateLayout(layout, false));
    }

    [Test]
    public void ValidateLayout_TooManyCells_Throws()
    {
        SpatialHashLayout layout = SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(128.5f, 0f, 128f), 1f);
        Assert.AreEqual(257, layout.Resolution.x);
        Assert.AreEqual(256, layout.Resolution.y);
        Assert.Throws<InvalidOperationException>(() => SpatialHashSet.ValidateLayout(layout, true));
        Assert.Throws<InvalidOperationException>(() => SpatialHashSet.ValidateLayout(layout, false));
    }

    private static void ExpectResolution(float extent, float minCellSize, int expected)
    {
        SpatialHashLayout layout = Layout(extent, minCellSize);
        Assert.AreEqual(expected, layout.Resolution.x);
        Assert.AreEqual(expected, layout.Resolution.y);
        double size = layout.Size.x;
        double cell = layout.CellSize.x;
        Assert.GreaterOrEqual(cell, minCellSize * (1.0 - 1e-5));
        Assert.LessOrEqual(Math.Abs(expected * cell - size), 1e-5 * size);
    }

    private static SpatialHashLayout Layout(float extent, float minCellSize)
    {
        return LayoutAxis(extent, extent, minCellSize);
    }

    private static SpatialHashLayout LayoutAxis(float extentX, float extentZ, float minCellSize)
    {
        return SpatialHashSet.ComputeLayout(Vector3.zero, new Vector3(extentX, 0f, extentZ), minCellSize);
    }
}
