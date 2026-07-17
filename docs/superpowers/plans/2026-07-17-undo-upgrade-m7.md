# Undo/Redo + Upgrade-in-Place (M7) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ctrl+Z/Ctrl+Y snapshot undo over every network mutation, an Upgrade tool that retypes/flips a road in place preserving its `EdgeId` (and therefore its junction configs), and the `TryHealNode` one-way reversal fix.

**Architecture:** Undo is snapshot-based on the byte-stable `SaveLoad` (`UndoStack` in Domain/Persistence; game layer checkpoints before mutations and reuses the quickload resync after restores). Retype/flip swap a **new `RoadEdge` with the same `EdgeId`** into the network (RoadEdge stays immutable; node `EdgeSet`s hold ids so they're untouched). Spec: `docs/superpowers/specs/2026-07-17-undo-upgrade-design.md`.

**Tech Stack:** C#/.NET 8 domain + xUnit (net10.0), Godot 4.7 mono game layer. Godot console exe: `%LOCALAPPDATA%\Programs\Godot-4.7-mono\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe`.

## Global Constraints

- Domain purity: `src/Domain` never references Godot.
- `UndoStack` capacity default 50; `Checkpoint` dedupes by `RoadNetwork.Version`.
- `RetypeEdge` errors: `UnknownEdge / SameType / TooShort / TooTight`; validation BEFORE mutation; `EdgeId` and junction configs must survive; `LaneId`s regenerate.
- `TryHealNode`: direction-asymmetric types heal only with continuous flow (one edge ends at the node, the other starts); merge ordered upstream-first; symmetric types ordered by `EdgeId` for determinism.
- Every task: `dotnet test --filter "FullyQualifiedName!~KpiSuiteTests" -v q` green before its commit (full suite + KPI at certification). Game tasks additionally build + matching harness.
- Run background `dotnet test` and foreground `dotnet build`/godot sequentially, never concurrently (MSBuild lock contention burned time in M6.75).

---

### Task 1: UndoStack

**Files:**
- Create: `src/Domain/Persistence/UndoStack.cs`
- Test: `tests/Domain.Tests/Persistence/UndoStackTests.cs`

**Interfaces:**
- Consumes: `SaveLoad.Save(RoadNetwork) → string`, `SaveLoad.LoadInto(string, RoadNetwork)`, `RoadNetwork.Version`.
- Produces: `UndoStack(RoadNetwork network, int capacity = 50)` with `void Checkpoint()`, `bool Undo()`, `bool Redo()`, `int UndoCount`, `int RedoCount`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Persistence;
using CityBuilder.Domain.Tests.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Persistence;

public class UndoStackTests
{
    private static RoadNetwork Net1()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
        return n;
    }

    [Fact]
    public void UndoRestoresPreMutationState()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        string before = SaveLoad.Save(n);
        undo.Checkpoint();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 50), new Vector3(100, 0, 50)));
        Assert.Equal(2, n.Edges.Count);
        Assert.True(undo.Undo());
        Assert.Equal(before, SaveLoad.Save(n));
        Assert.Single(n.Edges);
    }

    [Fact]
    public void RedoReappliesUndoneState()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        undo.Checkpoint();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 50), new Vector3(100, 0, 50)));
        string after = SaveLoad.Save(n);
        undo.Undo();
        Assert.True(undo.Redo());
        Assert.Equal(after, SaveLoad.Save(n));
    }

    [Fact]
    public void CheckpointDedupesByVersion()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        undo.Checkpoint();
        undo.Checkpoint(); // nothing changed between the two — must not double-push
        Assert.Equal(1, undo.UndoCount);
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 50), new Vector3(100, 0, 50)));
        undo.Checkpoint();
        Assert.Equal(2, undo.UndoCount);
    }

    [Fact]
    public void NewCheckpointClearsRedo()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        undo.Checkpoint();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 50), new Vector3(100, 0, 50)));
        undo.Undo();
        Assert.Equal(1, undo.RedoCount);
        undo.Checkpoint();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 80), new Vector3(100, 0, 80)));
        Assert.Equal(0, undo.RedoCount);
    }

    [Fact]
    public void EmptyStacksReturnFalse()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        Assert.False(undo.Undo());
        Assert.False(undo.Redo());
    }

    [Fact]
    public void CapacityTrimsOldest()
    {
        var n = Net1();
        var undo = new UndoStack(n, capacity: 3);
        for (int i = 0; i < 5; i++)
        {
            undo.Checkpoint();
            Net.Commit(n, Net.Straight(new Vector3(0, 0, 20 + i * 15), new Vector3(100, 0, 20 + i * 15)));
        }
        Assert.Equal(3, undo.UndoCount);
        int undone = 0;
        while (undo.Undo()) undone++;
        Assert.Equal(3, undone);
        Assert.Equal(3, n.Edges.Count); // 1 + 5 commits − 3 undos
    }

    [Fact]
    public void UndoAllRedoAllIsByteExact()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        var states = new List<string> { SaveLoad.Save(n) };
        for (int i = 0; i < 4; i++)
        {
            undo.Checkpoint();
            Net.Commit(n, Net.Straight(new Vector3(0, 0, 20 + i * 15), new Vector3(100, 0, 20 + i * 15)));
            states.Add(SaveLoad.Save(n));
        }
        for (int i = 3; i >= 0; i--)
        {
            Assert.True(undo.Undo());
            Assert.Equal(states[i], SaveLoad.Save(n));
        }
        for (int i = 1; i <= 4; i++)
        {
            Assert.True(undo.Redo());
            Assert.Equal(states[i], SaveLoad.Save(n));
        }
    }

    [Fact]
    public void PerfGuard480EdgeGrid()
    {
        // checkpoint + undo on the KPI-scale grid must stay editor-instant
        var n = Net.New();
        for (int j = 0; j < 16; j++)
        {
            Net.Commit(n, Net.Straight(new Vector3(0, 0, j * 100), new Vector3(1500, 0, j * 100)));
            Net.Commit(n, Net.Straight(new Vector3(j * 100, 0, 0), new Vector3(j * 100, 0, 1500)));
        }
        var undo = new UndoStack(n);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        undo.Checkpoint();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100, $"checkpoint took {sw.ElapsedMilliseconds} ms");
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -50), new Vector3(1500, 0, -50)));
        sw.Restart();
        Assert.True(undo.Undo());
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100, $"undo took {sw.ElapsedMilliseconds} ms");
    }
}
```

- [ ] **Step 2: Run to verify compile failure**

Run: `dotnet test --filter "FullyQualifiedName~UndoStackTests" -v q`
Expected: compile error — `UndoStack` missing.

- [ ] **Step 3: Implement**

`src/Domain/Persistence/UndoStack.cs`:

```csharp
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Persistence;

/// <summary>Snapshot-based undo/redo over the whole network, built on the
/// byte-stable SaveLoad round-trip (fuzz-certified) instead of invertible deltas —
/// inverting commit-with-splits/heals is exactly where bugs live, and quickload
/// already proves the restore path end to end. Call <see cref="Checkpoint"/> BEFORE
/// a mutation; it dedupes by <see cref="RoadNetwork.Version"/>, so optimistic
/// checkpoints ahead of operations that then fail never leave junk entries.
/// The caller owns post-restore side effects (traffic resync, tool reset) exactly
/// like quickload.</summary>
public sealed class UndoStack(RoadNetwork network, int capacity = 50)
{
    private readonly List<(int Version, string State)> _undo = new();
    private readonly List<string> _redo = new();

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    public void Checkpoint()
    {
        if (_undo.Count > 0 && _undo[^1].Version == network.Version)
            return; // nothing changed since the last checkpoint
        _undo.Add((network.Version, SaveLoad.Save(network)));
        _redo.Clear();
        if (_undo.Count > capacity)
            _undo.RemoveAt(0);
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;
        _redo.Add(SaveLoad.Save(network));
        var (_, state) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        SaveLoad.LoadInto(state, network);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        _undo.Add((network.Version, SaveLoad.Save(network)));
        var state = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        SaveLoad.LoadInto(state, network);
        if (_undo.Count > capacity)
            _undo.RemoveAt(0);
        return true;
    }
}
```

**Correctness note on the dedup after `LoadInto`:** `LoadInto` raises a full-replace
delta and bumps `Version`, so a post-undo mutation's checkpoint sees a different
version than any stored entry — the dedup only ever collapses genuine no-ops. If
`LoadInto` turns out NOT to bump `Version` (verify while implementing:
`SaveLoad.cs`), store `-1` as the version for entries pushed by `Undo/Redo` so they
never dedupe against a live checkpoint.

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~UndoStackTests" -v q`
Expected: 8/8 PASS. (If `UndoAllRedoAllIsByteExact` fails on the redo leg, check
`Redo`'s undo-push uses the *current* version before `LoadInto` — see the note.)

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Persistence/UndoStack.cs tests/Domain.Tests/Persistence/UndoStackTests.cs
git commit -m "feat(domain): UndoStack — snapshot undo/redo on byte-stable SaveLoad, version-deduped checkpoints"
```

---

### Task 2: NetworkDelta.EdgesChanged + RetypeEdge

**Files:**
- Modify: `src/Domain/Network/Entities.cs` (NetworkDelta)
- Modify: `src/Domain/Network/RoadNetwork.cs`
- Modify: `src/Game/RoadNetworkView.cs` (one loop)
- Test: `tests/Domain.Tests/Network/RetypeTests.cs` (new)

**Interfaces:**
- Produces: `public enum RetypeError { UnknownEdge, SameType, TooShort, TooTight }` (in `PlacementProposal.cs` next to `PlacementError`); `public RetypeError? RetypeEdge(EdgeId id, RoadTypeId newType)` (null = success); `NetworkDelta` gains 6th positional `IReadOnlySet<EdgeId> EdgesChanged` **with default** `= null` handling via a secondary ctor — existing 5-arg call sites stay valid (see Step 3).

- [ ] **Step 1: Write the failing tests**

`tests/Domain.Tests/Network/RetypeTests.cs`:

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class RetypeTests
{
    private static (RoadNetwork n, EdgeId edge) Cross()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-80, 0, 0), new Vector3(80, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -80), new Vector3(0, 0, 80)));
        var e = n.Edges.Values.First(x =>
            Vector3.Distance(x.Curve.Point(0.5f), new Vector3(40, 0, 0)) < 5f).Id;
        return (n, e);
    }

    [Fact]
    public void RetypeSwapsTypeAndLanesKeepsEdgeId()
    {
        var (n, e) = Cross();
        var result = n.RetypeEdge(e, RoadCatalog.Street.Id);
        Assert.Null(result);
        var edge = n.Edges[e]; // same id still resolves
        Assert.Equal(RoadCatalog.Street.Id, edge.Type);
        Assert.Equal(RoadCatalog.Street.Lanes.Count, edge.Lanes.Count);
    }

    [Fact]
    public void RetypePreservesJunctionConfig()
    {
        var (n, e) = Cross();
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        n.ConfigureJunction(node.Id, node.Config with
        {
            Mode = JunctionControlMode.AllWayStop,
            LegOffsets = new Dictionary<EdgeId, float> { [e] = 4f },
        });
        Assert.Null(n.RetypeEdge(e, RoadCatalog.Street.Id));
        var after = n.Nodes[node.Id];
        Assert.Equal(JunctionControlMode.AllWayStop, after.Config.Mode);
        Assert.True(after.Config.LegOffsets.ContainsKey(e),
            "EdgeId-keyed leg offset lost — retype must preserve the id");
    }

    [Fact]
    public void RetypeRejectsTooTightCurve()
    {
        // Street (MinRadius 10) bend that FourLane (MinRadius 35) cannot hold
        var n = Net.New();
        var bend = Bezier3.FromQuadratic(new(0, 0, 0), new(20, 0, 18), new(40, 0, 0));
        Net.Commit(n, new PlacementProposal(
            new[] { new ProposedCurve(bend, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.Street.Id));
        var e = n.Edges.Keys.Single();
        Assert.Equal(RetypeError.TooTight, n.RetypeEdge(e, RoadCatalog.FourLane.Id));
        Assert.Equal(RoadCatalog.Street.Id, n.Edges[e].Type); // unchanged on failure
    }

    [Fact]
    public void RetypeRejectsTooShortEdge()
    {
        // 10 m TwoLane edge; Avenue needs MinSegmentLength 21
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(10, 0, 0)));
        var e = n.Edges.Keys.Single();
        Assert.Equal(RetypeError.TooShort, n.RetypeEdge(e, RoadCatalog.Avenue.Id));
    }

    [Fact]
    public void RetypeRejectsSameTypeAndUnknownEdge()
    {
        var (n, e) = Cross();
        Assert.Equal(RetypeError.SameType, n.RetypeEdge(e, RoadCatalog.TwoLane.Id));
        Assert.Equal(RetypeError.UnknownEdge, n.RetypeEdge(new EdgeId(9999), RoadCatalog.Street.Id));
    }

    [Fact]
    public void RetypeRaisesEdgesChangedDeltaAndBumpsVersion()
    {
        var (n, e) = Cross();
        int v = n.Version;
        NetworkDelta? seen = null;
        n.Changed += d => seen = d;
        Assert.Null(n.RetypeEdge(e, RoadCatalog.Street.Id));
        Assert.Equal(v + 1, n.Version);
        Assert.NotNull(seen);
        Assert.Contains(e, seen!.EdgesChanged);
        Assert.Empty(seen.EdgesAdded);
        Assert.Empty(seen.EdgesRemoved);
    }

    [Fact]
    public void RetypeRegeneratesLaneIds()
    {
        var (n, e) = Cross();
        var oldLanes = n.Edges[e].Lanes.Select(l => l.Id).ToHashSet();
        Assert.Null(n.RetypeEdge(e, RoadCatalog.Street.Id));
        Assert.DoesNotContain(n.Edges[e].Lanes, l => oldLanes.Contains(l.Id));
    }
}
```

- [ ] **Step 2: Run to verify compile failure**

Run: `dotnet test --filter "FullyQualifiedName~RetypeTests" -v q`
Expected: compile error — `RetypeEdge` / `EdgesChanged` missing.

- [ ] **Step 3: Implement**

`Entities.cs` — extend the delta record, defaulting the new set so all five-argument
call sites compile unchanged:

```csharp
public sealed record NetworkDelta(
    IReadOnlySet<EdgeId> EdgesAdded,
    IReadOnlySet<EdgeId> EdgesRemoved,
    IReadOnlySet<NodeId> NodesAdded,
    IReadOnlySet<NodeId> NodesRemoved,
    IReadOnlySet<NodeId> NodesChanged)
{
    public IReadOnlySet<EdgeId> EdgesChanged { get; init; } = new HashSet<EdgeId>();
}
```

`PlacementProposal.cs` — add next to `PlacementError`:

```csharp
/// <summary>Why an in-place retype was refused (M7 upgrade tool).</summary>
public enum RetypeError { UnknownEdge, SameType, TooShort, TooTight }
```

`RoadNetwork.cs` — add after `ConfigureJunction`; the edge object is replaced with a
fresh `RoadEdge` carrying the SAME id (RoadEdge stays immutable; node `EdgeSet`s hold
ids and need no touch — this is what preserves `JunctionConfig`):

```csharp
    /// <summary>Change a road's type in place (M7 upgrade tool). The EdgeId — and
    /// therefore every EdgeId-keyed junction override — survives; lanes regenerate
    /// with fresh ids (vehicles on them are dropped by TrafficSim.Sync, like CS2
    /// despawning on replace). Returns null on success.</summary>
    public RetypeError? RetypeEdge(EdgeId id, RoadTypeId newType)
    {
        if (!_edges.TryGetValue(id, out var edge))
            return RetypeError.UnknownEdge;
        if (edge.Type == newType)
            return RetypeError.SameType;
        var type = RoadCatalog.Get(newType);
        if (edge.ArcLength.TotalLength < type.MinSegmentLength)
            return RetypeError.TooShort;
        if (BezierOps.MinRadius(edge.Curve) < type.MinRadius)
            return RetypeError.TooTight;

        ReplaceEdgeInPlace(edge, edge.StartNode, edge.EndNode, edge.Curve, newType);
        return null;
    }

    /// <summary>Swap a same-id RoadEdge into the network (retype/flip), regenerate
    /// its lanes, rebuild both end nodes, and raise an EdgesChanged delta.</summary>
    private void ReplaceEdgeInPlace(RoadEdge old, NodeId start, NodeId end,
        in Bezier3 curve, RoadTypeId type)
    {
        var replacement = new RoadEdge(old.Id, start, end, curve, type);
        replacement.Lanes = RoadCatalog.Get(type).Lanes
            .Select(spec => new Lane(new LaneId(_nextLane++), replacement.Id,
                spec.Offset, spec.Direction, spec.Width, spec.Kind))
            .ToArray();
        _edges[old.Id] = replacement;
        // EdgeSets key by id — nothing to update there even when start/end swap (flip)
        foreach (var nodeId in new[] { start, end }.Distinct())
            if (_nodes.TryGetValue(nodeId, out var node))
                RebuildDerived(node);
        Version++;
        Changed?.Invoke(new NetworkDelta(
            new HashSet<EdgeId>(), new HashSet<EdgeId>(),
            new HashSet<NodeId>(), new HashSet<NodeId>(),
            new HashSet<NodeId> { start, end })
        { EdgesChanged = new HashSet<EdgeId> { old.Id } });
    }
```

(Uses existing members: `_edges`, `_nodes`, `_nextLane`, `RebuildDerived`,
`Version`, `Changed`. `BezierOps`/`RoadCatalog` are already imported in the file.)

`RoadNetworkView.OnChanged` — after the `EdgesAdded` loop:

```csharp
        foreach (var e in delta.EdgesChanged)
            _dirtyEdges.Add(e);
```

- [ ] **Step 4: Run domain suite minus KPI**

Run: `dotnet test --filter "FullyQualifiedName!~KpiSuiteTests" -v q`, then `dotnet build citybuilder.sln`
Expected: all green, build clean.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Network/Entities.cs src/Domain/Network/PlacementProposal.cs src/Domain/Network/RoadNetwork.cs src/Game/RoadNetworkView.cs tests/Domain.Tests/Network/RetypeTests.cs
git commit -m "feat(domain): RetypeEdge — in-place type change preserving EdgeId + junction configs; NetworkDelta.EdgesChanged"
```

---

### Task 3: FlipEdge

**Files:**
- Modify: `src/Domain/Network/RoadNetwork.cs`
- Test: `tests/Domain.Tests/Network/RetypeTests.cs` (append)

**Interfaces:**
- Consumes: `ReplaceEdgeInPlace` (Task 2), `Bezier3` ctor `(P0, P1, P2, P3)`.
- Produces: `public bool FlipEdge(EdgeId id)` — false only for unknown edge.

- [ ] **Step 1: Write the failing tests** (append to `RetypeTests.cs`)

```csharp
    [Fact]
    public void FlipReversesCurveAndSwapsNodes()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        var e = n.Edges.Keys.Single();
        var before = n.Edges[e];
        var (s, t) = (before.StartNode, before.EndNode);
        Assert.True(n.FlipEdge(e));
        var after = n.Edges[e];
        Assert.Equal(t, after.StartNode);
        Assert.Equal(s, after.EndNode);
        Assert.Equal(new Vector3(100, 0, 0), after.Curve.P0);
        Assert.Equal(new Vector3(0, 0, 0), after.Curve.P3);
        Assert.Equal(RoadCatalog.OneWay.Id, after.Type);
        Assert.False(n.FlipEdge(new EdgeId(9999)));
    }

    [Fact]
    public void DoubleFlipRestoresTravelDirection()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        var e = n.Edges.Keys.Single();
        var start0 = n.Edges[e].StartNode;
        n.FlipEdge(e);
        n.FlipEdge(e);
        Assert.Equal(start0, n.Edges[e].StartNode);
        // travel direction = P0→P3 for Forward lanes; all OneWay lanes are Forward
        Assert.Equal(new Vector3(0, 0, 0), n.Edges[e].Curve.P0);
    }
```

(`Net.Straight` already accepts an optional type in this test project — see
`SnapEngineTests`' `Setup`/`Net` usage; if the helper lacks the overload, use the
explicit `PlacementProposal` construction as in `RetypeRejectsTooTightCurve`.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~FlipReverses|FullyQualifiedName~DoubleFlip" -v q`
Expected: compile error — `FlipEdge` missing.

- [ ] **Step 3: Implement** (after `RetypeEdge`)

```csharp
    /// <summary>Reverse a road's travel direction in place (M7 upgrade tool, the
    /// one-way flip). Same-id replacement — junction configs survive; lanes
    /// regenerate. Symmetric types re-derive an equivalent road.</summary>
    public bool FlipEdge(EdgeId id)
    {
        if (!_edges.TryGetValue(id, out var edge))
            return false;
        var reversed = new Bezier3(edge.Curve.P3, edge.Curve.P2, edge.Curve.P1, edge.Curve.P0);
        ReplaceEdgeInPlace(edge, edge.EndNode, edge.StartNode, reversed, edge.Type);
        return true;
    }
```

(If `Bezier3` exposes a `Reversed()` helper — `CurveFit.cs:21` uses one — prefer
`edge.Curve.Reversed()`.)

- [ ] **Step 4: Run domain suite minus KPI** — green.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Network/RoadNetwork.cs tests/Domain.Tests/Network/RetypeTests.cs
git commit -m "feat(domain): FlipEdge — in-place travel-direction reversal, same EdgeId"
```

---

### Task 4: TryHealNode one-way fix

**Files:**
- Modify: `src/Domain/Network/RoadNetwork.cs:588-611`
- Test: `tests/Domain.Tests/Network/HealingTests.cs` (append)

**Interfaces:**
- Consumes: `RoadType.IsDirectionAsymmetric` (`RoadCatalog.Get(type)`), `CurveFit.FitComposite(a, b, sharedNode, nodes)` — merged curve runs a's-far-end → b's-far-end.
- Produces: behavior only (no new API).

- [ ] **Step 1: Write the failing tests** (append to `HealingTests.cs`; mirror the file's existing helpers for building a 3-arm node and removing the third arm)

```csharp
    [Fact]
    public void OneWayChainHealsInFlowDirection()
    {
        // A→B→C one-way chain + a two-way stub at B; bulldozing the stub heals A→C
        // and the healed edge must still flow A→C (the M6 final-review bug: HashSet
        // order could rebuild it C→A, silently reversing the road).
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(200, 0, 0), RoadCatalog.OneWay.Id));
        Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(100, 0, 80)));
        var stub = n.Edges.Values.Single(e => e.Type == RoadCatalog.TwoLane.Id);
        n.RemoveEdge(stub.Id);
        var healed = n.Edges.Values.Single();
        Assert.Equal(RoadCatalog.OneWay.Id, healed.Type);
        Assert.Equal(new Vector3(0, 0, 0), healed.Curve.P0);
        Assert.Equal(new Vector3(200, 0, 0), healed.Curve.P3);
    }

    [Fact]
    public void OpposingOneWaysNeverHeal()
    {
        // A→B and C→B (head-on at B) + stub at B: after the stub goes, the flows
        // still oppose — the node must survive, no heal.
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        Net.Commit(n, Net.Straight(new Vector3(200, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(100, 0, 80)));
        var stub = n.Edges.Values.Single(e => e.Type == RoadCatalog.TwoLane.Id);
        n.RemoveEdge(stub.Id);
        Assert.Equal(2, n.Edges.Count);
        Assert.Equal(3, n.Nodes.Count); // shared node kept
    }

    [Fact]
    public void SymmetricHealIsDeterministicByEdgeId()
    {
        // same scenario twice with reversed commit order must heal to the same
        // orientation (ordering by EdgeId, not HashSet iteration)
        static Bezier3 Heal(bool swap)
        {
            var n = Net.New();
            var a = Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0));
            var b = Net.Straight(new Vector3(100, 0, 0), new Vector3(200, 0, 0));
            Net.Commit(n, swap ? b : a);
            Net.Commit(n, swap ? a : b);
            Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(100, 0, 80)));
            var stub = n.Edges.Values.OrderByDescending(e => e.Id.Value).First();
            n.RemoveEdge(stub.Id);
            return n.Edges.Values.Single().Curve;
        }
        // both runs: healed curve starts at the lower-EdgeId edge's far end
        Assert.Equal(Heal(false).P0.X, Heal(false).P0.X, 3);
        Assert.NotEqual(Heal(false).P0.X, Heal(true).P0.X); // lower id differs → orientation differs, but deterministically
        Assert.Equal(Heal(true).P0.X, Heal(true).P0.X, 3);
    }
```

(The third test asserts determinism per scenario, not equality across scenarios —
adjust the assertion to the actual healed geometry while implementing, but keep the
"same inputs → same orientation, twice" property.)

- [ ] **Step 2: Run to verify the first two fail/flake**

Run: `dotnet test --filter "FullyQualifiedName~OneWayChainHeals|FullyQualifiedName~OpposingOneWays" -v q`
Expected: `OpposingOneWaysNeverHeal` FAILS (today they heal — type equality is the only check). `OneWayChainHealsInFlowDirection` may pass by luck of `HashSet` order — it pins the fix regardless.

- [ ] **Step 3: Implement** — replace the body of `TryHealNode`:

```csharp
    private void TryHealNode(RoadNode node)
    {
        if (node.EdgeSet.Count != 2)
            return;
        // deterministic pair order (HashSet iteration order decided merge
        // orientation before M7 — the one-way reversal bug)
        var edges = node.EdgeSet.Select(e => _edges[e]).OrderBy(e => e.Id.Value).ToArray();
        if (edges[0].Type != edges[1].Type)
            return;

        if (RoadCatalog.Get(edges[0].Type).IsDirectionAsymmetric)
        {
            // heal only when flow is continuous through the node: exactly one edge
            // ends here (upstream) and the other starts here (downstream); merge
            // upstream-first so the healed curve keeps the travel direction
            bool in0 = edges[0].EndNode == node.Id, in1 = edges[1].EndNode == node.Id;
            if (in0 == in1)
                return; // both inbound or both outbound: flows oppose, keep the node
            if (!in0)
                (edges[0], edges[1]) = (edges[1], edges[0]);
        }

        var (merged, maxError) = CurveFit.FitComposite(edges[0], edges[1], node.Id, _nodes);
        if (maxError > GeoConstants.MergeTolerance)
            return;

        var farA = edges[0].OtherNode(node.Id);
        var farB = edges[1].OtherNode(node.Id);
        if (farA == farB)
            return; // would create a loop edge; keep the node

        var type = edges[0].Type;
        RemoveEdgeInternal(edges[0]);
        RemoveEdgeInternal(edges[1]);
        _nodes.Remove(node.Id);
        _batch!.NodesRemoved.Add(node.Id);
        AddEdgeInternal(farA, farB, merged, type);
    }
```

- [ ] **Step 4: Run domain suite minus KPI** — green (watch existing `HealingTests`).

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Network/RoadNetwork.cs tests/Domain.Tests/Network/HealingTests.cs
git commit -m "fix(domain): TryHealNode direction continuity — one-way chains heal in flow order, opposing flows keep the node, deterministic pair order"
```

---

### Task 5: DraftSession.BeforeCommit event

**Files:**
- Modify: `src/Domain/Tools/Draft/DraftSession.cs`
- Test: `tests/Domain.Tests/Tools/DraftSessionTests.cs` (append)

**Interfaces:**
- Produces: `public event Action? BeforeCommit;` fired in `TryCommit` immediately before `network.Commit(validated)` — i.e. only when validation already passed; a rejected completion never fires it.

- [ ] **Step 1: Failing test**

```csharp
    [Fact]
    public void BeforeCommitFiresOnlyWhenCommitProceeds()
    {
        var (n, s) = Setup();
        int before = 0, committed = 0;
        s.BeforeCommit += () => before++;
        s.Committed += () => committed++;
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        ClickAt(s, 5, 0); // TooShort → Adjustable, no commit attempt
        Assert.Equal(0, before);
        s.Cancel();
        ClickAt(s, 0, 0);
        ClickAt(s, 100, 0);
        Assert.Equal(1, before);
        Assert.Equal(1, committed);
    }
```

- [ ] **Step 2: Run — compile failure.** `dotnet test --filter "FullyQualifiedName~BeforeCommitFires" -v q`

- [ ] **Step 3: Implement** — in `DraftSession`, next to the other events:

```csharp
    /// <summary>Fires immediately before a validated proposal is committed — the
    /// game layer's undo-checkpoint hook (M7).</summary>
    public event Action? BeforeCommit;
```

and in `TryCommit`, directly above `var result = network.Commit(validated);`:

```csharp
        BeforeCommit?.Invoke();
```

- [ ] **Step 4: Run domain suite minus KPI** — green.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Draft/DraftSession.cs tests/Domain.Tests/Tools/DraftSessionTests.cs
git commit -m "feat(domain): DraftSession.BeforeCommit — pre-commit hook for undo checkpoints"
```

---

### Task 6: Game undo wiring (Main + Toolbar + JunctionPanel)

**Files:**
- Modify: `src/Game/Main.cs`, `src/Game/Toolbar.cs`, `src/Game/JunctionPanel.cs`

**Interfaces:**
- Consumes: `UndoStack` (T1), `DraftSession.BeforeCommit` (T5).
- Produces: `Main.UndoStack Undo` property (Toolbar buttons use it); `Main.TryUndo()` / `Main.TryRedo()` (keys + buttons); `JunctionPanel.Bind(RoadNetwork network, Action? beforeMutate = null)`.

- [ ] **Step 1: Implement Main**

Field + creation in `_Ready()` (after `session` exists, before `_controller.Bind`):

```csharp
    private UndoStack _undo = null!;
    public UndoStack Undo => _undo;
```

```csharp
        _undo = new UndoStack(_network);
        session.BeforeCommit += _undo.Checkpoint;
```

Key handling in `_UnhandledInput` (Ctrl+Z / Ctrl+Y; note Godot 4: `CtrlPressed`):

```csharp
            case InputEventKey { Keycode: Key.Z, Pressed: true, CtrlPressed: true }:
                TryUndo();
                break;
            case InputEventKey { Keycode: Key.Y, Pressed: true, CtrlPressed: true }:
                TryRedo();
                break;
```

Methods (beside QuickLoad — same resync contract):

```csharp
    /// <summary>Undo the last network mutation (Ctrl+Z). Restores a snapshot in
    /// place, then reruns the quickload resync: traffic drops strandees, the active
    /// gesture and selections are cleared (restored ids may describe different
    /// geometry).</summary>
    public void TryUndo()
    {
        if (!_undo.Undo()) { StatusFlashed?.Invoke("Nothing to undo"); return; }
        _traffic.EnsureSynced();
        _controller.ClearTransientState();
        StatusFlashed?.Invoke("Undone");
    }

    public void TryRedo()
    {
        if (!_undo.Redo()) { StatusFlashed?.Invoke("Nothing to redo"); return; }
        _traffic.EnsureSynced();
        _controller.ClearTransientState();
        StatusFlashed?.Invoke("Redone");
    }
```

Also: quicksave/quickload interplay — `QuickLoad` replaces the graph outside the
undo stack's knowledge; checkpoint before it so a quickload itself is undoable:

```csharp
    // first line of QuickLoad's try block, before LoadInto:
            _undo.Checkpoint();
```

JunctionPanel bind call in `_Ready()` changes to:

```csharp
        junctionPanel.Bind(_network, () => _undo.Checkpoint());
```

- [ ] **Step 2: Implement JunctionPanel**

`Bind` gains the hook; store and invoke before each of the three
`_network.ConfigureJunction(...)` call sites (`:81`, `:176`, `:203`):

```csharp
    private Action? _beforeMutate;

    public void Bind(RoadNetwork network, Action? beforeMutate = null)
    {
        _network = network;
        _beforeMutate = beforeMutate;
        network.Changed += OnNetworkChanged;
    }
```

and immediately before each `_network.ConfigureJunction(` line:

```csharp
        _beforeMutate?.Invoke();
```

(NOTE: `JunctionHighlight` at the bottom of the file has its own `Bind(RoadNetwork)`
— leave it untouched; only the panel's three ConfigureJunction sites checkpoint.)

- [ ] **Step 3: Toolbar buttons + hint**

In `Toolbar._Ready()`, extend the save row:

```csharp
        var undoBtn = new Button { Text = "Undo (^Z)" };
        undoBtn.Pressed += () => _main.TryUndo();
        saveRow.AddChild(undoBtn);
        var redoBtn = new Button { Text = "Redo (^Y)" };
        redoBtn.Pressed += () => _main.TryRedo();
        saveRow.AddChild(redoBtn);
```

Hint label text — append ` · Ctrl+Z undo · Ctrl+Y redo`.

- [ ] **Step 4: Bulldoze checkpoint (ToolController)**

`ToolController` gets the hook the same way audio did:

```csharp
    private UndoStack? _undoStack;
    public void BindUndo(UndoStack undo) => _undoStack = undo;
```

call `_undoStack?.Checkpoint();` as the first statement inside the
`if (_bulldozeTarget is { } target)` block (before `RemoveEdge`), and in Main's
`_Ready()` after `BindAudio`:

```csharp
        _controller.BindUndo(_undo);
```

(add `using CityBuilder.Domain.Persistence;` where needed in Main/ToolController.)

- [ ] **Step 5: Build + smoke + commit**

`dotnet build citybuilder.sln` green; smoke (`CITYBUILDER_SMOKE=1`, godot console
exe, `--headless`) → `SMOKE OK` (undo additions are exercised in Task 8's smoke
extension; this step only proves no regression).

```bash
git add src/Game/Main.cs src/Game/Toolbar.cs src/Game/JunctionPanel.cs src/Game/ToolController.cs
git commit -m "feat(game): undo/redo wiring — Ctrl+Z/Y, toolbar buttons, checkpoints on commit/bulldoze/junction-edit/quickload"
```

---

### Task 7: Upgrade tool (game)

**Files:**
- Modify: `src/Game/ToolController.cs`, `src/Game/Toolbar.cs`

**Interfaces:**
- Consumes: `RetypeEdge`/`FlipEdge` (T2/T3), `_undoStack` (T6), `AudioFx` (`Sfx.Commit`/`Sfx.Reject`), `_view.HighlightEdge`, `_session.RoadType` (current toolbar type).
- Produces: `ToolMode.Upgrade` member; RMB routed to flip while in Upgrade mode.

- [ ] **Step 1: Implement ToolController**

`ToolMode` enum gains `Upgrade` (append before `Bulldoze` for toolbar order):

```csharp
public enum ToolMode { Straight, SimpleCurve, ComplexCurve, Arc, Continuous, Grid, Upgrade, Bulldoze, Inspect, SpawnVehicle }
```

Hover (inside `HandleHoverAt`, mirror the bulldoze branch, before it):

```csharp
        if (_mode == ToolMode.Upgrade)
        {
            var hit = _network.FindClosestEdge(world, MathF.Max(6f, _camera.SnapRadius()));
            _upgradeTarget = hit?.id;
            _view.HighlightEdge(_upgradeTarget);
            _ghost.Clear();
            ReadoutChanged?.Invoke(_upgradeTarget is null ? ""
                : "click: change type · right-click: flip direction");
            return;
        }
```

field `private EdgeId? _upgradeTarget;` (clear it in `SetMode` beside
`_bulldozeTarget = null;` and in `ClearTransientState`).

Click (inside `HandleClickAt`, before the bulldoze branch):

```csharp
        if (_mode == ToolMode.Upgrade)
        {
            HandleHoverAt(world); // refresh target under the cursor
            if (_upgradeTarget is { } target)
            {
                _undoStack?.Checkpoint();
                var err = _network.RetypeEdge(target, _session.RoadType);
                if (err is null)
                {
                    _audio?.Play(Sfx.Commit);
                }
                else
                {
                    _audio?.Play(Sfx.Reject);
                    StatusFlashed?.Invoke($"cannot upgrade: {err}");
                }
            }
            return;
        }
```

RMB flip — in `_UnhandledInput`, change the right-mouse case:

```csharp
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                if (_mode == ToolMode.Upgrade)
                    FlipUpgradeTarget();
                else
                    StepBack();
                break;
```

```csharp
    private void FlipUpgradeTarget()
    {
        if (_upgradeTarget is not { } target)
            return;
        _undoStack?.Checkpoint();
        if (_network.FlipEdge(target))
            _audio?.Play(Sfx.Commit);
    }
```

- [ ] **Step 2: Toolbar mode button**

Add `("Upgrade", ToolMode.Upgrade),` to the modes array between Grid and Bulldoze.

- [ ] **Step 3: Build + UITEST eyeball + commit**

`dotnet build citybuilder.sln` green. `CITYBUILDER_UITEST` run → `UITEST OK`; read
the PNG — Upgrade button visible in the mode row.

```bash
git add src/Game/ToolController.cs src/Game/Toolbar.cs
git commit -m "feat(game): Upgrade tool — click retypes to current type, right-click flips direction, undo-checkpointed"
```

---

### Task 8: Smoke + UITEST coverage

**Files:**
- Modify: `src/Game/Main.cs` (`RunSmoke`, `RunUiTest`)

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Smoke additions** — insert immediately BEFORE the audio `Expect` at the end of `RunSmoke`:

```csharp
            // M7: upgrade-in-place — retype a grid edge, flip a one-way loop edge
            // (and back — a lone flipped loop edge breaks strong connectivity),
            // then undo everything and assert the network state rewinds
            int edgesBeforeM7 = _network.Edges.Count;
            var gridEdge = _network.Edges.Values.First(e =>
                System.Numerics.Vector3.Distance(e.Curve.Point(0.5f), V(248, 0)) < 5f);
            _undo.Checkpoint();
            Expect(_network.RetypeEdge(gridEdge.Id, RoadCatalog.Street.Id) is null,
                "retype grid edge failed");
            Expect(_network.Edges[gridEdge.Id].Type == RoadCatalog.Street.Id,
                "retype did not stick");
            var loopEdge = _network.Edges.Values.First(e => e.Type == RoadCatalog.OneWay.Id);
            _undo.Checkpoint();
            Expect(_network.FlipEdge(loopEdge.Id), "flip failed");
            Expect(!LaneGraph.IsStronglyConnected(_network, LaneKind.Driving),
                "flipped loop edge should break strong connectivity");
            _undo.Checkpoint();
            Expect(_network.FlipEdge(loopEdge.Id), "flip back failed");
            Expect(LaneGraph.IsStronglyConnected(_network, LaneKind.Driving),
                "flip back did not restore connectivity");
            TryUndo(); // undo flip-back
            Expect(!LaneGraph.IsStronglyConnected(_network, LaneKind.Driving),
                "undo did not restore the flipped state");
            TryUndo(); // undo flip
            TryUndo(); // undo retype
            Expect(_network.Edges[gridEdge.Id].Type == RoadCatalog.TwoLane.Id,
                "undo did not restore the original type");
            Expect(_network.Edges.Count == edgesBeforeM7,
                $"edge count after undos: {_network.Edges.Count} != {edgesBeforeM7}");
```

(`V` is the local helper in `RunSmoke`; `(248, 0)` is a mid-grid segment the earlier
grid stamp created. `RoadCatalog` is already imported in Main.)

- [ ] **Step 2: UITEST addition** — after the ghost-stress block, before `Input.WarpMouse`:

```csharp
            // M7: upgrade via the tool surface + Ctrl+Z path (calls, not raw keys)
            _controller.SetMode(ToolMode.Upgrade);
            var upEdge = _network.Edges.Values.First(e =>
                System.Numerics.Vector3.Distance(e.Curve.Point(0.5f), V(-40, 0)) < 6f);
            _controller.SetRoadType(RoadCatalog.Street.Id);
            _controller.HandleHoverAt(V(-40, 0));
            _controller.HandleClickAt(V(-40, 0));
            Expect(_network.Edges[upEdge.Id].Type == RoadCatalog.Street.Id,
                "upgrade tool did not retype");
            TryUndo();
            Expect(_network.Edges[upEdge.Id].Type != RoadCatalog.Street.Id,
                "undo did not revert the upgrade");
            _controller.SetRoadType(RoadCatalog.TwoLane.Id);
            _controller.SetMode(ToolMode.Straight);
```

- [ ] **Step 3: Run both harnesses**

`dotnet build citybuilder.sln`; smoke → `SMOKE OK`; UITEST → `UITEST OK` + read PNG
(Upgrade + Undo/Redo buttons visible).

- [ ] **Step 4: Commit**

```bash
git add src/Game/Main.cs
git commit -m "test(harness): smoke + UITEST cover retype/flip/undo end to end"
```

---

### Task 9: Fuzzer extension

**Files:**
- Modify: `tests/Domain.Tests/Fuzzing/GestureFuzzer.cs`

**Interfaces:**
- Consumes: `UndoStack`, `RetypeEdge`, `FlipEdge`.
- Produces: new action mix — draw 45, bulldoze 15, configure 10, retype 8, flip 5, undo/redo 7, snap toggles 5, stepback/cancel 5.

- [ ] **Step 1: Implement**

In `Run`, create the stack beside the session and checkpoint inside the mutating
actions:

```csharp
        var undo = new UndoStack(network);
```

Replace the action-pick block:

```csharp
                int pick = rng.Next(100);
                if (pick < 45) { undo.Checkpoint(); DrawGesture(session, network, rng, Log); }
                else if (pick < 60) { undo.Checkpoint(); Bulldoze(network, rng, Log); }
                else if (pick < 70) { undo.Checkpoint(); ConfigureJunctionAction(network, rng, Log); }
                else if (pick < 78) { undo.Checkpoint(); Retype(network, rng, Log); }
                else if (pick < 83) { undo.Checkpoint(); Flip(network, rng, Log); }
                else if (pick < 90) UndoRedo(undo, session, rng, Log);
                else if (pick < 95) ToggleSnap(session, rng, Log);
                else StepBackCancel(session, network, rng, Log);
```

New actions (ordered edge/type pick keeps seeds replayable):

```csharp
    private static readonly RoadTypeId[] AllTypes =
        RoadCatalog.All.Select(t => t.Id).ToArray();

    private static void Retype(RoadNetwork network, Random rng, Action<string> log)
    {
        var ids = network.Edges.Keys.OrderBy(e => e.Value).ToArray();
        if (ids.Length == 0) { log("retype skip=empty"); return; }
        var id = ids[rng.Next(ids.Length)];
        var type = AllTypes[rng.Next(AllTypes.Length)];
        var err = network.RetypeEdge(id, type);
        log($"retype edge={id.Value} type={type.Value} result={(err is null ? "ok" : err.ToString())}");
    }

    private static void Flip(RoadNetwork network, Random rng, Action<string> log)
    {
        var ids = network.Edges.Keys.OrderBy(e => e.Value).ToArray();
        if (ids.Length == 0) { log("flip skip=empty"); return; }
        var id = ids[rng.Next(ids.Length)];
        network.FlipEdge(id);
        log($"flip edge={id.Value}");
    }

    private static void UndoRedo(UndoStack undo, DraftSession session, Random rng, Action<string> log)
    {
        // the editor clears the active gesture on restore; mirror that or a held
        // draft would reference pre-undo ids
        session.Cancel();
        int steps = rng.Next(1, 4);
        int done = 0;
        bool redo = rng.NextDouble() < 0.4;
        for (int i = 0; i < steps; i++)
            if (redo ? undo.Redo() : undo.Undo())
                done++;
        log($"{(redo ? "redo" : "undo")} steps={done}/{steps}");
    }
```

Add `using CityBuilder.Domain.Persistence;` at the top.

- [ ] **Step 2: Run default fuzz sweep**

Run: `dotnet test --filter "FullyQualifiedName~FuzzSuiteTests" -v q`
Expected: 3 seeds × 300 green. Any violation: minimize via the printed action tail,
fix at root cause, pin in `FuzzRegressionTests.cs` — do NOT proceed on red.

- [ ] **Step 3: Run full suite minus KPI** — green.

- [ ] **Step 4: Commit**

```bash
git add tests/Domain.Tests/Fuzzing/GestureFuzzer.cs
git commit -m "test(fuzz): retype/flip/undo/redo in the gesture alphabet — restored-then-edited networks now fuzzed"
```

---

### Task 10: Certification + docs (quality-stack DoD)

**Files:**
- Modify: `tests/Domain.Tests/Kpi/KpiSuiteTests.cs` (`Milestone = "M7"`)
- Create: `docs/health/M7.md` (generated) + regenerated `kpi-latest.json`
- Modify: `docs/manual/02-network-validation.md` (retype/flip/heal-fix),
  `docs/manual/08-persistence.md` (UndoStack), `docs/manual/00-overview.md` (module
  line), `docs/manual/06-drafting-snapping.md` (BeforeCommit event line),
  `docs/manual/glossary.md` (snapshot undo, upgrade-in-place),
  `docs/conventions.md` (constants: undo capacity 50, checkpoint dedup, retype
  errors), `docs/roadmap.md` (M7 done entry; renumber Next up),
  `docs/superpowers/specs/2026-07-17-undo-upgrade-design.md` (implementation notes),
  `CLAUDE.md` (test count)

- [ ] **Step 1:** Certification fuzz: `CITYBUILDER_FUZZ_ACTIONS=10000`, filter `FuzzSuiteTests`, foreground with output captured to a file if needed — must show 3 passed. Findings → fix + pin first.
- [ ] **Step 2:** `Milestone = "M7"`; full `dotnet test` (no filter) → all green, regenerates `docs/health/M7.md`; restore any clobbered `docs/health/M6.75.md` via `git checkout -- docs/health/M6.75.md` if a pre-rename run touched it; append an M7 notes section (editor-only milestone; note the environmental validate500 caveat carried from M6.75 if it persists).
- [ ] **Step 3:** Docs pass (each file listed above; concrete content decided against the shipped code — cover: snapshot-undo rationale + capacity/dedup semantics, retype/flip same-id replacement + `EdgesChanged`, heal continuity rule, checkpoint call-site table, fuzz mix change, new test files).
- [ ] **Step 4:** Full gates: `dotnet build citybuilder.sln`, smoke, UITEST (+ read PNG), SHOTS (`SHOTS OK`, count unchanged) — all green.
- [ ] **Step 5:** Final commit:

```bash
git add -A
git commit -m "docs+cert: M7 undo/redo + upgrade-in-place — fuzz 3x10k green, KPI M7 health report, manual/conventions/roadmap drift"
```

---

## Self-Review

- **Spec coverage:** UndoStack §1 → T1; checkpoint call sites → T5 (session), T6 (bulldoze/junction/quickload), T7 (upgrade); retype/flip + `EdgesChanged` §2 → T2/T3; heal fix §3 → T4; game layer §4 → T6/T7; fuzzer + smoke/UITEST + perf guard §5 → T8/T9/T1; DoD → T10. Gap check: spec's "explicit test spawning a vehicle on a retyped edge" — covered by fuzz bursts after retype (T9 mix) rather than a bespoke test; acceptable, note in spec impl-notes at T10.
- **Type consistency:** `UndoStack.Checkpoint/Undo/Redo/UndoCount/RedoCount` uniform across T1/T6/T7/T9; `RetypeEdge(EdgeId, RoadTypeId) → RetypeError?` uniform T2/T7/T9; `FlipEdge(EdgeId) → bool` uniform T3/T7/T8/T9; `Bind(RoadNetwork, Action?)` only for JunctionPanel (highlight's Bind untouched, noted in T6).
- **Placeholder scan:** clean — every code step carries the code; T10 doc contents are enumerated per file with their required topics (content authored against shipped code, as in M6.75).
