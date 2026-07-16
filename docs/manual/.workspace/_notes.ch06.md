# ch06 notes (Drafting & Snapping)

## Terminology added (candidates for glossary.md)
- Draft handle / IDraftShape: gesture layer's strategy interface (RequiredHandles/RoleOf/Curves), stateless, 6 impls (Straight/QuadCurve/CubicCurve/Arc/GridStamp; Chain reuses QuadCurveShape).
- Tangent lock (G1 lock): RoadDraft.StartTangent != null; shrinks a shape's RequiredHandles; released via DraftSession.ReleaseTangentLock (game layer binds "T", per DraftSessionTests.cs:147 comment).
- Candidate-scored snapping: SnapEngine.Resolve picks lowest score = distance/weight across all producers, not a priority cascade. Weight rescales effective radius per kind.
- AdjustMode: session-level toggle (not a SessionState) forcing every complete gesture to Adjustable for manual confirm, even if valid.
- Flashed dual contract (M6): fires on both hard failure AND on soft success with DroppedSegments > 0 — non-null message != nothing built.

## Forward-refs made (need back-links when those chapters are written)
- ch. 02 (network/validation): RoadNetwork.Validate (RoadNetwork.cs:69-173) owns every numeric guard summarized here (MinSegmentLength, MinRadius, MinJunctionAngleDeg, TangentContinuationDeg exemption, crossing-spacing floors); RoadNetwork.Commit's node-reuse relocation + CommitResult.DroppedSegments; HasSharpLegAtNode (commit-time, once real node known). ch06 only explains the draft-side degenerate-input guards (Eps checks) and cites Validate's guards as summary, not primary source.
- ch. 01 (geometry): Bezier3.FromQuadratic, BezierOps.ArcFromTangent (175° sweep cap, >90° splits into 2 curves), BezierOps.MinRadius (readout), BezierOps.ClosestPoint (edge snap projection).
- src/Game (not chaptered yet / ToolController out of scope): "T" key binding for tangent-lock release, numpad-Enter-doesn't-confirm, radius-readout-doesn't-turn-red, camDist*0.02 clamp [1,20] snap radius computation (CameraRig.SnapRadius()) — all cited from roadmap.md/conventions.md, not independently verified against source since out of ch06's file scope.

## Open questions / uncertainty (1 total in chapter)
- [UNCERTAIN] WeightGuideline == WeightGridPoint == 1.5 (SnapEngine.cs:35-36) — no comment states this equality is intentional vs coincidental (unlike WeightNode's documented 3.0→4.0 fix). Worth asking author or checking git blame if it matters later.

## Patterns observed (may recur in other chapters / worth glossary treatment)
- "Cheap degenerate-input filter vs expensive semantic validator" split: RoadDraft/Shapes.cs return null (Eps-distance/zero-vector guards) rather than building a proposal at all; RoadNetwork.Validate is where every *numeric floor* actually lives. Nice clean division of labor, worth naming if an architecture appendix ever names layering patterns generally.
- "AtNode stays strict, OnEdge gets G1 slack" asymmetry (RoadNetwork.cs:236 fromEdge-only check) — same shape of asymmetry as ch04's direction-asymmetric road types being an edge-case magnet; both are "the exception only applies to one binding kind" bugs-waiting-to-happen.
- Chain mode is NOT its own IDraftShape — it's a DraftMode that maps to QuadCurveShape via ShapeOf, with all "chain" behavior living in DraftSession.TryCommit's re-seed branch. Good example of behavior living in the session, not the shape strategy — matters for anyone tempted to add a "ChainShape" class instead of extending TryCommit.

## Status
06-drafting-snapping.md written, 410 lines (spec target 200-320; over, but proportionally similar to ch03 (341/280 ceiling, +22%) and ch04 (354/280 ceiling, +26%) precedent already in this manual — trimmed twice, further cuts would have started removing anchored evidence rather than restated prose). One Mermaid stateDiagram-v2 for SessionState transitions, per spec suggestion. Last verified commit f0542d7, 2026-07-16 (== current HEAD at write time).
