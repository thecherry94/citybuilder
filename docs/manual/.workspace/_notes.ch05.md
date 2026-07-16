# Chapter 05 working notes (traffic sim)

## Terminology (candidates for glossary.md)
- **Conflict-point passed-point rule**: JunctionArbiter.cs:28-34 — a conflicting
  occupant blocks entry only until *its own* S has advanced past its conflict-point arc
  distance (STheirs) + Vehicle.Length + ClearMargin(0.5m). Not "connector busy = blocked";
  this is the mechanism that lets crossing connectors coexist most of the time.
- **Movement rank**: JunctionArbiter.MovementRank, lexicographic (Row, Turn) tuple, higher
  wins both axes. Row dominates Turn (a Yield-straight still loses to a Free-left).
- **Impatience / AcceptedGap**: max(2.2, 2.8 - 0.03*JunctionWait) — the gap-acceptance
  threshold shrinks the longer a vehicle waits at the line. Paired with Idm.T=0.95s as the
  two M5 assertiveness levers that fixed the "passive cars clog junctions" throughput bug.
- **Deadlock breaker**: JunctionArbiter.cs:151-156, DeadlockBreakSec=6 — only fires for
  cmp==0 (equal-rank) standoffs; symmetric 4-way-uncontrolled deadlock escape valve, not a
  general impatience mechanism (that's AcceptedGap's job).
- **Virtual stop-line wall**: ComputeAccel synthesizes a (gap, dv) pair as if a stationary
  leader sat at the cut when MayEnter is false — this is why braking toward a red/yield is
  smooth (IDM's own braking curve) rather than an instant hard stop.
- **FreeFlowTime incremental accumulation**: credited at HandleTransitions per completed
  lane run / connector, never estimated from the planned route at spawn — the fix for a
  would-be replan double-count bug (TrafficSim.cs:77-80 comment spells out why).

## Forward-refs made (need back-links when those chapters are written)
- None outbound to un-written chapters — ch05 is the last of the traffic-adjacent group.
  Backward refs used freely: ch.03 (SignalController green/amber/all-red timing, RowFor),
  ch.04 (ConnectorBuilder's never-strand fallback + ConflictPoint computation,
  Vehicle.Length/ClearMargin/SpawnClearance forward-ref from ch04 notes — now resolved
  here), gotchas.md:80-88 (direction-asymmetric adjacency bug class).

## Open questions / uncertainty (2 total in chapter)
- [UNCERTAIN] `TrafficSim.LookAheadHorizon` (TrafficSim.cs:16, 120m) is declared but no
  read site found within this chapter's scope (TrafficSim.cs, JunctionArbiter.cs,
  LaneChange.cs, TrafficSpawner.cs). Possibly a src/Game rendering concern, possibly
  vestigial/dead. Worth a grep across src/Game before the next pass touches it.
- [UNCERTAIN] Merge-conflict tail blind spot: ConflictApproachClear only scans the feeding
  *lane* of a conflicting connector; a rival mid-lane-change onto that feeding lane may not
  be cleanly caught by either that scan or the conflict-point passed-point rule. No test
  found exercising this specific case — flagged as a plausible gap from reading the code,
  not a confirmed bug.

## Patterns observed (cross-cutting, may matter to other chapters)
- Same "authored/resolved" and "mirror-logic-in-a-checker" patterns from ch03/ch04 recur
  here: SimInvariants.ConflictPointCoOccupancyViolations is lifted verbatim into
  AssertivenessGuardTests as the single source of truth shared between the burst fuzz
  harness and the standing regression guard — same spirit as NetworkInvariants re-deriving
  ConnectorBuilder's formula independently (ch04 note).
- Two-layer defense pattern: soft model (IDM) + hard failsafe (EnforceNoPenetration) run
  every tick regardless of whether the soft model "should" have been sufficient. Worth
  naming in a testing-philosophy appendix alongside the ch04 "never strand" philosophy —
  this project consistently pairs a physically-motivated soft mechanism with an
  independent hard invariant-enforcing backstop.
- Direction-asymmetric roads remain the single biggest recurring edge-case source
  (adjacency ordering by signed offset, not |offset| — TrafficSim.cs:523-541) — third
  chapter in a row (03/04/05) where this exact root cause resurfaces in a different guise.
- All per-tick cross-vehicle logic funnels through exactly one function (MayEnter) called
  from exactly two sites that must agree — a "single decision point, multiple call sites
  synced structurally" pattern also seen in ch03's RowFor (ConnectorBuilder.cs:273-286).

## Status
05-traffic-sim.md written to docs/manual/05-traffic-sim.md, 483 lines (spec target
250-400; went over ~20%, comparable overage to ch03's 341/280 due to required-section
density — every mandated section carries real anchored content, already trimmed twice
during authoring). One Mermaid flowchart (the tick pipeline) included per spec. Last
verified commit f0542d7, 2026-07-16. [UNCERTAIN] count: 2 (LookAheadHorizon dead-code
question; merge-conflict tail blind spot).
