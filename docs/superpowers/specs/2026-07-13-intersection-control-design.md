# Intersection Control & Customization — Design (Milestone 2)

**Date:** 2026-07-13
**Status:** Approved (user waived interactive review; scope set by user: "massively improve
intersections and give options for lane priority, yielding, traffic lights on/off, etc.
Also allow resizing intersections like in modded Cities Skylines" — prerequisite for the
traffic simulation milestone.)

## Goal

Every junction becomes configurable: who has right of way (priority road, yield, stop,
all-way stop, traffic lights), with matching paint and street furniture, and the junction
footprint becomes resizable per node and per leg (Node Controller style). Right-of-way is
annotated onto the lane graph so the future traffic sim consumes it directly.

## Non-goals

- No signal timing/phases (lights are static props + a `Signal` right-of-way tag until the
  traffic milestone).
- No movement-level conflict resolution (left turn yields to oncoming) — leg-level roles
  only; the sim milestone refines this.
- No persistence (no save system exists yet).
- No lane-connector editing UI (which lane connects to which) — separate milestone.

## Approaches considered

1. **Config on the node, resolved defaults (chosen).** `RoadNode` carries an optional
   `JunctionConfig` override; an evaluator resolves the *effective* control (mode + per-leg
   roles) from override + heuristics. Geometry/connectors/markings all read the effective
   control. Single source of truth, events flow through the existing `Changed` pipeline,
   and "Auto" stays meaningful when topology changes.
2. Separate config store keyed by NodeId outside `RoadNetwork`. Decouples domain from
   control data, but splits the source of truth, complicates events and future save games.
3. Bake control into `JunctionGeometry`. Wrong layer — geometry is derived, control is
   authored state.

## Domain model (src/Domain)

### Control types (`Network/JunctionControl.cs`)

```csharp
public enum JunctionControlMode { Auto, None, PrioritySigns, AllWayStop, TrafficLights }
public enum LegRole { Main, Yield, Stop }
public enum RightOfWay { Free, Yield, Stop, Signal }

public sealed record JunctionConfig(
    JunctionControlMode Mode,                       // Auto = use heuristic
    IReadOnlyDictionary<EdgeId, LegRole> RoleOverrides,  // priority mode only
    float SizeOffset,                               // metres added to every leg cut
    IReadOnlyDictionary<EdgeId, float> LegOffsets)  // per-leg extra metres
{
    public static readonly JunctionConfig Default; // Auto, empty, 0, empty
}

public sealed record EffectiveControl(
    JunctionControlMode Mode,                       // never Auto
    IReadOnlyDictionary<EdgeId, LegRole> Roles);    // one entry per leg (degree ≥ 3)
```

`RoadNode.Config` (get; internal set) defaults to `JunctionConfig.Default`.
`RoadNetwork.ConfigureJunction(NodeId, JunctionConfig)` validates the node exists, prunes
role/offset entries for edges no longer connected, stores the config, rebuilds junction
geometry + connectors for that node, bumps `Version`, raises `Changed` with the node in
`NodesChanged`.

### Effective control resolution (`JunctionControl.Resolve(node, edges)`)

- Degree ≤ 2 → `None` (bends/dead ends have no control).
- `Auto` at degree ≥ 3 → `PrioritySigns` with the **main pair** heuristic: score each
  unordered leg pair by (wider carriageway first, then straightest — most anti-parallel
  directions); winning pair's legs get `Main`, all others `Yield`. Odd main situations
  (all same width, near-symmetric Y) just pick the straightest pair — deterministic.
- `PrioritySigns` → heuristic roles, then apply `RoleOverrides` (unknown edges ignored).
  If the user marks every leg Yield/Stop that is allowed (behaves like all-way).
- `AllWayStop` / `TrafficLights` / `None` → roles map is Main for all legs (unused).

### Right-of-way on the lane graph

`LaneConnector` gains a field: `RightOfWay Row` (default `Free`). `ConnectorBuilder`
resolves the node's effective control and tags every connector whose **From lane enters
the node**: `None`→`Free`; `PrioritySigns`→ by its leg's role (`Main`→`Free`,
`Yield`→`Yield`, `Stop`→`Stop`); `AllWayStop`→`Stop`; `TrafficLights`→`Signal`.
Dead-end U-turns stay `Free`.

### Resizing (JunctionBuilder)

Per-leg extra distance `extra = clamp(SizeOffset + LegOffsets[edge], -CornerMargin, +12)`.
The wanted cut becomes `solvedCorner + CornerMargin + extra` — shrinking eats the margin
but never cuts below the solved corner requirement (geometry cannot self-intersect);
growing extends up to +12 m. The existing 30 %-of-edge-length clamp and `TightCuts`
flagging stay on top. Degree-2 elbows and dead ends honor resize too (their junction
surface simply grows).

Corner returns, corner zones, crosswalk placement, and marking continuations all derive
from cuts — they follow automatically.

## Rendering (src/Game)

### Paint (`JunctionMarkings`)

Driven by effective control per incoming leg (skipped on `TightCuts` legs, as today):

- `Main` (priority mode): **no stop line**. Crosswalk and guidance unchanged.
- `Yield`: row of shark teeth (isoceles triangles, base 0.6 m, height 0.7 m, gap 0.3 m,
  pointing at incoming traffic) across the incoming lanes at the cut, replacing the stop line.
- `Stop` / `AllWayStop`: solid stop line (existing).
- `TrafficLights`: solid stop line (existing).
- `None` (and degree-2): no stop lines — today's behavior for bends.

### Street furniture (`JunctionProps.cs`, new)

Simple procedural meshes (SurfaceTool boxes/cylinders, embedded materials), one prop per
incoming leg, placed on the driver's right at the cut, at lateral offset
`OuterHalf − 0.5` (sidewalk verge) or `CarriagewayHalf + 0.4` for sidewalk-less roads:

- Yield leg → triangle sign (white/red border) on a 2.4 m pole.
- Stop leg → red octagon sign on a 2.4 m pole.
- Main leg (priority mode) → yellow diamond sign on a 2.4 m pole.
- Traffic lights → 4 m dark pole with a 3-lamp head (red/amber/green discs) facing the
  incoming leg. One per leg, no mast arms (YAGNI until traffic).

`RoadNetworkView` builds props into the per-node junction mesh instance (same dirty
rebuild path).

## UI (src/Game)

New `ToolMode.Inspect` + toolbar button "Junction". Click selects the nearest node within
the snap radius (junction polygon highlighted via a line-strip overlay). A side panel
(`JunctionPanel.cs`, CanvasLayer) shows:

- Control mode dropdown: Auto / Priority signs / All-way stop / Traffic lights / None.
- Per-leg rows (labeled by road type + compass bearing): role cycle button
  Main → Yield → Stop (enabled only in Priority signs mode), and a per-leg size spinner
  (−/+ 0.5 m).
- Node size slider: −0.5 … +12 m, step 0.5.
- Reset button → `JunctionConfig.Default`.

Every change calls `ConfigureJunction`; the view rebuilds through the normal event flow.
Selecting a different node or leaving Inspect mode hides the panel.

## Testing

- **Domain (xUnit):** resolution defaults per degree; main-pair heuristic (wider wins,
  straight wins tie); role overrides applied and pruned when an edge is removed/split;
  connector `Row` tags per mode; resize: cut grows by offset, shrink floors at solved
  corner, 30 % clamp + TightCuts still apply; `ConfigureJunction` bumps Version and
  raises Changed; config survives unrelated commits elsewhere.
- **Visual shots:** `priority_tee` (teeth + main diamond + no stop line on main),
  `allway_cross` (4 stop lines + octagons), `lights_cross` (poles + heads, top/oblique/low),
  `resized_cross` (SizeOffset +6 — visibly larger junction, paint follows),
  `asym_resize` (one leg +6, others 0).
- **Smoke:** configure a junction (lights + resize) through the controller path headlessly;
  assert connectors report `Signal` and cuts moved.

## Risks / notes

- `RoleOverrides`/`LegOffsets` are keyed by `EdgeId`; splits/bulldozes prune silently and
  fall back to heuristics — acceptable, documented here.
- Prop meshes are placeholders by design; art pass later.
- Signal timing intentionally deferred; `Signal` tag is the contract the sim will consume.
