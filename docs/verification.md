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
