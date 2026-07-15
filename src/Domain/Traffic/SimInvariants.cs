using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Traffic;

/// <summary>
/// Shared health checker for a live <see cref="TrafficSim"/> run: everything a
/// regression test, a fuzz harness, or a debug overlay would want to assert about
/// drivability under an ambient burst of traffic, in one place. Complements
/// <see cref="NetworkInvariants"/> (which only looks at static network structure) —
/// this is about what a running sim actually does with that network.
/// </summary>
public static class SimInvariants
{
    private const float PenetrationMargin = 0.05f;
    private const float ConflictMargin = 0.3f; // mirrors AssertivenessGuardTests

    /// <summary>Spawns a small ambient population on <paramref name="n"/> and ticks it
    /// for <paramref name="ticks"/> steps at 60 Hz, watching for: an exception during
    /// spawn/tick (recorded, burst stopped immediately); a follower ending up inside
    /// its leader on any lane/connector queue, post-<c>Tick</c> — i.e. after
    /// <c>EnforceNoPenetration</c> ran, so a violation here means the failsafe itself
    /// failed; and two vehicles co-occupying a junction conflict point at once.
    /// Empty = the network is drivable.</summary>
    public static IReadOnlyList<string> CheckBurst(RoadNetwork n, int seed, int ticks = 300, int population = 12)
    {
        var violations = new List<string>();
        try
        {
            var sim = new TrafficSim(n, seed) { TargetPopulation = population };
            for (int i = 0; i < ticks && violations.Count == 0; i++)
            {
                sim.Tick(1f / 60f);
                CheckPenetration(sim, violations);
                if (violations.Count == 0)
                    violations.AddRange(ConflictPointCoOccupancyViolations(sim, n));
            }
        }
        catch (Exception ex)
        {
            violations.Add($"exception during burst (seed {seed}, tick loop): {ex}");
        }
        return violations;
    }

    /// <summary>No queue entry may be found inside the bumper-to-bumper space its
    /// leader occupies. Queues are already front-to-back ordered (see
    /// <see cref="TrafficSim.AllQueues"/>), so a single pass per queue suffices.</summary>
    private static void CheckPenetration(TrafficSim sim, List<string> outViolations)
    {
        foreach (var queue in sim.AllQueues())
            for (int i = 1; i < queue.Count; i++)
            {
                var leader = queue[i - 1];
                var follower = queue[i];
                if (follower.S > leader.S - Vehicle.Length + PenetrationMargin)
                    outViolations.Add(
                        $"vehicle {follower.Id} penetrates leader {leader.Id} " +
                        $"(follower.S={follower.S:F2}, leader.S={leader.S:F2})");
            }
    }

    /// <summary>Lifted from AssertivenessGuardTests.AssertNoConflictPointCoOccupancy so
    /// the burst checker and the standing M5 regression guard share one source of
    /// truth: no two vehicles may ever occupy the same conflict point at once.</summary>
    internal static IEnumerable<string> ConflictPointCoOccupancyViolations(TrafficSim sim, RoadNetwork n)
    {
        foreach (var node in n.Nodes.Values)
        for (int i = 0; i < node.Connectors.Count; i++)
        foreach (var cp in node.ConnectorConflicts[i])
        {
            if (cp.Other <= i)
                continue; // each pair once
            var mine = sim.VehiclesOnConnector(node.Id, i);
            var theirs = sim.VehiclesOnConnector(node.Id, cp.Other);
            foreach (var a in mine)
            foreach (var b in theirs)
            {
                bool aAt = a.S + ConflictMargin > cp.SMine && a.S - Vehicle.Length - ConflictMargin < cp.SMine;
                bool bAt = b.S + ConflictMargin > cp.STheirs && b.S - Vehicle.Length - ConflictMargin < cp.STheirs;
                if (aAt && bAt)
                    yield return $"vehicles {a.Id} and {b.Id} co-occupy a conflict point at node {node.Id}";
            }
        }
    }
}
