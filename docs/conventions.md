# Conventions & constants

## Space & traffic frame
- 1 unit = 1 metre. Y is up. Ground plane is XZ.
- **Right-hand traffic.** Driver's right of direction `d` = `d.Cross(Vector3.Up)`;
  for a +X heading that is +Z. `Bezier3.NormalXZ` of a +X line is +Z, so
  **lane offset > 0 = driver's right when travelling Forward (P0→P3)**.
- Forward lanes sit on positive offsets, Backward on negative. "Leftmost lane" within a
  direction group = smallest `|offset|` (closest to the centreline).
- Edge curves are single cubics; lanes are the parent curve + lateral offset. Lane
  travel distance is measured on the centreline arc length (approximation, fine so far).
- **`Vehicle.S` is the FRONT bumper**, in metres along the current lane's drawable span
  (between junction cuts) or connector curve, in travel direction. Gap math and
  rendering both rely on this; the rendered centre trails by `Length/2`.

## Key constants
| What | Value |
|---|---|
| MinEdgeLength | 4 m (geometric floor inside curve math only) |
| NodeReuseRadius | 0.5 m (crossing/endpoint node-connection exemption; split absorb uses the edge type's MinSegmentLength) |
| MinJunctionAngleDeg | 25° (`SharpAngle` at endpoint bindings, `Kinked` inside a proposal, and the `CrossingTooShallow` floor for proposal-vs-existing crossings; sliver crossings are governed by per-type MinSegmentLength, not this angle) |
| TangentContinuationDeg | 1° (OnEdge departures within it are legal G1 ramp exits, exempt from `SharpAngle`; AtNode stays strict) |
| Per-type MinSegmentLength (`max(8 m, Width)`) | TwoLane 8, FourLane 16, Street 12, Avenue 21, OneWay 12, Asymmetric 12 m |
| Per-type MinRadius | TwoLane 20, FourLane 35, Street 10, Avenue 25, OneWay 10, Asymmetric 20 m |
| Grid tool cell (`GridStampShape`) | 48 m |
| Snap grid cell (`GridConfig.Default`, toolbar-selectable) | 8 m (4/8/16/32 also offered), off by default |
| Snap weights (`SnapEngine`, distance/weight scoring — higher wins ties) | Node 4.0 > GuideIntersection 2.5 > Perpendicular 2.2 > Edge 2.0 > Guideline 1.5 = GridPoint 1.5 > GridLine 1.0 |
| SurfaceY / MarkingY / SidewalkRise | 0.07 / 0.10 / 0.13 |
| Marking dash on/off, line width | 3 m / 3 m, 0.15 m |
| JunctionBuilder CornerMargin / MaxCutFraction / MaxExtra | 0.5 m / 30 % / 12 m |
| Snap radius | camDist × 0.02, clamped [1, 20] |
| Vehicle length | 4.5 m |
| IDM T / s0 / a / b | 0.95 s / 2 m / 2.6 / 2.8 m/s² (`T` tightened for M5 assertiveness; was 1.1 s) |
| MOBIL gain / safe / urgent follower decel | 0.3 / 2.5 / 4.0 m/s² |
| Lane change duration / mandatory window / lock-in | 2 s / 80 m / 25 m |
| Gap acceptance (impatience, `JunctionArbiter.AcceptedGap`) | `max(2.2, 2.8 − 0.03·waitSeconds)` s — 2.8 s fresh, shrinking to a 2.2 s floor the longer a vehicle waits at the line |
| ClearMargin (past-point clearance, `JunctionArbiter`) | 0.5 m — an occupant on a conflicting connector still blocks entry until its `S` has advanced past `conflictPointS + Vehicle.Length + 0.5` (rear bumper clear of the conflict point by 0.5 m) |
| DeadlockBreakSec (`JunctionArbiter`) | 6 s — once a vehicle has waited this long it ignores a stationary equal-rank rival with a later (or no) arrival ticket |
| Movement rank (`JunctionArbiter.MovementRank`, lexicographic `(Row, Turn)`, higher wins both axes) | Row: Free/Signal-green 3, Yield 2, Stop 1. Turn: Straight 3, Right 2, Left 1, U-turn 0 |
| Right-hand-rule window (`JunctionArbiter.ApproachesFromMyRight`) | signed angle `atan2(cross, dot)` between the two connectors' entry tangents, open interval (−150°, −30°) |
| Signals green/amber/all-red | 12 / 3 / 1 s |
| Connector speeds (`TrafficSim.ConnectorSpeed`) | Straight = `min(fromEdge.SpeedLimit, toEdge.SpeedLimit)` (priority traffic doesn't brake for junctions); Right 9 m/s; Left 10 m/s; U-turn 5 m/s |
| Speed limits (from `DesignSpeedKmh`) | TwoLane 80, FourLane 100, Street 50, Avenue 60, OneWay 50, Asymmetric 60 km/h |

## Road type profiles (M5 additions)
- **OneWay** (`RoadTypeId(5)`, width 12 m, 50 km/h, min radius 10 m): two `Forward`
  driving lanes at ±1.75 m (same travel direction despite opposite-sign offsets — the
  direction-asymmetric case, see gotchas) plus 2.5 m sidewalks at ±4.75 m.
- **Asymmetric 2+1** (`RoadTypeId(6)`, width 12 m, 60 km/h, min radius 20 m, no
  sidewalks): one `Backward` driving lane at −4.25 m, two `Forward` driving lanes at
  −0.75 m and +2.75 m. The opposing separation line sits off the geometric centerline,
  at −2.5 m, on purpose — that's the boundary between the single backward lane and the
  double-forward lanes, not the edge's `Offset = 0`.
- `RoadType.IsDirectionAsymmetric` (`ForwardCount != BackwardCount`) flags both types for
  ghost-arrow drawing purposes.

## Traffic calibration record
- `AssertivenessGuardTests.MinorRoadDischargesThroughPriorityStream` (measured
  2026-07-15): on a busy 4-way cross (TwoLane priority × Street minor, priority pulse
  every 4.5 s alternating direction, 2 sim-minute window, 40 minor spawns attempted),
  current M5 arbitration discharges **18/40** minor vehicles vs a pre-M5 baseline
  (commit `a67a1e3`, same scenario) of **7/40**. Regression floor = 75% of the M5 number
  = `(int)(0.75 * 18)` = **13**, comfortably above the passive baseline (margin of 6
  vehicles / 46% of the floor). Full sweep methodology in
  `.superpowers/sdd/task-10-report.md`.

## Process
- Spec → plan → TDD execution, one commit per green task
  (`docs/superpowers/specs|plans/YYYY-MM-DD-*.md`).
- Route costs and control behavior must stay consistent: if you change a delay constant
  in the arbiter, check `RoutePlanner`'s matching cost.
- ids (`NodeId`/`EdgeId`/`LaneId`) are opaque ints; `EdgeId`-keyed config (role
  overrides, leg offsets) is pruned automatically when topology changes.
