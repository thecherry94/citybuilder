# Manual Outline

**Created:** 2026-07-16
**Target commit:** `f0542d7` on branch `main`
**Scope:** entire repo — `src/Domain` (net8.0, pure C#), `src/Game` (Godot 4.6.2 mono), plus the quality-stack test infrastructure where it explains how to verify each subsystem.
**Target reader:** an experienced game/sim programmer new to this codebase; knows C#, Godot basics, and general traffic-sim concepts; cannot ask the original author.
**Approval:** chapter list was specified in the user-approved M6 plan (`docs/superpowers/plans/2026-07-15-quality-stack-m6.md` Task 12); the standing user directive is autonomous completion, so this outline executes without a further review stop.

## Survey summary

- ~9,400 LOC C# source (excl. generated/obj), 40 domain + game files; ~13k LOC tests (xUnit, net10.0).
- Subsystems: geometry (4 files), network core (10), catalog (2), drafting/snapping (6), traffic (11), persistence (3), game/rendering (18).
- Existing docs: docs/architecture.md, conventions.md, verification.md, gotchas.md, roadmap.md — the manual complements them (deeper, per-subsystem, book-shaped); cross-link, don't duplicate.

## Book structure (flat, per the approved plan)

| Chapter | File | Sources | Template |
|---|---|---|---|
| 00 Overview & reading guide | `00-overview.md` | survey + all chapters | front matter (written in Phase 6) |
| 01 Geometry | `01-geometry.md` | src/Domain/Geometry/* | module |
| 02 Network & validation | `02-network-validation.md` | RoadNetwork.cs, Entities.cs, Ids.cs, CurveFit.cs, NetworkInvariants.cs, Catalog/RoadType.cs | module |
| 03 Junctions & control | `03-junctions-control.md` | JunctionBuilder.cs, JunctionControl.cs, SignalController.cs | module |
| 04 Lane graph & connectors | `04-lane-graph-connectors.md` | LaneGraph.cs, ConnectorBuilder.cs | algorithm |
| 05 Traffic sim | `05-traffic-sim.md` | TrafficSim.cs, TrafficSpawner.cs, Vehicle.cs, Idm.cs, JunctionArbiter.cs, LaneChange.cs, Route.cs, RoutePlanner.cs, SimInvariants.cs | module+algorithm |
| 06 Drafting & snapping | `06-drafting-snapping.md` | Tools/Draft/*, Tools/Snapping/*, PlacementProposal.cs | module |
| 07 Rendering & markings | `07-rendering-markings.md` | src/Game/* (view classes), Catalog/MarkingRules.cs | module |
| 08 Persistence | `08-persistence.md` | Persistence/*, RoadNetwork.Persistence.cs, Main.cs quick save/load | module |
| Glossary | `glossary.md` | compiled from _terminology.md | appendix |

Each chapter: purpose, key algorithms, invariants, tuning constants with rationale, known limits, how to verify (per the M6 spec §5).

## Batches

- **Batch 1 (domain structure):** 01, 02, 03, 04 — parallel subagents.
- **Batch 2 (behavior + surfaces):** 05, 06, 07, 08 — parallel subagents.
- **Phase 6 (controller):** 00 overview, glossary, README index, cross-ref resolution, coherence pass.

## Skip list (documented in 00-overview)

| Component | Reason |
|---|---|
| `src/*/obj/` generated files | build artifacts |
| `tests/` per-file coverage | tests are cited as verification pointers inside each chapter, not chaptered |
| `src/Game/VisualShots.cs` internals | test harness, covered by docs/verification.md; referenced from ch. 07 |
| Godot project plumbing (project.godot, scenes) | standard engine config |
