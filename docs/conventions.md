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
| Per-type MinSegmentLength (`max(8 m, Width)`) | TwoLane 8, FourLane 16, Street 12, Avenue 21 m |
| Per-type MinRadius | TwoLane 20, FourLane 35, Street 10, Avenue 25 m |
| Grid tool cell (`GridStampShape`) | 48 m |
| Snap grid cell (`GridConfig.Default`, toolbar-selectable) | 8 m (4/8/16/32 also offered), off by default |
| Snap weights (`SnapEngine`, distance/weight scoring — higher wins ties) | Node 4.0 > GuideIntersection 2.5 > Perpendicular 2.2 > Edge 2.0 > Guideline 1.5 = GridPoint 1.5 > GridLine 1.0 |
| SurfaceY / MarkingY / SidewalkRise | 0.07 / 0.10 / 0.13 |
| Marking dash on/off, line width | 3 m / 3 m, 0.15 m |
| JunctionBuilder CornerMargin / MaxCutFraction / MaxExtra | 0.5 m / 30 % / 12 m |
| Snap radius | camDist × 0.02, clamped [1, 20] |
| Vehicle length | 4.5 m |
| IDM T / s0 / a / b | 1.1 s / 2 m / 2.6 / 2.8 m/s² |
| MOBIL gain / safe / urgent follower decel | 0.3 / 2.5 / 4.0 m/s² |
| Lane change duration / mandatory window / lock-in | 2 s / 80 m / 25 m |
| Gap acceptance (yield/stop) | 4 s |
| Signals green/amber/all-red | 12 / 3 / 1 s |
| Junction speeds straight/left/right/u-turn | 14 / 9 / 8 / 5 m/s |
| Speed limits (from `DesignSpeedKmh`) | TwoLane 80, FourLane 100, Street 50, Avenue 60 km/h |

## Process
- Spec → plan → TDD execution, one commit per green task
  (`docs/superpowers/specs|plans/YYYY-MM-DD-*.md`).
- Route costs and control behavior must stay consistent: if you change a delay constant
  in the arbiter, check `RoutePlanner`'s matching cost.
- ids (`NodeId`/`EdgeId`/`LaneId`) are opaque ints; `EdgeId`-keyed config (role
  overrides, leg offsets) is pruned automatically when topology changes.
