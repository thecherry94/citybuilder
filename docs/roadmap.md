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
- **M6.5 — Traffic feel tuning** (2026-07-17): the KPI-driven tuning pass that was
  M7's fast-follow, promoted to its own mini-milestone. IDM+ min-form car following
  (Schakel et al.), speed-dependent launch acceleration (VISSIM CC8-style, 3.5 m/s²
  fading to 2.6 by 5 m/s, sign-guarded to never amplify braking), anticipatory
  spillback at junction exits (`SpillbackAnticipationSec = 0.7 s` — the root-cause fix
  for the M6 discharge stall: followers no longer dead-stop behind a leader already
  accelerating out of the clearance window), per-driver personalities
  (`Vehicle.Profile`: desired speed 0.85-1.2×, gap-acceptance floors 2.6/2.2/1.8 s),
  and curvature-based turn speeds (`√(2.2·Rmin)` clamped [4, straight], U-turn ≤ 6).
  Headline numbers (M6 origin → M6.5, `docs/health/M6.5.md`):

  | metric | M6 | M6.5 |
  |---|---|---|
  | signal.sat_headway_s | 3.522 | **2.036** (target ≤ 2.4) |
  | signal.startup_lost_s | 1.333 | 1.183 (in [1,4]) |
  | grid.delay_index | 2.734 | 2.482 |
  | grid.stops_per_trip | 1.367 | 1.644 (≤ 1.71 guard — realism cost of slow tight corners) |
  | yield4.completed | 18 | 21 (guard floor raised 13 → 18) |
  | diag.penetration_clamps | 0 | 0 |

  Certified: 3 × 10k-action fuzz clean, 267/267 tests, all visual/smoke/UI harnesses.
  Known limits: leader-start anticipation (both gap-credit variants) is a measured
  no-op under IDM+ launch dynamics — do not reintroduce without new evidence; the
  A/B reasoning survives in `tests/Domain.Tests/Traffic/QueueDischargeTests.cs`
  docstrings. stops_per_trip sits 0.07 under its absolute guard — the next tuning
  pass should watch it first. `EnforceNoPenetration`/`SimInvariants.AllQueues` never
  check the connector→exit-lane seam that spillback anticipation newly populates
  (IDM-guarded only, clamp lands one tick late there) — add a cross-seam invariant
  check next milestone.

- **M6.75 — Road-building feel** (2026-07-17): the CS2-feel editor pass, driven by two
  web-research rounds (docs/community + decompiled-`Game.dll` snippets via the modding
  ecosystem). Hard node capture (`max(0.6·radius, 3 m)` ring beats every soft candidate
  — the T-junction "slides along the leg" fix) with hysteresis (1.4× release ring via
  `SnapContext.HeldNode`; kills CS2's documented candidate-fighting), 8 m cell-length
  ticks (`SnapTypes.CellLength`, composes with the always-exact 15° angle snap — CS2's
  lateral-band weakness deliberately not cloned), perpendicular guides off every node
  leg (+ 48-nearest cap), per-kind snap indicators (node lock ring, edge tick, perp
  glyph, angle badge, cell ticks, guide-crossing dots), pooled ghost rendering
  (~543 → ~113 µs per render, `CITYBUILDER_GHOSTPROBE=1`), and the project's first
  audio (five synthesized one-shots via `tools/sfxgen`, `DraftSession`
  `HandlePlaced/Committed/Rejected` events, rate-limited snap ticks). Shipped on the
  Godot 4.7 migration. Found-and-fixed: the zoom-scaled capture ring could shrink
  below cell-tick miss distances, letting a node's own continuation guide steal the
  snap and commit a disconnected duplicate node (smoke caught it; 3 m absolute floor +
  regression test). Known limits: perpendicular-arrival snap (weight 2.2) practically
  never beats an edge candidate under the cursor — reachable only with Edges toggled
  off; guide-crossing dots render only for *active* guides (those near the current
  snap), not every collectable pair on screen; snap indicators have no screenshot
  coverage at extreme zoom (gallery shots are mid-zoom only).

- **M7 — Undo/redo + upgrade-in-place** (2026-07-17): CS2's most-loved editing
  comforts. **Snapshot undo** (`UndoStack` on the byte-stable `SaveLoad`, capacity 50,
  version-deduped pre-mutation checkpoints; Ctrl+Z/Y + toolbar; restore reruns the
  quickload resync) — chosen over the roadmap's sketched invertible deltas: the
  snapshot path was already fuzz-certified while delta inversion lives exactly where
  the fuzzer keeps finding bugs; perf guard pins checkpoint+undo < 100 ms at 480
  edges. **Upgrade tool** (`ToolMode.Upgrade`): LMB retypes to the current toolbar
  type, RMB flips travel direction — both as same-`EdgeId` in-place replacements
  (`RetypeEdge` with `TooShort/TooTight` validation against the existing curve,
  `FlipEdge`), preserving `EdgeId`-keyed junction configs; `NetworkDelta.EdgesChanged`
  re-meshes them. **Top-backlog bug fixed**: `TryHealNode` now orders its pair by
  `EdgeId` and heals direction-asymmetric types only with continuous flow
  (upstream-first merge) — one-way chains can no longer heal reversed. Fuzzer gained
  checkpoint-before-mutation + retype/flip/undo-redo actions, turning the M6
  "post-load-edit seams untested" gap into standing coverage.
  Known limits: undo covers the network only (vehicles resync as after quickload —
  ambient traffic respawns); the healed-edge floor recheck gap remains (pre-M7,
  see ch02); `Prune`-on-EdgeId-churn config loss for *fully-dropped* commit segments
  and `SaveLoad.ValidateGame` counter bounds stay deferred.

- **M7.5 — Roundabouts** (2026-07-17): CS2's convert-in-place workflow, replacing the
  CS1 hand-built ring (which trips the sliver/sharp-leg guards when an approach lands on a
  ring segment). **Convert any junction** (degree ≥ 3) **to a live roundabout**: a pure
  `RoundaboutPlanner` computes a CCW one-way ring (built from the existing `OneWay` type,
  arcs sliced directly from angle spans so >90° gaps split cleanly), and a network-owned
  registry (`RoadNetwork.Roundabouts.cs`) trims each leg in place (same `EdgeId`), wires the
  ring, and deletes the center — all in one batch. **Zero traffic-sim changes**:
  yield-on-entry is *derived* in `RebuildDerived` (ring legs `Main`, approaches `Yield` under
  `PrioritySigns`), so M2 control + M5 arbitration give circulating priority for free
  (`RoundaboutTrafficTests`: entry yields, ring is drivable + collision-free). **Live entity**:
  radius editable (lossless re-trim from captured `LegFullCurves`), bulldozing an approach
  re-arcs the ring, dropping below 3 approaches auto-dissolves. Save format **v2** (byte-stable
  round-trip; v1 loads unchanged). Editor: convert/radius/remove in the junction inspector,
  undo-checkpointed. Certified: **3 × 10k-action fuzz clean** (extended with
  convert/adjust-radius/remove actions), KPI `roundabout.*` baseline + `docs/health/M7.5.md`,
  new manual [ch. 09](manual/09-roundabouts.md), smoke + UITEST green.
  The fuzzer drove out the robustness model — **roundabouts are immutable except via bulldoze
  and the roundabout API**: ring/approach edges reject retype/flip/split/cross, ring nodes
  reject config edits, heal won't merge onto a ring node.
  **Hardening pass (2026-07-18)** after an adversarial review (10 verified findings): the
  original conversion bypassed `Validate` and no invariant checked edge crossings, so fuzz
  was structurally blind to rings stamped across bystander roads. Shipped: a **no-crossing
  invariant** (`CheckEdgeCrossings`, audited on every fuzz action — it immediately exposed
  and then policed the fixes), an **`Obstructed`** conversion gate, **first-crossing trim**
  (a committable hook leg could pierce the ring), **slots at the leg's actual circle
  crossing** (curved legs drifted 1.8–3.9 m off their ring nodes), **commit-side ownership
  guards** (Validate-snapshot-vs-Commit-live bypass), **flip capture** (flips survived
  regeneration), save-format **membership uniqueness**, panel **selection successor** +
  feasibility-clamped spinners (`MinFeasibleRadius` finally wired), a **ring-edge bulldoze
  guard**, and hover hot-path de-duplication. The crossing invariant then went on to expose
  **two pre-existing commit-path bugs unrelated to roundabouts** deep in dense fuzz runs
  (seeds 101@8321, 202@8673): reuse-absorption displacement re-crossing the absorbed edge,
  and an endpoint bound to a node while a *non-incident* edge passed within Validate's
  0.5 m endpoint exemption — both now dropped by a commit-side segment recheck
  (`SegmentCrossesLiveEdgeOffNode`, joining the floors + sharp-leg recheck family;
  seed-pinned in `FuzzRegressionTests`). Re-certified: 3×10k fuzz with the crossing rule
  live, full suite, smoke, UITEST, KPI.
  **Approach-crossing fix (2026-07-18, user find at M8 kickoff):** the v1 lock was
  over-broad — it refused roads crossing an *approach* anywhere along its length. Now only
  the ring itself (ring edges/nodes + the absorption zone at the ring end) is locked;
  crossing an approach splits it like any road, with the captured leg curve re-keyed onto
  the inner child (`OnApproachSplit`) so radius edits stay lossless even for curved
  approaches. Regression-pinned; fuzz now exercises approach splits organically.
  Known limits (deferred): **attaching a new leg directly to the ring** (refused via
  `PlacementError.TouchesRoundabout` rather than corrupting the ring; add legs by building
  the road first and reconverting, or bulldoze to re-arc); dedicated multi-lane / turbo ring
  cross-sections; in-flight vehicles not preserved across conversion (resync as after
  quickload); a dissolved roundabout leaves adjacent degree-2 bend nodes unhealed (cosmetic,
  heals on next edit).

- **M8 — Elevation & bridges** (2026-07-19): signed Y gains meaning; the flat world
  assumption is now explicit (ground = Y0 plane; rules phrased against "ground" so
  terrain can slot in later). **The three-band crossing rule** (`VerticalRules`, ONE
  classifier for Validate/Commit/segment-recheck/invariant/roundabout-obstruction):
  coplanar within 0.6 m → junction as before; ≥ 4.7 m clearance → **grade-separated, no
  junction at all**; between → `VerticalClash`, never legal. **Per-type gradients**
  (10/8/6% by class) enforced at four altitudes — Validate `TooSteep`, the commit-side
  relocation drop guard (fuzz find 303@241: absorption onto a different-Y node steepened
  blended segments), `TryHealNode` refusal, invariant audit. **CS2-style authoring**:
  PgUp/PgDn ±5 m (Ctrl ±1 m) on `DraftSession.CurrentElevation`; snapped ends adopt the
  target's Y so ramps fall out of drawing away from a ground road; elevation+gradient
  readout. **Zero traffic-sim changes** (third milestone running): no junction → nothing
  to arbitrate; 3D lanes/tangents gave climbing and vehicle pitch for free. **Derived
  structures** (`StructureView`): embankment skirts ≤ 1 m, girder fascia + pillars
  (24 m/≥2 m clearance) above — no stored bridge state, gallery `bridge_*` shots as
  evidence. Roundabouts convert at elevation; ramping legs re-profile onto the ring
  plane or refuse (`LegTooSteep`, fuzzer-taught). KPI headline (`gradesep`): identical
  demand over crossing arterials — **294 trips bridged vs 146 at-grade** (the junction
  spawn-starves half the traffic). Certified: 3×10k fuzz with elevation in the alphabet,
  full suite, smoke bridge scenario, UITEST elevated draw, manual ch10 + drift.
  The 10k certification depth caught a fourth gradient-floor gap — `RetypeEdge`
  accepted an 8% ramp onto a 6% type (303@3987, now `RetypeError.TooSteep`, pinned).
  Known limits: editor-clamped ≥ 0 (M8.5 unlocks trenches/tunnels); no vertical
  retrofit of committed roads (redraw, CS2-like); grade doesn't affect vehicle speed;
  pillars are visual only (can stand in an underpass carriageway — cosmetic);
  **fuzz wall-clock grew ~4× with elevation in the alphabet** (10k×3 ≈ 45 min — still
  inside the "minutes not hours" gate, but a profiling pass is queued for M8.5; a
  Y-band prefilter already exempts level decks from per-action re-intersection).

- **Post-M8 — Elevated-building feel pass** (2026-07-20): play-testing M8 surfaced two
  UX misses. **Gradient caps retuned** from realistic 10/8/6% to CS2-style game-feel
  **20/15/12%** (Street/OneWay, TwoLane/Asymmetric, FourLane/Avenue) — at 6% a +6 m
  bridge needed a 100 m approach, which played as "the game won't let me build a
  bridge"; all four enforcement altitudes follow the catalog value automatically.
  **Elevated ghost now shows height, not just validity**: the exact structures a commit
  would produce (pillars/fascia/skirts via the shared `StructureView.BuildStructures`,
  now public and curve-based), a ground-footprint shadow (curve flattened to Y0,
  `Materials.GhostShadow`), and pooled `⬆ N m` badges at elevated endpoints — all on
  the existing placement-changed dirty flag. Gallery `elevated_ghost_{valid,steep}`
  shots as evidence. Spec/plan: `2026-07-20-elevated-ghost-feedback*`.
  The retune immediately paid for the fuzz discipline: pin 202@8700 caught
  `RingObstructed` skipping the re-profiled approach legs ("trimmed legs are
  sub-curves" predated M8's Y rewrite) — a 15% leg's re-profile can drop a crossing
  the original cleared by `MinClearance` into the clash band. Legs are now checked
  with the same vertical classifier as the ring arcs (deterministic pin:
  `ConversionRefusesWhenAReprofiledLegWouldClashWithABystander`).

- **M8.5 — Trenches & tunnels** (2026-07-20): the negative half of the vertical axis M8
  opened. No vertical rule changed — the three-band classifier, gradient caps, and
  heal/retype guards were signed since M8 — so the feature cost **one bit of stored
  state** and rendering. **`RoadEdge.Covered`** (explicit player choice of open cut vs
  tunnel, rejected depth-derivation during design): save **format v3** (v1/v2 load
  uncovered), propagation invariants (split children inherit, heal keeps it iff both
  agree, retype/flip/roundabout-retrim preserve — `CoveredFlagTests`). **Editor unlock**
  to `[−50, +50]` m with signed `⬇` readout + ghost badges. **Derived below-ground
  structures** (`StructureView`): retaining-wall open cuts, tunnel spans, **portal faces
  where the covered deck crosses `PortalDepth` (3 m)** — never at curve ends, so split
  tunnels don't sprout mid-portals — and a translucent **cut-opening strip** (the flat
  ground plane has no holes). **X-ray view** (`U` toggle + auto while digging;
  translucent ground + dimmed surface). **Pillar placement awareness** (the M8
  underpass known-limit fix: pillars defer/skip out of carriageways). **XZ plan-view
  picking** (`FindClosestEdgeXZ`/`FindNodeNearXZ`) — the covered-toggle UITEST exposed
  that all tool picking measured 3D distance from the ground cursor, so *bridges had
  been unhoverable since M8*; now plan-view with a ground-nearest tie-break. Upgrade-tool
  "Covered (tunnel)" toggle + UITEST. **Zero traffic-sim changes** (fourth milestone
  running).
  **The queued profiling pass delivered a net speedup** despite adding negative
  elevation: Validate's two per-edge scans (crossing intersection + `OverlapsExisting`'s
  33×E `ClosestPoint`) had no spatial prefilter — adding the M8-invariant's convex-hull
  AABB + Y-band reject cut **`validate500_ms` 60→12 ms (5×)**, made grade-separated
  draws over a dense grid nearly free (new `perf.validate500_graded_ms` KPI, < 30 ms
  guard), and — with a sign-split alphabet that matches the pre-M8.5 off-ground
  frequency — brought **fuzz 1500×3 from 5m24s to 1m25s** (faster than pre-M8.5).
  Leak-free cached `RoadEdge.MinRadius`/`MaxGradient` (1.8× isolated invariant sweep);
  a crossing-memo explored during profiling was dropped (it pinned dead edges and
  regressed the dynamic loop). Certified: **3×10k fuzz** (signed elevation +
  `ToggleCovered` in the alphabet), full suite, KPI baseline + `docs/health/M8.5.md`,
  smoke tunnel+cover+v3-roundtrip, UITEST covered toggle, gallery
  `trench_*`/`tunnel_*`/`tunnel_xray_*`/`underpass_pillars_*`, manual
  [ch. 11](manual/11-trenches-tunnels.md).
  Known limits (deferred): a surface road crossing an **uncovered** deep trench visually
  spans the pit (no synthesized bridge deck — cover it, or wait for terrain); portal
  faces are evidence-grade flat quads; a covered edge dead-ending below ground shows no
  portal; the fuzz Commit/snap path (not the invariant or Validate) remains the loop's
  wall-clock floor — a deeper editor-commit optimization is the next perf lever if one
  is ever needed.

- **M8.75 — Observability harness** (2026-07-20): a tooling mini-milestone before
  zoning adds a new class of geometry — born from a web-research pass on LLM-agent
  game-dev practice ("the decisive capability is verification, not generation").
  **`GeometryDump`** (`src/Domain/Diagnostics/`): layered plan-view **SVG**
  (edges/junctions/lanes/conflicts/labels; lanes in travel order with arrowheads,
  conflict points as dots, elevation/covered/ids in labels; invariant-culture — the
  de-DE dev machine would otherwise emit `3,5`) and **JSON** (exact coordinates,
  junction polygons, roundabout membership) — wired into three call sites: any xUnit
  test, **fuzz failures** (auto-dump `fuzz-artifacts/seed<N>_action<K>.svg|.json`
  appended to the assert message), and **smoke** (`CITYBUILDER_SMOKE_DUMP=<dir>`).
  **Golden-image diffing** (`CITYBUILDER_SHOTS_GOLDEN=check|update`): 25 committed
  baselines over 9 static scenarios (incl. a new `roundabout` gallery scene — the
  gallery had none), in-harness `Godot.Image` compare (channel tolerance 8/255,
  0.5 % changed-pixel gate; ImageMagick dropped — not even installed here), failure
  path proven by running `check` under `CITYBUILDER_SHOTS_TINT=1` (every
  junction-bearing shot flagged, junction-free scenes stayed green, exit 1).
  Verified: 397 quick tests + default fuzz sweep green, smoke incl. dump, gallery
  137 shots + `GOLDEN OK 25`, UITEST. No KPI/manual-chapter changes (developer
  tooling; verification.md is the manual for it).
  Known limits: goldens are single-machine (GPU/driver change ⇒ intentional
  `update` + git-diff review); golden set excludes traffic scenarios by design;
  conflict-point SVG placement approximates t by arc-length ratio (debug-grade).

## Next up (roughly in order — each is one milestone)

1. **Zoning & buildings.** Zone strips along edges (the offset machinery generalizes),
   demand-free procedural growth first: lots, simple building shells, despawn on
   bulldoze. Purely visual city-ness; no economy yet.
2. **Citizens & destinations.** Trips get meaning: buildings spawn/attract vehicle
   trips (home→work), pedestrians walk the sidewalk graph (it's already a lane kind!),
   crosswalk usage ties into signals. This is where the sim starts feeling like CS.
3. **Public transport (first line).** Bus stops on sidewalk lanes, a line editor,
   buses as vehicles with stop dwell — exercises everything above.
4. **Economy & services (long game).** Demand model driving zoning growth, service
   buildings with coverage (fire/police/garbage as vehicle trips), districts/policies.
   Only start when 1–4 feel solid.

## Standing constraints

- Hundreds of vehicles today, **thousands someday**: keep the sim allocation-free and
  data-oriented; batching/jobs are an optimization later, not a rewrite.
- Save/load shipped in M6 (`SaveLoad`, versioned JSON, byte-stable round-trip, F5/F9
  in-game), now at **format v3** (M7.5 added roundabouts, M8.5 added `Covered`; v1/v2
  saves still load) — extend `SaveGame` with a version bump whenever new persistent state
  appears (vehicles/trips are not yet saved; ambient traffic respawns after load).
- Protected left-turn phases, junction merging, and lane-connector/signal-timing editing
  UI are deferred, not forgotten (movement-level priorities landed in M5).
- **Quality-stack definition-of-done, standing from M6 on**: every milestone from M6
  onward ships fuzz suite green (extended when editor surface or invariants grew),
  regenerated KPI baseline + `docs/health/M<N>.md`, drift-updated manual chapters, and a
  current roadmap — see golden rule 2 and `docs/verification.md`.
