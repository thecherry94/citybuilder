# Chapter 03 — Junctions & Control

A road network is a graph of edges (the curves cars drive) and nodes (where edges
meet), but nodes alone don't tell you two things that matter downstream: *where does
drivable asphalt actually stop*, and *who is allowed to go first*. Those are two
separate problems solved by two separate pieces of code. `JunctionBuilder` answers
the geometry question: it looks at every edge converging on a node and decides where
each one gets cut back to make room for a junction surface, producing a polygon,
curb corners, and — critically — the cut points that `ConnectorBuilder` ([chapter 04](04-lane-graph-connectors.md))
uses as the endpoints for lane-to-lane connector curves. `JunctionControl` (with
`SignalController` for the timed case) answers the control question: given the same
node, what regime governs it — free-for-all, priority signs, all-way stop, or timed
lights — and which leg plays which role. Geometry runs first and is purely about
shape; control runs independently and is purely about right-of-way. The two only
meet downstream, in `ConnectorBuilder`, which stamps each generated connector with
the `RightOfWay` that `JunctionControl` resolved, and in the traffic sim's arbiter
([chapter 05](05-traffic-sim.md)), which reads that stamp back off at simulation time.

## At a glance

- **Source:** `JunctionBuilder.cs` (292 lines), `JunctionControl.cs` (95 lines),
  `SignalController.cs` (73 lines).
- **Supporting types:** `Entities.cs:75-103` (`JunctionSegmentKind`, `CornerZone`,
  `JunctionGeometry`), `JunctionControl.cs:6-30` (`JunctionControlMode`, `LegRole`,
  `RightOfWay`, `JunctionConfig`, `EffectiveControl`).
- **Entry points:** `JunctionBuilder.Build(node, edges)`, `JunctionControl.Resolve(node, edges)`,
  `new SignalController(node, network)` + `.Advance(dt)` / `.Phase(leg)`.
- **Called from:** `RoadNetwork.RebuildDerived` (`RoadNetwork.cs:713-721`), in that
  fixed order — geometry, then connectors, then conflicts. `ConnectorBuilder.Build`
  (`ConnectorBuilder.cs:49, 189, 228, 273-284`) calls `JunctionControl.Resolve` to
  stamp `RightOfWay` per connector. `TrafficSim`'s `JunctionArbiter` partial
  (`JunctionArbiter.cs:178-203`) owns one `SignalController` per lights node.
- **Depends on:** `RoadCatalog`/`RoadType` (`OuterHalf`/`CarriagewayHalf` drive the
  wedge solve), `Bezier3`/`ArcLengthTable` for curve offsets and parameterization.
- **Tests:** `JunctionResizeTests.cs`, `JunctionControlTests.cs`, `SignalTests.cs`,
  `Kpi/KpiScenarios.cs` (`SignalDischarge`), all under `tests/Domain.Tests/`.
- **Last verified against commit:** `a31ebfb` on 2026-07-17 (M6.5 traffic-feel pass).

## Junction geometry

`JunctionBuilder.Build` (`JunctionBuilder.cs:24`) takes one node and the edge table
and returns a `JunctionGeometry` — a per-edge cut parameter (`CutT`), a carriageway
surface polygon, a per-segment classification, and a list of raised corner zones.
Everything downstream (lane connectors, meshes, sidewalk rendering) treats `CutT` as
the boundary between "this is a road" and "this is a junction."

The node's edges are gathered into `Leg` records (`JunctionBuilder.cs:259-291`), one
per connected edge, each carrying the direction the edge leaves the node in
(`Dir`, XZ ground plane per `docs/conventions.md`), two half-widths from the road
type — `FullHalf` (`RoadType.OuterHalf`, sidewalks included) and `CwHalf`
(`RoadType.CarriagewayHalf`, paved surface only) — and whether the edge starts or
ends here (`StartsHere`, which flips which end of the arc-length table is "distance
zero from the node"). Legs are sorted by angle so adjacent array entries are
adjacent in the wedge sweep around the node.

Degenerate degrees are special-cased before any wedge solving. Degree 1
(`JunctionBuilder.cs:35-39`) is a dead end: `CutT` is distance zero from the node,
no surface polygon. Degree 2 is checked for the "continuing road" case
(`JunctionBuilder.cs:41-50`): if both legs share both half-widths and point almost
exactly opposite each other (dot ≤ −0.95), the edges are one continuous road
passing through and again get zero-cut — this is what keeps a long avenue built
from several committed segments from growing a phantom junction at every
intermediate node. Any other degree-2 node (a bend, a width change) falls through
into the general wedge solve, same as degree ≥ 3.

The general case computes two outlines from the same per-wedge solve: an *outer*
(full-width) outline used only to size the cuts, and an *inner* (carriageway-width)
outline that becomes the actual asphalt polygon. For each pair of adjacent legs
`(a, b)`, `SolveCorner` (`JunctionBuilder.cs:206-208`) intersects the two legs'
offset border lines and accepts the intersection only if it lies *ahead* of both
legs (`sa, sb ≥ 0`) — a legitimate convex corner. If that fails,
`SolveElbowOutside` (`JunctionBuilder.cs:213-214`) checks whether the same
intersection lies *behind* both legs (`sa, sb ≤ 0`): the outside of a plain
two-leg bend, where routing through the intersection point (instead of an arc)
keeps the road at constant width around the elbow instead of bulging it. If
neither holds (near-collinear legs, a width transition, or a tip narrower than
either leg), `NodeArc` (`JunctionBuilder.cs:234-254`) sweeps points around the node
between the two legs' borders, radius lerped from `radiusA` to `radiusB` — this is
what makes acute Y-junctions and width tapers work without a solved-corner case.

Once every leg's `CutDistance` clears its corner solve, the final cut per edge
(`JunctionBuilder.cs:89-104`) adds a fixed `CornerMargin` (0.5 m, so meshes don't
touch the theoretical corner point exactly) plus an authored *extra* —
`node.Config.SizeOffset` (every leg) plus a per-leg `node.Config.LegOffsets[edgeId]`
override, clamped to `[-(CornerMargin - 0.05), MaxExtra]` ≈ `[-0.45, 12]` m — then
clamps against `MaxCutFraction` (30% of the edge's own length), so a short edge can
never be entirely swallowed by its own junction. If the 30% clamp bites, the edge is
added to `TightCuts`; geometry stays sound (cuts never overlap) but callers (per the
`JunctionGeometry` doc comment, `Entities.cs:82-89`) should skip junction paint
there since the surface won't fully cover the solved wedge. Shrinking
(`SizeOffset < 0`) can only claw back down to just above the solved corner
requirement (`JunctionResizeTests.ShrinkFloorsAtSolvedCorner`), never fold the
geometry inside out.

The carriageway polygon (`JunctionBuilder.cs:106-163`) is built wedge by wedge: a
cut cross-section point on leg `a`, then a corner run (the rounded quadratic-bezier
curb return through the solved corner, `RoundCorner` at `JunctionBuilder.cs:192-204`,
or the raw `NodeArc`/elbow points) to leg `b`'s cross-section. In parallel, each
wedge is checked (`HasBand`, `JunctionBuilder.cs:175-188`) for whether the outer and
inner outlines diverge enough to need a raised sidewalk piece; if so, a `CornerZone`
ring is emitted (inner points forward, then outer points walked backward, per
`Entities.cs:77-80`) and the segment is classified `Curbed`; otherwise `Open` (a
bare boundary, e.g. two carriageway-only country roads meeting). `Cut` segments are
the cross-sections themselves — this `JunctionSegmentKind` classification
(`Entities.cs:75`) lets the mesh/render layer know which polygon edges need a
ground skirt versus a curb mesh versus a plain cross-section, without re-deriving
any wedge logic.

Everything `ConnectorBuilder` needs comes out of `CutT`: it reads
`node.Junction.CutT[edgeId]` as the curve parameter to start each lane connector
from (`ConnectorBuilder.cs:31-32`), falling back to `0`/`1` if a degenerate node
has no entry. This is why `RoadNetwork.RebuildDerived` (`RoadNetwork.cs:713-721`)
rebuilds junction geometry strictly before connectors — the comment there says it
outright: "connectors start at the junction cuts."

## Control resolution

`JunctionControl.Resolve` (`JunctionControl.cs:42-63`) takes the same node/edges pair
and answers a different question: not where the asphalt stops, but who yields to
whom. The result is an `EffectiveControl` — a concrete `JunctionControlMode` (never
`Auto`) plus one `LegRole` per connected edge.

Node degree gates everything first: degree ≤ 2 is always resolved to `None`
(`JunctionControl.cs:45-47`) regardless of what's stored in `node.Config.Mode` — a
bend or a dead end has no meaningful contest for right-of-way, so even an
explicitly-authored `TrafficLights` mode is ignored there
(`JunctionControlTests.BendResolvesToNone`). This is a pure degree check, not a road
class check, but note it composes with `JunctionBuilder`'s degree-2 continuing-road
case: a real 2-leg bend still gets `None` control even though it does get a wedge
solve for geometry (a bend has curb corners; it doesn't have a stop line).

For degree ≥ 3, `node.Config.Mode` is read. `Auto` resolves to `PrioritySigns`
(`JunctionControl.cs:49-51`); any other explicit mode — `None`, `AllWayStop`,
`TrafficLights` — passes straight through and every leg gets `LegRole.Main`
uniformly (`JunctionControl.cs:52-53`), since role only matters for `PrioritySigns`
(`AllWayStop`/`TrafficLights` derive right-of-way from mode alone downstream, not
role — see `ConnectorBuilder.RowFor` below).

Only in the `PrioritySigns` branch does role assignment happen. `MainPair`
(`JunctionControl.cs:68-87`) picks the two legs that become the main road by
scanning every unordered pair and scoring on `(width, straightness)`: width is the
*sum* of the two legs' `RoadType.OuterHalf` (so sidewalk corridor counts, not just
bare carriageway — `JunctionControlTests.AutoPicksWiderRoadAsMain`: a Street with
sidewalks outranks a wider-carriageway TwoLane country road), and straightness is
the negative dot product of the two legs' leaving directions (opposite legs,
dot ≈ −1, score ≈ +1 — the straightest continuation). Width wins outright above a
0.01 m tolerance; within it, straightness breaks the tie
(`JunctionControlTests.AutoPicksStraightPairAsMainInTee` picks the through-street
over the perpendicular stub at a T). Every leg outside the winning pair becomes
`LegRole.Yield`; the pair itself is `Main`.

`node.Config.RoleOverrides` is then applied on top (`JunctionControl.cs:59-61`):
any entry whose edge id is still present in `roles` overwrites the computed role —
how a player forces a leg to `Stop` even though it would otherwise compute as `Main`
(`JunctionControlTests.RoleOverrideWins`). Entries for edges no longer on the node
are ignored at resolve time (`roles.ContainsKey(eid)` guards the write) — but
they're also actively pruned earlier, not just ignored. `RoadNetwork.Prune`
(`RoadNetwork.cs:700-711`) strips `RoleOverrides`/`LegOffsets` keys referencing
edges no longer in `node.EdgeSet`, running both inside `ConfigureJunction`
(`RoadNetwork.cs:691`) and defensively at the top of every `RebuildDerived`
(`RoadNetwork.cs:717`) — so even a config with stale keys set directly (bypassing
`ConfigureJunction`) gets cleaned at the next topology-triggered rebuild. This is
why `JunctionControlTests.BulldozingALegPrunesItsOverride` observes an empty
`RoleOverrides` immediately after `RemoveEdge`.

`JunctionConfig` (`JunctionControl.cs:19-30`) is the authored, persisted input
(`Mode`, `RoleOverrides`, `SizeOffset`, `LegOffsets`); `EffectiveControl`
(`JunctionControl.cs:33-35`) is the resolved, always-concrete output that
`ConnectorBuilder` and the traffic sim consume. Keeping them separate types (rather
than mutating `JunctionConfig` in place) makes `Resolve` a pure function of
`(node, edges)`, safe to call repeatedly — both `ConnectorBuilder.Build`
(`ConnectorBuilder.cs:49`) and `JunctionArbiter.SyncSignals`
(`JunctionArbiter.cs:200-202`) call it independently on every rebuild rather than
sharing a cached result.

`ConnectorBuilder.RowFor` (`ConnectorBuilder.cs:273-286`) is where `EffectiveControl`
turns into the `RightOfWay` stamped onto each connector: `AllWayStop` → every
connector `Stop`; `TrafficLights` → every connector `Signal`; `PrioritySigns` →
per-leg role (`Yield`/`Stop`/else `Free`); anything else (`None`) → `Free`. Role only
ever matters through this `PrioritySigns` case — an `AllWayStop` node's `EffectiveControl.Roles`
dictionary is populated (all `Main`, from `JunctionControl.cs:53`) but `RowFor` never
reads it for that mode.

## Signals

`SignalController` (`SignalController.cs:11-73`) is a fixed-cycle two-phase signal
for exactly one traffic-lights node. It doesn't decide *whether* a node has lights —
that's `JunctionControlMode.TrafficLights` from control resolution — it only decides
*when* each leg gets to go, once a node is already in that mode.

The constructor (`SignalController.cs:19-48`) partitions the node's legs into two
opposing groups by brute-force search over every 2-partition (`1 << legs.Length`
masks, leg 0 pinned to group 0 to fold symmetric partitions together): a
partition's score is the sum of `|dot(dir_i, dir_j)|` for every pair of legs in the
same group, rewarding grouping legs that point along the same line (opposing
approaches of a straight cross) and penalizing splitting them. The highest-scoring
partition that uses both groups (`hasZero && hasOne`) wins — for a plain 4-way
perpendicular cross this reliably produces {north, south} vs {east, west}.
`[UNCERTAIN]` for an odd leg (a 3-way lights node or a 5-way node), which leg ends
up sharing a group with which opposing leg is whatever the score function picks;
no test exercises a non-4-way lights node.

Each group cycles `Green` (`GreenSec` = 12 s) → `Amber` (`AmberSec` = 3 s) → `Red`
for the rest of the cycle, the two groups offset by exactly `HalfCycle`
(`GreenSec + AmberSec + AllRedSec` = 16 s), so group 1 is red for the first 16 s
while group 0 runs green+amber+all-red, then they swap (`SignalController.cs:14, 50-65`).
The trailing `AllRedSec` (1 s) is folded into `HalfCycle` but never produces a
distinct phase from `Phase` — it's the tail of `Red` (anything past
`GreenSec + AmberSec`), so its only visible effect is delaying the *other* group's
green by that extra second: a clearance gap baked into the offset, not a phase a
leg is ever reported as being "in." `Advance(dt)` accumulates sim time mod
`2*HalfCycle`; `Phase(leg)` (`SignalController.cs:52-65`) offsets the clock by the
leg's group and classifies the result. A leg not in `_group` returns `Red`.

`SignalController` is a pure phase clock — it has no idea what "go" means for a
particular connector. That contract is consumed one level up in the traffic sim's
arbiter (`src/Domain/Traffic/JunctionArbiter.cs`, chapter 05's territory): `IsGreen`
(`JunctionArbiter.cs:180-181`) treats a node with no registered `SignalController`
as always green, and `MayEnter`'s `RightOfWay.Signal` case (`JunctionArbiter.cs:41-42`)
requires both `IsGreen` *and* the same conflict-approach-clear check every other
right-of-way class uses — a green light doesn't exempt a vehicle from checking
conflicting occupants already committed to the junction. `MovementRank`
(`JunctionArbiter.cs:83-96`) folds `Signal` into the same top tier as `Free`
(rank 3, "Signal only reaches here when green") since the caller has already
gated on `IsGreen`. `JunctionArbiter.SyncSignals` (`JunctionArbiter.cs:195-203`) is
the lifecycle glue: removes a node's `SignalController` the moment its mode stops
being `TrafficLights`, lazily creates one when a node becomes lights-controlled,
and never touches the `_t` clock of a controller already running — unrelated
config edits on a lights node don't reset its phase mid-cycle.

## Worked example

Take a plain 4-way cross: a TwoLane road running east–west through a Street running
north–south (mirroring `JunctionControlTests.Cross`, extended with lights). The node
has degree 4, so `JunctionControl.Resolve` skips the `None` fast path. Suppose
`node.Config.Mode = TrafficLights`: `Resolve` short-circuits before `MainPair` even
runs (`JunctionControl.cs:52-53`) and returns `EffectiveControl(TrafficLights, {all
four edges: Main})` — role is computed but never consulted downstream for this mode.

`ConnectorBuilder.RowFor` then stamps every connector on this node `RightOfWay.Signal`,
regardless of which leg it enters from (`ConnectorBuilder.cs:277`).
`JunctionArbiter.SyncSignals` notices the mode and constructs a `SignalController`
(`JunctionArbiter.cs:200-202`). The four legs are two collinear pairs
(east–west and north–south, `dot ≈ ±1` within each pair, `dot ≈ 0` across pairs), so
the partition search finds {east, west} and {north, south} as the maximum-scoring
split — any partition mixing an E–W leg with an N–S leg scores near `0` on that
cross-pair term.

At sim time `t = 0` (arbitrary phase origin, `_t = 0` on construction), group 0
(say, east–west) is `Green` for `[0, 12)`, `Amber` for `[12, 15)`, `Red` for
`[15, 32)` before repeating; group 1 (north–south) is offset by `HalfCycle = 16`:
`Red` for `[0, 16)`, `Green` for `[16, 28)`, `Amber` for `[28, 31)`, `Red` again for
`[31, 32)`. So at `t = 5`: east/west green, north/south red — a vehicle on the
north leg's connector must wait; `MayEnter` returns `false` via `IsGreen` failing,
regardless of how clear the conflicting connectors are. At `t = 20`: north/south
green, east/west red — the reverse. There is exactly a 1-second window each
half-cycle (the tail of `Red` right before the *other* group's `Green` starts)
where both groups are `Red` simultaneously — the all-red clearance interval —
during which every vehicle waits regardless of leg.

## Invariants

- `CutT` is always within the edge's own `[0, 1]` parameter range and the cut
  distance is always ≤ `30%` of the edge's arc length (`MaxCutFraction`,
  `JunctionBuilder.cs:100`), so two junctions on the same short edge can never
  overlap and swallow the whole edge.
- Shrinking a junction (`SizeOffset < 0`) can reduce a cut only down to just above
  the geometrically solved corner requirement — never enough to fold or self-intersect
  the surface polygon (`JunctionResizeTests.ShrinkFloorsAtSolvedCorner`).
- `JunctionControl.Resolve` is a pure function: same `(node, edges)` in, same
  `EffectiveControl` out, no mutation. Degree ≤ 2 always resolves to `None`,
  independent of `node.Config.Mode`.
- `RoleOverrides`/`LegOffsets` keys are always a subset of `node.EdgeSet` after any
  `ConfigureJunction` call or topology-changing edit — `RoadNetwork.Prune` runs on
  every path that could leave them stale (`RoadNetwork.cs:691, 717`).
- A `SignalController`'s two direction groups always partition *all* of the node's
  legs (every leg is in exactly one group; the search rejects any partition that
  doesn't use both groups when there's more than one leg), so `Phase(leg)` is
  well-defined for every leg the controller was built with.
- A node's `SignalController` phase clock (`_t`) persists across unrelated config
  edits and is only discarded when the node stops being `TrafficLights`
  (`JunctionArbiter.SyncSignals`, `JunctionArbiter.cs:195-199`) — editing, say, the
  node's `SizeOffset` does not reset signal timing.

## Tuning constants

| Constant | Value | Location | Rationale |
|---|---|---|---|
| `CornerMargin` | 0.5 m | `JunctionBuilder.cs:19` | Keeps cut cross-sections just past the theoretical corner solve so meshes never touch it exactly; also the shrink floor. |
| `MaxCutFraction` | 30% | `JunctionBuilder.cs:20` | Ceiling on how much of a short edge a junction can eat; guarantees two junctions on one edge never overlap. |
| `ZoneMinBand` | 0.05 m | `JunctionBuilder.cs:21` | Divergence threshold between inner/outer outlines below which a corner zone is skipped as noise. |
| `MaxExtra` | 12 m | `JunctionBuilder.cs:22` | Resize ceiling per leg — an authored upper bound on how far a player can grow one approach. |
| `GreenSec` / `AmberSec` / `AllRedSec` | 12 / 3 / 1 s | `SignalController.cs:13` | Fixed timing for the two-phase cycle; also `docs/conventions.md`'s "Signals green/amber/all-red" row. |

The `GreenSec = 12 s` caveat that stood here through M6 is **resolved as of the M6.5
traffic-feel pass**: the M6 KPI health report (`docs/health/M6.md`) recorded
`signal.sat_headway_s = 3.522` — above the 1–3 s saturation-headway plausibility
anchor (`KpiScenarios.cs`) — because a 12 s green discharged only ~4 vehicles of a
stopped queue. The predicted fix ("a driver-model change to standing-start
acceleration") turned out to be only part of the answer: M6.5's diagnosis showed the
dominant cause was the junction arbiter's *unprojected spillback check* forcing every
discharging follower to a dead stop at the line (see chapter 05's junction-arbitration
section). With the anticipatory spillback projection (`SpillbackAnticipationSec = 0.7 s`),
IDM+ car following, and launch acceleration, `docs/health/M6.5.md` records
`signal.sat_headway_s = 2.04` — comfortably inside the plausibility band, with at
least 5-6 vehicles now clearing a single 12 s green. `GreenSec` itself was never
touched, exactly as this note predicted.

## Known limits

- **No protected left-turn phases.** `SignalController` only ever produces two
  opposing-group phases; a left across oncoming traffic on a green relies entirely
  on the arbiter's gap-acceptance logic (`ConflictApproachClear`, chapter 05), same
  as an unprotected left at a real signal with no arrow.
- **No junction merging.** Two nodes close enough to functionally be one complex
  intersection (a staggered T, a closely-spaced pair of crosses) are still
  resolved and controlled fully independently — separate `JunctionGeometry`,
  `EffectiveControl`, and (if lights) `SignalController`, with no coordination.
- **No signal-timing authoring UI.** `GreenSec`/`AmberSec`/`AllRedSec` are
  compile-time constants shared by every `TrafficLights` node; there's no
  per-node override analogous to `SizeOffset`/`LegOffsets`, and no in-game control
  over cycle length or phase split.

## How to verify

- `dotnet test --filter FullyQualifiedName~JunctionControlTests` and
  `~JunctionResizeTests` exercise control resolution, override pruning, and
  geometric resizing in isolation.
- `dotnet test --filter FullyQualifiedName~SignalTests` exercises phase alternation
  (`OpposingLegsShareAPhaseAndAxesAlternate`) and the entry-gate contract
  (`VehicleEntersOnlyOnGreen`).
- `dotnet test --filter FullyQualifiedName~KpiScenarios` (via the M6 health harness,
  `docs/health/M6.md`) regenerates `signal.startup_lost_s` / `signal.sat_headway_s`
  against `docs/health/kpi-baseline.json` — a `SizeOffset`, `GreenSec`, or driver-model
  change should be checked against this baseline before being called done.
- For a geometry regression, add a case to `JunctionResizeTests` asserting `CutT` /
  `TightCuts` rather than eyeballing a screenshot first — then confirm visually with
  the screenshot harness (`docs/verification.md`) since curb-corner rounding and
  corner-zone rendering are easiest to sanity-check by eye.
