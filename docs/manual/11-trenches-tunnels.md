# Chapter 11 — Trenches & tunnels

**Last verified against commit:** M8.5 head, 2026-07-20.

M8.5 is the negative half of the vertical axis M8 opened. The domain was already
signed — the three-band crossing classifier, gradient caps, heal/retype guards, and
roundabout rules all operate on ΔY and |dY/ds| — so *no vertical rule changed*. What
M8.5 added is exactly one bit of stored state, an editor unlock, and rendering:

- **`RoadEdge.Covered`** (`Entities.cs`): the player's choice of open cut vs tunnel.
  Explicit, not depth-derived — a deliberate design decision (the depth-derived
  alternative was rejected during design review). Save format **v3** persists it
  (`EdgeDto.Covered`, default `false`, so v1/v2 payloads load unchanged).
- **Editor unlock** (`DraftSession.CurrentElevation`): clamp widened from
  `[0, MaxElevation]` to `[−MaxDepth, +MaxElevation]` (both 50 m). PgUp/PgDn
  unchanged; the readout and ghost badges gained the `⬇ N m` direction.
- **Derived structures** (`StructureView.BuildStructures`, now
  `(curve, arc, width, covered, pillarObstructed)`): below-ground spans mirror the
  above-ground bands.

## The below-ground bands

Per sampled span (4 m steps, same sampler as M8), with deck Y negative:

| Condition | Renders |
|---|---|
| `|Y| ≤ 0.05` | nothing (ground) |
| below ground, **uncovered** — or covered but shallower than `PortalDepth` (3 m) | **open cut**: retaining walls ground-lip→deck both sides, a 0.6 m coping strip, and the **cut-opening strip** (see below) |
| below ground, **covered**, deck ≤ −`PortalDepth` | **tunnel**: nothing at the surface except **portal faces** where the deck crosses `PortalDepth` |

**Portals appear only at internal depth crossings, never at curve ends.** This is what
makes chains of covered edges (produced by splits) work: a split mid-tunnel does not
sprout portal faces at the new node — the portal lives where the deck passes −3 m,
which is on the ramp, not at the seam. The corollary: a covered deep edge that simply
*ends* (dead end below ground) shows no portal; that is accepted, not accidental.

**Open cuts punch real holes in the ground via a mask.** The world's ground is a
single flat plane, and a translucent strip above it can never reveal what the depth
test already discarded below — the first strip-only pass rendered trenches as flat
featureless decals with the sunken road invisible. The working mechanism: every
open-cut span emits its opening strip (carriageway + coping footprint) twice —
once translucent in the visual mesh (`Materials.CutOpening`, α ≈ 0.30, now just a
depth-shade hint), and once white on `StructureView.CutMaskLayer` (layer 11), which
only a top-down orthographic camera in `Main.BuildGround`'s `CutMaskViewport`
(2048², 1 texel/m, shared world, black background) renders. Both ground shader
variants sample that mask by world XZ and `discard` where it is set, so the hole is
real: retaining walls, coping, and the sunken carriageway genuinely render. The main
camera excludes the mask layer. Covered spans deeper than `PortalDepth` emit no cut
strip, so tunnel surfaces stay intact. A terrain system with real holes replaces
this wholesale — it stays isolated as the `cut` surface in `BuildStructures`.

## Covered-flag semantics and propagation

`Covered` has **no validation surface**: `RoadNetwork.SetCovered(id, covered)` never
returns an error for geometric reasons — the flag is inert on spans at or above
ground (no glass tunnels in the sky, by derivation rather than by rule). It returns
`false` only for an unknown edge, a roundabout **ring** edge (ring regeneration owns
those), or a no-op toggle. On success it bumps `Version` and raises
`NetworkDelta.EdgesChanged` — the renderer path is identical to retype/flip.

Propagation invariants (all pinned in `CoveredFlagTests`):

- **Split children inherit** the parent's flag (`SplitEdgeWithReuse`).
- **Heal keeps the flag iff both merged edges agree**, else the healed edge is
  uncovered (`TryHealNode`) — conservative and visible; never a surprise tunnel.
- **Retype, flip, and roundabout leg re-trims preserve** it (`ReplaceEdgeInPlace`,
  the Roundabouts leg replacement).
- **Undo/redo restores it** for free: the undo stack snapshots via `SaveLoad`, and
  the flag is in the v3 format.

## Editor surface

- **Upgrade tool**: the toolbar "Covered (tunnel)" checkbox flips
  `ToolController.CoveredToggleActive`; while active, Upgrade-mode LMB calls
  `SetCovered(hovered, !covered)` instead of retyping (RMB flip unchanged). Status
  flashes "covered (tunnel)" / "open cut".
- **X-ray view**: `U` toggles manually; drafting with a below-ground elevation
  auto-engages it (`ToolController.DraftBelowGround`, polled by `Main.PollXRay`) and
  restores the manual state after. X-ray = translucent ground material + 0.55
  transparency on above-ground edges/nodes/structures
  (`RoadNetworkView.SetXRay`, `StructureView.SetXRay`); below-ground geometry was
  always meshed — the opaque ground merely hid it.
- **XZ picking** (`FindClosestEdgeXZ` / `FindNodeNearXZ`, `RoadNetwork.cs`): the
  covered-toggle UITEST exposed that all tool picking measured 3D distance from the
  ground-plane cursor, so any deck with |Y| > pick radius — including M8 bridges —
  was unhoverable by upgrade/bulldoze/inspect. Picking is now plan-view with a
  tie-break toward the deck nearest the ground (`XZPickingTests`).

## Pillar awareness

`BuildStructures` takes an optional `pillarObstructed(deckTop)` predicate; the
network-backed implementation is `StructureView.CarriagewayObstructed`: an XZ probe
against every *other* edge, obstructed when that edge's deck threads the pillar's
column (above the pillar base at ground, below the carried deck) within half-width
+ margin. A blocked eligible spot **defers** — `sincePillar` keeps accumulating so
the next clear span takes the pillar (the "shift"); past 2× spacing it skips one
outright. GhostView passes the same predicate, so the preview is placement-exact.
Trenches do *not* count as obstructions: the pillar base stops at Y = 0.

## Known limits (M8.5)

- A surface road crossing an **uncovered** deep trench visually spans the opening —
  no local bridge deck is synthesized. The player's fix is covering the trench;
  terrain will eventually make real geometry of it.
- Portal faces are flat quads with wing walls — evidence-grade, not beauty-grade.
- A covered edge dead-ending below ground shows no portal (see above).
- Vehicles are unaffected by grade (standing M8 limit).
