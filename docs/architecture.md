# Architecture

Two strictly separated layers:

```
src/Domain   pure C# (net8.0, System.Numerics) — the entire game state & simulation
src/Game     Godot 4.6 mono — rendering, input, UI; reacts to domain events
tests/       xUnit (net10.0) — tests the domain headlessly
```

`RoadNetwork.Changed` fires `NetworkDelta`s; `RoadNetworkView` marks dirty and rebuilds
at most once per frame. The Godot layer holds **no authoritative state**.

## Domain modules

### Geometry (`src/Domain/Geometry`)
- `Bezier3` — every road edge is one cubic bézier (immutable). `OffsetPoint(t, offset)`
  gives lateral lane positions via `NormalXZ`.
- `BezierOps` — tessellation, closest point, curve-curve intersections (XZ, AABB
  subdivision), self-intersection.
- `ArcLengthTable` — distance ↔ parameter mapping (128 samples).

### Network (`src/Domain/Network`)
- `RoadNetwork` — source of truth. `Validate`/`Commit` placement pipeline (crossings
  auto-split edges, node reuse, stale-version revalidation), `RemoveEdge` with
  tangent-preserving heal (Schneider fit), `ConfigureJunction`. All mutations bump
  `Version` and raise `Changed`.
- `JunctionBuilder` — per-node geometry: cut positions per leg, carriageway polygon
  (dual outlines: `OuterHalf` full width drives cuts, `CarriagewayHalf` is the asphalt),
  corner returns (quadratics through offset-line intersections; degree-2 elbows use them
  on **both** sides so bends stay parallel-width), `NodeArc` for reflex wedges,
  raised sidewalk `CornerZone`s, `TightCuts` for clamped short edges, resize via
  `JunctionConfig.SizeOffset`/`LegOffsets`.
- `ConnectorBuilder` — lane-level links across nodes with **turn-lane assignment**
  (lefts/u-turns from the leftmost lane, rights from the rightmost; straights are capacity-limited to the receiving arm and aligned 1:1, surplus inner lanes becoming dedicated lefts;
  degree-2 bends and dead ends unrestricted), `TurnKind` classification (±30°/±150°),
  `RightOfWay` tags from junction control, and `ConnectorConflicts` (curve-crossing or
  same-target-lane pairs — the traffic arbiter's input).
- `JunctionControl` — authored `JunctionConfig` on each node; `Resolve` yields effective
  mode + per-leg roles. Auto = priority signs with the widest-corridor (OuterHalf), then
  straightest, pair as main road. Degree ≤ 2 is always `None`.
- `LaneGraph` — adjacency + strong-connectivity checks (per lane kind).

### Catalog (`src/Domain/Catalog`)
Road types are pure data: `LaneSpec(Offset, Direction, Width, Kind)` lists per type;
`CarriagewayHalf`, `OuterHalf`, `HasSidewalks`, `SpeedLimit` are derived. Types:
TwoLane (8 m), FourLane (16 m), Street (12 m, sidewalks), Avenue (21 m, bikes+sidewalks).

### Tools (`src/Domain/Tools`)
- `DraftSession` — the tool state machine (`Idle → Placing → Adjustable`): owns the
  current `RoadDraft`, resolves snapping in context (anchor, chain/lock tangent, grid),
  validates/commits via `RoadNetwork`, exposes ghost + readout state. The game layer
  only forwards input.
- `RoadDraft` + `IDraftShape` strategies (`Straight`, `QuadCurve`, `CubicCurve`, `Arc`,
  `Chain` via quad, `GridStamp`) — editable handle lists mapped to proposal curves;
  G1 start-tangent lock when a draft starts on a node/edge (releasable, `T` in-game).
- `SnapEngine` (`Tools/Snapping`) — candidate-scored snapping: node / edge / guideline /
  guide-intersection / grid point+line / perpendicular producers, score =
  distance ÷ kind weight, angle-snap fallback from the reference tangent; parallel
  guides off straight-ish edges.

### Traffic (`src/Domain/Traffic`)
Two-layer design:
- **Strategic**: `RoutePlanner` — A* over `(edge, direction)` states; movement costs =
  travel time + turn penalties (L 4 s / R 1.5 s / U 8 s) + control delay (Yield 2 / Stop
  4 / Signal 5 s). Replanning = plan again from the current edge; vehicles never leave
  the lane graph.
- **Tactical (per tick)**: `Idm` car-following on per-lane queues (leader-only, O(1));
  `LaneChange` MOBIL-lite (discretionary gain > 0.3 m/s² with follower-safety, mandatory
  merges toward turn-serving lanes within 80 m with urgency, changes occupy **both**
  lanes for 2 s); `JunctionArbiter` (conflict-*point* arbitration with passed-point
  clearance, movement ranks + right-hand rule + deadlock breaker, impatience gap
  acceptance 2.8→2.2 s, stop-line compliance, all-way FIFO, signal phases — see
  conventions.md for the constants); `SignalController`
  (two direction-clustered phases, 12 s green / 3 s amber / 1 s all-red);
  `TrafficSpawner` (seeded ambient population, fringe origins, stuck replans after 20 s).
- `TrafficSim.Tick(dt)` is fixed-step and deterministic under a seed. A hard
  non-penetration clamp backs all models. `Pose()` renders the vehicle centre, keeping
  it on the *previous* segment until it clears the boundary (see gotchas).

## Game modules (`src/Game`)

`Main` (scene assembly in code, smoke/UITEST modes, fixed-step traffic driver) ·
`RoadNetworkView` (dirty rebuild; edge meshes, junction mesh + paint + props) ·
`MeshBuilders` (lane-profile-driven edge meshes, junction surfaces, `MarkingLayout`) ·
`JunctionMarkings` (stop lines/teeth by role, turn arrows from actual movements,
crosswalks, guidance, degree-2 corner continuations) · `JunctionProps` (signs, light
poles) · `SignalLampView` (animated lamps) · `TrafficView` (MultiMesh cars) ·
`ToolController`/`Toolbar`/`JunctionPanel`/`GhostView`/`GridOverlay` UI ·
`VisualShots` (screenshot + motion-filmstrip harness) · `CameraRig` · `Materials`.
