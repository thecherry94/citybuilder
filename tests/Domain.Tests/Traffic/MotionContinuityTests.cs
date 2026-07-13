using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

/// <summary>Teleport detector: rendered vehicle poses must move continuously —
/// per tick no farther than physics allows. Catches pose discontinuities at
/// lane/connector boundaries that screenshots can never show.</summary>
public class MotionContinuityTests
{
    private const float Dt = 1f / 30f;

    private static EdgeId EdgeAt(RoadNetwork n, Vector3 mid)
        => n.Edges.Values.Single(e => Vector3.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;

    private static void AssertContinuous(TrafficSim sim, int ticks, string context)
    {
        var last = new Dictionary<int, (Vector3 pos, float speed)>();
        for (int i = 0; i < ticks; i++)
        {
            sim.Tick(Dt);
            foreach (var v in sim.Vehicles)
            {
                var (pos, _) = sim.Pose(v);
                if (last.TryGetValue(v.Id, out var prev))
                {
                    // longitudinal bound + lane-change lateral drift allowance
                    float bound = (prev.speed + 4f * Dt) * Dt + 0.12f;
                    float moved = Vector3.Distance(pos, prev.pos);
                    Assert.True(moved <= bound,
                        $"{context}: tick {i} vehicle {v.Id} jumped {moved:F2} m " +
                        $"(speed {prev.speed:F1} allows {bound:F2})");
                }
                last[v.Id] = (pos, v.Speed);
            }
            foreach (var gone in last.Keys.Where(id => sim.Vehicles.All(v => v.Id != id)).ToArray())
                last.Remove(gone);
        }
    }

    [Fact]
    public void StraightThroughJunctionIsSmooth()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150), RoadCatalog.Street.Id));
        var sim = new TrafficSim(n);
        sim.Spawn(EdgeAt(n, new Vector3(-75, 0, 0)), true, EdgeAt(n, new Vector3(75, 0, 0)));
        AssertContinuous(sim, 30 * 60, "straight through cross");
    }

    [Fact]
    public void TurningAndAmbientTrafficIsSmooth()
    {
        var n = Net.New();
        for (int i = 0; i < 2; i++)
        {
            Net.Commit(n, Net.Straight(new Vector3(-60, 0, i * 150), new Vector3(300, 0, i * 150), RoadCatalog.Street.Id));
            Net.Commit(n, Net.Straight(new Vector3(i * 150, 0, -60), new Vector3(i * 150, 0, 300), RoadCatalog.Street.Id));
        }
        var sim = new TrafficSim(n, seed: 9) { TargetPopulation = 25 };
        AssertContinuous(sim, 30 * 90, "ambient grid");
    }
}
