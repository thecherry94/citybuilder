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
