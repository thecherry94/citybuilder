using System.Linq;
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
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
}
