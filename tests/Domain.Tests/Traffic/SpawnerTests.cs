using System.Diagnostics;
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class SpawnerTests
{
    private const float Dt = 1f / 30f;

    /// <summary>3×3 street grid, 200 m spacing, with fringe stubs.</summary>
    private static RoadNetwork Grid()
    {
        var n = Net.New();
        for (int i = 0; i < 3; i++)
        {
            Net.Commit(n, Net.Straight(new Vector3(-100, 0, i * 200), new Vector3(500, 0, i * 200), RoadCatalog.Street.Id));
            Net.Commit(n, Net.Straight(new Vector3(i * 200, 0, -100), new Vector3(i * 200, 0, 500), RoadCatalog.Street.Id));
        }
        return n;
    }

    [Fact]
    public void SameSeedIsDeterministic()
    {
        var a = new TrafficSim(Grid(), seed: 7) { TargetPopulation = 40 };
        var b = new TrafficSim(Grid(), seed: 7) { TargetPopulation = 40 };
        for (int i = 0; i < 500; i++)
        {
            a.Tick(Dt);
            b.Tick(Dt);
        }
        Assert.Equal(a.Vehicles.Count, b.Vehicles.Count);
        foreach (var (va, vb) in a.Vehicles.Zip(b.Vehicles))
        {
            Assert.Equal(va.Id, vb.Id);
            Assert.Equal(va.S, vb.S, 3);
            Assert.Equal(va.Lane, vb.Lane);
        }
    }

    [Fact]
    public void PopulationConvergesToTarget()
    {
        var sim = new TrafficSim(Grid(), seed: 3) { TargetPopulation = 30 };
        for (int i = 0; i < 3000; i++)
            sim.Tick(Dt);
        Assert.InRange(sim.Vehicles.Count, 20, 30);
    }

    [Fact]
    public void BulldozeInvalidatesRoutesGracefully()
    {
        var n = Grid();
        var sim = new TrafficSim(n, seed: 5) { TargetPopulation = 40 };
        for (int i = 0; i < 1200; i++)
            sim.Tick(Dt);
        Assert.True(sim.Vehicles.Count > 10);

        // remove a central edge mid-flow
        var victim = n.Edges.Values.First(e =>
            Vector3.Distance(e.Curve.Point(0.5f), new Vector3(100, 0, 200)) < 60f);
        n.RemoveEdge(victim.Id);

        for (int i = 0; i < 600; i++)
        {
            sim.Tick(Dt);
            foreach (var v in sim.Vehicles)
            {
                for (int s = v.StepIndex; s < v.Route.Steps.Count; s++)
                    Assert.True(n.Edges.ContainsKey(v.Route.Steps[s].Edge),
                        $"vehicle {v.Id} routed through a removed edge");
            }
        }
    }

    [Fact]
    public void AmbientSpawnerCopesWithOneWayFringes()
    {
        // a one-way stub feeding a two-way loop: ambient spawning must reach the target
        // even when the RNG picks the impossible direction first (direction retry)
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(150, 0, 0), RoadCatalog.OneWay.Id));
        Net.Commit(n, Net.Straight(new(150, 0, 0), new(300, 0, 0)));
        Net.Commit(n, Net.Straight(new(300, 0, 0), new(300, 0, 150)));
        Net.Commit(n, Net.Straight(new(300, 0, 150), new(150, 0, 150)));
        Net.Commit(n, Net.Straight(new(150, 0, 150), new(150, 0, 0)));
        // short window: with this seed the RNG happens to pick the impossible
        // (backward) direction on the one-way stub often enough that only the
        // in-tick retry reaches the target in time (verified red without it)
        var sim = new TrafficSim(n, seed: 3) { TargetPopulation = 6 };
        for (int i = 0; i < 120; i++) sim.Tick(1f / 60f);
        Assert.True(sim.Vehicles.Count >= 4, $"only {sim.Vehicles.Count} spawned");

        // nothing ever drives against a one-way lane
        var lanesById = n.Edges.Values.SelectMany(e => e.Lanes).ToDictionary(l => l.Id, l => l);
        foreach (var v in sim.Vehicles)
        {
            if (v.Lane is not { } laneId)
                continue;
            var lane = lanesById[laneId];
            if (n.Edges[lane.Edge].Type == RoadCatalog.OneWay.Id)
                Assert.Equal(LaneDirection.Forward, lane.Direction);
        }
    }

    [Fact]
    public void ThreeHundredVehiclesStayCheap()
    {
        var sim = new TrafficSim(Grid(), seed: 11) { TargetPopulation = 300 };
        for (int i = 0; i < 1500; i++)
            sim.Tick(Dt); // fill up
        Assert.True(sim.Vehicles.Count > 100, $"only {sim.Vehicles.Count} vehicles");

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            sim.Tick(Dt);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"1000 ticks with {sim.Vehicles.Count} vehicles took {sw.ElapsedMilliseconds} ms");
    }
}
