using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class LaneChangeTests
{
    private const float Dt = 1f / 30f;

    private static EdgeId EdgeAt(RoadNetwork n, Vector3 mid)
        => n.Edges.Values.Single(e => Vector3.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;

    [Fact]
    public void OvertakesSlowLeader()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(400, 0, 0), RoadCatalog.FourLane.Id));
        Net.Commit(n, Net.Straight(new Vector3(400, 0, 0), new Vector3(500, 0, 0), RoadCatalog.FourLane.Id));
        var main = EdgeAt(n, new Vector3(200, 0, 0));
        var exit = EdgeAt(n, new Vector3(450, 0, 0));

        var sim = new TrafficSim(n);
        var slow = sim.Spawn(main, true, exit)!;
        for (int i = 0; i < 90; i++)
        {
            slow.Speed = MathF.Min(slow.Speed, 3f);
            sim.Tick(Dt);
        }
        var fast = sim.Spawn(main, true, exit)!;
        // pin both to the same lane initially so the overtake is forced
        sim.ForceLane(fast, slow.Lane!.Value);

        bool changed = false, passed = false;
        for (int i = 0; i < 30 * 90 && !passed; i++)
        {
            slow.Speed = MathF.Min(slow.Speed, 3f);
            slow.S = MathF.Min(slow.S, 250f); // keep the slow car on the edge
            sim.Tick(Dt);
            changed |= fast.ChangeFrom is not null;
            if (fast.Lane is not null && slow.Lane is not null && fast.S > slow.S + Vehicle.Length)
                passed = true;
        }
        Assert.True(changed, "fast vehicle never started a lane change");
        Assert.True(passed, "fast vehicle never passed the slow leader");
    }

    [Fact]
    public void MergesIntoTurnLaneBeforeJunction()
    {
        // FourLane cross: left turns only from the leftmost lane. Force the vehicle
        // into the rightmost lane and demand a left turn — it must merge in time.
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-200, 0, 0), new Vector3(200, 0, 0), RoadCatalog.FourLane.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -200), new Vector3(0, 0, 200), RoadCatalog.FourLane.Id));

        var west = EdgeAt(n, new Vector3(-100, 0, 0));
        var north = EdgeAt(n, new Vector3(0, 0, -100));
        var sim = new TrafficSim(n);
        var v = sim.Spawn(west, true, north)!; // W→N is a left turn

        // move it to the rightmost lane (offset +5.25 for forward travel)
        var rightLane = n.Edges[west].Lanes
            .Single(l => l.Kind == LaneKind.Driving
                && l.Direction == LaneDirection.Forward && MathF.Abs(l.Offset) > 3f);
        sim.ForceLane(v, rightLane.Id);
        Assert.Null(v.PlannedConnector); // right lane cannot turn left

        bool merged = false;
        for (int i = 0; i < 30 * 120 && sim.Arrived == 0; i++)
        {
            sim.Tick(Dt);
            if (v.Lane is { } lane && lane == n.Edges[west].Lanes
                    .Single(l => l.Direction == LaneDirection.Forward
                        && l.Kind == LaneKind.Driving && MathF.Abs(l.Offset) < 3f).Id)
                merged = true;
        }
        Assert.True(merged, "vehicle never merged into the left-turn lane");
        Assert.Equal(1, sim.Arrived);
    }

    [Fact]
    public void DenseTwoLaneTrafficStaysCollisionFree()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(500, 0, 0), RoadCatalog.FourLane.Id));
        Net.Commit(n, Net.Straight(new Vector3(500, 0, 0), new Vector3(600, 0, 0), RoadCatalog.FourLane.Id));
        var main = EdgeAt(n, new Vector3(250, 0, 0));
        var exit = EdgeAt(n, new Vector3(550, 0, 0));

        var sim = new TrafficSim(n);
        var rng = new Random(42);
        var speedCap = new Dictionary<int, float>();

        for (int i = 0; i < 4800; i++)
        {
            if (i % 45 == 0 && i < 3000)
            {
                var v = sim.Spawn(main, true, exit);
                if (v is not null)
                    speedCap[v.Id] = 4f + (float)rng.NextDouble() * 20f; // mixed speeds
            }
            foreach (var v in sim.Vehicles)
                if (speedCap.TryGetValue(v.Id, out var cap))
                    v.Speed = MathF.Min(v.Speed, cap);
            sim.Tick(Dt);

            // collision invariant per lane, including both lanes of changing vehicles
            foreach (var laneGroup in sim.Vehicles
                .Where(v => v.Lane is not null)
                .SelectMany(v => v.ChangeFrom is { } f
                    ? new[] { (lane: v.Lane!.Value, v), (lane: f, v) }
                    : new[] { (lane: v.Lane!.Value, v) })
                .GroupBy(x => x.lane))
            {
                var ordered = laneGroup.Select(x => x.v).OrderByDescending(x => x.S).ToArray();
                for (int k = 1; k < ordered.Length; k++)
                {
                    float gap = ordered[k - 1].S - Vehicle.Length - ordered[k].S;
                    Assert.True(gap > 0.05f,
                        $"tick {i}: lane {laneGroup.Key.Value} gap {gap:F2} " +
                        $"between {ordered[k - 1].Id} and {ordered[k].Id}");
                }
            }
        }
        Assert.True(sim.Arrived > 10, $"only {sim.Arrived} arrived");
    }
}
