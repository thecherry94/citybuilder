using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using Xunit;

namespace CityBuilder.Domain.Tests;

/// <summary>Geometry of degree-2 bend nodes (90° street corners and the like).</summary>
public class CornerBendTests
{
    private static RoadNode StreetCornerNode(out RoadNetwork n)
    {
        n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-50, 0, 0), new Vector3(0, 0, 0), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(0, 0, 40), RoadCatalog.Street.Id));
        return n.Nodes.Values.Single(x => x.Edges.Count == 2);
    }

    [Fact]
    public void CutsAreSymmetric()
    {
        var node = StreetCornerNode(out var n);
        var dists = node.Edges
            .Select(eid => Vector3.Distance(
                n.Edges[eid].Curve.Point(node.Junction.CutT[eid]), node.Position))
            .ToArray();
        Assert.True(MathF.Abs(dists[0] - dists[1]) < 0.5f,
            $"cut distances should be symmetric: {dists[0]:F2} vs {dists[1]:F2}");
    }

    [Fact]
    public void ElbowKeepsParallelWidth()
    {
        // roads run along -X and +Z: the outside of the turn is the (+X, -Z) diagonal.
        // With a parallel-width elbow the carriageway boundary's apex out there is the
        // corner-return apex (~1.41 m for a 7 m carriageway); the old node arc bulged
        // to CarriagewayHalf (3.5 m), making the outer lane wider than the inner one.
        var node = StreetCornerNode(out _);
        float maxOutward = node.Junction.SurfacePolygon
            .Max(p => (p.X - p.Z) / MathF.Sqrt(2));
        Assert.True(maxOutward < 2.0f,
            $"outer boundary bulges {maxOutward:F2} m beyond the node; expected ~1.41 m corner return");

        // and the apex itself is present so the asphalt still covers the outer lane
        Assert.Contains(node.Junction.SurfacePolygon,
            p => Vector3.Distance(p, new Vector3(1, 0, -1)) < 0.15f);
    }

    [Fact]
    public void ElbowLanesAreEquallyWideAtApex()
    {
        // the marking centerline apex (quadratic through the node between the 6.5 m
        // cuts) must sit halfway between the inner and outer carriageway boundaries
        var node = StreetCornerNode(out _);
        var poly = node.Junction.SurfacePolygon;
        float Diag(Vector3 p) => (p.Z - p.X) / MathF.Sqrt(2); // + toward inside of turn

        // boundary points ON the bend diagonal (|x| == |z|): by symmetry the corner
        // returns place their mid samples exactly there, on both sides of the node
        var onDiagonal = poly.Where(p => MathF.Abs(p.X + p.Z) < 0.1f).ToArray();
        Assert.NotEmpty(onDiagonal);
        float inner = onDiagonal.Max(Diag);
        float outer = onDiagonal.Min(Diag);
        float centerApex = Diag(new Vector3(-1.625f, 0, 1.625f));
        float innerLane = inner - centerApex;
        float outerLane = centerApex - outer;
        Assert.True(MathF.Abs(innerLane - outerLane) < 0.3f,
            $"lanes at bend apex differ: inner {innerLane:F2} m vs outer {outerLane:F2} m");
    }
}
