# Road-Building UX — Design (Milestone 4)

**Date:** 2026-07-14
**Status:** Approved (user-set scope: full draft/gesture model — Approach C; snapping
suite = grid + perpendicular/tangent + parallel guidelines; curve UX = tangent-locked
starts, arc mode, live radius readout, editable handles; four new hard validation
rules, no anarchy override. Undo/redo + upgrade tool deferred to M5.)

## Goal

Drawing roads should feel like modded Cities: Skylines. An in-progress road is a
first-class **draft** with visible, draggable handles instead of an irreversible click
sequence. Snapping grows grid, perpendicular, and parallel-guide support and picks the
*best* candidate rather than the first by fixed priority. Curves leave existing roads
tangentially (G1) by default, a constant-radius arc tool joins the mode list, and the
validator hard-blocks degenerate geometry: sliver segments, sharp junction angles,
too-tight curves, and kinked chains — the "bumps and stupid connections" class of bugs
can no longer be committed.

## Non-goals

- No undo/redo, no upgrade-in-place, no parallel-road *drawing mode* (M5 or later).
- No editing of already-committed geometry (drafts die at commit; node/edge moving is
  a future feature the draft model is designed to grow into).
- No anarchy override — validation rules are always hard blocks.
- No length snapping; no grid rotation or custom grid origin (world-aligned only).
- Parallel guidelines come off straight(ish) edges only — offsetting a curved bézier
  is out of scope.
- Save/load unchanged (still absent; unchanged constraint from the roadmap).

## Architecture: the draft/gesture model

`IPlacementTool` (click-state machines that commit on the final click) is **retired**.
Replacement, all in `src/Domain/Tools/` and fully unit-testable without Godot:

**`RoadDraft`** — an editable in-progress gesture. Holds an ordered list of
`DraftHandle(Position, Role, SnapResult)` (roles: `Endpoint`, `Control`, `Direction`)
and a shape strategy. Operations: `AddHandle`, `MoveHandle(index, snap)`,
`RemoveLastHandle`, `Preview(hover)`; `IsComplete` when the shape has all required
handles. `BuildProposal()` produces the same `PlacementProposal` the network already
validates/commits — that seam is unchanged.

**Shape strategies** (`IDraftShape`) map handles → curves:

- `StraightShape` — 2 endpoints.
- `QuadCurveShape` — start, control, end (3 handles; 2 when tangent-locked, control
  implied on the tangent ray at 40 % of chord, as ContinuousTool does today).
- `CubicCurveShape` — start, 2 controls, end (4 handles; 3 when tangent-locked).
- `ArcShape` — constant-radius circular arc: start, direction handle, end (3 handles;
  **2 when tangent-locked** — start tangent + end point determine a unique arc).
  Emitted as cubic bézier approximation (split in two above 90°, arcs clamped < 180°);
  exposes `Radius` for the readout.
- `ChainShape` — the continuous mode: each completed segment commits and the next
  inherits its end tangent (tangent lock does this for free now).
- `GridStampShape` — the 3-click grid stamp, unchanged behavior, but its corner/extent
  handles are now draggable before commit like everything else.

**Tangent lock:** when a draft's start handle binds to a node or edge, the draft
captures the local road tangent and passes it to the shape as a start-direction
constraint — every curve leaves existing roads G1-smooth. For nodes with several
edges, the guideline the cursor leaves along picks the reference edge; pressing the
lock toggle (game layer) releases it. `SnapResult` gains an optional
`DirectionConstraint` unit vector so *end* handles can carry arrival-direction
constraints too (perpendicular snap, below).

**`DraftSession`** — the domain-side tool state machine, replacing most of
ToolController's logic. Owns the current draft, mode, road type, snap settings; takes
`PointerMoved / Click / StepBack / Cancel / Confirm / BeginHandleDrag / DragTo /
EndHandleDrag`; exposes ghost state (proposal + validation + handles + active guides +
readout: length, angle, **min radius**). States: `Idle → Placing → Adjustable →
commit/cancel`. Commit policy: **instant commit stays the default** — a complete,
valid draft commits on the final click, preserving today's flow. A draft becomes
`Adjustable` (handles stay up, drag to fix, Enter/Confirm commits, Esc cancels) in two
cases: the completed proposal is *invalid* (fix instead of losing the gesture), or the
user enabled the adjust-mode toggle. The session validates via `RoadNetwork.Validate`
and commits via `Commit` itself; the game layer only forwards input and renders.

## Snap engine v2 (`src/Domain/Tools/Snapping/`)

`SnapService`'s fixed priority chain is replaced by `SnapEngine` with the same facade
(`Resolve(raw, radius, enabled, ctx) → SnapResult`) but two-stage candidate scoring:

1. **Position candidates** from small producers, each emitting
   `(position, kind, payload)`: node, edge, guideline, guideline-intersection,
   **grid point**, **grid line**, **perpendicular point** (see below). Winner by
   lowest `distance / weight(kind)`; weights order the kinds (node 3.0 > guideline
   intersection 2.5 > edge ≈ perpendicular 2.0 > guideline 1.5 > grid point 1.2 >
   grid line 1.0) while still letting a dead-on weak snap beat a barely-in-radius
   strong one — the "why did it snap THERE?" fix.
2. **Direction fallback**: if no position candidate wins and an anchor exists, angle
   snap projects onto 15°-step rays measured from `SnapContext.ReferenceTangent` —
   which the session now actually supplies (chain/lock tangent), fixing the
   measured-from-world-X bug at `ToolController.cs:277`.

**Grid snapping:** `GridConfig(cellSize)` (default 8 m, toolbar-selectable
4/8/16/32), world-aligned, origin at world zero. Candidates are the nearest grid
intersection and nearest grid line projection. New `SnapTypes.Grid` flag.

**Perpendicular snap** (`SnapTypes.Perpendicular`): with an anchor set, for each
nearby edge solve (sampled + refined) for the point where the chord from the anchor
meets the edge at 90°; emit it as a position candidate whose `SnapResult` carries a
`DirectionConstraint` (the arrival direction), which curve shapes honor as an end
tangent — curved roads also *arrive* perpendicular. Tangential arrival (0°) is not a
snap; leaving-along-the-road is already covered by guideline extensions.

**Parallel guidelines** (`SnapTypes.Parallel`): straight-ish nearby edges (chord
deviation < 0.5 m) additionally spawn guides offset laterally by
`edge.OuterHalf + newType.OuterHalf` on both sides — snapping to one yields a road
exactly curb-to-curb with the existing one. They are ordinary `Guideline`s: they
render dashed and participate in guideline-intersection snapping automatically.

## Geometry guards (validation)

New/changed in `RoadNetwork.Validate` + catalog; all hard blocks:

- **Per-type minimums on `RoadType`:** `MinSegmentLength = max(8 m, Width)` and
  `MinRadius` (TwoLane 20, FourLane 35, Street 10, Avenue 25 — tunable constants).
  `GeoConstants.MinEdgeLength` (4 m) survives only as a geometric floor inside curve
  math; validation and `SplitEdgeWithReuse` both use the affected edge's type value.
- **`TooShort`** now uses the proposal type's `MinSegmentLength`, and also fires when
  consecutive crossing points along the new curve (including endpoints, after
  node-reuse absorption) would produce a resulting segment below it — commit can no
  longer manufacture slivers that validation never saw.
- **`RadiusTooTight`**: `BezierOps.MinRadius(curve)` (sampled XZ curvature, 32
  samples) < type `MinRadius`. The ghost readout shows the live radius and turns
  red before you click.
- **`SharpAngle`**: at every proposal endpoint binding to an existing node or edge,
  and at every proposal-internal junction (chain joints, grid intersections), the
  angle between the new curve's tangent and any other edge tangent at that node must
  be ≥ `MinJunctionAngleDeg = 25°`. Kills protruding "bumps" (near-0° duplicates) and
  unbuildable Y-junctions; straight-through continuations (~180°) remain fine. This
  same rule applied to proposal-internal joints *is* the zigzag/kink guard
  (**`Kinked`** error name for that case).
- Invariant (regression-test it): **after any sequence of commits, no edge is shorter
  than its type's `MinSegmentLength` and no junction has a leg pair under 25° that
  wasn't already there.** Existing test fixtures that relied on 4 m segments or sharp
  fixtures get updated as part of this milestone.

## Game layer (`src/Game/`)

- **ToolController** shrinks to an adapter: raycast → `DraftSession` calls for road
  modes; bulldoze/inspect/spawn-vehicle stay as they are. Mouse-down near a visible
  handle starts a drag instead of adding a click; Enter confirms an `Adjustable`
  draft; right-click steps back; Esc cancels (unchanged).
- **GhostView**: renders draft handles (billboarded discs, hover/drag highlight) on
  top of the existing ghost ribbon + guides; invalid drafts keep today's red tint.
- **GridOverlay** (new node): faint grid lines around the cursor while `Grid` snap is
  active, fading with distance.
- **Toolbar**: Arc tool button; snap toggles for Grid (+ cell-size selector),
  Parallel, Perpendicular; adjust-mode toggle; readout extended with radius
  (`57.3 m   90°   R 24 m`).

## Testing

- **Snap engine**: per-producer tests + scoring invariants (node in radius beats
  grid; grid point on a guideline beats plain grid point; perpendicular candidate
  produces an exact 90° crossing; angle snap measures from the reference tangent).
- **Shapes**: arc sample-points equidistant from center (± ε); tangent-locked starts
  are G1 with the bound edge; `MoveHandle` regenerates the proposal consistently.
- **DraftSession**: state-machine flows — place→instant-commit; invalid→Adjustable→
  fix→confirm; step-back; cancel; chain continuation tangents.
- **Validation invariants** as above, plus: grid-stamp commits still satisfy the
  minimums with 48 m cells.
- **Visual/UI**: screenshot scenarios for grid overlay, parallel guides, handles, red
  sharp-angle ghost; `CITYBUILDER_UITEST` flow that drags a handle and commits.
- Full sweep per CLAUDE.md: `dotnet test`, `dotnet build`, smoke, screenshot harness.

## File plan

| Area | Change |
| --- | --- |
| `Domain/Tools/Draft/` | new: `DraftHandle`, `RoadDraft`, `IDraftShape` + 6 shapes, `DraftSession` |
| `Domain/Tools/Snapping/` | new: `SnapEngine`, candidate producers; `SnapService` deleted |
| `Domain/Tools/PlacementTools.cs` | deleted (`IPlacementTool` + 5 tools superseded) |
| `Domain/Geometry/BezierOps` | + `MinRadius`, `ArcFromTangent` |
| `Domain/Catalog/RoadType` | + `MinRadius`, `MinSegmentLength` |
| `Domain/Network/RoadNetwork` | validation rules; per-type split threshold |
| `Game/ToolController` | rework to `DraftSession` adapter |
| `Game/GhostView`, `Game/Toolbar` | handles + drag; new toggles, arc button, radius readout |
| `Game/GridOverlay.cs` | new |
