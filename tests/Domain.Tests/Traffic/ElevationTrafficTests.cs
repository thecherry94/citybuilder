using System.Linq;
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

// M8 rides entirely on existing sim machinery: a grade-separated crossing has NO
// junction, so there is nothing to arbitrate — these tests prove that structurally
// and dynamically. No production sim code is expected to change for elevation.
public class ElevationTrafficTests
{
    [Fact]
    public void VehiclesOnABridgeAndTheRoadBelowNeverArbitrate()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-150, 0, 0), new(150, 0, 0)));
        var bridge = new Bezier3(new(0, 6, -150), new(0, 6, -50), new(0, 6, 50), new(0, 6, 150));
        Net.Commit(n, new PlacementProposal(
            new[] { new ProposedCurve(bridge, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));

        var sim = new TrafficSim(n, seed: 5);
        var ground = n.Edges.Values.First(e => e.Curve.Point(0.5f).Y < 1f);
        var deck = n.Edges.Values.First(e => e.Curve.Point(0.5f).Y > 5f);
        var a = sim.Spawn(ground.Id, true, ground.Id);
        var b = sim.Spawn(deck.Id, true, deck.Id);
        Assert.NotNull(a);
        Assert.NotNull(b);
        for (int i = 0; i < 60 * 30; i++)
            sim.Tick(1f / 60f);
        // both completed their trips: no junction existed to arbitrate, nobody waited
        Assert.True(sim.Arrived >= 2, $"arrived={sim.Arrived}");
    }

    [Fact]
    public void GradeSeparatedNetworkBurstIsSafe()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-150, 0, 0), new(150, 0, 0)));
        Net.Commit(n, Net.Straight(new(-150, 0, 60), new(150, 0, 60)));
        var bridge = new Bezier3(new(0, 6, -150), new(0, 6, -50), new(0, 6, 50), new(0, 6, 150));
        Net.Commit(n, new PlacementProposal(
            new[] { new ProposedCurve(bridge, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        Assert.Empty(SimInvariants.CheckBurst(n, seed: 11, ticks: 400, population: 10));
    }
}
