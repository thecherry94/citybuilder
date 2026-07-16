# 01 · Geometry

Every road edge, lane, and junction connector in this game is one cubic Bézier curve
in the XZ ground plane. This subsystem is the small, dependency-free layer that
answers every question the rest of the domain needs about those curves: where is
the point at parameter `t`, which way is it facing there, how far is that along the
curve in metres (not in `t`), where does it cross another curve or itself, and how
tight is its tightest bend. Nothing above it — network validation, junction/
connector building, traffic simulation, rendering — does its own curve math; it all
bottoms out here. A bug or sampling blind spot down here doesn't stay local: "Known
limits" and the worked example below both trace production bugs that surfaced in
higher layers but were rooted in a fixed sample count in this file.

## At a glance

**Source files**
- `src/Domain/Geometry/Bezier3.cs` — the curve type: evaluation, derivatives,
  tangent/normal, split, arc length.
- `src/Domain/Geometry/BezierOps.cs` — algorithms over `Bezier3`: tessellation,
  closest point, curve-curve intersection, self-intersection, min radius, and
  circular-arc construction (`ArcFromTangent`).
- `src/Domain/Geometry/ArcLengthTable.cs` — precomputed `t ↔ distance` lookup for one
  curve instance.
- `src/Domain/Geometry/GeoConstants.cs` — shared epsilon and two geometric floors.

**Key types**: `Bezier3` (readonly struct, 4 control points), `BezierOps` (static
algorithm namespace), `ArcLengthTable` (per-curve cache object), `GeoConstants`
(static constants).

**Used by** (via `rg`, not exhaustive):
- `Bezier3` itself — `RoadEdge.Curve`/`Lane` (`Network/Entities.cs`), `RoadNetwork.cs`,
  `ConnectorBuilder.cs`, `CurveFit.cs`, `Tools/Draft/Shapes.cs`,
  `Tools/Snapping/SnapEngine.cs`, `Traffic/TrafficSim.cs`, `Game/JunctionMarkings.cs`,
  `Game/MeshBuilders.cs`, `Game/VisualShots.cs`.
- `BezierOps.MinRadius` — placement floors: `NetworkInvariants.cs:66`,
  `RoadNetwork.cs:83,450`. `SelfIntersects` — `RoadNetwork.cs:86`. `Intersections` —
  crossing/junction discovery: `RoadNetwork.cs:96,394`, `ConnectorBuilder.cs:261`.
- `ClosestPoint` — split-point/snap projection: `RoadNetwork.cs:61,329`,
  `CurveFit.cs:50,68`, `SnapEngine.cs:241`. `Tessellate` — mesh only:
  `Game/MeshBuilders.cs:192,549`. `ArcFromTangent` — draft arc gesture:
  `Tools/Draft/Shapes.cs:143`.
- `ArcLengthTable` — `RoadEdge` caches one per edge at construction
  (`Entities.cs:37,46`, 128 samples); `ConnectorBuilder.cs:240-242` and
  `Traffic/TrafficSim.cs:28,508` build cheap 24-sample tables per junction
  connector; `Game/JunctionMarkings.cs:264,378` (24/32 samples, dash placement).

**Last verified against commit `f0542d7`, 2026-07-16.**

## How it works

### Curve evaluation, derivatives, and the degenerate-tangent fallback

`Bezier3` (`Bezier3.cs:10`) is a plain 4-point cubic; `Point`, `Derivative`, and
`SecondDerivative` (`Bezier3.cs:21-34`) are the textbook Bernstein-basis and
hodograph formulas — nothing decided here, just implemented. `Bezier3.Line`
(`:14-15`) builds a "straight" curve by putting the two interior control points at
1/3 and 2/3 of the chord, which is why the comment at `:6-9` calls a straight road "a
degenerate cubic": there is no separate line type anywhere in the domain, every road
edge is a `Bezier3`, and code that wants to special-case straight segments has to
detect them (see `IsFlat`, below) rather than switch on a type.

The one non-obvious piece is `Tangent` (`Bezier3.cs:39-53`). The raw derivative is
zero at a cusp or when two control points coincide (`Bezier3.FromQuadratic` can
produce this if the quadratic's control point sits on the chord). Rather than
returning a zero or NaN direction, `Tangent` falls back through three tiers: (1) the
analytic derivative if its length exceeds `GeoConstants.Eps` (compared squared to
skip a `Math.Sqrt`, `:42`), (2) a finite-difference sample `1e-3` inside the curve
from `t` in both directions (`:44-49`), (3) the chord `P3 - P0`, and only if that is
also zero, a hardcoded `Vector3.UnitX` (`:52`) — arbitrary but deterministic, so
callers never get a zero-length direction to normalize. Every junction-angle check,
connector tangent, and lane marking that asks "which way is this curve pointing" is
implicitly relying on `Tangent` never returning zero.

### NormalXZ, OffsetPoint, and the right-hand-traffic contract

`NormalXZ` (`Bezier3.cs:57-63`) computes `cross(tangent, +Y)` and zeroes the Y
component. `Vector3.Cross` in System.Numerics computes
`(a.Y·b.Z − a.Z·b.Y, a.Z·b.X − a.X·b.Z, a.X·b.Y − a.Y·b.X)`; for a curve travelling
`+X` (`tangent=(1,0,0)`) that gives `cross(tangent, up) = (0,0,1)`, i.e. `+Z` —
confirmed by `Bezier3Tests.cs:38-44`. This is the load-bearing convention documented
in `docs/conventions.md:5-9`: **lane offset > 0 is the driver's right when
travelling P0→P3, under right-hand traffic.** `OffsetPoint` (`Bezier3.cs:65`) is the
entire lane-geometry mechanism: a lane is never its own curve, it's the parent edge
curve plus a constant lateral `offset` evaluated through this one function at
whatever `t` the caller needs (lane markings, vehicle positions, connector
endpoints). Get the sign of `NormalXZ` wrong and every lane in the game silently
swaps sides.

### Split, Length, and why Length uses adaptive subdivision instead of a formula

`Split` (`Bezier3.cs:68-77`) is de Casteljau's algorithm — the standard way to cut a
cubic into two cubics that exactly reproduce the original curve on `[0,t]` and
`[t,1]`. There is no closed form for a cubic Bézier's arc length, so `Length`
(`:80-93`) recursively bisects: at each level it compares the control-polygon length
(`net`, sum of the 3 control-segment lengths) to the chord `P0`→`P3`; a flat curve
has `net ≈ chord`, and the recursion stops once `net - chord` is within
`1e-4 + 1e-3 * net` of zero (relative tolerance ~1e-3) or depth 16 is hit, returning
`(net + chord) / 2` for that piece. This is deliberately *not* precise to float
epsilon — it only needs to be good enough for length-floor checks and
speed-along-curve math — and `Bezier3Tests.CurveLengthMatchesNumericReference`
(`Bezier3Tests.cs:60-74`) pins the error against a 10,000-segment polyline to <1%.

### Arc-length parameterization — why `ArcLengthTable` exists

`Bezier3.Point(t)` is *not* arc-length-parameterized: equal steps in `t` do not mean
equal steps in distance travelled, especially where curvature is uneven along the
curve. But dashed lane markings, vehicle `S` positions, and "distance along the
curve" spacing all need to reason in metres, not in `t`. `ArcLengthTable`
(`ArcLengthTable.cs:5`) bridges the two: its constructor (`:11-22`) walks the curve
at `samples` (default 128) uniform-`t` steps, accumulating a cumulative-distance
array; `TotalLength` is that array's last entry. `DistanceAtT` (`:24-30`) linearly
interpolates within the table by `t`-fraction; `TAtDistance` (`:32-45`) is the
inverse — binary search to bracket the target distance, then linear interpolation
within that bracket. Callers pick their own sample count as a speed/accuracy trade:
`RoadEdge` caches a 128-sample table for the whole edge lifetime
(`Entities.cs:37,46`, built once), while `ConnectorBuilder` and `TrafficSim` build
cheap 24-sample tables per short junction connector (`ConnectorBuilder.cs:242`,
`TrafficSim.cs:508`), since those are rebuilt whenever the network topology changes
(the "connector indices are per-rebuild" gotcha, `docs/gotchas.md:64-66` — [FWD:
[ch. 05](05-traffic-sim.md) traffic]).

### Tessellate and ClosestPoint

`Tessellate` (`BezierOps.cs:13-31`) is an adaptive-flatness subdivider for mesh
generation only (its sole callers are `Game/MeshBuilders.cs:192,549`): it bisects
`[t0,t1]` while the curve's midpoint deviates from the chord midpoint by more than
`chordTolerance`, capped at depth 12, returning the sorted `t` values actually
needed to draw the curve within tolerance — flat road stretches get almost no
extra points, sharp bends get many.

`ClosestPoint` (`:34-57`) is coarse-to-fine: 64 uniform samples find the
nearest-sample bucket, then 40 ternary-search iterations narrow a
`[bestT - 1/64, bestT + 1/64]` window. Ternary search assumes a single minimum
inside that window, which is only guaranteed locally — the coarse pass exists to
land near the *global* minimum's basin first.

### Intersections — recursive AABB subdivision, and why it can legitimately fire 3 times

`Intersections` (`:61-101`) finds every XZ crossing between two curves by
recursively splitting whichever curve has the larger bounding-box extent (`Extent`,
`:183-187`) until both sides are "flat" (`IsFlat`, `:166-181` — perpendicular
distance of both interior control points to the chord under `FlatTol = 1e-3`) or
`depth >= MaxDepth` (28), then solving the two flat segments as straight lines
(`SegmentIntersect`, `:206-218`, a parametric line-line solve with a `1e-12`
parallel-denominator guard). Recursion is pruned by `AabbOverlap` (`:196-203`, `Eps`
pad so touching boxes still compare at the boundary). Hits within `1e-3` of an
already-found hit are dropped (`:80`) — the same true crossing can be approached
from adjacent leaf-segment pairs near a subdivision boundary and double-counted.
`docs/gotchas.md:50` records the sharpest gotcha here: **a cubic can cross a
straight line three times** (it's a degree-3 curve) — code elsewhere must not "fix"
an intersection count of 3 as a bug.

### SelfIntersects and its known false positives

`SelfIntersects` (`:104-119`) samples the curve at 32 fixed uniform-`t` spans,
builds the resulting 33-point XZ polyline, and checks every non-adjacent segment
pair (`j >= i + 2`, skipping the first/last pair since open curves don't close) for
a straight-line crossing via the same `SegmentIntersect`. This is a polyline
approximation of self-intersection, not an exact curve test, and per
`docs/gotchas.md:50-57` it has a **known intermittent false-positive bug**:
exactly-straight `Bezier3.Line` curves at certain absolute angles (20°, 27°, 28°,
31°, 33°, 35°, 40°, 45° observed) get flagged as self-crossing even though a
straight segment cannot cross itself. `[UNCERTAIN]` on the exact mechanism — not
traced, but the likely cause is float rounding in `Point(t)` at those angles
producing two near-collinear polyline vertices close enough to
`SegmentIntersect`'s `1e-6` tolerance band that the `j >= i+2` adjacency skip isn't
enough separation to rule out a near-degenerate "crossing." Discovered during M4
Task 5 and deliberately left unfixed (out of scope for that milestone) — new visual
scenarios should avoid those angles rather than "fix" a scenario around the bug; it
can also reject legitimate user-drawn roads at those exact headings.

### MinRadius — curvature sampling, and the M6 arc-length-adaptive fix

`MinRadius` (`:133-149`) samples curvature `κ = |x'z'' - z'x''| / speed³` (the
standard planar curvature formula from the first and second derivatives, restricted
to the XZ plane) at `n+1` uniform-`t` points and returns `1/max(κ)` — the tightest
radius of curvature anywhere on the sampled curve, `+Infinity` for a curve so
straight `max(κ) < 1e-9`. This is what road-type floors are checked against
(`type.MinRadius` in `docs/conventions.md`'s per-type table, e.g. OneWay 10 m,
FourLane 35 m).

Before M6 this always sampled a **fixed 32 points regardless of curve length**. That
was fine for the short segments most drawing gestures produce, but broke down for a
long edge shortened progressively in place: `RoadNetwork.SplitEdgeWithReuse` can cut
an edge down without changing its underlying curve shape (a legal node-reuse
absorption), and each re-measurement of `MinRadius` after a split resamples the
*same unchanged geometry* over a *different, shorter* range with the *same*
32-point budget — the samples get denser after each split. A short, sharp bend
that sat entirely between two of the original widely-spaced samples (invisible to
the original measurement) can fall on a sample after a later split, and the
reported minimum radius drops — for geometry that never changed shape, only got
measured more precisely. `AdaptiveSampleCount` (`:155-162`) is the M6 fix: when
`samples` isn't given, the count is `clamp(ceil(Length() / 8m), 32, 4096)` — one
sample per ~8 m of arc length, floored at the old fixed-32 value (short curves are
unaffected) and ceilinged at 4096. See "Worked example" for the fuzz-regression
numbers this fixes.

### ArcFromTangent — circular-arc construction for the draft tool

`ArcFromTangent` (`:224-286`) is the one *constructive* algorithm here (the rest
query existing curves): given a start point, a required departure tangent, and an
end point, it finds the unique circle through both that leaves `start` along
`tangent`, returned as one cubic (sweep ≤ 90°) or two matched cubics (up to the
175° cap) via the standard `k = 4/3 · tan(Δ/4) · r` handle-length identity for
approximating a circular arc with a cubic Bézier. The signed lateral offset `h` of
the end point relative to the tangent line (`:239-240`) picks which side the
circle's center falls on, hence the rotation sense; if `h` is within
`1e-3 * |chord|` of zero the points are collinear, returning a straight
`Bezier3.Line` (end ahead) or `null` (end behind — unrepresentable as one simple
arc). Sweeps beyond 175° also return `null` (`:256-257`) — a deliberate draft-tool
design cap, not a mathematical limit; near-360° loop-arounds are composed by the
caller from multiple gestures. `[UNCERTAIN]` on why 175° specifically — check
`docs/superpowers/specs` for the arc-gesture spec.

## Worked example

Take the exact curve from `BezierOpsTests.MinRadiusRecoversCircleRadius`
(`BezierOpsTests.cs:113-126`): a single cubic approximating a 90° arc of radius
`r = 50` centered at the origin, built with the standard handle length
`k = 4/3 · tan(π/8) · r ≈ 27.614`:

```
P0 = (50, 0, 0)         P1 = (50, 0, 27.614)
P2 = (27.614, 0, 50)    P3 = (0, 0, 50)
```

- `Point(0.5)` ≈ `(35.355, 0, 35.355)`, landing almost exactly on the true circle
  (`50·cos45°, 50·sin45°`) — a 4-point cubic fit of a 90° arc is very close at the
  midpoint.
- `Derivative(0.5)` ≈ `(-54.29, 0, 54.29)` → `Tangent(0.5) ≈ (-0.7071, 0, 0.7071)`,
  perpendicular to the radius vector, in the CCW travel direction (curve runs `+X`
  axis → `+Z` axis).
- `NormalXZ(0.5) ≈ (-0.7071, 0, -0.7071)` — points *toward the circle's center*.
  Reminder that "driver's right" isn't universally "outward": on a curve turning
  left (CCW, as here) positive offset moves *toward* center; on a CW bend, away.
  `OffsetPoint(0.5, 5)` ≈ `(31.82, 0, 31.82)`, distance `45.0` from the origin —
  5 m closer to center, confirming the sign.
- `Length()` ≈ `78.54 m` (true quarter-circumference `π·50/2 = 78.5398`; the gap is
  cubic-circle approximation error, far below `Length()`'s `1e-3` relative target).
- `MinRadius` at `t=0.5`: hand-computed curvature `κ ≈ 0.01988` → radius `≈ 50.3 m`,
  within the test's ±2% band — a reminder that a cubic circle approximation has
  *not-quite-constant* curvature, exactly why `MinRadius` samples rather than
  evaluating once.
- This curve is 78.5 m long, so `AdaptiveSampleCount` gives `ceil(78.54/8) = 10`,
  clamped up to the `minSamples = 32` floor — the M6 change is a no-op here; it
  only changes behavior above ~256 m (`32 * 8`).

Now the case the M6 change actually fixes, from
`FuzzRegressionTests.Seed303LongEdgeSplitRevealsTrueMinRadiusRegardlessOfSampleSpacing`
(`FuzzRegressionTests.cs:219-240`): an OneWay edge (10 m radius floor) roughly 500 m
long, containing one short, sharp bend. At commit time, 32 fixed samples over 500 m
(~15.6 m apart) straddled the bend and measured a safe-looking `10.29 m` — passing
the 10 m floor. Three legal `SplitEdgeWithReuse` absorptions later, the same
unchanged geometry is now under 400 m; each re-validation resampled 32 points over
the *new*, shorter length, and the third split finally landed a sample on the bend,
reporting its true radius: `9.86 m` — below the floor, for geometry that was never
reshaped. With the fix, the *original* 500 m edge is sampled at
`clamp(ceil(500/8), 32, 4096) = 63` points (~8 m apart) from the start, catching the
sub-floor bend immediately instead of only after later splits force denser
resampling.

## Invariants

- **`Tangent` never returns a zero-length or NaN direction** (`Bezier3.cs:39-53`).
  Every offset, connector-tangent, and junction-angle computation in the network
  and traffic layers assumes this.
- **`NormalXZ`'s sign is the single source of truth for "driver's right"**
  (`Bezier3.cs:57-63`, `docs/conventions.md:5-9`). Lane offsets, ghost-arrow
  drawing, and vehicle lateral position all key off it; flip the sign and every
  lane in the game silently swaps sides.
- **`Split` reproduces the original curve exactly** on both sub-ranges (de
  Casteljau is exact, not approximate) — `Intersections` and `Tessellate` recurse
  via `Split` and rely on the halves never drifting from the parent curve
  (`Bezier3Tests.SplitHalvesMatchOriginal`, `Bezier3Tests.cs:47-57`).
- **`ArcLengthTable.DistanceAtT`/`TAtDistance` are monotonic and mutually inverse**
  to within table resolution (`ArcLengthTableRoundTrips`, `BezierOpsTests.cs:83-90`)
  — dash placement and vehicle `S`-to-`t` conversion depend on this round-trip.
- **A cubic may cross a line up to 3 times; `Intersections` must report all of
  them** (`docs/gotchas.md:50`).
- **`MinRadius`'s sample resolution must scale with curve length** when no explicit
  count is given, or long, repeatedly-split edges can under-report their tightest
  bend — the M6 invariant this file's history revolves around.
- **`ArcFromTangent` returns curves anchored exactly at `start`/`tangent`/`end`, or
  `null`** — callers (`Tools/Draft/Shapes.cs:143`) never need to validate output
  geometry, only handle `null`.

## Tuning constants

| Constant | Value | Where | Rationale |
|---|---|---|---|
| `GeoConstants.Eps` | `1e-4` m | `GeoConstants.cs:6` | Shared "basically zero" epsilon for lengths/vectors across geometry, snapping, curve fitting; sub-millimeter, far below any placement tolerance. |
| `GeoConstants.MinEdgeLength` | `4` m | `GeoConstants.cs:9` | Documented as a curve-math floor, but `rg` finds no production caller — only pinned by `SmokeTests.cs:11`. Per-road-type `MinSegmentLength` (`docs/conventions.md`, 8-21 m) is the floor that actually gates placement; treat this as legacy. |
| `GeoConstants.MergeTolerance` | `0.05` m | `GeoConstants.cs:12` | Max deviation allowed when re-fitting two edges into one merged curve (`CurveFit.cs:46`, `RoadNetwork.cs:597`). |
| `BezierOps.FlatTol` | `1e-3` m | `BezierOps.cs:8` | "Flat enough to treat as a line" threshold in `IsFlat` (`:166-181`), used only by `Intersections`' subdivision stop — `Tessellate` uses its own `chordTolerance` param. |
| `BezierOps.MaxDepth` | 28 | `BezierOps.cs:9` | Recursion cap for `Intersections`; bounds worst-case cost per curve pair. |
| `Length()`'s subdivision tolerance | rel. `1e-3`, abs. `1e-4`, depth 16 | `Bezier3.cs:88` | 16 levels (up to 65536 leaf segments) always terminates well before the cap in practice. |
| `Tessellate`'s depth cap | 12 | `BezierOps.cs:25` | Belt-and-suspenders bound alongside the caller-supplied `chordTolerance`. |
| `ClosestPoint` samples / refine iters | 64 / 40 | `BezierOps.cs:36,47` | 64 samples find the right unimodal basin; 40 ternary-search iterations narrow it — cheap enough for per-frame snapping. |
| `SelfIntersects` sample count | fixed 32 spans | `BezierOps.cs:106` | **Not** touched by the M6 fix — still a fixed grid, same false-positive/miss risk profile described above. |
| `MinRadius`'s adaptive step | 8 m/sample, floor 32, ceiling 4096 | `BezierOps.cs:157-159` | M6 fix: constant per-metre curvature resolution across lengths; floor preserves old behavior on short curves. |
| `ArcFromTangent`'s max sweep | 175° | `BezierOps.cs:226` | Draft-tool design cap, not a mathematical limit. `[UNCERTAIN]` why 175° vs. e.g. 170°/179° — check `docs/superpowers/specs`. |
| `ArcLengthTable` sample counts in use | 128 (edge default), 32/24 (markings/connectors/traffic) | `ArcLengthTable.cs:11`, `ConnectorBuilder.cs:242`, `TrafficSim.cs:508`, `JunctionMarkings.cs:264,378` | Long-lived edges get the dense default built once; short-lived junction connectors (rebuilt every topology edit) use a cheap 24-sample table. |

## Known limits

- **`SelfIntersects` false-positives on certain straight-line angles** — 20°, 27°,
  28°, 31°, 33°, 35°, 40°, 45° observed so far (`docs/gotchas.md:51-57`,
  `BezierOps.cs:104-119`). Discovered during M4 Task 5, explicitly deferred. Not
  cosmetic: it can reject a legitimate user-drawn straight road at one of these
  headings. New visual scenarios should avoid those angles rather than "fix" a
  scenario around the bug. A real fix would need an exact (non-polyline-sampled)
  self-intersection test or a wider adjacency-exclusion band. The same fixed
  32-span grid was explicitly out of scope for the M6 arc-length-adaptive change
  (which only touched `MinRadius`), so a very long, mostly-straight curve with a
  short self-crossing loop could in principle hide it between two samples —
  `[UNCERTAIN]` whether this is latent or already prevented by length/radius
  floors; would need a targeted fuzz seed to confirm.
- **`GeoConstants.MinEdgeLength` is effectively dead code** in production paths (see
  Tuning constants); still pinned by a smoke test, so new code should not treat it
  as a real floor.
- **`ArcFromTangent`'s 175° sweep cap** means near-U-turn single-gesture arcs are
  unsupported by design — the draft tool composes multiple arcs for anything
  tighter. Not a bug, but worth knowing before "fixing" a gesture that refuses a
  valid-looking curve.

## How to verify

- `dotnet test --filter FullyQualifiedName~Domain.Tests.Geometry` runs
  `tests/Domain.Tests/Geometry/Bezier3Tests.cs` and
  `tests/Domain.Tests/Geometry/BezierOpsTests.cs` — evaluation, tangent/normal
  sign, split correctness, arc-length round-trip, intersection counts,
  self-intersection true/false cases, `MinRadius` on a known circle and a
  wide-vs-tight comparison, and all four `ArcFromTangent` sweep cases.
- `tests/Domain.Tests/Fuzzing/FuzzRegressionTests.cs` —
  `Seed303LongEdgeSplitRevealsTrueMinRadiusRegardlessOfSampleSpacing` (`:236-240`)
  pins the M6 adaptive-sampling fix against the exact 500 m-edge scenario in the
  worked example; it replays the full `GestureFuzzer`, so it also exercises
  `RoadNetwork.SplitEdgeWithReuse` end-to-end.
- `dotnet test` (full suite) before any change here — this subsystem has no Godot
  dependency and no screenshot/motion harness of its own; correctness is fully
  covered by the domain unit and fuzz-regression tests above. Visual symptoms of a
  geometry bug (wrong lane side, corrupted junction curves) would surface in the
  screenshot harness for chapters 03/04/07, but the root-cause fix and regression
  test belong here.
