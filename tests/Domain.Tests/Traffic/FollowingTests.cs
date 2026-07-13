using System.Numerics;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class FollowingTests
{
    private const float Dt = 1f / 30f;

    private static (RoadNetwork n, EdgeId west, EdgeId east) TwoSegmentRoad()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(150, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(150, 0, 0), new Vector3(300, 0, 0)));
        var west = n.Edges.Values.Single(e => e.Curve.Point(0.5f).X < 150).Id;
        var east = n.Edges.Values.Single(e => e.Curve.Point(0.5f).X > 150).Id;
        return (n, west, east);
    }

    [Fact]
    public void SingleVehicleTraversesAndArrives()
    {
        var (n, west, east) = TwoSegmentRoad();
        var sim = new TrafficSim(n);
        var v = sim.Spawn(west, forward: true, east);
        Assert.NotNull(v);

        for (int i = 0; i < 30 * 60 && sim.Arrived == 0; i++)
            sim.Tick(Dt);

        Assert.Equal(1, sim.Arrived);
        Assert.Empty(sim.Vehicles);
    }

    [Fact]
    public void PlatoonNeverCollides()
    {
        var (n, west, east) = TwoSegmentRoad();
        var sim = new TrafficSim(n);
        for (int i = 0; i < 5; i++)
        {
            Vehicle? v = null;
            // tick until entry is clear for the next spawn
            for (int t = 0; t < 300 && v is null; t++)
            {
                v = sim.Spawn(west, true, east);
                if (v is null)
                    sim.Tick(Dt);
            }
            Assert.NotNull(v);
        }

        for (int i = 0; i < 2000; i++)
        {
            sim.Tick(Dt);
            var byLane = sim.Vehicles.Where(v => v.Lane is not null)
                .GroupBy(v => v.Lane!.Value);
            foreach (var group in byLane)
            {
                var ordered = group.OrderByDescending(v => v.S).ToArray();
                for (int k = 1; k < ordered.Length; k++)
                {
                    float gap = ordered[k - 1].S - Vehicle.Length - ordered[k].S;
                    Assert.True(gap > 0.2f,
                        $"tick {i}: gap {gap:F2} between {ordered[k - 1].Id} and {ordered[k].Id}");
                }
            }
        }
    }

    [Fact]
    public void QueuesBehindStoppedLeader()
    {
        var (n, west, east) = TwoSegmentRoad();
        var sim = new TrafficSim(n);
        var leader = sim.Spawn(west, true, east)!;
        for (int t = 0; t < 600 && sim.Spawn(west, true, east) is null; t++)
            sim.Tick(Dt);

        // freeze the leader by force each tick (roadworks stand-in)
        for (int i = 0; i < 1500; i++)
        {
            leader.Speed = 0;
            leader.S = MathF.Min(leader.S, 60f);
            sim.Tick(Dt);
        }

        var follower = sim.Vehicles.Single(v => v.Id != leader.Id);
        Assert.True(follower.Speed < 0.5f, $"follower speed {follower.Speed:F2}");
        float gap = leader.S - Vehicle.Length - follower.S;
        Assert.InRange(gap, 0.5f, 2f * Idm.S0 + 1f);
    }

    [Fact]
    public void CrossesJunctionViaConnector()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-100, 0, 0), new Vector3(100, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -100), new Vector3(0, 0, 100)));
        var west = n.Edges.Values.Single(e =>
            Vector3.Distance(e.Curve.Point(0.5f), new Vector3(-50, 0, 0)) < 3).Id;
        var east = n.Edges.Values.Single(e =>
            Vector3.Distance(e.Curve.Point(0.5f), new Vector3(50, 0, 0)) < 3).Id;

        var sim = new TrafficSim(n);
        var v = sim.Spawn(west, true, east)!;
        bool crossed = false;
        for (int i = 0; i < 30 * 90 && sim.Arrived == 0; i++)
        {
            sim.Tick(Dt);
            crossed |= v.Crossing is not null;
        }
        Assert.True(crossed, "vehicle never entered a connector");
        Assert.Equal(1, sim.Arrived);
    }
}
