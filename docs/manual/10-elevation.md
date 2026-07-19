# Chapter 10 — Elevation & structures

Elevation in citybuilder is **geometry, not a feature flag**. Every curve and node has
always carried Y; M8 gave that Y meaning: roads climb within per-type gradient limits,
an XZ crossing with enough vertical separation is *not a junction at all*, and the
renderer derives bridge decks, pillars, and embankments from nothing but height above
the ground plane. There is no bridge entity, no stored structure kind, and no save
format change — an M7.5 save loads unchanged because elevation was always serialized;
it just never varied.

The standing assumption: **the world is flat — ground is the Y=0 plane.** Every rule in
this chapter is phrased against "ground" so a future terrain milestone can substitute
`terrain(x, z)` without re-litigating the semantics. Negative elevation (trenches,
tunnels) is M8.5: the domain is signed already; only the editor clamp and the missing
underground rendering hold it back.

## At a glance

- **Sources:** `src/Domain/Geometry/VerticalRules.cs` (the classifier — the one source
  of vertical truth), vertical constants in `GeoConstants.cs`, `RoadType.MaxGradient`
  (`Catalog/RoadType.cs`), rule hooks across `RoadNetwork.cs` /
  `NetworkInvariants.cs` / `RoundaboutPlanner.cs`, authoring in
  `Tools/Draft/DraftSession.cs`; rendering in `src/Game/StructureView.cs`.
- **Key types:** `CrossingKind { Junction, GradeSeparated, VerticalClash }`;
  `VerticalRules.ClassifyCrossing(yNew, yExisting)`; `VerticalRules.MaxGradient(curve)`
  (max sampled |dY/ds|, resolution scaled to arc length like `BezierOps.MinRadius`);
  `PlacementError.TooSteep` / `PlacementError.VerticalClash`;
  `DraftSession.CurrentElevation`.
- **Used by:** Validate + Commit + the commit-side segment recheck + the crossing
  invariant (all four through the SAME classifier); the roundabout planner; the fuzzer;
  `StructureView`/`VisualShots` on the game side.
- **Last verified against commit:** M8, 2026-07-19.

## The three-band rule

Where two curves cross in XZ (remember: `BezierOps.Intersections` is XZ-projected,
ch. 01), the vertical separation `dy` at the crossing point decides everything:

| band | meaning | behavior |
|---|---|---|
| `dy < 0.6 m` (`JunctionYTolerance`) | coplanar | a junction, exactly as pre-M8: split, shared node, arbitration |
| `0.6 ≤ dy < 4.7 m` | too close to clear, too far to join | **illegal**: `VerticalClash` at Validate, dropped at Commit, flagged by the invariant |
| `dy ≥ 4.7 m` (`MinClearance`) | grade-separated | **not a crossing in any sense**: no split, no junction, no ghost marker |

`VerticalRules.ClassifyCrossing` is the only place these thresholds live. It is called
from: `Validate`'s crossing loop (before any junction math), `CommitCurve`'s live
crossing loop (grade-separated hits skipped before splitting; clash drops the curve —
the standing live-divergence policy), `SegmentCrossesLiveEdgeOffNode` (the commit-side
corruption recheck), `NetworkInvariants.CheckEdgeCrossings` (the post-state auditor),
and `RingObstructed` (a bystander crossing a planned ring with full clearance doesn't
obstruct). **If you add a new crossing-aware code path, call the classifier — never
re-derive the thresholds.**

One subtlety the M8 implementation tripped on and fixed: because `Intersections` is
XZ-projected, any "are these two hits the same point" coincidence check must measure
**XZ distance** and feed the Y difference to the classifier. A 3D coincidence check
silently discards every vertically-separated pair — including the illegal clash band.

## Gradients

`RoadType.MaxGradient`: Street/OneWay **10%**, TwoLane/Asymmetric **8%**,
FourLane/Avenue **6%**. Enforced at four altitudes, mirroring the length/radius floor
discipline of ch. 02:

1. **Validate** — `TooSteep` on any proposed curve above its type limit (+0.001 slack).
2. **Commit floor guard** — a stop relocated by reuse absorption onto a node at a
   different Y drags the displacement-blended segment steeper than the proposal ever
   was; gradient joined length/radius in the drop-don't-commit recheck (found by the
   fuzzer at seed 303@241 within ~300 actions of elevation entering the alphabet).
3. **`TryHealNode`** — a composite fit over a crest can exceed the limit; the merge is
   refused (node kept), like the merge-tolerance gate.
4. **`NetworkInvariants.CheckEdgeGeometry`** — committed edges audited at +0.005 slack.

## Authoring

`DraftSession.CurrentElevation` (clamped [0, `MaxElevation`] = 50 m) applies to **free**
draft endpoints; endpoints snapped `AtNode`/`OnEdge` **adopt the target's Y** — so
drawing away from a ground road at elevation 8 produces a ramp from 0 to 8
automatically. The lift happens in exactly one place (`ApplyElevation`, called on every
proposal the session validates or commits): endpoint Ys resolved, control-point Y
linearly interpolated (P1 at 1/3, P2 at 2/3), so the gradient is uniform along the
curve and ghost validation always sees the same lifted geometry the commit will.

Game-side: PgUp/PgDn steps ±5 m, Ctrl+PgUp/PgDn ±1 m (`ToolController.StepElevation`);
the readout shows `⬆ Nm` and the live ghost gradient as a percentage. Elevation
persists across gestures until changed (CS2 behavior). Junction coplanarity is enforced
at Validate (`BindingElevationClash`): an endpoint binding to a node/edge must arrive
within `JunctionYTolerance` of its Y — the snap-adoption path makes the common case
never trip it.

**Stacked nodes are normal.** `FindNodeNear`/`FindClosestEdge` measure 3D distance, so
an endpoint 8 m above a ground node creates a *separate* node at the same XZ — that is
precisely what a grade-separated crossing looks like in the graph.

## Roundabouts at elevation

Conversion planes the ring at the center node's Y (approach legs are coplanar there by
the junction rule). Two ramp-specific rules in `RoundaboutPlanner`, both fuzzer-taught:
the trim circle is an **XZ** circle at the ring plane, and a ramping leg — which meets
that circle *above or below* the plane — is **re-profiled** linearly from its outer end
down onto the plane (pinning only the endpoint kinks the tail: 10.2% on an 8% type was
committed before the fix). If even the uniform descent exceeds the leg's gradient, the
conversion refuses with `RoundaboutError.LegTooSteep`. `RingObstructed` classifies
crossings vertically, so a ring can legally pass over or under unrelated roads.

## Traffic

**Zero sim changes** — the M7.5 pattern held again. A grade-separated crossing has no
junction, so there is nothing to arbitrate; lanes and connectors sample 3D curves, so
vehicles climb ramps and `ArcLength` (3D) makes distances along-slope automatically.
Vehicle *pitch* was also free: `TrafficSim.Pose` returns the 3D tangent and
`TrafficView` never flattened it. Grade does not affect speed in M8 (deferred, noted in
the roadmap). The KPI face of the feature: `gradesep` runs identical saturating demand
over two crossing arterials — the at-grade junction completes **146** trips (its queues
back up to the spawn points and refuse entries), the bridged variant **294**.

## Rendering — derived structures

`StructureView` mirrors `RoadNetworkView`'s dirty-edge flow (same `NetworkDelta`
sets). Per edge, the curve is sampled every ~4 m and each span classified by height:

- `≤ 0.05 m` — ground, no structure;
- `≤ EmbankmentMax` (1 m) — **embankment**: earth-material side skirts from deck edge
  down to ground;
- above — **bridge**: concrete girder fascia (1.2 m deep) along both deck edges, plus
  square pillars every 24 m of arc where clearance ≥ 2 m.

Everything re-derives from the curve — structures can never go stale against the graph.
The gallery's `bridge` scenario (`VisualShots`) captures top/oblique/low shots; the low
shot is the reference for fascia + pillars. Note for harnesses that bind a
`StructureView` to a pre-built network: call `RebuildAll()` — no deltas will arrive.

## Invariants

- No edge exceeds its type's `MaxGradient` (+0.005 slack).
- Off-node XZ intersections are legal **iff** grade-separated (`dy ≥ MinClearance`);
  the coplanar case must be a shared node (as ever), the clash band never.
- Junction legs are coplanar with their node (within `JunctionYTolerance`).
- All pre-M8 invariants unchanged; `CheckLegAngles` remains an XZ-planar rule.

## Known limits

- **Editor-clamped to [0, +50 m]** — negative elevation is M8.5 (trenches/tunnels).
- **No vertical retrofit**: elevation is set while drafting; changing a committed
  road's height means redraw (CS2-without-mods behavior).
- **Grade doesn't affect vehicle speed** (deferred; would live in the IDM inputs).
- **Pillars ignore what's beneath them** — purely visual, they can stand in a ground
  road's carriageway under the deck. Cosmetic; a placement-aware pillar pass is
  M8.5-adjacent polish.
- **`MinRadius` stays XZ-projected** — at ≤10% grades the 3D-vs-XZ arc difference is
  <0.5%, far inside the floors' 0.1 m slack.

## How to verify

- `dotnet test`: `VerticalRulesTests` (classifier bands, gradient sampling),
  `ElevationValidationTests` (TooSteep, three bands, coplanar bindings),
  `ElevationCommitTests` (bridge commits create no junction; stacked nodes),
  `ElevationNetworkTests` (heal profile, elevated/ramped roundabouts, clearance-aware
  obstruction), `ElevationTrafficTests` (no arbitration over a bridge; burst safety),
  `DraftElevationTests` (session lift + snap adoption), invariant cases in
  `NetworkInvariantsTests`, and the seed-303 gradient pin in `FuzzRegressionTests`.
- **Fuzzer**: elevation steps are in the draw alphabet (70% ground / 30% stepped);
  every action audits the gradient + clearance invariants. Certified 3×10k.
- `CITYBUILDER_SMOKE=1 godot --headless .` — builds a live deck over a road via the
  real controller/session path, proves no junction formed, meshes structures.
- `CITYBUILDER_SHOTS` gallery — `bridge_top/oblique/low.png`; read the low shot for
  pillars/fascia. KPI: `docs/health/M8.md` (`gradesep.*`).
