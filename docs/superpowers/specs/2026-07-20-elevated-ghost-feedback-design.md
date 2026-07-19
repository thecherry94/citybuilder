# Elevated ghost feedback & CS2 gradient caps — design

**Date:** 2026-07-20
**Status:** approved (user picked: ghost pillars + ground footprint, in-world elevation
labels, CS2-style gradient caps; per-segment steep coloring and clearance ticks were
explicitly not selected)

## Problem

M8 shipped elevation with a ghost that renders at true Y but gives almost no *height*
feedback: a flat blue/red strip floating in space, with elevation and gradient visible
only in the status-bar readout. And the per-type gradient caps (6–10%) are real-world
engineering values — at 6% a +6 m bridge needs a 100 m approach, which plays as
"the game won't let me build a bridge."

## 1. CS2-style gradient caps (domain)

`RoadCatalog` `MaxGradient` values change; nothing else does. `Validate`
(`PlacementError.TooSteep`), `RetypeEdge` (`RetypeError.TooSteep`), roundabout
conversion (`RoundaboutError.LegTooSteep`), `NetworkInvariants.CheckGradients`, and
fuzz all read `RoadType.MaxGradient`, so they follow automatically.

| Type | Old | New |
|---|---|---|
| Street, One-Way | 0.10 | **0.20** |
| Two-Lane, Asymmetric 2+1 | 0.08 | **0.15** |
| Four-Lane, Avenue | 0.06 | **0.12** |

A +6 m grade-separation now needs a ~40 m approach on the strictest types instead of
100 m — CS2's playability-over-realism stance.

Tests pinned to the old boundaries move to the new ones (same shape, new numbers):

- `SteepRampIsTooSteep`: 12% ramp → no longer steep for TwoLane; use 18%.
- `GradientLimitIsPerType`: 9% (TwoLane vs Street boundary) → 17% (over 15%, under 20%).
- `RetypeRefusesWhenTheExistingRampExceedsTheNewTypesGradient`: 8% ramp refused onto
  FourLane → 13% ramp (legal on TwoLane 15%, refused onto FourLane 12%, legal onto
  Street 20%).
- `ConvertingAJunctionWithSteepRampLegsIsRefusedNotCorrupted`: TwoLane legs at the full
  cap: 4.8 m/60 m (8%) → 9 m/60 m (15%). Linear ramps keep their gradient under
  trimming, so the leg still cannot descend to the ring plane within the cap.

Loosening caps cannot break the invariant checker (it only got more permissive), so the
existing fuzz suite must stay green unchanged.

## 2. Ghost structures: pillars, fascia, embankment skirts (game)

**One mesher, two consumers.** `StructureView.BuildStructures(RoadEdge)` becomes a thin
wrapper over a new curve-based static:

```csharp
public static ArrayMesh? BuildStructures(in Bezier3 curve, ArcLengthTable arc, float width)
```

It keeps baking `Materials.Earth`/`Materials.Concrete` into the two surfaces; the
committed path is byte-identical. `GhostView` calls it per proposal curve when the
validated placement instance changes (the existing reference-identity dirty flag),
constructing a throwaway `ArcLengthTable` per curve (128 samples, change-only — not
per-frame). Ghost instances render it with `MaterialOverride` =
`GhostValid`/`GhostInvalid` (override wins over baked surface materials), into a new
pooled `_structures` list, cleared/hidden exactly like `_strips`.

The preview therefore shows the *actual* pillars, girder fascia, and embankment skirts
the commit would produce — same thresholds (`EmbankmentMax`, pillar spacing), because
it is the same code.

## 3. Ground footprint (game)

When any proposal curve leaves the ground (any control point or endpoint Y > 0.5 m),
also render the curve *flattened to Y=0* through `MeshBuilders.BuildGhostStrip`
(a cubic's Y is bounded by its control net, so zeroing the four control-point Ys is an
exact ground projection; XZ offsets are unaffected). Material: new
`Materials.GhostShadow` — dark, ~25% alpha, unshaded, no depth write — visually a
shadow, never mistakable for a road. Pooled alongside the structure meshes. Ghost strip
Y-lift (+0.04) already prevents z-fighting at ground level.

## 4. In-world elevation labels (game)

Pooled `Label3D`s (the `_angleLabel` recipe: billboard, outline, ~0.05 pixel size) at
each **unique** proposal-curve endpoint with Y > 0.5 m, text `⬆ {Y:0} m`, positioned
a few metres above the deck. Dedupe endpoints by rounded XZ+Y so chained curves don't
double-label their shared joint. The status-bar readout stays as-is.

Not in scope: per-segment red coloring of the too-steep stretch, clearance tick markers
at crossings, terrain-aware footprints (ground is flat Y=0 in this milestone).

## Data flow

Unchanged: domain validates and emits `ValidatedPlacement`; `GhostView.Show` renders
it. All new visuals derive from `placement.Proposal.Curves` inside `GhostView` — no new
domain surface, no new session state.

## Error handling

- Degenerate/short curves: `BuildStructures` and `BuildGhostStrip` already return null
  meshes; pooled instances just stay hidden.
- Ground-level drafting must produce zero new nodes' visible cost: structures/footprint
  builders short-circuit on the all-Y≈0 case (existing `anyElevated` check).

## Verification

1. `dotnet test` — updated gradient boundary tests green (305 total count may shift).
2. `dotnet build citybuilder.sln`.
3. `CITYBUILDER_SMOKE=1 godot --headless .` — SMOKE OK.
4. Screenshot harness: new/extended scenario with a live `GhostView` showing an
   elevated draft (valid + invalid variants); read the shots to confirm pillars,
   footprint shadow, and elevation labels are present and legible.
5. Fuzz 10k spot run (caps loosened only; expect green with zero test edits).
