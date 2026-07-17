using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class RetypeTests
{
    private static (RoadNetwork n, EdgeId edge) Cross()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-80, 0, 0), new Vector3(80, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -80), new Vector3(0, 0, 80)));
        var e = n.Edges.Values.First(x =>
            Vector3.Distance(x.Curve.Point(0.5f), new Vector3(40, 0, 0)) < 5f).Id;
        return (n, e);
    }

    [Fact]
    public void RetypeSwapsTypeAndLanesKeepsEdgeId()
    {
        var (n, e) = Cross();
        var result = n.RetypeEdge(e, RoadCatalog.Street.Id);
        Assert.Null(result);
        var edge = n.Edges[e]; // same id still resolves
        Assert.Equal(RoadCatalog.Street.Id, edge.Type);
        Assert.Equal(RoadCatalog.Street.Lanes.Count, edge.Lanes.Count);
    }

    [Fact]
    public void RetypePreservesJunctionConfig()
    {
        var (n, e) = Cross();
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        n.ConfigureJunction(node.Id, node.Config with
        {
            Mode = JunctionControlMode.AllWayStop,
            LegOffsets = new Dictionary<EdgeId, float> { [e] = 4f },
        });
        Assert.Null(n.RetypeEdge(e, RoadCatalog.Street.Id));
        var after = n.Nodes[node.Id];
        Assert.Equal(JunctionControlMode.AllWayStop, after.Config.Mode);
        Assert.True(after.Config.LegOffsets.ContainsKey(e),
            "EdgeId-keyed leg offset lost — retype must preserve the id");
    }

    [Fact]
    public void RetypeRejectsTooTightCurve()
    {
        // Street (MinRadius 10) bend that FourLane (MinRadius 35) cannot hold
        var n = Net.New();
        var bend = Bezier3.FromQuadratic(new(0, 0, 0), new(20, 0, 18), new(40, 0, 0));
        Net.Commit(n, new PlacementProposal(
            new[] { new ProposedCurve(bend, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.Street.Id));
        var e = n.Edges.Keys.Single();
        Assert.Equal(RetypeError.TooTight, n.RetypeEdge(e, RoadCatalog.FourLane.Id));
        Assert.Equal(RoadCatalog.Street.Id, n.Edges[e].Type); // unchanged on failure
    }

    [Fact]
    public void RetypeRejectsTooShortEdge()
    {
        // 10 m TwoLane edge; Avenue needs MinSegmentLength 21
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(10, 0, 0)));
        var e = n.Edges.Keys.Single();
        Assert.Equal(RetypeError.TooShort, n.RetypeEdge(e, RoadCatalog.Avenue.Id));
    }

    [Fact]
    public void RetypeRejectsSameTypeAndUnknownEdge()
    {
        var (n, e) = Cross();
        Assert.Equal(RetypeError.SameType, n.RetypeEdge(e, RoadCatalog.TwoLane.Id));
        Assert.Equal(RetypeError.UnknownEdge, n.RetypeEdge(new EdgeId(9999), RoadCatalog.Street.Id));
    }

    [Fact]
    public void RetypeRaisesEdgesChangedDeltaAndBumpsVersion()
    {
        var (n, e) = Cross();
        int v = n.Version;
        NetworkDelta? seen = null;
        n.Changed += d => seen = d;
        Assert.Null(n.RetypeEdge(e, RoadCatalog.Street.Id));
        Assert.Equal(v + 1, n.Version);
        Assert.NotNull(seen);
        Assert.Contains(e, seen!.EdgesChanged);
        Assert.Empty(seen.EdgesAdded);
        Assert.Empty(seen.EdgesRemoved);
    }

    [Fact]
    public void RetypeRegeneratesLaneIds()
    {
        var (n, e) = Cross();
        var oldLanes = n.Edges[e].Lanes.Select(l => l.Id).ToHashSet();
        Assert.Null(n.RetypeEdge(e, RoadCatalog.Street.Id));
        Assert.DoesNotContain(n.Edges[e].Lanes, l => oldLanes.Contains(l.Id));
    }

    [Fact]
    public void FlipReversesCurveAndSwapsNodes()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        var e = n.Edges.Keys.Single();
        var before = n.Edges[e];
        var (s, t) = (before.StartNode, before.EndNode);
        Assert.True(n.FlipEdge(e));
        var after = n.Edges[e];
        Assert.Equal(t, after.StartNode);
        Assert.Equal(s, after.EndNode);
        Assert.Equal(new Vector3(100, 0, 0), after.Curve.P0);
        Assert.Equal(new Vector3(0, 0, 0), after.Curve.P3);
        Assert.Equal(RoadCatalog.OneWay.Id, after.Type);
        Assert.False(n.FlipEdge(new EdgeId(9999)));
    }

    [Fact]
    public void DoubleFlipRestoresTravelDirection()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        var e = n.Edges.Keys.Single();
        var start0 = n.Edges[e].StartNode;
        n.FlipEdge(e);
        n.FlipEdge(e);
        Assert.Equal(start0, n.Edges[e].StartNode);
        // travel direction = P0→P3 for Forward lanes; all OneWay lanes are Forward
        Assert.Equal(new Vector3(0, 0, 0), n.Edges[e].Curve.P0);
    }
}
