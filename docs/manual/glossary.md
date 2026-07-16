# Glossary

Terms used across the manual, alphabetized. Each entry links to the chapter section
that explains it most fully. Where two chapters used a term slightly differently, the
definition here is the one reconciled against the code at commit `f0542d7`.

See also [docs/conventions.md](../conventions.md) for the raw constant table and the
space/traffic frame, and [00-overview.md](00-overview.md) for how these pieces fit
together.

---

**Absorption (node-reuse absorption)** — When `SplitEdgeWithReuse` would split an edge
within that edge type's `MinSegmentLength` of one of its ends, it snaps the split point
to the existing end node instead of creating a new one. The absorption radius scales with
road width (up to 16–21 m), and is *not* the small fixed `NodeReuseRadius` (0.5 m) — the
two are easy to conflate. See [02 · How commits work](02-network-validation.md#how-commits-work).

**AcceptedGap / impatience** — The gap-acceptance threshold a waiting vehicle demands
before entering a junction: `max(2.2, 2.8 − 0.03·JunctionWait)` seconds. It shrinks the
longer a car waits at the line — one of the two M5 assertiveness levers (with `Idm.T`)
that fixed "minor-road cars clog junctions forever." See [05 · Junction arbitration](05-traffic-sim.md#junction-arbitration).

**AdjustMode** — A session-level toggle (not a `DraftSession.State`) forcing every
completed, valid gesture to stop at `Adjustable` for manual confirmation instead of
committing immediately. See [06 · Session state machine](06-drafting-snapping.md#the-session-state-machine).

**ArcFromTangent** — The one constructive geometry algorithm: fits a circular arc through
a start point, a required departure tangent, and an end point, returned as one cubic
(sweep ≤ 90°) or two (up to a 175° cap; `null` beyond). Backs the draft tool's arc
gesture. See [01 · How it works](01-geometry.md#how-it-works).

**Arc-length parameterization / ArcLengthTable** — A curve's parameter `t` is not uniform
in metres; `ArcLengthTable` precomputes the `t ↔ distance` mapping for one curve instance
so consumers can reason in metres (dash spacing, vehicle `S`). Edges cache a 128-sample
table; short-lived junction connectors use cheap 24-sample tables. See
[01 · How it works](01-geometry.md#how-it-works).

**Arrow bug (M5 "arrow report")** — Informal name for the pre-`2a0e6c9` defect where two
lanes' worth of traffic were drawn as straight connectors into a road with one receiving
lane. The scars it left are the capacity-aware straight-block rules. See
[04 · Turn-lane assignment](04-lane-graph-connectors.md#turn-lane-assignment).

**Batch (`BeginBatch`/`EndBatch`)** — The bracket around every topology-mutating entry
point so one logical edit raises exactly one `Changed` event. `EndBatch` rebuilds derived
data for touched nodes, reconciles add/remove pairs, bumps `Version`, and fires one
`NetworkDelta`. See [02 · How commits work](02-network-validation.md#how-commits-work).

**Bézier3 / cubic Bézier (the universal curve type)** — Every road edge, lane path, and
connector is one 4-point cubic Bézier in the XZ plane. There is no separate "line" type —
a straight road is `Bezier3.Line`, a degenerate cubic; code that special-cases straight
segments must detect them geometrically (`IsFlat`), never by type. See
[01 · How it works](01-geometry.md#how-it-works).

**CarriagewayHalf / OuterHalf** — The two half-widths junction geometry uses:
`CarriagewayHalf` is the paved surface up to the sidewalks' inner edge (drives the asphalt
polygon); `OuterHalf` is the full built half-width including sidewalks (drives the cut
sizing and the main-road width score). See [02 · Road-type catalog](02-network-validation.md#the-road-type-catalog).

**Chain mode** — A `DraftMode`, not its own `IDraftShape`: it reuses `QuadCurveShape`, and
all chain behavior lives in `DraftSession.TryCommit`, which re-seeds a new G1-locked draft
at the committed endpoint after each commit. See [06 · Draft shapes](06-drafting-snapping.md#draft-shapes).

**Changed event / NetworkDelta** — `RoadNetwork.Changed` fires exactly one `NetworkDelta`
per mutation batch, listing edges/nodes added, removed, and changed. Every `src/Game` view
subscribes and resyncs from the delta; the domain never calls a view directly. See
[02 · How commits work](02-network-validation.md#how-commits-work).

**Conflict point (`ConflictPoint`)** — Where two connectors' paths interact, stored as
`(Other, SMine, STheirs)` — the arc-length position of the interaction on *each* connector.
Either a curve crossing (`BezierOps.Intersections`) or a same-target merge (each curve's
own end). Symmetric by construction. See [04 · Conflict points](04-lane-graph-connectors.md#conflict-points).

**Conflict-point passed-point rule** — The arbiter blocks entry only until a conflicting
occupant's own `S` has advanced past its conflict-point arc distance plus
`Vehicle.Length + ClearMargin` — not "connector busy = blocked." This is what lets crossing
connectors be occupied simultaneously when neither car is near the crossing yet. See
[05 · Junction arbitration](05-traffic-sim.md#junction-arbitration).

**Connector (`LaneConnector`)** — A Bézier curve linking one arriving lane to one departing
lane across a node; what vehicles actually drive through a junction. Carries a `TurnKind`
and a `RightOfWay` stamp. See [04 · Turn-lane assignment](04-lane-graph-connectors.md#turn-lane-assignment).

**Cut point (`CutT`)** — The parametric `t` where an edge's rendered asphalt stops and the
junction polygon begins. `JunctionBuilder` computes it per leg; `ConnectorBuilder` uses it
as each connector's start point; the render layer dirties every edge touching a changed
node because a junction rebuild can move it. See [03 · Junction geometry](03-junctions-control.md#junction-geometry).

**CornerZone / JunctionSegmentKind** — A `CornerZone` is a raised-sidewalk ring emitted per
wedge where the inner and outer junction outlines diverge; `JunctionSegmentKind`
(`Cut`/`Open`/`Curbed`) classifies each polygon edge so the mesh layer knows what to build
without re-deriving wedge logic. See [03 · Junction geometry](03-junctions-control.md#junction-geometry).

**Deadlock breaker** — `DeadlockBreak` (`DeadlockBreakSec = 6`): after waiting past 6 s, a
vehicle ignores a *stationary equal-rank* rival that holds a later (or no) arrival ticket.
Fires only for equal-rank (`cmp == 0`) standoffs — the escape valve for a symmetric
uncontrolled-cross deadlock, distinct from general impatience. See
[05 · Junction arbitration](05-traffic-sim.md#junction-arbitration).

**Direction-aware signed-offset ordering** — Lanes are ranked left→right by *signed*
`Offset` (Forward groups ascending, Backward descending), never by `|Offset|`, because
direction-asymmetric types put driving lanes on the same side of centerline. Getting this
wrong is this codebase's single most recurring bug class. See [04 · Lane ordering](04-lane-graph-connectors.md#lane-ordering).

**Direction-asymmetric road type** — A type where `ForwardCount != BackwardCount`
(`OneWay`, `Asymmetric` 2+1). Travel direction can't be inferred from a symmetric lane
layout, so these types drive directional-arrow painting and are the recurring edge-case
source across chapters 03–05. See [02 · Road-type catalog](02-network-validation.md#the-road-type-catalog).

**DraftSession / IDraftShape** — `DraftSession` is the pure-domain gesture state machine
(`Idle → Placing → Adjustable`) owning an in-progress `RoadDraft`; `IDraftShape` is the
stateless strategy interface (six shapes) turning handles into `Bezier3` curves. The Godot
layer only forwards input. See [06 · Session state machine](06-drafting-snapping.md#the-session-state-machine).

**DroppedSegments** — A count on `CommitResult`: a commit can report `Success = true` while
building fewer edges than proposed, because commit-side floor/leg-angle guards drop a
segment that node-reuse relocation shrank below its type's floor. Callers must surface it,
not assume 1:1 proposal-to-edge. See [02 · How commits work](02-network-validation.md#how-commits-work).

**EffectiveControl vs JunctionConfig** — `JunctionConfig` is the *authored, persisted*
input (mode, role/leg overrides, size offset — may be stale or `Auto`); `EffectiveControl`
is the *resolved, always-concrete* output of the pure function `JunctionControl.Resolve`.
The project reuses this "authored input / resolved output" pairing widely. See
[03 · Control resolution](03-junctions-control.md#control-resolution).

**EnforceNoPenetration** — The hard failsafe run every tick after integration: one
front-to-back pass per queue clamps each follower's `S` behind its leader and caps its
speed. IDM keeps the *desired* gap; this backstops the case where independently-computed
accelerations still integrate into an overlap in one step. See [05 · The tick](05-traffic-sim.md#the-tick).

**Flashed contract (M6)** — `DraftSession.Flashed` fires on hard failure *and* on soft
success with `DroppedSegments > 0`. A non-null flash message therefore does not mean
nothing was built — it is not a pure error channel. See [06 · Session state machine](06-drafting-snapping.md#the-session-state-machine).

**FreeFlowTime** — A trip stat accumulated incrementally at each completed lane run /
connector crossing, never estimated from the planned route at spawn — the design that
avoids a replan double-count bug. See [05 · Trip stats](05-traffic-sim.md#trip-stats).

**G1 tangent-continuation exemption** — A curve departing an `OnEdge` binding within
`TangentContinuationDeg` (1°) of that edge's own tangent is a legitimate ramp continuation,
exempt from the 25° junction-angle floor. `OnEdge`/`fromEdge` only — `AtNode` departures
stay strict even at near-zero angles. See [02 · How commits work](02-network-validation.md#how-commits-work).

**Guideline / Parallel guide** — Guidelines are the dashed "extend this road's tangent"
construction lines built per edge leaving nearby nodes; parallel guides are curb-to-curb
offset lines beside existing *straight* edges. Both feed `SnapEngine` candidates and only
appear with the relevant snap flags enabled. See [06 · Snapping](06-drafting-snapping.md#snapping).

**IDM (Intelligent Driver Model)** — The car-following model; `Idm.Accel` is the single
static function all acceleration flows through (following, stopping at a wall, lane-change
safety, failsafe). Every constant (`T`, `S0`, `A`, `B`) is game-tuned, not textbook. See
[05 · Car following (IDM)](05-traffic-sim.md#car-following-idm).

**Ids (`NodeId`/`EdgeId`/`LaneId`/`RoadTypeId`)** — Opaque `int` wrappers, minted from
monotonic counters and never reused after removal. They are the *only* cross-layer
reference: `src/Game` caches Godot nodes keyed by id and re-looks-up the domain struct each
frame; persistence restores ids verbatim so nothing outside the network dangles. See
[02 · Network & validation](02-network-validation.md).

**JunctionArbiter / MayEnter** — The single gate a vehicle passes to leave its lane and
start a connector; every cross-vehicle junction interaction (right-of-way, gap acceptance,
deadlock recovery, signals, stop FIFO) funnels through `MayEnter`, called from two sites
that must agree. See [05 · Junction arbitration](05-traffic-sim.md#junction-arbitration).

**JunctionControl.Resolve** — The pure function `(node, edges) → EffectiveControl`: degree
≤ 2 always resolves to `None`; degree ≥ 3 reads `Config.Mode` (`Auto → PrioritySigns`),
and only `PrioritySigns` assigns per-leg roles by scoring `(width, straightness)` to pick
the main-road pair. See [03 · Control resolution](03-junctions-control.md#control-resolution).

**Lane offset / OffsetPoint** — A lane is never its own curve: it is the parent edge curve
plus a constant signed lateral `offset`, evaluated through `Bezier3.OffsetPoint(t, offset)`.
`offset > 0` is the driver's right when travelling P0→P3 (right-hand traffic). See
[01 · How it works](01-geometry.md#how-it-works).

**MinRadius** — The tightest radius of curvature anywhere on a curve, sampled from planar
curvature; checked against per-type floors (e.g. OneWay 10 m, FourLane 35 m). Sample
density is length-adaptive since M6 (`clamp(ceil(Length()/8m), 32, 4096)` instead of a
fixed 32 points), so a long edge shortened by repeated splits reports the same tightest
bend before and after. See [01 · How it works](01-geometry.md#how-it-works).

**MinSegmentLength** — `max(8 m, Width)` per road type: the shortest committable edge *and*
the node-reuse absorption distance for splits on that type — a wide type both refuses
shorter edges and snaps splits to its ends across a wider radius. See
[02 · Road-type catalog](02-network-validation.md#the-road-type-catalog).

**MOBIL-lite lane change** — The lane-change model: discretionary changes chase an
acceleration gain over `DiscretionaryGain`; mandatory changes reach a turn-serving lane
before a junction with ramping urgency. A change occupies both lanes for `ChangeDuration`
(2 s). See [05 · Lane changes](05-traffic-sim.md#lane-changes).

**Movement rank** — The lexicographic `(Row, Turn)` tuple the arbiter compares: Row
(Free/Signal-green 3 > Yield 2 > Stop 1) dominates Turn (Straight 3 > Right 2 > Left 1 >
U-turn 0). See [05 · Junction arbitration](05-traffic-sim.md#junction-arbitration).

**NetworkInvariants** — A second, independent pass over already-committed state (not a copy
of `Validate`'s logic): a post-state *auditor* callable by tests, the fuzzer, or a debug
overlay against any network however it was built. `Validate` is the ground-truth *gate*;
this is the audit. See [02 · NetworkInvariants](02-network-validation.md#networkinvariants).

**Never-strand fallback (M6)** — A post-pass in `ConnectorBuilder`: any driving lane left
with zero connectors, when the node still has departing capacity on another edge, is
connected to its nearest eligible departure with all rank rules relaxed. Distinct from a
*legally* stranded lane. See [04 · Turn-lane assignment](04-lane-graph-connectors.md#turn-lane-assignment).

**NodeReuseRadius** — 0.5 m: the small fixed distance under which a free endpoint or
crossing picks up an existing node instead of creating one. Distinct from the per-type
absorption radius (see **Absorption**). See [02 · How commits work](02-network-validation.md#how-commits-work).

**Resync pattern** — The `src/Game` view discipline: bind once to a domain object,
subscribe to `Changed`, cache Godot nodes keyed by domain id, and never hold state the
domain didn't already have. `RoadNetworkView` is the reference implementation every other
view copies. See [07 · The view resync model](07-rendering-markings.md#the-view-resync-model).

**RightOfWay / RowFor** — `RightOfWay` (`Free`/`Yield`/`Stop`/`Signal`) is the per-connector
control stamp the traffic arbiter reads. `ConnectorBuilder.RowFor` is the single place
`EffectiveControl` becomes that stamp (`AllWayStop → Stop`, `TrafficLights → Signal`,
`PrioritySigns → per-leg role`, else `Free`). See [03 · Control resolution](03-junctions-control.md#control-resolution).

**Round-trip contract** — The persistence guarantee `Save(Load(Save(n))) == Save(n)`
byte-for-byte. Chosen over structural equality because it needs no comparer, diffs cleanly,
and serves as the fuzzer's oracle (checked every 10 actions). See
[08 · The round-trip contract](08-persistence.md#the-round-trip-contract).

**RoutePlanner (A\*)** — Strategic routing: A* over `(EdgeId, Forward)` states (not lanes),
with movement costs = travel time + turn penalties (L 4 s / R 1.5 s / U 8 s) + control
delay (Yield 2 / Stop 4 / Signal 5 s). Replanning is just planning again from the current
edge. See [05 · Routing](05-traffic-sim.md#routing).

**SelfIntersects** — A polyline-approximation self-intersection test over 32 fixed spans,
with a known intermittent false-positive on exactly-straight curves at certain absolute
angles (20°, 27°, 28°, 31°, …) — deliberately unfixed; new scenarios should avoid those
headings. See [01 · Known limits](01-geometry.md#known-limits).

**SignalController** — A fixed-cycle two-phase signal for one lights node: partitions legs
into two opposing groups and cycles each Green (12 s) → Amber (3 s) → Red, offset by a
half-cycle with a 1 s all-red clearance folded in. A pure phase clock — it doesn't know
what "go" means for a connector; the arbiter does. See [03 · Signals](03-junctions-control.md#signals).

**SimInvariants** — The traffic-side post-state auditor (no-penetration, no conflict-point
co-occupancy), used by burst/fuzz harnesses and lifted verbatim into the standing
assertiveness regression guards — the same "mirror logic in an independent checker" spirit
as `NetworkInvariants`. See [05 · Invariants](05-traffic-sim.md#invariants).

**Sliver (sliver edge)** — A would-be edge shorter than its type's `MinSegmentLength`; the
guard vocabulary throughout `Validate`/`Commit`. See [02 · How commits work](02-network-validation.md#how-commits-work).

**SnapEngine / candidate-scored snapping** — `SnapEngine.Resolve` picks the lowest
`score = distance / weight` across all producers (node, edge, guideline, guide-intersection,
grid, perpendicular), *not* a priority cascade — so a dead-on weak candidate can beat a
barely-in-range strong one. See [06 · Snapping](06-drafting-snapping.md#snapping).

**SplitEdgeWithReuse** — The one function turning "split at `t`" into either a real de
Casteljau split (two edges, one node) or a no-op returning an existing end node (see
**Absorption**), used for both endpoint resolution and mid-edge crossings. See
[02 · How commits work](02-network-validation.md#how-commits-work).

**Straight block** — Per `(fromEdge, toEdge)` pair classified `Straight`, the
`[start, end)` window of source lanes eligible for a straight connector — capacity-aware
(never more than the target can receive) and per-target-capped for forks. See
[04 · Turn-lane assignment](04-lane-graph-connectors.md#turn-lane-assignment).

**Stranded lane (iff-rule)** — An arriving driving lane with zero outgoing connectors.
Legal *if and only if* its node offers no departing driving lane on any *other* edge
(same-edge U-turns don't count) — a CS2-style ruling (spec amendment `ceb1887`). Routing
simply never selects it. Stranding when receiving capacity exists elsewhere is a hard
violation. See [02 · NetworkInvariants](02-network-validation.md#networkinvariants).

**Tangent lock (G1 lock)** — `RoadDraft.StartTangent != null`: the draft's start direction
is pinned (shrinking the shape's required-handle count), released via
`DraftSession.ReleaseTangentLock` (the game binds "T"). Chain segments re-lock by default.
See [06 · Draft shapes](06-drafting-snapping.md#draft-shapes).

**TightCuts** — Edges whose 30% `MaxCutFraction` clamp bit before reaching the wanted cut:
geometry stays sound but junction paint should skip there. See [03 · Junction geometry](03-junctions-control.md#junction-geometry).

**Traffic frame** — The project's spatial convention: XZ ground plane, +Y up, right-hand
traffic; lane offsets are signed perpendicular to the curve tangent, `offset > 0` = driver's
right when travelling Forward. See [docs/conventions.md](../conventions.md) and
[01 · How it works](01-geometry.md#how-it-works).

**TurnKind / turn classification** — `Classify(inDir, outDir)` maps a direction pair to
`Straight | Left | Right | UTurn` by signed angle: `|deg| < 30°` Straight, `> 150°` U-turn,
else Left/Right by sign. The 30° band comfortably contains the ~1° ramp-continuation case.
See [04 · Turn classification](04-lane-graph-connectors.md#turn-classification).

**Validate / Commit (two-phase pipeline)** — `Validate` is a pure, side-effect-free dry run
against a proposal returning errors + crossings + the network `Version`. `Commit` replays
that decision against the *current* live network and performs the graph surgery. Nothing
else mutates `_nodes`/`_edges`. See [02 · How commits work](02-network-validation.md#how-commits-work).

**Validate-before-mutate** — The load-path safety property: `ValidateGame` runs entirely
before `RestoreInto` opens its batch, so a validation failure touches nothing and fires no
`Changed` event. See [08 · RestoreInto](08-persistence.md#restoreinto).

**Vehicle.S (front bumper)** — A vehicle's position is the arc-length distance of its
*front bumper* along its current lane span or connector; the rendered centre trails by
`Length/2`. Gap math and rendering both depend on this convention. See
[05 · The tick](05-traffic-sim.md#the-tick) and [docs/conventions.md](../conventions.md).

**Virtual stop-line wall** — When `MayEnter` is false, `ComputeAccel` synthesizes a
`(gap, dv)` pair as if a stationary leader sat at the cut, so IDM's own braking curve slows
the car smoothly toward a red/yield rather than an instant hard stop. See
[05 · Car following (IDM)](05-traffic-sim.md#car-following-idm).

**Wedge solve / corner solve** — Per adjacent-leg-pair corner computation in
`JunctionBuilder`, with three fallback strategies for one wedge: `SolveCorner` (convex
corner ahead of both legs), `SolveElbowOutside` (plain bend, intersection behind both), and
`NodeArc` (swept arc for Y-tips, width tapers, near-collinear legs). See
[03 · Junction geometry](03-junctions-control.md#junction-geometry).
