# M8 — Elevation & bridges (design)

**Date:** 2026-07-19
**Status:** approved (derived-structure model; M8 = elevated, M8.5 = trenches + tunnels)
**Milestone:** M8, first of the two-part vertical pair (M8.5 covers negative elevation:
trenches with retaining walls, covered tunnels with portals, underground view).

## Problem

The domain carries Y everywhere — curves, nodes, saves — but the world is flat: every
XZ crossing becomes a junction, there is no way to author height, and nothing renders
above the ground plane. Elevation is the roadmap's #1 (prerequisite for highways) and
the biggest expressiveness jump available: bridges, ramps, and grade-separated
crossings where a road passes OVER another without a junction.

## Decisions locked with the user

- **Authoring:** CS2-style steps. PgUp/PgDn raises/lowers the active draft endpoint in
  5 m steps (Ctrl+PgUp/PgDn = 1 m); the road interpolates between endpoint elevations.
- **Vertical scope:** full ± including tunnels across M8+M8.5; **M8 ships elevated only**
  (editor clamps to [0, +50 m]); the domain accepts signed Y so M8.5 needs no format or
  domain break.
- **Phasing:** M8 bridges → M8.5 trenches/tunnels. Each lands playable + certified.

## Standing assumption (documented, not decided per-feature)

**The world is flat: ground is the Y=0 plane.** There is no terrain system; elevation is
structural height above ground. If terrain ever lands (own milestone), "ground" becomes
`terrain(x,z)` and every rule below is already phrased against a ground function.

## Architecture — derived structures over signed Y

No new entities. Elevation lives where it always has — `RoadNode.Position.Y` and the
curve control points — and everything else **derives**:

- **Structure kind per span** (renderer-side): sampled height above ground classifies
  each stretch of an edge — `< EmbankmentMax` (1 m) → embankment skirt, above → bridge
  deck with girder + pillars. No stored flags, no save change (v2 already round-trips Y).
- **Crossing semantics** (domain): XZ crossings classify by vertical separation at the
  crossing point. This is the semantic heart of M8.
- **Pillars** (renderer): auto-placed along bridge spans every ~24 m where clearance to
  ground ≥ 2 m. Purely visual in M8.

Rejected alternatives: explicit bridge entities (roundabout-style registry — more
control than M8 needs; revisit if custom pillar/style authoring ever matters) and
terrain-first (a milestone of its own).

## Domain rules

### Vertical constants (`GeoConstants` additions)

| Constant | Value | Meaning |
|---|---|---|
| `JunctionYTolerance` | 0.6 m | Crossings/legs within this ΔY are coplanar: junction. |
| `MinClearance` | 4.7 m | ΔY at an XZ crossing at or above this = grade-separated, legal, NO junction. |
| `EmbankmentMax` | 1.0 m | Render threshold: below = skirt, above = bridge deck + pillars. |
| `MaxElevation` | 50 m | Editor clamp (domain unclamped). |
| Elevation steps | 5 m / 1 m | PgUp/PgDn / Ctrl+PgUp/PgDn. |

### Gradient limits (per road type, new `RoadType.MaxGradient`)

Street/OneWay **10%**, TwoLane/Asymmetric **8%**, FourLane/Avenue **6%**. Enforced on
proposals as max sampled `|dY/ds|` along each curve → new `PlacementError.TooSteep`.
(Sampled like `MinRadius`, resolution scaled to arc length.)

### Crossing classification (Validate + Commit + invariant, all three)

For each XZ intersection between a proposed curve and an existing edge, with `dy` =
|Y_new − Y_existing| at the crossing point:

- `dy < JunctionYTolerance` → **junction**: exactly today's behavior (split, shared
  node). The split point's node takes the coplanar Y.
- `dy ≥ MinClearance` → **grade-separated**: NOT a crossing. No split, no junction, no
  error. The curves simply pass over/under each other.
- otherwise → **`PlacementError.VerticalClash`**: too close to clear, too far to join.

`CommitCurve`'s crossing loop applies the same classification against the live network
(grade-separated hits are skipped before splitting; clash → drop segment, the standing
"drop, never commit corrupt" policy). `SegmentCrossesLiveEdgeOffNode` and
`NetworkInvariants.CheckEdgeCrossings` get the identical clause: an off-node XZ
intersection is a violation only when `dy < MinClearance` (the coplanar case must be a
shared node, as today; the clash band is always a violation). One classification
helper, used by all call sites — no re-derived thresholds.

### Junctions, endpoints, and elevation

- **Junction coplanarity:** every leg must arrive at its node's Y within
  `JunctionYTolerance`; `Validate` refuses endpoint bindings that would land a leg at a
  node with `dy` beyond tolerance (`VerticalClash`), and drafting auto-adopts the
  target's Y when snapping to a node or edge (so the common path never trips it).
- **Node reuse is 3D already:** `FindNodeNear`/`FindClosestEdge` use 3D distance, so an
  elevated endpoint above a ground node creates a separate node — stacked nodes at the
  same XZ are legal and expected (that's what a grade-separated crossing looks like).
- **Junction geometry/lane connectors** build in the node's Y plane (they already use
  node-relative offsets; legs arrive coplanar by the rule above).
- **Heal:** `CurveFit` merge operates on 3D points already; healed curves keep their
  profile. The healed-edge gradient is rechecked like length/radius (same drop rule).
- **Roundabouts:** conversion planes the ring at the center node's Y; `RingObstructed`
  and the ownership crossing checks reuse the clearance classification (a ring may
  legally pass over/under unrelated roads). Approaches must meet the ring coplanar.

### Traffic

No sim changes expected (the M7.5 pattern): lanes/connectors sample 3D curves, so
vehicles get Y for free; `ArcLength` is 3D so distances/speeds are along-slope. Grade
does not affect speed in M8 (deferred with a note). Vehicle rendering gains pitch from
the curve tangent's Y.

## Editor UX

- **Elevation stepping:** PgUp/PgDn (±5 m) and Ctrl+PgUp/PgDn (±1 m) adjust the ACTIVE
  draft endpoint's elevation, clamped [0, MaxElevation]; persists as the "current
  elevation" for subsequent placements until reset (CS2 behavior). Esc/confirm keeps it.
- **Readout:** the draft readout shows the endpoint elevation (`+12 m`) and the segment
  gradient (`4.2%`), red when `TooSteep`/`VerticalClash` (the existing readout/flash
  channels).
- **Ghost:** rendered at true Y (the ghost mesh already follows the curve; verify
  z-fighting vs ground at 0 and add the elevation to the ghost's guide visuals).
- **Snapping:** all snap candidates remain XZ-planar; snapping to a node/edge adopts
  its Y (elevation control then applies relative adjustment for the free endpoint only).
- **Bulldoze/upgrade/junction tools:** unchanged (they operate on picked entities).

## Rendering (Game layer)

- **Deck:** the existing road mesh already extrudes along the 3D curve — verify and fix
  Y assumptions (markings, sidewalk offsets are curve-relative and should follow).
- **Structure meshes (new, derived per edge):** sampled spans classified by height:
  embankment skirt (vertical quad strip deck-edge→ground, earth material) below
  `EmbankmentMax`; girder skirt (fixed-depth fascia) + **pillars** (cylinders every
  ~24 m arc length, centered under the deck, where clearance ≥ 2 m) above it.
  Implemented as one `StructureView` node beside `RoadNetworkView`, driven by the same
  `NetworkDelta` re-mesh flow.
- **Junction surfaces** at node Y (verify `JunctionBuilder` polygons carry Y — they use
  node positions; the skirt around elevated junction decks reuses the girder fascia).
- **Vehicles:** pitch along tangent (`Basis` from 3D tangent instead of XZ-projected).
- Screenshot gallery gains a bridge shot; motion trails cover an over/under crossing.

## Quality stack (definition of done, M8)

1. **Unit:** gradient enforcement per type; crossing classification (all three bands);
   junction coplanarity refusals; grade-separated commit produces NO junction and the
   invariant passes; clash band refused at Validate AND dropped at commit; elevated
   roundabout conversion; heal across sloped edges; stacked nodes (same XZ, different Y).
2. **Invariant:** `CheckEdgeCrossings` clearance clause + a new `CheckGradients` rule
   (committed edges within type gradient, 0.1 slack pattern); `CheckLegAngles` stays
   XZ-planar (verify with sloped legs).
3. **Fuzzer:** draw actions gain random endpoint elevations (steps in [0, 50], biased
   toward 0 so ground networks stay dominant); invariants audit every action. 3×10k.
4. **Traffic:** behaviour test — vehicles traverse a bridge over a road with zero
   junction arbitration at the crossing; burst safety on a grade-separated network.
5. **KPI:** new `gradesep` scenario (two crossing arterials, bridged vs at-grade
   junction — delay_index contrast is the whole point of elevation); baseline + report.
6. **Harness:** smoke builds a bridge over an existing road, asserts no junction was
   created, vehicles flow both levels; UITEST steps elevation via the controller;
   screenshot gallery bridge shot verified by reading the image.
7. **Docs:** manual ch01/ch02/ch06/ch07 drift + new ch10 (elevation & structures),
   conventions (vertical constants), gotchas, roadmap. Save format unchanged (v2's Y
   round-trip already covers elevation — assert byte-stability with an elevated network).

## Non-goals (M8)

- Negative elevation, trenches, retaining walls, tunnels, portals, underground view
  (M8.5 — the editor clamp is the only thing holding this back; domain is signed).
- Terrain. Grade-affected vehicle speed. Custom pillar styles/placement. Elevated
  road types (catalog unchanged). Moving existing nodes vertically (elevation is set
  while drafting; retrofitting height = redraw, same as CS2 without mods).

## Open implementation questions (resolved in the plan)

- Whether `MinRadius`'s XZ projection needs a 3D-arc-length correction for steep
  curves (likely negligible at ≤10% grades; verify with a worked example).
- Ghost/guide rendering at elevation (grid overlay stays at ground — verify readability).
- Where the elevation step input lands in `ToolController` vs `DraftSession`
  (lean: `DraftSession` owns "current elevation" so the fuzzer can drive it directly).
