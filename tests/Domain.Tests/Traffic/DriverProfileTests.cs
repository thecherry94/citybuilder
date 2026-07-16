using System.Numerics;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

/// <summary>Per-driver personality: a seeded scalar in [0,1] drawn from the sim's own
/// RNG at spawn time, varying desired speed and gap acceptance (TM:PE-style) so
/// vehicles stop moving in robotic lockstep.</summary>
public class DriverProfileTests
{
    /// <summary>Cross of two long roads (4 edges after the auto-split at the
    /// intersection) so the ambient spawner — which requires at least 2 distinct
    /// edges to pick a from/to pair — actually spawns traffic.</summary>
    private static List<float> SpawnProfiles(int seed)
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-500, 0, 0), new Vector3(500, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -500), new Vector3(0, 0, 500)));
        var sim = new TrafficSim(n, seed: seed) { TargetPopulation = 10 };
        for (int i = 0; i < 60 * 20; i++)
            sim.Tick(1f / 60f);
        return sim.Vehicles.Select(v => v.Profile).ToList();
    }

    [Fact]
    public void ProfilesAreDeterministicPerSeed()
    {
        var a = SpawnProfiles(seed: 7);
        var b = SpawnProfiles(seed: 7);
        var c = SpawnProfiles(seed: 8);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.All(a, p => Assert.InRange(p, 0f, 1f));
        Assert.True(a.Distinct().Count() > 3, "profiles must actually vary");
    }

    [Fact]
    public void AggressiveDriverCruisesFasterThanTimidOnFreeRoad()
    {
        // Two vehicles on separate long roads, no leaders, no junctions in the way:
        // pin one vehicle's profile to 0.1 (timid) and the other's to 0.9 (aggressive)
        // right after spawn (simplest deterministic way to control the two profiles
        // — Vehicle.Profile is a settable property, and DesiredSpeed reads it fresh
        // every tick, so overwriting it post-spawn is equivalent to having drawn that
        // value from the RNG).
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(2000, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 50), new Vector3(2000, 0, 50)));
        var sim = new TrafficSim(n, seed: 3);
        var edgesOnRoad1 = n.Edges.Keys.Where(e => n.Edges[e].Curve.Point(0.5f).Z < 25f)
            .OrderBy(e => e.Value).ToArray();
        var edgesOnRoad2 = n.Edges.Keys.Where(e => n.Edges[e].Curve.Point(0.5f).Z >= 25f)
            .OrderBy(e => e.Value).ToArray();

        var timid = sim.Spawn(edgesOnRoad1[0], forward: true, edgesOnRoad1[^1]);
        var aggressive = sim.Spawn(edgesOnRoad2[0], forward: true, edgesOnRoad2[^1]);
        Assert.NotNull(timid);
        Assert.NotNull(aggressive);
        timid!.Profile = 0.1f;
        aggressive!.Profile = 0.9f;

        for (int i = 0; i < 60 * 15; i++)
            sim.Tick(1f / 60f);

        Assert.True(aggressive.Speed >= timid.Speed * 1.10f,
            $"aggressive driver ({aggressive.Speed:F2} m/s) should cruise >= 10% faster " +
            $"than timid driver ({timid.Speed:F2} m/s)");
    }
}
