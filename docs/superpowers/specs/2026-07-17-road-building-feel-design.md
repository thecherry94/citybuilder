# Road-Building Feel — Design (Milestone 6.75)

**Date:** 2026-07-17
**Status:** Approved (user-set scope: CS2-faithful snapping *semantics* with its documented
weaknesses fixed; long-range 90° guides; per-kind snap indicators; ghost mesh pooling;
first audio; 8 m cell-length ticks added at design review. Explicitly descoped: numeric
length readouts, richer ghost mesh. Undo/redo + upgrade-in-place remain M7.)

## Motivation

Road building works but doesn't *feel* like Cities: Skylines 2. Three concrete
complaints, all confirmed in code:

1. **T-junction sliding.** Snapping is candidate-scored (`score = distance / weight`,
   lowest wins, node weight 4.0 vs edge 2.0). On a junction leg the edge is at ~0 m, so
   its score is ~0 and the node only wins when the cursor is nearly on top of it. CS2
   hard-captures the node the moment you're close: the ghost endpoint pops onto the node
   and stays pinned while the cursor drifts.
2. **No long-range 90° helpers.** Guidelines today are only tangent extensions out of
   node legs. There is no perpendicular helper projected from distant geometry, so
   connecting two far-apart roads at a right angle is guesswork.
3. **Anonymous snap indicator.** One sphere for every snap kind; the user never sees
   *what* they are snapped to. Plus: `GhostView` frees and reallocates every preview
   mesh on every mouse move (perceived sluggishness), and the project has no audio at
   all.

Research on CS2 (two web passes, 2026-07-17; sources in the research notes at the
bottom) adds the twist: CS2's own snapping is *criticized* by its players — the 90°
angle snap accepts within a **constant lateral band in world meters** (decompile-level
evidence), so its angular window shrinks with segment length and long roads can commit
179.6°; overlapping snap candidates fight each other with no priority feedback; and the
community workaround is "turn most snaps off". The scope decision is therefore: **clone
CS2's semantics and rhythm (sticky node capture, dashed guides, the 8 m cell), but fix
the weaknesses — length-independent angular snapping with exact committed angles,
hysteresis against candidate fighting, and always-visible snap state.**

## Non-goals

- No numeric length/angle readouts at the cursor beyond the snap-kind badge (the arc
  badge shows the snapped angle; nothing else). No radius readout changes.
- No richer ghost mesh (lane markings / junction paint preview) — the translucent
  strip stays.
- No zoning-cell preview, no named-error taxonomy or red/orange severity tiers, no
  replace mode, no elevation (M8), no undo/redo or upgrade-in-place (M7).
- No CS2-style lateral-band angle snap — ours stays a hard 15°-step *angular*
  quantization (constant in degrees, so it never weakens with segment length); we adopt
  the *readout-exactness* property (a snapped commit is exactly on the ray), not the
  magnetic weakness.
- Audio: exactly five one-shot SFX on a single bus. No music, no mixing UI, no
  spatialization, no vehicle/ambient sounds.

## 1. Snapping v2.1 — sticky capture + hysteresis (`SnapEngine`)

The candidate-scoring engine survives unchanged in shape; two mechanisms layer on top.

**Hard node capture.** A node candidate within `NodeCaptureRadius = 0.6 × radius` of
the raw cursor (radius = the zoom-scaled resolve radius the game already passes) enters
a *hard tier*: hard-tier candidates outrank every soft candidate regardless of score;
among several hard candidates the nearest wins. Between 0.6× and 1.0× radius the node
competes by weight as today (the soft approach zone — preserves the current
`NodeBeatsEdge` far-field behavior). Result: sliding along a T-junction leg, the
endpoint pops onto the node as soon as the cursor is genuinely close, and edge snapping
near junctions only applies where a mid-span split is actually intended.

**Hysteresis (node captures only).** `SnapContext` gains `HeldNode: NodeId?` — the
node captured on the previous resolve, threaded through by `DraftSession` (the engine
stays stateless; the session owns the memory). While `HeldNode` is set, that node
releases only when the cursor moves beyond `ReleaseFactor = 1.4 × NodeCaptureRadius`
from it; inside the release ring the held node stays the winner even if an edge or a
fresh candidate scores better. Crossing into a *different* node's capture radius
transfers the hold (nearest hard capture wins — no dead zone between adjacent nodes).
This kills candidate flicker — CS2's top complaint — without changing what wins first.

Constants (all tunable, `SnapEngine`): `NodeCaptureFraction = 0.6`,
`ReleaseFactor = 1.4`.

**Angle-snap exactness (no change, now guarded).** Our angle snap already quantizes to
exact 15° rays measured from `ReferenceTangent` — the CS2 "179.6°" failure cannot
happen. Decompiled CS2 code shows why theirs fails: direction snap accepts within a
constant *lateral* band in meters, so the effective angular window shrinks ∝ 1/length
and long roads barely snap. Our quantization is *angular* (constant in degrees at any
length) — already the strong version; the milestone adds a regression test pinning the
invariant (committed direction exactly on the snapped ray) so it survives future
snapping work.

## 2. Cell-length ticks (`SnapTypes.CellLength`, new toolbar toggle)

CS2's "snap to zoning cell length": with an anchor set, the drawn segment's *length*
ratchets in `CellLength = 8 m` increments.

- **As a candidate:** emit `anchor + dir(raw − anchor) × round(|raw − anchor| / 8) × 8`
  with `WeightCellLength = 1.2` — a weak snap, loses to any geometry snap nearby.
- **Composed with angle snap:** when the directional fallback fires, the angle-snapped
  result's length is then quantized to the same 8 m ticks (angle *and* length — clean
  diagonals and grid spans, the CS2 rhythm).
- Ignored when no anchor exists (first click of a draft). Default ON; toolbar toggle
  beside Grid. Grid default cell stays 8 m — one shared rhythm.

## 3. Long-range perpendicular guides

`CollectGuidelines` today emits one guide per node leg: the tangent *continuation*
beyond the node. Add, per leg, the two **perpendicular guides** (leaving tangent
rotated ±90°, origin at the node, same `GuidelineReach = 200 m`). Existing machinery
does the rest for free: perpendicular guides participate in guideline projection,
pairwise **guide-intersection candidates** (weight 2.5 — connecting two distant roads
at 90° snaps to the dashed crossing), and parallel-guide interplay.

Perf note: guide count per node roughly triples (1 → 3 per leg). Collection is already
bounded by `GuidelineSearch = 200 m`; the pairwise intersection loop is O(G²) — with
tripled G, worst-case dense-city counts need a cap. Guard: if collected guides exceed
`MaxGuidelines = 48`, keep the 48 nearest by origin distance (tunable; prevents O(G²)
blowup, far guides are visual noise anyway).

## 4. Readable snap indication (`GhostView` v2)

Replace the anonymous sphere with a per-kind indicator, each a small pooled mesh at the
snap position (flat on the ground, +0.2 m, ghost-accent colors):

| SnapKind | Indicator |
|---|---|
| Node | lock ring (flat torus) around the node — *the* capture signal |
| Edge | short tick mark across the edge at the snap point |
| GuidelineIntersection | filled dot where the two dashed guides cross |
| Perpendicular | right-angle glyph (two short strokes forming an L) at the foot |
| Guideline | small dot sliding on the dashed line |
| GridPoint / GridLine | cell-corner / cell-edge highlight quad |
| Angle | arc badge at the anchor + `Label3D` with the snapped angle (e.g. "90°") |
| CellLength (composed) | tick marks every 8 m along the ghost centerline near the end |

Guides themselves stay dashed lines; guide *intersections* additionally get the dot
even when not the active snap (CS2 shows the crossing you could snap to).

## 5. Ghost mesh pooling (the sluggishness fix)

`GhostView.Show` currently `QueueFree`s and reallocates every strip, handle, and
indicator on every mouse move. Rework:

- Pool `MeshInstance3D`s per kind (strips, handles, indicators); `Visible = false`
  instead of free; grow-on-demand, never shrink during a session.
- Reuse `ArrayMesh` objects — clear surfaces and refill rather than allocate.
- Skip rebuild entirely when the ghost state is unchanged (session exposes a cheap
  revision counter bumped on any state mutation; `ToolController` compares).

No easing/interpolation anywhere — decisive means the ghost tracks the cursor
instantly; feel comes from capture/hysteresis, not animation.

## 6. First audio (`AudioFx`, Game layer)

A single `AudioFx` node under `Main` owning one `AudioStreamPlayer` pool (4 players,
round-robin). Five one-shot events, mapped by `ToolController` from observable state
transitions (domain stays pure — no audio concepts in `src/Domain`):

| Event | Trigger (state diff in ToolController) | Sound |
|---|---|---|
| Snap tick | active snap (kind, target id) changed to a non-Free snap | short tick |
| Point placed | click accepted by the session (handle added) | soft click |
| Road committed | session commit succeeded | plop |
| Invalid click | click on invalid ghost / commit refused | muted error blip |
| Bulldoze | edge/node removed via bulldoze | crunch |

Assets: Kenney CC0 packs (interface sounds / impact sounds), 5 OGG files committed
under `assets/audio/` with an `assets/audio/LICENSE.md` provenance note. Snap ticks are
rate-limited (≥ 60 ms apart) so guide-hopping doesn't machine-gun.

## Testing & verification (quality-stack DoD)

Domain tests (`tests/Domain.Tests/Tools/`):
- `NodeCaptureBeatsEdgeOnLeg` — cursor on a T-junction leg, 3 m from the node: node
  wins (the complaint scenario, currently fails by design of the old weights).
- `SoftZonePreservesFarBehavior` — node between 0.6–1.0× radius competes by weight
  (ports `NodeBeatsEdge` semantics forward).
- `HysteresisHoldsInsideReleaseRing` / `ReleasesBeyondRing` / `TransfersToNearerNode`.
- `AngleSnapCommitExactlyOnRay` — invariant guard (§1).
- `CellLengthQuantizes` + `CellLengthComposesWithAngleSnap` (27 m free drag → 24 m;
  angle-snapped drag lands on both the ray and an 8 m tick).
- `PerpendicularGuidesConnectDistantRoads` — two roads ~150 m apart: guide-intersection
  candidate appears at the 90° crossing.
- `GuidelineCapRetainsNearest` — cap keeps the 48 nearest, none dropped under the cap.

Fuzzer: gesture fuzzer gains the CellLength toggle in its action alphabet; invariants
unchanged; 10k-action run green ×3 seeds. Existing snap tests re-tuned only where the
hard tier intentionally changes outcomes (each such change called out in the PR).

Game-side: screenshot harness gains shots per indicator kind (node ring, edge tick,
guide-intersection dot, perpendicular glyph, angle badge, cell ticks) plus a
perpendicular-guide long-range scene — verified by reading the images. Smoke run covers
`AudioFx` load (headless dummy audio driver — asserts streams load, not sound).
Sluggishness fix verified by a frame-cost probe before/after (ghost update under
continuous mouse motion, printed µs; no formal KPI, numbers quoted in the health doc).

DoD: KPI harness rerun (expect traffic metrics unchanged — editor-only milestone),
`docs/health/M6.75.md`, manual chapters drift-updated (road tools + new audio note),
roadmap updated.

## Implementation notes (deltas from this spec, recorded at ship time)

- **Ghost dirty-skip**: implemented as a `ValidatedPlacement` reference cache inside
  `GhostView` instead of a session revision counter — the session already emits a
  fresh instance per real change, so reference identity is the dirty flag; fewer
  moving parts, same effect. Measured ~543 → ~113 µs per render.
- **Audio source**: synthesized in-repo (`tools/sfxgen`, fixed seed, byte-stable,
  CC0-equivalent) instead of downloading Kenney packs — deterministic and
  license-trivial. Five WAVs under `assets/audio` with provenance in `LICENSE.md`.
- **Angle badge**: rendered as a `Label3D` degree readout + snapped-tip dot + cell
  ticks; no arc mesh (the arc added clutter without information).
- **Capture-floor amendment (found by smoke)**: the spec's ring `0.6 × radius` alone
  breaks at small zoom-scaled radii (~2.4 m): cell-length quantization can commit a
  node up to 4 m from the raw click, and a follow-up click at the intended spot then
  sits on that node's own continuation guide, which outscores it — committing a
  disconnected duplicate node. Shipped as `max(0.6 × radius, NodeCaptureFloor = 3 m)`
  with a regression test; mirrors CS2's world-space-absolute snap distance.
- **Gallery-only snap states**: the perpendicular-arrival glyph is practically
  unreachable with Edge snapping on (edge at ~0 m always outscores the foot); the
  screenshot gallery disables Edges for that station, mirroring how a player would
  actually reach it.

## Research notes (CS2 reference, two passes, gathered 2026-07-17)

Pass 1 (docs/community) and pass 2 (mod source + decompiled `Game.dll` snippets via
bwolman/cs_modding_claude, yenyang/Anarchy, optimus-code/Cities2Modding, et al.). Key
facts the design leans on:

- CS2 snap toggles: all-snapping master, existing geometry, zoning cell length (8 m
  ticks), 90° angles, building sides, guidelines, zone grid. Node capture is sticky;
  node beats edge near junctions (observed).
  ([cs2.paradoxwikis.com/Roads](https://cs2.paradoxwikis.com/Roads))
- **Tier-then-distance priority is how CS2 actually works** (decompiled call sites):
  candidates carry a `float2` snap priority from
  `ToolUtils.CalculateSnapPriority(level, …)`; net node/area candidates pass
  `level = 1`, guides/grid/direction/shoreline pass `level = 0` — a hard tier that
  beats any distance advantage, with scaled world distance deciding within a tier.
  Our hard node-capture tier is the same architecture, sharpened (node above edge in
  the capture ring, which CS2 leaves to distance — the T-junction fix).
- **Angle snap weakness confirmed at code level**: direction snap accepts iff the
  lateral offset from the candidate ray is below a constant world-meters threshold
  (`< m_DistanceScale`), so the angular window shrinks with length — the "179.6° on
  long roads" complaint ([Paradox forum](https://forum.paradoxplaza.com/forum/threads/180-and-90-degree-hard-snapping.1608414/)).
  Ours stays angular (15° steps, constant at any length) with exact commits.
- **The 8 m cell is a code constant**: `Game.Zones.ZoneUtils.CELL_SIZE = 8f`;
  zone/building snapping quantizes `round(x/8)·8` with 4 m parity offsets; snap
  distances are normalized into 8 m units for scoring. Validates the cell-length tick
  design and the 8 m default grid.
- CS2 snap radii are **world-space and per road prefab** (`PlaceableNetData
  .m_SnapDistance`); no camera-zoom scaling found in code. Our zoom-scaled resolve
  radius is a deliberate divergence (our camera ranges far wider than CS2's).
- Guides are dashed continuation + perpendicular helpers with a marker at crossings;
  stated use "connecting different road networks & grids" (CO Dev Diary #1,
  [colossalorder.fi](https://colossalorder.fi/?p=1547)). `NetToolSystem`'s guideline
  generation internals are not public — reach/appearance remain observation-grade.
- The 8 m rhythm elsewhere: parallel offsets 0.25–12 cells, elevation ladder
  1.25/2.5/5/10 m.
