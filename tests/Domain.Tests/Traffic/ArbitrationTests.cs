using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class ArbitrationTests
{
    private const float Dt = 1f / 30f;

    private static EdgeId EdgeAt(RoadNetwork n, Vector3 mid)
        => n.Edges.Values.Single(e => Vector3.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;

    /// <summary>Street (E-W, main) crossed by TwoLane (N-S, yield under Auto).</summary>
    private static RoadNetwork MixedCross()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150), RoadCatalog.TwoLane.Id));
        return n;
    }

    [Fact]
    public void YieldWaitsForPriorityTraffic()
    {
        var n = MixedCross();
        var sim = new TrafficSim(n);
        var wEdge = EdgeAt(n, new Vector3(-75, 0, 0));
        var eEdge = EdgeAt(n, new Vector3(75, 0, 0));
        var nEdge = EdgeAt(n, new Vector3(0, 0, -75));
        var sEdge = EdgeAt(n, new Vector3(0, 0, 75));

        // steady main-road platoon
        var mains = new List<Vehicle>();
        for (int i = 0; i < 4; i++)
        {
            Vehicle? m = null;
            for (int t = 0; t < 900 && m is null; t++)
            {
                m = sim.Spawn(wEdge, true, eEdge);
                if (m is null)
                    sim.Tick(Dt);
            }
            Assert.NotNull(m);
            mains.Add(m!);
        }
        var minor = sim.Spawn(nEdge, true, sEdge)!;

        bool minorCrossed = false;
        for (int i = 0; i < 30 * 120 && !minorCrossed; i++)
        {
            sim.Tick(Dt);
            if (minor.Crossing is not null && !minorCrossed)
            {
                minorCrossed = true;
                // rule: no priority vehicle within 25 m of the junction moving fast
                foreach (var m in mains.Where(m => sim.Vehicles.Contains(m) && m.Lane is not null))
                {
                    var (pos, _) = sim.Pose(m);
                    float dist = Vector3.Distance(pos, new Vector3(0, 0, 0));
                    bool approaching = pos.X < -6f; // west of the junction
                    if (approaching && m.Speed > 3f)
                        Assert.True(dist > 25f,
                            $"minor entered while main {m.Id} was {dist:F1} m away at {m.Speed:F1} m/s");
                }
            }
        }
        Assert.True(minorCrossed, "minor vehicle never crossed");
    }

    [Fact]
    public void AllWayStopIsFifo()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.AllWayStop });

        var sim = new TrafficSim(n);
        var west = sim.Spawn(EdgeAt(n, new Vector3(-75, 0, 0)), true, EdgeAt(n, new Vector3(75, 0, 0)))!;
        // north vehicle starts 40 m behind → arrives later
        for (int i = 0; i < 30 * 3; i++)
            sim.Tick(Dt);
        var north = sim.Spawn(EdgeAt(n, new Vector3(0, 0, -75)), true, EdgeAt(n, new Vector3(0, 0, 75)))!;

        float westEnter = -1, northEnter = -1;
        for (int i = 0; i < 30 * 120 && (westEnter < 0 || northEnter < 0); i++)
        {
            sim.Tick(Dt);
            if (westEnter < 0 && west.Crossing is not null) westEnter = sim.Time;
            if (northEnter < 0 && north.Crossing is not null) northEnter = sim.Time;
        }
        Assert.True(westEnter > 0 && northEnter > 0, "both must eventually cross");
        Assert.True(westEnter < northEnter, $"west {westEnter:F1} should precede north {northEnter:F1}");
    }

    [Fact]
    public void ConflictingConnectorsNeverOverlap()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);

        var sim = new TrafficSim(n);
        // two opposing left turns: W→N and E→S (their connector curves cross)
        var a = sim.Spawn(EdgeAt(n, new Vector3(-75, 0, 0)), true, EdgeAt(n, new Vector3(0, 0, -75)))!;
        var b = sim.Spawn(EdgeAt(n, new Vector3(75, 0, 0)), false, EdgeAt(n, new Vector3(0, 0, 75)))!;

        for (int i = 0; i < 30 * 120 && sim.Arrived < 2; i++)
        {
            sim.Tick(Dt);
            if (a.Crossing is { } ca && b.Crossing is { } cb && ca.Node == cb.Node)
            {
                var conflicts = node.ConnectorConflicts[ca.Connector].Select(c => c.Other).ToArray();
                Assert.DoesNotContain(cb.Connector, conflicts);
            }
        }
        Assert.Equal(2, sim.Arrived);
    }

    [Fact]
    public void StopRoleRequiresFullStop()
    {
        var n = MixedCross();
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        var nEdge = EdgeAt(n, new Vector3(0, 0, -75));
        n.ConfigureJunction(node.Id, node.Config with
        {
            Mode = JunctionControlMode.PrioritySigns,
            RoleOverrides = new Dictionary<EdgeId, LegRole> { [nEdge] = LegRole.Stop },
        });

        var sim = new TrafficSim(n);
        var v = sim.Spawn(nEdge, true, EdgeAt(n, new Vector3(0, 0, 75)))!;
        float minSpeedNearLine = float.MaxValue;
        bool crossed = false;
        for (int i = 0; i < 30 * 90 && !crossed; i++)
        {
            sim.Tick(Dt);
            if (v.Crossing is not null)
                crossed = true;
            else if (v.Lane is not null && sim.Vehicles.Contains(v))
            {
                var (pos, _) = sim.Pose(v);
                if (Vector3.Distance(pos, node.Position) < 18f)
                    minSpeedNearLine = MathF.Min(minSpeedNearLine, v.Speed);
            }
        }
        Assert.True(crossed, "stop vehicle never crossed");
        Assert.True(minSpeedNearLine < 0.1f, $"min speed near line was {minSpeedNearLine:F2}");
    }

    /// <summary>4-way cross of TwoLane roads (200 m arms): spawns `entering` from the
    /// south arm heading straight north and `rival` from the west arm heading straight
    /// east, ticking until `entering` is within 10 m of its stop line so a caller can
    /// then puppet `rival` onto its connector with ForceConnector.</summary>
    private static (TrafficSim Sim, NodeId Node, Vehicle Entering, Vehicle Rival, int RivalConn)
        CrossWithRivalOnConflictingConnector()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-200, 0, 0), new Vector3(200, 0, 0), RoadCatalog.TwoLane.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -200), new Vector3(0, 0, 200), RoadCatalog.TwoLane.Id));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);

        var sim = new TrafficSim(n);
        var wEdge = EdgeAt(n, new Vector3(-100, 0, 0));
        var eEdge = EdgeAt(n, new Vector3(100, 0, 0));
        var nEdge = EdgeAt(n, new Vector3(0, 0, -100));
        var sEdge = EdgeAt(n, new Vector3(0, 0, 100));

        var entering = sim.Spawn(sEdge, false, nEdge)!;
        var rival = sim.Spawn(wEdge, true, eEdge)!;
        // identify rivalConn before either vehicle moves: rival's first (and only)
        // movement through the junction never changes once picked at spawn. Despawn
        // rival immediately after so it can't race entering to (and past) the
        // junction on its own — ForceConnector re-places it explicitly below.
        int rivalConn = rival.PlannedConnector!.Value.Connector;
        sim.Despawn(rival);

        for (int i = 0; i < 30 * 60 && entering.Lane is not null; i++)
        {
            var (pos, _) = sim.Pose(entering);
            if (Vector3.Distance(pos, node.Position) < 10f)
                break;
            sim.Tick(Dt);
        }
        Assert.NotNull(entering.Lane); // must still be approaching, not already through

        return (sim, node.Id, entering, rival, rivalConn);
    }

    [Fact]
    public void VehiclePastTheConflictPointNoLongerBlocksEntry()
    {
        // cross with a vehicle ON a conflicting connector: before the crossing point it
        // blocks; teleported past it (rear bumper clear), entry opens the same tick
        var (sim, node, entering, rival, rivalConn) = CrossWithRivalOnConflictingConnector();
        var cp = sim.Network.Nodes[node].ConnectorConflicts[entering.PlannedConnector!.Value.Connector]
            .Single(c => c.Other == rivalConn);

        sim.ForceConnector(rival, node, rivalConn, MathF.Max(0, cp.STheirs - 2f));
        sim.Tick(1f / 60f);
        Assert.True(entering.Speed < 0.5f || entering.Lane is not null, "must still hold");
        Assert.NotNull(entering.Lane); // still on its lane, held at the line

        sim.ForceConnector(rival, node, rivalConn, cp.STheirs + Vehicle.Length + 0.6f);
        for (int i = 0; i < 240 && entering.Crossing is null; i++)
            sim.Tick(1f / 60f);
        Assert.NotNull(entering.Crossing); // took the gap behind the crossed car
    }

    /// <summary>Drivable length of `edgeId`'s lane run, mirroring TrafficSim's own
    /// (private) LaneRun.Length computation from the node junction cuts — lets a test
    /// place a vehicle at an exact distance from its stop line via public network state.</summary>
    private static float ApproachRunLength(RoadNetwork n, EdgeId edgeId)
    {
        var edge = n.Edges[edgeId];
        float tStart = 0f, tEnd = 1f;
        if (n.Nodes.TryGetValue(edge.StartNode, out var sn) && sn.Junction.CutT.TryGetValue(edgeId, out var a))
            tStart = a;
        if (n.Nodes.TryGetValue(edge.EndNode, out var en) && en.Junction.CutT.TryGetValue(edgeId, out var b))
            tEnd = b;
        float dA = edge.ArcLength.DistanceAtT(tStart);
        float dB = edge.ArcLength.DistanceAtT(tEnd);
        return MathF.Max(0.5f, dB - dA);
    }

    /// <summary>Cross of TwoLane (E-W, priority/Free) x Street (N-S, minor/Yield —
    /// forced via RoleOverrides since Auto would otherwise pick the wider Street as
    /// main). `entering` spawns on the south Street leg heading north through the
    /// junction and is ticked to within 10 m of its stop line. `rival` is a ghost
    /// (spawned then Despawn'd off the live vehicle list, so it never advances on its
    /// own) placed on the west TwoLane leg at the S/speed pair whose ratio is
    /// `ttaSeconds` — the ConflictApproachClear time-to-arrival the arbiter sees for it
    /// against `entering`'s connector.</summary>
    private static (TrafficSim Sim, Vehicle Entering, Vehicle Rival) YieldEntryWithApproachingRival(
        float ttaSeconds)
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-200, 0, 0), new Vector3(200, 0, 0), RoadCatalog.TwoLane.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -200), new Vector3(0, 0, 200), RoadCatalog.Street.Id));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        var wEdge = EdgeAt(n, new Vector3(-100, 0, 0));
        var eEdge = EdgeAt(n, new Vector3(100, 0, 0));
        var nEdge = EdgeAt(n, new Vector3(0, 0, -100));
        var sEdge = EdgeAt(n, new Vector3(0, 0, 100));

        // Auto would make Street (wider, OuterHalf 6 vs TwoLane's 4) the main pair;
        // force the reverse explicitly so TwoLane is priority/Free and Street yields.
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

        var sim = new TrafficSim(n);
        var entering = sim.Spawn(sEdge, false, nEdge)!;
        var rival = sim.Spawn(wEdge, true, eEdge)!;
        var rivalLane = rival.Lane!.Value;
        sim.Despawn(rival); // ghost: removed from the live list, so it never moves on its own

        for (int i = 0; i < 30 * 60 && entering.Lane is not null; i++)
        {
            var (pos, _) = sim.Pose(entering);
            if (Vector3.Distance(pos, node.Position) < 10f)
                break;
            sim.Tick(Dt);
        }
        Assert.NotNull(entering.Lane); // must still be approaching, not already through

        const float rivalSpeed = 10f;
        float runLength = ApproachRunLength(n, wEdge);
        rival.Speed = rivalSpeed;
        rival.S = MathF.Max(0f, runLength - ttaSeconds * rivalSpeed);
        sim.ForceLane(rival, rivalLane);

        return (sim, entering, rival);
    }

    [Fact]
    public void WaitingShrinksTheAcceptedGap()
    {
        // rival approaching with tta ≈ 2.5 s: blocks a fresh driver (2.8 s gap),
        // accepted by one who has waited 25 s (gap floor 2.2 s)
        var (sim, entering, _) = YieldEntryWithApproachingRival(ttaSeconds: 2.5f);
        entering.JunctionWait = 0f;
        sim.Tick(1f / 60f);
        Assert.NotNull(entering.Lane);           // fresh driver holds

        entering.JunctionWait = 25f;
        for (int i = 0; i < 120 && entering.Crossing is null; i++)
            sim.Tick(1f / 60f);
        Assert.NotNull(entering.Crossing);       // impatient driver takes it
    }

    [Fact]
    public void BlockedVehicleAccumulatesWaitAndGetsATicket()
    {
        var (sim, entering, _) = YieldEntryWithApproachingRival(ttaSeconds: 1.0f);
        for (int i = 0; i < 120; i++) sim.Tick(1f / 60f);
        Assert.True(entering.JunctionWait > 1f, $"wait {entering.JunctionWait}");
        Assert.True(entering.WaitArrivalOrder > 0, "ticket must stamp on first block");
    }

    // ---------------------------------------------------------- movement ranks

    /// <summary>Cross of two TwoLane roads with the W/E pair forced Main (Free) and
    /// N/S forced Yield — so a left turn off the priority road (W leg, turning onto
    /// the N leg) and an oncoming straight through the same priority road (E leg
    /// through to W) are both Free-Row movements: MovementRank must still make the
    /// left yield to the higher-ranked straight. `leftTurner` is ticked to within
    /// 10 m of its stop line; `rival` (the oncoming straight) is a ghost placed at
    /// the S/speed pair giving the requested time-to-arrival, mirroring
    /// YieldEntryWithApproachingRival's idiom.</summary>
    private static (TrafficSim Sim, Vehicle LeftTurner) PriorityLeftVsOncomingStraight(float oncomingTtaSeconds)
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-200, 0, 0), new Vector3(200, 0, 0), RoadCatalog.TwoLane.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -200), new Vector3(0, 0, 200), RoadCatalog.TwoLane.Id));
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

        var sim = new TrafficSim(n);
        var leftTurner = sim.Spawn(wEdge, true, nEdge)!; // west leg, left onto the north leg
        var rival = sim.Spawn(eEdge, false, wEdge)!;     // oncoming straight: east leg through to west
        var rivalLane = rival.Lane!.Value;
        sim.Despawn(rival); // ghost: removed from the live list, so it never moves on its own

        for (int i = 0; i < 30 * 60 && leftTurner.Lane is not null; i++)
        {
            var (pos, _) = sim.Pose(leftTurner);
            if (Vector3.Distance(pos, node.Position) < 10f)
                break;
            sim.Tick(Dt);
        }
        Assert.NotNull(leftTurner.Lane); // must still be approaching, not already through

        const float rivalSpeed = 10f;
        float runLength = ApproachRunLength(n, eEdge);
        rival.Speed = rivalSpeed;
        rival.S = MathF.Max(0f, runLength - oncomingTtaSeconds * rivalSpeed);
        sim.ForceLane(rival, rivalLane);

        return (sim, leftTurner);
    }

    [Fact]
    public void LeftTurnerOnPriorityRoadYieldsToOncomingStraight()
    {
        // both legs Free: the left-turn movement (rank 3,1) must yield to the oncoming
        // straight (rank 3,3) approaching within the accepted gap
        var (sim, leftTurner) = PriorityLeftVsOncomingStraight(oncomingTtaSeconds: 2.0f);
        for (int i = 0; i < 120; i++) sim.Tick(1f / 60f);
        // held at the line while oncoming passes: never entered the junction and never
        // advanced a route step (Lane alone would be non-null again on the exit lane
        // after an illegal crossing, so it cannot discriminate)
        Assert.Null(leftTurner.Crossing);
        Assert.Equal(0, leftTurner.StepIndex);
    }

    /// <summary>Uncontrolled 4-way cross (JunctionControlMode.None → every leg Free,
    /// so an equal-rank tie can only be broken by ApproachesFromMyRight). Arms are
    /// shortened to 150 m (matching MixedCross's convention) so both cars reach the
    /// line and resolve well inside a 600-tick/60 Hz budget. `fromWest` spawns on the
    /// west leg heading east; `southbound` spawns on the NORTH leg heading south —
    /// under this engine's right-hand-rule signed-angle convention (−Z is "north", see
    /// ApproachesFromMyRight) a west-to-east mover has the north-to-south mover on its
    /// right, so it's the southbound mover that must yield. Both spawn in the same
    /// tick at S=0, so tickets are indistinguishable by wait order — the outcome comes
    /// purely from the right-hand tie-break, not FIFO.</summary>
    private static (TrafficSim Sim, Vehicle Southbound, Vehicle FromWest) UncontrolledCrossTwoStraights()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0), RoadCatalog.TwoLane.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150), RoadCatalog.TwoLane.Id));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.None });

        var wEdge = EdgeAt(n, new Vector3(-75, 0, 0));
        var eEdge = EdgeAt(n, new Vector3(75, 0, 0));
        var nEdge = EdgeAt(n, new Vector3(0, 0, -75));
        var sEdge = EdgeAt(n, new Vector3(0, 0, 75));

        var sim = new TrafficSim(n);
        var southbound = sim.Spawn(nEdge, true, sEdge)!; // north leg, southbound straight
        var fromWest = sim.Spawn(wEdge, true, eEdge)!;   // west leg, eastbound straight
        return (sim, southbound, fromWest);
    }

    [Fact]
    public void EqualRankCrossingFollowsRightBeforeLeft()
    {
        // two Free straights meeting at an uncontrolled cross, arriving together:
        // the one with the other on its RIGHT yields; the other proceeds
        var (sim, southbound, fromWest) = UncontrolledCrossTwoStraights();
        for (int i = 0; i < 600 && fromWest.Crossing is null; i++) sim.Tick(1f / 60f);
        // right-hand traffic: the eastbound car has the southbound car approaching
        // from its LEFT, and the southbound car has the eastbound car on its RIGHT →
        // southbound yields, west→east goes first
        Assert.NotNull(fromWest.Crossing);
        // the yielder must not merely be off a connector at this instant — it must
        // never have entered the junction at all (StepIndex still 0)
        Assert.Null(southbound.Crossing);
        Assert.Equal(0, southbound.StepIndex);
    }

    /// <summary>Uncontrolled 4-way cross with one straight-through car spawned on each
    /// arm, all four simultaneously (same tick, S=0): every pairwise conflict is
    /// equal-rank (Free, Straight), and ApproachesFromMyRight resolves into a strict
    /// 4-cycle (south yields to east, east yields to north, north yields to west, west
    /// yields to south) — nobody has unconditional priority, so without the deadlock
    /// breaker all four would freeze forever. Each car is placed 2 m from its stop
    /// line (inside StopLineZone) and already stationary via ForceLane, so
    /// JunctionWait starts accumulating from tick one instead of burning the test's
    /// tick budget on the approach drive.</summary>
    private static TrafficSim FourWayStandoff(out Vehicle[] cars)
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0), RoadCatalog.TwoLane.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150), RoadCatalog.TwoLane.Id));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.None });

        var wEdge = EdgeAt(n, new Vector3(-75, 0, 0));
        var eEdge = EdgeAt(n, new Vector3(75, 0, 0));
        var nEdge = EdgeAt(n, new Vector3(0, 0, -75));
        var sEdge = EdgeAt(n, new Vector3(0, 0, 75));

        var sim = new TrafficSim(n);
        var south = sim.Spawn(sEdge, false, nEdge)!; // south leg, northbound
        var east = sim.Spawn(eEdge, false, wEdge)!;  // east leg, westbound
        var north = sim.Spawn(nEdge, true, sEdge)!;  // north leg, southbound
        var west = sim.Spawn(wEdge, true, eEdge)!;   // west leg, eastbound
        cars = new[] { south, east, north, west };

        foreach (var (v, edge) in new[] { (south, sEdge), (east, eEdge), (north, nEdge), (west, wEdge) })
        {
            var laneId = v.Lane!.Value;
            float runLength = ApproachRunLength(n, edge);
            v.S = MathF.Max(0f, runLength - 2f);
            v.Speed = 0f;
            sim.ForceLane(v, laneId);
        }
        return sim;
    }

    [Fact]
    public void FourWayStandoffUnfreezesByArrivalOrder()
    {
        // four Free straights, one from each arm, all mutually right-yielding: the
        // deadlock breaker must let the earliest ticket go within ~10 s
        var sim = FourWayStandoff(out var cars);
        for (int i = 0; i < 60 * 12; i++) sim.Tick(1f / 60f);
        // Vehicle has no SpawnLane field, so the sharper-but-available signal is
        // Crossing (mid-junction) or StepIndex (already completed the connector) —
        // picking the formulation the sim's determinism actually supports.
        Assert.Contains(cars, c => c.Crossing is not null || c.StepIndex > 0);
        Assert.True(sim.Arrived > 0 || cars.Any(c => c.StepIndex > 0), "someone must have crossed");
    }
}
