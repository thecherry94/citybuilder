# M9 Zoning & Buildings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** CS2-style zone painting (8 m cell bands along edges, 6 kinds) with deterministic demand-free building growth, fully integrated with save v4, undo, fuzz, KPI, gallery/golden, smoke, UITEST, manual.

**Architecture:** New domain namespace `CityBuilder.Domain.Zoning`: pure cell geometry (`ZoneCellGeometry`) → one validity classifier (`ZoneRules`) → stored state (`ZoneMap`, subscribes to `RoadNetwork.Changed` for re-flow) → growth (`ZoneSim.Tick`). Game layer adds `ToolMode.Zone` (brush/side-fill/erase), a zone overlay + building-shell renderer (delta-driven like `StructureView`, instanced like `TrafficView`), toolbar section, `Z` toggle. Persistence: `SaveGame` gains trailing-optional zone/building DTOs (the `Covered` pattern), `SaveLoad`/`UndoStack` become zone-aware — undo snapshots MUST carry zoning (they are SaveLoad JSON).

**Tech Stack:** .NET 8 domain (System.Numerics), xUnit net10.0, Godot 4.7 mono.

## Global Constraints

- Domain never references Godot. All zoning logic in `src/Domain/Zoning/`.
- Spec: `docs/superpowers/specs/2026-07-20-zoning-buildings-design.md` — cell = 8 m station × depth 0–5 band from `RoadType.OuterHalf`; degenerate rule: non-self-intersecting quad with area ≥ 32 m²; ground-only hosts (max |Y| < 0.6 along curve); junction cuts bound stations; growth delay = 12 s + hash-jitter [0, 30 s); claim maxima/heights per the spec table; kind palette per spec §3.
- Every task ends green (`dotnet test --filter "FullyQualifiedName!~Fuzz"` + `dotnet build citybuilder.sln`) and committed. Windowed-harness tasks follow the retry-once gotcha (memory: windowed-harness-run-gotchas).
- Invariant-culture for any string formatting that reaches files.
- All literals cross-checked at implementation time against the current code — file:line refs below were verified 2026-07-20 (commit `db4ba57`); re-grep if drifted.

---

### Task 1: Cell addressing + `ZoneCellGeometry`

**Files:**
- Create: `src/Domain/Zoning/ZoneTypes.cs`, `src/Domain/Zoning/ZoneCellGeometry.cs`
- Test: `tests/Domain.Tests/Zoning/ZoneCellGeometryTests.cs`

**Interfaces (produced):**
```csharp
namespace CityBuilder.Domain.Zoning;
public enum ZoneKind : byte { None = 0, ResidentialLow, ResidentialHigh, CommercialLow, CommercialHigh, IndustrialLow, IndustrialHigh }
public enum EdgeSide : byte { Right, Left }
public readonly record struct CellAddress(EdgeId Edge, EdgeSide Side, int Station, int Depth);
public readonly record struct CellQuad(Vector3 NearInner, Vector3 FarInner, Vector3 FarOuter, Vector3 NearOuter)
{
    public Vector3 Center { get; }          // average of corners
    public float AreaXZ { get; }            // shoelace on XZ
    public bool SelfIntersects { get; }     // NearInner→FarInner vs FarOuter→NearOuter segment test in XZ
}
public static class ZoneCellGeometry
{
    public const float CellSize = 8f;
    public const int MaxDepth = 6;
    public const float MinCellArea = 32f;
    // Station boundaries in arc-length metres over the DRAWABLE span (between junction cuts).
    public static (float startDist, int stationCount) Stations(RoadEdge edge, RoadNode startNode, RoadNode endNode);
    // Null when the cell is degenerate (area/self-intersection rule).
    public static CellQuad? Quad(RoadEdge edge, RoadType type, float startDist, EdgeSide side, int station, int depth);
}
```
`Stations`: drawable span = `[distAt(cutT_start), distAt(cutT_end)]` via `edge.ArcLength.DistanceAtT` and each node's `Junction.CutT[edge.Id]` (`Entities.cs:116-119`: edge *starting* at a node draws `[CutT,1]`, *ending* draws `[0,CutT]`; a node with `JunctionGeometry.Empty` contributes cut 0/1). `stationCount = floor(span/8)`, leftover unzonable. `Quad`: t values from `edge.ArcLength.TAtDistance(startDist + station·8 [+8])`; lateral offsets `±(type.OuterHalf + depth·8)` / `±(… + 8)` via `edge.Curve.OffsetPoint` (`Bezier3.cs:65`); sign + for `Right`, − for `Left`. Corners at the host's Y (curve Y at those t values — ground rule enforced later by `ZoneRules`, geometry stays honest).

- [ ] **Step 1: Failing tests** — straight 200 m TwoLane free-standing edge (no junctions): 25 stations both sides; `Quad` corners exact (e.g. Right station 0 depth 0 on a (0,0,0)→(200,0,0) edge: NearInner=(0,0,4), FarInner=(8,0,4), FarOuter=(8,0,12), NearOuter=(0,0,12) — TwoLane `OuterHalf` = 4 (2 driving lanes ±1.75 + width... **verify with `RoadCatalog.TwoLane.OuterHalf` at run time and encode the read value**)); depth 5 outer offset = OuterHalf+48; a 30 m edge → 3 stations; a 6 m drawable span → 0; tight-arc inside band drops cells (build with `Bezier3.FromQuadratic`, assert some `Quad(...) == null` at depth ≥ 3 inside) while outside band keeps them; junction-cut test: 4-way cross (Net helpers) → station 0 starts at the cut, not the node.
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~ZoneCellGeometryTests"` → FAIL (types missing).
- [ ] **Step 3:** Implement per the interface block above (shoelace area; segment-intersection helper for the two lateral rails; return null on rule breach).
- [ ] **Step 4:** Tests PASS. **Step 5:** Quick gate + build. **Step 6:** Commit `feat(m9): zone cell geometry — stations, depth bands, degenerate rule`.

---

### Task 2: `ZoneRules` validity classifier

**Files:**
- Create: `src/Domain/Zoning/ZoneRules.cs`
- Test: `tests/Domain.Tests/Zoning/ZoneRulesTests.cs`

**Interfaces (produced):**
```csharp
public static class ZoneRules
{
    public const float MaxHostElevation = 0.6f;   // = VerticalRules.JunctionYTolerance — reference that constant, don't redefine
    public static bool HostZonable(RoadEdge edge);                 // max |Y| along curve < 0.6 (sample control hull + tessellation)
    public static bool CellPaintable(RoadNetwork n, RoadEdge host, in CellQuad quad);  // rules 2+3 below
}
```
Rule 2 (overlap): reject when the quad's XZ AABB (+0 margin) hits any edge's carriageway AABB and the quad polygon intersects that edge's carriageway band (centerline `ClosestPoint` distance < `OuterHalf` at any quad corner or quad center), or any node `Junction.SurfacePolygon` (point-in-polygon / segment crossing). Reuse the M8.5 AABB prefilter helpers if internal — else local AABB math; MUST stay cheap (this runs per painted cell + on re-flow, KPI-guarded in Task 13).
Rule 3 (nearest-edge arbitration): among ground edges whose band AABB (OuterHalf+48 laterally around the curve hull) contains the quad center, the winner is the edge with the smallest `BezierOps.ClosestPoint` distance to the quad center; ties by lower `EdgeId.Value`. `CellPaintable` returns false when `host` is not the winner.

- [ ] **Step 1: Failing tests** — elevated host (a +5 m edge) → `HostZonable` false; ground edge true; cell quad overlapping a crossing road's carriageway → false; cell inside a junction polygon → false; two parallel ground TwoLanes 40 m apart → the inner facing bands: each cell belongs to its nearest edge (assert both directions + a midway tie goes to lower EdgeId); far-apart parallels don't interfere.
- [ ] **Step 2:** FAIL. **Step 3:** implement. **Step 4:** PASS. **Step 5:** gate+build. **Step 6:** Commit `feat(m9): ZoneRules — ground-only, overlap, nearest-edge arbitration`.

---

### Task 3: `ZoneMap` — paint/erase/fill, versioning, delta event

**Files:**
- Create: `src/Domain/Zoning/ZoneMap.cs`
- Test: `tests/Domain.Tests/Zoning/ZoneMapTests.cs`

**Interfaces (produced):**
```csharp
public sealed record ZoneDelta(IReadOnlySet<EdgeId> SidesChanged, IReadOnlySet<BuildingId> BuildingsRemoved, IReadOnlySet<BuildingId> BuildingsAdded);
public readonly record struct BuildingId(int Value);
public sealed class Building { BuildingId Id; ZoneKind Kind; EdgeId Edge; EdgeSide Side; int Station, Depth, FrontageCells, DepthCells; int StyleSeed; float Height; }
public sealed class ZoneMap
{
    public ZoneMap(RoadNetwork network);        // subscribes network.Changed → ReFlow (Task 4)
    public int Version { get; }
    public event Action<ZoneDelta>? Changed;
    public ZoneKind KindAt(CellAddress a); public float PaintedAt(CellAddress a);
    public IReadOnlyDictionary<BuildingId, Building> Buildings { get; }
    public BuildingId? ClaimAt(CellAddress a);  // building occupying the cell, if any
    public int Paint(IEnumerable<CellAddress> cells, ZoneKind kind, float simTime);   // returns cells actually painted (validity-filtered)
    public int Erase(IEnumerable<CellAddress> cells);                                  // clears paint; despawns claims touching them
    public int FillSide(EdgeId edge, EdgeSide side, ZoneKind kind, float simTime);     // all valid cells, full depth
    // Task 5 adds: SpawnBuilding / DespawnBuilding internals used by ZoneSim
}
```
Storage: `Dictionary<(EdgeId, EdgeSide), SideBand>` where `SideBand { float StartDist; int Stations; ZoneKind[] Kinds; float[] PaintedAt; BuildingId?[] Claim; }` sized `Stations×6`, rebuilt lazily per `RoadNetwork.Version` (geometry cache) but paint state persistent. Painting an occupied cell with a different kind: rejected (erase first) — one rule, tested. Same kind: no-op.

- [ ] Steps 1–6 TDD as above. Tests: paint marks exactly the valid subset (invalid cells silently skipped, count returned); erase clears + returns count; fill-side paints full band; repaint-same-kind bumps nothing (`Version` unchanged); paint-over-claim rejected; delta event fires with the edge id; `KindAt` default `None`. Commit `feat(m9): ZoneMap — cell paint state with validity-filtered mutations`.

---

### Task 4: Topology re-flow

**Files:**
- Modify: `src/Domain/Zoning/ZoneMap.cs` (the `Changed`-subscriber body)
- Test: `tests/Domain.Tests/Zoning/ZoneReflowTests.cs`

Semantics (spec §1 re-flow): on `NetworkDelta` — removed edges drop their bands + despawn claims; for `EdgesAdded`/`EdgesChanged`/`NodesChanged`, re-derive affected bands: remap old paint to new stations by **arc-length midpoint** (old station's center distance → new station index; kind + PaintedAt carried; out-of-range drops); claims survive iff every claimed cell re-maps contiguously to same-size rect with same kind, else despawn; then re-run `ZoneRules.CellPaintable` on cells whose quads changed OR that a new/changed edge's band AABB overlaps — invalidated cells lose paint and despawn claims. Split children: both children re-derive; paint remaps by absolute arc position along the original curve (children keep the parent's geometry sliced, so distances translate via each child's own StartDist).

- [ ] Steps 1–6 TDD. Tests: split a zoned edge by drawing a crossing → paint survives on both children away from the new junction, cells at the junction die; bulldoze host → band gone, buildings gone (delta lists them); retype TwoLane→Avenue (wider `OuterHalf`) → stations survive, quads shift outward, a cell now overlapping a nearby parallel road dies; new road stamped through a zoned band → exactly the overlapped cells die + their building despawns while the rest stay; heal (bulldoze third arm of a T with zoned legs) → merged edge carries both segments' paint. Commit `feat(m9): zoning re-flow — road edits remap paint, despawn orphaned claims`.

---

### Task 5: Buildings — claim fitting

**Files:**
- Create: `src/Domain/Zoning/ZoneCatalog.cs` (per-kind maxima/heights/palette-family table from the spec)
- Modify: `src/Domain/Zoning/ZoneMap.cs` (claim/spawn/despawn)
- Test: `tests/Domain.Tests/Zoning/BuildingClaimTests.cs`

```csharp
public static class ZoneCatalog
{
    public sealed record ZoneStyle(ZoneKind Kind, int MaxFrontage, int MaxDepth, float MinHeight, float MaxHeight);
    public static ZoneStyle Get(ZoneKind k);   // table exactly per spec §2
}
// ZoneMap gains:
internal Building? TrySpawnAt(CellAddress anchor, float simTime, int seed);  // greedy largest rect (frontage-first), same-kind vacant valid cells, ≥1×1
internal void Despawn(BuildingId id);
```
Height = `MinHeight + (MaxHeight−MinHeight) · Hash01(styleSeed)`; `StyleSeed = Hash(edge,side,station,depth,seed)` — one shared `static int Hash(...)` (xxhash-style mix, stable across runs, no `Random`).

- [ ] Steps 1–6 TDD. Tests: full 6×6 same-kind area + ResidentialHigh anchor → 3×3 claim; 1×1 island grows 1×1; claims never overlap (paint two anchors, spawn both, assert disjoint cells); mixed-kind neighbors bound the rect; determinism: same inputs ⇒ identical claim + height. Commit `feat(m9): building claims — greedy rect fitting per kind maxima`.

---

### Task 6: `ZoneSim` growth

**Files:**
- Create: `src/Domain/Zoning/ZoneSim.cs`
- Test: `tests/Domain.Tests/Zoning/ZoneSimTests.cs`

```csharp
public sealed class ZoneSim
{
    public ZoneSim(ZoneMap map, int seed);
    public float SimTime { get; internal set; }     // saved in v4
    public const float GrowthDelayBase = 12f; public const float GrowthJitter = 30f;
    public void Tick(float dt);                     // advance SimTime; spawn due candidates
}
```
Candidate = vacant painted cell whose delay expired: `SimTime ≥ newest PaintedAt among the would-be rect + GrowthDelayBase + GrowthJitter·Hash01(addr,seed)`. Incremental: dirty set per edge-side (marked by ZoneMap on paint/re-flow/despawn), scanned round-robin capped per tick (e.g. 4 sides/tick) — no global sweep. Spawn via `TrySpawnAt`.

- [ ] Steps 1–6 TDD. Tests: nothing before `GrowthDelayBase`; staggering (paint a 10-station band, tick to base+jitter/2 ⇒ strictly between 1 and all spawned); full coverage by base+jitter+ε; determinism (two sims, same seed & gestures, tick-count equal ⇒ identical building registries); erase-under-building despawns; perf smoke: 500 painted cells, 600 ticks < 50 ms (xunit stopwatch assert, generous — real ceiling set in Task 13). Commit `feat(m9): ZoneSim — deterministic staggered growth`.

---

### Task 7: Persistence v4 + undo integration

**Files:**
- Modify: `src/Domain/Persistence/SaveGame.cs`, `src/Domain/Persistence/SaveLoad.cs` (FormatVersion 3→4), `src/Domain/Persistence/UndoStack.cs`, `src/Domain/Network/RoadNetwork.Persistence.cs` (ValidateGame zoning block)
- Test: `tests/Domain.Tests/Persistence/SaveLoadZoningTests.cs`

DTO additions — trailing-optional (the `Covered` pattern, `SaveGame.cs:12/25`):
```csharp
// SaveGame gains: ZoneSideDto[]? Zones = null, BuildingDto[]? Buildings = null, int NextBuilding = 0, float ZoneSimTime = 0, int ZoneSeed = 0
public sealed record ZoneSideDto(int Edge, byte Side, float StartDist, int Stations, byte[] Kinds, float[] PaintedAt);
public sealed record BuildingDto(int Id, byte Kind, int Edge, byte Side, int Station, int Depth, int Frontage, int DepthCells, int StyleSeed, float Height);
```
API change: `SaveLoad.Save(RoadNetwork n, ZoneMap? zones = null, ZoneSim? sim = null)`; `LoadInto(json, network, zones?, sim?)` (network-only callers — fuzz round-trip pre-Task 8, VisualShots — keep compiling via the defaults). `UndoStack` ctor gains `(RoadNetwork network, ZoneMap? zones = null, ZoneSim? sim = null, int capacity = 50)` and snapshots/restores through the extended calls. `ValidateGame`: zone edge refs live, Side ∈ {0,1}, array lengths = Stations×6, kinds in enum range, building claims in-range/on-painted-cells/unique-ids/`NextBuilding` bound — mirroring the roundabout block (`RoadNetwork.Persistence.cs:185-228`).

- [ ] Steps 1–6 TDD. Tests: v4 byte-stable round-trip with zones+buildings+SimTime; v3 fixture (string literal of a current-format save) loads with empty zoning; v4-with-defaults omits nothing that breaks byte-equality; undo across a paint gesture restores paint AND buildings; ValidateGame rejects: dangling zone edge, kind byte 99, claim outside band, dup building id. Commit `feat(m9): save v4 — zones+buildings persisted, undo carries zoning`.

---

### Task 8: Invariants + fuzzer

**Files:**
- Create: `src/Domain/Zoning/ZoneInvariants.cs`
- Modify: `tests/Domain.Tests/Fuzzing/GestureFuzzer.cs`
- Test: `tests/Domain.Tests/Zoning/ZoneInvariantTests.cs`

```csharp
public static class ZoneInvariants
{
    public static IReadOnlyList<string> Check(RoadNetwork n, ZoneMap zones); // rule methods, NetworkInvariants style (NetworkInvariants.cs:20)
}
```
Rules: every painted cell's host edge live + `HostZonable`; no claim without all its cells painted its kind; claims disjoint; claim rects within band bounds; no painted cell quad null (degenerate) or failing `CellPaintable`; ZoneMap version monotonic vs last check.

Fuzzer: construct `ZoneMap`+`ZoneSim` beside the network (`GestureFuzzer.cs:61-67`); extend the action band chain (`:82-94`) — steal to make room, keeping draw dominant: paint-brush 6 % / side-fill 3 % / erase 3 % (each picks a random live ground edge/side/stations, logs `zone paint edge=… side=… s=…..… d=…..… kind=…` style lines); fold `ZoneSim.Tick(3f)` into the existing burst cadence (after `SimInvariants.CheckBurst`, `:125`); run `ZoneInvariants.Check` after every action next to `NetworkInvariants.Check` (`:107`); round-trip (`:150-181`) switches to the zone-aware Save/LoadInto and byte-compares. `FuzzArtifacts.DumpOnFailure` unchanged (network picture; zones layer arrives Task 9).

- [ ] Steps 1–6 TDD (unit tests drive each rule with a hand-built violation via internal setters/`InternalsVisibleTo`). Then: default sweep `dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"` green ×3 seeds. Any finding: root-cause fix + pin in `FuzzRegressionTests` per standing protocol. Commit `feat(m9): zone invariants + fuzz alphabet (paint/fill/erase, growth bursts)`.

---

### Task 9: `GeometryDump` zones layer

**Files:**
- Modify: `src/Domain/Diagnostics/GeometryDump.cs` (overloads `Svg(RoadNetwork, ZoneMap?)`, `Json(RoadNetwork, ZoneMap?)`; existing single-arg calls forward null)
- Test: extend `tests/Domain.Tests/Diagnostics/GeometryDumpSvgTests.cs` + `GeometryDumpJsonTests.cs`

SVG: new `<g id="zones">` between `junctions` and `lanes` — painted cell quads as polygons (fill = spec palette, fill-opacity 0.45), buildings as footprint outlines with label `b{id} {kind} h={height}`. JSON: `zones` (per edge-side: startDist, stations, kinds row-major) + `buildings` arrays. Layer-order test updates from 5 to 6 groups.

- [ ] Steps 1–6 TDD. Commit `feat(m9): GeometryDump zones layer`.

---

### Task 10: Editor — `ToolMode.Zone` in `ToolController`

**Files:**
- Modify: `src/Game/ToolController.cs`

Add `Zone` to `ToolMode` (`ToolController.cs:8`). New state: `public ZoneKind ActiveZoneKind { get; set; }` (None = eraser). Wire per the click-tool pattern (`HandleClickAt` switch `:244`, hover `:199`): LMB-drag paints (mouse-down starts a gesture: `_undoStack?.Checkpoint()` once, then each drag sample maps cursor→nearest edge/side/cell via `ZoneCellGeometry` reverse lookup — closest edge by `FindClosestEdgeXZ` (M8.5 plan-view picking), side by sign of cross product, station/depth from arc distance + lateral offset — and paints a 1-cell-radius patch); Shift+LMB → `FillSide`; RMB-drag erases (own checkpoint). Expose `CurrentZoneCells` hover info for the overlay. `Main` passes `ZoneMap`/`ZoneSim` via a new `BindZoning(ZoneMap, ZoneSim)`.

- [ ] Implement → build → commit `feat(m9): Zone tool — brush paint, side-fill, erase with undo checkpoints`. (Domain-free of Godot? No — ToolController is game-layer; logic that maps cursor→cells goes in a small pure helper `ZonePicking` in `src/Domain/Zoning/` WITH unit tests: given a world point + network, return `(EdgeId, EdgeSide, station, depth)?` — test that first, then the thin controller wiring.)

---

### Task 11: Rendering — overlay + building shells

**Files:**
- Create: `src/Game/ZoneOverlayView.cs`, `src/Game/BuildingView.cs`, additions to `src/Game/Materials.cs` (6 kind materials + grid-line mat) and `src/Game/MeshBuilders.cs` (`BuildZoneCellsMesh`, `BuildBuildingShell`)
- Modify: `src/Game/Main.cs` (fields, `_Ready` wiring, `_Process` tick, `Z` keybind `Main.cs:156-178`, QuickLoad/undo resync `Main.cs:205-241`)

`ZoneOverlayView : Node3D` — `Bind(RoadNetwork, ZoneMap)`, delta-driven dirty rebuild (the `StructureView` pattern, `StructureView.cs:30-71`): painted cells always drawn when visible; near-cursor unpainted grid drawn only while `ToolMode.Zone` active (controller feeds hover edge). Flat quads at `MeshBuilders.SurfaceY − 0.01` (under paint, over ground). `SetShown(bool)` for the `Z` toggle + auto-on with the tool.
`BuildingView : Node3D` — six `MultiMeshInstance3D` children (one per kind family, the `TrafficView` pattern `TrafficView.cs:21-43`) with per-kind base meshes from `BuildBuildingShell` (box + roof variant per spec table; podium/accent as part of the one ArrayMesh); instance transform = footprint center + yaw facing the host edge; scale from claim size/height; `SetInstanceColor` tint jitter from `StyleSeed`. Rebuild from `ZoneMap.Changed` deltas.
`Main`: `_zones = new ZoneMap(_network); _zoneSim = new ZoneSim(_zones, seed: 2);` in `_Ready` (after `_network`); tick inside the fixed-step loop (`Main.cs:34-40`) — growth runs regardless of `TrafficEnabled` (buildings grow while traffic is paused; deliberate); `Z` keybind toggles overlay; QuickLoad/TryUndo/TryRedo resync: ZoneMap re-derives bands (network `Changed` fires from `RestoreInto`'s batch) — verify claims restored not re-grown.

- [ ] Implement (mesh-builder pure parts first where testable) → build → run the game once via `run` skill for a manual sanity paint → commit `feat(m9): zone overlay + procedural building shells`.

---

### Task 12: Toolbar + UITEST

**Files:**
- Modify: `src/Game/Toolbar.cs` (mode button in the tuple array `Toolbar.cs:48-71`; zone-kind `OptionButton` mirroring the road-type picker `:76-82` incl. an "Eraser" entry; wired to `_controller.ActiveZoneKind`), `src/Game/Main.cs` (`RunUiTest` `:392` — switch to Zone tool, side-fill a starter edge, tick 60 s of growth, assert ≥1 building, screenshot shows painted overlay)

- [ ] Implement → build → run UITEST (windowed) → read the PNG (overlay + a building visible) → commit `feat(m9): zoning toolbar + UITEST coverage`.

---

### Task 13: KPI + perf ceilings

**Files:**
- Modify: `tests/Domain.Tests/Kpi/KpiScenarios.cs` (new `ZoneGrowth()` scenario: 3×3 grid via `BuildGrid`, fill all sides all six kinds round-robin, `ZoneSim.Tick` 120 s → `zoning.grown_at_120s`, plus stopwatch metrics `perf.zonetick_ms` (600 ticks over 500+ cells) and `perf.paint500_ms` (500-cell paint)), `tests/Domain.Tests/Kpi/KpiSuiteTests.cs` (merge line + `ExpectedKeys` + perf ceilings: set to 3× first measured value, hard-coded like `TickCeilingMs` `KpiSuiteTests.cs:25-29`; `Milestone` const stays until Task 14 flips it to `"M9"`)

- [ ] TDD-ish: scenario returns stable metrics across two in-process runs → gate+build → commit `feat(m9): zoning KPI — growth count + paint/tick perf ceilings`.

---

### Task 14: Gallery, smoke, certification, docs

**Files:**
- Modify: `src/Game/VisualShots.cs` (`zoned_block` scenario: small grid, all six kinds side-filled, `ZoneSim` fast-forwarded 120 s in Build (no Traffic), shots top/oblique/driver-height; add to `GoldenScenarios`), `src/Game/Main.cs` (smoke: paint zones on starter roads, tick until ≥5 buildings, bulldoze one host → assert despawn, quicksave/quickload → assert buildings identical), `KpiSuiteTests.Milestone` → `"M9"`, `docs/manual/12-zoning.md` (new chapter), `docs/verification.md` (zone bits), `docs/roadmap.md` (M9 Done entry), spec sync if any deviation accrued.

- [ ] Gallery + `CITYBUILDER_SHOTS_GOLDEN=update` (goldens grow by 3; review the diff) then `=check` green.
- [ ] Smoke headless green incl. new zoning segment; `CITYBUILDER_SMOKE_DUMP` SVG read once (zones layer sanity).
- [ ] **Certification:** `CITYBUILDER_FUZZ_ACTIONS=10000 dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"` — 3×10k green (findings → fix + pin, rerun).
- [ ] KPI baseline regenerated → `docs/health/M9.md`; manual drift pass; roadmap entry.
- [ ] Full final sweep: quick suite, build, smoke, shots+golden check, UITEST.
- [ ] Commit `cert(m9): zoning & buildings certified — fuzz 3x10k, KPI M9, manual ch12, roadmap` .

---

## Self-review notes

- Spec coverage: cells/rules/map/re-flow (T1–4), claims/growth (T5–6), v4+undo (T7 — undo-carries-zoning is load-bearing, discovered from UndoStack internals), invariants+fuzz (T8), dumps (T9), tool (T10), rendering (T11), toolbar/UITEST (T12), KPI (T13), gallery/smoke/cert/manual (T14).
- Deliberate deviations from spec text: none of substance; `ZonePicking` helper added (T10) to keep cursor→cell logic testable and domain-pure.
- Uncertainties flagged for implementation: `RoadCatalog.*.OuterHalf` exact values (encode measured values in T1 tests); `FindClosestEdgeXZ` signature (T10); whether M8.5 AABB helpers are reusable or internal-local (T2); golden count after T14 (~28).
- Order is strict: T7 (persistence) before T8 (fuzz round-trips zones); T10 before T11 (overlay needs hover state); everything green before T14 cert.
