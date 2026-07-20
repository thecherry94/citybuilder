# M9 — Zoning & buildings

**Date:** 2026-07-20
**Status:** Approved (design); implementation deferred until plan approval
**Roadmap slot:** Next-up #1 — "purely visual city-ness; no economy yet"

## Why

The road network is certified through M8.5; the next step toward CS2 gameplay is a
city that *looks* alive. M9 adds player-painted zoning and demand-free procedural
building growth. Everything here is a foundation for M10 (citizens & destinations):
buildings become trip origins/attractors later, so identity and determinism matter
more than visual richness.

Decisions locked during brainstorming (2026-07-20):
- **CS2-style cell grid** (8 m cells, up to 6 deep, following edge curves) — not
  strips, not free rectangles.
- **Six zone kinds**: R/C/I × low/high density.
- **Gradual seeded growth** on a sim tick — deterministic, staggered, no demand model.
- **Brush + side-fill painting**, RMB erase.

## 1. Domain model — `src/Domain/Zoning/`

New namespace `CityBuilder.Domain.Zoning`. The domain stays Godot-free (golden rule 1).

### ZoneKind

```
enum ZoneKind : byte { None = 0, ResidentialLow, ResidentialHigh,
                       CommercialLow, CommercialHigh, IndustrialLow, IndustrialHigh }
```

### Cell addressing (derived, never stored as geometry)

A cell is `(EdgeId Edge, EdgeSide Side, int Station, int Depth)` with
`enum EdgeSide : byte { Right, Left }` (right = positive lane offsets = driver's right
travelling Forward, per conventions).

- **Stations**: 8 m arc-length slots along the edge's *drawable span* (between junction
  cuts, `JunctionGeometry.CutT` — zoning never reaches into junction areas). Station
  count = `floor(drawableArcLength / 8)`; the leftover (< 8 m) at the far end is
  unzonable. Station boundaries are computed via `ArcLengthTable.TAtDistance`.
- **Depths**: bands 0–5. A cell's inner boundary sits at lateral offset
  `RoadType.OuterHalf + Depth·8`, outer boundary at `OuterHalf + (Depth+1)·8`, built
  with `Bezier3.OffsetPoint` at the station-boundary t values. A cell quad is the four
  corner points (two consecutive station boundaries × two depth offsets), flat at the
  host edge's ground Y (= 0, see validity).
- **Degenerate cells dropped**: on tight inside curves offset quads can pinch. A cell
  exists only if its quad is **non-self-intersecting with area ≥ 32 m²** (50 % of the
  nominal 8×8 = 64 m²) — one rule, tested.

### ZoneCellGeometry (pure functions)

`static class ZoneCellGeometry`: given `(RoadEdge, RoadType, JunctionGeometry cuts)`
produces per-side `CellBand` (stations × depths quads + validity flags). Deterministic,
cached per `RoadNetwork.Version` by consumers (same pattern as connector-speed cache).

### ZoneRules (ONE validity classifier, like `VerticalRules`)

A cell is **paintable** iff all of:
1. **Host edge is fully at ground**: `max |Y| along curve < JunctionYTolerance (0.6 m)`
   — no zoning on ramps, bridges, tunnels (CS2-like; revisit with terrain).
2. **No carriageway/junction overlap**: the cell quad intersects no edge's carriageway
   band (width = `OuterHalf·2`) and no junction `SurfacePolygon` (AABB prefilter from
   M8.5 reused — this is a Validate-grade spatial query, must stay < the perf gate).
3. **Nearest-edge-wins band arbitration**: if the quad also lies inside *another*
   ground edge's potential band (parallel roads closer than ~2×48 m), the cell belongs
   to the edge whose centerline is nearest to the quad center; the loser's cell at that
   position is invalid. Deterministic tie-break by `EdgeId`.

Rules are evaluated at paint time AND re-evaluated on network deltas (a new road
stamped through a zoned band invalidates the overlapped cells → their buildings
despawn; see re-flow).

### ZoneMap (stored state)

`sealed class ZoneMap` owned by the same aggregate that owns `RoadNetwork`
(constructed with a reference to it, subscribes to `Changed`):
- Per `(EdgeId, EdgeSide)`: `ZoneKind[] kinds` indexed `[station · 6 + depth]`, plus
  `float PaintedAt` per cell (sim-time seconds; drives growth staggering).
- **Building registry**: `IReadOnlyDictionary<BuildingId, Building>`;
  `record struct BuildingId(int Value)`.
- `Building`: `Id`, `ZoneKind Kind`, `EdgeId Edge`, `EdgeSide Side`,
  `int Station, int Depth` (anchor = min corner), `int FrontageCells, int DepthCells`,
  `int StyleSeed`, `float Height`, world footprint polygon (derived, cached).
- Mutations: `Paint(cells, kind)`, `Erase(cells)`, `FillSide(edge, side, kind)` —
  all validate through `ZoneRules`, all bump a `Version`, all raise a delta event for
  the render layer, all undo-checkpointed by the caller (same contract as network
  mutations).

### Topology re-flow (the CS2 rule: road edits re-flow zoning)

On `NetworkDelta`:
- **Split** (edge replaced by children): remap cell paint by arc length onto the
  children (a station maps to the child covering its midpoint distance); claims
  (buildings) remap iff their full rect survives contiguously, else despawn.
- **Removed edge**: its cells and their buildings despawn.
- **Retype**: `OuterHalf` may shift band offsets — kinds/stations survive (station
  grid is arc-length-based), quads re-derive; buildings whose footprint now violates
  `ZoneRules` despawn.
- **EdgesChanged / junction cut moves**: stations re-derive from new cuts; paint
  remaps by arc length; out-of-range stations drop.
- **New edge near zoned band**: re-evaluate rule 2/3 for overlapped cells only
  (AABB prefilter); invalidated cells lose paint and despawn their buildings.

## 2. Growth — `ZoneSim`

`sealed class ZoneSim(ZoneMap map, int seed)` with `Tick(float dt)`; owns nothing else.
**Zero traffic-sim changes** (target: 5th consecutive milestone).

- Maintains `SimTime` (float seconds, saved in v4 so growth resumes after load).
- A **candidate** is a maximal vacant zoned rect anchor; scan is incremental (dirty
  per edge-side on any paint/re-flow, not a global sweep per tick).
- Spawn delay per candidate = `GrowthDelayBase (12 s) + jitter` where jitter ∈ [0, 30 s)
  from `hash(edgeId, side, station, depth, seed)` — stateless determinism, no RNG
  stream to persist. Delay measured from the newest `PaintedAt` among the rect's cells.
- On expiry: claim the **largest fitting rect** up to the kind's max (table below),
  greedy frontage-first; spawn `Building` with `StyleSeed = hash(...)`, height from the
  kind's range keyed by seed. Cells under a claim are occupied (not paintable-over;
  erase despawns).
- Dezone/erase under a building, or any cell of its claim invalidated → **despawn
  immediately** (no rubble state — YAGNI).

| Kind | max frontage×depth (cells) | height range m | shell look |
|---|---|---|---|
| ResidentialLow | 2×2 | 4–9 | small box + gable roof, warm palette |
| ResidentialHigh | 3×3 | 15–40 | tower on podium, flat roof |
| CommercialLow | 2×2 | 5–8 | flat roof + fascia strip, cool palette |
| CommercialHigh | 3×3 | 20–45 | glassy tower, flat roof |
| IndustrialLow | 3×2 | 5–7 | low wide hall, saw/flat roof, grey-brown |
| IndustrialHigh | 4×3 | 8–14 | big hall + silo/stack block accents |

Minimum claim is 1×1 for all kinds (a lone valid cell still grows something).
Footprint polygon = claimed cell quads' union inset by 1 m (visual setback).

## 3. Editor & UI — `src/Game`

- **`ToolMode.Zone`** with a current `ZoneKind` selection; toolbar gains a zoning
  section: 6 kind buttons + eraser (reuses the road-type-button pattern).
- **Overlay**: while the tool is active (or `Z` toggle on), cell grids render near
  roads within a radius of the cursor (translucent quads, kind colors below); painted
  cells always render when overlay is on. Colors: ResLow `#7bd07b`, ResHigh `#2f9e44`,
  ComLow `#74a9e8`, ComHigh `#1d6fd1`, IndLow `#e8c46a`, IndHigh `#c9962a`
  (α ≈ 0.45), eraser highlight red.
- **Brush**: LMB drag paints every valid cell under a 1-cell-radius brush;
  **Shift+LMB** fills the hovered edge side at full 6-cell depth (valid cells only);
  **RMB drag** erases. One undo checkpoint per gesture (mouse-down → mouse-up).
- **Buildings render** as procedural shells: per style family a small mesh builder
  (box, roof variant, accent block per the table), batched with MultiMesh per family;
  per-instance tint variation from `StyleSeed`. No textures, no assets — palette +
  proportion carry the look (matches the project's procedural aesthetic).
- Bulldoze tool unchanged: bulldozing roads despawns via re-flow. (Direct
  building-bulldoze: the eraser over its cells — no separate tool this milestone.)

## 4. Persistence — save format v4

`SaveGame` bumps to v4: per-edge-side zone arrays (kind + PaintedAt), buildings
(all fields incl. StyleSeed), `ZoneSim.SimTime`, and the zoning seed. v1–v3 load with
empty zoning. Byte-stable round-trip as always. Undo snapshots automatically cover
zoning once it lives in `SaveGame`.

## 5. Verification (standing quality stack + new surface)

- **TDD domain tests**: station/depth derivation on straight+curved+short edges
  (counts, quad corners, degenerate-drop rule); `ZoneRules` truth table (elevated
  host, carriageway overlap, junction overlap, nearest-edge arbitration + tie-break);
  paint/erase/fill semantics; re-flow on split/remove/retype/new-crossing (buildings
  despawn exactly when their cells die); growth determinism (same seed ⇒ identical
  building set at t=120 s), staggering (not all at once), claim maxima per kind;
  v4 round-trip byte-stable; v3 loads empty-zoned.
- **Invariants (fuzzer-audited)**: no building without all its claimed cells zoned and
  valid; no two claims overlap; no zoned cell on a non-ground edge; no cell quad
  intersects a carriageway/junction polygon; ZoneMap references only live EdgeIds.
- **Fuzzer alphabet grows**: paint-brush, side-fill, erase, zone-then-bulldoze-host,
  zone-then-split-host (crossing draw), with `ZoneSim.Tick` bursts folded into the
  existing `SimInvariants.CheckBurst` cadence. Milestone cert = 3×10k as usual.
- **KPI**: new `zoning.grown_at_120s` (deterministic scenario: fixed grid, all six
  kinds painted, tick 120 s, count buildings — banded ±25 %) and
  `perf.zonetick_ms` + `perf.paint500_ms` absolute ceilings (growth tick and paint
  re-validation must not regress editor feel; ceilings set at implementation from
  first measurements, then frozen).
- **GeometryDump**: new `zones` SVG layer (cell quads colored by kind, buildings as
  footprint polygons labeled `b{id} {kind} h={height}`); JSON gains `zones` +
  `buildings` sections. The M8.75 harness pays for itself here.
- **Gallery**: `zoned_block` scenario (small grid, all six kinds, growth
  fast-forwarded 120 s) with top/oblique/driver-height shots, added to
  `GoldenScenarios` (goldens regenerated via `update`, reviewed).
- **Smoke**: extend the scripted run — paint zones on the starter roads, tick until
  ≥ N buildings exist, assert despawn on bulldoze, quicksave/quickload round-trip.
- **UITEST**: zoning toolbar interaction + painted-overlay screenshot.
- **Manual**: new chapter `docs/manual/12-zoning.md` + drift pass; roadmap + KPI
  baseline + `docs/health/M9.md` regenerated at close.

## Out of scope (recorded)

Demand/economy; citizens/trips (M10); building levels, construction states, rubble;
zoning on elevated/underground roads; lot merging across edges; per-cell depth
painting UI beyond the brush (depth always fills what the brush covers); dedicated
building-bulldoze tool; localization of kind names.

## Sequencing note

Implementation order in the plan should front-load `ZoneCellGeometry` + `ZoneRules`
with their tests (the geometric risk), then ZoneMap/re-flow, then ZoneSim, then
editor/rendering, then harnesses — each stage landing green and committed before the
next, per golden rules 2–3.
