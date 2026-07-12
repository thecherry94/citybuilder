# Road Network Milestone 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** CS2-style road drawing toolkit (5 modes, snapping/guidelines, auto intersections, bulldoze+healing) over an engine-independent C# lane-graph network, rendered procedurally in Godot 4.6.

**Architecture:** Pure C# `Domain` classlib (System.Numerics, net8.0) is the source of truth: Bézier geometry, road graph with first-class lanes, snapping, tool state machines, validation/commit pipeline. Godot `Game` layer (root `citybuilder.csproj`, Godot.NET.Sdk) renders via change events and translates input. xUnit tests run headlessly against Domain.

**Tech Stack:** Godot 4.6.2 mono, .NET (Domain net8.0, tests net10.0), System.Numerics, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-12-road-network-design.md` — binding for all conventions.

## Global Constraints

- 1 unit = 1 meter, Y up, ground plane Y=0. Elevation-ready: all positions `Vector3`.
- Right-hand traffic. `right = normalize(cross(direction, up))`. Lane offset > 0 = right of centerline looking start→end.
- Min edge length 4 m. Geometry epsilon 1e-4 m. Healing merge tolerance 0.05 m max deviation.
- Splits landing < 4 m from an existing node reuse that node.
- Grid spacing 48 m. Snap radius = camera distance × 0.02, clamped [1, 20] m.
- Domain never references Godot. Game layer never mutates domain state except via `Validate`/`Commit`/`RemoveEdge`.
- Every task: tests first where the deliverable is domain logic; commit at end of task.

---

### Task 1: Solution scaffolding

**Files:**
- Create: `citybuilder.sln`, `citybuilder.csproj` (root, Godot.NET.Sdk/4.6.2, net8.0, `<Compile Remove="src/Domain/**;tests/**"/>`, ProjectReference to Domain)
- Create: `src/Domain/Domain.csproj` (net8.0, ImplicitUsings, Nullable enable)
- Create: `tests/Domain.Tests/Domain.Tests.csproj` (net10.0, xunit 2.9.x, Microsoft.NET.Test.Sdk, xunit.runner.visualstudio, ProjectReference Domain)
- Create: `src/Domain/Placeholder.cs` + one smoke test asserting `1+1==2` replaced later.
- Modify: `.gitignore` (add `.godot/`, `bin/`, `obj/`, `*.user`)

**Steps:**
- [ ] Write csprojs/sln; `dotnet build citybuilder.sln` → succeeds
- [ ] `dotnet test` → 1 passing
- [ ] `godot --headless --import .` (from project root) → exits clean
- [ ] Commit `chore: solution scaffolding (Domain, Game, Tests)`

### Task 2: Bezier3 core

**Files:** Create `src/Domain/Geometry/Bezier3.cs`, `tests/Domain.Tests/Geometry/Bezier3Tests.cs`

**Produces (exact API, immutable readonly struct):**
```csharp
public readonly struct Bezier3(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3) {
    public Vector3 P0, P1, P2, P3;                       // via ctor
    public static Bezier3 Line(Vector3 a, Vector3 b);     // ctrl at 1/3, 2/3
    public static Bezier3 FromQuadratic(Vector3 a, Vector3 ctrl, Vector3 b); // degree elevation
    public Vector3 Point(float t);
    public Vector3 Derivative(float t);
    public Vector3 Tangent(float t);                      // normalized, degenerate-safe (falls back to chord)
    public Vector3 NormalXZ(t);                           // normalize(cross(Tangent, Y-up)) projected to XZ → right side
    public Vector3 OffsetPoint(float t, float offset);    // Point + NormalXZ*offset
    public (Bezier3 a, Bezier3 b) Split(float t);         // de Casteljau
    public float Length();                                // adaptive subdivision, tol 1e-3
    public Bezier3 Reversed();
}
```

**Tests (write first, expect fail, then implement):** endpoints (`Point(0)==P0`, `Point(1)==P3`); `Line` length == analytic distance; tangent of `Line` == direction; `NormalXZ` of +X line == +Z (locks right-side convention); split halves rejoin (sample 10 points each, ≤1e-4); quarter-circle-ish curve length within 1% of numeric reference; `FromQuadratic` interpolates the quadratic at t=0.5.

- [ ] Failing tests → implement → pass → commit `feat(domain): cubic bezier core`

### Task 3: Bezier utilities

**Files:** Create `src/Domain/Geometry/BezierOps.cs`, `src/Domain/Geometry/ArcLengthTable.cs`, tests alongside.

**Produces:**
```csharp
public static class BezierOps {
    public static List<float> Tessellate(in Bezier3 c, float chordTolerance);       // returns t values incl. 0,1
    public static (float t, float dist) ClosestPoint(in Bezier3 c, Vector3 p);      // 64-sample coarse + 20 Newton iters
    public static List<(float t1, float t2)> Intersections(in Bezier3 a, in Bezier3 b); // XZ-plane crossings
    public static bool SelfIntersects(in Bezier3 c);
}
public sealed class ArcLengthTable(in Bezier3 c, int samples = 64) {
    public float TotalLength { get; }
    public float TAtDistance(float d);
    public float DistanceAtT(float t);
}
```
Intersections: recursive subdivision on XZ AABBs; when both sub-curves flat (max control-point deviation from chord < 1e-3) do segment-segment intersection; dedupe results closer than 1e-3 in t on both curves. SelfIntersects: subdivide into 32 spans, test non-adjacent span pairs.

**Tests:** two crossing lines → 1 intersection at expected t; S-curve over straight → 2; disjoint → 0; near-tangent touch → ≤1 (no dupes); closest point on line midpoint; arc table round-trip `TAtDistance(DistanceAtT(t))≈t`; tessellation chord error under tolerance (measure each chord midpoint vs curve).

- [ ] Failing tests → implement → pass → commit `feat(domain): bezier ops (tessellate/closest/intersect/arclength)`

### Task 4: Road catalog & ids

**Files:** Create `src/Domain/Catalog/RoadType.cs`, `src/Domain/Network/Ids.cs`, tests.

**Produces:**
```csharp
public readonly record struct NodeId(int Value); public readonly record struct EdgeId(int Value);
public readonly record struct LaneId(int Value);  public readonly record struct RoadTypeId(int Value);
public enum LaneDirection { Forward, Backward }   public enum LaneKind { Driving }
public sealed record LaneSpec(float Offset, LaneDirection Direction, float Width, LaneKind Kind);
public sealed record RoadType(RoadTypeId Id, string Name, float Width, IReadOnlyList<LaneSpec> Lanes, float DesignSpeed);
public static class RoadCatalog {
    public static readonly RoadType TwoLane;  // Width 8: lanes at +1.75 Fwd, -1.75 Back, 3.5 wide, 50 km/h
    public static readonly RoadType FourLane; // Width 16: +1.75,+5.25 Fwd; -1.75,-5.25 Back, 3.5 wide, 60 km/h
    public static RoadType Get(RoadTypeId id);
}
```
**Tests:** lane counts/offsets/directions per type; forward lanes all positive offsets.

- [ ] Failing tests → implement → pass → commit `feat(domain): road type catalog`

### Task 5: Network graph basics

**Files:** Create `src/Domain/Network/RoadNode.cs`, `RoadEdge.cs`, `Lane.cs`, `RoadNetwork.cs`, `NetworkDelta.cs`, tests.

**Produces:**
```csharp
public sealed class RoadNode { public NodeId Id; public Vector3 Position; public IReadOnlySet<EdgeId> Edges;
    public JunctionGeometry Junction; public IReadOnlyList<LaneConnector> Connectors; }
public sealed class RoadEdge { public EdgeId Id; public NodeId StartNode, EndNode; public Bezier3 Curve;
    public RoadTypeId Type; public IReadOnlyList<Lane> Lanes; public ArcLengthTable ArcLength; }
public sealed class Lane { public LaneId Id; public EdgeId Edge; public float Offset; public LaneDirection Direction;
    public float Width; public LaneKind Kind; }
public sealed record NetworkDelta(IReadOnlySet<EdgeId> EdgesAdded, IReadOnlySet<EdgeId> EdgesRemoved,
    IReadOnlySet<NodeId> NodesAdded, IReadOnlySet<NodeId> NodesRemoved, IReadOnlySet<NodeId> NodesChanged);
public sealed class RoadNetwork {
    public event Action<NetworkDelta>? Changed;
    public int Version { get; }
    public IReadOnlyDictionary<NodeId, RoadNode> Nodes; public IReadOnlyDictionary<EdgeId, RoadEdge> Edges;
    public NodeId? FindNodeNear(Vector3 p, float radius);
    public (EdgeId id, float t, float dist)? FindClosestEdge(Vector3 p, float maxDist);
    public ValidatedPlacement Validate(PlacementProposal proposal);
    public CommitResult Commit(ValidatedPlacement placement);
    public void RemoveEdge(EdgeId id);
}
```
Lanes generated from catalog on edge creation. This task: single-curve commits with **no crossings** (bindings: free endpoints create nodes; `ExistingNode` binding reuses). Delta events fire once per mutation batch.

**Tests:** add free edge → 2 nodes, 1 edge, lanes match type; add second edge bound to node A → 3 nodes total, node degree 2; RemoveEdge → orphaned degree-0 nodes removed; delta contents exact; Version increments.

- [ ] Failing tests → implement → pass → commit `feat(domain): road graph with lanes and change events`

### Task 6: Placement pipeline with crossing splits

**Files:** Create `src/Domain/Tools/PlacementProposal.cs` (`ProposedCurve(Bezier3 Curve, EndpointBinding Start, EndpointBinding End)`, `EndpointBinding` = None | Node(NodeId) | OnEdge(EdgeId, float t)), extend `RoadNetwork` (internal `SplitEdge`), tests.

**Behavior (spec §3 invariants, §6 edge cases):**
- `Validate`: per-curve length ≥ 4 m else invalid(TooShort); self-intersection invalid; near-parallel overlap invalid (sample 32 pts: if >50% of consecutive points lie within (widthA+widthB)/2 of an existing edge with |dot(tangents)|>0.95 → Overlapping); computes crossing list for preview. Multi-curve proposals validate per curve (grid).
- `Commit`: re-validate if `Version` stale, return failure if now invalid. Per curve, sequentially: resolve endpoint bindings (OnEdge → split that edge first; splits < 4 m from an end reuse that node); find crossings vs current network; split existing edges at crossings (per edge: sort ts descending, split repeatedly); split new curve at its crossing params; create nodes (reusing ≤4 m ones) and edges; rebuild junction+connectors for all touched nodes (Task 8/9 stubs no-op for now); one aggregated delta event.

**Tests:** two crossing straights → 4 edges, 5 nodes, center node degree 4; T-junction via `OnEdge` binding → 3 edges, 4 nodes; S-curve crossing straight twice → straight split twice (3+3=6 edges... assert exact: S-curve→3 pieces, straight→3 pieces, 2 shared nodes); proposal starting and ending on same edge → both splits correct; crossing < 4 m from an existing node reuses it (no sliver); too-short and self-intersecting proposals invalid; stale-version commit rejected after interleaved mutation.

- [ ] Failing tests → implement → pass → commit `feat(domain): auto-intersection placement pipeline`

### Task 7: Bulldoze healing

**Files:** Create `src/Domain/Network/CurveFit.cs`, extend RemoveEdge, tests.

**Produces:** `public static class CurveFit { public static (Bezier3 curve, float maxError) FitComposite(RoadEdge a, RoadEdge b, NodeId sharedNode); }` — orient both away from shared node → composite polyline (64 samples by arc length, from a's far end to b's far end); least-squares cubic fit with fixed endpoints, chord-length parameterization (Graphics Gems); maxError = max sample distance to fit (via ClosestPoint).

RemoveEdge flow: delete edge + lanes; drop degree-0 nodes; for each remaining endpoint node with degree 2 and both edges same RoadType: fit; if maxError ≤ 0.05 replace both edges + node with merged edge (preserve outer bindings); rebuild junctions/connectors on touched nodes; single delta.

**Tests:** split a known cubic by crossing then bulldoze the crossing road → original curve recovered (sample 20 pts ≤ 0.05 m) and node count back to original; degree-2 node with *different* types → not merged; bulldozing at 4-way leaves 3-way with regenerated junction.

- [ ] Failing tests → implement → pass → commit `feat(domain): bulldoze with network healing`

### Task 8: Junction geometry

**Files:** Create `src/Domain/Network/JunctionGeometry.cs` + builder, tests.

**Produces:**
```csharp
public sealed record JunctionGeometry(IReadOnlyDictionary<EdgeId, float> CutT, IReadOnlyList<Vector3> SurfacePolygon);
public static class JunctionBuilder { public static JunctionGeometry Build(RoadNode node, Func<EdgeId, RoadEdge> edges, Func<RoadTypeId, RoadType> types); }
```
Algorithm (straight-line approximation, spec §3): per edge compute leaving direction `d` (Tangent(0) if starts here else -Tangent(1)), halfwidth `w/2`; sort CCW by atan2(d.Z, d.X). Degree 1 → cuts 0, empty polygon. Degree 2 same width → cuts 0. Else for each adjacent pair: border lines `p = nodePos + d_i s + r_i σ_i (w_i/2)` with σ_i toward the neighbor (`sign(dot(r_i, d_j))`), 2×2 solve; near-parallel (|det|<1e-4) fallback `s = (w_i+w_j)/2 / max(0.2, |sin θ|)`. Cut distance per edge = max over its two corners + 0.5 m margin, clamped to ≤30% edge length; convert to t via `ArcLengthTable` (from correct end: if edge *ends* at node, cutT measured from t=1 backwards — store cut as t in edge param where junction begins). Polygon: walk edges CCW: [corner(i-1,i), cut cross-section right→left of edge i] … ; must be simple (non-self-intersecting).

**Tests:** symmetric 4-way of two-lane roads → all cuts equal, polygon 12 vertices (4 corners + 4×2 cross-section pts... assert count = 2×degree + degree corners = 12), polygon simple & contains node; 3-way T → cuts positive; acute 15° Y-junction → cuts larger than 90° case, still ≤30% clamp; two-lane meets four-lane at degree-2 → transition polygon generated (cuts > 0).

- [ ] Failing tests → implement → pass → wire into commit/remove rebuild → commit `feat(domain): junction geometry generation`

### Task 9: Lane connectors

**Files:** Create `src/Domain/Network/LaneConnector.cs` + builder, `src/Domain/Network/LaneGraph.cs` (test helper: adjacency walk), tests.

**Produces:**
```csharp
public sealed record LaneConnector(LaneId From, LaneId To, Bezier3 Curve);
public static class ConnectorBuilder { public static IReadOnlyList<LaneConnector> Build(RoadNode node, ...); }
```
Incoming lanes at node = Forward lanes of edges ending here + Backward lanes of edges starting here; outgoing symmetric. Connector for every in×out pair on different edges. Endpoints at lane offset points at the junction cut t; control points along travel tangents at ⅓ endpoint distance.

**Tests:** 4-way of two-lane roads: each of 4 incoming lanes → 3 connectors (36 total... assert per-incoming = 3, total = 12); no U-turn connectors; connector endpoints coincide with lane cut points ≤1e-3; 3×3 grid network → lane graph strongly connected (BFS over lanes+connectors reaches all lanes from any lane).

- [ ] Failing tests → implement → pass → commit `feat(domain): lane connectors + connectivity`

### Task 10: Snapping & guidelines

**Files:** Create `src/Domain/Tools/SnapService.cs`, `SnapResult.cs`, `Guideline.cs`, tests.

**Produces:**
```csharp
public enum SnapKind { Free, Node, Edge, GuidelineIntersection, Guideline, Angle }
public sealed record SnapResult(Vector3 Position, SnapKind Kind, NodeId? Node, (EdgeId, float)? Edge,
    float? SnappedAngleDeg, IReadOnlyList<Guideline> ActiveGuidelines);
public sealed record Guideline(Vector3 Origin, Vector3 Direction, float Length);
[Flags] public enum SnapTypes { None=0, Nodes=1, Edges=2, Angle=4, Guidelines=8, All=15 }
public sealed class SnapService(RoadNetwork network) {
    public SnapResult Resolve(Vector3 raw, float radius, SnapTypes enabled, SnapContext ctx);
}
public sealed record SnapContext(Vector3? Anchor, Vector3? ReferenceTangent); // anchor = previous click / start node
```
Priority: Node > GuidelineIntersection > Edge > Guideline > Angle > Free. Guidelines: for nodes within 200 m: one per connected edge, along the edge tangent at that node extended 200 m beyond the node (dead-end extensions and cross-axis references). Angle snap: with anchor: snap direction (raw−anchor) to nearest multiple of 15° measured from ReferenceTangent (or world +X if null), preserving |raw−anchor|.

**Tests:** point near node snaps to node even when edge closer; guideline intersection beats plain edge snap; angle snap 43°→45° from +X reference; disabled flags skip their kinds; radius respected.

- [ ] Failing tests → implement → pass → commit `feat(domain): snapping service with guidelines`

### Task 11: Tool state machines

**Files:** Create `src/Domain/Tools/PlacementTools.cs` (`StraightTool`, `SimpleCurveTool`, `ComplexCurveTool`, `ContinuousTool`, `GridTool`), `IPlacementTool.cs`, tests.

**Produces:**
```csharp
public interface IPlacementTool {
    RoadTypeId RoadType { get; set; }
    int ClickCount { get; }
    PlacementProposal? AddClick(SnapResult click);   // non-null → commit now
    PlacementProposal? Preview(SnapResult hover);    // ghost, may be invalid-shaped
    void StepBack(); void Reset();
    (float lengthM, float angleDeg)? Readout(SnapResult hover);
}
```
Click semantics per spec §4 (clicked mid points are control points; Continuous: after initial 3-click segment each click commits one tangent-chained segment, implied control at 40% chord along stored end tangent; Grid: A,B define axis1 (snapped B), C perpendicular extent, `floor(dist/48)` whole cells, perimeter+internal lines both axes as full-length straight curves, endpoint bindings None). Endpoint bindings derived from click SnapResults (Node/Edge hits).

**Tests (per tool):** click sequences produce expected curve control points; StepBack removes last click; Straight 2-click proposal binds endpoints from snaps; Continuous second segment starts with G1 tangent; Grid 96×96 drag → 3+3 lines (indices 0,48,96), commit on network yields 9 nodes of degree ≥2 and 12 edges (assert via commit); Readout length/angle correct for straight.

- [ ] Failing tests → implement → pass → commit `feat(domain): placement tool state machines`

### Task 12: Godot shell — scene, camera, ground

**Files:** Create `scenes/Main.tscn` (root Node3D `Main` + `CameraRig`, `Ground`, `RoadNetworkView`, `GhostView`, `LaneDebugOverlay`, `Ui` CanvasLayer), `src/Game/Main.cs`, `src/Game/CameraRig.cs`, `src/Game/GroundPlane.cs`, `src/Game/VecExt.cs` (System.Numerics↔Godot converters). Modify `project.godot` main scene → `res://scenes/Main.tscn`; delete `game.tscn`.

CameraRig: yaw Node3D → pitch Node3D (−50°) → Camera3D at `Distance` (default 80, wheel ±10%, clamp 10–400); WASD pans on XZ relative to yaw, speed ∝ Distance; Q/E yaw ±; middle-drag yaw/pitch. Ground: 2048² PlaneMesh, StandardMaterial with subtle shader-less grid via `albedo` + `uv1_scale`? → use a simple `ShaderMaterial` drawing 8 m grid lines (worldspace XZ mod). `Main.cs` builds `RoadNetwork`, `SnapService`, holds current tool; exposes `MouseGroundPoint()` (analytic ray-plane).

- [ ] Implement; `dotnet build` clean; `godot --headless --import . && timeout 25 godot --headless --quit-after 3 .` → no ERROR/exception lines; commit `feat(game): main scene, camera rig, ground`

### Task 13: Road rendering

**Files:** Create `src/Game/RoadNetworkView.cs`, `src/Game/MeshBuilders.cs`, `src/Game/Materials.cs`.

- `Materials`: shared StandardMaterial3D: Asphalt (#2e2e33, rough 0.9), Marking (white, emissive slight), GhostValid (blue, alpha 0.45), GhostInvalid (red, alpha 0.45), JunctionAsphalt (same as asphalt), DebugLane colors.
- `MeshBuilders.BuildEdgeMesh(RoadEdge, RoadType, cutStartT, cutEndT) → ArrayMesh`: tessellate curve between cuts (chord tol 0.15); asphalt strip cross-section width = type.Width, tiny bevel (edge verts dropped 0.05 down at ±w/2 ±0.3 skirt); markings as second surface at +0.02 Y: TwoLane → dashed center (3 on/3 off via ArcLengthTable, 0.15 wide) + solid side lines at ±(w/2−0.4); FourLane → double solid center (±0.15), dashed separators at ±3.5, solid sides at ±(w/2−0.4). Marking dashes clip to [cutStart, cutEnd].
- `BuildJunctionMesh(RoadNode) → ArrayMesh`: fan-triangulate SurfacePolygon from node position, asphalt material. Degree-1: semicircle cap of edge width.
- `RoadNetworkView`: subscribes `Changed`; dirty sets; `_Process` regenerates dirty `MeshInstance3D`s (per edge & per node children, name = id).

- [ ] Implement; headless smoke (script `tools/smoke.gd` NOT needed — instead `Main.cs` reads env var `CITYBUILDER_SMOKE=1` → programmatically commit a crossing pair + assert view child counts, print `SMOKE OK`, quit). Run: `CITYBUILDER_SMOKE=1 godot --headless .` expect `SMOKE OK`; commit `feat(game): procedural road & junction rendering`

### Task 14: Interaction — tools, ghost, UI, bulldoze, debug overlay

**Files:** Create `src/Game/ToolController.cs`, `src/Game/GhostView.cs`, `src/Game/LaneDebugOverlay.cs`, `src/Game/Ui/Toolbar.cs` (built in code, no .tscn editing needed beyond a CanvasLayer).

- `ToolController` (child of Main): current mode enum (Straight/Simple/Complex/Continuous/Grid/Bulldoze); LMB → `AddClick` (commit result → flash error if failed), RMB → `StepBack` (empty → nothing), Esc → `Reset`; mouse move → `SnapService.Resolve` (radius from camera distance) → `Preview` → GhostView. Bulldoze mode: hover `FindClosestEdge(p, 6)` → highlight via `MaterialOverride`; click → `RemoveEdge`.
- `GhostView`: renders preview proposal with MeshBuilders + ghost materials (network `Validate` decides blue/red); draws ActiveGuidelines (ImmediateMesh dashed), snap indicator (small sphere), readout Label (screen-space, follows mouse: `length 123.4 m ∠ 45°`).
- `LaneDebugOverlay`: toggle; ImmediateMesh lines: forward lanes green, backward orange, connectors cyan, arrowheads at 70% length; rebuilt on Changed when visible.
- `Toolbar` (top-left PanelContainer): mode ToggleButtons, road-type OptionButton (Two Lane/Four Lane), snap CheckBoxes (Nodes/Edges/Angle/Guidelines, all on), Lanes CheckBox. Wire to controller.

- [ ] Implement; extend SMOKE to simulate tool clicks programmatically (call controller methods directly: straight road commit + bulldoze) asserting network counts; run smoke; commit `feat(game): drawing tools UI, ghost preview, bulldoze, lane overlay`

### Task 15: Final verification

- [ ] `dotnet test` all green; `dotnet build` clean
- [ ] `godot --headless --import .` + smoke run clean
- [ ] Run superpowers:verification-before-completion checklist
- [ ] Re-check spec success criteria §1: curved boulevard through grid (cover via domain test if missing: complex curve through 3×3 grid commits with intersections at each crossing)
- [ ] Update `MEMORY.md` project memory; final commit; stop and ask user to review in editor

## Self-Review (done at write time)

- **Spec coverage:** modes (T11), snapping/guidelines (T10), auto intersections (T6), healing (T7), road types (T4), lane graph+overlay (T9/T14), junction geometry (T8), rendering (T13), UX shell (T12/T14), testing strategy (T2–T11 + smoke T13–T15). Out-of-scope items untouched. ✓
- **Type consistency:** `ValidatedPlacement`/`CommitResult` produced T5/T6, consumed T11/T14; `SnapResult` produced T10 consumed T11; `JunctionGeometry.CutT` consumed T13. ✓
- **Placeholders:** none; algorithms specified inline where non-obvious. ✓
