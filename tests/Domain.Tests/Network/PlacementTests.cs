using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class PlacementTests
{
    [Fact]
    public void CrossingRoadsSplitIntoFourEdgesAndFiveNodes()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        Assert.Equal(4, n.Edges.Count);
        Assert.Equal(5, n.Nodes.Count);
        var center = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        Assert.Equal(4, center.Edges.Count);
    }

    [Fact]
    public void TJunctionViaOnEdgeBindingSplits()
    {
        var n = Net.New();
        var r1 = Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var target = r1.CreatedEdges[0];
        Net.Commit(n, new PlacementProposal(new[]
        {
            new ProposedCurve(Bezier3.Line(new(0, 0, 0), new(0, 0, 80)),
                new EndpointBinding.OnEdge(target, 0.5f), EndpointBinding.None)
        }, RoadCatalog.TwoLane.Id));
        Assert.Equal(3, n.Edges.Count);
        Assert.Equal(4, n.Nodes.Count);
        var tee = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        Assert.Equal(3, tee.Edges.Count);
    }

    [Fact]
    public void ArchCrossingStraightTwiceSplitsBothTwice()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-200, 0, 0), new(200, 0, 0)));
        var arch = new Bezier3(new(-100, 0, -50), new(-30, 0, 200), new(30, 0, 200), new(100, 0, -50));
        Net.Commit(n, new PlacementProposal(
            new[] { new ProposedCurve(arch, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        // straight -> 3 pieces, arch -> 3 pieces; nodes: 2 + 2 + 2 crossings
        Assert.Equal(6, n.Edges.Count);
        Assert.Equal(6, n.Nodes.Count);
        Assert.Equal(2, n.Nodes.Values.Count(node => node.Edges.Count == 4));
    }

    [Fact]
    public void CurveStartingAndEndingOnSameEdgeSplitsItTwice()
    {
        var n = Net.New();
        var r1 = Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var target = r1.CreatedEdges[0];
        // half-loop from (−50,0) to (50,0) bulging north, both endpoints bound on the same edge
        var loop = new Bezier3(new(-50, 0, 0), new(-50, 0, 90), new(50, 0, 90), new(50, 0, 0));
        Net.Commit(n, new PlacementProposal(new[]
        {
            new ProposedCurve(loop,
                new EndpointBinding.OnEdge(target, 0.25f),
                new EndpointBinding.OnEdge(target, 0.75f))
        }, RoadCatalog.TwoLane.Id));
        Assert.Equal(4, n.Edges.Count);   // straight in 3 + loop
        Assert.Equal(4, n.Nodes.Count);   // 2 original ends + 2 junctions
        Assert.Equal(2, n.Nodes.Values.Count(node => node.Edges.Count == 3));
    }

    [Fact]
    public void CrossingNearExistingNodeReusesInsteadOfSliver()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        // vertical road crossing the horizontal one 1.5 m from its start node
        Net.Commit(n, Net.Straight(new(1.5f, 0, -50), new(1.5f, 0, 50)));
        // reuse: no edge shorter than MinEdgeLength may exist
        Assert.All(n.Edges.Values, e => Assert.True(e.ArcLength.TotalLength >= GeoConstants.MinEdgeLength - 0.01f,
            $"sliver edge of length {e.ArcLength.TotalLength}"));
        // the start node of the horizontal road was reused as the crossing node
        var corner = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, new Vector3(0, 0, 0)) < 2f);
        Assert.True(corner.Edges.Count >= 3);
    }

    [Fact]
    public void TooShortProposalIsInvalid()
    {
        var n = Net.New();
        var v = n.Validate(Net.Straight(new(0, 0, 0), new(2, 0, 0)));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.TooShort, v.Errors);
    }

    [Fact]
    public void SelfIntersectingProposalIsInvalid()
    {
        var n = Net.New();
        var loop = new Bezier3(new(0, 0, 0), new(40, 0, 20), new(-30, 0, 20), new(10, 0, 0));
        var v = n.Validate(new PlacementProposal(
            new[] { new ProposedCurve(loop, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.SelfIntersecting, v.Errors);
    }

    [Fact]
    public void OverlappingParallelProposalIsInvalid()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var v = n.Validate(Net.Straight(new(10, 0, 1), new(90, 0, 1)));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.Overlapping, v.Errors);
    }

    [Fact]
    public void TangentialCrossingIsRejectedAsTooShallow()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-90, 0, 0), new(90, 0, 0)));
        // U-curve whose apex just grazes the straight: crossing tangents nearly parallel
        var graze = Bezier3.FromQuadratic(new(-60, 0, -60), new(0, 0, 61f), new(60, 0, -60));
        var v = n.Validate(new PlacementProposal(
            new[] { new ProposedCurve(graze, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.CrossingTooShallow, v.Errors);
    }

    [Fact]
    public void SteepCurvedCrossingRemainsValid()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-90, 0, 0), new(90, 0, 0)));
        // curve dips well below the road: two crossings at healthy angles
        var dip = Bezier3.FromQuadratic(new(-60, 0, -60), new(0, 0, 80), new(60, 0, -60));
        var v = n.Validate(new PlacementProposal(
            new[] { new ProposedCurve(dip, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        Assert.True(v.IsValid, string.Join(",", v.Errors));
        Assert.Equal(2, v.CrossingPoints.Count);
    }

    [Fact]
    public void StaleCommitIsRejectedWhenNoLongerValid()
    {
        var n = Net.New();
        // validate a road while network is empty
        var v = n.Validate(Net.Straight(new(10, 0, 1), new(90, 0, 1)));
        Assert.True(v.IsValid);
        // network changes: same corridor now occupied
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var r = n.Commit(v);
        Assert.False(r.Success);
    }

    [Fact]
    public void ValidationReportsCrossingPointsForPreview()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var v = n.Validate(Net.Straight(new(0, 0, -50), new(0, 0, 50)));
        Assert.True(v.IsValid);
        var p = Assert.Single(v.CrossingPoints);
        Assert.True(Vector3.Distance(p, Vector3.Zero) < 0.1f);
    }
}
