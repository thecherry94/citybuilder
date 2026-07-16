# Roadmap — the marathon toward CS2 gameplay

Each milestone follows the same loop: spec → plan → TDD implementation → visual/motion
verification → commit. One milestone at a time; every one must leave a playable,
verified build.

## Done

- **M1 — Road network & tools** (2026-07-12): bézier edges, validate/commit placement,
  auto-intersections, bulldoze with healing, 5 drawing modes, snapping/guidelines,
  lane graph, procedural rendering, screenshot harness. 4 road types incl. sidewalks
  and bike lanes; full intersection paint.
- **M2 — Intersection control & customization** (2026-07-13): per-junction control
  modes (priority/yield/stop/all-way/lights), per-leg roles, Node-Controller-style
  resizing, control paint + signs + light props, `RightOfWay` on the lane graph,
  Junction inspector UI.
- **M3 — Traffic simulation** (2026-07-13): A* routing with control-aware costs, IDM
  following, dynamic lane changes + turn-lane assignment, conflict-set junction
  arbitration, cycling signals, ambient + manual spawning, MultiMesh rendering,
  motion-continuity test harness.
- **M4 — Road-building UX** (2026-07-14): draft/gesture model with draggable handles
  (`RoadDraft` + `IDraftShape` + `DraftSession` replace the old click tools),
  candidate-scored snapping (node/edge/guideline/guide-intersection/grid
  point+line/perpendicular-with-arrival-constraint/parallel guides), tangent-locked
  curve starts everywhere plus a constant-radius arc mode with a live radius readout,
  geometry guards (per-type min segment length/radius, 25° junction floor, kink +
  sliver-crossing blocks), grid overlay + toolbar controls.
  Known limits (M5 candidates): no G1 lock when starting on multi-edge junction nodes,
  parallel guides require the Guides toggle on, no anchor-handle drag in chain mode,
  numpad-Enter doesn't confirm, radius readout doesn't turn red when too tight,
  an OnEdge binding within node-reuse distance of an edge end skips the node-leg
  sharp-angle check (sub-1° exposure only), and a free endpoint landing on an edge
  that the same proposal also crosses nearby commits with the endpoint absorbed up
  to one min-segment-length away from the ghost position (invariant holds, WYSIWYG
  dented in that corner).
- **M5 — Traffic depth** (2026-07-15): conflict-point arbitration (arc-distance conflict
  points per node, past-point clearance via a 0.5 m `ClearMargin` + `Vehicle.Length`),
  movement ranks + right-hand rule + a deadlock breaker for junction priority, impatience
  gap acceptance (2.8 s fresh, shrinking to a 2.2 s floor the longer a vehicle waits),
  straights flowing at road speed through junctions, One-Way and Asymmetric 2+1 road
  types, and standing safety + throughput regression guards.
  Known limits (M6+ candidates): protected left-turn phases, junction merging for short
  blocks, and signal-timing + lane-connector editing UI are still deferred. The safety
  guard is a two-fault test: either `ClearMargin` or `AcceptedGap` alone is enough to
  keep the sim collision-free, so a regression that weakens only one of those two
  parameters slips past the co-occupancy invariant and would only show up as a
  throughput/behavior-test regression instead — worth remembering before trusting the
  safety test alone to validate a future change to either constant. Merge-type conflict
  points sit at the connector's end, so a vehicle that has just exited a connector has a
  momentarily invisible tail to the conflict scan; rear-end separation enforced on the
  shared downstream lane covers the gap in practice, but it isn't covered by the
  conflict-point mechanism itself. The direction-aware signed-offset lane ordering
  (TrafficSim adjacency, ConnectorBuilder turn ranks) currently has no discriminating
  regression test — no catalog type distinguishes signed from |offset| ordering; an
  early M6 task should add a test-only lane profile (e.g. forward lanes at −4/+1)
  that does.
- **M6 — Quality & knowledge stack** (2026-07-16): scripted gesture fuzzer certifying the
  road-editor surface at 10k actions with an invariant + findings-triage protocol,
  versioned save/load (`SaveLoad`, byte-stable round-trip) wired to F5 quicksave / F9
  quickload, a KPI harness (signal discharge, 4-way yield, grid commute, perf) feeding
  `docs/health/M6.md` against a committed baseline, a living manual under `docs/manual/`
  (chapter-per-subsystem, drift-checked), and a standing quality-stack
  definition-of-done for every milestone from here on.
  Known limits (M7 candidates): `TryHealNode` has no post-merge floor recheck against the
  resulting type's `MinSegmentLength`/`MinRadius` (`RoadNetwork.cs`) — flagged
  `[UNCERTAIN]` in the manual, no concrete failing case found by reading alone;
  `TrafficSim.Sync` preserves vehicles on a same-`LaneId` lane whose run length shrinks on
  the same edit (e.g. a junction cut moving) without reclamping `S` against the new
  `LaneRun.Length` — removed-key lanes do drop their vehicles, this path doesn't;
  `SaveLoad.ValidateGame` bounds entity ids against their counters but never bounds the
  counters themselves, so a hand-crafted negative counter (e.g. `NextNode: -5`) passes
  validation and is assigned verbatim — latent, not fuzzer-observed; and
  `BezierOps.SelfIntersects` still false-positives at several near-straight angles
  (20/27/28/31/33/35/40/45°), a pre-existing gap from M4.
  Final-review finds (M7 backlog, none introduced in M6): **`TryHealNode` can silently
  reverse a one-way road** — it checks type equality but not direction continuity, and
  merged-edge orientation follows `HashSet` enumeration order (`RoadNetwork.cs:589-611`);
  bulldozing the third arm off a one-way chain can heal it backwards. Invariant-legal, so
  the fuzzer can't see it; `HealingTests` has zero OneWay coverage — top M7 bug. Also:
  fully-dropped commit segments leave permanent splits of pre-existing edges (EdgeId churn
  discards authored `JunctionConfig` overrides via `Prune`); node-collapse skips at
  `RoadNetwork.cs:436` bypass the `DroppedSegments` counter (silent lossy commit); fuzz
  round-trips never *edit* a restored network (post-load-edit seams untested).

## Next up (roughly in order — each is one milestone)

1. **Editing comfort: undo/redo + upgrade tool (M7).** Invertible `NetworkDelta`s (the
   batching already exists), upgrade-in-place (change a road's type without redrawing,
   preserving junction configs). CS2's most-loved road UX. Small, self-contained, huge
   daily payoff.
   *M7-adjacent fast-follow*: a KPI-driven tuning pass — signal discharge/saturation
   headway, curvature-based turn speeds, and creep at yield lines — using the M6 KPI
   harness to validate before/after instead of eyeballing.
2. **Elevation & bridges.** The domain carries Y everywhere but is flat: gradient
   limits, ramps, pillars, over/underpasses (crossing rule changes: grade-separated
   crossings don't create junctions), retaining-wall/bridge meshes. Big win for network
   expressiveness; prerequisite for highways.
3. **Zoning & buildings.** Zone strips along edges (the offset machinery generalizes),
   demand-free procedural growth first: lots, simple building shells, despawn on
   bulldoze. Purely visual city-ness; no economy yet.
4. **Citizens & destinations.** Trips get meaning: buildings spawn/attract vehicle
   trips (home→work), pedestrians walk the sidewalk graph (it's already a lane kind!),
   crosswalk usage ties into signals. This is where the sim starts feeling like CS.
5. **Public transport (first line).** Bus stops on sidewalk lanes, a line editor,
   buses as vehicles with stop dwell — exercises everything above.
6. **Economy & services (long game).** Demand model driving zoning growth, service
   buildings with coverage (fire/police/garbage as vehicle trips), districts/policies.
   Only start when 1–5 feel solid.

## Standing constraints

- Hundreds of vehicles today, **thousands someday**: keep the sim allocation-free and
  data-oriented; batching/jobs are an optimization later, not a rewrite.
- Save/load shipped in M6 (`SaveLoad`, versioned JSON, byte-stable round-trip, F5/F9
  in-game) — extend `SaveGame` with a version bump whenever new persistent state appears
  (vehicles/trips are not yet saved; ambient traffic respawns after load).
- Protected left-turn phases, junction merging, and lane-connector/signal-timing editing
  UI are deferred, not forgotten (movement-level priorities landed in M5).
- **Quality-stack definition-of-done, standing from M6 on**: every milestone from M6
  onward ships fuzz suite green (extended when editor surface or invariants grew),
  regenerated KPI baseline + `docs/health/M<N>.md`, drift-updated manual chapters, and a
  current roadmap — see golden rule 2 and `docs/verification.md`.
