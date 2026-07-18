using System.Linq;
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class RoundaboutTests
{
    internal static RoadNetwork FourWayJunction(out NodeId center)
    {
        var n = new RoadNetwork();
        Net.Commit(n, Net.Straight(new(-60, 0, 0), new(60, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -60), new(0, 0, 60)));
        center = n.Nodes.Values.Single(x => Vector3.Distance(x.Position, Vector3.Zero) < 0.1f).Id;
        return n;
    }

    internal static bool IsApproach(RoadNetwork n, RoadEdge e)
        => n.Nodes[e.StartNode].Ring == null ^ n.Nodes[e.EndNode].Ring == null;

    [Fact]
    public void ConvertFourWayBuildsValidRing()
    {
        var n = FourWayJunction(out var center);
        var res = n.ConvertToRoundabout(center, 20f);
        Assert.True(res.Success);
        Assert.False(n.Nodes.ContainsKey(center)); // center replaced
        Assert.Single(n.Roundabouts);
        Assert.Empty(NetworkInvariants.Check(n));

        var slotNodes = n.Nodes.Values.Where(x => x.Ring != null && x.Edges.Count == 3).ToList();
        Assert.Equal(4, slotNodes.Count);
        Assert.All(slotNodes, x => Assert.Single(x.Edges, e => IsApproach(n, n.Edges[e])));
    }

    [Fact]
    public void ConvertRefusesDegreeTwo()
    {
        var n = new RoadNetwork();
        Net.Commit(n, Net.Straight(new(-60, 0, 0), new(60, 0, 0)));
        var end = n.Nodes.Values.First(x => x.Edges.Count == 1).Id;
        Assert.Equal(RoundaboutError.NotAJunction, n.ConvertToRoundabout(end, 20f).Error);
    }

    [Fact]
    public void ConvertPreservesLegEdgeIds()
    {
        var n = FourWayJunction(out var center);
        var legIdsBefore = n.Nodes[center].Edges.ToHashSet();
        n.ConvertToRoundabout(center, 20f);
        var survivingLegs = n.Edges.Values.Count(e => legIdsBefore.Contains(e.Id));
        Assert.Equal(4, survivingLegs);
        // and they are now approaches (one end ring, one end not)
        Assert.All(legIdsBefore, id => Assert.True(IsApproach(n, n.Edges[id])));
    }

    [Fact]
    public void RingEdgesAreOneWay()
    {
        var n = FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        foreach (var re in n.Roundabouts[id].RingEdges)
            Assert.Equal(RoadCatalog.OneWay.Id, n.Edges[re].Type);
    }

    [Fact]
    public void ConvertRefusesRadiusTooTight()
    {
        var n = FourWayJunction(out var center);
        Assert.Equal(RoundaboutError.RadiusTooTight, n.ConvertToRoundabout(center, 1f).Error);
        // no mutation on failure
        Assert.Empty(n.Roundabouts);
        Assert.True(n.Nodes.ContainsKey(center));
    }

    [Fact]
    public void RadiusChangeReArcsRing()
    {
        var n = FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        Assert.True(n.SetRoundaboutRadius(id, 30f).Success);
        Assert.Equal(30f, n.Roundabouts[id].Radius);
        Assert.Empty(NetworkInvariants.Check(n));
        foreach (var rn in n.Nodes.Values.Where(x => x.Ring == id))
            Assert.Equal(30f, Vector3.Distance(rn.Position, n.Roundabouts[id].Center), 1);
    }

    [Fact]
    public void RadiusChangeIsLosslessAcrossRepeats()
    {
        var n = FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        n.SetRoundaboutRadius(id, 35f);
        n.SetRoundaboutRadius(id, 20f); // back to start
        Assert.Empty(NetworkInvariants.Check(n));
        // four approaches still present, ring still valid
        Assert.Equal(4, n.Edges.Values.Count(e => IsApproach(n, e)));
    }

    [Fact]
    public void RemoveRoundaboutLeavesCleanStubs()
    {
        var n = FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        n.RemoveRoundabout(id);
        Assert.Empty(n.Roundabouts);
        Assert.DoesNotContain(n.Nodes.Values, x => x.Ring != null);
        Assert.Empty(NetworkInvariants.Check(n));
        Assert.Equal(4, n.Edges.Count); // four approach stubs remain
    }

    [Fact]
    public void RadiusTooTightDoesNotMutate()
    {
        var n = FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        var edgesBefore = n.Edges.Count;
        Assert.Equal(RoundaboutError.RadiusTooTight, n.SetRoundaboutRadius(id, 0.5f).Error);
        Assert.Equal(20f, n.Roundabouts[id].Radius);
        Assert.Equal(edgesBefore, n.Edges.Count);
    }

    [Fact]
    public void SetRadiusOnUnknownRoundaboutFails()
    {
        var n = FourWayJunction(out var center);
        n.ConvertToRoundabout(center, 20f);
        Assert.Equal(RoundaboutError.UnknownRoundabout, n.SetRoundaboutRadius(new RoundaboutId(999), 25f).Error);
    }

    [Fact]
    public void BulldozingAnApproachReArcsRing()
    {
        var n = FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        var leg = n.Edges.Values.First(e => IsApproach(n, e));
        n.RemoveEdge(leg.Id);
        Assert.Empty(NetworkInvariants.Check(n));
        // 4 → 3 approaches; still a live roundabout
        Assert.True(n.Roundabouts.ContainsKey(id));
        Assert.Equal(3, n.Edges.Values.Count(e => IsApproach(n, e)));
    }

    [Fact]
    public void CurvedLegConversionKeepsApproachCurvesOnTheirNodes()
    {
        // A curved leg's tangent bearing at the center differs from where its curve
        // actually crosses the ring circle. Slots must sit at the actual crossing —
        // otherwise the trimmed approach is bound to a node its curve doesn't touch
        // (fuzz: 1.8-3.9 m endpoint drift, ring arcs crossing the dangling curve).
        var n = new RoadNetwork();
        Net.Commit(n, Net.Straight(new(-60, 0, 0), new(60, 0, 0), RoadCatalog.Street.Id));
        var curved = new CityBuilder.Domain.Geometry.Bezier3(
            new(40, 0, 80), new(10, 0, 45), new(0, 0, 20), Vector3.Zero);
        Net.Commit(n, new PlacementProposal(new[]
        {
            new ProposedCurve(curved, EndpointBinding.None, EndpointBinding.None)
        }, RoadCatalog.Street.Id));
        var center = n.Nodes.Values.Single(x => Vector3.Distance(x.Position, Vector3.Zero) < 0.1f);
        Assert.Equal(3, center.Edges.Count);

        var res = n.ConvertToRoundabout(center.Id, 15f);
        Assert.True(res.Success, $"convert failed: {res.Error}");
        foreach (var e in n.Edges.Values.Where(e => IsApproach(n, e)))
        {
            Assert.True(Vector3.Distance(e.Curve.P0, n.Nodes[e.StartNode].Position) < 0.1f,
                $"approach {e.Id.Value} start drifts {Vector3.Distance(e.Curve.P0, n.Nodes[e.StartNode].Position):F2} m off its node");
            Assert.True(Vector3.Distance(e.Curve.P3, n.Nodes[e.EndNode].Position) < 0.1f,
                $"approach {e.Id.Value} end drifts {Vector3.Distance(e.Curve.P3, n.Nodes[e.EndNode].Position):F2} m off its node");
        }
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void ConversionRefusedWhenRingWouldCrossABystanderRoad()
    {
        var n = FourWayJunction(out var center);
        // diagonal road threading between the +X and +Z legs, crossing the planned
        // r=20 circle at ~45° without touching any leg — the ring arcs would cross it
        Net.Commit(n, Net.Straight(new(5, 0, 5), new(40, 0, 40)));
        var res = n.ConvertToRoundabout(center, 20f);
        Assert.Equal(RoundaboutError.Obstructed, res.Error);
        // no mutation on refusal, and the network is still healthy
        Assert.Empty(n.Roundabouts);
        Assert.True(n.Nodes.ContainsKey(center));
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void ConversionSucceedsOnceTheObstructionIsGone()
    {
        var n = FourWayJunction(out var center);
        var r = Net.Commit(n, Net.Straight(new(5, 0, 5), new(40, 0, 40)));
        n.RemoveEdge(r.CreatedEdges[0]);
        var res = n.ConvertToRoundabout(center, 20f);
        Assert.True(res.Success, $"expected success, got {res.Error}");
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void RingEdgesAreImmutableToRetypeAndFlip()
    {
        var n = FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        var ringEdge = n.Roundabouts[id].RingEdges[0];
        Assert.Equal(RetypeError.Locked, n.RetypeEdge(ringEdge, RoadCatalog.Street.Id));
        Assert.False(n.FlipEdge(ringEdge));
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void ConfiguringARingNodeIsIgnored()
    {
        var n = FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        var ringNode = n.Nodes.Values.First(x => x.Ring == id && x.Edges.Count == 3);
        n.ConfigureJunction(ringNode.Id, ringNode.Config with { Mode = JunctionControlMode.AllWayStop });
        Assert.Empty(NetworkInvariants.Check(n)); // yield-on-entry preserved
    }

    [Fact]
    public void DrawingOntoARingNodeIsRefused()
    {
        var n = FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        var ringNode = n.Nodes.Values.First(x => x.Ring == id && x.Edges.Count == 3);
        var prop = new PlacementProposal(new[]
        {
            new ProposedCurve(
                CityBuilder.Domain.Geometry.Bezier3.Line(ringNode.Position + new Vector3(20, 0, 60), ringNode.Position),
                EndpointBinding.None, new EndpointBinding.AtNode(ringNode.Id))
        }, RoadCatalog.TwoLane.Id);
        var v = n.Validate(prop);
        Assert.False(v.IsValid);
        Assert.Contains(PlacementError.TouchesRoundabout, v.Errors);
    }

    [Fact]
    public void BulldozingDownToTwoApproachesDissolves()
    {
        var n = FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        var legs = n.Edges.Values.Where(e => IsApproach(n, e)).Select(e => e.Id).ToList();
        n.RemoveEdge(legs[0]);
        n.RemoveEdge(legs[1]); // now 2 approaches
        Assert.False(n.Roundabouts.ContainsKey(id));
        Assert.DoesNotContain(n.Nodes.Values, x => x.Ring != null);
        Assert.Empty(NetworkInvariants.Check(n));
    }
}
