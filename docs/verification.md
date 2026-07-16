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

Screenshot harness extras:
- `CITYBUILDER_SHOTS_TINT=1` — tint junction meshes to attribute overlapping surfaces.
- `CITYBUILDER_SHOTS_DUMP=<scenario>` — print flat quads at marking height (Y≈0.10):
  **ground truth for where paint actually is** — far more reliable than pixel-guessing.
- Traffic scenarios also emit `*_motion0..7.png` filmstrips (0.33 s apart).

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
editor surface (draw/bulldoze/upgrade/ConfigureJunction gestures), checking
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
