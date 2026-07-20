# M8.5 — Trenches & tunnels (design)

**Date:** 2026-07-20
**Status:** approved (explicit covered flag; save v3; x-ray auto+toggle)
**Milestone:** M8.5, second half of the vertical pair started in M8
(`2026-07-19-elevation-bridges-design.md` reserved this scope).

## Problem

The domain has been signed-Y since M8 — the three-band crossing classifier, gradient
caps, heal/retype guards, and roundabout rules all work on ΔY and |dY/ds| and need no
change below ground. But the *editor* clamps elevation to [0, +50], nothing renders a
cut or a tunnel, an opaque ground plane hides anything that did go under, and pillars
happily stand in underpass carriageways (M8 known limit). M8.5 unlocks the negative
half: trenches with retaining walls, player-covered tunnels with portals, an
underground x-ray view, pillar placement awareness, and the queued fuzz wall-clock
profiling pass.

## Decisions locked with the user

- **Covered is an explicit per-edge flag**, not depth-derived (depth-derived was
  proposed and rejected — the player chooses open cut vs tunnel, CS2-mod style).
  Cost accepted: save format v3, upgrade-tool surface, fuzz alphabet growth.
- **X-ray view auto-engages while drafting below ground**, plus a manual toggle key.
- **Full autonomous run** through spec → plan → TDD → certification (standing
  preference re-confirmed for this milestone).

## Standing assumption (unchanged from M8)

Ground is the Y=0 plane. Depth below is structural, mirroring height above. If terrain
lands later, "ground" becomes `terrain(x,z)` everywhere at once.

## Architecture — one new bit of domain state, everything else derives

M8 shipped zero stored structure state. M8.5 adds exactly **one bit per edge**:
`RoadEdge.Covered`. Everything else stays derived:

- **Span classification** (renderer): sampled deck Y classifies each stretch —
  above-ground bands unchanged (embankment / bridge); below ground, an *uncovered*
  edge renders an **open cut** (retaining walls ground→deck) and a *covered* edge
  renders a **tunnel** (portals at the threshold crossings, carriageway visible only
  in x-ray).
- **Covered semantics**: the flag means "cover my below-ground spans". It has **no
  validation surface** — toggling is always legal on any edge; spans at or above
  ground ignore it (no glass tunnels in the sky, by derivation not by rule). This
  keeps the fuzzer's invariant set unchanged and makes nonsense states unrepresentable
  instead of rejected.
- **Traffic:** zero sim changes (fourth milestone running). Under-crossings are
  already grade-separated; covered is invisible to routing.

Rejected alternatives: depth-derived covering at −`MinClearance` (user rejected —
wants the choice); explicit tunnel entities with a registry (roundabout-style — far
more machinery than one flag, and nothing here needs identity beyond the edge).

## Domain

### Constants (`GeoConstants` additions/changes)

| Constant | Value | Meaning |
|---|---|---|
| `MaxDepth` | 50 m | Editor clamp floor becomes −MaxDepth (domain stays unclamped). |
| `PortalDepth` | 3.0 m | Deck depth at/below which a covered edge's span renders as tunnel tube; the portal face sits where the deck crosses this depth. Above it a covered edge still renders as open cut (a portal needs headroom). |

`MinClearance`, `JunctionYTolerance`, gradient caps (20/15/12%), and all four gradient
enforcement altitudes are untouched — they are already signed/absolute.

### Editor unlock

`DraftSession.CurrentElevation` clamps to **[−MaxDepth, +MaxElevation]**. PgUp/PgDn
±5 m and Ctrl ±1 m unchanged. Ghost badges gain the negative direction: `⬇ N m` (the
M8 `⬆` badge path, signed). The elevation/gradient readout shows negative depth.

### Covered flag

- `RoadEdge.Covered` (bool, default false).
- `RoadNetwork.SetCovered(EdgeId, bool)` — in-place, same-`EdgeId` (the `RetypeEdge` /
  `FlipEdge` family), no validation errors possible, raises `NetworkDelta.EdgesChanged`
  so the mesh rebuilds. Undo-checkpointed like every mutation.
- **Split/heal/absorb propagation:** children of a split inherit the parent's flag;
  heal keeps the flag iff both merged edges agree, else false (mixed heal = open —
  conservative and visible, never a surprise tunnel). Roundabout leg trims and
  approach splits propagate like any split.
- **Upgrade tool:** a "Covered" toggle joins the upgrade toolbar; in Upgrade mode with
  it active, LMB toggles `Covered` on the hovered edge instead of retyping.
  (RMB flip unchanged.)

### Save format v3

`EdgeDto` gains `Covered` (default false). v1/v2 saves load with false. Byte-stable
round-trip preserved; `FormatVersion` bumps to 3.

## Rendering (Game side)

### StructureView bands (below-ground mirror of M8's above-ground bands)

Per sampled span, with `d = −deckY` (depth):

| Condition | Structure |
|---|---|
| `d ≤ EmbankmentMax` (1 m) | Shallow cut curb — the mirror of the embankment skirt. |
| deeper, edge **uncovered** (or covered but `d < PortalDepth`) | **Open cut**: retaining walls from ground lip down to deck edge, both sides; ground-level coping strip so the cut reads from above. |
| deeper, edge **covered**, `d ≥ PortalDepth` | **Tunnel**: nothing rendered at the surface except the two **portals** (arch face + wing walls) where the deck crosses `PortalDepth`; deck/walls of the tube render only in x-ray. |

Same span-sampling machinery that places pillars today; `BuildStructures` stays the
single shared path so the **ghost previews cuts/portals exactly** (the M8 elevated
ghost work carries over for free — badges, footprint shadow, structure preview).

Known accepted artifact: a surface road crossing an *uncovered* deep trench visually
spans an open pit (no local bridge deck is synthesized). CS2 avoids this with terrain
deformation we don't have; the player's fix is toggling the trench covered. Documented
as a known limit, not special-cased.

### Underground / x-ray view

A Game-side `ViewMode` (Normal / XRay):

- **XRay:** ground plane material swaps to a translucent grid, surface-level and
  elevated roads dim, below-ground carriageways render normally (they already mesh —
  today the opaque plane hides them). Tunnel tubes render their deck + walls.
- **Auto:** entering a draft whose effective elevation is below ground switches to
  XRay for the draft's duration (restore on confirm/cancel); the `U` key toggles
  manually any time. Auto never fights the manual toggle: manual sets the mode the
  draft restores to.
- Mouse picking is analytic against Y=0 (`CameraRig.MouseGroundPoint`), so drawing
  underground needs no picking change — cursor picks XZ, elevation applies as today.

### Pillar placement awareness (M8 known-limit fix)

`BuildStructures` gains an optional obstacle predicate `Func<Vector3, bool>` (null =
current behavior, keeps the API curve-pure). The network-backed implementation answers
"is this XZ point inside any *other* edge's carriageway (half-width + 1 m margin) whose
deck there is below the pillar's deck?" Pillar placement tries the nominal station,
then shifts along the span (± up to half the 24 m spacing, 2 m steps), else skips that
pillar. Both `StructureView` (committed) and `GhostView` (preview) pass the same
predicate, so WYSIWYG holds.

## Fuzzing

- **Alphabet:** elevation steps now go negative (same weights as positive); new
  `ToggleCovered` action on random edges.
- **Invariants:** unchanged set (covered has no legal-state surface). Save round-trip
  invariant now certifies v3 byte-stability with mixed covered flags organically.
- **Perf pass (queued since M8):** 10k×3 grew to ~45 min when elevation entered the
  alphabet. Profile the per-action invariant audit (prime suspects: re-intersection
  scans and `MaxGradient` resampling on unchanged edges), land targeted caching or
  prefilters (the Y-band prefilter pattern), target **≥ 2× wall-clock reduction** with
  zero invariant weakening — measured before/after in the health doc.

## Verification & DoD (quality stack, standing)

- TDD throughout; regression tests prefer invariants (flag propagation on
  split/heal, v3 round-trip byte-stability, no-pillar-in-carriageway as a geometric
  assert over sampled placements).
- Gallery: `trench_{shallow,deep}`, `tunnel_portal`, `tunnel_xray`, `underpass_pillars`
  (pillar-awareness before/after), elevated-ghost shots extended with a below-ground
  draft. Screenshots read, not just produced.
- Smoke: scripted underpass — draw a road under an existing arterial, cover it,
  toggle x-ray, quicksave/quickload, SMOKE OK.
- UITEST: covered-toggle flow via the upgrade toolbar.
- 3×10k fuzz green with the extended alphabet; KPI baseline regenerated +
  `docs/health/M8.5.md` (perf pass numbers headline); manual drift-updated (ch10
  extended or ch11 added); roadmap updated.

## Out of scope (documented forward)

- Terrain (own milestone; every rule here is phrased against "ground").
- Synthesized bridge decks over uncovered trenches (known limit above).
- Tunnel lighting/props, dedicated tunnel road types, tolls.
- Vehicles are unaffected by grade (M8 limit, still true).
