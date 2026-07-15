# Quality & Knowledge Stack — Design (Milestone 6)

**Date:** 2026-07-15
**Status:** Approved (user-set scope: gesture fuzzing over the real editor API,
versioned save/load proven by round-trip fuzzing, KPI harness + per-milestone health
report, living system manual, codified definition-of-done. Tuning is a fast-follow
driven by the KPI numbers, not part of this milestone. Undo/redo becomes M7.)

## Goal

Make quality measurable and knowledge durable as the project grows toward a sellable
game. A seeded fuzzer plays the editor like a chaotic user and proves the invariants
hold for *any* road configuration; save/load exists and is verified against thousands
of generated networks; every system exports a few numbers into a per-milestone health
report; a maintained manual keeps the current state of every subsystem readable
without re-deriving it from source; and the milestone checklist enforces that all of
this stays current.

## Non-goals

- No behavior tuning in this milestone (turn speeds, creep, signal timing): the KPI
  harness lands first so the follow-up tuning pass is measured, not guessed.
- No vehicle/traffic state in save v1 (ambient traffic respawns after load; manual
  trips are lost — documented in the manual's persistence chapter).
- No external CI, coverage services, or telemetry databases.
- No undo/redo (M7).

## 1. Network invariants (`Domain`, shared)

Extract the standing invariants into one reusable checker the fuzzer, tests, and
future tools all call:

`NetworkInvariants.Check(RoadNetwork n) : IReadOnlyList<string>` (empty = healthy)
covering: every edge ≥ its type's `MinSegmentLength` and ≥ its type's `MinRadius`;
no junction leg pair under `MinJunctionAngleDeg` (25°); junction geometry sane
(`CutT` within [0,1], corner zones non-degenerate); every arriving driving lane has
≥ 1 connector; straight-capacity rule (no approach sends more straight source lanes
into an arm than it has receiving driving lanes); `MarkingRules.Layout` offsets
within ±Width/2 for every type in use; per-node `ConnectorConflicts` symmetric.
A companion `SimInvariants.CheckBurst(RoadNetwork n, int seed, int ticks)` spawns a
small ambient population and ticks, asserting no exception, no penetration
(`EnforceNoPenetration` deltas), and no conflict-point co-occupancy.

## 2. Gesture fuzzer (`tests/Domain.Tests/Fuzzing/`)

A seeded driver over the **real editor surface** — `DraftSession` + `SnapEngine` +
`RoadNetwork` — so snapping, validation, and commit run exactly as for a player:

- Action set (weighted): draw straight / quad / cubic / arc / chain segment / grid
  stamp with random road type, random click points (mix of free ground, near-node,
  near-edge so snapping engages), random snap toggles; bulldoze a random edge;
  `ConfigureJunction` with random mode + leg roles; cancel/step-back mid-gesture.
  Invalid placements are expected outcomes (session refuses), never failures.
- After every action: `NetworkInvariants.Check` must return empty; every k-th action
  additionally runs `SimInvariants.CheckBurst` and the save/load round-trip (§3).
- Determinism: one `Random(seed)`; a failure reports the seed, action index, and a
  replayable tail of the last actions so any finding becomes a one-line regression
  test (`FuzzRegressionTests` collects them).
- Budget: default suite runs 3 seeds × 300 actions inside `dotnet test` (seconds,
  not minutes); an opt-in long run (env `CITYBUILDER_FUZZ_ACTIONS`) does the
  10k-action milestone certification.
- Expectation: the first sweeps WILL find real bugs. Fixing them is in-scope for
  this milestone; an architectural finding stops and reports rather than patching.

## 3. Persistence (`src/Domain/Persistence/`)

Versioned JSON via System.Text.Json (net8, domain-pure):

- `SaveGame v1`: format version, road-type catalog version guard (ids referenced,
  not embedded), nodes (id, position, `JunctionConfig`), edges (id, endpoints,
  curve control points, type id), id counters. Derived data (lanes, junctions,
  connectors) is rebuilt on load, never stored — but lane ids must be reproduced
  deterministically so external references stay valid: edges serialize their lane
  ids and load reassigns them verbatim.
- `SaveLoad.Save(RoadNetwork) : string` / `SaveLoad.Load(string) : RoadNetwork`;
  loading an unknown newer format version throws a typed error.
- Round-trip contract (fuzz-verified): `Save(Load(Save(n))) == Save(n)` byte-equal,
  and the loaded network is structurally identical (node/edge/lane ids, positions,
  curves within float round-trip, configs) with identical derived-data counts.
- Game layer: F5 quick-save / F9 quick-load to `user://saves/quick.json`, plus
  toolbar Save/Load buttons; loading swaps the network behind
  `RoadNetworkView`/`TrafficSim` via the existing `Changed`/`Sync` machinery
  (traffic despawns, ambient respawns). UI test exercises save → edit → load →
  assert the edit is gone.

## 4. KPI harness + health report (`tests/Domain.Tests/Kpi/`, `docs/health/`)

- Sim instrumentation (domain, allocation-light): per-vehicle trip stats — spawn
  time, arrival time, stop count (speed crossing below 0.5 m/s), so scenarios can
  compute delay and stops per trip. Free-flow reference = route length / limits.
- Scenarios (deterministic, seeded): `signal_discharge` (startup lost time, mean
  discharge headway of a 10-car queue), `yield_4way` (mean + p95 minor-road delay,
  completed trips), `grid_commute` (trip delay index = actual/free-flow, stops per
  trip on a 3×3 grid with ambient load). Perf budgets: `Validate` on a 500-edge
  network (ms), `Tick` at 300 vehicles (ms/tick) — generous bands to stay unflaky.
- Output: suite writes `docs/health/kpi-latest.json` and renders
  `docs/health/M<N>.md` (one table: metric, value, baseline, delta); asserts every
  metric within the committed baseline band (`kpi-baseline.json`). Milestones
  commit both the refreshed baseline and the report.
- Visual: a `speed_heatmap` shot scenario tints edges by average vehicle speed
  under load (debug view reuse), joining the screenshot harness.

## 5. Living manual (`docs/manual/`)

Bootstrapped this milestone with the dedicated manual-authoring skill; maintained by
drift updates every milestone. Chapters: 00 overview & reading guide, 01 geometry
(béziers, arc-length, curvature), 02 network & validation, 03 junctions & control,
04 lane graph & connectors (incl. capacity-aware turn lanes), 05 traffic sim
(routing, IDM, arbitration ranks, impatience), 06 drafting & snapping, 07 rendering
& markings, 08 persistence, glossary. Each chapter: purpose, key algorithms,
invariants, tuning constants with rationale, known limits, how to verify. The
manual is the canonical answer to "what is the current state of X" — for future
sessions and for humans.

## 6. Definition-of-done (CLAUDE.md + verification.md)

A milestone is done only when: tests + build + smoke + shots green; fuzz suite green
(and extended if the milestone added editor surface or invariants); KPI baseline
regenerated with the health report committed; affected manual chapters
drift-updated; roadmap current. Written into CLAUDE.md's golden rules (concise) with
the mechanics in docs/verification.md.

## Success criteria

- Certification fuzz run: ≥ 10 000 mixed actions across seeds, zero invariant
  violations, zero unhandled exceptions (after fixing what the sweeps find).
- Save→load→save byte-identical across the fuzz corpus; quick save/load works
  in-game (UI-test verified).
- `docs/health/M6.md` committed with all scenario KPIs + perf budgets and baselines.
- Manual covers all chapters; a fresh session can answer subsystem-state questions
  from manual + latest health report without reading source.
- CLAUDE.md/verification.md carry the definition-of-done.

## File plan

| Area | Change |
| --- | --- |
| `Domain/Network/NetworkInvariants.cs` | new shared checker |
| `Domain/Traffic` | trip stats instrumentation; `SimInvariants` burst checker |
| `Domain/Persistence/` | new: `SaveGame` schema, `SaveLoad` |
| `tests/Domain.Tests/Fuzzing/` | new: gesture fuzzer, regression collection |
| `tests/Domain.Tests/Kpi/` | new: scenarios, baseline, report writer |
| `Game/Main`/`Toolbar` | quick save/load, UI-test flow |
| `Game/VisualShots` | speed-heatmap scenario |
| `docs/manual/` | new book (skill-authored) |
| `docs/health/` | new: baseline JSON + M6 report |
| `CLAUDE.md`, `docs/verification.md` | definition-of-done |
