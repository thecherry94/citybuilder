# Undo/Redo + Upgrade-in-Place — Design (Milestone 7)

**Date:** 2026-07-17
**Status:** Approved (autonomous run pre-authorized by user: spec → plan → implementation
→ certification without further check-ins. Scope = roadmap M7: undo/redo, upgrade-in-
place preserving junction configs, plus the top M7-backlog bug — `TryHealNode` one-way
reversal — which this milestone's fuzzing would otherwise trip over.)

## Motivation

CS2's most-loved editing comforts: mistakes are one keystroke away from undone, and a
road's type or direction changes without redrawing it (junction configuration intact).
Our editor has neither — a mis-click means bulldoze-and-redraw, and retyping a road
loses its junction overrides because the only path is remove+add with fresh `EdgeId`s.

## Non-goals

- No invertible-delta undo (see Approach). No undo of camera, tool mode, snap toggles,
  or traffic state — network only (vehicles resync exactly like quickload).
- No undo history UI (list/preview); just Ctrl+Z / Ctrl+Y + toolbar buttons + flash.
- No multi-edge box-select upgrade; single edge per click (CS2 parity).
- No CS2 "left/center/right widening" placement on width change — the curve stays; the
  road widens symmetrically around it.
- Deferred M7-backlog items stay deferred: full-drop `JunctionConfig` loss via `Prune`
  on EdgeId churn (retype avoids the churn instead), node-collapse `DroppedSegments`
  bypass, `SaveLoad.ValidateGame` counter bounds.

## Approach: snapshot undo (chosen over invertible deltas)

The roadmap sketched "invertible NetworkDeltas"; this design deliberately chooses
**snapshots via `SaveLoad`** instead:

- `Save → LoadInto` is **byte-stable-certified** (fuzzer round-trips every 10 actions,
  30k-action soak at M6.75) and battle-tested by F5/F9 quickload including the traffic
  resync path. Snapshot undo inherits all of that for free.
- Invertible deltas must correctly invert commit-with-splits, node reuse/absorption,
  and heals — precisely the seams where the fuzzer keeps finding real bugs. High risk,
  zero user-visible benefit at editor scale.
- Cost: one JSON string per checkpoint (~100 KB at 500 edges; capacity 50 ≈ 5 MB) and
  a full `LoadInto` per undo (quickload-speed, instant in practice). A perf guard test
  pins checkpoint+undo cost on a 480-edge grid.
- Bonus: fuzzing undo/redo *is* the "edit a restored network" coverage the M6 known
  limits flagged as missing — every post-undo action edits a `LoadInto`-restored graph.

## 1. `UndoStack` (`src/Domain/Persistence/UndoStack.cs`)

```csharp
public sealed class UndoStack(RoadNetwork network, int capacity = 50)
{
    public int UndoCount { get; }   // snapshots available to undo into
    public int RedoCount { get; }
    public void Checkpoint();       // call BEFORE a mutation
    public bool Undo();             // false when empty
    public bool Redo();
}
```

- `Checkpoint()` pushes `SaveLoad.Save(network)` onto the undo stack and clears the
  redo stack — **deduped by `RoadNetwork.Version`**: if the top snapshot was taken at
  the current version (nothing changed since), it skips the push. Call sites may
  therefore checkpoint optimistically before operations that can fail (a rejected
  commit leaves Version unchanged → the next checkpoint replaces nothing, no junk
  entries). Capacity trims the oldest.
- `Undo()`: if empty → false; else push `Save(network)` to redo, `LoadInto` the popped
  snapshot, return true. `Redo()` mirrors. Both leave the stacks consistent when
  called repeatedly (undo-all then redo-all restores the exact byte sequence).
- Owner of side-effects stays the caller: after a successful `Undo/Redo`, the game
  layer runs the same resync as quickload (`TrafficSim.EnsureSynced`,
  `ToolController.ClearTransientState`, flash).

**Checkpoint call sites (game layer, all pre-mutation):**
- `DraftSession` gains `public event Action? BeforeCommit;` fired in `TryCommit`
  immediately before `network.Commit` (the only mutation the session performs).
  `ToolController.Bind` wires it to `Checkpoint`.
- Bulldoze: `ToolController` checkpoints before `RemoveEdge`.
- Junction editing: `JunctionPanel` gets the `UndoStack` at bind time and checkpoints
  before each `ConfigureJunction` apply.
- Upgrade tool: checkpoint before `RetypeEdge` / `FlipEdge`.

## 2. Upgrade-in-place (`RoadNetwork.RetypeEdge` / `FlipEdge`)

```csharp
public enum RetypeError { UnknownEdge, SameType, TooShort, TooTight }
public RetypeError? RetypeEdge(EdgeId id, RoadTypeId newType);  // null = success
public bool FlipEdge(EdgeId id);                                // false = unknown edge
```

- **`RetypeEdge` mutates in place — `EdgeId` survives**, which is the whole point:
  `JunctionConfig` role overrides and leg offsets are keyed by `EdgeId` and pruned on
  churn; preserving the id preserves the player's junction authoring. Validation
  before mutating: new type's `MinSegmentLength` vs the curve's length (`TooShort`),
  new type's `MinRadius` vs `BezierOps.MinRadius` (`TooTight`). On success: set
  `Type`, regenerate `Lanes` from the new catalog profile (fresh `LaneId`s — vehicles
  on the edge are dropped by `TrafficSim.Sync`'s removed-lane path, same as CS2
  despawning on replace), rebuild derived data on both end nodes, `Version++`,
  `Changed` with the edge in a new **`NetworkDelta.EdgesChanged`** set.
- **`FlipEdge`**: reverse the curve (`P3,P2,P1,P0`), swap `StartNode`/`EndNode`,
  regenerate lanes, rebuild both nodes, `EdgesChanged` delta. Valid for every type
  (symmetric types just re-derive identically); its UI is only surfaced in Upgrade
  mode. No validation can fail beyond edge existence.
- **`NetworkDelta` gains `EdgesChanged`** (default empty set for existing call sites).
  `RoadNetworkView` re-meshes edges in `EdgesChanged` exactly like `EdgesAdded`;
  `LaneDebugOverlay`/`TrafficView` already key off node rebuilds + `Sync`.
- `RoadEdge.Type` (and lane array) become internally settable; no public mutability.

## 3. `TryHealNode` one-way fix (top M7-backlog bug)

Today (`RoadNetwork.cs:588-611`) healing a degree-2 node checks type equality only;
merged orientation follows `HashSet` enumeration order — bulldozing the third arm off
a one-way chain can heal it **backwards** (invariant-legal, fuzzer-invisible).

Fix, minimal and safe:
- For **direction-asymmetric** types (`RoadType.IsDirectionAsymmetric`): heal only
  when flow is continuous through the node — exactly one edge *ends* at the node and
  the other *starts* there. Both-in or both-out: keep the node (correct — the flows
  genuinely oppose; a healed edge cannot represent them).
- Order the merge in flow direction: upstream edge first, so the merged curve runs
  far(upstream) → far(downstream); assert the fitted curve's endpoints match.
- Symmetric types keep today's behavior (orientation is semantically irrelevant), but
  the merge is made deterministic anyway (order by `EdgeId`) so heals stop depending
  on `HashSet` iteration order — reproducibility hygiene.
- `HealingTests` gains OneWay coverage: chain heals forward (never reversed, asserted
  via lane directions/curve tangent), opposing one-ways at a node never heal,
  deterministic orientation for symmetric types.

## 4. Game layer

- **`ToolMode.Upgrade`**: hover highlights the edge under the cursor (bulldoze-style);
  LMB = checkpoint + `RetypeEdge(hover, current toolbar type)` — flash the error name
  on `TooShort`/`TooTight`/`SameType`; RMB = checkpoint + `FlipEdge` (RMB routes to
  flip only in Upgrade mode; StepBack is meaningless there). Toolbar gains "Upgrade"
  in the mode row. Audio: retype/flip play the commit plop; failures the reject blip
  (existing `AudioFx`, no new sounds).
- **Undo bindings**: Ctrl+Z undo, Ctrl+Y redo (`Main._UnhandledInput`, beside F5/F9);
  toolbar "Undo/Redo" buttons beside Save/Load. After a successful undo/redo, Main
  runs the quickload resync and flashes "Undone"/"Redone"; empty stacks flash
  "Nothing to undo/redo". Bulldoze sound is NOT played on undo (it's a restore, not
  an edit).
- Hint line gains "Ctrl+Z undo · Ctrl+Y redo".

## 5. Fuzzer & quality stack

- `GestureFuzzer` creates an `UndoStack`, checkpoints before every mutating action
  (draw commit, bulldoze, configure, retype, flip), and the action mix gains:
  **retype** (random edge → random type, failures expected and fine), **flip**
  (random edge), and **undo/redo bursts** (1–3 steps). Invariants + burst + round-trip
  checks unchanged — they now run against restored-then-edited networks, closing the
  M6 "post-load-edit seams untested" gap. Redistributed mix: draw 45, bulldoze 15,
  configure 10, retype 8, flip 5, undo/redo 7, snap toggles 5, stepback/cancel 5.
- New domain tests: `UndoStackTests` (checkpoint dedup by version, undo/redo byte
  round-trip, capacity trim, redo cleared on new checkpoint, empty-stack falses,
  undo-all/redo-all byte equality), `RetypeTests` (config preserved across retype,
  `TooShort`/`TooTight`/`SameType`, lanes match new profile, delta carries
  `EdgesChanged`, view-relevant version bump), `FlipTests` (curve reversed, nodes
  swapped, one-way lane directions consistent), `HealingTests` one-way additions (§3),
  and a perf guard: checkpoint + undo on a 480-edge grid under 100 ms each.
- Smoke: retype a grid edge to Street, flip a one-way loop edge (connectivity must
  survive — direction flip on a 3-edge loop breaks strong connectivity, so flip it
  back; asserts both transitions), then undo twice and assert edge count/type
  restored. UITEST: upgrade an edge via the tool + Ctrl+Z path, screenshot includes
  the Upgrade button.
- DoD: 3×10k cert fuzz, KPI rerun + `docs/health/M7.md`, manual drift (ch02 heal/
  retype/flip, ch08 undo, 00 overview, glossary), conventions constants, roadmap.

## Implementation notes (deltas from this spec, recorded at ship time)

- **`RoadEdge` stayed fully immutable** — retype/flip swap a *new* `RoadEdge` with the
  same `EdgeId` into the dictionaries (`ReplaceEdgeInPlace`); node `EdgeSet`s hold ids
  so no visibility changes were needed at all (the spec's "internally settable"
  clause turned out unnecessary).
- **Retype-under-traffic coverage** is provided by the fuzzer's burst checks running
  after retype actions (every 25 actions) rather than a bespoke spawn-on-retyped-edge
  test — same seam, standing coverage.
- The heal-fix code landed inside the T2/T3 commit (`9d61778`) with its tests in
  `c1dc452` — a background-gate commit boundary slip, content fully tested either way.
- Smoke's flip scenario asserts both directions: a flipped one-way loop edge *breaks*
  strong connectivity (expected), flip-back restores it, and undo replays the broken
  state — the connectivity gate doubles as flip evidence.

## Testing seams / risks

- **Undo across traffic**: vehicles are not part of snapshots (same as quicksave);
  `EnsureSynced` purges strandees — already regression-covered by the UITEST
  save/load scenario.
- **`LoadInto` counter restore** guarantees no id collisions after undo (ids continue
  from saved counters; fuzz round-trips assert byte-stability which subsumes this).
- **Retype under traffic**: dropped vehicles on regenerated lanes — `TrafficSim.Sync`
  removed-lane path, covered by a new burst-after-retype fuzz reachability plus an
  explicit test spawning a vehicle on a retyped edge.
- The known `TrafficSim.Sync` same-`LaneId`-shrunk-run gap (M6 limits) is unaffected:
  retype/flip always change `LaneId`s.
