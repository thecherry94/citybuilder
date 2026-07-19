using System.Linq;
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class ElevationNetworkTests
{
    private static Bezier3 Ramp(Vector3 a, Vector3 b) => ElevationValidationTests.Ramp(a, b);

    private static PlacementProposal One(Bezier3 c, RoadTypeId? t = null)
        => ElevationValidationTests.One(c, t);

    [Fact]
    public void HealingASplitRampKeepsItsProfile()
    {
        var n = Net.New();
        Net.Commit(n, One(Ramp(new(0, 0, 0), new(60, 4, 0))));
        Net.Commit(n, One(Ramp(new(60, 4, 0), new(120, 8, 0))));
        Assert.Equal(2, n.Edges.Count);
        // force a heal: add and remove a third arm at the shared mid node
        var arm = Net.Commit(n, One(Ramp(new(60, 4, 0), new(60, 4, 80))));
        n.RemoveEdge(arm.CreatedEdges[0]);
        Assert.Single(n.Edges); // healed into one ramp
        var healed = n.Edges.Values.Single();
        float topY = MathF.Max(healed.Curve.Point(0).Y, healed.Curve.Point(1).Y);
        Assert.Equal(8f, topY, 1);
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void ElevatedRoundaboutConverts()
    {
        var n = Net.New();
        Net.Commit(n, One(Ramp(new(-60, 10, 0), new(60, 10, 0)), RoadCatalog.Street.Id));
        Net.Commit(n, One(Ramp(new(0, 10, -60), new(0, 10, 60)), RoadCatalog.Street.Id));
        var center = n.Nodes.Values.Single(x =>
            Vector3.Distance(x.Position, new(0, 10, 0)) < 0.1f);
        var res = n.ConvertToRoundabout(center.Id, 20f);
        Assert.True(res.Success, $"convert failed: {res.Error}");
        Assert.Empty(NetworkInvariants.Check(n));
        // ring nodes sit on the +10 m plane
        Assert.All(n.Nodes.Values.Where(x => x.Ring != null),
            x => Assert.Equal(10f, x.Position.Y, 1));
    }

    [Fact]
    public void ConvertingAJunctionWithGentleRampLegsStaysWithinGradients()
    {
        // Street legs at 4%: the trimmed approaches must descend to the ring plane
        // without exceeding the type gradient, and end exactly ON the plane
        var n = Net.New();
        Net.Commit(n, One(Ramp(new(-60, 2.4f, 0), new(0, 0, 0)), RoadCatalog.Street.Id));
        Net.Commit(n, One(Ramp(new(60, 2.4f, 0), new(0, 0, 0)), RoadCatalog.Street.Id));
        Net.Commit(n, One(Ramp(new(0, 2.4f, -60), new(0, 0, 0)), RoadCatalog.Street.Id));
        var center = n.Nodes.Values.Single(x => Vector3.Distance(x.Position, Vector3.Zero) < 0.1f);
        var res = n.ConvertToRoundabout(center.Id, 15f);
        Assert.True(res.Success, $"convert failed: {res.Error}");
        Assert.Empty(NetworkInvariants.Check(n)); // includes the gradient rule
    }

    [Fact]
    public void ConvertingAJunctionWithSteepRampLegsIsRefusedNotCorrupted()
    {
        // TwoLane legs at their full 8%: descending from the cut height to the ring
        // plane over the trimmed remainder needs >8% — refuse, never commit corrupt
        var n = Net.New();
        Net.Commit(n, One(Ramp(new(-60, 4.8f, 0), new(0, 0, 0))));
        Net.Commit(n, One(Ramp(new(60, 4.8f, 0), new(0, 0, 0))));
        Net.Commit(n, One(Ramp(new(0, 4.8f, -60), new(0, 0, 0))));
        var center = n.Nodes.Values.Single(x => Vector3.Distance(x.Position, Vector3.Zero) < 0.1f);
        var res = n.ConvertToRoundabout(center.Id, 20f);
        Assert.False(res.Success);
        Assert.Equal(RoundaboutError.LegTooSteep, res.Error);
        Assert.Empty(NetworkInvariants.Check(n)); // and nothing was mutated
        Assert.True(n.Nodes.ContainsKey(center.Id));
    }

    [Fact]
    public void GroundRoundaboutIsNotObstructedByABridgeAbove()
    {
        var n = RoundaboutTests.FourWayJunction(out var center);
        // +8 m bridge crossing straight over the future ring area
        Net.Commit(n, One(Ramp(new(-80, 8, 10), new(80, 8, 10))));
        var res = n.ConvertToRoundabout(center, 20f);
        Assert.True(res.Success, $"convert failed: {res.Error}");
        Assert.Empty(NetworkInvariants.Check(n));
    }
}
