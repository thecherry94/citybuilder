# ch04 notes (Lane graph & connectors)

## Terminology added (candidates for glossary.md)
- Turn-lane rank (`laneRank`/`outRank`): direction-aware signed-offset left→right index within one edge's lane group at a node. NEVER |offset| — see gotchas.md:80-88.
- Straight block: per (fromEdge,toEdge) `[start,end)` lane-index window eligible for a Straight connector; capacity-aware, per-target-capped for forks (M6).
- Never-strand fallback: post-pass connecting any zero-connector arriving lane to its nearest eligible departure when the node has departing capacity elsewhere. Distinct from "legally stranded" (iff-rule, already in _terminology.md).
- Arrow bug / M5 arrow report: informal name (from code comments) for the pre-2a0e6c9 defect where straight connectors exceeded receiving capacity.

## Forward-refs made (need back-links when those chapters are written)
- ch. 03 (junctions & control): JunctionGeometry.CutT, JunctionBuilder, JunctionControl.Resolve, TangentContinuationDeg/OnEdge split exemption.
- ch. 05 (traffic sim): JunctionArbiter.MayEnter as sole consumer of ConnectorConflicts/RightOfWay; Vehicle.Length, ClearMargin, SpawnClearance.
- ch. 01 (geometry): Bezier3, BezierOps.Intersections, ArcLengthTable.

## Open questions / uncertainty (1 total in chapter)
- [UNCERTAIN] No dedicated LaneConnectorTests fixture found exercising the multi-target (fork/wye, targets.Length > 1) branch of the per-target capacity cap (step 3, cs:100-116, M6 commit 25fea99) in isolation — only covered indirectly via NetworkInvariants.Check in StraightCapacityInvariantAcrossMixedTypes, where every pairing has a single straight target. Worth a dedicated fixture if this logic is touched again.

## Patterns observed (cross-cutting, may matter to other chapters)
- The "mirror logic in a checker, not shared code" pattern: NetworkInvariants.CheckStraightCapacity intentionally re-derives ConnectorBuilder's drop formula rather than calling into it, specifically so it's an independent set of teeth against regressions (NetworkInvariants.cs:160-169). Same spirit as the M5 fixture tests carrying independent hand-computed expectations. Worth naming as a project convention if a "testing philosophy" appendix ever gets written.
- Recurring "surplus/merge fallback, never strand" philosophy reused three times in this file alone (straight-block merge fallback, per-target cap, never-strand pass) — all descend from one design principle: a narrowing/mismatched road must never leave a lane with zero connectors when an alternative exists.
- Direction-asymmetric road types (OneWay, Asymmetric 2+1) are the single biggest source of edge cases across this whole file — same root cause recurs in TrafficSim._adjacent per gotchas.md.

## Status
04-lane-graph-connectors.md written, ~354 lines (target 200-350, slightly over ceiling but tight — mostly unavoidable given 5-step algorithm + worked example + conflict points required by spec). Last verified commit f0542d7 (== current HEAD at write time). One Mermaid flowchart (assignment decision flow) included per spec suggestion.
