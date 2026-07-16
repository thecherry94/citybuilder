using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

/// <summary>M5's standing guards: assertive drivers must never co-occupy a conflict
/// point (safety), and must actually discharge a minor road through priority traffic
/// (throughput — the pre-M5 passive behavior fails this floor).</summary>
public class AssertivenessGuardTests
{
    private static EdgeId EdgeAt(RoadNetwork n, Vector3 mid)
        => n.Edges.Values.Single(e => Vector3.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;

    /// <summary>4-way cross of TwoLane (E-W, 400 m arms) x Street (N-S, 400 m arms).
    /// Auto would make Street (wider, OuterHalf 6 vs TwoLane's 4) the main pair, so —
    /// mirroring ArbitrationTests' YieldEntryWithApproachingRival idiom — all four legs
    /// are explicitly overridden under PrioritySigns: TwoLane (E-W) is Main, Street
    /// (N-S) is Yield.</summary>
    private static (RoadNetwork Net, Vector3 Center) BusyCross()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-200, 0, 0), new Vector3(200, 0, 0), RoadCatalog.TwoLane.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -200), new Vector3(0, 0, 200), RoadCatalog.Street.Id));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        var wEdge = EdgeAt(n, new Vector3(-100, 0, 0));
        var eEdge = EdgeAt(n, new Vector3(100, 0, 0));
        var nEdge = EdgeAt(n, new Vector3(0, 0, -100));
        var sEdge = EdgeAt(n, new Vector3(0, 0, 100));
        n.ConfigureJunction(node.Id, node.Config with
        {
            Mode = JunctionControlMode.PrioritySigns,
            RoleOverrides = new Dictionary<EdgeId, LegRole>
            {
                [wEdge] = LegRole.Main,
                [eEdge] = LegRole.Main,
                [nEdge] = LegRole.Yield,
                [sEdge] = LegRole.Yield,
            },
        });
        return (n, node.Position);
    }

    [Fact]
    public void NoTwoVehiclesEverCoOccupyAConflictPoint()
    {
        var (n, _) = BusyCross();
        var sim = new TrafficSim(n, seed: 11) { TargetPopulation = 60 };
        for (int i = 0; i < 60 * 180; i++)
        {
            sim.Tick(1f / 60f);
            AssertNoConflictPointCoOccupancy(sim, n);
        }
    }

    /// <summary>Delegates to SimInvariants (the burst checker's one source of truth for
    /// this rule) so the standing guard and the M6 fuzz/burst harness can never drift
    /// apart.</summary>
    private static void AssertNoConflictPointCoOccupancy(TrafficSim sim, RoadNetwork n)
    {
        foreach (var violation in SimInvariants.ConflictPointCoOccupancyViolations(sim, n))
            Assert.Fail(violation);
    }

    // ---------------------------------------------------------- throughput

    private readonly record struct DirectedLeg(EdgeId Edge, bool Forward, EdgeId Goal);

    /// <summary>Both directed through-movements across the cross's two TwoLane (E-W,
    /// priority) leg edges: spawn on one leg heading toward the junction, routed to
    /// the opposite leg.</summary>
    private static List<DirectedLeg> PriorityEdges(RoadNetwork n) => DirectedLegs(n, RoadCatalog.TwoLane.Id);

    /// <summary>Both directed through-movements across the cross's two Street (N-S,
    /// minor/Yield) leg edges.</summary>
    private static List<DirectedLeg> MinorEdges(RoadNetwork n) => DirectedLegs(n, RoadCatalog.Street.Id);

    private static List<DirectedLeg> DirectedLegs(RoadNetwork n, RoadTypeId type)
    {
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        var legs = node.Edges.Where(e => n.Edges[e].Type == type).OrderBy(e => e.Value).ToArray();
        Assert.Equal(2, legs.Length);
        var (a, b) = (legs[0], legs[1]);
        // "forward" travel approaches the shared node when the edge's own EndNode is
        // that node (Start→End then points at it); otherwise the node is the edge's
        // StartNode, so approaching means traveling backward (End→Start).
        bool aApproachesForward = n.Edges[a].EndNode == node.Id;
        bool bApproachesForward = n.Edges[b].EndNode == node.Id;
        return new List<DirectedLeg> { new(a, aApproachesForward, b), new(b, bApproachesForward, a) };
    }

    [Fact]
    public void MinorRoadDischargesThroughPriorityStream()
    {
        var (n, _) = BusyCross();
        var sim = new TrafficSim(n, seed: 13);
        var (ew, ns) = (PriorityEdges(n), MinorEdges(n));

        int minorSpawned = 0, priorityPulse = 0;
        // id -> last-observed OnLastStep, refreshed every tick a minor is still alive.
        // A tracked id vanishing from sim.Vehicles is only possible two ways: (1) it
        // completed its route (which requires OnLastStep true the instant before, per
        // TrafficSim.HandleTransitions — Arrived++ only fires off a last-step lane) or
        // (2) ReplanStuck dropped it after 20s stalled with no replannable route. Since
        // OnLastStep is only ever true one tick before a genuine arrival, using the
        // pre-tick snapshot to classify each disappearance distinguishes the two
        // without needing sim.Arrived (which also counts the priority stream).
        var minorOnLastStep = new Dictionary<int, bool>();
        int minorArrived = 0, minorStuckDropped = 0;

        for (int i = 0; i < 60 * 120; i++)
        {
            // steady priority stream: one car every 4.5 s, alternating direction (so
            // each direction sees a fresh car every 9 s — dense enough to force real
            // yield decisions without perma-blocking the minor legs; see calibration
            // note below for how this cadence was chosen)
            if (i % 270 == 0)
            {
                var pe = ew[priorityPulse++ % ew.Count];
                sim.Spawn(pe.Edge, pe.Forward, pe.Goal);
            }
            // minor road pressure: keep a queue trying to cross
            if (i % 90 == 0 && minorSpawned < 40)
            {
                var me = ns[minorSpawned % ns.Count];
                if (sim.Spawn(me.Edge, me.Forward, me.Goal) is { } v)
                {
                    minorSpawned++;
                    minorOnLastStep[v.Id] = v.OnLastStep;
                }
            }

            sim.Tick(1f / 60f);

            var alive = new HashSet<int>(sim.Vehicles.Select(v => v.Id));
            foreach (var id in minorOnLastStep.Keys.ToArray())
            {
                if (alive.Contains(id))
                    continue; // still in transit; classified when it eventually vanishes
                if (minorOnLastStep[id])
                    minorArrived++;
                else
                    minorStuckDropped++;
                minorOnLastStep.Remove(id);
            }
            foreach (var v in sim.Vehicles)
                if (minorOnLastStep.ContainsKey(v.Id))
                    minorOnLastStep[v.Id] = v.OnLastStep;
        }

        // sanity check: the classification above only means something if disappearance
        // is overwhelmingly arrival, not stuck-replan-failure giving up
        Assert.True(minorStuckDropped <= minorSpawned / 4,
            $"{minorStuckDropped}/{minorSpawned} minors were stuck-dropped rather than arriving — " +
            "measurement unreliable");
        Assert.True(minorArrived >= MinorDischargeFloor,
            $"only {minorArrived}/{minorSpawned} minor vehicles got through in 2 sim-minutes " +
            $"({minorStuckDropped} stuck-dropped)");
    }

    /// <summary>CALIBRATION (M5 original measured 2026-07-15, full methodology and raw
    /// numbers in .superpowers/sdd/task-10-report.md; re-measured 2026-07-16 after the
    /// M6.5 traffic-feel pass): with M5 arbitration this scenario discharged 18/40
    /// minor vehicles in the 2 sim-minute window (pre-M5 commit a67a1e3, before any
    /// conflict-point/movement-rank/impatience/deadlock-breaker work, managed only
    /// 7/40 — the passive baseline). After M6.5 (IDM+ min form, launch acceleration,
    /// anticipatory spillback at 0.7 s, driver personalities, curvature turn speeds)
    /// the measured discharge is 21/40 with 0 stuck-dropped. Floor = measured − 3
    /// (M5 margin convention, locking in the M6.5 gain): 21 − 3 = 18 — which is the
    /// full pre-M6.5 measurement itself, and still 2.6× the passive baseline.</summary>
    private const int MinorDischargeFloor = 18; // measured 21 − margin 3
}
