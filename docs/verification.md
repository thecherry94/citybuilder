# Verification & debugging

Nothing is "done" until verified with the harness matching the change. Evidence before
claims, always.

## The gates

| Harness | Command | Verifies |
|---|---|---|
| Unit tests | `dotnet test` | domain logic, geometry, sim invariants |
| Build | `dotnet build citybuilder.sln` | game layer compiles |
| Smoke | `CITYBUILDER_SMOKE=1 godot --headless .` → `SMOKE OK` | end-to-end: tools → network → junction control → traffic arrivals |
| Screenshots | `CITYBUILDER_SHOTS=tests/visual/shots godot .` → `SHOTS OK N` | rendering, paint, props (needs a real window) |
| UI test | `CITYBUILDER_UITEST=<out.png> godot .` → `UITEST OK …` | scripted UI flow + full-UI screenshot |
| Golden diff | `CITYBUILDER_SHOTS=… CITYBUILDER_SHOTS_GOLDEN=check godot .` → `GOLDEN OK N` | silent render regressions vs committed baselines in `tests/visual/golden/` |

Screenshot harness extras:
- **Angle policy** (2026-07-20, elevated-junction find): high oblique/top shots hide
  whole defect classes — buried paint, paper-thin slabs, grazing-angle shredding all
  read fine from 35°+ overhead. A scene involving elevation or junctions should also
  shoot **driver height** (pitch ≈ −10°), a **close corner**, and **from below** the
  deck (positive pitch) — see the `elevated_tee_ramps` scenario for the pattern.
- `CITYBUILDER_SHOTS_TINT=1` — tint junction meshes to attribute overlapping surfaces.
- `CITYBUILDER_SHOTS_DUMP=<scenario>` — print flat quads at marking height (`MeshBuilders.MarkingY`):
  **ground truth for where paint actually is** — far more reliable than pixel-guessing.
- Traffic scenarios also emit `*_motion0..7.png` filmstrips (0.33 s apart).
- `CITYBUILDER_SHOTS_GOLDEN=check|update` (M8.75) — `check` compares the curated
  static golden set (`VisualShots.GoldenScenarios`, no traffic scenarios) against
  `tests/visual/golden/` in-harness (`Godot.Image` bytes, per-channel tolerance 8/255,
  fail when > 0.5 % of pixels change; report on stderr, exit 1). `update` regenerates
  the baselines — an intentional act, reviewed via the git diff of the PNGs.

## Geometry dumps (M8.75) — read these before pixel-guessing

`GeometryDump` (`src/Domain/Diagnostics/`) exports the **domain's** view of the network
— complementing `CITYBUILDER_SHOTS_DUMP`, which dumps what the *renderer* emitted; a
discrepancy between the two is itself diagnostic signal.

- `GeometryDump.Svg(network)` / `.SvgToFile(...)` — layered plan-view SVG
  (`edges`/`junctions`/`lanes`/`conflicts`/`labels` groups; svg x = world X, svg y =
  world Z; elevation + `covered` in labels; lanes drawn in travel order with
  arrowheads; conflict points as red dots). Read the coordinates as text instead of
  squinting at PNGs.
- `GeometryDump.Json(network)` / `.JsonToFile(...)` — the same data with exact
  coordinates (nodes incl. junction polygons, edges incl. sampled polylines + lanes,
  roundabout membership). Query it before writing a state-probe test.
- **Fuzz failures dump automatically**: a failing `FuzzSuiteTests` seed writes
  `fuzz-artifacts/seed<N>_action<K>.svg|.json` next to the test binaries and appends
  the paths to the failure message — the replayable action tail now ships with a picture.
- **Smoke end state**: `CITYBUILDER_SMOKE_DUMP=<dir>` alongside `CITYBUILDER_SMOKE=1`
  writes `smoke_network.svg|.json` and prints `SMOKE DUMP <path>`.
- Any test can call it directly (one-liner) for throwaway debugging — write to the
  scratchpad/temp dir and delete probes afterwards.

## Debug methodology (learned the hard way)

**Static visuals** — read the PNGs, crop/zoom with `magick`. Two traps:
1. Material camouflage (concrete vs grass): re-shoot with TINT before assuming mesh bugs.
2. Perception vs geometry (the "inner lane too narrow" saga): dump the rendered quads
   (`CITYBUILDER_SHOTS_DUMP`) and compare numbers before touching any math. The paint
   was correct twice; the real bugs were asphalt asymmetry and dash phasing.

**Motion / kinematics — screenshots cannot catch these.** Use, in order:
1. **Invariant tests** (`MotionContinuityTests`): per tick, no vehicle pose may move
   farther than `(speed + a·dt)·dt + lateral allowance`. This teleport detector found
   both junction-snap bugs. Write invariants (no collision, no teleport, deterministic
   under seed) rather than example asserts — they keep catching future bugs.
2. **Motion trails**: composite a scenario's filmstrip —
   `magick tests/visual/shots/<scenario>_motion*.png -evaluate-sequence max trails.png`
   Equally spaced car ghosts = smooth; gaps/clumps at boundaries = snapping; dense
   ghost ladders = queues. One readable still image encodes motion over time.
3. **State probes**: when an invariant fails at tick N, write a throwaway xunit test
   that replays (same seed = same everything) and dumps the vehicle's fields around
   tick N via `ITestOutputHelper`, run with `-l "console;verbosity=detailed"`. Delete
   the probe afterwards.

**UI** — `panel.Visible == true` proves nothing about *where* a control is: the junction
panel once rendered at x = [−304, −12], fully off-screen, with Visible true. The UITEST
screenshot is the only proof a control is actually on screen.

## Perf
`SpawnerTests.ThreeHundredVehiclesStayCheap` gates 300 vehicles × 1000 ticks < 2 s.
Keep the sim hot path allocation-free (per-lane `List<Vehicle>` reuse, precomputed
conflict sets, caches keyed by `RoadNetwork.Version`).

## Milestone-level gates: fuzzer + KPI

Beyond the per-change gates above, closing a milestone additionally requires the gesture
fuzzer green and a regenerated KPI health report (golden rule 2).

**Gesture fuzzer** (`tests/Domain.Tests/Fuzzing/`) drives `GestureFuzzer` over the real
editor surface (draw/bulldoze/ConfigureJunction gestures, snap toggles, mid-gesture
step-back/cancel), checking
`NetworkInvariants.Check` after every action, `SimInvariants.CheckBurst` every 25 actions,
and a `SaveLoad` round-trip (save → load → save, byte-equal) every 10 actions.
- Default sweep, part of plain `dotnet test`: 3 seeds (101, 202, 303) × 300 actions each
  — `FuzzSuiteTests`.
- Certification sweep, run once per milestone rather than on every `dotnet test`:
  `CITYBUILDER_FUZZ_ACTIONS=10000 dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"`
  — same 3 seeds at 10,000 actions each (30,000 total); ~8 min on a 12-core box.
- A failure prints the seed, the failing action index, and a replayable action tail —
  paste the tail into a throwaway test calling `GestureFuzzer.Run` to reproduce locally.
- Every real finding is fixed at its root cause in domain code, then pinned forever as a
  seed + action-count fact in `FuzzRegressionTests.cs` (comment naming the root cause) —
  so the exact scenario that once broke something stays covered even after the default
  seeds or action count later change.
- Extend both `NetworkInvariants.Check` and `SimInvariants.CheckBurst` whenever the editor
  surface (new tools/gestures) or the invariants they should hold grow — the fuzzer is
  only as strong as the checks it runs.

**KPI suite** (`tests/Domain.Tests/Kpi/`) is a single fact,
`KpiSuiteTests.GenerateHealthReport`, that runs the deterministic scenarios in
`KpiScenarios` (signal discharge, 4-way yield, grid commute, perf) and merges their
metrics.
- First run ever (no `docs/health/kpi-baseline.json` on disk) bootstraps the baseline and
  passes, provided perf is already under its ceilings.
- Every later run asserts non-perf metrics stay within ±25% of the baseline, and perf
  metrics stay under absolute ceilings (`perf.validate500_ms` < 150 ms,
  `perf.tick300_ms` < 8 ms) — the ceilings are never banded against history, they're hard
  budgets regardless of drift.
- Every run, bootstrap or not, refreshes `docs/health/kpi-latest.json` and
  `docs/health/M<N>.md` (a metric/value/baseline/delta table). Regenerate these at the
  end of a milestone so the checked-in report reflects that milestone's build.

**Manual drift**: `docs/manual/` is the living reference manual for the codebase,
authored and kept current via the `explain-codebase` skill. At the end of a milestone,
run that skill's update mode against what actually changed so the manual's chapters keep
matching the code — the skill handles the mechanics; the obligation is not to skip it.
