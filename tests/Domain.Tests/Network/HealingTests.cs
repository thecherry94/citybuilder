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
    public void OneWayChainHealsInFlowDirection()
    {
        // A→B→C one-way chain + a two-way stub at B; bulldozing the stub heals A→C
        // and the healed edge must still flow A→C (the M6 final-review bug: HashSet
        // order could rebuild it C→A, silently reversing the road).
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(200, 0, 0), RoadCatalog.OneWay.Id));
        var stubResult = Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(100, 0, 80)));
        n.RemoveEdge(stubResult.CreatedEdges[0]);

        var healed = Assert.Single(n.Edges.Values);
        Assert.Equal(RoadCatalog.OneWay.Id, healed.Type);
        Assert.Equal(new Vector3(0, 0, 0), healed.Curve.P0);
        Assert.Equal(new Vector3(200, 0, 0), healed.Curve.P3);
    }

    [Fact]
    public void OpposingOneWaysNeverHeal()
    {
        // A→B and C→B (head-on at B) + stub at B: after the stub goes, the flows
        // still oppose — the node must survive, no heal.
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        Net.Commit(n, Net.Straight(new Vector3(200, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        var stubResult = Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(100, 0, 80)));
        n.RemoveEdge(stubResult.CreatedEdges[0]);

        Assert.Equal(2, n.Edges.Count);
        Assert.Equal(3, n.Nodes.Count); // shared node kept
    }

    [Fact]
    public void SymmetricHealOrientationFollowsLowerEdgeId()
    {
        // deterministic rule replacing HashSet iteration order: the healed curve
        // starts at the lower-EdgeId edge's far end
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(200, 0, 0)));
        var stubResult = Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(100, 0, 80)));
        var survivors = n.Edges.Values
            .Where(e => !stubResult.CreatedEdges.Contains(e.Id))
            .OrderBy(e => e.Id.Value).ToArray();
        var lowerFar = survivors[0].OtherNode(
            survivors[0].EndNode == survivors[1].StartNode || survivors[0].EndNode == survivors[1].EndNode
                ? survivors[0].EndNode : survivors[0].StartNode);
        var expectedStart = n.Nodes[lowerFar].Position;
        n.RemoveEdge(stubResult.CreatedEdges[0]);

        var healed = Assert.Single(n.Edges.Values);
        Assert.Equal(expectedStart, healed.Curve.P0);
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
