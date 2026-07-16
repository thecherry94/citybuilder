# Ch08 notes — Persistence

## Terminology (candidates for glossary.md)
- **Round-trip contract**: `Save(Load(Save(n))) == Save(n)` byte-equal. Chosen over
  structural-equality assertions because it needs no comparer and is the fuzzer's
  oracle (GestureFuzzer.cs:137-149, every 10 actions).
- **Validate-before-mutate**: RoadNetwork.Persistence.cs — ValidateGame runs entirely
  before BeginBatch(). Same discipline could be named/cross-referenced if other
  mutation entry points (e.g. a future import/undo feature) adopt it.
- **Byte-stable ordering**: sort-by-id + non-indented JSON + shared JsonSerializerOptions.
  Same "why determinism" reasoning could recur if any other subsystem serializes
  dictionaries (traffic KPI export? health report JSON?) — worth a cross-ref if ch05/
  KPI chapter touches JSON output.

## Forward-refs planted (need back-links when those chapters are read/revised)
- ch. 02 (RoadCatalog.Get) — referenced for lane specs on load.
- ch. 03 (JunctionBuilder) / ch. 04 (ConnectorBuilder) — referenced as what
  RebuildDerived recomputes; ch04 already exists and could link back to ch08 as "how
  this output gets thrown away and rebuilt on load."
- ch. 05 (TrafficSim, TrafficSpawner ambient respawn) — not yet written per manifest
  (in-progress). When ch05 is authored, cross-link: TrafficSim.Sync (ch08 explains the
  EnsureSynced call site and stale-vehicle purge via RunUiTest scenario), and
  TrafficSpawner's ambient respawn behavior (ch08 only asserts it exists per spec,
  didn't read TrafficSpawner.cs itself — out of ch08's scope).

## Open questions / uncertain
- No formal [UNCERTAIN] markers were needed — every claim in ch08 was directly
  traceable to source (SaveGame.cs, SaveLoad.cs, RoadNetwork.Persistence.cs,
  RoadNetwork.cs batch machinery, Main.cs, ToolController.cs, SaveLoadTests.cs,
  GestureFuzzer.cs, design spec, roadmap.md) or to a real generated JSON sample
  (built via a throwaway scratch console app referencing Domain.csproj, run in
  /tmp, deleted after — not left in the repo).
- Genuinely unverified without running code: whether TrafficSpawner's ambient
  respawn produces vehicle *counts* comparable to pre-load state, or just "some"
  traffic. Deferred to ch05 since it's out of scope here; ch08 only cites the design
  spec's own wording ("ambient traffic respawns after load").
- The "negative-counter nit" (ValidateGame never bounds NextNode/NextEdge/NextLane
  themselves, only entity ids against them) is a real gap I found by reading the
  validation code, not something documented elsewhere — flagged as Known limits in
  ch08, not filed as a bug (out of scope for a manual-authoring task per instructions).

## Patterns confirmed consistent with earlier chapters
- Manifest-driven "Last verified against commit" stamp format matches ch01-04.
- Style: dense prose paragraphs with file:line anchors inline, not footnoted;
  matches ch04's density best (both are "the most careful chapter" for their
  respective subsystem's bug history — ch04's connector arrow bug vs ch08's
  null-guard fix / byte-stability discipline).
- Chapter came in at 372 lines — longer than the plan's 150-250 target but in the
  same range as ch01-04 (341-365 lines). Consistency with sibling chapters weighted
  over the raw target since the manual reads as one book.
