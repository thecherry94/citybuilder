# Traffic Feel Tuning Pass (M6.5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reactive, assertive, human-varied traffic — `signal.sat_headway_s` 3.52 → ≤ 2.4 s — per spec `docs/superpowers/specs/2026-07-16-traffic-feel-tuning.md`.

**Architecture:** Six independent behavior changes, each its own commit with before/after KPI evidence and a fresh baseline handed to the next task. The M5 arbitration architecture is untouched; changes live in `Idm`, `TrafficSim.ComputeAccel/DesiredSpeed/ConnectorSpeed`, `Vehicle`, and the KPI scenarios.

**Tech Stack:** C# domain (net8.0, no Godot), xUnit (net10.0).

## Global Constraints

- `src/Domain` never references Godot. Determinism: fixed seed ⇒ identical simulation (profiles drawn ONLY from the sim's seeded RNG, in spawn order).
- Safety inviolate: `AssertivenessGuardTests` (conflict co-occupancy, penetration) and the full fuzz suite (3×300 default) must pass EVERY task. Fixture-repair rule: adjust geometry/timing, never weaken invariant assertions.
- KPI flow per task: run KPI suite vs incoming baseline → record deltas verbatim in the task report (band failure on an improved metric = expected evidence) → delete `docs/health/kpi-baseline.json` → rebootstrap → rerun green → commit code + refreshed health artifacts together.
- Guard bands that must hold at every task: `signal.startup_lost_s` ∈ [1, 4]; `yield4.completed` ≥ 13; `grid.delay_index` never above its incoming value +5%; `grid.stops_per_trip` ≤ 1.71 absolute (M6 origin 1.367 + 25% — origin-anchored because per-task baseline rebootstraps would let it compound; T2 review adoption); perf ceilings unchanged (validate500 < 150 ms, tick300 < 8 ms).
- Constants land verbatim: IDM+ min-form; LaunchA = 3.5 f, LaunchFadeSpeed = 5 f; AnticipationSec = 0.8 f, AnticipationCap = 4 f; Profile v0 ×lerp(0.85, 1.20), gap +lerp(+0.4, −0.4) s; a_lat = 2.2 f, turn-speed clamp [4, straight].
- Commit per green task with the trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

---

### Task 1: Discharge instrumentation + diagnostic metrics

**Files:**
- Modify: `tests/Domain.Tests/Kpi/KpiScenarios.cs` (signal_discharge scenario), `tests/Domain.Tests/Kpi/KpiSuiteTests.cs` (diag-prefix exclusion + report filename const), `src/Domain/Traffic/TrafficSim.cs` (penetration-clamp counter)

**Interfaces:**
- Produces: metric keys `diag.signal.h1`…`diag.signal.h5` (float seconds, per-queue-position discharge headway) and `diag.penetration_clamps` (float count over the grid_commute run); `internal int PenetrationClampCount` on `TrafficSim`; `const string Milestone = "M6.5"` in `KpiSuiteTests` controlling the report filename (`docs/health/M6.5.md`).

- [ ] **Step 1: Write the failing test** — add to `KpiSuiteTests` (or a new small fact in the same file):

```csharp
[Fact]
public void DiagnosticMetricsAreEmittedButNeverBanded()
{
    var metrics = KpiScenarios.SignalDischarge();
    for (int i = 1; i <= 5; i++)
        Assert.True(metrics.ContainsKey($"diag.signal.h{i}"), $"missing diag.signal.h{i}");
    Assert.True(metrics["diag.signal.h1"] > metrics["diag.signal.h5"],
        "position-1 headway should exceed position-5 (Bonneson pattern)");
}
```

(Adapt the scenario-builder call name to the real one in `KpiScenarios.cs` — read it first; it returns `Dictionary<string, float>`.)

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter "FullyQualifiedName~DiagnosticMetrics" 2>&1 | tail -3`. Expected: FAIL (keys missing).

- [ ] **Step 3: Implement.** In the signal_discharge scenario, the connector-entry times of the 10 pre-queued vehicles are already recorded (that's how `sat_headway_s` is computed) — derive `h_i = entry_i − entry_{i−1}` for i = 1..5 with `h1 = entry_1 − greenOnset`, and add them under the `diag.` keys. In `TrafficSim.EnforceNoPenetration`, increment `PenetrationClampCount` whenever the clamp actually modifies a vehicle's `S` or `Speed` (not on no-op passes). In the grid_commute scenario, read the counter at the end into `diag.penetration_clamps`. In `KpiSuiteTests`: (a) metrics whose key starts with `"diag."` are written to `kpi-latest.json` and the markdown report but SKIPPED by the ±25 % band assertion and excluded from `kpi-baseline.json`; (b) introduce `const string Milestone = "M6.5"` and write the markdown to `docs/health/{Milestone}.md` (the M6.md report stays in git history; do not delete it).

- [ ] **Step 4: Full suite + KPI flow** — `dotnet test 2>&1 | tail -3` green (adding diag keys must not disturb banded metrics; if the suite fails on bands, that is a bug in your exclusion, not a baseline issue). Read `docs/health/M6.5.md`: h₁ > h₂ > … plausible (expect h₁ ≈ 2.5–4.5 s and slow decay — this run is the "before" snapshot the whole pass is judged against). Record the h-values and clamp count verbatim in the task report.

- [ ] **Step 5: Commit** — `feat(test): per-position discharge headways + penetration-clamp diagnostics`

---

### Task 2: IDM+ (min form)

**Files:**
- Modify: `src/Domain/Traffic/Idm.cs:25`
- Test: `tests/Domain.Tests/Traffic/IdmTests.cs` (read it first; extend, don't rewrite)

**Interfaces:**
- Consumes/produces: `Idm.Accel(float v, float v0, float gap, float dv)` signature unchanged.

- [ ] **Step 1: Write the failing test:**

```csharp
[Fact]
public void EquilibriumGapIsExactlyS0PlusVT()
{
    // At equilibrium (accel == 0, dv == 0) IDM+ must hold gap == S0 + v*T.
    // Plain IDM inflates this gap by 1/sqrt(1-(v/v0)^4) — the sluggishness root cause.
    float v = 10f, v0 = 14f;
    float eq = Idm.S0 + v * Idm.T;
    float accel = Idm.Accel(v, v0, gap: eq, dv: 0f);
    Assert.True(MathF.Abs(accel) < 0.05f,
        $"IDM+ should be ~zero accel at gap==s0+vT, got {accel:F3}");
    Assert.True(Idm.Accel(v, v0, eq * 1.3f, 0f) > 0.1f, "should accelerate when gap is generous");
    Assert.True(Idm.Accel(v, v0, eq * 0.7f, 0f) < -0.1f, "should brake when gap is tight");
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter "FullyQualifiedName~EquilibriumGap" 2>&1 | tail -3`. Expected: FAIL (plain IDM has ~−0.3 accel at s0+vT for these values).

- [ ] **Step 3: Implement** — in `Idm.Accel`, replace the final line:

```csharp
// before:  return A * (free - ratio * ratio);
return A * MathF.Min(free, 1f - ratio * ratio);   // IDM+ (Schakel et al.)
```

- [ ] **Step 4: Full suite + KPI flow per Global Constraints.** Expect `signal.sat_headway_s` and `grid.delay_index` to improve and possibly break the incoming band — record verbatim, rebootstrap, rerun green. Existing IdmTests asserting exact accel values may legitimately shift: adjust EXPECTED NUMBERS only where the test was asserting plain-IDM arithmetic, never weaken directional/invariant assertions; report every touched fixture.

- [ ] **Step 5: Commit** — `feat(domain): IDM+ min-form car following`

---

### Task 3: Speed-dependent launch acceleration

**Files:**
- Modify: `src/Domain/Traffic/Idm.cs`
- Test: `tests/Domain.Tests/Traffic/IdmTests.cs`

**Interfaces:**
- Produces: `Idm.LaunchA = 3.5f`, `Idm.LaunchFadeSpeed = 5f` consts; `Accel` signature unchanged.

- [ ] **Step 1: Failing test:**

```csharp
[Fact]
public void LaunchAccelerationExceedsCruiseAcceleration()
{
    float atRest = Idm.Accel(v: 0f, v0: 14f, gap: Idm.FreeGap, dv: 0f);
    float atSpeed = Idm.Accel(v: 6f, v0: 14f, gap: Idm.FreeGap, dv: 0f);
    Assert.True(atRest > 3.2f, $"standstill launch should use ~3.5 m/s², got {atRest:F2}");
    Assert.True(atSpeed < 2.6f * 1.01f, "above fade speed the cruise cap must hold");
}
```

- [ ] **Step 2: red** — `dotnet test --filter "FullyQualifiedName~LaunchAcceleration" 2>&1 | tail -3` (plain: atRest == A == 2.6).

- [ ] **Step 3: Implement** — in `Idm`:

```csharp
public const float LaunchA = 3.5f;        // standstill launch, m/s² (VISSIM CC8-style)
public const float LaunchFadeSpeed = 5f;  // m/s at which launch boost has fully faded

private static float EffectiveA(float v)
    => v >= LaunchFadeSpeed ? A : LaunchA + (A - LaunchA) * (v / LaunchFadeSpeed);
```

and in `Accel`, use `float a = EffectiveA(v);` for the two leading multipliers (`a * free`, `a * MathF.Min(...)`) while `sStar`'s `MathF.Sqrt(A * B)` keeps the base `A` (interaction-term stability — spec constraint).

- [ ] **Step 4: Full suite + KPI flow.** Watch `signal.startup_lost_s`: it may drop below 1.33; it must stay ≥ 1.0 (band floor). If it lands under 1.0, reduce LaunchA to 3.2 (one retry, document) — below that, STOP and report.

- [ ] **Step 5: Commit** — `feat(domain): speed-dependent launch acceleration`

---

### Task 4: Leader-start anticipation

**Files:**
- Modify: `src/Domain/Traffic/TrafficSim.cs` (`ComputeAccel`, after `LeaderGap` at `TrafficSim.cs:210`)
- Test: `tests/Domain.Tests/Traffic/TripStatsTests.cs` or a new `QueueDischargeTests.cs`

**Interfaces:**
- Produces: `TrafficSim.AnticipationSec = 0.8f`, `AnticipationCap = 4f` consts.

- [ ] **Step 1: Failing test** (new file `tests/Domain.Tests/Traffic/QueueDischargeTests.cs`; use the `Net` helper like sibling tests):

```csharp
[Fact]
public void StoppedFollowerLaunchesWhenLeaderMoves()
{
    // Two cars queued nose-to-tail at a red-light stand-in (leader held, then released).
    // Follower must begin accelerating within ~1s of the leader moving, NOT wait for
    // the gap to open to s0+vT first.  Build: one straight road, spawn leader+follower,
    // force leader stopped (block via a holder vehicle or run until queued at line),
    // then release and measure the follower's first tick with Speed > 0.5.
    // Assert: followerStart - leaderStart < 1.5f  (real startup wave: 1-2 s/vehicle).
}
```

Write this as a REAL test, not the comment sketch: the cleanest deterministic setup is the signal_discharge layout from `KpiScenarios` (lights junction, pre-queued vehicles) — copy its queue-buildup pattern, find green onset via the same `PhaseFor` probing, record per-vehicle first-movement times, and assert `start[i+1] - start[i] < 1.5f` for the first 4 pairs. Read `KpiScenarios.cs` first and reuse its approach; keep the test under 15 s of simulated time.

- [ ] **Step 2: red** — `dotnet test --filter "FullyQualifiedName~StoppedFollowerLaunches" 2>&1 | tail -3`. Confirm the measured wave is currently ≥ 1.5 s/vehicle (that's the bug being fixed). If it already passes, tighten to 1.2 f and re-confirm red before proceeding (calibrate the gate, don't skip it; report the measured before-value).

- [ ] **Step 3: Implement** — in `ComputeAccel`, immediately after `var (gap, dv) = LeaderGap(v);`:

```csharp
// leader-start anticipation: a stopped follower reacts to the leader MOVING,
// not to the gap having physically opened (real startup wave: 1-2 s/vehicle)
if (v.Speed < 0.5f && gap < Idm.FreeGap / 2 && dv < -0.5f)
{
    float lead = v.Speed - dv; // leader speed
    gap += MathF.Min(lead * AnticipationSec, AnticipationCap);
}
```

with the two consts on `TrafficSim`. Note `dv = v.Speed − vLead`, so a moving leader ⇒ `dv < 0`; the stop-line pseudo-walls set `dv = v.Speed` (≥ 0) so anticipation NEVER applies to junction holds — verify this reading against `ComputeAccel`'s wall branches (`TrafficSim.cs:213-241`) and state it in the report.

- [ ] **Step 4: Full suite + KPI flow.** Expect the biggest `sat_headway_s` drop of the pass here. Safety gates green (penetration invariant especially — anticipation must never cause a clamp-count spike: compare `diag.penetration_clamps` before/after, flag > 2× increase as a failure to investigate, not commit).

- [ ] **Step 5: Commit** — `feat(domain): leader-start anticipation for queue discharge`

---

### Task 5: Per-driver personality

**Files:**
- Modify: `src/Domain/Traffic/Vehicle.cs`, `src/Domain/Traffic/TrafficSpawner.cs` (both spawn paths — ambient and manual), `src/Domain/Traffic/TrafficSim.cs` (`DesiredSpeed`), `src/Domain/Traffic/JunctionArbiter.cs:78` (`AcceptedGap`)
- Test: `tests/Domain.Tests/Traffic/DriverProfileTests.cs` (new)

**Interfaces:**
- Produces: `Vehicle.Profile` (float ∈ [0,1], default 0.5f so hand-constructed test vehicles behave neutrally); profile applied as: desired speed `v0 *= 0.85f + 0.35f * v.Profile`; accepted gap `+= 0.4f - 0.8f * v.Profile`.

- [ ] **Step 1: Failing tests:**

```csharp
[Fact]
public void ProfilesAreDeterministicPerSeed()
{
    // same seed → identical profiles in spawn order; different seed → different
    var a = SpawnProfiles(seed: 7); var b = SpawnProfiles(seed: 7); var c = SpawnProfiles(seed: 8);
    Assert.Equal(a, b);
    Assert.NotEqual(a, c);
    Assert.All(a, p => Assert.InRange(p, 0f, 1f));
    Assert.True(a.Distinct().Count() > 3, "profiles must actually vary");
}

[Fact]
public void AggressiveDriverCruisesFasterThanTimidOnFreeRoad()
{
    // two vehicles on separate long roads, profile 0.1 vs 0.9, no leaders;
    // after 15s the 0.9 driver's speed exceeds the 0.1 driver's by >= 10%.
}
```

Implement `SpawnProfiles` as a local helper: build a road, run a seeded `TrafficSim` with `TargetPopulation = 10` for a few sim-seconds, collect `Vehicles.Select(v => v.Profile)` (find the real vehicle-enumeration accessor — internals are visible to the test project). Write the second test fully (two separate straight roads in one network, one manual `Spawn` each, then `ForceLane`-style profile assignment or spawn-order-controlled seeding — read how manual `Spawn` flows through `TrafficSpawner` first and pick the cleanest deterministic way to pin the two profiles; setting `v.Profile` directly after spawn is acceptable and simplest).

- [ ] **Step 2: red** — `dotnet test --filter "FullyQualifiedName~DriverProfile" 2>&1 | tail -3`. Expected: compile error (`Profile` missing).

- [ ] **Step 3: Implement** — `Vehicle.Profile { get; set; } = 0.5f`; in BOTH spawner paths draw `Profile = (float)_rng.NextDouble()` from the sim's existing seeded RNG at spawn time (verify the RNG the spawner already uses and use the same instance — a second RNG breaks determinism review); in `DesiredSpeed` multiply the returned `v0` (both the lane branch and the connector branch) by `0.85f + 0.35f * v.Profile`; in `AcceptedGap` return `MathF.Max(1.8f, 2.8f - 0.03f * v.JunctionWait + (0.4f - 0.8f * v.Profile))` — note the floor drops 2.2 → 1.8 ONLY for aggressive profiles via the offset; document that 1.8 is the new hard floor and the mean driver keeps the old 2.2.

- [ ] **Step 4: Full suite + KPI flow.** Fuzz suite green is CRITICAL here (seed-pinned regressions must not shift — profiles draw from the sim RNG inside TrafficSim/Spawner, which the fuzzer's bursts construct fresh per burst; confirm pinned fuzz regressions still pass unchanged). Safety gates green: co-occupancy with the 1.8 s floor must hold (AssertivenessGuardTests) — if it fails, raise the aggressive floor to 2.0 (one retry, document), else STOP.

- [ ] **Step 5: Commit** — `feat(domain): per-driver personality (desired speed + gap acceptance)`

---

### Task 6: Geometry-based turn speeds

**Files:**
- Modify: `src/Domain/Traffic/TrafficSim.cs` (`ConnectorSpeed`, `TrafficSim.cs:271-281`; add cache + resync invalidation)
- Test: `tests/Domain.Tests/Traffic/TurnSpeedTests.cs` (new)

**Interfaces:**
- Produces: `TrafficSim.LateralComfort = 2.2f` const; `ConnectorSpeed` behavior: straights unchanged; turns/U-turns `clamp(sqrt(2.2 * Rmin), 4, straightSpeed)` where `Rmin = BezierOps.MinRadius(connector curve)` and `straightSpeed = min(from, to run limits)`.

- [ ] **Step 1: Failing test:**

```csharp
[Fact]
public void TightTurnIsSlowerThanSweepingTurn()
{
    // Small cross (tight corner radii) vs a huge-radius Y: right-turn connector speed
    // must be lower at the tight junction. Build both, locate a Right connector each
    // (node.Connectors[i].Turn == TurnKind.Right), and compare via the internal
    // ConnectorSpeed accessor (add `internal float ConnectorSpeedFor(NodeId, int)`
    // delegating to the private method if none exists).
    // Also: Assert.InRange(speed, 4f, straightSpeed) for both.
}
```

Write it fully against the real helpers (`Net` fixture builders; read `LaneConnectorTests` for how to find connectors by turn kind).

- [ ] **Step 2: red** — currently both return exactly 9 f → equality assertion fails.

- [ ] **Step 3: Implement** — replace the `Right/Left/U` arms with the radius formula; compute `Rmin` via `BezierOps.MinRadius(conn.Curve)` ONCE per connector into a `Dictionary<(NodeId, int), float> _connectorSpeed` cache, cleared wherever the sim already resyncs to network changes (find the existing `Changed`/version-resync hook — the same place `_runs` rebuilds). U-turns keep a lower cap: `MathF.Min(result, 6f)`.

- [ ] **Step 4: Full suite + KPI flow.** `yield4.completed` and `grid.delay_index` may move either direction (tight urban corners get slower, sweepers faster) — guard bands per Global Constraints; `FreeFlowTime`'s 8 m/ConnectorSpeed estimate consumes the new values automatically (verify `TrafficSim.cs:406` still compiles against the cache). Perf: tick300 must not regress > 10 % (cache exists precisely for this).

- [ ] **Step 5: Commit** — `feat(domain): curvature-based turn speeds`

---

### Task 7: Recalibration close-out

**Files:**
- Modify: `tests/Domain.Tests/Traffic/AssertivenessGuardTests.cs` (MinorDischargeFloor), `docs/roadmap.md`, `docs/manual/05-traffic-sim.md` (constants table + tick description), `docs/manual/03-junctions-control.md` (sat-headway note), `CLAUDE.md` (test count)
- No new code.

- [ ] **Step 1:** Full gate stack in sequence: `dotnet test` | `dotnet build citybuilder.sln` | `CITYBUILDER_SMOKE=1 godot --headless .` | `CITYBUILDER_SHOTS=tests/visual/shots godot .` (read the heatmap PNG — expect measurably greener grid than M6's under the same load) | UI test.
- [ ] **Step 2:** Certification fuzz `CITYBUILDER_FUZZ_ACTIONS=10000 dotnet test --filter "FullyQualifiedName~FuzzSuiteTests" 2>&1 | tail -5` — behavior changes must survive 30 k actions (bursts run the new model).
- [ ] **Step 3:** Referee check against spec acceptance: `signal.sat_headway_s` ≤ 2.4, `startup_lost_s` ∈ [1,4], `yield4.completed` ≥ 13 and reported, `grid.delay_index` ≤ M6 value. If the referee target is missed, STOP and report (stretch items CAH/follow-up-time are the named next levers — do NOT freelance them in).
- [ ] **Step 4:** Lock gains: set `MinorDischargeFloor` to (new measured yield4.completed − 3) if measured improved by ≥ 3 (M5 calibration convention — read the constant's comment); update manual ch. 05 constants table (IDM+ form, LaunchA/fade, AnticipationSec, Profile ranges, LateralComfort, new AcceptedGap floor) and ch. 03's sat-headway known-limit note (now resolved — say to what value); roadmap: mark the tuning fast-follow done under M6.5 with the headline numbers; CLAUDE.md test count.
- [ ] **Step 5:** Commit — `feat: M6.5 traffic feel — IDM+, launch accel, anticipation, personalities, curve speeds`

---

## Plan self-review (done at authoring time)

- **Spec coverage:** §0→T1, §1→T2, §4→T3, §2→T4, §3→T5, §5→T6, acceptance+drift→T7. Stretch items explicitly excluded (T7 step 3 forbids freelancing them).
- **Type consistency:** `Idm.Accel` signature stable across T2/T3; `Vehicle.Profile` default 0.5 keeps every existing hand-built test vehicle neutral; `diag.` prefix defined once (T1) and consumed in T4's clamp comparison.
- **Known judgment points:** T4's test-calibration step (measure-then-gate), T5's RNG-instance identification, T6's cache-invalidation hook — each carries a read-first instruction instead of invented code.
- **Sequencing:** instrumentation before any behavior change (before-snapshot); IDM+ before launch/anticipation (their KPI deltas would otherwise be confounded with the structural fix); personality after the physics settles; close-out last.
