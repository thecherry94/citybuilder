# Ch02 working notes (Network & Validation)

## Terminology introduced
- Sliver edge / sliver: a would-be edge shorter than its type's MinSegmentLength; the
  guard vocabulary throughout Validate/Commit.
- Absorption / node-reuse absorption: SplitEdgeWithReuse snapping a split point to an
  existing end node instead of splitting, when within that edge's own MinSegmentLength
  of the end (RoadNetwork.cs:513-536). Radius scales with road type width (up to 16-21 m),
  NOT the small fixed NodeReuseRadius (0.5 m) — these are two different distances that
  are easy to conflate; flagged clearly in ch02.
- G1 tangent-continuation exemption: already seeded in _terminology.md by an earlier
  pass; ch02 expands it with the OnEdge-only scope (AtNode stays strict).
- Stranded lane / iff-rule: already seeded; ch02 is the canonical explanation site
  (NetworkInvariants.CheckLaneCoverage), cites spec amendment ceb1887 directly.

## Forward-refs planted (need back-links when those chapters are written)
- ch03 (Junctions & control): RebuildDerived -> JunctionBuilder.Build /
  ConnectorBuilder.Build (RoadNetwork.cs:715-721); JunctionConfig/JunctionGeometry
  fields on RoadNode.
- ch04 (Lane graph & connectors): TurnKind, RightOfWay, ConnectorConflicts, U-turns
  excluded from "departing capacity" in the stranded-lane rule; CheckStraightCapacity
  mirrors ConnectorBuilder's drop-to-turn-lane logic.
- ch05 (Traffic sim): SpeedLimit / DesignSpeedKmh feed ConnectorSpeed costs.
- ch06 (Drafting & snapping): DraftSession/RoadDraft as the actual Validate/Commit
  caller; G1 start-tangent lock produces the OnEdge tangential-departure geometry ch02
  validates.
- ch08 (Persistence): RoadNetwork.Persistence.cs restores nodes directly, bypassing
  HandleNodeAfterRemoval/TryHealNode (per its own comment) — worth a cross-ref check
  when ch08 is written re: whether restored state can violate invariants TryHealNode
  would otherwise have applied.

## Open questions / uncertainties raised in ch02
1. TryHealNode has no MinSegmentLength/MinRadius recheck after CurveFit.FitComposite —
   flagged [UNCERTAIN] as a possible latent gap (Known limits section). No repro found
   by reading; would need a targeted property test to confirm reachability.
2. RoadCatalog.Get linear scan over 6 types — non-issue now, flagged only in case the
   catalog grows.
3. Whether a larger road-type catalog is actually on the roadmap — checked
   docs/roadmap.md, found no mention either way.

## Patterns worth reusing in later chapters
- The "Validate = ground truth gate, checker = post-state auditor" framing
  (NetworkInvariants.cs:7-15 doc comment) is a clean way to explain any future
  validate/checker pairs in the codebase, if one shows up in traffic (SimInvariants?).
- Worked-example choice: picked an existing xUnit test
  (PlacementTests.TJunctionViaOnEdgeBindingSplits) and traced it verbatim rather than
  inventing new coordinates — kept the trace verifiable against a real, currently-passing
  assertion. Recommend this approach for ch03-08 worked examples too where a suitable
  test exists.

## Scope note
Chapter written to ~360 lines against a 200-350 target; trimmed once already. Slightly
over budget but every section earns its length (six-row catalog table, dense
commit-guard rationale). Not re-trimmed further to avoid cutting load-bearing detail.
