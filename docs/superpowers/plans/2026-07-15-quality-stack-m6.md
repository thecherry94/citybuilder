# Quality & Knowledge Stack (M6) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Task 12 (manual) is run by the CONTROLLER via the explain-codebase skill, not by a generic implementer subagent.

**Goal:** Gesture fuzzing over the real editor API, versioned save/load proven by round-trip fuzzing, KPI harness + per-milestone health report, living manual, and a codified definition-of-done — per spec `docs/superpowers/specs/2026-07-15-quality-stack-design.md`.

**Architecture:** One shared `NetworkInvariants` checker consumed by tests, fuzzer, and future tools; persistence as a `RoadNetwork` partial (`RestoreInto`) so views/sim resync through the existing `Changed` batch machinery; KPI scenarios as plain xUnit facts writing `docs/health/` artifacts; the manual authored by the dedicated skill.

**Tech Stack:** C# domain (net8.0, System.Numerics, System.Text.Json — NO Godot), xUnit (net10.0), Godot 4.6.2 mono game layer.

## Global Constraints

- `src/Domain` must never reference Godot. Persistence uses System.Text.Json only.
- Per task: `dotnet test` green + `dotnet build citybuilder.sln` clean; game tasks run the matching harness. Commit per green task.
- Determinism everywhere: every fuzz/KPI scenario takes an explicit seed; failures must print `(seed, action index, action tail)`.
- Exact budgets: default fuzz suite 3 seeds × 300 actions inside `dotnet test`; certification via env `CITYBUILDER_FUZZ_ACTIONS=10000` (opt-in, not in the default run). Perf ceilings: `Validate` on 500-edge network < 150 ms; `Tick` at 300 vehicles < 8 ms/tick averaged over 600 ticks.
- Save format: `SaveGame` v1, `FormatVersion = 1`; loading a HIGHER version throws `SaveFormatException`. Byte-equal contract: `Save(Load(Save(n))) == Save(n)`.
- KPI bands: default ±25% vs `docs/health/kpi-baseline.json`; perf metrics use the absolute ceilings above instead. First run bootstraps the baseline file (test passes, writes baseline, logs that it did).
- Fuzz findings protocol: small bugs are fixed in-task, each with a seed-pinned regression test in `FuzzRegressionTests`; anything architectural (fix would span subsystems or contradict a spec) STOPS with a report instead of patching.
- Fixture-repair rule as ever: adjust geometry/timing, never weaken invariant assertions; report every touched fixture.

---

### Task 1: `NetworkInvariants` — the shared checker

**Files:**
- Create: `src/Domain/Network/NetworkInvariants.cs`
- Test: `tests/Domain.Tests/Network/NetworkInvariantsTests.cs`

**Interfaces:**
- Produces (Tasks 4, 5, 6, 13 consume):

```csharp
namespace CityBuilder.Domain.Network;

public static class NetworkInvariants
{
    /// <summary>All violations in the network; empty = healthy. Messages contain
    /// ids and numbers, suitable for direct assertion output.</summary>
    public static IReadOnlyList<string> Check(RoadNetwork n);

    // pure rule helpers, individually unit-testable:
    public static void CheckEdgeGeometry(RoadEdge e, RoadType type, List<string> outViolations);      // length ≥ MinSegmentLength − 0.1, radius ≥ MinRadius − 0.1
    public static void CheckLegAngles(NodeId node, IReadOnlyList<Vector3> legDirs, List<string> o);   // no pair < MinJunctionAngleDeg − 0.5
    public static void CheckJunctionData(RoadNode node, List<string> o);                              // CutT ∈ [0,1]; ConnectorConflicts symmetric
    public static void CheckLaneCoverage(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges, List<string> o); // arriving driving lanes have ≥1 connector (nodes with ≥2 edges)
    public static void CheckStraightCapacity(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges, List<string> o); // straight source lanes ≤ receiving lanes per approach→arm
}
```

- Rule sources (copy thresholds, do not re-derive): `RoadType.MinSegmentLength/MinRadius`, `RoadNetwork.MinJunctionAngleDeg`, the leg-direction convention `e.StartNode == node.Id ? Tangent(0) : -Tangent(1)`, the capacity rule from `LaneConnectorTests.StraightCapacityInvariantAcrossMixedTypes` (this task LIFTS that test's assertion body into the checker; the test then calls the checker — one source of truth).
- Marking bounds: static per catalog type, checked once inside `Check`: every `MarkingRules.Layout(type)` offset within ±`type.Width / 2`.
- Dead-end nodes (1 edge) skip leg-angle/lane-coverage rules; degree-2 skip capacity.

- [ ] **Step 1: Write the failing tests** — three groups in `NetworkInvariantsTests.cs`:

```csharp
[Fact]
public void HealthyMixedNetworkHasNoViolations()
{
    var n = Net.New();
    Net.Commit(n, Net.Straight(new(-100, 0, 0), new(500, 0, 0), RoadCatalog.FourLane.Id));
    Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.TwoLane.Id));
    Net.Commit(n, Net.Straight(new(200, 0, -100), new(200, 0, 100), RoadCatalog.Asymmetric.Id));
    Net.Commit(n, Net.Straight(new(400, 0, -100), new(400, 0, 100), RoadCatalog.OneWay.Id));
    Assert.Empty(NetworkInvariants.Check(n));
}

[Fact]
public void LegAngleRuleFlagsSharpPairs()
{
    var o = new List<string>();
    NetworkInvariants.CheckLegAngles(new NodeId(1), new[]
    {
        new Vector3(1, 0, 0),
        Vector3.Normalize(new Vector3(1, 0, 0.2f)), // ~11° apart
    }, o);
    Assert.NotEmpty(o);
    o.Clear();
    NetworkInvariants.CheckLegAngles(new NodeId(1), new[]
    {
        new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 0, 1),
    }, o);
    Assert.Empty(o);
}

[Fact]
public void EdgeGeometryRuleFlagsShortAndTight()
{
    var o = new List<string>();
    var shortEdge = new RoadEdge(new EdgeId(1), new NodeId(1), new NodeId(2),
        Bezier3.Line(new(0, 0, 0), new(3, 0, 0)), RoadCatalog.TwoLane.Id);
    NetworkInvariants.CheckEdgeGeometry(shortEdge, RoadCatalog.TwoLane, o);
    Assert.NotEmpty(o);
}
```

(Adapt the `RoadEdge` construction to its actual constructor — read `Entities.cs`; if lanes are required, populate from the catalog the way `AddEdgeInternal` does, or add an internal test factory.) Also move `StraightCapacityInvariantAcrossMixedTypes`'s body: the test keeps its network setup but its per-node assertions become `Assert.Empty(NetworkInvariants.Check(n))`.

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter "FullyQualifiedName~NetworkInvariantsTests" 2>&1 | tail -3`. Expected: compile error.

- [ ] **Step 3: Implement** — orchestrator `Check`: iterate edges (geometry rule), nodes (leg angles from the convention above using node.EdgeSet; junction data; lane coverage; straight capacity — reuse the exact grouping logic from the lifted test), catalog marking bounds once. Rule helpers pure, appending `$"edge {e.Id.Value}: length {len:F1} < min {min}"`-style messages.

- [ ] **Step 4: Full suite green** (the lifted LaneConnector test must still pass through the checker).

- [ ] **Step 5: Commit** — `feat(domain): NetworkInvariants shared checker`

---

### Task 2: `SimInvariants.CheckBurst`

**Files:**
- Create: `src/Domain/Traffic/SimInvariants.cs`
- Test: `tests/Domain.Tests/Traffic/SimInvariantsTests.cs`

**Interfaces:**
- Produces (Tasks 5, 6 consume):

```csharp
namespace CityBuilder.Domain.Traffic;

public static class SimInvariants
{
    /// <summary>Spawn a small ambient population and tick; returns violations
    /// (exception text, follower-inside-leader, conflict-point co-occupancy).
    /// Empty = the network is drivable.</summary>
    public static IReadOnlyList<string> CheckBurst(RoadNetwork n, int seed, int ticks = 300, int population = 12);
}
```

- Internals: `new TrafficSim(n, seed) { TargetPopulation = population }`; per tick — catch any exception into a violation and stop; scan every lane/connector queue for `follower.S > leader.S - Vehicle.Length + 0.05` (post-`Tick`, i.e. after `EnforceNoPenetration` — a violation here means the failsafe itself failed); conflict-point co-occupancy exactly as `AssertivenessGuardTests.AssertNoConflictPointCoOccupancy` (LIFT that helper here; the guard test then delegates to it — one source of truth). Queue access via the existing internal `VehiclesOnConnector` + a new internal `TrafficSim.LaneQueues` enumerator if needed (`internal IEnumerable<IReadOnlyList<Vehicle>> AllQueues()`).

- [ ] **Step 1: Failing test:**

```csharp
[Fact]
public void BurstOnHealthyCrossIsClean()
{
    var n = Net.New();
    Net.Commit(n, Net.Straight(new(-200, 0, 0), new(200, 0, 0)));
    Net.Commit(n, Net.Straight(new(0, 0, -200), new(0, 0, 200)));
    Assert.Empty(SimInvariants.CheckBurst(n, seed: 5));
}

[Fact]
public void BurstSurvivesAnEmptyNetwork()
{
    Assert.Empty(SimInvariants.CheckBurst(Net.New(), seed: 5)); // no roads: nothing to do, no crash
}
```

- [ ] **Step 2: red** → **Step 3: implement** (lift + delegate as specified) → **Step 4: full suite green** → **Step 5: Commit** — `feat(domain): SimInvariants drivability burst`

---

### Task 3: Persistence — `SaveGame` v1, `Save`, `RestoreInto`

**Files:**
- Create: `src/Domain/Persistence/SaveGame.cs` (DTOs + `SaveFormatException`), `src/Domain/Persistence/SaveLoad.cs`, `src/Domain/Network/RoadNetwork.Persistence.cs` (partial: `RestoreInto`)
- Modify: `src/Domain/Network/RoadNetwork.cs:15` (`sealed class` → `sealed partial class`)
- Test: `tests/Domain.Tests/Persistence/SaveLoadTests.cs`

**Interfaces:**
- Produces (Tasks 5, 9 consume):

```csharp
namespace CityBuilder.Domain.Persistence;

public sealed class SaveFormatException(string message) : Exception(message);

// DTOs: plain ints for ids, arrays ordered by id for byte-stable output
public sealed record SaveGame(int FormatVersion, NodeDto[] Nodes, EdgeDto[] Edges,
    int NextNode, int NextEdge, int NextLane);
public sealed record NodeDto(int Id, float X, float Y, float Z, ConfigDto Config);
public sealed record ConfigDto(int Mode, float SizeOffset, RoleDto[] Roles, LegOffsetDto[] LegOffsets);
public sealed record RoleDto(int Edge, int Role);
public sealed record LegOffsetDto(int Edge, float Offset);
public sealed record EdgeDto(int Id, int Start, int End, int Type,
    float[] Curve /* 12 floats: P0..P3 */, int[] LaneIds /* catalog order */);

public static class SaveLoad
{
    public const int FormatVersion = 1;
    public static string Save(RoadNetwork n);              // stable ordering, no indentation
    public static RoadNetwork Load(string json);            // fresh network via RestoreInto
    public static void LoadInto(string json, RoadNetwork n); // clears + repopulates in ONE batch
}
```

- `RoadNetwork.RestoreInto` (in the partial, private-field access): `BeginBatch()`; remove all existing edges/nodes (via the internal removal paths so the batch records them); re-add nodes with EXACT ids + positions + pruned configs, edges with EXACT ids/curves/types and lanes reassigned VERBATIM from `LaneIds` (catalog order — same order `AddEdgeInternal` enumerates `type.Lanes`); restore `_nextNode/_nextEdge/_nextLane`; `EndBatch()` (derived data rebuilds, ONE `Changed` event). Reject ids ≥ counters with `SaveFormatException`.
- Stable serialization: nodes/edges sorted by id; `RoleDto`/`LegOffsetDto` sorted by edge id; `JsonSerializerOptions` shared static (no indent, default float handling — System.Text.Json round-trips floats exactly).

- [ ] **Step 1: Failing tests:**

```csharp
[Fact]
public void RoundTripIsByteStableAndStructurallyIdentical()
{
    var n = Net.New();
    Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), RoadCatalog.Avenue.Id));
    Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.Asymmetric.Id));
    var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
    n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.TrafficLights, SizeOffset = 2f });

    string json = SaveLoad.Save(n);
    var loaded = SaveLoad.Load(json);
    Assert.Equal(json, SaveLoad.Save(loaded)); // byte-equal

    Assert.Equal(n.Nodes.Count, loaded.Nodes.Count);
    Assert.Equal(n.Edges.Count, loaded.Edges.Count);
    foreach (var (id, e) in n.Edges)
    {
        var le = loaded.Edges[id];
        Assert.Equal(e.Type, le.Type);
        Assert.Equal(e.Lanes.Select(l => l.Id), le.Lanes.Select(l => l.Id)); // lane ids verbatim
        Assert.Equal(e.Curve.P0, le.Curve.P0);
        Assert.Equal(e.Curve.P3, le.Curve.P3);
    }
    var lnode = loaded.Nodes[node.Id];
    Assert.Equal(JunctionControlMode.TrafficLights, lnode.Config.Mode);
    Assert.Equal(node.Connectors.Count, lnode.Connectors.Count); // derived data rebuilt
}

[Fact]
public void LoadIntoReplacesInPlaceWithOneChangedEvent()
{
    var n = Net.New();
    Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
    string snapshot = SaveLoad.Save(n);
    Net.Commit(n, Net.Straight(new(0, 0, 50), new(100, 0, 50)));
    int events = 0;
    n.Changed += _ => events++;
    SaveLoad.LoadInto(snapshot, n);
    Assert.Equal(1, events);
    Assert.Single(n.Edges);
    Assert.Equal(snapshot, SaveLoad.Save(n));
}

[Fact]
public void NewerFormatVersionThrows()
{
    var n = Net.New();
    string json = SaveLoad.Save(n).Replace("\"FormatVersion\":1", "\"FormatVersion\":99");
    Assert.Throws<SaveFormatException>(() => SaveLoad.Load(json));
}
```

- [ ] **Step 2: red** → **Step 3: implement** as specified (read `RoadNetwork.cs`'s `AddNodeInternal`/`AddEdgeInternal`/`RemoveEdgeInternal` first and mirror their batch bookkeeping exactly; lanes with explicit ids need a private overload or inline construction in the partial) → **Step 4: full suite + build green** → **Step 5: Commit** — `feat(domain): versioned save/load with byte-stable round-trip`

---

### Task 4: Gesture fuzzer core

**Files:**
- Create: `tests/Domain.Tests/Fuzzing/GestureFuzzer.cs` (driver + action generator), `tests/Domain.Tests/Fuzzing/FuzzSuiteTests.cs`, `tests/Domain.Tests/Fuzzing/FuzzRegressionTests.cs` (starts empty with a doc comment)
- Test: the suite IS the test.

**Interfaces:**
- Produces (Tasks 5, 6 consume):

```csharp
namespace CityBuilder.Domain.Tests.Fuzzing;

public sealed record FuzzOptions(int Seed, int Actions, int BurstEvery = 25, int RoundTripEvery = 10);

public sealed class FuzzResult
{
    public bool Ok;
    public int FailedAtAction;          // -1 when Ok
    public string Failure = "";         // first violation or exception
    public IReadOnlyList<string> ActionTail = Array.Empty<string>(); // last ≤ 10 actions, replayable text
}

public static class GestureFuzzer
{
    public static FuzzResult Run(FuzzOptions opts);
}
```

- Driver: fresh `RoadNetwork` + `SnapEngine` + `DraftSession`; `var rng = new Random(opts.Seed)`. Per action, pick weighted:
  - draw gesture (weight 55): random `DraftMode` (Straight/QuadCurve/CubicCurve/Arc/Chain/GridStamp), random road type from `RoadCatalog.All`, `session.RoadType = type`; 2–4 clicks at points from a mixed distribution — 60% uniform in [−400, 400]², 25% within 12 m of an existing node, 15% within 8 m of an existing edge midpoint (so snapping engages); Chain mode: after the gesture, 30% chance of 1–2 continuation clicks, then `Cancel()`. If `session.State == SessionState.Adjustable` after the last click (invalid), `session.Cancel()` — an EXPECTED outcome, not a failure.
  - bulldoze (weight 20): `network.RemoveEdge` on a uniformly random edge id from `network.Edges.Keys` (skip when empty).
  - configure junction (weight 15): random node with ≥ 3 edges (skip when none): `ConfigureJunction` with random `JunctionControlMode`, random `SizeOffset` ∈ [0, 4], random per-leg `LegRole`s.
  - snap toggles (weight 5): flip a random `SnapTypes` flag on `session.EnabledSnaps`; grid cell from {4, 8, 16, 32}.
  - step-back/cancel mid-gesture (weight 5): start a gesture, 1–2 clicks, `StepBack()` then `Cancel()`.
  Each action appends a replayable line to a ring buffer, e.g. `draw Quad type=6 clicks=(12.3,0,88.1)(...)`, `bulldoze edge=42`, `configure node=7 mode=AllWayStop`.
- After EVERY action: `NetworkInvariants.Check(network)` — any violation → populate `FuzzResult` and stop. Exceptions anywhere → caught, reported the same way.
- `FuzzSuiteTests`:

```csharp
[Theory]
[InlineData(101)]
[InlineData(202)]
[InlineData(303)]
public void DefaultSweepHoldsAllInvariants(int seed)
{
    int actions = int.TryParse(Environment.GetEnvironmentVariable("CITYBUILDER_FUZZ_ACTIONS"), out var a) ? a : 300;
    var result = GestureFuzzer.Run(new FuzzOptions(seed, actions));
    Assert.True(result.Ok,
        $"seed {seed} failed at action {result.FailedAtAction}: {result.Failure}\n" +
        string.Join("\n", result.ActionTail));
}
```

- [ ] **Step 1: write driver + suite** (no meaningful red gate exists for a fuzzer — its first full run IS the gate; treat any finding as a bug to triage per the Global Constraints protocol).
- [ ] **Step 2: run the suite** — `dotnet test --filter "FullyQualifiedName~FuzzSuiteTests" 2>&1 | tail -20`. Triage every failure: reproduce with the printed seed, minimize by lowering `Actions`, fix the underlying bug (domain code), add a pinned regression to `FuzzRegressionTests` (`GestureFuzzer.Run(new FuzzOptions(seed, failingActionCount))` + a comment describing the root cause). Architectural findings: STOP and report.
- [ ] **Step 3: full suite + build green.**
- [ ] **Step 4: Commit** — `feat(test): gesture fuzzer over the real editor surface` (plus separate `fix(domain): …` commits per bug found, each with its regression pin).

---

### Task 5: Fuzzer integration — sim bursts + save round-trips

**Files:**
- Modify: `tests/Domain.Tests/Fuzzing/GestureFuzzer.cs`

**Interfaces:** consumes Task 2 `SimInvariants.CheckBurst` and Task 3 `SaveLoad`.

- [ ] **Step 1:** in the driver loop: every `BurstEvery` actions run `SimInvariants.CheckBurst(network, seed: opts.Seed ^ actionIndex, ticks: 180, population: 8)`; every `RoundTripEvery` actions assert the round-trip contract (`Save`, `Load`, `Save` byte-equal + node/edge counts match) — violations reported through the same `FuzzResult` path with a `burst:`/`roundtrip:` prefix.
- [ ] **Step 2:** run the suite; triage findings exactly as Task 4 Step 2 (expect save/load edge cases here — e.g. networks emptied by bulldozing, configs referencing pruned edges).
- [ ] **Step 3:** full suite + build green. **Step 4: Commit** — `feat(test): fuzz-integrated sim bursts and save round-trips`

---

### Task 6: Certification sweep

**Files:** none new (findings produce fixes + pins as in Task 4).

- [ ] **Step 1:** `CITYBUILDER_FUZZ_ACTIONS=10000 dotnet test --filter "FullyQualifiedName~FuzzSuiteTests" 2>&1 | tail -20` (expect minutes, not hours — if a seed takes > ~10 min, profile the driver before anything else; the sim bursts dominate, tune `BurstEvery` upward rather than shrinking coverage).
- [ ] **Step 2:** triage → fix → pin, loop until all three seeds certify clean at 10 000 actions. Record the final run's wall time in the task report.
- [ ] **Step 3: Commit** — `test: 10k-action fuzz certification` (any fixes as separate commits).

---

### Task 7: Trip stats instrumentation

**Files:**
- Modify: `src/Domain/Traffic/Vehicle.cs`, `src/Domain/Traffic/TrafficSim.cs`
- Test: `tests/Domain.Tests/Traffic/TripStatsTests.cs`

**Interfaces:**
- Produces (Task 8 consumes):

```csharp
// Vehicle additions:
public float SpawnTime { get; set; }
public float FreeFlowTime { get; set; }   // route length over limits, computed at spawn/replan
public int Stops { get; set; }            // 0.5 m/s downward crossings after having first moved
public bool HasMoved { get; set; }

// TrafficSim additions:
public sealed record TripRecord(int VehicleId, float SpawnTime, float ArrivalTime, float FreeFlowTime, int Stops);
public List<TripRecord>? TripLog { get; set; }   // null (default) = no recording, no allocation
```

- `Spawn` sets `SpawnTime = Time` and `FreeFlowTime` = Σ over route steps of lane-run length / lane speed limit (use `_runs` after lane selection; connector time ≈ 8 m / turn speed is fine — document the approximation). `Tick`: `if (v.Speed > 2f) v.HasMoved = true; if (v.HasMoved && previousSpeed >= 0.5f && v.Speed < 0.5f) v.Stops++;` (track previous speed locally in the loop — Accel and dt give it: `prev = v.Speed - v.Accel * dt` is WRONG after clamping; instead capture before the speed update). Arrival in `HandleTransitions` appends to `TripLog` when non-null.

- [ ] **Step 1: failing test:**

```csharp
[Fact]
public void TripLogRecordsDelayAndStops()
{
    var n = Net.New();
    Net.Commit(n, Net.Straight(new(0, 0, 0), new(400, 0, 0)));
    var sim = new TrafficSim(n, seed: 2) { TripLog = new() };
    var edges = n.Edges.Keys.OrderBy(e => e.Value).ToArray();
    sim.Spawn(edges[0], forward: true, edges[^1]);
    for (int i = 0; i < 60 * 60 && sim.TripLog.Count == 0; i++) sim.Tick(1f / 60f);
    var trip = Assert.Single(sim.TripLog);
    Assert.True(trip.ArrivalTime > trip.SpawnTime);
    Assert.True(trip.FreeFlowTime > 5f && trip.FreeFlowTime < trip.ArrivalTime - trip.SpawnTime + 5f);
    Assert.Equal(0, trip.Stops); // empty road, no stops
}
```

- [ ] **Step 2: red** → **Step 3: implement** → **Step 4: full green (hot path unchanged when TripLog null — verify no per-tick allocation added)** → **Step 5: Commit** — `feat(domain): per-trip stats for KPI scenarios`

---

### Task 8: KPI scenarios, baseline, health report

**Files:**
- Create: `tests/Domain.Tests/Kpi/KpiScenarios.cs` (scenario builders returning `Dictionary<string, float>`), `tests/Domain.Tests/Kpi/KpiSuiteTests.cs` (one orchestrating fact), `docs/health/` (baseline bootstrap on first run)

**Interfaces:**
- Metrics (exact keys, all float): `signal.startup_lost_s`, `signal.sat_headway_s`, `yield4.minor_delay_mean_s`, `yield4.minor_delay_p95_s`, `yield4.completed`, `grid.delay_index`, `grid.stops_per_trip`, `perf.validate500_ms`, `perf.tick300_ms`.
- Scenarios (all seeded, all pure domain):
  - `signal_discharge`: cross of TwoLane, lights via `ConfigureJunction`; pre-queue 10 vehicles on one approach (spawn + tick until queued at red); from the tick the leg turns green (`PhaseFor`), record each queued vehicle's connector-entry time; `startup_lost_s` = first entry − green onset; `sat_headway_s` = mean gap of entries 4–10.
  - `yield_4way`: the `BusyCross` layout from `AssertivenessGuardTests` (priority TwoLane × Street with explicit roles), TripLog on, 120 sim-seconds of pulsed traffic; minor-road delay = arrival − spawn − free-flow per minor trip.
  - `grid_commute`: 3×3 TwoLane grid (100 m cells), ambient `TargetPopulation = 60`, 180 sim-seconds; `delay_index` = mean(actual/free-flow) over completed trips, `stops_per_trip` = mean stops.
  - `perf`: build a 500-edge grid (reuse the fuzzer's generator or a 16×16 grid), `Stopwatch` one `Validate` of a long crossing proposal → `validate500_ms`; 300-vehicle ambient on that grid, 600 ticks, mean ms/tick → `tick300_ms`.
- `KpiSuiteTests.GenerateHealthReport` (single `[Fact]`): run all scenarios → merge metrics → locate repo root (walk up from `AppContext.BaseDirectory` to the dir containing `citybuilder.sln`) → if `docs/health/kpi-baseline.json` missing: write it from current values, write `kpi-latest.json` + `M6.md`, log bootstrap, PASS. Else: assert non-perf metrics within ±25% of baseline, perf metrics under the absolute ceilings (150 ms / 8 ms), then write `kpi-latest.json` + `M6.md` (markdown table: metric | value | baseline | Δ%).

- [ ] **Step 1:** write scenarios + suite (the fact is its own gate: first run bootstraps, second run must pass against the bootstrap — run twice).
- [ ] **Step 2:** `dotnet test --filter "FullyQualifiedName~KpiSuiteTests" 2>&1 | tail -5` twice; inspect `docs/health/M6.md` by reading it — values must be plausible (startup lost 1–4 s, sat headway 1–3 s, delay index 1.0–3.0); implausible numbers mean a scenario bug, not a tuning problem — fix the scenario.
- [ ] **Step 3:** full suite + build green. **Step 4: Commit** (including `docs/health/kpi-baseline.json`, `kpi-latest.json`, `M6.md`) — `feat(test): KPI harness + first health report`

---

### Task 9: Game quick save/load

**Files:**
- Modify: `src/Game/Main.cs` (F5/F9 handling + save path), `src/Game/Toolbar.cs` (Save/Load buttons), `src/Game/Main.cs` `RunUiTest` (save → edit → load → assert edit gone)

**Interfaces:** consumes `SaveLoad.Save/LoadInto`. Save path: `user://saves/quick.json` via `ProjectSettings.GlobalizePath` + `Directory.CreateDirectory`.

- [ ] **Step 1:** Main: `case InputEventKey { Keycode: Key.F5, Pressed: true }` → write `SaveLoad.Save(_network)` to the path + status flash; `Key.F9` → if file exists, `SaveLoad.LoadInto(text, _network)` (the `Changed` event resyncs view/sim/traffic; `_traffic.EnsureSynced()` after). Toolbar: two Buttons calling the same handlers (expose `Main.QuickSave()/QuickLoad()`).
- [ ] **Step 2:** UI test insert (after the existing drag block): `QuickSave(); HandleClickAt(build a small extra road); Expect(edge count grew); QuickLoad(); Expect(edge count back to saved);`.
- [ ] **Step 3:** `dotnet build citybuilder.sln` clean; `CITYBUILDER_SMOKE=1 godot --headless .` SMOKE OK; UI test run → UITEST OK, read the PNG.
- [ ] **Step 4: Commit** — `feat(game): quick save/load (F5/F9 + toolbar)`

---

### Task 10: Speed-heatmap visual scenario

**Files:**
- Modify: `src/Game/RoadNetworkView.cs` (small debug API: `SetEdgeTint(EdgeId, Color?)` applying a modulate/albedo override to that edge's mesh instance; `ClearTints()`), `src/Game/VisualShots.cs` (`m6_speed_heatmap` scenario)

- [ ] **Step 1:** scenario: build the 3×3 grid + a bottleneck (one Street segment replacing a TwoLane on the main through-route), run `TrafficSim` 30 sim-seconds at population 60, compute mean vehicle speed per edge (bucket `v.Lane`'s edge), tint edges lerp(red→green) by speed/limit, screenshot top-down. Follow the file's scenario pattern; note shot scenarios currently only build networks — extend the `Scenario` record with an optional post-build hook if needed (read the harness first, smallest change wins).
- [ ] **Step 2:** SHOTS run → read the heatmap PNG: bottleneck edge visibly red/orange, free edges green.
- [ ] **Step 3: Commit** — `feat(game): speed-heatmap shot scenario`

---

### Task 11: Definition-of-done + verification docs

**Files:**
- Modify: `CLAUDE.md` (golden rule 2 extension), `docs/verification.md` (new section), `docs/roadmap.md` (standing constraint note)

- [ ] **Step 1:** CLAUDE.md golden rule 2 gains: "A milestone additionally ships: fuzz suite green (extended when editor surface or invariants grew), regenerated KPI baseline + `docs/health/M<N>.md`, drift-updated manual chapters, current roadmap." `docs/verification.md`: how to run the fuzzer (default + certification env var), how findings become pinned regressions, how the KPI suite bootstraps/asserts/writes, where health reports live, manual drift procedure (one paragraph — the skill handles the mechanics).
- [ ] **Step 2:** Commit — `docs: codify quality-stack definition-of-done`

---

### Task 12: Manual bootstrap (CONTROLLER-RUN — explain-codebase skill)

**Files:** `docs/manual/` (book), created by the skill.

- [ ] **Step 1:** The controller invokes the `explain-codebase` skill (full manual authoring mode) targeting `docs/manual/` with chapters: 00 overview & reading guide, 01 geometry, 02 network & validation, 03 junctions & control, 04 lane graph & connectors (incl. capacity-aware turn lanes), 05 traffic sim (routing, IDM, ranks, impatience), 06 drafting & snapping, 07 rendering & markings, 08 persistence, glossary. Each chapter: purpose, key algorithms, invariants, tuning constants with rationale, known limits, verification pointers.
- [ ] **Step 2:** Acceptance: every chapter exists and names its invariants + constants consistently with the code (spot-check constants against source); glossary cross-references resolve; `docs/manual/README.md` indexes the book.
- [ ] **Step 3:** Commit — `docs: living system manual (M6 bootstrap)`

---

### Task 13: Milestone close

- [ ] **Step 1:** Full sweep: `dotnet test` (incl. fuzz suite + KPI suite) | `dotnet build citybuilder.sln` | smoke | shots (read new PNGs) | UI test.
- [ ] **Step 2:** Regenerate the health report (delete `kpi-latest.json`, rerun suite), commit final `docs/health/M6.md`; roadmap M6 → Done (undo/redo = M7, tuning fast-follow noted); CLAUDE.md test count updated to the real number.
- [ ] **Step 3:** Commit — `feat: M6 quality & knowledge stack — fuzzing, save/load, KPIs, manual`

---

## Plan self-review (done at authoring time)

- **Spec coverage:** invariants §1→T1, sim burst §1→T2, persistence §3→T3+T9, fuzzer §2→T4–6 (incl. findings protocol + certification), KPI+health §4→T7+T8+T10, manual §5→T12, definition-of-done §6→T11, milestone close→T13. Success criteria all mapped (10k certification T6, byte-identical T3/T5, health report T8/T13, manual T12, DoD T11).
- **Type consistency:** `NetworkInvariants.Check`, `SimInvariants.CheckBurst`, `SaveLoad.Save/Load/LoadInto`, `FuzzOptions/FuzzResult`, `TripRecord/TripLog`, metric keys — each defined once, consumed by name in later tasks.
- **Known judgment points:** RoadEdge test construction (T1 step 1 notes the read-first), Scenario post-build hook (T10 smallest-change note), fuzz findings are by design unpredictable — T4/T5/T6 carry the triage protocol instead of pretending to know the bugs in advance.
- **Sequencing note:** T4 before T5 keeps the first fuzz sweep cheap (invariants only) so early findings are attributable; sim/save integration lands once the pure-editor layer certifies at 300 actions.
