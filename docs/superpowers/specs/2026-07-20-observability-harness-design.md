# M8.75 — Observability harness (geometry dumps + golden-image diffing)

**Date:** 2026-07-20
**Status:** Approved (design), pending implementation plan

## Why

Geometry debugging is the project's dominant cost as the network model grows
(elevation, tunnels, roundabouts — soon zoning lots and building footprints).
Screenshots require pixel-guessing; the existing `CITYBUILDER_SHOTS_DUMP` covers only
rendered paint quads. Web research into LLM-agent game-dev practice (2026-07-20)
converged on two adoptable patterns this repo lacks:

1. **Agent-readable geometry exports** — dump domain state as text/vector (SVG/JSON)
   so geometry is read as exact coordinates, not squinted at in PNGs.
2. **Golden-image diffing** — committed baseline screenshots diffed with tolerance,
   catching silent render regressions that today only surface when a human (or agent)
   happens to re-read a gallery shot.

Both land *before* M9 Zoning & buildings, which introduces a new class of geometry
(zone strips, lots, shells) that will need this tooling from day one.

## Component 1 — `GeometryDump` (pure domain, `src/Domain`)

A static exporter taking a `RoadNetwork` (and optionally a `TrafficSim`) and producing:

### SVG (top-down plan view)

- Edge centerlines and carriageway outlines (offset by half road width).
- Node markers with ids; junction polygons.
- Lane-graph curves with direction arrows; conflict points.
- Roundabout rings highlighted.
- Text labels: elevation at sampled points (only where |Y| > 0.05 m), `Covered` flag,
  edge/node ids, road type.
- Organized as SVG `<g>` layer groups (`edges`, `lanes`, `junctions`, `labels`,
  `conflicts`) so a reader can attend to one layer at a time.
- Plan-view mapping: SVG x = world X, SVG y = world Z (ground plane is XZ, Y up).
  During implementation, verify this matches the screenshot harness's top-shot
  orientation and record the mapping in the SVG header comment so SVG and screenshots
  correlate directly.

### JSON

The same data structured: nodes (id, position, junction geometry), edges (id, type,
covered, sampled centerline polyline, width), lanes (id, kind, sampled polyline,
connectivity), conflict points. Exact-coordinate queries without SVG parsing.

### Call sites

1. **Any xUnit test** — one-liner, writes into the test output directory. Primary use:
   throwaway state probes during debugging (pair of `GeometryDump.Svg(network, path)` /
   `.Json(...)`).
2. **Fuzzer failure path** — when an invariant fails, alongside the existing replayable
   action tail, auto-dump SVG+JSON of the network at failure. A failing seed instantly
   ships with a picture.
3. **Smoke harness** — `CITYBUILDER_SMOKE=1` dumps the final network state next to its
   other outputs, giving every smoke run an inspectable artifact.

No in-game hotkey, no UI (YAGNI; triggers are test/harness-side only).

### Non-goals

Not a renderer-truth dump (that is `CITYBUILDER_SHOTS_DUMP`'s job): this is *domain*
truth. Discrepancy between the two is itself diagnostic signal. No vehicle/traffic
per-tick dumps this milestone (deferred; motion trails + invariants still cover motion).

## Component 2 — Golden-image diffing (screenshot harness extension)

- A curated subset of **stable** screenshot scenarios (~10–15 shots covering flat paint,
  junction control, bridges, tunnels/x-ray, roundabouts, elevated junctions) gets
  committed baselines under `tests/visual/golden/`.
- New harness mode: after producing shots, compare each against its golden with
  ImageMagick `compare -metric AE -fuzz <N>%`; fail (nonzero exit + report listing
  offending shots and changed-pixel counts) when the changed-pixel count exceeds a
  per-shot threshold. Tolerance absorbs AA/driver jitter; thresholds start uniform and
  get tuned per shot only if flaky.
- `CITYBUILDER_SHOTS_GOLDEN=check` runs the comparison;
  `CITYBUILDER_SHOTS_GOLDEN=update` regenerates baselines (an intentional act, reviewed
  via git diff of the PNGs).
- Traffic/motion scenarios are excluded (nondeterministic frame timing); only static
  composition shots participate.
- Full-res PNGs are committed: a curated set is a few MB, git history of goldens doubles
  as a visual changelog. Single-machine development makes GPU output stable enough;
  if cross-machine noise ever appears, per-shot fuzz thresholds are the first knob.

## Verification (of this milestone)

- **TDD** for `GeometryDump`: for a small known network (e.g. one T-junction with an
  elevated leg), assert the SVG contains expected elements at expected coordinates and
  the JSON round-trips the network's node/edge/lane counts and key coordinates.
- **Golden mode proves itself both ways**: (a) `check` passes on an unchanged build;
  (b) deliberately tint a material / nudge a constant locally and confirm `check` fails
  with a useful report, then revert.
- Standing quality stack: full `dotnet test` + build; fuzz suite stays green (the only
  fuzzer change is the failure-path dump hook — no alphabet/invariant change, so no
  10k re-certification required unless something else moves); KPI untouched;
  `docs/verification.md` gains the two new harness modes; roadmap updated. No new
  manual chapter — this is developer tooling, not a player-facing subsystem.

## Out of scope (recorded for later)

- Traffic state dumps (per-tick vehicle CSV/JSON).
- Replay-file formalization (fuzz tails / editor session recording as first-class
  artifacts).
- Godot runtime MCP bridge experiment (interactive agent input/screenshot loop) —
  candidates: tugcantopaloglu/godot-mcp (advertises 4.7 + C#), erodenn/godot-mcp-runtime.
