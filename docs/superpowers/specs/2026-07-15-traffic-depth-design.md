# Traffic Depth — Design (Milestone 5)

**Date:** 2026-07-15
**Status:** Approved (user-set scope: junction assertiveness + movement-level
priorities + two new road types, all defined lane-graph-first so future asymmetric /
configurable intersections inherit the behavior unchanged. Protected left phases,
junction merging, and signal-timing / lane-connector editing UI stay deferred.
Undo/redo + upgrade tool becomes M6.)

## Goal

Drivers stop being passive. Vehicles take the gaps real drivers take: a car that has
already crossed your path no longer blocks you, accepted gaps shrink from 4 s to
2.8 s (and tighten further the longer a driver waits), priority traffic flows through
junctions at road speed instead of braking to 14 m/s, and left-turners yield to
oncoming straights while right-turners merge freely — per movement, not per leg.
Two new catalog road types (one-way street, asymmetric 2+1) prove the machinery
never assumes symmetric two-way roads.

## Non-goals

- No protected left-turn signal phases; the two-phase fixed cycle stays.
- No junction merging for short blocks; no signal-timing or lane-connector editing UI.
- No taper markings or dropped curbs (visual depth rides with a later pass).
- No per-edge lane overrides (CS2-style configurable intersections) — but nothing in
  this milestone may block them: all new behavior keys off `LaneConnector` /
  lane-graph data, never off `RoadType` identity or symmetric-leg assumptions.
- Stop signs still require a full stop; all-way-stop FIFO arbitration is unchanged.

## 1. Conflict-point geometry (`ConnectorBuilder`)

`RoadNode.ConnectorConflicts` today is `IReadOnlyList<int[]>` — which connectors
conflict, not where. It becomes per-connector lists of

```csharp
public readonly record struct ConflictPoint(int Other, float SMine, float STheirs);
```

with `SMine`/`STheirs` the arc-distance along each connector's curve to the crossing
point (via `BezierOps.Intersections` + `ArcLengthTable`, computed once at connector
build time). Merge conflicts (same target lane) use both curves' end points. Existing
consumers that only need the index set keep working through the `Other` field.

## 2. Arbitration v2 (`JunctionArbiter`)

**Passed-point rule** replaces "never enter while a conflicting connector is
occupied": an occupant on conflicting connector *j* blocks entry only while its rear
bumper has not cleared the crossing point — `S_j < STheirs + Vehicle.Length + 0.5 m`.
A car past your path is air.

**Movement rank** replaces the `freeOnly` filter. Each connector's rank is the pair
`(RowRank, TurnRank)` compared lexicographically:
`RowRank`: Free = 3, Signal-green = 3, Yield = 2, Stop = 1 (Signal-red never enters).
`TurnRank`: Straight = 3, Right = 2, Left = 1, U-turn = 0.
`ConflictApproachClear` scans only conflicting movements of **strictly higher rank**;
equal ranks resolve by the **right-hand rule** (yield to the movement approaching
from your right, computed from incoming-edge bearings at the node; right-hand traffic,
Y-up), and a full tie falls back to arrival order. So: priority-leg lefts yield to
oncoming priority straights; minor-leg movements yield to all priority movements
exactly as today; two Free straights at an uncontrolled crossing follow right-before-
left. Deadlock breaker: a vehicle that has waited > 6 s while every mutually-waiting
rival also waits takes its turn by arrival ticket (lowest wins), ignoring the
right-hand rule — four cars at an uncontrolled cross must never freeze.

**Gap acceptance with impatience:** base accepted gap 2.8 s (was 4 s), shrinking by
0.03 s per second waited at the line down to a 2.2 s floor (~20 s to bottom out).
Waiting time accumulates on the vehicle while it is first in lane within the
stop-line zone and resets on entry. The time-to-arrival estimate for rivals is
unchanged (`dist / max(speed, 0.5)`).

## 3. Assertive dynamics (`TrafficSim`, `Idm`)

- `ConnectorSpeed` for **straight** movements = `min(source lane limit, target lane
  limit)` — priority through-traffic no longer brakes for junctions; the 40 m
  approach envelope naturally becomes a no-op when the connector speed matches the
  lane limit. Turns: Right 9 m/s, Left 10 m/s, U-turn 5 m/s (small raises).
- IDM headway `T` 1.1 → 0.95 s for tighter queue discharge; `A` stays 2.6 m/s²,
  `B` stays 2.8 m/s².
- No explicit creep mechanic: rolling yields emerge because point-aware arbitration
  says yes earlier — vehicles that used to stop now flow through. The stop-line wall
  itself is unchanged.

## 4. New road types (`RoadCatalog` + rendering + tools)

- **One-Way Street** (id 5): width 12 m, two Forward driving lanes (offsets ±1.75),
  sidewalks ±4.75 (2.5 m), 50 km/h, MinRadius 10 — a directional Street.
- **Asymmetric Road 2+1** (id 6): width 12 m, Backward lane at −4.25, Forward lanes
  at −0.75 and +2.75 (all 3.5 m), 60 km/h, MinRadius 20. The opposing-separation
  line sits at −2.5 m — off the geometric centerline, on purpose.
- **Marking rule generalizes:** the center separation line is drawn at the boundary
  between opposing lane groups (midpoint of the innermost opposing lane edges), not
  at offset 0; a road with zero Backward lanes draws no separation line and instead
  paints direction arrows on each driving lane every ~30 m.
- **Ghost direction arrow:** while drawing a type whose lane directions are
  asymmetric (any type where Forward count ≠ Backward count), the ghost shows travel
  arrows so the player knows which way the road will flow (draw direction = Forward).
- **Lane adjacency ordering fix (latent bug the 2+1 type would expose):**
  `TrafficSim`'s `_adjacent` orders same-direction lanes by `|offset|`, which is
  wrong once a direction group spans offset 0. Order by **signed offset, direction-
  aware** (ascending for Forward = left→right in the travel frame; descending for
  Backward).
- One-way dead ends: no u-turn connector exists (nothing opposes); a routeless
  vehicle there despawns via the existing no-route path. The ambient spawner may
  spawn at a one-way fringe only in the lane direction.
- Toolbar road-type picker already iterates `RoadCatalog.All` — picks both up.

## 5. Verification

- **Safety invariant (new, standing):** over a long busy-junction run, no two
  vehicles on mutually conflicting connectors are ever both within 1 m of their
  mutual conflict point on the same tick — catches over-assertiveness forever.
- **Throughput regression (new, standing):** a scripted priority-road × minor-road
  4-way with continuous priority traffic and queued minor-road vehicles must
  discharge the minor queue at a rate calibrated during implementation to the fixed
  behavior (asserted as an absolute floor with margin; the pre-M5 behavior fails it).
- **Right-hand rule + rank tests:** left-vs-oncoming-straight yields on a Free leg;
  right-turn does not wait for a crossed-but-passed vehicle; equal-rank crossing
  follows right-before-left; deadlock breaker unfreezes a 4-way standoff.
- **Asymmetric-type tests:** lane graph / connectors / turn-lane assignment /
  adjacency on the 2+1 and one-way types (incl. the signed-offset adjacency fix);
  routing across a one-way loop; spawner behavior at one-way fringes.
- Existing motion-continuity and traffic tests stay green (IDM `T` change may shift
  example-based expectations — fix fixtures, never weaken invariants).
- Visual: congestion scenario shot at a busy yield junction; render shot of both new
  types (markings, arrows, junction paint); smoke extended with a one-way segment;
  full sweep per CLAUDE.md.

## File plan

| Area | Change |
| --- | --- |
| `Domain/Network/ConnectorBuilder` | conflict points with arc distances (`ConflictPoint`) |
| `Domain/Network/Entities` | `ConnectorConflicts` type change |
| `Domain/Traffic/JunctionArbiter` | passed-point rule, movement rank, right-hand rule, impatience, deadlock breaker |
| `Domain/Traffic/TrafficSim` | connector speeds, signed-offset adjacency, waiting timer |
| `Domain/Traffic/Idm` | `T` 0.95 |
| `Domain/Catalog/RoadType` | One-Way Street, Asymmetric 2+1 |
| `Game/MeshBuilders`/`RoadNetworkView` | opposing-boundary separation line, one-way arrows |
| `Game/GhostView` | direction arrows on asymmetric-type ghosts |
| `tests/Domain.Tests/Traffic` | safety invariant, throughput floor, rank/right-hand/deadlock, asymmetric-type coverage |
| `Game/VisualShots`, smoke | congestion + new-type scenarios |
