# Traffic Feel Tuning Pass (M6.5) — Design

**Date:** 2026-07-16
**Status:** Shipped 2026-07-17 as M6.5 (referee met: sat_headway 2.036 ≤ 2.4). AMENDED
in-flight: scope §4's leader-start anticipation (0.8 s / 4 m cap) was A/B-tested in two
variants and dropped as a measured no-op — the actual discharge bottleneck was the
unprojected spillback check at junction exits, fixed as `SpillbackAnticipationSec`
(0.5 s, raised to 0.7 s as the pre-recorded T7 referee lever, soaked at 3×10k fuzz).
Evidence: `tests/Domain.Tests/Traffic/QueueDischargeTests.cs` docstrings + M6.5 ledger.
(research-driven; see
[docs/research/00-synthesis-traffic-feel.md](../../research/00-synthesis-traffic-feel.md)
for sources and rationale — this spec only fixes scope and acceptance).

## Goal

Make vehicles feel reactive and assertive — CS-like queue discharge, quick launches,
human variety — without touching the M5 safety architecture. Every change is measured
by the M6 KPI harness before/after; the referee metric is `signal.sat_headway_s`
3.52 → **≤ 2.4 s**.

## Non-goals

- No arbitration-rule changes (conflict points, ranks, right-hand rule stay).
- No CS-style despawn failsafe, no mid-segment lane-change rework.
- Stretch items (CAH/coolness blend, HCM follow-up-time gap structure) are documented
  in the research synthesis but OUT of this pass — revisit only if the core wave
  misses the referee target.

## Scope (research ranking #0–#6)

1. **Instrumentation first** (§0): per-queue-position discharge headways (h₁…h₅) and a
   penetration-clamp activation counter, emitted as **diagnostic** metrics — written
   into the health report, never banded against baseline. Diagnostic keys use the
   `diag.` prefix; the KPI suite excludes that prefix from band assertions.
2. **IDM+** (§1): `Idm.Accel` switches from `a·(free − ratio²)` to `a·min(free, 1 − ratio²)`
   (Schakel et al.). Equilibrium gap becomes exactly `s0 + vT`.
3. **Launch acceleration** (§4): max acceleration is speed-dependent — 3.5 m/s² at
   standstill fading linearly to the cruise 2.6 m/s² at 5 m/s (VISSIM CC8/CC9 pattern).
   Only the leading multiplier scales; the `√(a·b)` interaction term keeps base `A`.
4. **Leader-start anticipation** (§2): a stopped follower (v < 0.5 m/s) whose leader is
   moving (leader speed > 0.5 m/s) evaluates IDM with the gap augmented by
   `leaderSpeed · 0.8 s` (capped at gap + 4 m) — it launches on the leader's movement,
   not on gap growth. Real-world startup wave: 1–2 s/vehicle.
5. **Driver personality** (§3): one seeded scalar per vehicle (`Vehicle.Profile` ∈ [0,1],
   drawn from the sim RNG at spawn): desired speed ×lerp(0.85, 1.20), accepted gap
   +lerp(+0.4, −0.4) s. IDM physics constants (A, B, T, S0) stay global. Determinism:
   same seed ⇒ same profiles ⇒ same simulation.
6. **Geometry turn speeds** (§5): `ConnectorSpeed` for turns becomes `√(a_lat · R)` with
   `a_lat = 2.2 m/s²`, R = the connector curve's minimum radius, clamped to
   [4 m/s, straight-speed]; straights unchanged (min of adjoining limits). Cached per
   connector, invalidated on resync (MinRadius sampling is not per-tick cheap).

## Acceptance

- `signal.sat_headway_s` ≤ 2.4 (from 3.52); `signal.startup_lost_s` within 1–4;
  `yield4.completed` ≥ current floor 13, expected ↑ (floor re-raised to lock gains,
  M5 calibration convention); `grid.delay_index` must not increase.
- Safety inviolate: AssertivenessGuardTests (co-occupancy, penetration) and the full
  fuzz suite green; SimInvariants bursts clean. Fixture-repair rule as ever.
- Determinism preserved (fixed seed ⇒ identical trip logs; KPI non-perf metrics
  bit-identical across reruns).
- Each change lands as its own commit with a before/after KPI note in the task report;
  final baseline regenerated once at the end (not per-task) with `docs/health/M6.5.md`.
- Drift updates: manual ch. 05 constants table, roadmap fast-follow entry closed,
  CLAUDE.md test count.

## Verification flow per task

Run the KPI suite against the incoming baseline to *observe* deltas (a band failure on
an improved metric is the expected evidence — captured verbatim in the task report),
then delete `kpi-baseline.json`, rebootstrap, and re-run to green so every task hands
the next one a fresh baseline and the full suite stays green at each commit. Behavior
regressions between tasks are guarded by the per-task fresh baselines plus the
non-negotiable guard tests. The final task renames the report to `M6.5.md` health
naming, re-raises `MinorDischargeFloor`, and runs the whole gate stack.
