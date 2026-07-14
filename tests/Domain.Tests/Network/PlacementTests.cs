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

    // Pre-task-5, a crossing 1.5 m from an existing node (below GeoConstants.MinEdgeLength
    // but above NodeReuseRadius) was *accepted*: SplitEdgeWithReuse silently folded it into
    // the node at commit time, so no sub-MinEdgeLength edge ever existed. Task 5's crossing
    // guard checks against the road TYPE's minimum segment length (8 m for TwoLane), not
    // the old blanket 4 m floor, and it only exempts crossings within NodeReuseRadius (0.5 m)
    // of the existing edge's end (see RoadNetwork.Validate's "atExistingEnd" check) — a
    // deliberate, node-connection-only exemption, not a MinEdgeLength-wide one. So this
    // 1.5 m crossing — inside the old reuse gap but outside the new node-connection
    // exemption — now falls in newly-forbidden territory and is correctly rejected before
    // it ever reaches the reuse machinery. This test used to assert the reuse *worked*;
    // it now documents that the same geometry is rejected outright. Flagged as a concern
    // in the task 5 report: the scenario's original intent (verify reuse absorbs a
    // near-node crossing without leaving a sliver) is no longer reachable through Validate.
    [Fact]
    public void CrossingWithinOldReuseGapOfExistingNodeIsNowRejected()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        // vertical road crossing the horizontal one 1.5 m from its start node
        var v = n.Validate(Net.Straight(new(1.5f, 0, -50), new(1.5f, 0, 50)));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.TooShort, v.Errors);
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

    [Fact]
    public void SegmentShorterThanTypeMinimumIsRejected()
    {
        var n = Net.New();
        // 10 m FourLane: above the old 4 m floor, below FourLane's 16 m minimum
        var v = n.Validate(Net.Straight(new(0, 0, 0), new(10, 0, 0), RoadCatalog.FourLane.Id));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.TooShort, v.Errors);
    }

    [Fact]
    public void SegmentAtTypeMinimumIsAccepted()
    {
        var n = Net.New();
        var v = n.Validate(Net.Straight(new(0, 0, 0), new(16.5f, 0, 0), RoadCatalog.FourLane.Id));
        Assert.True(v.IsValid, string.Join(",", v.Errors));
    }

    [Fact]
    public void CurveTighterThanTypeMinRadiusIsRejected()
    {
        var n = Net.New();
        // hairpin quadratic: control far off-axis → radius well under TwoLane's 20 m
        var curve = Bezier3.FromQuadratic(new(0, 0, 0), new(15, 0, 40), new(30, 0, 0));
        var v = n.Validate(new PlacementProposal(
            new[] { new ProposedCurve(curve, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.RadiusTooTight, v.Errors);
    }

    [Fact]
    public void GentleCurveIsAccepted()
    {
        var n = Net.New();
        var curve = Bezier3.FromQuadratic(new(0, 0, 0), new(60, 0, 15), new(120, 0, 0));
        var v = n.Validate(new PlacementProposal(
            new[] { new ProposedCurve(curve, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        Assert.True(v.IsValid, string.Join(",", v.Errors));
    }

    [Fact]
    public void NearParallelStubOffExistingNodeIsRejected()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var end = n.Edges[r.CreatedEdges[0]].EndNode;
        // leaves the node at ~11° to the existing edge — a protruding bump
        var v = n.Validate(Net.Straight(new(100, 0, 0), new(60, 0, 8),
            start: new EndpointBinding.AtNode(end)));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.SharpAngle, v.Errors);
    }

    [Fact]
    public void StraightContinuationOffExistingNodeIsAccepted()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var end = n.Edges[r.CreatedEdges[0]].EndNode;
        var v = n.Validate(Net.Straight(new(100, 0, 0), new(200, 0, 0),
            start: new EndpointBinding.AtNode(end)));
        Assert.True(v.IsValid, string.Join(",", v.Errors));
    }

    [Fact]
    public void SharpAngleAlsoCaughtForUnboundEndpointNearNode()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        // free endpoint 0.3 m from the node at (100,0,0): commit would reuse the node
        var v = n.Validate(Net.Straight(new(100.3f, 0, 0), new(60, 0, 8)));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.SharpAngle, v.Errors);
    }

    [Fact]
    public void ZigzagDoubleBackWithinProposalIsRejected()
    {
        var n = Net.New();
        // two segments sharing an endpoint, second doubles back at ~14°
        var v = n.Validate(new PlacementProposal(new[]
        {
            new ProposedCurve(Bezier3.Line(new(0, 0, 0), new(50, 0, 0)), EndpointBinding.None, EndpointBinding.None),
            new ProposedCurve(Bezier3.Line(new(50, 0, 0), new(10, 0, 10)), EndpointBinding.None, EndpointBinding.None),
        }, RoadCatalog.TwoLane.Id));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.Kinked, v.Errors);
    }

    [Fact]
    public void CrossingTooCloseToExistingJunctionIsRejected()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        // crosses the edge 3 m from its end node → 3 m sliver on the existing TwoLane (min 8)
        var v = n.Validate(Net.Straight(new(97, 0, -50), new(97, 0, 50)));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.TooShort, v.Errors);
    }

    [Fact]
    public void TwoCrossingsTooCloseTogetherAreRejected()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(40, 0, -50), new(40, 0, 50)));
        // verticals 7 m apart: far enough not to trip the parallel-overlap check
        // (needs >6.4 m for TwoLane) but still under TwoLane's 8 m segment minimum
        Net.Commit(n, Net.Straight(new(47, 0, -50), new(47, 0, 50)));
        // new road crosses both verticals 7 m apart → 7 m sliver between junctions
        var v = n.Validate(Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.TooShort, v.Errors);
    }

    [Fact]
    public void EndpointOnEdgeTooCloseToItsEndIsRejected()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var edge = r.CreatedEdges[0];
        // T-connection landing 3 m from the edge's end node (t≈0.97, TwoLane min 8)
        var v = n.Validate(Net.Straight(new(97, 0, 50), new(97, 0, 0),
            end: new EndpointBinding.OnEdge(edge, 0.97f)));
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.TooShort, v.Errors);
    }
}
