using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;

namespace CityBuilder.Domain.Tests.Kpi;

/// <summary>Deterministic scenario builders for the M6 KPI health report. Every
/// scenario is pure domain, seeded, and produces a small metrics dictionary.
/// Plausibility anchors
/// (real traffic engineering): startup lost time ~2 s (1-4 s accepted), saturation
/// headway ~2 s (1-3 s accepted), grid delay index 1.0-3.0. An implausible number
/// coming out of one of these means the SCENARIO has a bug (wrong phase detection, a
/// queue that never really queued, a wrong free-flow reference) — never a reason to
/// retune a domain constant (that tuning pass is an explicit post-M6 follow-up).</summary>
public static class KpiScenarios
{
    private const float Dt = 1f / 60f;

    // ------------------------------------------------------------ signal_discharge

    /// <summary>Cross of TwoLane roads with a traffic-light junction. Ten vehicles
    /// are placed bumper-to-bumper behind the stop line on one approach — genuinely
    /// "queued at red", not merely strung out still driving toward it, which is what a
    /// naive spawn-and-let-it-drive-there approach produces in practice (measured:
    /// only the front 3-4 of 10 had actually reached and stopped at the line by the
    /// time green arrived, corrupting the headway measurement with vehicles still in
    /// transit). From the tick the leg turns green, each queued vehicle's
    /// connector-entry time is recorded.
    ///
    /// startup_lost_s is the gap between green onset and the first entry (HCM
    /// "start-up lost time"). sat_headway_s is meant to be the steady discharge
    /// headway once flowing — measured here as the mean of consecutive-entry gaps
    /// that are NOT cycle boundaries. A 12 s green with this driver model's
    /// standing-start acceleration only clears about 3-4 vehicles per cycle (each
    /// follower's gap is governed by how far its leader has advanced into the short
    /// junction connector, not by lane spacing — confirmed by rerunning with looser
    /// initial queue spacing, which changed nothing), so a literal "entries 4-10"
    /// read spans two-plus signal cycles and its raw mean gap is dominated by ~20 s+
    /// red-wait jumps between cycles (an earlier version of this scenario measured
    /// sat_headway_s ~9.5 s this way — the scenario bug, not the true headway).
    /// Any gap >= one green phase's duration cannot be an intra-green discharge gap
    /// by construction, so those are dropped before averaging.
    ///
    /// PLAUSIBILITY NOTE: because each 12 s green only discharges queue positions
    /// 1-4 before the queue re-stops for the next red, every measured gap is an
    /// early-position headway (HCM h1~3.8 s, h2~3.1 s, h3~2.7 s, h4~2.4 s), not the
    /// h5+ steady-state ~2 s — so this metric is expected to sit near the top of
    /// (or slightly above) the 1-3 s saturation anchor. Post-M6 driver-model tuning
    /// that speeds standing-start discharge should pull it down.</summary>
    public static Dictionary<string, float> SignalDischarge()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-130, 0, 0), new Vector3(130, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -130), new Vector3(0, 0, 130)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.TrafficLights });

        var west = EdgeAt(n, new Vector3(-65, 0, 0));
        var east = EdgeAt(n, new Vector3(65, 0, 0));

        var sim = new TrafficSim(n, seed: 41);

        // run until a red onset on the west leg — placing the queue right at the
        // start of a red phase gives it the full window before the next green
        var prevPhase = sim.PhaseFor(node.Id, west);
        for (int i = 0; i < 60 * 120; i++)
        {
            sim.Tick(Dt);
            var phase = sim.PhaseFor(node.Id, west);
            if (prevPhase != SignalPhase.Red && phase == SignalPhase.Red)
                break;
            prevPhase = phase;
        }
        if (sim.PhaseFor(node.Id, west) != SignalPhase.Red)
            throw new InvalidOperationException("signal_discharge: never observed a red onset on the west leg");

        // place 10 vehicles bumper-to-bumper behind the stop line: spawn each at the
        // lane's entry (handles route/PlannedConnector setup), then reposition it
        // directly onto its slot in the stationary queue. Front bumper of vehicle i
        // sits at frontS - i * (Length + S0); frontS = 121 leaves the vehicle just
        // short of this 130 m lane's ~129.6 m drawable length (a genuine stop-line
        // queue, not vehicles still driving toward it).
        const float frontS = 121f;
        float spacing = Vehicle.Length + Idm.S0;
        var queuedIds = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            var v = sim.Spawn(west, true, east)
                ?? throw new InvalidOperationException($"signal_discharge: failed to place queued vehicle {i}");
            v.S = frontS - i * spacing;
            v.Speed = 0f;
            queuedIds.Add(v.Id);
        }

        // run to the next green onset on the west leg
        float greenOnset = -1f;
        prevPhase = sim.PhaseFor(node.Id, west);
        for (int i = 0; i < 60 * 40; i++)
        {
            sim.Tick(Dt);
            var phase = sim.PhaseFor(node.Id, west);
            if (prevPhase != SignalPhase.Green && phase == SignalPhase.Green)
            {
                greenOnset = sim.Time;
                break;
            }
            prevPhase = phase;
        }
        if (greenOnset < 0f)
            throw new InvalidOperationException("signal_discharge: green never arrived after queueing");

        // track each queued vehicle's connector-entry time (Lane -> Crossing); the
        // budget spans several signal cycles since not all 10 clear in one green
        var entryTimes = new Dictionary<int, float>();
        var remaining = new HashSet<int>(queuedIds);
        for (int i = 0; i < 60 * 150 && entryTimes.Count < queuedIds.Count; i++)
        {
            sim.Tick(Dt);
            foreach (var v in sim.Vehicles)
                if (remaining.Contains(v.Id) && v.Crossing is not null && !entryTimes.ContainsKey(v.Id))
                    entryTimes[v.Id] = sim.Time;
        }
        if (entryTimes.Count < queuedIds.Count)
            throw new InvalidOperationException(
                $"signal_discharge: only {entryTimes.Count}/{queuedIds.Count} discharged after green");

        var sorted = queuedIds.Select(id => entryTimes[id]).OrderBy(t => t).ToArray();
        float startupLost = sorted[0] - greenOnset;

        var gaps = new List<float>();
        for (int i = 0; i + 1 < sorted.Length; i++)
            gaps.Add(sorted[i + 1] - sorted[i]);
        // a gap spanning a full green phase can only be a cycle boundary (queue
        // stalled through amber/red/next red), never a genuine discharge headway
        var intraCycleGaps = gaps.Where(g => g < SignalController.GreenSec).ToArray();
        if (intraCycleGaps.Length == 0)
            throw new InvalidOperationException("signal_discharge: every consecutive entry gap crossed a cycle boundary");

        return new Dictionary<string, float>
        {
            ["signal.startup_lost_s"] = startupLost,
            ["signal.sat_headway_s"] = intraCycleGaps.Average(),
        };
    }

    // ------------------------------------------------------------------- yield_4way

    private readonly record struct DirectedLeg(EdgeId Edge, bool Forward, EdgeId Goal);

    /// <summary>Same BusyCross layout as AssertivenessGuardTests: a 4-way cross of
    /// TwoLane (E-W, priority/Main) x Street (N-S, minor/Yield), all four legs
    /// explicitly overridden under PrioritySigns.</summary>
    private static (RoadNetwork Net, List<DirectedLeg> Priority, List<DirectedLeg> Minor) BusyCross()
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
        return (n, DirectedLegs(n, RoadCatalog.TwoLane.Id), DirectedLegs(n, RoadCatalog.Street.Id));
    }

    private static List<DirectedLeg> DirectedLegs(RoadNetwork n, RoadTypeId type)
    {
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        var legs = node.Edges.Where(e => n.Edges[e].Type == type).OrderBy(e => e.Value).ToArray();
        var (a, b) = (legs[0], legs[1]);
        bool aApproachesForward = n.Edges[a].EndNode == node.Id;
        bool bApproachesForward = n.Edges[b].EndNode == node.Id;
        return new List<DirectedLeg> { new(a, aApproachesForward, b), new(b, bApproachesForward, a) };
    }

    /// <summary>BusyCross's priority-vs-minor mix (TripLog on) for 120 sim-seconds:
    /// one priority pulse every 4.5 s alternating direction, minor pressure spawning
    /// every 1.5 s up to 40 attempts. Per-minor-trip delay = arrival - spawn -
    /// free-flow, clamped at >= 0 (the 8 m connector free-flow approximation can
    /// over-credit fractionally on short hops).</summary>
    public static Dictionary<string, float> Yield4Way()
    {
        var (n, ew, ns) = BusyCross();
        var sim = new TrafficSim(n, seed: 13) { TripLog = new List<TrafficSim.TripRecord>() };

        var minorIds = new HashSet<int>();
        int minorSpawned = 0, priorityPulse = 0;
        for (int i = 0; i < 60 * 120; i++)
        {
            if (i % 270 == 0)
            {
                var pe = ew[priorityPulse++ % ew.Count];
                sim.Spawn(pe.Edge, pe.Forward, pe.Goal);
            }
            if (i % 90 == 0 && minorSpawned < 40)
            {
                var me = ns[minorSpawned % ns.Count];
                if (sim.Spawn(me.Edge, me.Forward, me.Goal) is { } v)
                {
                    minorSpawned++;
                    minorIds.Add(v.Id);
                }
            }
            sim.Tick(Dt);
        }

        var delays = sim.TripLog!
            .Where(t => minorIds.Contains(t.VehicleId))
            .Select(t => MathF.Max(0f, t.ArrivalTime - t.SpawnTime - t.FreeFlowTime))
            .OrderBy(d => d)
            .ToArray();

        return new Dictionary<string, float>
        {
            ["yield4.minor_delay_mean_s"] = delays.Length > 0 ? delays.Average() : 0f,
            ["yield4.minor_delay_p95_s"] = Percentile(delays, 0.95f),
            ["yield4.completed"] = delays.Length,
        };
    }

    // ---------------------------------------------------------------- grid_commute

    /// <summary>3x3 TwoLane grid (100 m cells), ambient TargetPopulation = 60, 180
    /// sim-seconds. delay_index averages actual/free-flow travel time over completed
    /// trips (1.0 = free flow, no delay); stops_per_trip averages recorded stop
    /// counts.</summary>
    public static Dictionary<string, float> GridCommute()
    {
        var n = BuildGrid(3, 100f);
        var sim = new TrafficSim(n, seed: 29)
        {
            TargetPopulation = 60,
            TripLog = new List<TrafficSim.TripRecord>(),
        };
        for (int i = 0; i < 60 * 180; i++)
            sim.Tick(Dt);

        var trips = sim.TripLog!;
        var ratios = trips
            .Where(t => t.FreeFlowTime > 0.01f)
            .Select(t => (t.ArrivalTime - t.SpawnTime) / t.FreeFlowTime)
            .ToArray();
        var stops = trips.Select(t => (float)t.Stops).ToArray();

        return new Dictionary<string, float>
        {
            ["grid.delay_index"] = ratios.Length > 0 ? ratios.Average() : 0f,
            ["grid.stops_per_trip"] = stops.Length > 0 ? stops.Average() : 0f,
        };
    }

    // ---------------------------------------------------------------------- perf

    /// <summary>500-ish-edge grid (16x16 TwoLane, 480 edges): one Stopwatch-timed
    /// Validate of a long diagonal crossing proposal (validate500_ms), then a
    /// 300-vehicle ambient population, timing 600 ticks at that steady population
    /// (tick300_ms, mean ms/tick). The 300-vehicle fill uses direct Spawn() calls
    /// (bypassing the ambient spawner's cooldown) so filling up doesn't itself dominate
    /// the measured window; TargetPopulation is then set to 300 so the timed ticks
    /// still exercise ordinary ambient replenishment.</summary>
    public static Dictionary<string, float> Perf()
    {
        var n = BuildGrid(16, 100f);

        var proposal = Net.Straight(new Vector3(-50, 0, -50), new Vector3(1550, 0, 1550));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        n.Validate(proposal);
        sw.Stop();
        float validateMs = (float)sw.Elapsed.TotalMilliseconds;

        var sim = new TrafficSim(n, seed: 71);
        var rng = new Random(71);
        var edges = n.Edges.Keys.ToArray();
        int attempts = 0;
        while (sim.Vehicles.Count < 300 && attempts < 50_000)
        {
            attempts++;
            var from = edges[rng.Next(edges.Length)];
            var to = edges[rng.Next(edges.Length)];
            if (from == to)
                continue;
            bool fwd = rng.Next(2) == 0;
            if (sim.Spawn(from, fwd, to) is null)
                sim.Spawn(from, !fwd, to);
        }
        if (sim.Vehicles.Count < 300)
            throw new InvalidOperationException($"perf: only filled {sim.Vehicles.Count}/300 vehicles");
        sim.TargetPopulation = 300;

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
            sim.Tick(Dt);
        sw2.Stop();
        float tickMs = (float)sw2.Elapsed.TotalMilliseconds / 600f;

        return new Dictionary<string, float>
        {
            ["perf.validate500_ms"] = validateMs,
            ["perf.tick300_ms"] = tickMs,
        };
    }

    // ------------------------------------------------------------------- shared

    private static EdgeId EdgeAt(RoadNetwork n, Vector3 mid)
        => n.Edges.Values.Single(e => Vector3.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;

    /// <summary>size x size grid of nodes spaced `cell` metres apart (TwoLane),
    /// built the way an editor would: full-length horizontal/vertical lines that the
    /// network auto-splits at their crossings, exactly like GestureFuzzer's grid
    /// stamps and every other multi-edge fixture in this test project.</summary>
    private static RoadNetwork BuildGrid(int size, float cell)
    {
        var n = Net.New();
        float extent = (size - 1) * cell;
        for (int j = 0; j < size; j++)
            Net.Commit(n, Net.Straight(new Vector3(0, 0, j * cell), new Vector3(extent, 0, j * cell)));
        for (int i = 0; i < size; i++)
            Net.Commit(n, Net.Straight(new Vector3(i * cell, 0, 0), new Vector3(i * cell, 0, extent)));
        return n;
    }

    private static float Percentile(float[] sortedAscending, float p)
    {
        if (sortedAscending.Length == 0)
            return 0f;
        int rank = (int)MathF.Ceiling(p * sortedAscending.Length) - 1;
        rank = Math.Clamp(rank, 0, sortedAscending.Length - 1);
        return sortedAscending[rank];
    }
}
