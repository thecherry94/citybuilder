# Road Network System — Milestone 1 Design

**Date:** 2026-07-12
**Project:** citybuilder (Godot 4.6 .NET, Forward+)
**Status:** Approved (user delegated review)

## 1. Goal

A Godot scene where the player flies a camera over a flat ground plane and builds
road networks with Cities: Skylines 2–quality drawing tools. This milestone delivers
the interactive road toolkit and the underlying network graph; it deliberately builds
the lane-level data model correctly from day one so traffic simulation can be added
later without a rewrite.

### In scope

- **Drawing modes:** Straight (2 clicks), Simple Curve (3 clicks), Complex Curve
  (4 clicks), Continuous (chained, tangent-continuous), Grid (3 clicks: corner,
  width direction, length).
- **Snapping & guidelines:** individually toggleable snap types — existing nodes,
  existing edges (split on connect), angle snap (multiples of 15° relative to the
  tangent of the road being extended, or world axes when starting fresh), and
  guidelines (tangent extensions projected from nearby segment endpoints, including
  snapping to guideline intersections). Live floating readout of segment length (m)
  and angle while drawing.
- **Automatic intersections:** drawing across existing roads splits them and creates
  shared nodes anywhere segments cross or touch; junction geometry (the intersection
  surface polygon) regenerates automatically from the connected edges.
- **Bulldoze with healing:** removing an edge deletes orphaned nodes; a surviving
  degree-2 node whose two edges have the same road type is merged back into a single
  edge when a fitted cubic reproduces both curves within tolerance (0.05 m max
  deviation); otherwise the degree-2 node is kept and renders seamlessly.
- **Road types:** TwoLane (1 driving lane per direction) and FourLane (2 per
  direction), defined as data so more types are content, not code.
- **Lane graph:** every edge carries first-class lane children; lane connectors are
  generated at every node (which incoming lane can reach which outgoing lane).
  A debug overlay renders every lane curve and connector.
- **Rendering:** procedural meshes — asphalt strip per edge with geometric lane
  markings, polygon intersection surfaces at nodes, dead-end caps; clean but simple
  materials.
- **UX shell:** RTS-style camera (WASD pan, wheel zoom, Q/E + middle-drag rotate),
  toolbar UI (mode buttons, road type selector, bulldoze, snap toggles, lane debug
  toggle), right-click steps back one click in the current placement, Esc cancels.

### Out of scope (must not be blocked by the design)

Elevation/bridges/tunnels/terrain (data model stores full 3D positions and
per-node elevation; all logic works on 3D vectors even though Y is always 0 for
now), Parallel mode, replace/upgrade tool, undo/redo, traffic simulation, zoning,
utilities, save/load (the domain model must be serializable-friendly — plain data,
no engine object references — but no save UI ships in this milestone).

### Success criteria

1. Draw a curved boulevard through an existing grid: correct intersections appear at
   every crossing, with regenerated junction surfaces and lane connectors.
2. Bulldoze the middle of it: the network heals (splits merge back where possible,
   junction geometry of touched nodes regenerates).
3. The lane graph is provably connected — verified by unit tests and visually by the
   debug overlay.
4. All domain unit tests pass headlessly via `dotnet test`; the Godot project builds
   and the scene loads without script errors.

## 2. Architecture

**Approach: pure C# domain core + thin Godot presentation layer** (approved over
Godot-scene-centric and ECS-first alternatives).

```
src/
  Domain/              # engine-independent class library (net8.0, System.Numerics)
    Geometry/          # Bezier3, polyline sampling, curve intersection, offsets
    Network/           # RoadNetwork, RoadNode, RoadEdge, Lane, LaneConnector
    Catalog/           # RoadType definitions (TwoLane, FourLane)
    Tools/             # placement proposals, validation, snapping, guidelines,
                       # per-mode click state machines (pure logic)
  Game/                # Godot layer (references Domain)
    RoadNetworkView    # listens to network change events, owns per-edge/per-node
                       # mesh instances
    MeshBuilders       # edge strip, markings, junction polygon, dead-end caps
    ToolController     # input → domain tool state machines → ghost preview
    CameraRig, UI, LaneDebugOverlay
tests/
  Domain.Tests/        # xUnit, runs with plain `dotnet test`
```

- The **Domain** assembly uses `System.Numerics` vectors (Y-up, matching Godot;
  1 unit = 1 m) and never references Godot. Conversion helpers live in the Game
  layer. This keeps the whole geometry/graph core headlessly testable and avoids
  GodotSharp versioning friction.
- The **domain is the single source of truth.** All mutations go through
  `RoadNetwork` methods which enforce invariants and raise granular change events
  (`EdgesAdded/Removed`, `NodesAdded/Removed/Changed`). The Godot view only reacts
  to events; it holds no authoritative state.
- Tool *logic* (what do N clicks mean in each mode, what is currently proposed,
  is it valid) is domain code; the Godot `ToolController` only translates input
  events and mouse-ray ground hits into calls on it. This makes every mode's
  click-state machine unit-testable.

## 3. Data model

### Geometry foundation

- `Bezier3` — immutable cubic Bézier (P0..P3, `Vector3`). Every edge is one cubic;
  a straight is a degenerate cubic with control points at 1/3 and 2/3. Operations:
  evaluate, first/second derivative, tangent, normal-in-plane (XZ), arc length +
  arc-length parameterization (Gauss–Legendre with cached LUT), closest point
  (coarse sampling + Newton refinement), curve–curve intersection (recursive
  subdivision on XZ-projected AABBs with flatness termination), split at t,
  bounding box, adaptive tessellation by curvature/chord error.
- Lane curves are **not** independent Béziers: a lane is the parent edge's curve
  plus a constant lateral offset, evaluated as `edge.Point(t) + edge.NormalXZ(t) *
  offset`. Exact, cheap, and always in sync with the edge.
- Curve construction from clicks: Simple Curve = quadratic (start, control, end)
  degree-elevated to cubic; Complex Curve = cubic directly from 4 clicks;
  Continuous = Simple Curve whose start tangent is forced to the previous
  segment's end tangent.

### Network entities (plain C#, id-referenced, serialization-friendly)

- `RoadNode` — `NodeId`, `Position` (Vector3), connected `EdgeId` set, generated
  `JunctionGeometry` (per-edge cut parameter + surface polygon), generated
  `LaneConnector` list.
- `RoadEdge` — `EdgeId`, `StartNodeId`, `EndNodeId`, `Bezier3 Curve` (oriented
  start→end), `RoadTypeId`, generated `Lane` list.
- `Lane` — `LaneId`, parent `EdgeId`, `Offset` (signed meters from the edge
  centerline, measured in the edge's start→end frame: positive = right when
  looking along the curve), `Direction` (Forward = travels start→end, Backward),
  `Width`, `Kind` (Driving for now). Right-hand traffic: forward lanes sit at
  positive offsets, backward lanes at negative offsets.
- `LaneConnector` — at a node: (incoming LaneId, outgoing LaneId, connector
  `Bezier3`). Generated for every incoming lane → every outgoing lane on a
  *different* edge (no U-turns). No turn restrictions yet; the structure is where
  they will live.
- `RoadType` — `Id`, `Name`, total `Width`, ordered lane blueprint (offset,
  direction, width, kind), design speed, marking layout description. TwoLane:
  8 m wide, lanes at ±1.75 m, 3.5 m each. FourLane: 16 m wide, lanes at ±1.75 m
  and ±5.25 m.

### RoadNetwork invariants (enforced by every mutation)

1. **Planarity at ground level:** no two edges cross without sharing a node.
   `AddEdge` computes intersections of the new curve against all existing edges
   (broad-phase via AABB grid), splits both the new curve and existing edges at
   every crossing, and inserts shared nodes.
2. **Endpoint snapping is explicit:** a proposal whose endpoint was snapped to an
   existing node connects to it; snapped to an existing edge splits that edge at
   the snap point first.
3. **No degenerate edges:** minimum segment length 4 m; proposals whose curve
   self-intersects, or that overlap an existing edge near-parallel within the road
   width, are invalid.
4. **Node lifecycle:** removing an edge removes nodes left with degree 0; a
   degree-2 node with same-type edges attempts the merge-back fit (§1 bulldoze).
5. **Derived data freshness:** any mutation regenerates lanes for touched edges,
   and junction geometry + lane connectors for every touched node.

### Junction geometry

For each node, sort connected edges by heading angle. For each adjacent pair of
edges, numerically intersect their facing border curves (centerline offset by
half-width) to find each edge's *cut parameter* — where the edge's own mesh stops.
An edge's final cut is the max required by its two neighbors (clamped so at least
30% of the edge remains drawable). The junction surface polygon is formed by the
corner points plus the cut cross-sections of every edge. Degree-1 nodes get a
semicircular cap; degree-2 nodes get cut = 0 (edges meet seamlessly) unless their
half-widths differ (type change), in which case a small transition surface is
generated.

## 4. Tools & interaction

### Placement pipeline (per mouse move, while a tool is active)

1. Godot `ToolController` raycasts the mouse against the Y=0 plane → world point.
2. `SnapService.Resolve(point, context, enabledSnapTypes)` returns the snapped
   point + snap metadata (node/edge/guideline/angle hit, for HUD display) using
   priority: node > guideline intersection > edge > guideline > angle > free.
   Snap radius scales with camera height (constant ~12 px screen-space intent).
3. The active mode's state machine produces a `PlacementProposal` (list of curves
   + endpoint bindings) from its collected clicks + the current hover point.
4. `RoadNetwork.Validate(proposal)` dry-runs it: computes splits/joins it would
   cause, checks invariants, returns `ValidatedPlacement` (valid/invalid + reasons
   + preview geometry including where intersections would appear).
5. The view renders ghost meshes (blue = valid, red = invalid) plus guidelines,
   snap indicators, and the length/angle readout.
6. Left click advances the mode's state machine; when a mode completes,
   `RoadNetwork.Commit(validatedPlacement)` applies it atomically. Right click
   steps the state machine back one click; Esc resets it.

### Mode specifics

- **Straight:** click A, click B → one edge.
- **Simple Curve:** clicks A (start), B (control), C (end).
- **Complex Curve:** clicks A, B, C, D → cubic A,B,C,D.
- **Continuous:** first segment like Simple Curve; every subsequent click adds a
  segment whose start tangent continues the previous one (control point implied
  along the tangent at 40% of chord length); right-click steps back one segment;
  Esc ends the chain.
- **Grid:** click corner A, click B (defines first axis direction and grid width
  along it), move/click C (defines perpendicular extent) → straight edges both
  directions at 48 m spacing (6 × 8 m zoning cells, a constant for now), clipped
  to the dragged rectangle, all sharing nodes at crossings. Partial cells at the
  far edges are dropped (only whole 48 m cells are stamped).
- **Bulldoze:** hover highlights the whole edge (red tint); click removes it and
  triggers healing. Bulldoze targets edges only (nodes disappear via lifecycle
  rules).

### Grid-mode note

Grid mode generates many edges in one commit; the proposal/commit API therefore
accepts multi-curve proposals from the start (Straight produces 1 curve, Grid
produces N).

## 5. Rendering (Godot layer)

- `RoadNetworkView` maintains `EdgeId → MeshInstance3D` and `NodeId →
  MeshInstance3D` maps; change events dirty entries, meshes regenerate at most
  once per frame (batched in `_Process`).
- **Edge mesh:** tessellate the curve (adaptive, ~0.15 m chord tolerance), sweep
  the road cross-section → asphalt strip with slight edge bevel. Lane markings are
  generated geometry floating 2 cm above the asphalt: solid white edge lines,
  dashed white separators between same-direction lanes (FourLane), dashed center
  line (TwoLane) / double solid center (FourLane). Marking strips stop at the
  junction cut.
- **Node mesh:** junction polygon triangulated (fan from centroid — the polygon is
  star-shaped by construction), same asphalt material, no markings inside for now.
- **Ghost meshes:** same builders with transparent blue/red materials.
- **Lane debug overlay:** toggleable `ImmediateMesh` lines — green forward lanes,
  orange backward, cyan connectors, arrowheads showing direction.
- **Ground:** 2048 × 2048 m plane, flat matte green-grey material with a subtle
  8 m grid shader so distances read.
- Materials are shared `StandardMaterial3D` resources; no textures required this
  milestone.

## 6. Error handling & edge cases

- Invalid proposals never throw at commit: `Commit` takes only a
  `ValidatedPlacement` produced by `Validate`; the controller cannot commit an
  invalid one (commit of a stale validation — network changed since — re-validates
  internally and rejects, returning a failure the UI shows briefly).
- Geometry robustness: all intersection/closest-point routines use epsilon 1e-4 m;
  near-tangential crossings that produce splits < 4 m from a node reuse that node
  instead of creating a sliver edge.
- Curves crossing multiple times (e.g. S-curve over a straight) produce one node
  per crossing.
- A proposal that starts and ends on the same existing edge splits it twice
  correctly (split parameters recomputed after the first split).
- Degenerate clicks (double-click same point, zero-length hover) keep the proposal
  invalid rather than crashing tessellation (guard: curve length < epsilon →
  invalid).
- The Godot layer never mutates domain state outside `Commit`; a caught exception
  in mesh building logs and skips that entry rather than crashing the scene.

## 7. Testing strategy

**Domain unit tests (xUnit, `dotnet test`, no Godot):**

- Bezier3: evaluation endpoints, arc length vs analytic straight line, closest
  point on known curves, split continuity, curve–curve intersection counts and
  positions (crossing, tangent, disjoint, S-curve double crossing), adaptive
  tessellation chord error bound.
- Network operations: add two crossing roads → 5 nodes / 4 edges; T-junction via
  edge snap → split + 3 edges; X through a grid of N streets → N nodes created;
  bulldoze middle edge → healing merges back to original curve within 0.05 m;
  bulldoze at a type-change node → no merge, node kept; orphan node cleanup.
- Lanes & connectors: lane offsets/directions per road type; connector
  completeness at 3-way and 4-way nodes (in-lanes × out-lanes minus U-turns);
  lane graph strong connectivity on a 3×3 grid network.
- Tools: each mode's state machine (click sequences → expected proposal curves,
  right-click stepping back); snapping priority and radius; angle snap values;
  guideline generation from nearby endpoints; validation rejections (too short,
  self-intersecting, overlapping parallel).
- Junction geometry: cut parameters positive and polygon non-self-intersecting for
  2/3/4/5-way nodes at various angles including acute (15°) approaches.

**Integration smoke (scripted):** `dotnet build` the solution;
`godot --headless --import` then `godot --headless --quit-after 2` running the
main scene must produce no script errors in output. Visual verification of
drawing feel is manual — flagged for the user's review at the end.

## 8. Milestone exit

When all tests pass and the smoke script is clean: commit, then stop and ask the
user to play with the tools in the editor before any further milestone (parallel
mode, elevation, replace tool, undo/redo, traffic) is planned.
