using System.Numerics;
using CityBuilder.Domain.Geometry;
using Xunit;

namespace CityBuilder.Domain.Tests.Geometry;

public class Bezier3Tests
{
    private const float Tol = 1e-4f;

    private static void AssertNear(Vector3 expected, Vector3 actual, float tol = Tol)
        => Assert.True(Vector3.Distance(expected, actual) <= tol,
            $"expected {expected}, got {actual} (dist {Vector3.Distance(expected, actual)})");

    [Fact]
    public void PointAtEndpointsReturnsControlEndpoints()
    {
        var b = new Bezier3(new(0, 0, 0), new(1, 0, 2), new(3, 0, 2), new(4, 0, 0));
        AssertNear(b.P0, b.Point(0));
        AssertNear(b.P3, b.Point(1));
    }

    [Fact]
    public void LineHasAnalyticLength()
    {
        var b = Bezier3.Line(new(1, 0, 1), new(4, 0, 5));
        Assert.Equal(5f, b.Length(), 3);
    }

    [Fact]
    public void LineTangentIsDirection()
    {
        var b = Bezier3.Line(new(0, 0, 0), new(10, 0, 0));
        AssertNear(Vector3.UnitX, b.Tangent(0.5f));
    }

    [Fact]
    public void NormalXZOfPlusXLineIsPlusZ()
    {
        // Locks the right-side convention: right = cross(direction, up)... facing +X, right must be a
        // consistent side; we define right = normalize(cross(up, d)) x ... asserted here as +Z or -Z? See impl.
        var b = Bezier3.Line(new(0, 0, 0), new(10, 0, 0));
        AssertNear(new Vector3(0, 0, 1), b.NormalXZ(0.5f));
    }

    [Fact]
    public void SplitHalvesMatchOriginal()
    {
        var b = new Bezier3(new(0, 0, 0), new(2, 0, 6), new(8, 0, 6), new(10, 0, 0));
        var (l, r) = b.Split(0.37f);
        for (int i = 0; i <= 10; i++)
        {
            float t = i / 10f;
            AssertNear(b.Point(0.37f * t), l.Point(t), 1e-3f);
            AssertNear(b.Point(0.37f + (1 - 0.37f) * t), r.Point(t), 1e-3f);
        }
    }

    [Fact]
    public void CurveLengthMatchesNumericReference()
    {
        var b = new Bezier3(new(0, 0, 0), new(0, 0, 5.522847f), new(4.477153f, 0, 10), new(10, 0, 10));
        // dense polyline reference
        float reference = 0;
        Vector3 prev = b.Point(0);
        for (int i = 1; i <= 10000; i++)
        {
            var p = b.Point(i / 10000f);
            reference += Vector3.Distance(prev, p);
            prev = p;
        }
        Assert.True(MathF.Abs(b.Length() - reference) / reference < 0.01f,
            $"len {b.Length()} vs ref {reference}");
    }

    [Fact]
    public void FromQuadraticInterpolatesQuadraticMidpoint()
    {
        Vector3 a = new(0, 0, 0), c = new(5, 0, 10), e = new(10, 0, 0);
        var b = Bezier3.FromQuadratic(a, c, e);
        // quadratic at 0.5: 0.25a + 0.5c + 0.25e
        AssertNear(0.25f * a + 0.5f * c + 0.25f * e, b.Point(0.5f));
    }

    [Fact]
    public void ReversedSwapsEndpointsAndMatchesGeometry()
    {
        var b = new Bezier3(new(0, 0, 0), new(2, 0, 6), new(8, 0, 6), new(10, 0, 0));
        var r = b.Reversed();
        for (int i = 0; i <= 10; i++)
        {
            float t = i / 10f;
            AssertNear(b.Point(t), r.Point(1 - t), 1e-4f);
        }
    }

    [Fact]
    public void DegenerateTangentFallsBackToChord()
    {
        // control points equal to endpoints: derivative at t=0 is zero; tangent must still be usable
        var b = new Bezier3(new(0, 0, 0), new(0, 0, 0), new(10, 0, 0), new(10, 0, 0));
        AssertNear(Vector3.UnitX, b.Tangent(0));
        AssertNear(Vector3.UnitX, b.Tangent(1));
    }
}
