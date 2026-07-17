using System.Linq;
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

// Roundabout behaviour rides entirely on existing junction control (M2) + arbitration
// (M5): ring nodes are PrioritySigns junctions with the approach forced to Yield. These
// tests assert that yield-on-entry resolves structurally and that the ring is drivable
// and collision-free — no production sim code is expected to change for roundabouts.
public class RoundaboutTrafficTests
{
    private static RoadNetwork FourWayRoundabout(out RoundaboutId id)
    {
        var n = RoundaboutTests.FourWayJunction(out var center);
        id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        return n;
    }

    [Fact]
    public void ApproachConnectorsYieldWhileCirculatingIsFree()
    {
        var n = FourWayRoundabout(out var id);

        // lane -> owning edge
        var laneEdge = new Dictionary<LaneId, EdgeId>();
        foreach (var e in n.Edges.Values)
            foreach (var lane in e.Lanes)
                laneEdge[lane.Id] = e.Id;

        var ringEdges = n.Roundabouts[id].RingEdges.ToHashSet();
        int approachYields = 0, ringConnectors = 0;

        foreach (var node in n.Nodes.Values.Where(x => x.Ring == id))
        foreach (var c in node.Connectors)
        {
            bool fromRing = ringEdges.Contains(laneEdge[c.From]);
            if (fromRing)
            {
                ringConnectors++;
                Assert.Equal(RightOfWay.Free, c.Row); // circulating traffic has priority
            }
            else
            {
                // entering movement must give way
                Assert.True(c.Row is RightOfWay.Yield or RightOfWay.Stop,
                    $"approach connector had Row={c.Row}, expected Yield/Stop");
                approachYields++;
            }
        }

        Assert.True(approachYields > 0, "expected at least one yielding approach connector");
        Assert.True(ringConnectors > 0, "expected circulating connectors");
    }

    [Fact]
    public void RoundaboutIsDrivableAndCollisionFree()
    {
        var n = FourWayRoundabout(out _);
        // ambient traffic circulates + enters for 600 ticks; empty = no penetration,
        // no conflict-point co-occupancy, no exception (drivable).
        Assert.Empty(SimInvariants.CheckBurst(n, seed: 7, ticks: 600, population: 16));
    }
}
