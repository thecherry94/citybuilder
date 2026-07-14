using System.Numerics;
using CityBuilder.Domain.Geometry;
using Xunit;

namespace CityBuilder.Domain.Tests.Geometry;

public class BezierOpsTests
{
    [Fact]
    public void CrossingLinesHaveOneIntersectionAtExpectedParams()
    {
        var a = Bezier3.Line(new(-10, 0, 0), new(10, 0, 0));
        var b = Bezier3.Line(new(0, 0, -10), new(0, 0, 10));
        var hits = BezierOps.Intersections(a, b);
        var hit = Assert.Single(hits);
        Assert.Equal(0.5f, hit.t1, 2);
        Assert.Equal(0.5f, hit.t2, 2);
    }

    [Fact]
    public void SCurveOverStraightHasTwoIntersections()
    {
        // arch starting and ending below the axis, bulging above it: exactly 2 crossings
        var s = new Bezier3(new(-10, 0, -5), new(-3, 0, 20), new(3, 0, 20), new(10, 0, -5));
        var line = Bezier3.Line(new(-20, 0, 0), new(20, 0, 0));
        var hits = BezierOps.Intersections(s, line);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void DisjointCurvesHaveNoIntersections()
    {
        var a = Bezier3.Line(new(0, 0, 0), new(10, 0, 0));
        var b = Bezier3.Line(new(0, 0, 5), new(10, 0, 5));
        Assert.Empty(BezierOps.Intersections(a, b));
    }

    [Fact]
    public void SharedEndpointIsNotDuplicated()
    {
        var a = Bezier3.Line(new(0, 0, 0), new(10, 0, 0));
        var b = Bezier3.Line(new(10, 0, 0), new(10, 0, 10));
        var hits = BezierOps.Intersections(a, b);
        Assert.True(hits.Count <= 1, $"expected at most 1 hit, got {hits.Count}");
    }

    [Fact]
    public void ClosestPointOnLineIsProjection()
    {
        var a = Bezier3.Line(new(0, 0, 0), new(10, 0, 0));
        var (t, dist) = BezierOps.ClosestPoint(a, new Vector3(5, 0, 3));
        Assert.Equal(0.5f, t, 2);
        Assert.Equal(3f, dist, 2);
    }

    [Fact]
    public void ClosestPointClampsToEndpoints()
    {
        var a = Bezier3.Line(new(0, 0, 0), new(10, 0, 0));
        var (t, dist) = BezierOps.ClosestPoint(a, new Vector3(-4, 0, 3));
        Assert.Equal(0f, t, 2);
        Assert.Equal(5f, dist, 2);
    }

    [Fact]
    public void TessellationRespectsChordTolerance()
    {
        var b = new Bezier3(new(0, 0, 0), new(0, 0, 20), new(20, 0, 20), new(20, 0, 0));
        const float tol = 0.15f;
        var ts = BezierOps.Tessellate(b, tol);
        Assert.Equal(0f, ts[0]);
        Assert.Equal(1f, ts[^1]);
        for (int i = 0; i + 1 < ts.Count; i++)
        {
            var mid = (b.Point(ts[i]) + b.Point(ts[i + 1])) / 2;
            var onCurve = b.Point((ts[i] + ts[i + 1]) / 2);
            Assert.True(Vector3.Distance(mid, onCurve) <= tol * 1.5f,
                $"chord {i} deviates {Vector3.Distance(mid, onCurve)}");
        }
    }

    [Fact]
    public void ArcLengthTableRoundTrips()
    {
        var b = new Bezier3(new(0, 0, 0), new(5, 0, 12), new(15, 0, 12), new(20, 0, 0));
        var table = new ArcLengthTable(b);
        foreach (var t in new[] { 0f, 0.2f, 0.5f, 0.77f, 1f })
            Assert.Equal(t, table.TAtDistance(table.DistanceAtT(t)), 2);
        Assert.Equal(b.Length(), table.TotalLength, 1);
    }

    [Fact]
    public void StraightLineDoesNotSelfIntersect()
    {
        Assert.False(BezierOps.SelfIntersects(Bezier3.Line(new(0, 0, 0), new(10, 0, 0))));
    }

    [Fact]
    public void LoopedCurveSelfIntersects()
    {
        // control points force a loop
        var loop = new Bezier3(new(0, 0, 0), new(20, 0, 10), new(-15, 0, 10), new(5, 0, 0));
        Assert.True(BezierOps.SelfIntersects(loop));
    }

    [Fact]
    public void MinRadiusOfStraightLineIsInfinite()
    {
        var line = Bezier3.Line(new Vector3(0, 0, 0), new Vector3(100, 0, 0));
        Assert.Equal(float.PositiveInfinity, BezierOps.MinRadius(line));
    }

    [Fact]
    public void MinRadiusRecoversCircleRadius()
    {
        // quarter circle of radius 50 approximated by one cubic: kappa constant ~1/50
        const float r = 50f;
        float k = 4f / 3f * MathF.Tan(MathF.PI / 8f) * r; // standard 90° arc handle length
        var arc = new Bezier3(
            new Vector3(r, 0, 0),
            new Vector3(r, 0, k),
            new Vector3(k, 0, r),
            new Vector3(0, 0, r));
        float min = BezierOps.MinRadius(arc);
        Assert.InRange(min, r * 0.98f, r * 1.02f);
    }

    [Fact]
    public void MinRadiusOfTightBendIsSmallerThanWideBend()
    {
        var wide = Bezier3.FromQuadratic(new Vector3(0, 0, 0), new Vector3(50, 0, 10), new Vector3(100, 0, 0));
        var tight = Bezier3.FromQuadratic(new Vector3(0, 0, 0), new Vector3(50, 0, 60), new Vector3(100, 0, 0));
        Assert.True(BezierOps.MinRadius(tight) < BezierOps.MinRadius(wide));
    }
}
