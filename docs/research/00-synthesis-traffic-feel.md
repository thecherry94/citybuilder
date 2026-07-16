# Traffic feel: research synthesis & attack plan

**Date:** 2026-07-16 · **Trigger:** cars feel sluggish/non-reactive and accelerate too
slowly vs Cities: Skylines. **Companion reports** (full citations there):
[cs-vanilla-ai.md](cs-vanilla-ai.md) · [tmpe-and-mods.md](tmpe-and-mods.md) ·
[driver-model-literature.md](driver-model-literature.md)

## The diagnosis, in one paragraph

Our IDM parameters are *not* the problem — a=2.6 m/s², T=0.95 s, s0=2 m already sit at
the assertive end of the signalized-intersection calibration literature and should yield
~1.8 s saturation headway, yet we measure 3.52 s. The gap is **structural**: (1) plain
IDM's subtractive form inflates equilibrium gaps and makes standstill restarts wait for
the physical gap to open (~1–2 m) before any meaningful acceleration — the literature's
verdict is that plain IDM cannot reach realistic capacity at reasonable T; (2) every
vehicle is identical (zero per-driver variance — the exact "robotic" hole TM:PE's whole
personality system exists to patch in CS1); and (3) our reactive queue start-up wave is
slower than the real 1.0–2.0 s/vehicle because followers wait for gap growth instead of
anticipating the leader's launch. Notably, CS1 gets its "alive" feel by the opposite
architecture — cruise at limit by default, brake only for a predicted conflict, winner
never slows — while we are cautious-by-default; we don't need to adopt their model, but
we need to kill the places where caution produces no safety, only sluggishness.

## Ranked candidate changes (feel-impact ÷ risk)

| # | Change | What | Expected effect | Source |
|---|---|---|---|---|
| 0 | **Instrument first** | Per-queue-position discharge headways in the KPI signal scenario (h₁…h₁₀). Real pattern: h₁≈3.5–4 s falling to ~2 s by position 5. Tells us exactly where the 3.52 s comes from before we touch anything. | diagnosis | Bonneson; lit report §3 |
| 1 | **IDM+ (min form)** | `a·min(free, interaction)` instead of `a·(free − ratio²)`. One line in `Idm.Accel`. Equilibrium gap becomes exactly s0+vT → realistic capacity. | sat headway ↓ toward ~2 s; denser stable flow | Schakel et al.; lit report §1 |
| 2 | **Leader-start anticipation** | At standstill in a queue, when the leader starts moving (or its connector clears), begin accelerating after a short reaction delay instead of waiting for the gap to grow. | startup wave 1–2 s/vehicle (matches real platoons); queues "peel off" visibly | lit report §4 |
| 3 | **Per-driver personality scalar** | One seeded `timedRand`-style scalar per vehicle (TM:PE's exact trick): lerp desired speed ×0.8–1.3, plus small offsets on T and AcceptedGap. Do NOT vary A/B physics. | kills the robotic lockstep; lane use diversifies | TM:PE `VehicleBehaviorManager`; tmpe report §2 |
| 4 | **Speed-dependent launch accel** | VISSIM standard: standstill launch ≈2× cruise accel (CC8=3.5 vs CC9=1.5 m/s²). Scale `Idm.A` by a launch boost below ~5 m/s. | directly answers "don't accelerate quickly enough" | VISSIM defaults; lit report §4 |
| 5 | **CAH/coolness blend** (with b 2.8→~2.0) | Blend IDM with constant-acceleration heuristic (c≈0.99) so cars stop panic-braking at 8 m/s² where 2 suffices — smoother approach, later braking, more assertive look. | approach behavior looks confident, not hesitant | Treiber/Kesting ACC; lit report §2 |
| 6 | **Geometry-based turn speeds** | `v = √(a_lat·R)` with a_lat≈2.0–2.5 m/s², v_min clamp — replaces fixed Right 9 / Left 10 m/s. Our fixed values equal a 40–50 m radius at comfort a_lat: too fast for tight corners, too slow on sweepers. | turns look intentional; junction clearing speeds correct both ways | AASHTO e+f; lit report §5 |
| 7 | **Structured gap acceptance** | Keep our aggressive base (a feel choice), add HCM's *structure*: per-movement critical gaps (~1.8× spread right→minor-left) and a separate **follow-up time** (~0.5–0.6× critical gap) so a platoon streams through one accepted gap instead of re-judging per car. | minor-road bursts through gaps like real drivers | HCM; lit report §6 |
| 8 | **Audit `EnforceNoPenetration`** | The back-solve clamp may be manufacturing invisible hard stops (the same failure TM:PE's abandoned reserved-space check had). Telemetry: count clamp activations in the KPI scenarios. | possibly free smoothness | tmpe report §6 |

Not adopted (rejected consciously): CS1's despawn failsafe (we don't delete stuck cars);
mid-segment lane-change removal (CO says it made things worse — ours are already
node-anchored); copying CS accel constants (none are published; units unitless).

## Verification plan

The M6 KPI harness is the referee: `signal.sat_headway_s` 3.52 → target ≤ 2.4;
`signal.startup_lost_s` stays 1–4; `yield4.completed` ≥ 18 floor 13 must not regress
(safety guards: AssertivenessGuardTests co-occupancy + penetration invariants);
`grid.delay_index` expected ↓. Each change lands separately with a before/after health
report; the queue-position instrumentation (#0) lands first and its numbers go into
`docs/health/`.
