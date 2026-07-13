# Traffic Simulation Implementation Plan (M3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Vehicles that plan routes over the lane graph, follow each other (IDM), change lanes dynamically (MOBIL-lite), obey junction control incl. cycling traffic lights, replan when stuck/invalidated, and render as instanced meshes with ambient + manual spawning.

**Architecture:** Strategic layer = `RoutePlanner` A* over (edge, direction) states with movement costs. Tactical layer = per-tick IDM car following on per-lane queues, MOBIL lane changes, per-node conflict-set junction arbitration driven by `RightOfWay`, fixed-cycle signal controllers. All simulation is pure C# in `src/Domain/Traffic/`; Godot renders via MultiMesh and ticks the sim on a fixed-step accumulator.

**Tech Stack:** C# net8.0 domain (System.Numerics), Godot 4.6.2 mono view, xUnit net10.0, screenshot harness.

## Global Constraints

- Domain stays Godot-free; sim deterministic under fixed `Tick(dt)` + seeded RNG.
- 1 unit = 1 m, Y up, right-hand traffic; vehicle length 4.5 m.
- IDM: T=1.2 s, s0=2 m, a=1.5, b=2.0 m/s². MOBIL: gain>0.3 m/s², politeness 0.3, safe follower decel ≤2.5 m/s². Lane change duration 2 s. Mandatory-merge window 80 m, lock-in 25 m.
- Costs: left 4 s, right 1.5 s, u-turn 8 s; Yield 2 s, Stop 4 s, Signal 5 s.
- Signals: green 12 s, amber 3 s, all-red 1 s, two direction-clustered phases.
- Speed limits (m/s): TwoLane 22.2, FourLane 27.8, Street 13.9, Avenue 16.7.
- Hot path: no per-tick allocations; per-lane `List<Vehicle>` kept sorted by s (descending = front first).
- Verify per task: `dotnet test`; game tasks add `dotnet build citybuilder.sln`, smoke, shots/UITEST.
- Commit after every task.

---

### Task 1: RoadType.SpeedLimit

**Files:** Modify `src/Domain/Catalog/RoadType.cs`; test `tests/Domain.Tests/Catalog/RoadCatalogTests.cs` (extend).

**Interfaces:** Produces `RoadType.SpeedLimit : float` (m/s) — record gains a required positional/init member; catalog values per Global Constraints.

- [ ] Failing test: every catalog type has `SpeedLimit > 5`; `RoadCatalog.FourLane.SpeedLimit == 27.8f`.
- [ ] Add `float SpeedLimit` to the record + values (TwoLane 22.2f, FourLane 27.8f, Street 13.9f, Avenue 16.7f). Run `dotnet test` → green. Commit `feat(domain): road speed limits`.

### Task 2: Connector conflict sets

**Files:** Modify `src/Domain/Network/ConnectorBuilder.cs`, `src/Domain/Network/Entities.cs` (RoadNode), `src/Domain/Network/RoadNetwork.cs` (RebuildDerived); test `tests/Domain.Tests/Network/ConflictSetTests.cs`.

**Interfaces:** Produces `RoadNode.ConnectorConflicts : IReadOnlyList<int[]>` — parallel to `Connectors`; entry i lists indices j whose connector conflicts with i (curves intersect in XZ via `BezierOps.Intersections`, or same To-lane = merge). Symmetric. Built by `ConnectorBuilder.BuildConflicts(connectors)` called in `RebuildDerived` after connectors.

- [ ] Failing tests: 4-way cross — two crossing straight movements conflict; a right turn and the straight from the opposite leg don't; two connectors into the same target lane conflict; sets symmetric.
- [ ] Implement `BuildConflicts` (pairwise; skip pairs sharing the From lane — they're queue-ordered, not conflicts). Store on node. Run tests → green. Commit `feat(domain): junction connector conflict sets`.

### Task 3: RoutePlanner

**Files:** Create `src/Domain/Traffic/Route.cs`, `src/Domain/Traffic/RoutePlanner.cs`; test `tests/Domain.Tests/Traffic/RoutePlannerTests.cs`.

**Interfaces:** Produces `record RouteStep(EdgeId Edge, bool Forward)`, `record Route(IReadOnlyList<RouteStep> Steps)`, `RoutePlanner.Plan(RoadNetwork n, EdgeId fromEdge, bool fromForward, EdgeId toEdge) : Route?`. State = (edge, direction); neighbors = movements at the end node reachable via connectors from any lane of the current (edge, direction); step cost = edge length / SpeedLimit + turn penalty + Row delay (constants above); heuristic = straight-line distance / max speed. Deterministic tie-break by edge id.

- [ ] Failing tests: straight line beats detour; on a grid where the short path crosses a Stop-everything junction and a slightly longer one rides the priority main, planner picks the main (verify by making the short route pass an `AllWayStop` node — cost 4 s per crossing); disconnected target → null; Plan from mid-network works (replan case).
- [ ] Implement A* (priority queue over (edge, dir), closed set, parent map). Movements derived from `node.Connectors` grouped by (From-edge → To-edge); the movement's Row = max delay among its connectors (they share a leg, so identical). Run tests → green. Commit `feat(domain): route planner over the lane graph`.

### Task 4: TrafficSim core — following & route execution

**Files:** Create `src/Domain/Traffic/Vehicle.cs`, `src/Domain/Traffic/Idm.cs`, `src/Domain/Traffic/TrafficSim.cs`; test `tests/Domain.Tests/Traffic/FollowingTests.cs`.

**Interfaces:**
- `Vehicle`: `int Id`, `Route Route`, `int StepIndex`, `LaneId? Lane` (null while on a connector), `(NodeId node, int connectorIndex)? Crossing`, `float S`, `float Speed`, `const float Length = 4.5f`, lane-change fields (Task 7), `float StuckTime`. Position query helper `WorldPos(network)` for tests/view.
- `Idm.Accel(v, vLead, gap, v0)` — standard IDM; `gap = float.MaxValue` when free road.
- `TrafficSim(RoadNetwork network, int seed)`: `IReadOnlyList<Vehicle> Vehicles`, `Vehicle? Spawn(EdgeId from, bool forward, EdgeId to)` (places at s=0 on the best serving lane, returns null if no route/space), `void Tick(float dt)`, `int Arrived`. Vehicles advance s by IDM against their lane leader; at the junction cut they enter the connector chosen for the route's next movement (arbitration comes in Task 5 — for now enter when the connector's target lane entry is clear); at connector end they enter the next edge's lane; on final step they despawn at the edge end (`Arrived++`).
- Lane occupancy: `sim.LaneVehicles(LaneId)` ordered front-first; maintained incrementally.

- [ ] Failing tests: single vehicle traverses a straight two-edge road and arrives; a platoon of 5 behind a leader capped at half speed never has gap < s0/2 over 2000 ticks (no collision); vehicle queues (speed→~0) behind a stopped leader; vehicle crossing a junction follows connector then continues.
- [ ] Implement. Integration: `speed = max(0, speed + a·dt); s += speed·dt`. Leader gap across boundaries: a vehicle near the cut sees the first vehicle on its chosen connector/next lane as leader. Run tests → green. Commit `feat(domain): traffic sim core — IDM following and route execution`.

### Task 5: Junction arbitration

**Files:** Create `src/Domain/Traffic/JunctionArbiter.cs` (used by TrafficSim); test `tests/Domain.Tests/Traffic/ArbitrationTests.cs`.

**Interfaces:** `JunctionArbiter.MayEnter(sim, vehicle, node, connectorIndex, dt) : bool` — Free: conflict set clear (no vehicle on a conflicting connector); Yield: + time-gap ≥ 4 s for approaching vehicles on lanes feeding conflicting Free connectors (gap = distToCut/speed); Stop: full stop first (`vehicle.Speed < 0.1` recorded), then as Yield; all-way Stop: stop first, then FIFO by arrival order at the node (sim keeps per-node arrival queue); Signal: leg must be green (Task 6; until then a stub `ISignalState.IsGreen(node, edge) => true`).

- [ ] Failing tests: yield vehicle waits while main-road platoon passes, enters after; two all-way arrivals cross in arrival order; crossing left turns don't overlap (conflict set holds one out); stop vehicle actually reaches speed <0.1 before entering.
- [ ] Implement + wire into TrafficSim's cut-entry. Run tests → green. Commit `feat(domain): right-of-way junction arbitration`.

### Task 6: Signal controllers

**Files:** Create `src/Domain/Traffic/SignalController.cs`; modify `src/Domain/Traffic/TrafficSim.cs`; test `tests/Domain.Tests/Traffic/SignalTests.cs`.

**Interfaces:** `SignalController(RoadNode node, RoadNetwork n)`: clusters legs into 2 groups (pick the split of legs into two sets maximizing within-set |dot| of bearings — for ≤5 legs brute force); `Advance(dt)`; `Phase(EdgeId leg) : SignalPhase { Green, Amber, Red }`; cycle green 12 / amber 3 / all-red 1 alternating. `TrafficSim` creates/destroys controllers on network change for `TrafficLights` nodes and exposes `SignalPhase PhaseFor(NodeId, EdgeId)`; arbiter consults it (Amber = don't enter).

- [ ] Failing tests: 4-way lights node — N/S green while E/W red; phases alternate after 16 s; vehicle on red leg waits, proceeds on green; amber blocks entry.
- [ ] Implement + replace Task-5 stub. Run tests → green. Commit `feat(domain): fixed-cycle traffic signals`.

### Task 7: Dynamic lane changes

**Files:** Create `src/Domain/Traffic/LaneChange.cs`; modify `Vehicle` (FromLane/ToLane/Progress), `TrafficSim`; test `tests/Domain.Tests/Traffic/LaneChangeTests.cs`.

**Interfaces:** evaluated every 0.5 s per vehicle (staggered by id): discretionary — target adjacent same-direction driving lane; benefit = idmAccel(target) − idmAccel(current) > 0.3 && new-follower decel ≤ 2.5 (politeness 0.3 weighs follower loss); mandatory — if current lane's connectors don't serve the next movement and distToCut < 80: change toward nearest serving lane (safety still enforced; urgency scales accepted follower decel up to 4.0 near the junction); distToCut < 25 && in serving lane → no discretionary changes; trapped wrong-lane vehicles brake (v0 → 2 m/s) until a gap appears. During a change (2 s) the vehicle occupies both lanes (leader for both lanes' followers); lateral offset for rendering = smoothstep(progress) between lane offsets.

- [ ] Failing tests: vehicle behind a 3 m/s leader on FourLane overtakes (ends up ahead in 60 s); no overtake when target lane's follower would brake >2.5; vehicle spawned in non-turn lane merges into turn lane before the junction and completes its left turn; during a change the follower in the TARGET lane keeps ≥ s0 to the changer.
- [ ] Implement. Run tests → green. Commit `feat(domain): MOBIL-lite dynamic lane changes`.

### Task 8: Spawner, stuck replan, perf sanity

**Files:** Create `src/Domain/Traffic/TrafficSpawner.cs`; modify `TrafficSim`; test `tests/Domain.Tests/Traffic/SpawnerTests.cs`.

**Interfaces:** `sim.TargetPopulation { get; set; }` — each tick, if below target and cooldown elapsed, spawn: origin = random fringe (edge with a degree-1 node) else random edge, destination = random other edge; despawn on arrival. Seeded `Random`. Stuck: first-in-lane vehicle with Speed<0.1 for 20 s → replan from current (edge, dir); replan also when `network.Version` changed and route contains a removed edge; no route → despawn. Perf: 300 vehicles on a boulevard grid, 1000 ticks < 2 s wall clock (loose).

- [ ] Failing tests: same seed → identical vehicle positions after 500 ticks across two sims; population converges to target ±10 %; bulldozing a route edge triggers replan or despawn (no vehicle references a removed edge afterwards); perf test under budget.
- [ ] Implement. Run tests → green. Commit `feat(domain): ambient spawner, stuck replanning, perf gate`.

### Task 9: TrafficView + sim driver + toolbar row

**Files:** Create `src/Game/TrafficView.cs`; modify `src/Game/Main.cs`, `src/Game/Toolbar.cs`.

**Interfaces:** `TrafficView.Bind(TrafficSim sim, RoadNetwork network)`; MultiMeshInstance3D, box car 4.2×1.8×1.4 at lane offset + lane-change lateral lerp, yaw from curve tangent, per-instance color from an 8-color palette by vehicle id. `Main` owns `TrafficSim` (seed 1), ticks it in `_Process` with a fixed 1/60 accumulator (max 4 substeps), only when enabled. Toolbar row: "Traffic" checkbox + population HSlider 0–300 + live count label.

- [ ] Implement; verify `dotnet build citybuilder.sln` 0 errors, smoke OK (sim disabled by default in smoke unless asserted), UITEST screenshot shows the new toolbar row.
- [ ] Commit `feat(game): vehicle rendering and traffic controls`.

### Task 10: Signal lamp animation

**Files:** Create `src/Game/SignalLampView.cs`; modify `src/Game/JunctionProps.cs` (housing keeps dark placeholder discs removed), `src/Game/Main.cs`.

**Interfaces:** `SignalLampView.Bind(network, sim)`; on network change rebuilds per-lights-node lamp sets: per approach 3 small `MeshInstance3D` discs (shared quad mesh; shared LampRed/Amber/Green materials + a dark "off" material); every 0.25 s sets each disc's material by `sim.PhaseFor(node, edge)` (green phase → green lit, others dark, etc.).

- [ ] Implement; shots of `lights_cross` scenario after ticking sim show one axis green, other red (Task 12 wires ticking into scenarios).
- [ ] Commit `feat(game): animated signal lamps`.

### Task 11: Manual spawn tool

**Files:** Modify `src/Game/ToolController.cs` (ToolMode.SpawnVehicle), `src/Game/Toolbar.cs` (button "Car"), `src/Game/Main.cs` (wire sim into controller).

**Interfaces:** first click picks origin edge (FindClosestEdge, highlight), second picks destination edge → `sim.Spawn(origin, forwardTowardCursorSide, dest)`; status flash on no-route; Esc clears.

- [ ] Implement; UITEST extended: enter SpawnVehicle mode, spawn one vehicle programmatically, screenshot contains it (assert sim.Vehicles.Count).
- [ ] Commit `feat(game): manual vehicle spawn tool`.

### Task 12: Visual traffic scenarios + verification sweep

**Files:** Modify `src/Game/VisualShots.cs`, `src/Game/Main.cs` (smoke), memory notes.

**Interfaces:** Scenario gains optional `Action<RoadNetwork, TrafficSim>? Traffic` + `int WarmupTicks`; VisualShots creates a sim (seed 7), applies, ticks warmup, then shoots with a bound TrafficView + SignalLampView. New scenarios: `traffic_flow` (boulevard grid, TargetPopulation 120, 3600 warmup ticks, top+oblique), `traffic_red_light` (lights cross, spawns feeding the red axis, queue visible, low shot), `traffic_yield` (priority tee, side-road queue while main flows). Smoke: spawn 10 deterministic trips on the smoke network, tick 3000 × 1/60, expect `Arrived > 0` and every vehicle's s within lane bounds.

- [ ] Implement scenarios; run full shots; Read and fix what they reveal (queue spacing, car orientation on curves, lamp states, z-fighting).
- [ ] Full gate: `dotnet test`, build, smoke, shots, UITEST. Update memory (traffic architecture facts + verify commands). Commit `feat: traffic visual scenarios + verification sweep`.

## Self-review

- Spec coverage: speed limits (T1), conflict sets (T2), planner (T3), IDM/route execution (T4), arbitration (T5), signals (T6), lane changes (T7), spawner/replan/perf/scale posture (T8), rendering+UI (T9), lamp animation (T10), manual spawn (T11), visual/smoke verification (T12). Non-goals respected. ✓
- Placeholders: none — constants and signatures specified. ✓
- Type consistency: `RouteStep/Route/Plan(...)`, `Vehicle` fields, `PhaseFor`, `TargetPopulation` used consistently across tasks. ✓
