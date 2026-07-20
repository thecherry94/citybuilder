# Gotchas

Hard-won. Read the relevant section before touching that area.

## Godot rendering
- **Front faces are CLOCKWISE.** Flat meshes need orientation-normalized winding
  (`AddTriangleUp` / `Tri(..., n)` helpers swap vertices by cross-product sign) or the
  shadow pass blackens them.
- **Never fan-triangulate a junction polygon from the node** — the node can lie outside
  the polygon (acute Y "beak"). Use `Geometry2D.TriangulatePolygon`, include node arcs
  on reflex wedges.
- **`SurfaceTool.GenerateNormals` smooths everything by default** — assign one
  `SetSmoothGroup` per cross-section band or adjacent meshes look "stitched".
- **Junction outlines must use `RoadType.OuterHalf`** (sidewalks' real outer edge), not
  `Width/2` — a 0.5 m mismatch shows as corner steps.
- **Sub-pixel geometry vanishes without MSAA**: 0.15 m road markings disappear in
  top-down shots beyond ~65 m camera distance. `Msaa3D = Msaa4X` is set in Main.
  "No output rendered" in a distant shot is not proof geometry is missing — zoom in or
  dump quads.
- Materials are embedded per surface (`SurfaceTool.SetMaterial`); per-node junction
  meshes combine asphalt + paint + props as surfaces of one ArrayMesh.

## Godot process / harness
- **`OS.GetCmdlineArgs()` strips engine-recognized flags** (`--headless`,
  `--editor-pid`), and `System.Environment.GetCommandLineArgs()` is EMPTY under Godot's
  embedded .NET host. To detect editor-launched play instances use
  `EngineDebugger.IsActive()` OR `--editor-pid` in `/proc/self/cmdline`.
- **Env vars leak into a Godot editor launched from a dev shell** and hijack the play
  button (CITYBUILDER_SHOTS once turned every play into the screenshot suite). Main.cs
  ignores harness env vars when launched from the editor — keep that guard.
- Headless shots mode is **silent until completion** — absence of output does not mean
  it didn't trigger; check whether the shots directory was created.
- `grep -c` exits non-zero on zero matches and silently breaks `&&` chains in verify
  scripts — don't chain the next command on it.
- UI anchoring: use `SetAnchorsAndOffsetsPreset(...)`; combining `SetAnchorsPreset` with
  a manual `Position` + `GrowHorizontal` put a panel entirely off-screen while
  `Visible == true`.

## Domain geometry
- **Degree-2 bends need corner returns on BOTH sides** (offset-line intersection behind
  the node for the outer/reflex side). A `NodeArc` there bulges the outside of the turn
  ~2 m and makes the lanes visually unequal even though the centreline is exact.
- **Dash phasing can hide curve apexes**: pattern-centering placed a gap exactly on the
  bend apex, so the painted line read as an inner-hugging chord. Corner continuations
  pin a dash onto the apex (`SweepLine(..., centerOnApex: true)`).
- Marking sweep offsets of a single curve cusp on the inside of wide roads — each
  marking line gets its own corner quadratic (same construction as curb returns).
- Healing fits must be tangent-constrained with closest-point reparameterization
  (≤16 iterations); free-control least squares biases outward.
- A cubic can legitimately cross a line 3 times — don't "fix" intersection counts.
- **Known issue:** `BezierOps.SelfIntersects` has pre-existing intermittent false
  positives on exactly-straight lines at certain angles (20/27/28/31/33/35/40/45° seen
  so far) — its sampled-segment check occasionally flags near-collinear adjacent spans as
  crossing. Discovered during M4 Task 5, out of scope for that milestone. Avoid those
  angles in new visual scenarios rather than "fixing" a scenario into the bug — and
  note it can reject *real user placements* at those angles, so it needs a proper fix
  eventually.

## Traffic sim
- **`Vehicle.S` is the front bumper.** The rendered centre trails by half a length and
  must keep rendering on the *previous* segment (`PrevLane`/`PrevCrossing`) until it
  clears the boundary — clamping instead caused ~2.25 m teleports at every junction,
  and linear extrapolation is wrong behind tight U-turn connectors.
- **Connector indices are per-rebuild.** Any network edit invalidates
  `(NodeId, connectorIndex)` references: recompute `PlannedConnector` for every vehicle,
  drop `Prev*` pose history, despawn vehicles mid-crossing.
- **Never enter a junction mid-lane-change** (the lateral interpolation would snap);
  changes only start when they can finish before the cut.
- Spawn speed must respect braking distance behind the lane's tail vehicle, or
  physically unavoidable rear-endings occur.
- IDM is only collision-free under bounded leader deceleration — external speed clamps
  (tests, future scripted events) require the hard non-penetration clamp that runs
  after integration. Keep it.
- Turn-lane assignment lives in `ConnectorBuilder`: restricting turns per lane is what
  makes mandatory lane changes meaningful; degree-2 bends and dead-end U-turns stay
  unrestricted or lanes become unreachable (strong-connectivity tests catch this).
- Signals: `IsGreen` is consulted at entry; amber blocks. Controllers persist across
  unrelated network edits (phase timers survive), rebuilt only when the node's mode
  changes.
- **Never order lane groups by `|offset|` — it breaks on direction-asymmetric lanes**
  (M5's OneWay, both driving lanes `Forward` at ±1.75 m; Asymmetric 2+1, mixed
  directions at −4.25/−0.75/+2.75 m). Absolute-offset ordering misorders left/right
  relative to travel direction whenever a same-direction lane group spans 0. Use
  direction-aware *signed* ordering instead: `OrderBy(Offset)` for `Forward` groups,
  `OrderByDescending(Offset)` for `Backward` groups. The two fixed sites are
  `TrafficSim._adjacent` (left/right neighbor for lane changes) and
  `ConnectorBuilder.laneRank` (left→right rank for turn-lane assignment) — they
  cross-reference each other in comments; check both if you touch lane adjacency logic.
- **`LaneGraph.IsStronglyConnected` must be scoped to a `LaneKind`** on any network that
  can carry sidewalks (e.g. OneWay). `ConnectorBuilder` never links `Sidewalk`/`Bicycle`
  lanes to `Driving` ones by design (they're deliberately separate graphs), so an
  unscoped all-kinds check fails the instant a sidewalk-carrying type enters the network
  regardless of the driving topology's actual connectivity. Call with
  `kind: LaneKind.Driving` (see `Main.cs`'s smoke connectivity check) and, if you need to
  verify sidewalks/bike lanes too, check each kind separately.

## Junction control
- Auto main-road heuristic scores by **corridor width (`OuterHalf`)**, not carriageway
  width — otherwise a bare 8 m country road outranks a 12 m street with sidewalks.
- `RoleOverrides`/`LegOffsets` are `EdgeId`-keyed and silently pruned on splits — a
  rebuilt leg falls back to heuristics (documented, accepted).
- Resize shrink floors at the solved corner requirement (geometry cannot fold); the
  30 % edge-length clamp + `TightCuts` sit on top of authored offsets.

## Invariant checking & fuzz certification (M7.5 hardening lessons)
- **A fuzz pass only certifies what the invariant checker can see.** The original M7.5
  roundabout conversion stamped ring arcs across bystander roads for weeks of fuzz
  actions while 3×10k runs stayed green — `NetworkInvariants` simply had no rule about
  disjoint edges crossing. When you add a new mutation path, ask *which invariant would
  catch its failure modes* before trusting fuzz numbers; if the answer is "none", add
  the invariant first and expect it to light up immediately (ours did, on all three
  seeds, within 400 actions).
- **`BezierOps.Intersections` lies at the margins — twice.** (1) For near-collinear
  curves touching at a shared endpoint (chain segments) it can return hits with garbage
  parameters: the reported `(t1, t2)` points sit metres apart and metres from the true
  contact. Always verify `a.Point(t1) ≈ b.Point(t2)` before treating a hit as geometry.
  (2) Its results are parametrization-direction-sensitive: flipping an edge (same
  geometry, reversed control points) can move or reveal hits. Never assume
  `Intersections(a,b)` and `Intersections(a.Reversed(),b)` agree.
- **Distance-to-center along a committable curve is not monotonic.** A hook-shaped leg
  clearing every catalog floor (MinRadius ≥ 10, no self-intersection) can cross a circle
  three times. Any "find the crossing by bisection" code must bracket the *first*
  crossing by marching from a known-outside end, or it converges to an arbitrary one.
- **A curve's tangent bearing at a point is not the bearing of where it goes.** Placing
  roundabout slots at the leg's center-tangent bearing bound approaches to nodes their
  curves missed by metres. When geometry must meet a node, derive the node's position
  from the geometry (the actual crossing), never from a direction extrapolation.
- **Validate's "connection at an endpoint" exemption assumes the endpoint actually
  connects to the near edge — ResolveBinding may disagree.** A proposal endpoint within
  0.5 m of an existing edge exempts crossings there, but if an existing NODE also sits
  in range, ResolveBinding binds to the node and never splits the edge — committing a
  genuine transversal crossing 0.4 m from the node (fuzz seed 202@8673, a 64° drive-
  through). The commit-side segment recheck (`SegmentCrossesLiveEdgeOffNode`) therefore
  exempts near-endpoint contact only for edges *incident to the endpoint's node*; a
  non-incident edge crossing anywhere is a drop. SubCurve's displacement blending after
  reuse absorption can likewise drag a segment into re-crossing the edge whose crossing
  was absorbed (seed 101@8321) — same recheck catches it. These joined the floors and
  sharp-leg rechecks as the third member of the commit-side "drop, never commit corrupt"
  family.
- **Node-attached meshes must be node-plane-relative, never absolute-Y.** Junction
  boundary skirts dropped their outer vertices to `Y=0`, corner zones flattened to
  `topY = SurfaceY + rise`, and markings/props OVERWROTE curve-derived Y with constants
  — all invisible at ground level, all catastrophically wrong at +10 m (curtain walls
  from deck to ground, signals standing under the bridge). Pattern: `pos.Y += offset`
  against the geometry's own Y, or pass the node's `Position.Y` as the base; grep for
  `\.Y = ` in `src/Game` when adding node-attached visuals.
- **Everything below the ground plane is invisible unless the ground yields there.**
  The first trench gallery pass rendered a −4 m cut as a hairline seam: walls, coping,
  and the sunken road were all under the opaque `Ground` plane. The follow-up
  translucent "fake hole" strip was equally blind — it tinted the grass, but the depth
  test had already discarded the road behind the plane. Open cuts therefore punch real
  holes: the strips render white into `Main.BuildGround`'s top-down mask viewport and
  both ground shaders `discard` where the mask is set. X-ray still exists because a
  *covered* tunnel has no opening at all. If terrain ever lands, delete the strip/mask
  (isolated as the `cut` surface in `StructureView.BuildStructures`) and cut real holes.
- **Anything cursor-driven must be plan-view (XZ), not 3D distance.** The cursor
  lives on a horizontal plane, so `FindClosestEdge` (3D) put a ±10 m deck 10 m away
  before any lateral error — upgrade/bulldoze/inspect silently couldn't hover bridges
  from M8 on (covered-toggle UITEST caught it), and the SNAP ENGINE had the same bug
  for another milestone: elevated/tunnel end nodes were uncapturable, so those roads
  could never be continued (user find, 2026-07-20). Use `FindClosestEdgeXZ` /
  `FindNodeNearXZ`, and in `SnapEngine` XZ distances everywhere; stacked targets
  tie-break toward `preferredY` (ground for tools, the draft elevation for snapping —
  `XZPickingTests`, `SnapEngineTests.StackedNodesResolveTowardPreferredElevation`).
