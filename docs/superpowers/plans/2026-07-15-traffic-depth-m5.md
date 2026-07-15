# Traffic Depth (M5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Conflict-point junction arbitration, movement-level priorities with a right-hand rule, assertive gap acceptance with impatience, junction speeds at road limits for straights, and two direction-asymmetric road types (one-way, 2+1) — per spec `docs/superpowers/specs/2026-07-15-traffic-depth-design.md`.

**Architecture:** All behavior keys off the lane graph (`LaneConnector`, conflict points, lane `Direction`), never off `RoadType` identity or symmetric-leg assumptions. `ConnectorBuilder` gains conflict *positions*; `JunctionArbiter` gains passed-point occupancy, movement ranks, right-hand tie-break, impatience, and a deadlock breaker; the game layer only adds paint (lane arrows, ghost direction arrows).

**Tech Stack:** C# domain (net8.0, System.Numerics, NO Godot), xUnit (net10.0), Godot 4.6.2 mono game layer.

## Global Constraints

- `src/Domain` must never reference Godot (golden rule #1).
- Per task: `dotnet test` green, `dotnet build citybuilder.sln` clean; game-layer tasks also run the matching harness. Commit per green task.
- Exact behavior constants: `ClearMargin = 0.5f` m past-point margin (plus `Vehicle.Length`); accepted gap `max(2.2, 2.8 − 0.03·waitSeconds)` s; `DeadlockBreakSec = 6f`; connector speeds Straight = `min(fromLimit, toLimit)`, Right 9, Left 10, U-turn 5 m/s; `Idm.T = 0.95f`; right-hand window: signed angle from my approach tangent to theirs in `(−150°, −30°)` (right-hand traffic, Y-up, `Tangent(0)` of each connector curve).
- Movement rank = `(RowRank, TurnRank)` lexicographic; RowRank Free = 3, Signal-green = 3, Yield = 2, Stop = 1; TurnRank Straight = 3, Right = 2, Left = 1, U-turn = 0. Yield only to strictly-higher rank, or equal rank approaching from the right.
- New types (exact): One-Way Street id 5, width 12, driving Forward at ±1.75, sidewalks ±4.75 (2.5 m), 50 km/h, MinRadius 10. Asymmetric Road 2+1 id 6, width 12, Backward at −4.25, Forward at −0.75 and +2.75 (all 3.5 m), 60 km/h, MinRadius 20.
- Stop signs still require a full stop; all-way-stop FIFO (`FifoTurn`) unchanged.
- Fixture repair rule: the `Idm.T` change and new arbitration may shift example-based expectations in `tests/Domain.Tests/Traffic/` — adjust fixture geometry/numbers, never weaken an invariant assertion. Report every touched fixture.
- Test helper `Net` lives in `tests/Domain.Tests/Network/NetworkBasicsTests.cs`; traffic tests live in `tests/Domain.Tests/Traffic/`; `InternalsVisibleTo("Domain.Tests")` is configured (internal test hooks like `ForceLane` are established practice).

---

### Task 1: Conflict points — `ConnectorBuilder.BuildConflicts` with arc distances

**Files:**
- Modify: `src/Domain/Network/ConnectorBuilder.cs:91-116` (`BuildConflicts`)
- Modify: `src/Domain/Network/Entities.cs:20` (`ConnectorConflicts` type)
- Modify: `src/Domain/Traffic/JunctionArbiter.cs:27,75` and `tests/Domain.Tests/Traffic/ArbitrationTests.cs:119` (mechanical `cp.Other` adaptation — behavior unchanged this task)
- Test: `tests/Domain.Tests/Network/ConflictSetTests.cs` (adapt + extend)

**Interfaces:**
- Produces: `public readonly record struct ConflictPoint(int Other, float SMine, float STheirs)` in `CityBuilder.Domain.Network` (declare in `ConnectorBuilder.cs`); `RoadNode.ConnectorConflicts : IReadOnlyList<ConflictPoint[]>`; `BuildConflicts(IReadOnlyList<LaneConnector>) : IReadOnlyList<ConflictPoint[]>`. Tasks 2, 4, 10 consume these exact names.

- [ ] **Step 1: Write the failing tests**

In `tests/Domain.Tests/Network/ConflictSetTests.cs`, first adapt existing assertions mechanically: iterate `foreach (var cp in node.ConnectorConflicts[i])` and use `cp.Other`; `Assert.Contains(...)` becomes `Assert.Contains(northToSouth, node.ConnectorConflicts[westToEast].Select(c => c.Other))` etc. Then append:

```csharp
[Fact]
public void ConflictPointsAreSymmetricAndOnBothCurves()
{
    var node = BuildCrossNode(); // reuse this file's existing cross fixture helper
    for (int i = 0; i < node.Connectors.Count; i++)
    foreach (var cp in node.ConnectorConflicts[i])
    {
        var mirror = node.ConnectorConflicts[cp.Other].Single(c => c.Other == i);
        Assert.Equal(cp.SMine, mirror.STheirs, 2);
        Assert.Equal(cp.STheirs, mirror.SMine, 2);
        // the stored point lies on my curve at the stored arc distance
        var myCurve = node.Connectors[i].Curve;
        var table = new ArcLengthTable(myCurve, 24);
        Assert.InRange(cp.SMine, -0.01f, table.TotalLength + 0.01f);
        if (node.Connectors[i].To != node.Connectors[cp.Other].To) // crossing, not merge
        {
            var mine = myCurve.Point(table.TAtDistance(cp.SMine));
            var theirCurve = node.Connectors[cp.Other].Curve;
            var theirTable = new ArcLengthTable(theirCurve, 24);
            var theirs = theirCurve.Point(theirTable.TAtDistance(cp.STheirs));
            Assert.True(Vector3.Distance(mine, theirs) < 0.6f,
                $"conflict point mismatch: {mine} vs {theirs}");
        }
    }
}

[Fact]
public void MergeConflictsUseCurveEnds()
{
    var node = BuildCrossNode();
    for (int i = 0; i < node.Connectors.Count; i++)
    foreach (var cp in node.ConnectorConflicts[i])
    {
        if (node.Connectors[i].To != node.Connectors[cp.Other].To)
            continue;
        Assert.Equal(new ArcLengthTable(node.Connectors[i].Curve, 24).TotalLength, cp.SMine, 1);
    }
}
```

(If the file's fixture helper has a different name, use that one — do not build a new cross.) Add `using CityBuilder.Domain.Geometry;` / `using System.Numerics;` if missing.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~ConflictSetTests" 2>&1 | tail -5`
Expected: compile error — `ConflictPoint` undefined.

- [ ] **Step 3: Implement**

In `ConnectorBuilder.cs`, above the class:

```csharp
/// <summary>A conflict between two connectors of one node: the other connector's
/// index and the arc distance to the crossing point along each curve (curve ends
/// for same-target merges).</summary>
public readonly record struct ConflictPoint(int Other, float SMine, float STheirs);
```

Replace `BuildConflicts`:

```csharp
/// <summary>Pairwise conflicts between a node's connectors — where, not just
/// whether: crossing points carry arc distances along both curves so arbitration
/// can tell "approaching my path" from "already past it". Same-target merges use
/// both curve ends. Connectors sharing the source lane are queue-ordered, not
/// conflicting.</summary>
public static IReadOnlyList<ConflictPoint[]> BuildConflicts(IReadOnlyList<LaneConnector> connectors)
{
    var tables = new ArcLengthTable[connectors.Count];
    for (int i = 0; i < connectors.Count; i++)
        tables[i] = new ArcLengthTable(connectors[i].Curve, 24);

    var sets = new List<ConflictPoint>[connectors.Count];
    for (int i = 0; i < connectors.Count; i++)
        sets[i] = new List<ConflictPoint>();

    for (int i = 0; i < connectors.Count; i++)
    for (int j = i + 1; j < connectors.Count; j++)
    {
        var a = connectors[i];
        var b = connectors[j];
        if (a.From == b.From)
            continue;
        if (a.To == b.To)
        {
            sets[i].Add(new ConflictPoint(j, tables[i].TotalLength, tables[j].TotalLength));
            sets[j].Add(new ConflictPoint(i, tables[j].TotalLength, tables[i].TotalLength));
            continue;
        }
        var hits = BezierOps.Intersections(a.Curve, b.Curve);
        if (hits.Count == 0)
            continue;
        var (t1, t2) = hits.OrderBy(h => h.t1).First(); // first crossing along my travel
        sets[i].Add(new ConflictPoint(j, tables[i].DistanceAtT(t1), tables[j].DistanceAtT(t2)));
        sets[j].Add(new ConflictPoint(i, tables[j].DistanceAtT(t2), tables[i].DistanceAtT(t1)));
    }
    return sets.Select(s => s.ToArray()).ToArray();
}
```

`Entities.cs:20`: `public IReadOnlyList<ConflictPoint[]> ConnectorConflicts { get; internal set; } = Array.Empty<ConflictPoint[]>();`

Mechanical adaptations (no behavior change this task):
- `JunctionArbiter.cs:27`: `foreach (var cp in node.ConnectorConflicts[ci]) if (_connectorVehicles[(nodeId, cp.Other)].Count > 0) return false;`
- `JunctionArbiter.cs:75`: `foreach (var cp in node.ConnectorConflicts[ci]) { var other = node.Connectors[cp.Other]; ... pc.Connector != cp.Other ...}` (rename loop var, keep logic).
- `ArbitrationTests.cs:119`: adapt the conflict lookup to `.Select(c => c.Other)` or iterate `cp.Other`.

- [ ] **Step 4: Full suite green**

Run: `dotnet test 2>&1 | tail -3` then `dotnet build citybuilder.sln 2>&1 | tail -3`
Expected: PASS / Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(domain): conflict points with arc distances (ConflictPoint)"
```

---

### Task 2: Passed-point occupancy rule + connector test hook

**Files:**
- Modify: `src/Domain/Traffic/JunctionArbiter.cs` (occupancy loop in `MayEnter`)
- Modify: `src/Domain/Traffic/TrafficSim.cs` (add internal `ForceConnector` test hook next to `ForceLane`)
- Test: `tests/Domain.Tests/Traffic/ArbitrationTests.cs` (append)

**Interfaces:**
- Consumes: Task 1 `ConflictPoint`.
- Produces: `internal void TrafficSim.ForceConnector(Vehicle v, NodeId node, int connector, float s)`; occupancy rule constant `ClearMargin = 0.5f`. Task 10's safety test relies on the rule.

- [ ] **Step 1: Write the failing test**

Append to `ArbitrationTests.cs` (reuse the file's existing helpers for building a controlled cross and spawning; follow its established pattern for placing vehicles):

```csharp
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
```

Write the `CrossWithRivalOnConflictingConnector()` helper in this test file: build a 4-way cross of TwoLane roads (200 m arms) with `Net.Commit`, spawn `entering` from the south arm going straight north and `rival` from the west arm going straight east (routes via `sim.Spawn(edge, forward, goal)`), tick until `entering` is within 10 m of its line, and identify `rivalConn` as `rival.PlannedConnector.Connector` before forcing it onto the connector. Follow the spawn/tick idioms already in this file.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~ArbitrationTests" 2>&1 | tail -5`
Expected: FAIL — `ForceConnector` undefined, and with the old rule entry stays blocked even past the point.

- [ ] **Step 3: Implement**

`TrafficSim.cs`, next to `ForceLane`:

```csharp
/// <summary>Test hook: place a vehicle on a junction connector at arc position s.</summary>
internal void ForceConnector(Vehicle v, NodeId node, int connector, float s)
{
    RemoveFromQueues(v);
    v.Lane = null;
    v.ChangeFrom = null;
    v.ChangeProgress = 0;
    v.PlannedConnector = null;
    v.Crossing = (node, connector);
    v.S = s;
    _connectorVehicles[(node, connector)].Add(v);
    SortQueue(_connectorVehicles[(node, connector)]);
}
```

`JunctionArbiter.cs` — add `private const float ClearMargin = 0.5f;` and replace the occupancy loop:

```csharp
// a conflicting occupant blocks only until its rear bumper clears our crossing point
foreach (var cp in node.ConnectorConflicts[ci])
{
    var occupants = _connectorVehicles[(nodeId, cp.Other)];
    for (int k = 0; k < occupants.Count; k++)
        if (occupants[k].S < cp.STheirs + Vehicle.Length + ClearMargin)
            return false;
}
```

- [ ] **Step 4: Full suite green** — `dotnet test 2>&1 | tail -3`. The relaxed rule may change timings in existing arbitration/motion tests; apply the fixture-repair rule from Global Constraints and report anything touched.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(domain): passed-point junction occupancy — crossed cars stop blocking"
```

---

### Task 3: Impatience — waiting timer, arrival tickets for all waits, accepted-gap curve

**Files:**
- Modify: `src/Domain/Traffic/Vehicle.cs` (add fields), `src/Domain/Traffic/TrafficSim.cs` (accumulate/reset), `src/Domain/Traffic/JunctionArbiter.cs` (`AcceptedGap`, replace `GapAcceptanceSec` uses)
- Test: `tests/Domain.Tests/Traffic/ArbitrationTests.cs` (append)

**Interfaces:**
- Consumes: Tasks 1–2.
- Produces: `Vehicle.JunctionWait : float`, `Vehicle.BlockedAtLine : bool` (per-tick flag); `private float AcceptedGap(Vehicle v) => MathF.Max(2.2f, 2.8f - 0.03f * v.JunctionWait);` — Task 4's `ConflictApproachClear` consumes `AcceptedGap`. Arrival tickets (`WaitArrivalOrder`) now stamp for ANY vehicle first blocked at its line (not just stop signs) — Task 4's deadlock breaker consumes them.

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

Helper `YieldEntryWithApproachingRival(float ttaSeconds)`: cross of TwoLane (priority, Free) × Street (minor, Yield via `ConfigureJunction` with `RoleOverrides`), `entering` spawned on the minor leg, a priority rival positioned (via ticks or `ForceLane` + setting `S`) so its distance/speed ratio ≈ `ttaSeconds` when `entering` reaches its line. Keep the rival's stream steady by re-pinning its `S` each tick if needed for the hold-assertion phase.

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter "FullyQualifiedName~ArbitrationTests" 2>&1 | tail -5`. Expected: compile error (`JunctionWait` undefined).

- [ ] **Step 3: Implement**

`Vehicle.cs` — after `WaitArrivalOrder`:

```csharp
public float JunctionWait { get; set; }       // seconds blocked at the line (impatience)
public bool BlockedAtLine { get; set; }       // set by the arbiter wall each tick
```

`TrafficSim.ComputeAccel` — in the junction-wall branch, replace the inner `if (remaining < gap)` body's surroundings so the flag and ticket stamp:

```csharp
float remaining = _runs[laneId].Length - 0.4f - v.S;
if (remaining < 40f && !MayEnter(v, pc.Node, pc.Connector))
{
    if (remaining < StopLineZone)
    {
        v.BlockedAtLine = true;
        if (v.WaitArrivalOrder == 0)
            v.WaitArrivalOrder = Time + v.Id * 1e-6f; // FIFO ticket, unique
    }
    if (remaining < gap)
    {
        gap = MathF.Max(remaining, 0.05f);
        dv = v.Speed;
    }
}
```

(`StopLineZone` is in the same partial class — `JunctionArbiter.cs:13`.) In `Tick`, before `v.Accel = ComputeAccel(v)` set `v.BlockedAtLine = false;` (clear in the same first loop), and in the second loop add `if (v.BlockedAtLine) v.JunctionWait += dt;`. In `HandleTransitions`, where connector entry already resets `HasStopped`/`WaitArrivalOrder`, also `v.JunctionWait = 0;`.

`JunctionArbiter.cs` — replace the constant use: delete `GapAcceptanceSec`, add

```csharp
private static float AcceptedGap(Vehicle v) => MathF.Max(2.2f, 2.8f - 0.03f * v.JunctionWait);
```

and thread the entering vehicle into `ConflictApproachClear(node, nodeId, ci, v, freeOnly)` so `tta < AcceptedGap(v)` replaces `tta < GapAcceptanceSec` (signature change is local to this file).

Note: `AtLineStopped` keeps its own ticket stamping for stop signs (harmless double-stamp guard: both check `== 0`).

- [ ] **Step 4: Full suite green**; repair fixtures per Global Constraints (the 4→2.8 s change will likely shift one or two arbitration expectations — geometry/timing adjustments only).

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(domain): impatience — 2.8 s accepted gap shrinking to 2.2 s while waiting"
```

---

### Task 4: Movement ranks, right-hand rule, deadlock breaker

**Files:**
- Modify: `src/Domain/Traffic/JunctionArbiter.cs` (replace `freeOnly` filter with rank logic)
- Test: `tests/Domain.Tests/Traffic/ArbitrationTests.cs` (append)

**Interfaces:**
- Consumes: Tasks 1–3 (`ConflictPoint`, `AcceptedGap`, tickets).
- Produces: rank semantics per Global Constraints. No public API change — `MayEnter` internals only.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void LeftTurnerOnPriorityRoadYieldsToOncomingStraight()
{
    // both legs Free: the left-turn movement (rank 3,1) must yield to the oncoming
    // straight (rank 3,3) approaching within the accepted gap
    var (sim, leftTurner) = PriorityLeftVsOncomingStraight(oncomingTtaSeconds: 2.0f);
    for (int i = 0; i < 120; i++) sim.Tick(1f / 60f);
    Assert.NotNull(leftTurner.Lane); // held at the line while oncoming passes
}

[Fact]
public void EqualRankCrossingFollowsRightBeforeLeft()
{
    // two Free straights meeting at an uncontrolled cross, arriving together:
    // the one with the other on its RIGHT yields; the other proceeds
    var (sim, fromSouth, fromWest) = UncontrolledCrossTwoStraights();
    for (int i = 0; i < 600 && fromWest.Crossing is null; i++) sim.Tick(1f / 60f);
    // right-hand traffic: for the northbound car the westbound rival approaches from
    // the right → southbound(north-going) yields, west→east goes first
    Assert.NotNull(fromWest.Crossing);
    Assert.Null(fromSouth.Crossing);
}

[Fact]
public void FourWayStandoffUnfreezesByArrivalOrder()
{
    // four Free straights, one from each arm, all mutually right-yielding: the
    // deadlock breaker must let the earliest ticket go within ~10 s
    var sim = FourWayStandoff(out var cars);
    for (int i = 0; i < 60 * 12; i++) sim.Tick(1f / 60f);
    Assert.Contains(cars, c => c.Crossing is not null || c.Lane != c.SpawnLane || c.StepIndex > 0);
    Assert.True(sim.Arrived > 0 || cars.Any(c => c.StepIndex > 0), "someone must have crossed");
}
```

Helpers built in-file with `Net` + `ConfigureJunction` + `sim.Spawn`; for the standoff, spawn all four simultaneously equidistant from the node (record each car's spawn lane in a local for the assertion, or simply assert `sim.Arrived > 0` after enough ticks — pick the sharper formulation that the sim's determinism supports).

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter "FullyQualifiedName~ArbitrationTests" 2>&1 | tail -5`. Expected: FAIL — today lefts on Free legs never yield (freeOnly scan skips nothing higher-ranked; `MayEnter` for Free returns true immediately), and the standoff deadlocks (well, today it doesn't deadlock because Free ignores approaches — after Task 4's rule lands the breaker is what keeps this test green; it must fail BEFORE implementation via the left-turn test at minimum).

- [ ] **Step 3: Implement**

In `JunctionArbiter.cs`:

```csharp
private const float DeadlockBreakSec = 6f;

private static (int Row, int Turn) MovementRank(LaneConnector conn) =>
    (conn.Row switch
    {
        RightOfWay.Free or RightOfWay.Signal => 3, // Signal only reaches here when green
        RightOfWay.Yield => 2,
        _ => 1,
    },
    conn.Turn switch
    {
        TurnKind.Straight => 3,
        TurnKind.Right => 2,
        TurnKind.Left => 1,
        _ => 0,
    });

/// <summary>Right-hand rule: does the other movement approach from my right?
/// Signed angle from my approach direction to theirs in (−150°, −30°).</summary>
private static bool ApproachesFromMyRight(LaneConnector mine, LaneConnector other)
{
    var m = mine.Curve.Tangent(0);
    var o = other.Curve.Tangent(0);
    float cross = m.X * o.Z - m.Z * o.X;
    float dot = m.X * o.X + m.Z * o.Z;
    float deg = MathF.Atan2(cross, dot) * 180f / MathF.PI;
    return deg > -150f && deg < -30f;
}
```

Replace `ConflictApproachClear` entirely (the `freeOnly` parameter dies):

```csharp
/// <summary>No higher-priority (or equal-priority from the right) traffic about to
/// use a conflicting connector within this driver's accepted gap. Vehicles that have
/// waited past DeadlockBreakSec ignore stationary equal-rank rivals with later
/// arrival tickets — four cars at an uncontrolled cross must never freeze.</summary>
private bool ConflictApproachClear(RoadNode node, NodeId nodeId, int ci, Vehicle me)
{
    var mine = node.Connectors[ci];
    var myRank = MovementRank(mine);
    float accepted = AcceptedGap(me);

    foreach (var cp in node.ConnectorConflicts[ci])
    {
        var other = node.Connectors[cp.Other];
        if (other.Row == RightOfWay.Signal && !IsGreen(nodeId, _lanes[other.From].Edge))
            continue; // red: that movement is not coming
        var theirRank = MovementRank(other);
        int cmp = theirRank.CompareTo(myRank);
        bool mustYield = cmp > 0 || (cmp == 0 && ApproachesFromMyRight(mine, other));
        if (!mustYield)
            continue;

        var feed = _laneVehicles[other.From];
        for (int k = 0; k < feed.Count; k++)
        {
            var rival = feed[k];
            if (rival.PlannedConnector is not { } pc || pc.Node != nodeId || pc.Connector != cp.Other)
                continue;
            float dist = _runs[other.From].Length - rival.S;
            if (dist > ApproachHorizon)
                continue;
            float tta = dist / MathF.Max(rival.Speed, 0.5f);
            if (tta >= accepted)
                continue;
            if (cmp == 0 && DeadlockBreak(me, rival, dist))
                continue; // stale standoff: earliest ticket goes
            return false;
        }
    }
    return true;
}

private static bool DeadlockBreak(Vehicle me, Vehicle rival, float rivalDistToLine)
    => me.JunctionWait > DeadlockBreakSec
       && rival.Speed < 0.5f
       && rivalDistToLine < StopLineZone + 2f
       && me.WaitArrivalOrder > 0
       && (rival.WaitArrivalOrder == 0 || me.WaitArrivalOrder < rival.WaitArrivalOrder);
```

Update `MayEnter`'s switch:

```csharp
case RightOfWay.Free:
    return ConflictApproachClear(node, nodeId, ci, v);
case RightOfWay.Signal:
    return IsGreen(nodeId, LaneRunEdge(v)) && ConflictApproachClear(node, nodeId, ci, v);
case RightOfWay.Yield:
    return ConflictApproachClear(node, nodeId, ci, v);
case RightOfWay.Stop:
    if (!AtLineStopped(v))
        return false;
    return _controls[nodeId].Mode == JunctionControlMode.AllWayStop
        ? FifoTurn(v, nodeId)
        : ConflictApproachClear(node, nodeId, ci, v);
```

(`_lanes[other.From].Edge` gives the leg edge for the green check — `_lanes` is the sim's lane cache.)

- [ ] **Step 4: Full suite green**; fixture repairs per Global Constraints (Free-leg behavior changed for turns; straight-through Free is unchanged). Expect `SignalTests`/`ArbitrationTests` timing shifts.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(domain): movement ranks, right-hand rule, deadlock breaker"
```

---

### Task 5: Assertive dynamics — connector speeds at road limits, Idm.T 0.95, direction-aware lane ordering

**Files:**
- Modify: `src/Domain/Traffic/TrafficSim.cs:207-214` (`ConnectorSpeed`), `:445-456` (`_adjacent` ordering)
- Modify: `src/Domain/Traffic/Idm.cs:8` (`T`)
- Modify: `src/Domain/Network/ConnectorBuilder.cs:50-57` (`laneRank` ordering — same latent bug)
- Test: `tests/Domain.Tests/Traffic/FollowingTests.cs`, `tests/Domain.Tests/Traffic/ArbitrationTests.cs` (append)

**Interfaces:**
- Consumes: nothing new. Produces: behavior only. Task 7's asymmetric-lane tests rely on the ordering fix.

- [ ] **Step 1: Write the failing tests**

Append to `ArbitrationTests.cs`:

```csharp
[Fact]
public void StraightConnectorFlowsAtRoadSpeed()
{
    // straight-through on a Free priority road: junction crossing must not force a
    // slowdown below the lane speed limit envelope
    var (sim, through) = PriorityStraightThroughEmptyCross();
    float minSpeed = float.MaxValue;
    bool crossed = false;
    for (int i = 0; i < 60 * 30; i++)
    {
        sim.Tick(1f / 60f);
        if (through.Crossing is not null) crossed = true;
        if (crossed || through.Crossing is not null)
            minSpeed = MathF.Min(minSpeed, through.Speed);
        if (through.StepIndex > 0) break;
    }
    // TwoLane limit 22.2 m/s: pre-change the 14 m/s cap forces braking; now the
    // vehicle should stay above 18 m/s throughout the empty junction
    Assert.True(minSpeed > 18f, $"slowed to {minSpeed:F1} m/s through an empty junction");
}
```

Append to `FollowingTests.cs` a direction-aware adjacency test (uses the internal cache via a small internal accessor — add `internal (LaneId? Left, LaneId? Right) AdjacentOf(LaneId id) => _adjacent[id];` to `TrafficSim.cs` in Step 3):

```csharp
[Fact]
public void AdjacencyOrdersBySignedOffsetPerDirection()
{
    // FourLane: forward lanes at +1.75 (left, inner) and +5.25 (right, outer);
    // backward at −1.75 (left is +offset side for backward travel? no: backward
    // travel's LEFT is the −offset… backward: left = positive offset) — assert the
    // travel-frame semantics: for each direction group, Left/Right are consistent
    // with the travel frame (right-hand traffic: overtaking lane is Left).
    var n = Net.New();
    Net.Commit(n, Net.Straight(new(0, 0, 0), new(200, 0, 0), RoadCatalog.FourLane.Id));
    var sim = new TrafficSim(n);
    var edge = n.Edges.Values.Single();
    var fwd = edge.Lanes.Where(l => l.Direction == LaneDirection.Forward
        && l.Kind == LaneKind.Driving).OrderBy(l => l.Offset).ToArray();
    // forward: lower offset = further left
    Assert.Equal(fwd[1].Id, sim.AdjacentOf(fwd[0].Id).Right);
    Assert.Equal(fwd[0].Id, sim.AdjacentOf(fwd[1].Id).Left);
    var bwd = edge.Lanes.Where(l => l.Direction == LaneDirection.Backward
        && l.Kind == LaneKind.Driving).OrderByDescending(l => l.Offset).ToArray();
    // backward: higher offset = further left in the travel frame
    Assert.Equal(bwd[1].Id, sim.AdjacentOf(bwd[0].Id).Right);
    Assert.Equal(bwd[0].Id, sim.AdjacentOf(bwd[1].Id).Left);
}
```

- [ ] **Step 2: Run to verify failure** — the adjacency test fails today for the backward group whenever signed ordering differs from `|offset|` ordering? For symmetric FourLane, `|offset|` ordering coincidentally equals travel-frame ordering — so make the test's real bite the asymmetric case: ALSO assert on a synthetic profile. Add to the same test (after the FourLane block):

```csharp
    // asymmetric profile spanning offset 0 (the case |offset| ordering gets wrong):
    // build via the 2+1 type once it exists — Task 7 extends this test. For now the
    // straight-connector test above is the failing gate.
```

Run: `dotnet test --filter "StraightConnectorFlowsAtRoadSpeed" 2>&1 | tail -5`
Expected: FAIL (14 m/s cap forces `minSpeed` ≈ 14).

- [ ] **Step 3: Implement**

`Idm.cs:8`: `public const float T = 0.95f;  // desired time headway, s (assertive)`

`TrafficSim.cs` `ConnectorSpeed`:

```csharp
/// <summary>Comfortable speed through a junction, by movement geometry. Straights
/// flow at the road's limit — priority traffic doesn't brake for junctions.</summary>
private float ConnectorSpeed((NodeId Node, int Connector) key)
{
    var conn = _network.Nodes[key.Node].Connectors[key.Connector];
    return conn.Turn switch
    {
        TurnKind.Straight => MathF.Min(_runs[conn.From].SpeedLimit, _runs[conn.To].SpeedLimit),
        TurnKind.Right => 9f,
        TurnKind.Left => 10f,
        _ => 5f, // u-turns
    };
}
```

`TrafficSim.cs` `_adjacent` ordering (replace the `OrderBy(|offset|)` line):

```csharp
var ordered = (group.Key == LaneDirection.Forward
    ? group.OrderBy(l => l.Offset)               // forward travel: left = −offset
    : group.OrderByDescending(l => l.Offset))    // backward travel: left = +offset
    .ToArray();
```

(the group is `GroupBy(l => l.Direction)` — change the lambda to keep the key available, i.e. `GroupBy(l => l.Direction)` already provides `group.Key`.) Add the `AdjacentOf` internal accessor next to `ForceLane`.

`ConnectorBuilder.cs` `laneRank` ordering — incoming lanes from one edge share a direction; replace `OrderBy(x => MathF.Abs(x.lane.Offset))` with:

```csharp
var ordered = (group.First().lane.Direction == LaneDirection.Forward
    ? group.OrderBy(x => x.lane.Offset)
    : group.OrderByDescending(x => x.lane.Offset)).ToArray();
```

- [ ] **Step 4: Full suite green** — the `Idm.T` change WILL shift following-distance expectations in `FollowingTests`/`MotionContinuityTests`; repair fixtures (timings/positions), never invariants. Report each.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(domain): straights at road speed, T=0.95, direction-aware lane ordering"
```

---

### Task 6: Catalog — One-Way Street + Asymmetric Road 2+1

**Files:**
- Modify: `src/Domain/Catalog/RoadType.cs` (two entries + `All`)
- Test: `tests/Domain.Tests/Catalog/RoadTypeLimitsTests.cs` (append)

**Interfaces:**
- Produces: `RoadCatalog.OneWay` (id 5), `RoadCatalog.Asymmetric` (id 6) — exact lane specs from Global Constraints. Also a helper later tasks use for arrows: `RoadType.ForwardCount` / `BackwardCount` computed properties (driving lanes only) and `bool IsDirectionAsymmetric => ForwardCount != BackwardCount`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NewTypesHaveExpectedLaneProfiles()
{
    Assert.Equal(2, RoadCatalog.OneWay.ForwardCount);
    Assert.Equal(0, RoadCatalog.OneWay.BackwardCount);
    Assert.True(RoadCatalog.OneWay.IsDirectionAsymmetric);
    Assert.Equal(2, RoadCatalog.Asymmetric.ForwardCount);
    Assert.Equal(1, RoadCatalog.Asymmetric.BackwardCount);
    Assert.False(RoadCatalog.TwoLane.IsDirectionAsymmetric);
    Assert.Contains(RoadCatalog.OneWay, RoadCatalog.All);
    Assert.Contains(RoadCatalog.Asymmetric, RoadCatalog.All);
    Assert.Equal(10f, RoadCatalog.OneWay.MinRadius);
    Assert.Equal(20f, RoadCatalog.Asymmetric.MinRadius);
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter "FullyQualifiedName~RoadTypeLimitsTests" 2>&1 | tail -5`. Expected: compile error.

- [ ] **Step 3: Implement**

On `RoadType`:

```csharp
public int ForwardCount => Lanes.Count(l => l.Kind == LaneKind.Driving && l.Direction == LaneDirection.Forward);
public int BackwardCount => Lanes.Count(l => l.Kind == LaneKind.Driving && l.Direction == LaneDirection.Backward);
/// <summary>Drawing direction matters for these (one-way, 2+1): ghost shows arrows.</summary>
public bool IsDirectionAsymmetric => ForwardCount != BackwardCount;
```

Catalog entries (before `All`, which gains both):

```csharp
/// <summary>Directional street: two same-way lanes between sidewalks. Drawing
/// direction = travel direction.</summary>
public static readonly RoadType OneWay = new(
    new RoadTypeId(5), "One-Way Street", 12f,
    new LaneSpec[]
    {
        new(-1.75f, LaneDirection.Forward, LaneWidth, LaneKind.Driving),
        new(+1.75f, LaneDirection.Forward, LaneWidth, LaneKind.Driving),
        new(+4.75f, LaneDirection.Forward, 2.5f, LaneKind.Sidewalk),
        new(-4.75f, LaneDirection.Backward, 2.5f, LaneKind.Sidewalk),
    },
    50f, 10f);

/// <summary>2+1 road: two forward lanes, one backward. The opposing separation
/// line sits off the geometric centerline (at −2.5 m), on purpose.</summary>
public static readonly RoadType Asymmetric = new(
    new RoadTypeId(6), "Asymmetric 2+1", 12f,
    new LaneSpec[]
    {
        new(-4.25f, LaneDirection.Backward, LaneWidth, LaneKind.Driving),
        new(-0.75f, LaneDirection.Forward, LaneWidth, LaneKind.Driving),
        new(+2.75f, LaneDirection.Forward, LaneWidth, LaneKind.Driving),
    },
    60f, 20f);
```

`All = new[] { TwoLane, FourLane, Street, Avenue, OneWay, Asymmetric };`

- [ ] **Step 4: Full suite green** (existing tests iterate `RoadCatalog.All` — e.g. `RoadTypeLimitsTests.EveryTypeHasPositiveGeometryLimits` must pass for the new entries; the toolbar picks them up automatically at the game layer, verified in Task 9's build).

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(domain): One-Way Street and Asymmetric 2+1 road types"
```

---

### Task 7: New types through the domain — lane graph, routing, spawner, adjacency

**Files:**
- Modify: `src/Domain/Traffic/TrafficSpawner.cs:31-36` (direction retry)
- Test: `tests/Domain.Tests/Traffic/RoutePlannerTests.cs`, `tests/Domain.Tests/Traffic/SpawnerTests.cs`, `tests/Domain.Tests/Traffic/FollowingTests.cs` (extend the Task-5 adjacency test), `tests/Domain.Tests/Network/LaneConnectorTests.cs` (append)

**Interfaces:**
- Consumes: Task 6 types, Task 5 ordering fix. Produces: behavior only.

- [ ] **Step 1: Write the failing tests**

`RoutePlannerTests.cs`:

```csharp
[Fact]
public void OneWayLoopRoutesOnlyWithTheFlow()
{
    // square loop of one-way edges drawn head-to-tail: A→B→C→D→A
    var n = Net.New();
    var a = new Vector3(0, 0, 0); var b = new Vector3(150, 0, 0);
    var c = new Vector3(150, 0, 150); var d = new Vector3(0, 0, 150);
    var e1 = Net.Commit(n, Net.Straight(a, b, RoadCatalog.OneWay.Id)).CreatedEdges[0];
    var e2 = Net.Commit(n, Net.Straight(b, c, RoadCatalog.OneWay.Id)).CreatedEdges[0];
    var e3 = Net.Commit(n, Net.Straight(c, d, RoadCatalog.OneWay.Id)).CreatedEdges[0];
    var e4 = Net.Commit(n, Net.Straight(d, a, RoadCatalog.OneWay.Id)).CreatedEdges[0];

    // with the flow: reachable all the way round
    Assert.NotNull(RoutePlanner.Plan(n, e1, forward: true, e4));
    // against the flow: no backward lanes exist → no plan
    Assert.Null(RoutePlanner.Plan(n, e1, forward: false, e3));
}
```

`SpawnerTests.cs`:

```csharp
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
    var sim = new TrafficSim(n, seed: 3) { TargetPopulation = 6 };
    for (int i = 0; i < 60 * 60; i++) sim.Tick(1f / 60f);
    Assert.True(sim.Vehicles.Count >= 4, $"only {sim.Vehicles.Count} spawned");
    // nothing ever drives against a one-way lane
    foreach (var v in sim.Vehicles)
        if (v.Lane is { } laneId)
            Assert.NotEqual(LaneDirection.Backward,
                n.Edges.Values.SelectMany(e => e.Lanes).First(l => l.Id == laneId) is { } lane
                    && lane.Edge == RoadCatalog.OneWay.Id ? lane.Direction : LaneDirection.Forward);
}
```

(Note: the direction assertion as sketched is awkward — implement it properly: look up the lane by id from the network, and if its edge's type is OneWay, assert the lane's `Direction == LaneDirection.Forward`. Write it as real code, not this sketch.)

`FollowingTests.cs` — extend `AdjacencyOrdersBySignedOffsetPerDirection` with the case `|offset|` ordering gets wrong:

```csharp
    // 2+1: forward lanes at −0.75 and +2.75 — |offset| ordering would call +2.75
    // "outer"… which here coincides; the REAL trap is a forward group at −4 and +1:
    // covered by the 2+1 backward/forward Left/Right semantics:
    var n2 = Net.New();
    Net.Commit(n2, Net.Straight(new(0, 0, 0), new(200, 0, 0), RoadCatalog.Asymmetric.Id));
    var sim2 = new TrafficSim(n2);
    var edge2 = n2.Edges.Values.Single();
    var f = edge2.Lanes.Where(l => l.Direction == LaneDirection.Forward).OrderBy(l => l.Offset).ToArray();
    Assert.Equal(f[1].Id, sim2.AdjacentOf(f[0].Id).Right);   // −0.75 is the LEFT forward lane
    Assert.Equal(f[0].Id, sim2.AdjacentOf(f[1].Id).Left);
    var back = edge2.Lanes.Single(l => l.Direction == LaneDirection.Backward);
    Assert.Null(sim2.AdjacentOf(back.Id).Left);
    Assert.Null(sim2.AdjacentOf(back.Id).Right);
```

`LaneConnectorTests.cs`:

```csharp
[Fact]
public void TurnLaneAssignmentOnAsymmetricApproach()
{
    // 2+1 approaching a 4-way: lefts only from the LEFT forward lane (−0.75),
    // rights only from the RIGHT forward lane (+2.75), straights from both
    var node = BuildCrossWithAsymmetricSouthArm(); // helper in this file's idiom
    var southArm = /* the 2+1 edge */;
    var leftLaneConn = node.Connectors.Where(c => IsFromLane(c, southArm, offset: -0.75f));
    var rightLaneConn = node.Connectors.Where(c => IsFromLane(c, southArm, offset: +2.75f));
    Assert.Contains(leftLaneConn, c => c.Turn == TurnKind.Left);
    Assert.DoesNotContain(rightLaneConn, c => c.Turn == TurnKind.Left);
    Assert.Contains(rightLaneConn, c => c.Turn == TurnKind.Right);
    Assert.DoesNotContain(leftLaneConn, c => c.Turn == TurnKind.Right);
    Assert.Contains(leftLaneConn, c => c.Turn == TurnKind.Straight);
    Assert.Contains(rightLaneConn, c => c.Turn == TurnKind.Straight);
}
```

(Write `BuildCrossWithAsymmetricSouthArm`/`IsFromLane` concretely following this file's existing fixture style — the south arm drawn from south toward the node so its Forward lanes arrive.)

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter "OneWayLoop|AmbientSpawnerCopes|TurnLaneAssignmentOnAsymmetric" 2>&1 | tail -5`. Expected: spawner test FAILS (direction retry missing → population starves when RNG picks backward on the one-way fringe); the others may pass already (they verify Task 5/6 work end-to-end) — that is fine, they are regression armor; at least one must fail.

- [ ] **Step 3: Implement the spawner retry**

`TrafficSpawner.cs` `SpawnerTick`:

```csharp
var from = origins[_rng.Next(origins.Count)];
var to = _allEdges[_rng.Next(_allEdges.Count)];
if (to == from)
    return;
bool fwd = _rng.Next(2) == 0;
if (Spawn(from, fwd, to) is null)
    Spawn(from, !fwd, to); // one-way fringes: only one direction has lanes
```

- [ ] **Step 4: Full suite green.**

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(domain): one-way/asymmetric types through routing, spawning, turn lanes"
```

---

### Task 8: Markings — generic layout verified + painted lane arrows for one-way types

**Files:**
- Modify: `src/Game/MeshBuilders.cs` (`BuildMarkings` + new `AddLaneArrow`)
- Test: `tests/Domain.Tests/Catalog/MarkingLayoutTests.cs` — NOTE: `MarkingLayout` lives in the GAME assembly (`MeshBuilders`), which domain tests cannot reference. Move the pure function first (see Step 1).

**Interfaces:**
- Consumes: Task 6 types.
- Produces: `CityBuilder.Domain.Catalog.MarkingRules.Layout(RoadType) : IEnumerable<(float offset, bool dashed)>` — the pure marking-layout function relocated from `MeshBuilders.MarkingLayout` (game keeps a delegating wrapper so junction-marking callers compile unchanged); `MeshBuilders` gains arrow painting for `type.BackwardCount == 0` (one-way) roads.

- [ ] **Step 1: Relocate the pure function + write the failing tests**

Create `src/Domain/Catalog/MarkingRules.cs` — move the body of `MeshBuilders.MarkingLayout` (lines 233-271) verbatim into:

```csharp
namespace CityBuilder.Domain.Catalog;

/// <summary>Paint rules from lane adjacency, valid for any lane profile — including
/// direction-asymmetric ones (the opposing boundary is wherever the innermost
/// opposing lanes meet, not offset 0). Pure domain so tests cover it headlessly.</summary>
public static class MarkingRules
{
    public const float EdgeLineInset = 0.35f; // take the existing value from MeshBuilders
    public static IEnumerable<(float Offset, bool Dashed)> Layout(RoadType type)
    {
        // …verbatim body from MeshBuilders.MarkingLayout…
    }
}
```

(Copy `EdgeLineInset`'s actual constant value from `MeshBuilders` — read it there.) In `MeshBuilders`, replace the body with a delegating call `public static IEnumerable<(float offset, bool dashed)> MarkingLayout(RoadType type) => MarkingRules.Layout(type);` so `JunctionMarkings` keeps compiling.

Create `tests/Domain.Tests/Catalog/MarkingLayoutTests.cs`:

```csharp
using CityBuilder.Domain.Catalog;
using Xunit;

namespace CityBuilder.Domain.Tests.Catalog;

public class MarkingLayoutTests
{
    [Fact]
    public void AsymmetricCenterLineSitsAtTheOpposingBoundary()
    {
        var lines = MarkingRules.Layout(RoadCatalog.Asymmetric).ToList();
        // boundary between backward (−4.25, w3.5 → edge −2.5) and forward (−0.75 →
        // edge −2.5): double solid at −2.5 ± 0.18
        Assert.Contains(lines, l => !l.Dashed && MathF.Abs(l.Offset - (-2.68f)) < 0.01f);
        Assert.Contains(lines, l => !l.Dashed && MathF.Abs(l.Offset - (-2.32f)) < 0.01f);
        // forward-forward separator dashed at +1.0
        Assert.Contains(lines, l => l.Dashed && MathF.Abs(l.Offset - 1.0f) < 0.01f);
    }

    [Fact]
    public void OneWayHasNoOpposingSeparationLine()
    {
        var lines = MarkingRules.Layout(RoadCatalog.OneWay).ToList();
        // single dashed separator between the two same-way lanes at offset 0
        Assert.Contains(lines, l => l.Dashed && MathF.Abs(l.Offset) < 0.01f);
        // and no double-solid pair anywhere
        Assert.DoesNotContain(lines, l => !l.Dashed && MathF.Abs(MathF.Abs(l.Offset) - 0.18f) < 0.05f);
    }
}
```

Check the double-solid rule in the moved code: it fires only when `driving.Length > 2` — the 2+1 has 3 driving lanes ✓; TwoLane keeps its dashed center ✓.

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter "FullyQualifiedName~MarkingLayoutTests" 2>&1 | tail -5`. Expected: compile error (`MarkingRules` missing), then green once moved — these tests primarily lock the generic rule for the new profiles.

- [ ] **Step 3: Painted arrows for one-way roads**

In `MeshBuilders.BuildMarkings`, after the line loop, when `type.BackwardCount == 0`:

```csharp
if (type.BackwardCount == 0)
    foreach (var lane in type.Lanes.Where(l => l.Kind == LaneKind.Driving))
        AddLaneArrows(st, edge, lane.Offset, tStart, tEnd);
```

```csharp
/// <summary>Forward direction arrows painted on a lane every ~30 m (one-way roads).</summary>
private static void AddLaneArrows(SurfaceTool st, RoadEdge edge, float offset, float tStart, float tEnd)
{
    float dStart = edge.ArcLength.DistanceAtT(tStart);
    float dEnd = edge.ArcLength.DistanceAtT(tEnd);
    const float spacing = 30f, shaft = 1.6f, head = 1.2f, halfW = 0.12f, headHalfW = 0.45f;
    for (float d = dStart + spacing / 2; d + shaft + head < dEnd; d += spacing)
    {
        float t0 = edge.ArcLength.TAtDistance(d);
        float t1 = edge.ArcLength.TAtDistance(d + shaft);
        float t2 = edge.ArcLength.TAtDistance(d + shaft + head);
        AddOffsetQuad(st, edge, t0, t1, offset, halfW);          // shaft
        AddOffsetTriangle(st, edge, t1, t2, offset, headHalfW);  // head
    }
}
```

Implement `AddOffsetQuad`/`AddOffsetTriangle` following `AddMarkQuad`'s exact vertex/normal/material pattern (same `MarkingY`, same `SurfaceTool` usage — read `AddMarkQuad` at `MeshBuilders.cs:310` and mirror it; the triangle uses the center point at `t2` and the two winged points at `t1` ± `headHalfW`).

- [ ] **Step 4: Verify** — `dotnet test 2>&1 | tail -3`, `dotnet build citybuilder.sln 2>&1 | tail -3`, then `CITYBUILDER_SMOKE=1 godot --headless . 2>&1 | grep SMOKE` (must stay OK). Visual check of arrows lands in Task 11's screenshot pass.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat: marking rules to domain + painted direction arrows on one-way lanes"
```

---

### Task 9: Ghost direction arrows for asymmetric types

**Files:**
- Modify: `src/Game/GhostView.cs` (`Show`)
- Test: build + harness only (game-layer visual; locked by Task 11's screenshots)

**Interfaces:**
- Consumes: Task 6 `RoadType.IsDirectionAsymmetric`; existing `GhostView` lines mesh.

- [ ] **Step 1: Implement**

In `GhostView.Show`, inside the `placement is not null` block, after the strips:

```csharp
// direction arrows: drawing an asymmetric type, show which way it will flow
var ghostType = RoadCatalog.Get(placement.Proposal.Type);
if (ghostType.IsDirectionAsymmetric)
{
    if (!anyLines)
    {
        _linesMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        anyLines = true;
    }
    foreach (var pc in placement.Proposal.Curves)
        AddGhostArrows(pc.Curve);
}
```

```csharp
private void AddGhostArrows(CityBuilder.Domain.Geometry.Bezier3 curve)
{
    var col = new Color(0.4f, 0.9f, 1f, 0.9f);
    float len = curve.Length();
    for (float d = 10f; d < len - 6f; d += 20f)
    {
        float t = d / len; // chord-parameter approximation is fine for a hint arrow
        var p = curve.Point(t).ToGodot() + Vector3.Up * 0.3f;
        var f = curve.Tangent(t).ToGodot();
        var right = new Vector3(f.Z, 0, -f.X);
        foreach (var wing in new[] { -right, right })
        {
            _linesMesh.SurfaceSetColor(col);
            _linesMesh.SurfaceAddVertex(p + f * 2.2f);
            _linesMesh.SurfaceSetColor(col);
            _linesMesh.SurfaceAddVertex(p + wing * 1.1f);
        }
        _linesMesh.SurfaceSetColor(col);
        _linesMesh.SurfaceAddVertex(p);
        _linesMesh.SurfaceSetColor(col);
        _linesMesh.SurfaceAddVertex(p + f * 2.2f);
    }
}
```

(`using CityBuilder.Domain.Catalog;` is already imported in this file.)

- [ ] **Step 2: Verify** — `dotnet build citybuilder.sln 2>&1 | tail -3` clean; `CITYBUILDER_SMOKE=1 godot --headless .` → SMOKE OK.

- [ ] **Step 3: Commit**

```bash
git add src/Game/GhostView.cs
git commit -m "feat(game): ghost direction arrows while drawing one-way/asymmetric roads"
```

---

### Task 10: Standing safety + throughput tests

**Files:**
- Test: `tests/Domain.Tests/Traffic/AssertivenessGuardTests.cs` (new)

**Interfaces:**
- Consumes: everything above. Produces: the milestone's two standing invariants.

- [ ] **Step 1: Write the safety-invariant test** (must pass immediately — it guards against over-assertiveness):

```csharp
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
    [Fact]
    public void NoTwoVehiclesEverCoOccupyAConflictPoint()
    {
        var (n, center) = BusyCross();
        var sim = new TrafficSim(n, seed: 11) { TargetPopulation = 60 };
        for (int i = 0; i < 60 * 180; i++)
        {
            sim.Tick(1f / 60f);
            AssertNoConflictPointCoOccupancy(sim, n);
        }
    }

    private static void AssertNoConflictPointCoOccupancy(TrafficSim sim, RoadNetwork n)
    {
        const float margin = 0.3f;
        foreach (var node in n.Nodes.Values)
        {
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
                    bool aAt = a.S + margin > cp.SMine && a.S - Vehicle.Length - margin < cp.SMine;
                    bool bAt = b.S + margin > cp.STheirs && b.S - Vehicle.Length - margin < cp.STheirs;
                    Assert.False(aAt && bAt,
                        $"vehicles {a.Id} and {b.Id} co-occupy a conflict point at node {node.Id}");
                }
            }
        }
    }
```

Add the internal accessor in `TrafficSim.cs` next to `ForceLane`:

```csharp
internal IReadOnlyList<Vehicle> VehiclesOnConnector(NodeId node, int connector)
    => _connectorVehicles.TryGetValue((node, connector), out var q) ? q : Array.Empty<Vehicle>();
```

`BusyCross()`: 4-way cross of TwoLane (E–W, 400 m arms) × Street (N–S, 400 m arms), `ConfigureJunction` to `PrioritySigns` with the Street legs' `RoleOverrides` = `LegRole.Yield` (follow `ArbitrationTests`' existing configure idiom).

- [ ] **Step 2: Write the throughput test with a CALIBRATION step**

```csharp
    [Fact]
    public void MinorRoadDischargesThroughPriorityStream()
    {
        var (n, _) = BusyCross();
        var sim = new TrafficSim(n, seed: 13);
        var (ew, ns) = (PriorityEdges(n), MinorEdges(n)); // helpers: pick by type

        int minorSpawned = 0, priorityPulse = 0;
        var minorIds = new HashSet<int>();
        for (int i = 0; i < 60 * 120; i++)
        {
            // steady priority stream: one car every ~2.5 s alternating directions
            if (i % 150 == 0)
            {
                var pe = ew[priorityPulse++ % ew.Count];
                sim.Spawn(pe.Edge, pe.Forward, pe.Goal);
            }
            // minor road pressure: keep a queue trying to cross/turn
            if (i % 90 == 0 && minorSpawned < 40)
            {
                var me = ns[minorSpawned % ns.Count];
                if (sim.Spawn(me.Edge, me.Forward, me.Goal) is { } v)
                {
                    minorSpawned++;
                    minorIds.Add(v.Id);
                }
            }
            sim.Tick(1f / 60f);
        }
        int minorArrived = /* count arrivals: track by watching sim.Vehicles membership
            of minorIds each tick, or simpler: minorSpawned − still-present minors */
            minorSpawned - sim.Vehicles.Count(v => minorIds.Contains(v.Id));
        Assert.True(minorArrived >= MinorDischargeFloor,
            $"only {minorArrived}/{minorSpawned} minor vehicles got through in 2 sim-minutes");
    }

    /// <summary>CALIBRATION (do in this task, document here): measured with the M5
    /// behavior on 2026-07-15: X vehicles discharge; pre-M5 (verified via
    /// `git stash` of the arbiter changes or by rerunning at the pre-M5 commit): Y.
    /// Floor = 75% of X, and it must exceed Y. Replace both numbers below.</summary>
    private const int MinorDischargeFloor = 0; // ← calibrate: (int)(0.75 * measured)
}
```

Calibration procedure (do it, then hard-code the result): run the test printing `minorArrived` (temporarily via `Assert.True(false, $"measured {minorArrived}")`), note the value with the full M5 behavior; then `git stash` nothing — instead check out the pre-M5 arbiter briefly (`git show ace37d2:src/Domain/Traffic/JunctionArbiter.cs > /tmp/old.cs`, compare by running the sim mentally is NOT acceptable — do it properly: `git worktree add /tmp/pre-m5 <commit-before-task-2>` and run the same scenario there with a copy of this test) to record the passive baseline. Set `MinorDischargeFloor = (int)(0.75 * measured)` and assert in the doc comment that the pre-M5 number is below the floor. If the pre-M5 baseline is NOT below the floor, the milestone's headline claim is false — stop and report instead of shipping the test.

- [ ] **Step 3: Run** — `dotnet test --filter "FullyQualifiedName~AssertivenessGuardTests" 2>&1 | tail -5`. Expected: PASS with the calibrated floor (and the safety invariant holding over 180 sim-seconds).

- [ ] **Step 4: Full suite + build green.**

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "test(domain): standing safety + throughput guards for assertive junctions"
```

---

### Task 11: Visual scenarios, smoke, full sweep

**Files:**
- Modify: `src/Game/VisualShots.cs` (two scenarios), `src/Game/Main.cs` (smoke: one-way leg)

**Interfaces:** none new — milestone verification.

- [ ] **Step 1: Visual scenarios** — read `VisualShots.cs` and follow its pattern:
  - `m5_new_types`: a one-way street (arrows visible) crossing a 2+1 (double-solid line off-center) at a junction; top + oblique shots.
  - `m5_congestion`: the `BusyCross` layout with ~40 ambient vehicles ticked ~30 sim-seconds so the junction is visibly busy but flowing (vehicles inside the box, no frozen queue on the minor road).

- [ ] **Step 2: Smoke** — in `Main.RunSmoke`, after the existing grid link, add a one-way segment somewhere disconnected-safe (e.g. `V(200,0)`→`V(296,0)` is taken; use a new area) and `Expect` the edge count accordingly; also `Expect(LaneGraph.IsStronglyConnected(_network), ...)` still holds — note a one-way STUB breaks strong connectivity, so add the one-way as part of a loop (three one-way edges closing a triangle with an existing two-way road), or place it and drop the strong-connectivity expectation change — DECISION: keep strong connectivity by making the one-way part of a small loop off the grid: `V(320,140)→V(400,140)` one-way, `V(400,140)→V(400,220)` one-way, `V(400,220)→V(320,140)` one-way (triangle), connected to the grid by a two-way `V(296,96)→V(320,140)`. Update the expected edge/node counts in the smoke assertions.

- [ ] **Step 3: Full sweep**

```bash
dotnet test 2>&1 | tail -3
dotnet build citybuilder.sln 2>&1 | tail -3
CITYBUILDER_SMOKE=1 godot --headless . 2>&1 | grep -E "SMOKE|FAIL"
CITYBUILDER_SHOTS=tests/visual/shots godot . 2>&1 | tail -3
CITYBUILDER_UITEST=<scratchpad>/ui-m5.png godot . 2>&1 | grep UITEST
```

READ the new screenshots (Read tool): `m5_new_types` (arrows on the one-way, off-center double line on the 2+1, clean junction paint) and `m5_congestion` (junction flowing, no frozen minor queue). Fix and re-shoot if wrong.

- [ ] **Step 4: Commit**

```bash
git add -A src tests
git commit -m "feat: M5 visual scenarios + one-way smoke loop"
```

---

### Task 12: Docs + milestone close

**Files:**
- Modify: `docs/roadmap.md`, `docs/conventions.md`, `docs/gotchas.md` (if applicable), `CLAUDE.md` (test count)

- [ ] **Step 1: Docs**
  - `docs/roadmap.md`: M5 → Done (2026-07-15): conflict-point arbitration, movement ranks + right-hand rule + deadlock breaker, impatience gap acceptance (2.8→2.2 s), straights at road speed, One-Way + Asymmetric 2+1 types, standing safety/throughput guards. Known limits: protected left phases / junction merging / signal-timing + lane-connector UI still deferred; renumber Next-up (undo/redo + upgrade tool = M6).
  - `docs/conventions.md`: new constants (accepted gap 2.8 base / 2.2 floor / 0.03 per-s, ClearMargin 0.5 m + vehicle length, DeadlockBreakSec 6, movement ranks table, right-hand window (−150°, −30°), connector speeds, Idm.T 0.95, new type profiles incl. the off-center opposing boundary).
  - `docs/gotchas.md`: direction-asymmetric lanes — never order lane groups by `|offset|`; use direction-aware signed ordering (the two fixed sites: `TrafficSim._adjacent`, `ConnectorBuilder.laneRank`).
  - `CLAUDE.md`: update the `dotnet test` count to the real number.

- [ ] **Step 2: Final verification** — full sweep again (all five commands from Task 11 Step 3); all green.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: M5 traffic depth — assertive junctions, movement priorities, one-way/2+1 roads"
```

---

## Plan self-review (done at authoring time)

- **Spec coverage:** conflict points (T1), passed-point rule (T2), impatience + 2.8/2.2 gap (T3), ranks + right-hand + deadlock breaker (T4), connector speeds + T=0.95 + signed-ordering fix in both sites (T5), new types (T6), routing/spawner/turn-lanes on new types (T7), marking generalization verified + one-way arrows (T8), ghost arrows (T9), safety + throughput standing guards with calibration (T10), visual/smoke (T11), docs (T12). Non-goals respected (no protected phases, no merging, no UI, stop signs unchanged — `AtLineStopped`/`FifoTurn` untouched).
- **Type consistency:** `ConflictPoint(Other, SMine, STheirs)` used identically in T1/T2/T10; `AcceptedGap(Vehicle)` static in T3, consumed in T4; `AdjacentOf` internal accessor introduced T5, used T5/T7; `VehiclesOnConnector` introduced T10 where used; `MarkingRules.Layout` produced/consumed within T8.
- **Known judgment points for implementers:** several tests specify intent + fixture sketch rather than final literals (helpers follow existing file idioms; the spawner-direction assertion is explicitly marked "write it as real code"); T10's floor is a mandated calibration with a stop-and-report clause; T11's smoke counts must be recomputed from the actual geometry.
