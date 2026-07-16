# Rendering & markings

`src/Game` is the only layer in this codebase allowed to know about `Godot`. Every
class in this chapter — `RoadNetworkView`, `MeshBuilders`, `JunctionMarkings`,
`TrafficView`, `SignalLampView`, the debug overlays — renders `src/Domain` state and
forwards player input back into it. None own game state: a view holds a reference to a
domain object (`RoadNetwork`, `TrafficSim`) and a cache of Godot nodes keyed by domain
id, nothing more. The domain never calls into a view directly; it raises one aggregated
`Changed` event per mutation batch (`RoadNetwork.Changed`, `RoadNetwork.cs:34`, [ch. 02](02-network-validation.md))
and the view resyncs itself from the delta. This is what makes quick-load ([ch. 08](08-persistence.md)) need
no view-side special case: loading a save mutates the same `RoadNetwork` object in
place and fires the same event, so every subscribed view just rebuilds whatever the
delta says changed (`Main.QuickLoad`, `Main.cs:164-185`). The pattern for a new view:
bind once, subscribe to `Changed`, never hold a stale mesh.

## At a glance

- **Sources:** `RoadNetworkView.cs` (161 lines), `MeshBuilders.cs` (565),
  `JunctionMarkings.cs` (484, plus `ArrowGlyph`), `TrafficView.cs` (62),
  `SignalLampView.cs` (95), `Materials.cs` (132); domain side
  `src/Domain/Catalog/MarkingRules.cs` (52). Skimmed: `GhostView.cs`,
  `LaneDebugOverlay.cs`, `GridOverlay.cs`, `JunctionProps.cs`, `Main.cs` scene wiring,
  `ToolController.cs` view calls, `VisualShots.cs` harness.
- **Entry points:** `RoadNetworkView.Bind`/`.FlushDirty()`; `MeshBuilders.BuildEdgeMesh`/
  `.BuildJunctionMesh`; `JunctionMarkings.Build`; `TrafficView.Bind`;
  `SignalLampView.Bind`.
- **Called by:** `Main._Ready()` wires every view once at scene start (`Main.cs:71-104`);
  `ToolController` ([ch. 06](06-drafting-snapping.md)) drives `GhostView`/`RoadNetworkView.HighlightEdge` per frame.
- **Depends on:** `RoadNetwork.Changed`/`NetworkDelta` (ch. 02), `RoadType`/`LaneSpec`,
  `JunctionGeometry`/`Corners`/`SurfacePolygon` ([ch. 03](03-junctions-control.md)), `Bezier3`/`ArcLengthTable`
  ([ch. 01](01-geometry.md)), `TrafficSim.Pose`/`PhaseFor` ([ch. 05](05-traffic-sim.md)).
- **Last verified against commit:** `f0542d7` on 2026-07-16.

## The view resync model

`RoadNetworkView` is the reference implementation of the resync pattern. It keeps two
`Dictionary<Id, MeshInstance3D>` caches (`_edgeInstances`, `_nodeInstances`,
`RoadNetworkView.cs:13-14`) and two dirty sets (`_dirtyEdges`, `_dirtyNodes`), touched
only by `OnChanged` (`RoadNetworkView.cs:25-49`): `EdgesRemoved`/`NodesRemoved` free the
corresponding `MeshInstance3D` and drop it from the cache; `EdgesAdded` and the union of
`NodesAdded`/`NodesChanged` mark entries dirty rather than rebuilding immediately. A
second-order rule is easy to miss on a first read: when a node is added or changed,
every edge meeting it is also marked dirty (`RoadNetworkView.cs:44-47`), because
junction rebuilds can move the *cut* point — the parametric `t` where an edge's asphalt
strip stops and the junction polygon begins (`RoadNode.Junction.CutT`, ch. 03) — on
each connected edge, so a neighbor's growing/shrinking junction forces a remesh even
when the edge's own geometry didn't change.

Dirty entries are flushed once per frame from `_Process` via `FlushDirty()`
(`RoadNetworkView.cs:51-65`), also called directly by the smoke test and `VisualShots`
so screenshot scenarios don't wait a frame. `GetOrCreate` (`RoadNetworkView.cs:105-114`)
is the only place `MeshInstance3D` nodes are constructed; rebuilding just replaces
`inst.Mesh`, never the node itself, so a `MaterialOverride`/tint set elsewhere survives
a remesh unless explicitly cleared (see `SetEdgeTint` below — exactly the assumption it
preserves).

This is also why `RestoreInto`/`LoadInto` (`RoadNetwork.Persistence.cs:20`, ch. 08)
needs no view-side rebinding: it clears and repopulates the entire graph inside one
`BeginBatch()`/`EndBatch()` pair, so a quick-load looks to the view like one very large
`NetworkDelta`. `RoadNetworkView` doesn't know or care that the ids happen to match what
was there before the load; it just frees what's gone and rebuilds what's dirty. The one
leak is transient, non-domain state that caches ids across frames — `Main.QuickLoad`
explicitly calls `_traffic.EnsureSynced()` and `_controller.ClearTransientState()`
afterward (`Main.cs:174-178`) because vehicle poses and an in-progress draft gesture
aren't part of `RoadNetwork` and don't get rebuilt by the `Changed` event.

## Road meshes & markings

`MeshBuilders.BuildEdgeMesh` (`MeshBuilders.cs:34-60`) is the per-edge mesh factory,
driven entirely by the road type's `LaneSpec` list rather than special-casing per
`RoadTypeId` — add a lane to the catalog and every downstream builder (asphalt width,
sidewalks, bike tint, markings) picks it up with no separate code path. It samples the
edge's `Bezier3` via `BezierOps.Tessellate(curve, ChordTolerance)`
(`ChordTolerance = 0.15f`) restricted to the `[tStart, tEnd]` cut window, then builds
four independent surfaces combined onto one `ArrayMesh`: asphalt (skirted on open ends,
tucked half a centimeter under the curb so no crack shows — `MeshBuilders.cs:93`),
raised concrete sidewalks that ramp down over `CurbRampLength` (1.4 m) at dead ends
only (junctions keep sidewalks flush, `RoadNetworkView.cs:69-70`), a tinted bike-lane
strip, and painted markings. Each surface gets its own `SetSmoothGroup` per
cross-section band (`MeshBuilders.cs:77`) — `SurfaceTool.GenerateNormals()` smooths
across *all* triangles in a surface by default, so without per-band groups top and
skirt would blend into one soft-shaded ramp instead of a sharp curb edge (a
`docs/gotchas.md` Godot-rendering entry).

Markings are computed twice, deliberately split across the domain/game boundary:
`MarkingRules.Layout(RoadType)` (`src/Domain/Catalog/MarkingRules.cs:13-51`) is pure
C#, testable headlessly, and answers *where* the paint goes and whether it's dashed;
`MeshBuilders.BuildMarkings` (`MeshBuilders.cs:207-230`) turns that into geometry.
`Layout` walks driving lanes sorted by `Offset` and, between each adjacent pair,
decides the boundary: same direction → dashed lane separator; opposite direction on a
≤2-lane road → dashed center line; opposite direction on a wider road → a double solid
line offset ±0.18 m either side of true center. Outside the outermost driving lane on
each side it looks for the next lane out: nothing → a solid "rural edge line" inset
0.4 m (`EdgeLineInset`); a bike lane → a solid separation line at the carriageway edge;
a sidewalk → no paint, since the curb is the visual boundary. This stays valid for
direction-asymmetric types (M5's `OneWay` and `Asymmetric` 2+1) without a special case
because it works from lane adjacency and `Offset`, never an assumed symmetric
centerline — the same "never trust `|Offset|`" lesson [ch. 04](04-lane-graph-connectors.md) documents for
`ConnectorBuilder`'s lane ranking shows up here as "never assume the opposing boundary
is offset 0" (`MarkingRules.cs:6-8`).

One-way roads (`type.BackwardCount == 0`) additionally get forward-direction arrows
painted on every driving lane every 30 m (`AddLaneArrows`, `MeshBuilders.cs:289-302`),
an M5 addition. The shaft-then-head geometry is built directly in the edge's own
arc-length parametrization: the shaft always points from `dStart` toward
`dStart + shaft + head`, i.e. along increasing arc length, which is only correct
because one-way lanes are guaranteed `Forward` — there's no separate direction flag.

Junction corner geometry is the harder case: `BuildJunctionMesh`
(`MeshBuilders.cs:327-379`) triangulates `node.Junction.SurfacePolygon` with
`Geometry2D.TriangulatePolygon`, never a fan from the node's own position — a
junction's true center can sit outside its own polygon at an acute "beak" — falling
back to a centroid fan only if triangulation itself fails on a degenerate outline
(`MeshBuilders.cs:353-362`). `JunctionMarkings.Build` (`JunctionMarkings.cs:34-118`)
paints on top of that mesh per-approach: a stop line, yield "shark teeth," or nothing
depending on `JunctionControl.Resolve`'s role for that leg (ch. 03); a turn arrow per
incoming lane built from `ArrowGlyph`, authored in a local frame (+Y toward the
junction, +X to the driver's right) and direction-aware because `moves` comes straight
from that lane's actual `Connector.Turn` set — a lane restricted to right-only by
`ConnectorBuilder`'s turn-lane assignment (ch. 04's central bug fix) never gets a
straight arrow, and repaints automatically the next time the node is dirtied since
markings are rebuilt from the same node-rebuild path, never cached separately.
Left-turn guidance dashes run along the connector curve's left edge, one per
approach→exit pair (the tightest connector of the group) rather than one per lane
pair, to avoid a "starburst" of overlapping dashes at multi-lane junctions.

Degree-2 nodes (90° bends, or curves too sharp to heal into one edge — ch. 01/02) get a
special path: `AddCornerContinuation` (`JunctionMarkings.cs:218-266`) sweeps each
marking line from `MarkingRules.Layout` around the bend along its *own* corner
quadratic — the intersection of the two edges' offset lines at that marking's specific
lateral offset — rather than offsetting one shared curve, because on wide roads a
single shared curve would cusp on the turn's inside and gap open on the outside.
`SweepLine`'s `centerOnApex` option (`JunctionMarkings.cs:311-324`) exists because
dash-period math naturally wants to center a *gap* at the arc's midpoint, but a degree-2
bend concentrates essentially all its curvature there — a gap at the apex reads as a
straight chord hugging the inner curb — so corner continuations pin a dash onto the
apex instead (both gotchas attributed in `docs/gotchas.md`'s domain-geometry section).

## Traffic & overlay views

`TrafficView` (`src/Game/TrafficView.cs`) is a single `MultiMeshInstance3D` with a
1024-instance capacity box mesh; per frame it reads `_sim.Vehicles` and `_sim.Pose(v)`
(ch. 05 — `Pose` returns a position/forward pair, not the raw `S` front-bumper distance
the sim tracks internally, so the view is insulated from the "vehicle center trails the
front bumper by half a length" convention in `docs/gotchas.md`'s traffic-sim section)
and calls `SetInstanceTransform`/`SetInstanceColor` per vehicle (`TrafficView.cs:43-61`).
There's no per-vehicle Godot node — this is instancing — so `Capacity` is a hard
ceiling, not a dynamic pool: `VisibleInstanceCount` clamps to
`Math.Min(_sim.Vehicles.Count, Capacity)`, silently dropping overflow past 1024 vehicles
rather than erroring. Vehicle color is a deterministic `Palette[v.Id & 7]` — stable for
a vehicle's lifetime without storing anything, at the cost of only 8 distinguishable
colors.

`SignalLampView` (`src/Game/SignalLampView.cs`) renders three small spheres per
signal-controlled approach (red/amber/green) and separates *structure* from *state*:
`Rebuild()` (`SignalLampView.cs:52-83`) walks every node with 3+ edges in
`TrafficLights` mode and places lamp meshes using the same
`JunctionProps.ApproachAnchors`/`SignalLampCenters` helpers `JunctionProps` uses for the
physical light housing, so lamps sit exactly on the static pole without duplicating the
anchor math. `Rebuild` only runs when `_dirty` (same `network.Changed` pattern as
`RoadNetworkView`), while `UpdatePhases()` polls `_sim.PhaseFor(nodeId, leg)` on a fixed
0.25 s cadence independent of network changes — phase changes every tick, topology
rarely, so conflating the two flags would either rebuild geometry every quarter-second
for nothing or only poll phases on topology edits.

Debug overlays follow the same `Changed`-driven dirty pattern but render with
`ImmediateMesh` instead of `SurfaceTool`/`ArrayMesh`, redrawing wireframe primitives
each time rather than building reusable geometry. `LaneDebugOverlay`
(`src/Game/LaneDebugOverlay.cs`) draws every lane's curve color-coded by kind/direction
(green forward, orange backward, cyan connectors, purple bicycle, gray sidewalk) with
an arrowhead at 70%/30% of the curve so travel direction reads without a legend; it's
`Visible = false` until the toolbar toggles it, dirtying on both visibility toggle and
network change so it doesn't rebuild while hidden. `GridOverlay` (`src/Game/GridOverlay.cs`)
draws a fading grid around the cursor while grid snap is active; per-vertex alpha
(`0.35f * (1f - |i| / (HalfCells + 1))`, `GridOverlay.cs:46`) only fades visually
because `Materials.DebugLines` sets `Transparency = Alpha` and
`ShadingMode = Unshaded` (`Materials.cs:118-124`) — an opaque material would silently
render every line at full white regardless of the per-vertex alpha, the same class of
gotcha `docs/gotchas.md` documents for ghost/highlight materials elsewhere.

`RoadNetworkView.SetEdgeTint`/`ClearTints` (`RoadNetworkView.cs:133-160`) is the M6
addition backing the speed-heatmap harness scenario in `VisualShots.cs:583-594`: it
replaces an edge's `MaterialOverride` with a fresh `StandardMaterial3D` colored from a
red↔green ratio of mean vehicle speed to the road's speed limit. Per the comment at
`RoadNetworkView.cs:129-132`, the material is never shared across tinted edges because
`MaterialOverride` replaces every surface on that instance at once — a shared instance
would repaint every tinted edge's asphalt in lockstep.

## Godot pitfalls this codebase learned

`docs/gotchas.md` exists because most of these were found by shipping a visibly broken
frame, and this chapter's code is where nearly all of them live:

- **Godot's front faces are clockwise.** Every hand-built flat triangle
  (`AddTriangleUp` in `MeshBuilders.cs:468-478`, its analog in `JunctionProps.cs:213-223`,
  `AddTriangle` in `JunctionMarkings.cs:406-417`) computes a cross product against the
  intended normal and swaps two vertices on a backward winding. Skipping this renders
  fine in the editor viewport but goes black under the shadow pass — invisible unless
  you check a real screenshot.
- **Never fan-triangulate a junction polygon from the node position** — the node's own
  coordinate can sit outside its polygon at an acute "beak." `BuildJunctionMesh`/
  `BuildCornerZones` call `Geometry2D.TriangulatePolygon`, falling back to a centroid
  fan only if triangulation itself fails.
- **`SurfaceTool.GenerateNormals()` smooths across a whole surface unless you set
  smooth groups.** Builders mixing a flat top with a vertical wall (`BuildAsphalt`,
  `BuildSidewalks`, `BuildCornerZones`) assign one `SetSmoothGroup` per band so the top
  stays flat-shaded instead of a curved-looking seam.
- **Sub-pixel geometry vanishes without MSAA.** Markings are 0.15 m wide (`MarkingWidth`,
  `MeshBuilders.cs:27`); beyond ~65 m top-down camera distance they fall under a pixel
  without multisampling, hence `GetViewport().Msaa3D = Viewport.Msaa.Msaa4X`
  (`Main.cs:48`). Corollary: an empty-looking distant screenshot is not proof the
  geometry failed — zoom in or dump the mesh's vertex count first.
- **Junction outlines must use `RoadType.OuterHalf`, not `Width/2`** — `OuterHalf`
  accounts for sidewalks outside the carriageway; the narrower half-width produces a
  visible ~0.5 m step at the approach sidewalk.
- **Materials are embedded per surface, not assigned per view.** One `ArrayMesh`
  carries asphalt, paint, sidewalks, and props as independent surfaces — but
  `MaterialOverride` on the owning instance replaces all of them at once, which is why
  `SetEdgeTint` allocates one material per tinted edge instead of sharing.
- **Env vars leak from an editor-launched dev shell.** `CITYBUILDER_SHOTS` set in a
  shell that later launches the editor's play button used to hijack every play session
  into screenshot mode. `Main.cs:56-69` guards every harness env var behind `fromEditor`
  (`EngineDebugger.IsActive()` or a raw `/proc/self/cmdline` check for `--editor-pid`,
  since `OS.GetCmdlineArgs()` strips engine flags).

## Worked example

Trace one edit end to end: the player finishes dragging a new two-lane segment that
ends mid-edge on an existing road (a T-junction forms). `DraftSession` (ch. 06) calls
into `RoadNetwork`, running inside one `BeginBatch()`/`EndBatch()` pair (ch. 02): it
adds the new edge and node, splits the existing edge at the T-point, and rebuilds
junction geometry for the new three-way node — `JunctionBuilder` (ch. 03) computes the
new `SurfacePolygon`/`Corners`/`CutT` for all three legs, `ConnectorBuilder` (ch. 04)
computes the new `LaneConnector`s. `EndBatch()` fires one `Changed` event: `EdgesAdded`
(new segment plus split remainder), `EdgesRemoved` (the old unsplit edge id),
`NodesAdded` (the T-node), `NodesChanged` (the two existing junctions, if the split
shortened an adjacent leg's `CutT`).

`RoadNetworkView.OnChanged` receives this on the same frame: frees the removed edge's
mesh instance, dirties the added/removed ids, and — crucially — walks the added/changed
node's `node.Edges` to also dirty every edge touching it (`RoadNetworkView.cs:44-47`),
remeshing the two pre-existing legs even though their own `EdgeId` never appeared in
`EdgesAdded`/`EdgesRemoved`. `SignalLampView` and `LaneDebugOverlay`, subscribed
independently, set their own `_dirty` flags with no ordering dependency between them.

On the next `_Process`, `FlushDirty()` runs: for each dirty edge it recomputes
`tStart`/`tEnd`/ramp flags from the (possibly new) junction data and calls
`MeshBuilders.BuildEdgeMesh` for asphalt/markings/sidewalks/bike tint as one `ArrayMesh`.
For the new node, `RebuildNode` calls `BuildJunctionMesh` for the corner surface, then
`JunctionMarkings.Build` and `JunctionProps.Build` each commit an extra surface onto
that mesh — three independent queries landing on one `ArrayMesh` via `Commit(mesh)`
calls that append rather than replace. `LaneDebugOverlay`, if visible, redraws its
`ImmediateMesh` from scratch from the now-updated graph. All of this resolves within
one frame after the edit; there is no async or multi-frame staging.

## Invariants

The game layer never mutates domain state. Every method in this chapter reads
`RoadNetwork`/`TrafficSim` and writes only Godot nodes; searching these files for a
`RoadNetwork` mutator call (`AddEdge`, `RemoveEdge`, `RestoreInto`) turns up none —
edits flow the other direction, through `DraftSession`/`ToolController` (ch. 06). Domain
ids (`EdgeId`, `NodeId`, `LaneId`) are the only handle the game layer keeps across
frames; a view never caches a `RoadEdge`/`RoadNode` struct itself, since those are
recreated on rebuild — every cache here (`_edgeInstances`, `_nodeInstances`, `_heads`)
is keyed by id and re-looked-up from `_network.Edges`/`.Nodes` each time. Mesh instances
are identified 1:1 with domain edges/nodes for their lifetime; `GetOrCreate` never
recreates an existing `MeshInstance3D`, so Godot-side state hung off that instance (a
tint, a `Visible` flag) survives remeshes unless the code that set it explicitly clears
it.

## Tuning constants

- `MeshBuilders.SurfaceY = 0.07f` / `MarkingY = 0.10f` — vertical layering so asphalt,
  markings, and sidewalks/props never z-fight (markings 3 cm above asphalt, sidewalks
  another ~6 cm above via `SidewalkRise = 0.13f`; `BikeTintY = SurfaceY + 0.004f`).
- `MarkingWidth = 0.15f` — real-world lane-paint width; the exact figure the MSAA
  gotcha above is about.
- `ChordTolerance = 0.15f` — tessellation error bound for `BezierOps.Tessellate`; every
  curve-following mesh (asphalt, sidewalks, bike tint, ghost strips) samples at this
  resolution, trading vertex count for curve fidelity.
- `DashOn = DashOff = 3f` (lane-separator dashes) vs. `GuidanceDashOn = 0.8f` /
  `GuidanceDashOff = 1.0f` (junction guidance) — guidance dashes are shorter and
  tighter since they cover a much smaller arc length.
- `CurbRampLength = 1.4f` — sidewalk ramp distance at dead ends; disabled entirely on
  stub edges shorter than 3× this (`MeshBuilders.cs:113`) so a ramp on both ends
  doesn't dip a too-short strip with no flat middle.
- `MinLaneLengthForArrow = 16f`, `MinGuidanceLength = 10f` — small junctions/short legs
  suppress turn-arrow/guidance paint rather than cram it onto too little span.
- `TrafficView.Capacity = 1024` — hard MultiMesh ceiling; see Known limits.
- `SignalLampView._pollAccum` cadence of 0.25 s — phase reads as instantaneous without
  re-walking every controlled node every frame.

## Known limits

- `TrafficView`'s 1024-vehicle cap is silent: it simply stops rendering overflow with
  no warning or counter exposed. `[UNCERTAIN]` whether current milestone traffic
  densities can realistically hit this ceiling — check against the M6 KPI/fuzz targets
  in `docs/verification.md` before relying on headroom here.
- `SetEdgeTint`/`ClearTints` are only exercised by the `VisualShots` heatmap scenario
  today (`VisualShots.cs:583-594`); no in-game UI action calls them yet, so the
  "debug/analysis overlay" doc comment on `RoadNetworkView.cs:129` is aspirational for
  player-facing use until a tool wires it up.
- `AddCornerContinuation` bails on a type transition at a degree-2 corner
  (`a.Type != b.Type`, `JunctionMarkings.cs:226-227`) — markings just stop at such a
  bend rather than tapering, a gap the source comment itself calls out.
- Junction props (`JunctionProps.cs`) are explicitly "placeholder procedural meshes"
  per their own doc comment — flat n-gon primitives, not modeled assets.

## How to verify

Rendering changes here are verified visually, not by domain unit tests (there are none
— everything Godot-typed is out of `dotnet test`'s reach). `CITYBUILDER_SHOTS=tests/visual/shots
godot .` runs the scenario-driven screenshot harness (`VisualShots.cs`) and needs a real
window, not `--headless`; the produced PNGs must actually be read, not just checked for
existence, per the MSAA gotcha above. `CITYBUILDER_UITEST=/tmp/ui.png godot .` drives a
scripted UI flow and captures the full UI including toolbar/overlay state.
`CITYBUILDER_SMOKE=1 godot --headless .` exercises the resync path end-to-end
(edits → `Changed` → `FlushDirty`) without rendering, printing `SMOKE OK` — the fastest
check that a mesh-builder change didn't throw or leave dirty sets unflushed. Full
harness details and the perf/KPI gates live in `docs/verification.md`.
