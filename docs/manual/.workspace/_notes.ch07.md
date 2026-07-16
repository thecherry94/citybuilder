# Notes — ch. 07 rendering & markings

Status: chapter written, 320 lines, verified f0542d7 2026-07-16. Manifest NOT updated
(instructed not to touch other files) — controller should flip 07 to done.

## Terminology to fold into glossary
- **Resync pattern**: bind once to a domain object + subscribe to `Changed`, cache
  Godot nodes keyed by domain id, never hold state the domain didn't already have.
  Reference impl: `RoadNetworkView`. Every other view in `src/Game` copies this shape.
- **Cut point (`CutT`)**: parametric t where an edge's rendered asphalt stops and the
  junction polygon begins. Already introduced in ch. 03; ch. 07 leans on it for the
  "dirty every edge touching a changed node" rule.
- **Corner continuation**: `JunctionMarkings.AddCornerContinuation` — degree-2 node
  marking sweep along a per-line corner quadratic (own construction, mirrors curb
  returns from ch. 01/02 domain-geometry gotchas).

## Cross-refs planted (need back-links when those chapters are (re)touched)
- ch. 02 → NetworkDelta/Changed batching (already exists, ch07 cites RoadNetwork.cs:34
  and the RestoreInto persistence path).
- ch. 03 → JunctionGeometry/CutT/SurfacePolygon/Corners, JunctionControl.Resolve role
  (Main/Yield/Stop) driving stop-line vs yield-teeth vs clean paint.
- ch. 04 → ConnectorBuilder turn-lane assignment feeding JunctionMarkings turn arrows
  (ArrowGlyph moves set) and the "never trust |Offset|" lesson MarkingRules also obeys.
- ch. 05 → TrafficSim.Pose/PhaseFor consumed by TrafficView/SignalLampView; the
  front-bumper-vs-render-center convention (gotchas.md) that Pose insulates the view from.
- ch. 06 → DraftSession/ToolController drive GhostView + RoadNetworkView.HighlightEdge;
  worked example's T-junction edit originates here.
- ch. 08 → RestoreInto/LoadInto (RoadNetwork.Persistence.cs:20) is why quick-load needs
  no view rebinding; Main.QuickLoad's EnsureSynced/ClearTransientState calls are the one
  place non-domain transient state must resync manually.

## Open questions / uncertain flagged in-chapter (count: 1)
- [UNCERTAIN] whether current milestone traffic densities can realistically hit
  TrafficView's 1024-vehicle MultiMesh cap — flagged in Known limits, pointed at
  docs/verification.md's M6 KPI/fuzz targets for whoever checks next.

## Observations (patterns worth a cross-chapter callout, not yet in _observations.md
since that file was empty at session start — leaving it untouched per scope, but
flagging here in case Phase 6 controller wants to promote these)
- The "never share a MaterialOverride instance across independently-tinted things"
  gotcha appears twice in this chapter's source (SetEdgeTint's own doc comment,
  RoadNetworkView.cs:129-132) — worth promoting to docs/gotchas.md verbatim since it's
  not there yet (chapter attributes it as "consistent with its spirit" rather than a
  literal existing entry).
- SetEdgeTint/ClearTints (M6, speed heatmap) has zero gameplay UI wiring — only
  exercised by VisualShots.cs:583-594. Worth flagging to the M6 task owner if a player-
  facing heatmap toggle is expected before milestone close.
- GridOverlay's alpha-transparency dependency on Materials.DebugLines is real but not
  yet in docs/gotchas.md verbatim — chapter calls this out explicitly as a gap.

## Things skimmed but not deep-dived (in case a later chapter needs them)
- ToolController.cs: only grepped for _ghost/_view call sites (HighlightEdge, ghost
  Clear/Show) — full input-handling logic belongs to ch. 06, not read here.
- Main.cs: read lines 1-200 (scene wiring + quicksave/load) not the full 455 lines;
  UI wiring (Toolbar/JunctionPanel) below that point wasn't read.
- VisualShots.cs: read only lines 1-50 and 575-604 (bind/run header + the heatmap
  scenario) per scope ("harness only, 2 paragraphs max"). Full scenario list (~600
  lines) unread.
