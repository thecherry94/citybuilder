using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class HealingTests
{
    [Fact]
    public void BulldozingCrossRoadHealsTheSplitCurve()
    {
        var n = Net.New();
        var original = new Bezier3(new(-100, 0, 0), new(-30, 0, 40), new(30, 0, 40), new(100, 0, 0));
        Net.Commit(n, new PlacementProposal(
            new[] { new ProposedCurve(original, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        var r2 = Net.Commit(n, Net.Straight(new(0, 0, -50), new(0, 0, 100)));

        // curve was split by the crossing road
        Assert.Equal(4, n.Edges.Count);

        // bulldoze both halves of the crossing road
        foreach (var e in r2.CreatedEdges.Where(id => n.Edges.ContainsKey(id)).ToList())
            n.RemoveEdge(e);

        // healed back to a single edge tracing the original curve
        var edge = Assert.Single(n.Edges.Values);
        Assert.Equal(2, n.Nodes.Count);
        for (int i = 0; i <= 20; i++)
        {
            var p = original.Point(i / 20f);
            var (_, dist) = BezierOps.ClosestPoint(edge.Curve, p);
            Assert.True(dist <= GeoConstants.MergeTolerance + 0.01f, $"deviation {dist} at sample {i}");
        }
    }

    [Fact]
    public void CornerNodeIsNotMerged()
    {
        var n = Net.New();
        // an L: two straights meeting at 90°, plus a third leg to bulldoze
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(100, 0, 0), new(100, 0, 100)));
        var r3 = Net.Commit(n, Net.Straight(new(100, 0, 0), new(200, 0, 0)));

        n.RemoveEdge(r3.CreatedEdges[0]);

        // corner survives: a 90° bend cannot be one cubic within tolerance
        Assert.Equal(2, n.Edges.Count);
        Assert.Equal(3, n.Nodes.Count);
    }

    [Fact]
    public void DifferentRoadTypesDoNotMerge()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0), RoadCatalog.TwoLane.Id));
        Net.Commit(n, Net.Straight(new(100, 0, 0), new(200, 0, 0), RoadCatalog.FourLane.Id));
        var r3 = Net.Commit(n, Net.Straight(new(100, 0, 0), new(100, 0, 80)));

        n.RemoveEdge(r3.CreatedEdges[0]);

        Assert.Equal(2, n.Edges.Count);
        Assert.Equal(3, n.Nodes.Count); // type-change node kept
    }

    [Fact]
    public void CollinearStraightsMergeOnHeal()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(100, 0, 0), new(200, 0, 0)));
        var r3 = Net.Commit(n, Net.Straight(new(100, 0, 0), new(100, 0, 80)));

        n.RemoveEdge(r3.CreatedEdges[0]);

        var edge = Assert.Single(n.Edges.Values);
        Assert.Equal(200f, edge.ArcLength.TotalLength, 0);
        Assert.Equal(2, n.Nodes.Count);
    }

    [Fact]
    public void BulldozeAtFourWayLeavesHealthyThreeWay()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var r2 = Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        // remove the north leg only
        var north = r2.CreatedEdges.Where(id => n.Edges.ContainsKey(id))
            .First(id => n.Edges[id].Curve.Point(0.5f).Z > 0 || n.Edges[id].Curve.Point(0.5f).Z > 0);
        n.RemoveEdge(north);

        Assert.Equal(3, n.Edges.Count);
        var center = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        Assert.Equal(3, center.Edges.Count);
    }
}
