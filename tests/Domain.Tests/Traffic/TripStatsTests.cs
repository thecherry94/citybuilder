using System.Linq;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class TripStatsTests
{
    [Fact]
    public void TripLogRecordsDelayAndStops()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(400, 0, 0)));
        var sim = new TrafficSim(n, seed: 2) { TripLog = new() };
        var edges = n.Edges.Keys.OrderBy(e => e.Value).ToArray();
        sim.Spawn(edges[0], forward: true, edges[^1]);
        for (int i = 0; i < 60 * 60 && sim.TripLog.Count == 0; i++) sim.Tick(1f / 60f);
        var trip = Assert.Single(sim.TripLog);
        Assert.True(trip.ArrivalTime > trip.SpawnTime);
        Assert.True(trip.FreeFlowTime > 5f && trip.FreeFlowTime < trip.ArrivalTime - trip.SpawnTime + 5f);
        Assert.Equal(0, trip.Stops); // empty road, no stops
    }
}
