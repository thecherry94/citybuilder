# Traffic Simulation — Design (Milestone 3)

**Date:** 2026-07-13
**Status:** Approved (user-set scope: two-layer architecture — strategic pathfinding +
local reactive behavior; hundreds of vehicles now, architecture must not preclude
thousands; ambient spawning + manual spawn tool; dynamic lane changes.)

## Goal

Vehicles drive the road network: they plan routes over the lane graph, follow each other
(IDM), change lanes dynamically (overtaking + turn preparation), obey junction control
(priority/yield/stop/all-way/signals — lights finally cycle), replan when blocked or the
network changes, and render as instanced meshes with an ambient population slider plus a
click-to-spawn debug tool.

## Non-goals

- No parking, no buildings/destinations semantics (a trip = fringe/edge → edge), no
  pedestrians/cyclists on the sim (their lanes stay visual), no vehicle variety logic
  (one car archetype, color variation only), no save/load.
- No adaptive/actuated signal timing — fixed two-phase cycles.
- No movement-level *priority* refinement beyond conflict sets (e.g. protected left
  phases); conflicts are geometric, priority is leg-level `RightOfWay` + arrival order.

## Architecture (two layers, as agreed)

**Strategic (rare, cheap):** `RoutePlanner` — A* at the *movement* level: states are
(edge, direction); transitions are junction movements (fromEdge→toEdge groups of
connectors). Cost = edge length / speed limit + turn penalty (left 4 s, right 1.5 s,
u-turn 8 s) + control delay by the movement's `RightOfWay` (Free 0, Yield 2 s, Stop 4 s,
Signal 5 s). Routes are edge-level; lanes are chosen tactically. Replan = same A* from
the vehicle's current edge — there is no "return to old path" problem on a lane graph.

**Tactical (per tick, local):**
- *Car following:* IDM per lane queue (desired speed = road limit, T=1.2 s, s0=2 m,
  a=1.5, b=2.0 m/s²). Each vehicle sees only its leader — O(1) per vehicle.
- *Lane changes (dynamic):* MOBIL-lite. Discretionary: change if projected IDM
  acceleration gain > 0.3 m/s² and the new follower keeps decel ≤ 2.5 m/s² (politeness
  0.3). Mandatory: within 80 m of the junction cut, pressure toward a lane whose
  connectors serve the route's next movement grows with proximity; inside 25 m the
  vehicle no longer changes away from a serving lane, and if trapped in a wrong lane it
  brakes to walking speed to merge (gap forced by follower courtesy braking). A change
  takes 2 s; laterally interpolated for rendering; the vehicle occupies BOTH lanes
  (leader for both followers) until done.
- *Junction arbitration:* per-node conflict sets computed at connector build time
  (connector curves that intersect, cached). Entry rules by the connector's `Row`:
  Free enters if its conflict set is clear; Yield/Stop additionally need a time-gap ≥
  4 s on conflicting Free approaches (Stop requires a full stop first); all-way Stop is
  arrival-order FIFO; Signal requires green for the leg + clear conflict set. Vehicles
  inside a junction always finish their connector.
- *Signals:* per lights-node fixed cycle: legs clustered into two direction groups
  (best opposing-pair split by bearing); green 12 s + amber 3 s each, 1 s all-red.
  Driven by sim time, deterministic.
- *Stuck & replan:* no movement for 20 s while first-in-lane → replan from current
  edge; route references a removed edge → replan immediately (network `Changed` bumps a
  version the sim checks); no route found → despawn.

## Domain model (`src/Domain/Traffic/`)

- `RouteStep(EdgeId Edge, bool Forward)`; `Route(IReadOnlyList<RouteStep>)`.
- `RoutePlanner.Plan(network, from: (EdgeId, bool), to: EdgeId) : Route?`
- `Vehicle`: Id, Route + step index, position = lane ref (LaneId or connector at node) +
  s (m along), speed, length 4.5 m, lane-change state (fromLane, toLane, progress),
  waiting/stopped timers.
- `TrafficSim`: `Tick(float dt)` fixed-step (tests drive it manually); owns vehicles,
  per-lane ordered occupancy, per-node signal controllers, spawner hook-ins;
  `Spawn(from, to)`, `TargetPopulation` for ambient mode (seeded RNG, fringe spawn at
  dead-end edges, despawn at goal edge end). All pure C# — no Godot.
- `RoadType.SpeedLimit` (m/s): TwoLane 22.2, FourLane 27.8, Street 13.9, Avenue 16.7.
- Junction conflict sets: `RoadNode.ConnectorConflicts : IReadOnlyList<int[]>` (indices
  into `Connectors`), built with the connectors via curve-curve XZ intersection
  (`BezierOps.Intersections`), plus same-target-lane merges.

Scale posture (hundreds → thousands): no per-tick allocations on the hot path, per-lane
arrays not dictionaries, vehicles as class instances in a pooled list, conflict sets
precomputed. No premature batching beyond that.

## Game layer (`src/Game/`)

- `TrafficView` (MultiMeshInstance3D): one box-car mesh (~4.2×1.8×1.4), per-instance
  transform from lane curve + lateral lane-change offset, per-instance color from a
  small palette. Updated every frame; sim ticked at fixed 60 Hz via accumulator in
  `_Process`.
- `SignalLampView`: per lights-junction, three small emissive discs per approach as
  individual MeshInstances (shared red/amber/green materials, visibility toggled by
  phase). Static housing/pole stay in `JunctionProps` (its baked lamp discs move here).
- UI: toolbar gains a Traffic row — enable checkbox, target-population slider (0–300),
  and a "Spawn" tool mode: first click = origin edge, second = destination edge, ghost
  highlights both; the spawned vehicle gets a distinct color for tracking.
- Smoke: build network, spawn N deterministic trips, tick M steps headlessly, assert
  arrivals > 0 and no vehicle exceeds lane bounds. UITEST screenshot includes traffic.

## Testing

- Domain: planner (shortest-by-time, prefers priority road over stop-controlled route,
  unreachable → null, replan from mid-route); IDM (no rear-end collision in a braking
  platoon, queue forms behind stopped leader, gap ≥ s0); lane change (overtake happens
  and completes, mandatory merge before junction, no change when unsafe); junction
  (yield waits for gap then goes, all-way FIFO order, signal red blocks / green flows,
  conflict set blocks crossing left turns); spawner determinism (same seed → same
  positions after N ticks); stuck replan fires.
- Visual: scenarios with seeded traffic ticked N steps before the shot — `traffic_flow`
  (boulevard grid, 120 vehicles), `traffic_red_light` (queue at red), `traffic_yield`
  (side road queue while main flows). Signal lamps visibly green one axis, red other.
- Perf sanity: 300 vehicles ticked 1000 steps in a unit test under a loose time budget.

## Risks / notes

- Junction conflict sets make left-turn/oncoming interaction believable without
  movement-level phases; known gap: two Free left-turners from opposite legs block each
  other only via conflict order (no protected phase) — acceptable, documented.
- Lane-change "both lanes occupied" rule is the collision-safety cornerstone; tests
  cover it explicitly.
- Signal clustering degrades gracefully at 3-way / 5-way nodes (groups by bearing).
