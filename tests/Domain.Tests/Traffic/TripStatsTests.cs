using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class TripStatsTests
{
    private static EdgeId EdgeAt(RoadNetwork n, Vector3 mid)
        => n.Edges.Values.Single(e => Vector3.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;

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

    [Fact]
    public void StopControlledApproachRecordsAtLeastOneStop()
    {
        // Street (E-W, main) crossed by TwoLane (N-S) with a Stop role forced on the
        // north approach: the arbiter demands a full stop (< 0.1 m/s) at the line, so
        // the vehicle — having first exceeded 2 m/s on approach — must dip below the
        // 0.5 m/s threshold exactly the Stops counter watches. Deterministic: single
        // vehicle, fixed seed, no rival traffic.
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150), RoadCatalog.TwoLane.Id));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        var nEdge = EdgeAt(n, new Vector3(0, 0, -75));
        var sEdge = EdgeAt(n, new Vector3(0, 0, 75));
        n.ConfigureJunction(node.Id, node.Config with
        {
            Mode = JunctionControlMode.PrioritySigns,
            RoleOverrides = new Dictionary<EdgeId, LegRole> { [nEdge] = LegRole.Stop },
        });

        var sim = new TrafficSim(n, seed: 2) { TripLog = new() };
        Assert.NotNull(sim.Spawn(nEdge, forward: true, sEdge));
        for (int i = 0; i < 60 * 120 && sim.TripLog.Count == 0; i++) sim.Tick(1f / 60f);

        var trip = Assert.Single(sim.TripLog);
        Assert.True(trip.Stops >= 1, $"expected at least one stop, recorded {trip.Stops}");
        // the mandatory standstill makes actual time strictly exceed free-flow time
        Assert.True(trip.ArrivalTime - trip.SpawnTime > trip.FreeFlowTime,
            $"delay must be positive: actual {trip.ArrivalTime - trip.SpawnTime:F1}s vs free-flow {trip.FreeFlowTime:F1}s");
    }
}
