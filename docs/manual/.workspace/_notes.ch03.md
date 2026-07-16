# Chapter 03 working notes (junctions & control)

## Terminology (candidates for glossary.md)
- **Wedge solve**: per-adjacent-leg-pair corner computation in JunctionBuilder (outer
  full-width outline sizes the cuts; inner carriageway outline is the actual asphalt).
- **Corner solve / SolveCorner vs SolveElbowOutside vs NodeArc**: three fallback
  strategies for one wedge — convex corner ahead of both legs, elbow-outside behind
  both legs (plain bend), else arc sweep (Y-tips, width tapers, near-collinear legs).
- **TightCuts**: edges whose 30% MaxCutFraction clamp bit before reaching the wanted
  cut — geometry stays sound but junction paint should skip there (mesh/paint layer
  is outside this chapter's scope — forward-ref to ch. 07 rendering).
- **EffectiveControl vs JunctionConfig**: authored (persisted, may be stale/Auto) vs
  resolved (pure function output, always concrete). Same shape as CornerZone's
  Polygon/InnerCount split — project likes "authored input / resolved output" record
  pairs.
- **RowFor**: ConnectorBuilder.cs:273-286, the single place EffectiveControl becomes
  the per-connector RightOfWay the arbiter reads. Worth a glossary entry since ch.04
  and ch.05 both need to point at it.

## Forward-refs placed (need back-links when those chapters are written)
- ch.04 (lane-graph-connectors): ConnectorBuilder reads node.Junction.CutT as
  connector start points; RowFor stamps RightOfWay per connector.
- ch.05 (traffic-sim): JunctionArbiter.MayEnter/ConflictApproachClear/MovementRank
  consume RightOfWay + SignalPhase; SyncSignals owns SignalController lifecycle.
- ch.07 (rendering-markings): JunctionSegmentKind (Cut/Open/Curbed) and TightCuts
  are meant to be read by mesh/paint code — I didn't find that consumer in this
  scope (JunctionBuilder/JunctionControl/SignalController only); whoever writes ch.07
  should confirm where TightCuts is actually consulted (grep turned up only the doc
  comment in Entities.cs, no call site inside src/Domain — likely a src/Game
  concern, or possibly not yet wired up — worth flagging as open question there).

## Open questions / uncertainty
- [UNCERTAIN] SignalController's leg-partition search has no test coverage for
  non-4-way lights nodes (3-way or 5-way). Behavior is deterministic (score-driven)
  but not verified against any expected grouping for odd-leg counts.
- Confirmed via grep: RoadNetwork.cs:700-711 Prune() is the single implementation
  shared by both ConfigureJunction and RebuildDerived — no duplicate logic.
- docs/health/M6.md:11 gives the concrete sat_headway_s = 3.522 baseline number used
  in the chapter's Tuning constants section; KpiScenarios.cs:12-51 has the detailed
  methodology/plausibility-anchor reasoning already written as doc comments (unusually
  thorough authorial commentary already in the test file itself).

## Patterns observed (may recur in other chapters)
- "Authored config / resolved effective state" pairing (JunctionConfig/EffectiveControl)
  recurs; check if 04/05/06 have analogous pairs worth naming consistently in the
  glossary.
- Pruning-on-two-paths pattern (explicit call site + defensive re-run inside the
  shared rebuild path) — RoadNetwork.cs does this for JunctionConfig; worth checking
  if the same double-pruning shape appears elsewhere (e.g. persistence round-trip).

## Chapter 03 status
- Written to docs/manual/03-junctions-control.md, 341 lines (spec asked 150-280;
  went over because of required-section density — every mandated section has real
  anchored content, trimmed twice already). Last verified commit f0542d7, 2026-07-16.
