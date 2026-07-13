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

## Next up (roughly in order — each is one milestone)

1. **Editing comfort: undo/redo + upgrade tool.** Invertible `NetworkDelta`s (the
   batching already exists), upgrade-in-place (change a road's type without redrawing,
   preserving junction configs), maybe parallel-road drawing mode. CS2's most-loved
   road UX. Small, self-contained, huge daily payoff.
2. **Traffic depth pass.** Protected left phases + movement-level priorities (conflict
   sets already exist), junction merging for very short blocks, taper markings at
   type-change transitions, dropped curbs at crosswalks. Turns M3's known limits into
   features.
3. **Elevation & bridges.** The domain carries Y everywhere but is flat: gradient
   limits, ramps, pillars, over/underpasses (crossing rule changes: grade-separated
   crossings don't create junctions), retaining-wall/bridge meshes. Big win for network
   expressiveness; prerequisite for highways.
4. **Zoning & buildings.** Zone strips along edges (the offset machinery generalizes),
   demand-free procedural growth first: lots, simple building shells, despawn on
   bulldoze. Purely visual city-ness; no economy yet.
5. **Citizens & destinations.** Trips get meaning: buildings spawn/attract vehicle
   trips (home→work), pedestrians walk the sidewalk graph (it's already a lane kind!),
   crosswalk usage ties into signals. This is where the sim starts feeling like CS.
6. **Public transport (first line).** Bus stops on sidewalk lanes, a line editor,
   buses as vehicles with stop dwell — exercises everything above.
7. **Economy & services (long game).** Demand model driving zoning growth, service
   buildings with coverage (fire/police/garbage as vehicle trips), districts/policies.
   Only start when 1–6 feel solid.

## Standing constraints

- Hundreds of vehicles today, **thousands someday**: keep the sim allocation-free and
  data-oriented; batching/jobs are an optimization later, not a rewrite.
- Save/load doesn't exist yet — before the feature set grows much more (latest: before
  zoning), add serialization of `RoadNetwork` + `JunctionConfig` + catalog refs.
- Signal timing, movement priorities and lane-connector editing UI are deferred, not
  forgotten (traffic depth pass).
