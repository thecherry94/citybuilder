# Ch.01 Geometry — working notes

## Terminology for new hires (candidates for glossary.md)
- **Cubic Bézier as the universal curve type**: there is no separate "line" type — a
  straight road is `Bezier3.Line`, a degenerate cubic with control points at 1/3 and
  2/3 of the chord (`Bezier3.cs:14-15`). Anything that special-cases "is this
  straight" must detect it geometrically (`BezierOps.IsFlat`), not by type-switching.
- **Lane offset**: a signed lateral distance from a lane to its parent edge curve,
  evaluated via `Bezier3.OffsetPoint(t, offset)`. Offset > 0 = driver's right when
  travelling P0→P3 (right-hand traffic). A lane is never its own curve object.
- **Arc-length parameterization**: converting between curve parameter `t` (not
  uniform in distance) and actual distance-along-curve in metres. `ArcLengthTable`
  is the mechanism; every consumer that needs "distance along this curve" goes
  through one.
- **`MinRadius` sample-density bug (M6)**: the historical bug where a fixed sample
  count across a whole curve under-measures curvature on long curves and reports a
  *worse* (more accurate) radius after the curve is later shortened by splitting —
  same geometry, denser resampling, different answer. Fixed by scaling sample count
  with `Length()`. Good example of "measurement resolution masquerading as a
  geometry change" — worth recognizing this shape of bug elsewhere in the sim.

## Forward references made in ch.01 (things ch.01 asserts but doesn't fully own)
- → **ch.02 (network/validation)**: `NetworkInvariants.CheckEdgeGeometry`
  (`NetworkInvariants.cs:59-70`) and `RoadNetwork.Validate` (`RoadNetwork.cs:80-90`)
  are the actual callers turning `MinRadius`/`SelfIntersects`/`Intersections` into
  placement pass/fail decisions with per-road-type floors. Ch.01 only explains the
  geometry primitives, not the validation policy layered on top.
- → **ch.02**: `RoadNetwork.SplitEdgeWithReuse` (the mechanism that shortens edges
  in place, central to the M6 MinRadius story) deserves its own explanation of the
  node-reuse-absorption policy; ch.01 only describes its effect on `MinRadius`
  sampling.
- → **ch.04 (lane graph & connectors)**: `ConnectorBuilder` builds one
  `ArcLengthTable` per connector at 24 samples and uses `BezierOps.Intersections`
  for conflict-point discovery between connectors at a junction
  (`ConnectorBuilder.cs:240-261`). Ch.01 only covers the primitives it calls.
- → **ch.05 (traffic sim)**: `TrafficSim` caches one `ArcLengthTable` per
  `(NodeId, connectorIndex)` and treats the whole cache as invalidated on every
  network rebuild (`TrafficSim.cs:28,508`; see the "connector indices are
  per-rebuild" gotcha, `docs/gotchas.md:64-66`). `Vehicle.S` (front-bumper distance)
  is defined and consumed there, not here.
- → **ch.06 (drafting & snapping)**: `BezierOps.ArcFromTangent` backs the draft
  tool's arc gesture (`Tools/Draft/Shapes.cs:143`); `SnapEngine` uses
  `ClosestPoint` for edge-projection snapping (`SnapEngine.cs:241`). Ch.01 covers
  only the geometry math, not gesture UX or snap scoring.
- → **ch.07 (rendering/markings)**: `Game/JunctionMarkings.cs` and
  `Game/MeshBuilders.cs` are the only consumers of `Tessellate` and build their own
  `ArcLengthTable`s at 24/32 samples for dash placement. Not covered here beyond
  noting the call sites.

## Open questions / [UNCERTAIN] flags in ch.01 (3 total)
1. **`SelfIntersects` false-positive root cause** — not traced at the float level;
   hypothesis is near-collinear adjacent polyline vertices from `Point(t)` rounding
   at specific angles beating `SegmentIntersect`'s `1e-6` tolerance. Would need
   instrumented tracing of `u`/`v` at e.g. 27° to confirm. (`BezierOps.cs:104-119`)
2. **`ArcFromTangent`'s 175° sweep cap rationale** — plausibly a draft-tool UX
   choice (leaving margin before the 180°/collinear-behind case) but not verified
   against a spec or commit message. Check `docs/superpowers/specs` for an
   arc-gesture spec, or `git blame BezierOps.cs` around that constant.
3. **Whether `SelfIntersects`' fixed 32-span grid can hide a self-crossing loop on
   a long curve** (the same class of bug M6 fixed in `MinRadius`, but explicitly
   out of scope for that change) — no known repro; would need a targeted fuzz seed
   on a long, tightly-looping curve to check whether length/radius floors already
   prevent a loop tight enough to hide between samples.

## Cross-cutting patterns noticed
- **"Resolution masquerading as a geometry change"**: the M6 `MinRadius` bug and
  the still-open `SelfIntersects` risk are the same shape — a fixed sample budget
  over a variable-length curve. Worth checking whether any other fixed-N-sample
  algorithm in the codebase (rendering, traffic) has the same latent issue when a
  long curve gets progressively shortened.
- **Degenerate-input fallback chains** are a recurring defensive pattern:
  `Tangent` (3-tier fallback: derivative → finite difference → chord → UnitX) is
  the clearest example; worth watching for the same "never return zero/NaN to a
  caller that can't handle it" discipline elsewhere (e.g. `NormalXZ`'s own
  fallback to `UnitZ` at `Bezier3.cs:62`).
- **Test-pinned but functionally dead constants**: `GeoConstants.MinEdgeLength` is
  referenced by nothing in production code, only a smoke test. Worth checking other
  chapters for similar "legacy constant kept alive only by a pinning test" cases
  before assuming every constant with a test is load-bearing.
- **Fuzz-regression tests as primary source of "why"**: the most precise account of
  *why* a change was made (exact numbers, root cause) lives in
  `tests/Domain.Tests/Fuzzing/FuzzRegressionTests.cs` XML doc comments, not in
  commit messages or the source files themselves. Future chapters should check that
  file for their subsystem before assuming the git log or the roadmap is the best
  source.
