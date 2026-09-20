using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class ScriptedTouchStrokeTests
{
    private static readonly Vector3 Origin = Vector3.zero;
    private static readonly Vector3 AxisU = Vector3.right;
    private static readonly Vector3 AxisV = Vector3.forward;
    private static readonly Vector2 Size = new Vector2(32f, 32f);
    private const float Duration = 0.5f;

    [Test]
    public void UvToWorld_Center_IsOrigin()
    {
        Vector3 world = ScriptedTouchStroke.UvToWorld(
            new Vector2(0.5f, 0.5f), Origin, AxisU, AxisV, Size);
        Assert.AreEqual(Vector3.zero, world);
    }

    [Test]
    public void UvToWorld_StartAndEnd_MatchFieldFormula()
    {
        Vector3 start = ScriptedTouchStroke.UvToWorld(
            new Vector2(0.5f, 0.25f), Origin, AxisU, AxisV, Size);
        Vector3 end = ScriptedTouchStroke.UvToWorld(
            new Vector2(0.5f, 0.75f), Origin, AxisU, AxisV, Size);
        Assert.AreEqual(new Vector3(0f, 0f, -8f), start);
        Assert.AreEqual(new Vector3(0f, 0f, 8f), end);
    }

    [Test]
    public void Evaluate_TZero_CountOneDeltaZeroAtStart()
    {
        Vector3 start = new Vector3(0f, 0f, -8f);
        Vector3 end = new Vector3(0f, 0f, 8f);
        ScriptedTouchSample sample = ScriptedTouchStroke.Evaluate(0f, Duration, start, end);
        Assert.AreEqual(1, sample.Count);
        Assert.AreEqual(start, sample.Position);
        Assert.AreEqual(Vector3.zero, sample.Delta);
    }

    [Test]
    public void Evaluate_HalfDuration_PositionIsMidpoint()
    {
        Vector3 start = new Vector3(0f, 0f, -8f);
        Vector3 end = new Vector3(0f, 0f, 8f);
        ScriptedTouchSample sample = ScriptedTouchStroke.Evaluate(
            Duration * 0.5f, Duration, start, end);
        Assert.AreEqual(1, sample.Count);
        Assert.AreEqual(Vector3.zero, sample.Position);
    }

    [Test]
    public void Evaluate_AtAndAfterDuration_CountZero()
    {
        Vector3 start = new Vector3(0f, 0f, -8f);
        Vector3 end = new Vector3(0f, 0f, 8f);
        ScriptedTouchSample atEnd = ScriptedTouchStroke.Evaluate(Duration, Duration, start, end);
        ScriptedTouchSample after = ScriptedTouchStroke.Evaluate(
            Duration + 0.1f, Duration, start, end);
        Assert.AreEqual(0, atEnd.Count);
        Assert.AreEqual(0, after.Count);
    }

    [Test]
    public void Evaluate_SameT_PositionIndependentOfDtGrid()
    {
        Vector3 start = new Vector3(0f, 0f, -8f);
        Vector3 end = new Vector3(0f, 0f, 8f);
        const float t = 0.2f;
        ScriptedTouchSample a = ScriptedTouchStroke.Evaluate(t, Duration, start, end);
        ScriptedTouchSample b = ScriptedTouchStroke.Evaluate(t, Duration, start, end);
        Assert.AreEqual(a.Position, b.Position);
        Assert.AreEqual(1, a.Count);
    }
}
