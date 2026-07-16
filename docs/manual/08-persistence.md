# Persistence

Persistence exists to answer one question honestly: after a player saves and reloads,
is it the *same city*? The domain answers this by storing the minimum graph data
needed to rebuild everything else, restoring ids verbatim so nothing outside the
network (a UI selection, a save-scummed screenshot diff, a future scripting API) is
left holding a reference to something that quietly became a different object, and
proving the round trip byte-for-byte rather than merely "close enough." The mechanism
is deliberately boring — a versioned JSON DTO, one validate-then-mutate entry point,
one `Changed` event — because save/load is exactly the kind of code where cleverness
buys bugs: a half-applied load that leaves the network in a hybrid state is worse than
one that never started.

## At a glance

- **Source:** `src/Domain/Persistence/SaveGame.cs` (DTOs, 23 lines),
  `src/Domain/Persistence/SaveLoad.cs` (serialize/deserialize entry points, 92 lines),
  `src/Domain/Network/RoadNetwork.Persistence.cs` (`RestoreInto`, validation, DTO↔domain
  conversion, 167 lines).
- **Entry points:** `SaveLoad.Save(RoadNetwork) : string`, `SaveLoad.Load(string) :
  RoadNetwork`, `SaveLoad.LoadInto(string, RoadNetwork)`.
- **Called by:** `Main.QuickSave`/`Main.QuickLoad` (`src/Game/Main.cs:146-185`), wired
  to F5/F9 and the toolbar's Save/Load buttons (`src/Game/Toolbar.cs:138-142`); the
  gesture fuzzer's periodic round-trip check (`tests/Domain.Tests/Fuzzing/GestureFuzzer.cs:137-149`).
  Not used by anything mid-simulation — this is an edit-mode operation only.
- **Depends on:** `RoadCatalog.Get` ([ch. 02](02-network-validation.md)) for road-type lane specs on load,
  `JunctionBuilder`/`ConnectorBuilder` ([ch. 03](03-junctions-control.md), [ch. 04](04-lane-graph-connectors.md)) via `RoadNetwork.RebuildDerived`
  to regenerate everything the save file doesn't store.
- **Used by:** `TrafficSim.EnsureSynced` and `ToolController.ClearTransientState`
  resync after every `QuickLoad` (see below); nothing else in the domain reaches into
  persistence directly.
- **Last verified against commit:** `f0542d7` on 2026-07-16.

## The format

`SaveGame` (`SaveGame.cs:10-22`) is a flat set of C# `record`s used purely as a JSON
DTO — never the live domain types, so a `System.Text.Json` shape change here can never
accidentally leak into `RoadNetwork`'s actual object graph:

```csharp
public sealed record SaveGame(int FormatVersion, NodeDto[] Nodes, EdgeDto[] Edges,
    int NextNode, int NextEdge, int NextLane);
public sealed record NodeDto(int Id, float X, float Y, float Z, ConfigDto Config);
public sealed record ConfigDto(int Mode, float SizeOffset, RoleDto[] Roles, LegOffsetDto[] LegOffsets);
public sealed record EdgeDto(int Id, int Start, int End, int Type,
    float[] Curve /* 12 floats: P0..P3, each X,Y,Z */, int[] LaneIds /* catalog order */);
```

What's stored is exactly what cannot be recomputed from anything else: node id and
position, each node's `JunctionConfig` (control mode, size offset, and any per-leg
role/offset overrides — most nodes have none, hence the usually-empty `Roles`/
`LegOffsets` arrays), and each edge's id, endpoint node ids, `Bezier3` control points,
road-type id, and lane ids. What's *never* stored — junction polygons, corner zones,
lane connectors, conflict points — is exactly the data `RoadNetwork.RebuildDerived`
(`src/Domain/Network/RoadNetwork.cs:715-721`) regenerates from the above on every
load, the same way it regenerates them after any ordinary edit. Storing derived data
would both bloat the file and create a desync risk: if `ConnectorBuilder`'s algorithm
changes between the save and a later load, a stored-and-restored connector list would
silently disagree with what the current code would build for the same graph.

**Lane ids are the one exception saved verbatim rather than recomputed**, and the
reason is the crux of the whole format: `AddEdgeInternal` normally mints fresh
`LaneId`s off the network's counter (`RoadNetwork.cs:625-628`), so a naive "just
rebuild lanes from the catalog" reload would hand every lane a *new* id. Anything that
held a `LaneId` from before the load — a spawned vehicle's current lane, a saved
route, a UI selection — would silently point at nothing (or worse, at an unrelated
lane that happens to reuse the number). `EdgeDto.LaneIds` (`SaveGame.cs:22`) records
each edge's lane ids in the catalog's own enumeration order
(`ToEdgeDto`, `SaveLoad.cs:78-91`: `edge.Lanes.Select(l => l.Id.Value)`), and
`RestoreInto` reconstructs each `Lane` by pairing that stored id with the *i*-th
`LaneSpec` from `RoadCatalog.Get(edge.Type).Lanes` (`RoadNetwork.Persistence.cs:59-65`)
— same offset/direction/width/kind the catalog would produce fresh, but the identity
that matters to the rest of the system stays put.

**Version guard.** `SaveGame.FormatVersion` is compared against `SaveLoad.FormatVersion`
(currently `1`, `SaveLoad.cs:13`) before anything else happens: a save with a *higher*
version throws `SaveFormatException` immediately (`SaveLoad.cs:45-47`) — this build is
older than the file and cannot know what fields it's missing. A save with an *equal or
lower* version is accepted; there is no migration path yet (see Known limits), so in
practice "lower" is currently a no-op since `1` is the only version that has ever
existed.

**Byte-stable ordering.** `Save` is deterministic by construction, not by luck:
`ToSaveGame` sorts nodes and edges by id (`SaveLoad.cs:54-61`,
`.OrderBy(x => x.Id.Value)`), and `ToConfigDto` sorts `RoleOverrides`/`LegOffsets`
dictionary entries by edge id (`SaveLoad.cs:71-76`) before projecting them to arrays —
`Dictionary<K,V>` enumeration order is not contractually stable in .NET, so skipping
this sort would make two saves of the identical graph differ byte-for-byte depending
on insertion history. `JsonSerializerOptions.WriteIndented = false` (`SaveLoad.cs:15-18`)
and a single shared `Options` instance close the loop: no whitespace variance, no
per-call option drift.

## The round-trip contract

`SaveLoadTests.RoundTripIsByteStableAndStructurallyIdentical`
(`tests/Domain.Tests/Persistence/SaveLoadTests.cs:13-38`) and the fuzzer's periodic
check both assert the same thing: `Save(Load(Save(n)))` is byte-equal to `Save(n)`.
Byte equality is a stronger and cheaper claim than "structurally equivalent" — it
needs no custom deep-comparison logic (a plain string `==`), it is trivially
diff-friendly when a regression does appear (`diff` on two save files points straight
at the changed field), and it is what makes the file format usable as a fuzzer oracle:
`GestureFuzzer` calls it every 10 actions
(`FuzzOptions.RoundTripEvery = 10`, `GestureFuzzer.cs:10,137-149`) across thousands of
randomly-generated networks, and any asymmetry between what `Save` writes and what
`Load` reconstructs shows up as a hard failure without a single hand-written fixture
for the specific graph shape that broke it.

What byte-equality guarantees: every field the format claims to preserve — ids,
positions, curve control points, road types, lane ids, junction configs, the id
counters — really does survive a save/load cycle unchanged, for arbitrary graphs the
fuzzer can construct. What it does *not* guarantee: that anything the format doesn't
capture (see "what's not saved" below) survives, or that the *derived* data
(connectors, junction geometry) is identical to what existed before the save — it's
only guaranteed to be *whatever `RebuildDerived` currently produces* for the restored
graph, which is provably consistent with the graph but not provably identical to a
stale computation from before. The domain-basics test asserts this weaker but still
meaningful property directly: `Assert.Equal(node.Connectors.Count,
lnode.Connectors.Count)` (`SaveLoadTests.cs:38`) — count parity, not object identity.

## RestoreInto

`RoadNetwork.RestoreInto(SaveGame)` (`RoadNetwork.Persistence.cs:20-81`) is `internal`
and lives in a partial class specifically so it can reach `_nodes`, `_edges`,
`_nextNode`/`_nextEdge`/`_nextLane`, and `_batch` — `RoadNetwork`'s private mutation
state — directly, rather than through a public API surface that would have to expose
"replace everything and reassign these exact ids" as a first-class operation nobody
else should ever call. This is deliberate private-field surgery, justified the same
way `RoadNetwork.cs`'s own low-level `AddNodeInternal`/`RemoveEdgeInternal` helpers are:
load is not an edit, it's a controlled reset, and it reuses the *same* internal removal
path (`RemoveEdgeInternal`, `RoadNetwork.cs:638-646`) normal bulldoze operations use so
the batch machinery records departures the same way — except node removal during a
load is inlined directly (`RoadNetwork.Persistence.cs:32-37`) rather than going through
`HandleNodeAfterRemoval`/`TryHealNode`, because restoring a snapshot must never trigger
the two-edge-merge healing that ordinary bulldozing relies on — a saved dead-end node
should reappear as a dead-end node, not get silently merged away.

**Validate before mutate — the whole point of `ValidateGame`.** Every check
`ValidateGame` (`RoadNetwork.Persistence.cs:83-154`) performs runs *before*
`RestoreInto` calls `BeginBatch()` (line 24, right after the `ValidateGame(game)` call
on line 22). This ordering is the entire safety property of the load path: if
validation throws, `RestoreInto` has touched nothing — no `_nodes`/`_edges` mutation,
no batch opened, and therefore no `Changed` event fired, ever. The comment at
`RoadNetwork.Persistence.cs:85-87` names the specific bug class this guards against:
`System.Text.Json` deserializes an absent or explicit-`null` JSON field into `null`
even for a non-nullable C# record property (nullability annotations are a compile-time
convention the deserializer doesn't enforce), so every reference-typed field —
`Nodes`, `Edges`, each `NodeDto.Config`, `ConfigDto.Roles`, `ConfigDto.LegOffsets`,
each `EdgeDto.Curve`/`LaneIds`, and every array element — gets an explicit null guard
here rather than being trusted to the mutation code, where a `NullReferenceException`
mid-batch would leave the network half-cleared. `SaveLoadTests.NullLaneIdsThrowsAndLeavesTargetNetworkUntouched`
(`SaveLoadTests.cs:80-100`) is the regression pin for exactly this: a payload with
`"LaneIds":null` throws `SaveFormatException` and the target network's own
`SaveLoad.Save` output is byte-identical to what it was before the attempted load, with
zero `Changed` events observed.

Beyond null-guarding, `ValidateGame` checks referential integrity end to end: node ids
are unique and strictly below `NextNode`; edges reference only node ids that exist in
this same payload; each edge's road type resolves in `RoadCatalog` (an unknown type id
throws rather than letting a later `RoadCatalog.Get` throw `KeyNotFoundException`
uncaught); `LaneIds.Length` matches that type's lane count exactly; and every lane id
across the whole payload is unique and strictly below `NextLane`. That "strictly below
the counter" check is doing double duty — it's a sanity bound (an id can't be at or
past the value the *next* mint would produce) and it's what makes counter restoration
safe: after `RestoreInto` sets `_nextNode`/`_nextEdge`/`_nextLane` from the save file
verbatim (`RoadNetwork.Persistence.cs:76-78`), the very next `AddNodeInternal`/
`AddEdgeInternal` call is guaranteed to mint an id no restored entity is using, because
validation already proved every restored id sits strictly below its counter.

**One batch, one event.** `RestoreInto` wraps the whole replace-everything sequence —
removing every current edge and node, re-adding every saved node then every saved edge,
resetting the three counters — between a single `BeginBatch()`/`EndBatch()` pair
(lines 24 and 80). `EndBatch` (`RoadNetwork.cs:659-682`) calls `RebuildDerived` once
per touched node and fires exactly one `Changed` event describing the whole delta.
`SaveLoadTests.LoadIntoReplacesInPlaceWithOneChangedEvent`
(`SaveLoadTests.cs:41-53`) pins this: subscribing to `Changed` before a `LoadInto` call
and counting invocations yields exactly `1`, regardless of how many nodes/edges
changed. This matters for anything downstream that treats `Changed` as "something is
different, go recompute" rather than "here is exactly what changed" — a view or cache
that did expensive work per event would do it once per load, not once per replaced
entity.

## In-game quick save/load

`Main.QuickSave`/`Main.QuickLoad` (`src/Game/Main.cs:146-185`) are bound to `F5`/`F9`
in `_UnhandledInput` (`Main.cs:134-139`) and to two toolbar buttons that call the same
methods (`Toolbar.cs:138-142`) — there is exactly one save/load code path regardless of
which UI element triggered it. Both write to a single fixed slot,
`user://saves/quick.json` (`Main.cs:14`), resolved to a real filesystem path via
`ProjectSettings.GlobalizePath` — Godot's per-OS user-data directory convention, so the
same code works unmodified across platforms. `QuickSave` creates the containing
directory if needed and catches `IOException`/`UnauthorizedAccessException` around the
write, flashing `"save failed: {message}"` through `StatusFlashed` rather than letting
a permissions or disk-full error crash the session (`Main.cs:148-158`). `QuickLoad`
checks `File.Exists` first and flashes `"No quick save"` if the slot is empty
(`Main.cs:167-171`), then wraps the actual load in a catch for `SaveFormatException`
alongside the same I/O exceptions — a malformed or newer-version file flashes its
`SaveFormatException` message directly to the player rather than crashing
(`Main.cs:181-184`). Both success paths flash a one-word confirmation
(`"Saved"`/`"Loaded"`).

`QuickLoad`'s two lines after `SaveLoad.LoadInto` are not optional cleanup — they are
the game layer's half of the round trip, because `RestoreInto`'s single `Changed`
event only reaches subscribers that actually listen for it, and two pieces of game
state don't:

- **`_traffic.EnsureSynced()`** (`Main.cs:175`) forces `TrafficSim.Sync()`
  (`src/Domain/Traffic/TrafficSim.cs:108,465-...`) to run immediately rather than
  waiting for the next `Tick`. `Sync` is version-gated (`_syncedVersion ==
  _network.Version` short-circuits, `TrafficSim.cs:467-468`) and `RestoreInto` already
  bumped `Version` inside `EndBatch`, so simply *not* calling `EnsureSynced` would still
  self-correct on the next tick — but only while the sim is actually ticking.
  `TrafficEnabled` can be off (e.g. the player is paused, or mid-edit), and the UI test
  and junction/signal views expect up-to-date lane/connector caches even while paused,
  so `EnsureSynced` forces the resync deterministically at load time instead of leaving
  it to chance. This is also where stale vehicles get reconciled against a network that
  may have entirely different lanes now — `Sync` rebuilds `_lanes`/`_runs`/
  `_connectorLength` from scratch and the RunUiTest flow demonstrates a vehicle whose
  lane vanished on load being purged rather than left dangling (see Worked example
  below for the exact scenario).
- **`_controller.ClearTransientState()`** (`Main.cs:178`, defined at
  `src/Game/ToolController.cs:231-240`) drops the active draft, the ghost preview, the
  hovered-edge highlight, bulldoze/spawn click targets, and the currently-inspected
  node. The comment at `ToolController.cs:227-230` states the reason plainly: surviving
  ids after a load may describe *entirely different geometry* than before, because
  `RestoreInto` doesn't preserve "the same objects, updated" — it tears down and
  rebuilds the whole graph. A tool mid-gesture that kept referencing a pre-load edge id
  could silently operate on a coincidentally-reused id belonging to an unrelated new
  edge; dropping all transient references is cheaper and safer than trying to prove
  each one is still valid.

**What is not saved.** Vehicles and their trips are entirely absent from `SaveGame` —
there is no `VehicleDto`, no reference to `TrafficSim` anywhere in the persistence
code. This is a deliberate v1 scope cut, not an oversight: the design spec states it
explicitly (`docs/superpowers/specs/2026-07-15-quality-stack-design.md:23-24,86-89`,
also `docs/roadmap.md:86`) — "no vehicle/traffic state in save v1 (ambient traffic
respawns after load; manual trips are lost)". Concretely: any vehicle spawned via the
two-click spawn tool is gone after a `QuickLoad`, whether or not its road survived the
load; `TrafficSpawner`'s ambient background traffic (out of this chapter's scope, see
[ch. 05](05-traffic-sim.md)) simply respawns new vehicles on whatever roads exist post-load, so a loaded
scene doesn't look empty, but it is not the *same* traffic that was there at save time.

## Worked example

A minimal network — one 40 m `TwoLane` road between two nodes at default junction
settings — produces exactly this `Save` output (line-wrapped here for readability; the
real file has no whitespace, per the byte-stability rule above):

```json
{
  "FormatVersion": 1,
  "Nodes": [
    {"Id": 1, "X": 0, "Y": 0, "Z": 0,
     "Config": {"Mode": 0, "SizeOffset": 0, "Roles": [], "LegOffsets": []}},
    {"Id": 2, "X": 40, "Y": 0, "Z": 0,
     "Config": {"Mode": 0, "SizeOffset": 0, "Roles": [], "LegOffsets": []}}
  ],
  "Edges": [
    {"Id": 1, "Start": 1, "End": 2, "Type": 1,
     "Curve": [0,0,0, 13.333333,0,0, 26.666668,0,0, 40,0,0],
     "LaneIds": [1, 2]}
  ],
  "NextNode": 3, "NextEdge": 2, "NextLane": 3
}
```

Field by field: `FormatVersion: 1` matches `SaveLoad.FormatVersion` exactly (equal, not
lower, since this file was just produced by this build). The two `NodeDto`s are the
straight road's endpoints at `(0,0,0)` and `(40,0,0)` (Y is up per the project's XZ
ground-plane convention, [ch. 01](01-geometry.md)) with `Mode: 0` — `JunctionControlMode.Auto`
(`src/Domain/Network/JunctionControl.cs:6`, the default a fresh node gets) and empty
override arrays, since neither node has a manually configured role or leg offset. The
one `EdgeDto` has `Type: 1` (`RoadCatalog.TwoLane.Id`, `RoadType.cs:60`) and a `Curve`
that is a straight line masquerading as a Bezier — `Bezier3.Line` places `P1`/`P2` at
the 1/3 and 2/3 points along the segment, which is why the middle two triples are
`13.333333`/`26.666668` rather than clustered at the endpoints. `LaneIds: [1, 2]`
corresponds one-to-one with `TwoLane`'s two `LaneSpec`s in catalog order — lane `1` is
the `+1.75` m `Forward` lane, lane `2` the `-1.75` m `Backward` lane
(`RoadType.cs:61-65`) — reconstructed on load by pairing these exact ids with those
exact specs (`RoadNetwork.Persistence.cs:59-65`). The three counters — `NextNode: 3`,
`NextEdge: 2`, `NextLane: 3` — are each one past the highest id their own pool has
minted so far: two nodes minted (`1`, `2`) → next node is `3`; one edge minted (`1`) →
next edge is `2`; two lanes minted (`1`, `2`) → next lane is `3`. They are independent
counters, not a shared sequence — the next edit after loading this file would add a
third node with id `3`, a second edge with id `2`, and two more lanes with ids `3` and
`4`.

## Invariants

- Every id referenced anywhere in a save file (node, edge, or lane) is strictly
  positive and strictly below its own type's `Next*` counter — enforced by
  `ValidateGame` before any mutation, and it is what makes counter restoration safe
  (see RestoreInto above).
- `Save(Load(Save(n)))` is byte-equal to `Save(n)` for any `RoadNetwork` the domain can
  construct — the fuzzer's standing regression guard (`GestureFuzzer.cs:137-149`), run
  every 10 fuzzed actions across generated seeds.
- A failed `LoadInto` call leaves its target network byte-identical to its pre-call
  state and fires zero `Changed` events (`NullLaneIdsThrowsAndLeavesTargetNetworkUntouched`,
  `SaveLoadTests.cs:80-100`) — validation failures never partially apply.
- A successful `LoadInto` fires exactly one `Changed` event regardless of graph size
  (`LoadIntoReplacesInPlaceWithOneChangedEvent`, `SaveLoadTests.cs:41-53`).
- Every `EdgeDto.LaneIds.Length` equals `RoadCatalog.Get(edge.Type).Lanes.Count` for
  that edge's type — checked explicitly (`RoadNetwork.Persistence.cs:143-145`) since a
  mismatch would otherwise index out of range while pairing ids to specs.

## Tuning constants

There is little to tune here by design — persistence is a correctness-critical path,
not a feel-tuned one:

- `SaveLoad.FormatVersion = 1` (`SaveLoad.cs:13`) — bump only when the DTO shape
  changes in a way old files can't be read as-is; see Known limits for what bumping it
  currently lacks.
- `JsonSerializerOptions { WriteIndented = false }` (`SaveLoad.cs:15-18`) — the one
  serializer knob, and it exists specifically for the byte-stability contract; turning
  indentation on would still round-trip correctly but would break every byte-equality
  assertion and diff-based debugging workflow that depends on compact, single-line
  output.

## Known limits

- **No migration path.** `FormatVersion` guards against reading a *newer* file with an
  *older* build (hard failure, `SaveLoad.cs:45-47`), but there is no mechanism yet for
  reading an *older* file with a *newer* format shape — since `1` is the only version
  that has ever shipped, this is untested in practice. The first time `FormatVersion`
  becomes `2`, this gap needs to be closed before old saves are trusted.
- **Single quick slot.** There is exactly one save path, `user://saves/quick.json`
  (`Main.cs:14`) — no named slots, no autosave rotation, no save-file browser.
- **The UI test overwrites the player's real quick-save slot.** `Main.RunUiTest`
  (`Main.cs:252-...`, the `CITYBUILDER_UITEST` harness) calls `QuickSave()`/
  `QuickLoad()` directly on the same `_main` instance with no path override
  (`Main.cs:293,303`) — there is no test-specific slot constant anywhere in `Main.cs`.
  Running the UI test harness on a machine with a real player save in
  `user://saves/quick.json` clobbers it silently.
- **Vehicles/trips are unsaved**, a documented v1 scope cut (see "In-game quick
  save/load" above) — not a bug, but worth remembering before relying on save/load to
  preserve an in-progress traffic scenario.
- **Negative-counter nit.** `ValidateGame` bounds every *entity id* against its
  counter (`id > 0 && id < NextX`) but never validates the counters themselves — a
  save file with zero nodes/edges and, say, `"NextNode": -5` passes validation
  trivially (the per-node loop never runs) and `RestoreInto` assigns `_nextNode = -5`
  verbatim (`RoadNetwork.Persistence.cs:76`). The next `AddNodeInternal` call would
  then mint `NodeId(-5)` — an id that violates the `id > 0` invariant `ValidateGame`
  itself enforces on load, so a save/load of *that* state would then fail to reload.
  No fuzzer or hand-written test currently exercises a hand-crafted negative-counter
  payload (the fuzzer only ever produces counters via real `Save` calls, which are
  always positive), so this is a latent gap rather than an observed failure.

## How to verify

- **Unit tests:** `tests/Domain.Tests/Persistence/SaveLoadTests.cs` —
  `RoundTripIsByteStableAndStructurallyIdentical` (the core contract, including a
  configured junction), `LoadIntoReplacesInPlaceWithOneChangedEvent` (one-event
  contract), `NewerFormatVersionThrows` (version guard), and three negative tests —
  `MissingNodesAndEdgesThrowsSaveFormatException`, `NullNodeConfigThrowsSaveFormatException`,
  `NullLaneIdsThrowsAndLeavesTargetNetworkUntouched` — that pin the "malformed payload
  throws the typed exception, never an NRE, and never half-mutates the target" chain.
- **Fuzzer:** the gesture fuzzer's round-trip check fires every 10 actions across
  every fuzz run (`GestureFuzzer.cs:137-149`, `docs/verification.md`'s fuzzer section)
  — this is what actually exercises the format against the huge variety of graph
  shapes hand-written fixtures don't cover, and is where a real regression would
  surface first.
- **UI test:** `CITYBUILDER_UITEST=/tmp/ui.png godot .` drives an actual save → add a
  road → confirm it's there → load → confirm the added road is gone and the vehicle
  that was on it got purged rather than left dangling on a nonexistent lane
  (`Main.cs:283-311`) — the one place the F5/F9 flow, `TrafficSim` resync, and
  `ToolController` transient-state clearing are all exercised together end to end.
- **Full sequence:** `dotnet test`, then `dotnet build citybuilder.sln`; a change
  touching `RestoreInto` or `SaveGame`'s shape should also be run through the fuzzer
  with an elevated action count (`CITYBUILDER_FUZZ_ACTIONS=10000`, per
  `docs/verification.md`) before being called done, since the round-trip check is
  exactly where a subtle field-ordering or null-handling regression would be caught
  that a handful of fixtures would miss.
