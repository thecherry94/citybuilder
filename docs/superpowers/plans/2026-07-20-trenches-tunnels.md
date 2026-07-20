# M8.5 — Trenches & Tunnels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unlock negative elevation with explicit player-covered tunnels (one new domain
bit, `RoadEdge.Covered`, save v3), derived cut/tunnel/portal structures, an x-ray view,
pillar placement awareness, and the queued fuzz perf pass.

**Architecture:** Mirrors M8's derived-structure model below ground. The only new stored
state is one bool per edge; span classification, portals, walls, and x-ray all derive.
Zero traffic-sim changes. Spec: `docs/superpowers/specs/2026-07-20-trenches-tunnels-design.md`.

**Tech Stack:** .NET 8 domain (System.Numerics, xUnit on net10.0), Godot 4.7 mono Game layer.

## Global Constraints

- Domain (`src/Domain`) never references Godot (golden rule 1).
- Every task: `dotnet test --filter "FullyQualifiedName!~Fuzz"` (~2 s quick gate) +
  `dotnet build citybuilder.sln` green before commit. Full fuzz only where a task says so.
- New constants: `GeoConstants.MaxDepth = 50f`, `GeoConstants.PortalDepth = 3.0f`.
  Unchanged: `JunctionYTolerance` 0.6, `MinClearance` 4.7, `EmbankmentMax` 1.0,
  gradient caps 20/15/12%.
- Save `FormatVersion` becomes **3**; v1/v2 must still load (`Covered=false`).
- No `git add -A` (repo hygiene); add files explicitly.
- Prefer invariant-style regression tests over example asserts (golden rule 4).

---

### Task 1: Domain — `Covered` flag, propagation, `SetCovered`

**Files:**
- Modify: `src/Domain/Network/Entities.cs` (RoadEdge, ~line 33)
- Modify: `src/Domain/Network/RoadNetwork.cs` (`AddEdgeInternal` ~842, `SplitEdgeWithReuse` ~701, `TryHealNode` ~783, `ReplaceEdgeInPlace` ~971, new `SetCovered` next to `FlipEdge` ~956)
- Modify: `src/Domain/Network/RoadNetwork.Roundabouts.cs` (~line 541 same-id leg replacement)
- Test: `tests/Domain.Tests/Network/CoveredFlagTests.cs` (create)

**Interfaces:**
- Produces: `RoadEdge.Covered` (`bool`, `internal set`), `RoadNetwork.SetCovered(EdgeId id, bool covered) : bool` (false on unknown edge, ring edge, or no-op; raises `NetworkDelta.EdgesChanged` + `Version++` on success). `AddEdgeInternal` gains trailing `bool covered = false`.
- Consumes: existing split/heal/retype/flip machinery, `IsRingEdge`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

/// <summary>M8.5: the explicit per-edge Covered flag. It has no validation surface —
/// toggling is always legal — so the tests here are about propagation invariants:
/// no editing operation may silently invent or drop a tunnel.</summary>
public class CoveredFlagTests
{
    private static RoadNetwork Net() => new();

    private static EdgeId Build(RoadNetwork n, Vector3 a, Vector3 b)
    {
        var proposal = new PlacementProposal(
            new[] { new ProposedCurve(Bezier3.Line(a, b),
                EndpointBinding.Free(a), EndpointBinding.Free(b)) },
            RoadCatalog.TwoLane);
        var result = n.Commit(proposal);
        return result.EdgesAdded.Single();
    }
    // NOTE: mirror the construction helpers actually used in HealingTests /
    // PlacementTests if the shapes above don't compile verbatim — the intent is
    // "commit one straight TwoLane edge and return its id".

    [Fact]
    public void SetCoveredRaisesEdgesChangedAndBumpsVersion()
    {
        var n = Net();
        var id = Build(n, new(0, 0, 0), new(60, 0, 0));
        long v = n.Version;
        NetworkDelta? seen = null;
        n.Changed += d => seen = d;
        Assert.True(n.SetCovered(id, true));
        Assert.True(n.Edges[id].Covered);
        Assert.True(n.Version > v);
        Assert.Contains(id, seen!.EdgesChanged);
    }

    [Fact]
    public void SetCoveredIsFalseOnNoOpUnknownAndRing()
    {
        var n = Net();
        var id = Build(n, new(0, 0, 0), new(60, 0, 0));
        Assert.False(n.SetCovered(id, false));                  // already false: no-op
        Assert.False(n.SetCovered(new EdgeId(9999), true));     // unknown
        // ring-edge refusal is covered in RoundaboutTests-style setup below if a
        // convenient ring builder exists; otherwise assert via IsRingEdge path there.
    }

    [Fact]
    public void SplitChildrenInheritCovered()
    {
        var n = Net();
        var id = Build(n, new(0, 0, 0), new(120, 0, 0));
        n.SetCovered(id, true);
        // crossing road forces a split of the covered edge
        Build(n, new(60, 0, -60), new(60, 0, 60));
        Assert.True(n.Edges.Values.Count(e => e.Covered) >= 2,
            "both children of a split covered edge must stay covered");
        Assert.All(n.Edges.Values.Where(e => e.Curve.P0.X is >= -1 and <= 121
            && MathF.Abs(e.Curve.P0.Z) < 1 && MathF.Abs(e.Curve.P3.Z) < 1),
            e => Assert.True(e.Covered));
    }

    [Fact]
    public void HealKeepsCoveredOnlyWhenBothAgree()
    {
        var n = Net();
        var left = Build(n, new(0, 0, 0), new(60, 0, 0));
        Build(n, new(60, 0, 0), new(120, 0, 0));
        Build(n, new(60, 0, 0), new(60, 0, 80)); // third arm keeps the node
        n.SetCovered(left, true);                 // only one side covered
        // bulldoze the third arm → degree-2 heal fires
        var arm = n.Edges.Values.Single(e => MathF.Abs(e.Curve.P3.Z - 80) < 1
            || MathF.Abs(e.Curve.P0.Z - 80) < 1);
        n.RemoveEdge(arm.Id);
        var healed = Assert.Single(n.Edges.Values);
        Assert.False(healed.Covered, "mixed heal must come out uncovered (conservative)");
    }

    [Fact]
    public void RetypeAndFlipPreserveCovered()
    {
        var n = Net();
        var id = Build(n, new(0, 0, 0), new(60, 0, 0));
        n.SetCovered(id, true);
        Assert.Null(n.RetypeEdge(id, RoadCatalog.Street));
        Assert.True(n.Edges[id].Covered, "retype must not drop the tunnel");
        Assert.True(n.FlipEdge(id));
        Assert.True(n.Edges[id].Covered, "flip must not drop the tunnel");
    }
}
```

Adjust helper `Build` to whatever committed-single-edge helper the existing test suites
use (`PlacementTests`/`HealingTests` have one) — the assertions are the contract.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CoveredFlagTests"`
Expected: compile FAIL — `RoadEdge.Covered` / `SetCovered` not defined.

- [ ] **Step 3: Implement**

`Entities.cs`, inside `RoadEdge`:

```csharp
    /// <summary>M8.5: player-chosen tunnel cover. Explicit state, not depth-derived;
    /// only below-ground spans render differently — the flag is inert above ground.
    /// Propagation: split children inherit, heal keeps it iff both edges agree,
    /// retype/flip/leg-regeneration preserve it.</summary>
    public bool Covered { get; internal set; }
```

`RoadNetwork.cs`:

```csharp
    private RoadEdge AddEdgeInternal(NodeId start, NodeId end, in Bezier3 curve,
        RoadTypeId type, bool covered = false)
    {
        var edge = new RoadEdge(new EdgeId(_nextEdge++), start, end, curve, type)
            { Covered = covered };
        // ... rest unchanged
```

- `SplitEdgeWithReuse` (~717): `AddEdgeInternal(edge.StartNode, mid.Id, a, edge.Type, edge.Covered)` and same for `eb`.
- `TryHealNode` (~829): `AddEdgeInternal(farA, farB, merged, type, edges[0].Covered && edges[1].Covered);`
- `ReplaceEdgeInPlace` (~974): after constructing `replacement`, add `replacement.Covered = old.Covered;`
- `RoadNetwork.Roundabouts.cs` ~541: after `var replacement = new RoadEdge(old.Id, start, end, trimmed, old.Type);` add `replacement.Covered = old.Covered;`
- New public op next to `FlipEdge`:

```csharp
    /// <summary>Toggle a road's tunnel cover in place (M8.5 upgrade tool). No
    /// validation surface: covering is always legal and inert above ground. Same-id,
    /// geometry/lanes untouched — only the renderer cares, via EdgesChanged.
    /// False on unknown edge, roundabout ring edge (ring regeneration owns those),
    /// or a no-op toggle.</summary>
    public bool SetCovered(EdgeId id, bool covered)
    {
        if (!_edges.TryGetValue(id, out var edge))
            return false;
        if (IsRingEdge(id))
            return false;
        if (edge.Covered == covered)
            return false;
        edge.Covered = covered;
        Version++;
        Changed?.Invoke(new NetworkDelta(
            new HashSet<EdgeId>(), new HashSet<EdgeId>(),
            new HashSet<NodeId>(), new HashSet<NodeId>(),
            new HashSet<NodeId>())
        { EdgesChanged = new HashSet<EdgeId> { id } });
        return true;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CoveredFlagTests"` then the quick gate
`dotnet test --filter "FullyQualifiedName!~Fuzz"` and `dotnet build citybuilder.sln`.
Expected: PASS / PASS / build green.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Network/Entities.cs src/Domain/Network/RoadNetwork.cs \
  src/Domain/Network/RoadNetwork.Roundabouts.cs tests/Domain.Tests/Network/CoveredFlagTests.cs
git commit -m "feat(domain): RoadEdge.Covered flag — SetCovered op + split/heal/retype/flip propagation"
```

---

### Task 2: Save format v3 (+ undo carries the flag)

**Files:**
- Modify: `src/Domain/Persistence/SaveGame.cs` (`EdgeDto`, line 24)
- Modify: `src/Domain/Persistence/SaveLoad.cs` (`FormatVersion` line 13, `ToEdgeDto` line 98)
- Modify: `src/Domain/Network/RoadNetwork.Persistence.cs` (`RestoreInto` edge loop, ~line 59)
- Test: `tests/Domain.Tests/Persistence/SaveLoadTests.cs` (extend — locate with `grep -rn "FormatVersion" tests/`)

**Interfaces:**
- Produces: `EdgeDto(..., bool Covered = false)`; `SaveLoad.FormatVersion == 3`.
- Consumes: `RoadEdge.Covered` from Task 1.

- [ ] **Step 1: Write the failing tests** (in the existing SaveLoad test class)

```csharp
    [Fact]
    public void CoveredFlagRoundTripsAndStaysByteStable()
    {
        var n = BuildSampleNetwork();                     // reuse the file's helper
        var anyEdge = n.Edges.Keys.First();
        n.SetCovered(anyEdge, true);
        var json = SaveLoad.Save(n);
        var restored = SaveLoad.Load(json);
        Assert.True(restored.Edges[anyEdge].Covered);
        Assert.Equal(json, SaveLoad.Save(restored));      // byte-stable at v3
    }

    [Fact]
    public void V2SaveWithoutCoveredLoadsAsUncovered()
    {
        var n = BuildSampleNetwork();
        // regress a fresh v3 save to a v2 payload: strip the Covered property and
        // rewrite the version — exactly what a real v2 file looks like
        var v2 = SaveLoad.Save(n)
            .Replace("\"FormatVersion\":3", "\"FormatVersion\":2")
            .Replace(",\"Covered\":false", "");
        var restored = SaveLoad.Load(v2);
        Assert.All(restored.Edges.Values, e => Assert.False(e.Covered));
    }
```

Also update any existing assert pinning `FormatVersion` to 2 (grep first — see Files).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SaveLoad"`
Expected: FAIL — `EdgeDto` has no `Covered`, version still 2.

- [ ] **Step 3: Implement**

```csharp
// SaveGame.cs — v3 (M8.5) appends Covered; absent in v1/v2 → default false
public sealed record EdgeDto(int Id, int Start, int End, int Type,
    float[] Curve /* 12 floats: P0..P3, each X,Y,Z */, int[] LaneIds /* catalog order */,
    bool Covered = false);
```

`SaveLoad.cs`: `FormatVersion = 3;` and `ToEdgeDto` passes `edge.Covered` as the last
argument. `RoadNetwork.Persistence.cs` edge loop: after constructing `edge`, add
`edge.Covered = ed.Covered;`

- [ ] **Step 4: Run quick gate + build**

Run: `dotnet test --filter "FullyQualifiedName!~Fuzz"` and `dotnet build citybuilder.sln`
Expected: all green — the undo stack snapshots via SaveLoad, so undo/redo of a covered
toggle now works for free (the fuzzer's UndoRedo action will exercise it in Task 8).

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Persistence/SaveGame.cs src/Domain/Persistence/SaveLoad.cs \
  src/Domain/Network/RoadNetwork.Persistence.cs tests/Domain.Tests/Persistence/SaveLoadTests.cs
git commit -m "feat(domain): save format v3 — Covered on EdgeDto, v1/v2 load as uncovered"
```

---

### Task 3: Editor unlock — negative elevation, signed readout + badges

**Files:**
- Modify: `src/Domain/Geometry/GeoConstants.cs` (add `MaxDepth`, `PortalDepth`)
- Modify: `src/Domain/Tools/Draft/DraftSession.cs` (clamp, line 40; XML doc line 33-36)
- Modify: `src/Game/ToolController.cs` (readout lines 362-364 and 376-382)
- Modify: `src/Game/GhostView.cs` (`AddElevationLabel`, lines 371-385)
- Test: extend the DraftSession elevation tests (locate: `grep -rln "CurrentElevation" tests/`)

**Interfaces:**
- Produces: `GeoConstants.MaxDepth = 50f`, `GeoConstants.PortalDepth = 3.0f`;
  `DraftSession.CurrentElevation` clamps to `[-MaxDepth, MaxElevation]`.
- Consumes: nothing new.

- [ ] **Step 1: Failing domain test** (in the existing DraftSession elevation test file)

```csharp
    [Fact]
    public void ElevationClampsToNegativeMaxDepth()
    {
        var s = NewSession();                       // file's existing helper
        s.CurrentElevation = -1000f;
        Assert.Equal(-GeoConstants.MaxDepth, s.CurrentElevation);
        s.CurrentElevation = -12f;
        Assert.Equal(-12f, s.CurrentElevation);     // negative values now legal
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ElevationClamps"`
Expected: FAIL — clamp floor is 0 today.

- [ ] **Step 3: Implement**

`GeoConstants.cs` additions:

```csharp
    /// <summary>Editor depth clamp below ground: elevation ∈ [−MaxDepth, MaxElevation] (M8.5).</summary>
    public const float MaxDepth = 50f;

    /// <summary>Deck depth at/below which a covered edge's span renders as tunnel
    /// (portal face where the deck crosses this depth). Shallower covered spans stay
    /// open cut — a portal needs headroom (M8.5).</summary>
    public const float PortalDepth = 3.0f;
```

`DraftSession.cs` line 40:
`set { _currentElevation = Math.Clamp(value, -GeoConstants.MaxDepth, GeoConstants.MaxElevation); Revalidate(); }`
(and update the `[0, MaxElevation]` doc comment).

`ToolController.cs` — both readout sites use one signed label helper:

```csharp
    private string ElevationLabel() => _session.CurrentElevation switch
    {
        > 0.01f => $"⬆ {_session.CurrentElevation:0} m",
        < -0.01f => $"⬇ {-_session.CurrentElevation:0} m",
        _ => "",
    };
```

Line 362-364 becomes `if (ElevationLabel() is { Length: > 0 } el) s = s.Length > 0 ? $"{s}   {el}" : el;`
Line 376-382 becomes `ReadoutChanged?.Invoke(... ElevationLabel() is { Length: > 0 } el2 ? el2 : "elevation: ground");`
(keep the existing structure; only the formatting branches change).

`GhostView.AddElevationLabel`: replace the `p.Y <= 0.5f` early-out and text line:

```csharp
        if (MathF.Abs(p.Y) <= 0.5f)
            return;
        ...
        l.Position = pos + Vector3.Up * (p.Y > 0 ? 4f : 1.5f); // below-ground badge floats near ground
        l.Text = p.Y > 0 ? $"⬆ {p.Y:0} m" : $"⬇ {-p.Y:0} m";
```

- [ ] **Step 4: Quick gate + build**

Run: `dotnet test --filter "FullyQualifiedName!~Fuzz"` and `dotnet build citybuilder.sln`
Expected: green. (Ghost badge is visually verified in Task 10's gallery.)

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Geometry/GeoConstants.cs src/Domain/Tools/Draft/DraftSession.cs \
  src/Game/ToolController.cs src/Game/GhostView.cs tests/
git commit -m "feat: unlock negative elevation — [-50, +50] clamp, ⬇ readout + ghost badges"
```

---

### Task 4: StructureView — cut walls, tunnels, portals

**Files:**
- Modify: `src/Game/StructureView.cs` (whole `BuildStructures` body)
- Modify: `src/Game/Materials.cs` (add `RetainingWall`)
- Modify: `src/Game/GhostView.cs:242` (pass `covered: false` explicitly — drafts always commit uncovered, so the ghost showing an open cut IS WYSIWYG)

**Interfaces:**
- Produces: `BuildStructures(in Bezier3 curve, ArcLengthTable arc, float width, bool covered = false)` (obstacle predicate arrives in Task 6). Instance path passes `edge.Covered`.
- Consumes: `GeoConstants.PortalDepth`, `RoadEdge.Covered`.

- [ ] **Step 1: Implement the banding** (visual code — verification is the gallery, no unit test)

In `BuildStructures`, the gate and per-span classification change. Sampling loop: replace
`if (p.Y > 0.05f) anyElevated = true;` with `if (MathF.Abs(p.Y) > 0.05f) anyStructure = true;`
(rename the local). Span loop — the below-ground half mirrors the above-ground half:

```csharp
        for (int i = 0; i < n; i++)
        {
            float midY = (pts[i].Y + pts[i + 1].Y) / 2f;
            float prevMidY = i > 0 ? (pts[i - 1].Y + pts[i].Y) / 2f : midY;
            if (MathF.Abs(midY) <= 0.05f) { sincePillar = PillarEvery; continue; }

            if (midY > 0.05f)
            {
                // ---- above ground: existing embankment/bridge/pillar code, verbatim ----
            }
            else
            {
                bool tunnel = covered && midY <= -GeoConstants.PortalDepth;
                if (!tunnel)
                {
                    // open cut: retaining wall from the ground lip DOWN to the deck edge
                    foreach (float dir in stackalloc float[] { -1f, 1f })
                    {
                        var a = pts[i] + side[i] * (half * dir);
                        var b = pts[i + 1] + side[i + 1] * (half * dir);
                        AddQuad(wall, a with { Y = 0 }, b with { Y = 0 }, b, a);
                        // narrow ground-lip coping so the cut reads in top-down shots
                        var ao = a + side[i] * (0.6f * dir);
                        var bo = b + side[i + 1] * (0.6f * dir);
                        AddQuad(wall, ao with { Y = 0 }, bo with { Y = 0 },
                            b with { Y = 0 }, a with { Y = 0 });
                    }
                    anyWall = true;
                }
                else if (prevMidY > -GeoConstants.PortalDepth)
                {
                    // portal face where the covered deck crosses PortalDepth going down
                    AddPortal(wall, pts[i], side[i], half);
                    anyWall = true;
                }
                // NOTE the mirror case: when the NEXT span rises back above PortalDepth,
                // emit the exit portal there (check midY vs nextMidY symmetric to above).
                // Portals appear ONLY at internal depth crossings — never at curve ends,
                // so chains of covered edges (splits) don't sprout portals mid-tunnel.
                sincePillar = PillarEvery; // no pillars below ground
            }
        }
```

`AddPortal` — a rectangular face with wing walls, enough to read as a tunnel mouth:

```csharp
    private static void AddPortal(SurfaceTool st, Vector3 deck, Vector3 side, float half)
    {
        float h = GeoConstants.PortalDepth + 1.5f;  // face reaches above the deck
        foreach (float dir in stackalloc float[] { -1f, 1f })
        {
            var edge = deck + side * (half * dir);
            var wing = deck + side * ((half + 2.5f) * dir);
            AddQuad(st, edge with { Y = 0 }, wing with { Y = 0 }, wing, edge); // wing wall
        }
        var l = deck - side * half; var r = deck + side * half;
        AddQuad(st, l with { Y = l.Y + h }, r with { Y = r.Y + h }, r, l);    // face
    }
```

Wire a third `SurfaceTool wall` committed with `Materials.RetainingWall`; keep earth +
concrete surfaces as-is. The instance path (line 77-78) becomes:

```csharp
    private ArrayMesh? BuildStructuresFor(RoadEdge edge)
        => BuildStructures(edge.Curve, edge.ArcLength, RoadCatalog.Get(edge.Type).Width,
            edge.Covered);
```

`Materials.cs` (match the file's existing `StandardMaterial3D` style, e.g. Concrete):

```csharp
    public static readonly StandardMaterial3D RetainingWall = new()
    {
        AlbedoColor = new Color(0.48f, 0.47f, 0.45f),
        Roughness = 0.9f,
    };
```

- [ ] **Step 2: Build + eyeball via gallery scratch run**

Run: `dotnet build citybuilder.sln` then
`CITYBUILDER_SHOTS=tests/visual/shots godot .` — existing `bridge_*` shots must be
unchanged (above-ground code path untouched). Read the produced PNGs.
Expected: build green; bridge shots identical to committed ones.

- [ ] **Step 3: Commit**

```bash
git add src/Game/StructureView.cs src/Game/Materials.cs src/Game/GhostView.cs
git commit -m "feat(game): below-ground structures — retaining-wall cuts, tunnel spans, portal faces"
```

---

### Task 5: X-ray view mode

**Files:**
- Modify: `src/Game/Main.cs` (`BuildGround` line 274, `_UnhandledInput` line 148, `_Process`)
- Modify: `src/Game/RoadNetworkView.cs` (per-edge `SetXRay` dimming)
- Modify: `src/Game/StructureView.cs` (same dimming hook)
- Modify: `src/Game/ToolController.cs` (expose `DraftBelowGround`)

**Interfaces:**
- Produces: `Main` keeps `_xrayManual` (U key) and applies
  `xrayActive = _xrayManual || _controller.DraftBelowGround` each frame;
  `RoadNetworkView.SetXRay(bool)` / `StructureView.SetXRay(bool)`;
  `ToolController.DraftBelowGround : bool`.
- Consumes: Godot `GeometryInstance3D.Transparency` (per-instance, no material swap needed).

- [ ] **Step 1: Implement**

`ToolController.cs`:

```csharp
    /// <summary>True while a road tool is active with a below-ground elevation —
    /// Main auto-engages x-ray so the player can see what they're digging (M8.5).</summary>
    public bool DraftBelowGround => IsRoadMode && _session.CurrentElevation < -0.01f;
```

`Main.cs` — ground gets a second, translucent material; `BuildGround` returns the
instance, Main keeps a reference plus both materials and swaps `MaterialOverride`:

```csharp
    private MeshInstance3D _ground = null!;
    private Material _groundOpaque = null!, _groundXray = null!;
    private bool _xrayManual, _xrayActive;
```

The x-ray ground shader is the existing one with two added lines
(`render_mode blend_mix;` under `shader_type spatial;` and `ALPHA = 0.22;` at the end
of `fragment()`); build both materials in `BuildGround`, store, default opaque.

```csharp
    private void ApplyXRay(bool on)
    {
        _xrayActive = on;
        _ground.MaterialOverride = on ? _groundXray : _groundOpaque;
        _roadView.SetXRay(on);
        _structures.SetXRay(on);
    }
```

In `_Process`: `bool want = _xrayManual || _controller.DraftBelowGround; if (want != _xrayActive) ApplyXRay(want);`
In `_UnhandledInput`, alongside the F5/F9 cases:
`case InputEventKey { Keycode: Key.U, Pressed: true }: _xrayManual = !_xrayManual; break;`

`RoadNetworkView.SetXRay` — dim surface/elevated edges so underground reads through
(mirror the class's per-edge instance dictionary; apply on rebuild too while active):

```csharp
    private bool _xray;
    public void SetXRay(bool on)
    {
        _xray = on;
        foreach (var (id, inst) in _instances)
            inst.Transparency = Dim(id, on);
    }
    private float Dim(EdgeId id, bool on)
        => on && _network.Edges.TryGetValue(id, out var e)
           && (e.Curve.P0.Y > -0.05f || e.Curve.P3.Y > -0.05f) ? 0.55f : 0f;
```

(Adapt the dictionary/field names to the class's actual ones; same pattern in
`StructureView` — its `_instances` is `Dictionary<EdgeId, MeshInstance3D>`, line 22.
Underground carriageways were always meshed — the opaque ground simply hid them, so
x-ray needs no new geometry.)

- [ ] **Step 2: Build + interactive sanity**

Run: `dotnet build citybuilder.sln`, then `CITYBUILDER_SMOKE=1 godot --headless .`
Expected: build green, SMOKE OK (x-ray must not disturb the scripted scenario).

- [ ] **Step 3: Commit**

```bash
git add src/Game/Main.cs src/Game/RoadNetworkView.cs src/Game/StructureView.cs src/Game/ToolController.cs
git commit -m "feat(game): x-ray view — translucent ground + surface dimming, U toggle, auto while digging"
```

---

### Task 6: Pillar placement awareness

**Files:**
- Modify: `src/Game/StructureView.cs` (`BuildStructures` predicate param + placement loop; new `CarriagewayObstructed` helper)
- Modify: `src/Game/GhostView.cs:242` (pass the same predicate so preview = commit)

**Interfaces:**
- Produces: `BuildStructures(..., bool covered = false, Func<Vector3, bool>? pillarObstructed = null)`;
  `StructureView.CarriagewayObstructed(RoadNetwork n, EdgeId? self, Vector3 groundPos) : bool`.
- Consumes: `RoadNetwork.FindClosestEdge(Vector3 p, float maxDist) : (EdgeId id, float t, float dist)?` (RoadNetwork.cs:57).

- [ ] **Step 1: Implement the predicate**

```csharp
    /// <summary>True when a pillar footed at groundPos (XZ) would stand inside another
    /// edge's carriageway — the M8 "pillar in the underpass" known limit. The deck
    /// there must be BELOW the pillar's own deck (the crossing road, not the bridge
    /// being supported).</summary>
    public static bool CarriagewayObstructed(RoadNetwork network, EdgeId? self, Vector3 groundPos)
    {
        var probe = new System.Numerics.Vector3(groundPos.X, 0, groundPos.Z);
        if (network.FindClosestEdge(probe, 12f) is not { } hit || hit.id == self)
            return false;
        var e = network.Edges[hit.id];
        float half = RoadCatalog.Get(e.Type).Width / 2f;
        var at = e.Curve.Point(e.ArcLength.TAtDistance(
            e.ArcLength.TotalLength * hit.t));   // NOTE: if hit.t is already a curve t
                                                 // (check FindClosestEdge), use Point(hit.t)
        bool underPillar = at.Y < groundPos.Y - 0.5f || at.Y <= 0.5f;
        return hit.dist <= half + PillarHalf + 1f && underPillar;
    }
```

Read `FindClosestEdge` (RoadNetwork.cs:57-80) to confirm whether `t` is a curve
parameter or arc fraction and simplify accordingly.

Placement loop change — a blocked eligible spot defers instead of placing (the pillar
"shifts along the span" to the next clear spot; a long obstruction skips it):

```csharp
            sincePillar += len / n;
            if (bridge && midY >= PillarMinClear && sincePillar >= PillarEvery)
            {
                var top = (pts[i] + pts[i + 1]) / 2f;
                if (pillarObstructed?.Invoke(top with { Y = 0 }) == true)
                {
                    if (sincePillar > 2f * PillarEvery)
                        sincePillar = 0;         // give up on this stretch — skip one
                    // else: keep accumulating, next span retries (the shift)
                }
                else
                {
                    sincePillar = 0;
                    AddPillar(concrete, top with { Y = top.Y - FasciaDepth / 2f }, side[i]);
                    anyConcrete = true;
                }
            }
```

Instance path: `BuildStructures(..., edge.Covered, p => CarriagewayObstructed(_network, edge.Id, p))`.
`GhostView` line 242: it has no committed EdgeId — pass
`p => StructureView.CarriagewayObstructed(_network, null, p)` (GhostView already binds
the network for validation display; if not, thread it through `Bind`).

- [ ] **Step 2: Build + gallery check**

Run: `dotnet build citybuilder.sln` + rerun the shots harness; read `bridge_low.png` —
pillar spacing on the plain bridge scenario must look unchanged (no obstacles there).
Expected: green, unchanged bridge shots. The new `underpass_pillars` scene lands in Task 10.

- [ ] **Step 3: Commit**

```bash
git add src/Game/StructureView.cs src/Game/GhostView.cs
git commit -m "feat(game): pillar placement awareness — defer/skip pillars landing in a carriageway"
```

---

### Task 7: Upgrade-tool covered toggle (UI + UITEST)

**Files:**
- Modify: `src/Game/ToolController.cs` (Upgrade click branch, lines 238-256; hover readout line 214-215)
- Modify: `src/Game/Toolbar.cs` (checkbox near the Adjust toggle, ~line 115)
- Modify: `src/Game/Main.cs` (`RunUiTest`, ~line 300 — extend the scripted flow)

**Interfaces:**
- Produces: `ToolController.CoveredToggleActive : bool` (settable).
- Consumes: `RoadNetwork.SetCovered` (Task 1).

- [ ] **Step 1: Implement**

`ToolController.cs`:

```csharp
    /// <summary>Toolbar "Covered" toggle: while active, Upgrade-mode LMB toggles the
    /// hovered edge's tunnel cover instead of retyping (M8.5).</summary>
    public bool CoveredToggleActive { get; set; }
```

Upgrade click branch — insert before the retype call:

```csharp
            if (_upgradeTarget is { } upTarget)
            {
                if (CoveredToggleActive)
                {
                    _undoStack?.Checkpoint();
                    bool now = !_network.Edges[upTarget].Covered;
                    if (_network.SetCovered(upTarget, now))
                    {
                        _audio?.Play(Sfx.Commit);
                        StatusFlashed?.Invoke(now ? "covered (tunnel)" : "open cut");
                    }
                    else
                    {
                        _audio?.Play(Sfx.Reject);
                        StatusFlashed?.Invoke("cannot toggle cover here");
                    }
                    return;
                }
                // ...existing retype path unchanged
```

Hover readout (line 214-215): when `CoveredToggleActive`, say
`"click: toggle covered · right-click: flip direction"`.

`Toolbar.cs` — next to the Adjust checkbox (line 115 pattern):

```csharp
        var coveredToggle = new CheckBox { Text = "Covered (tunnel)" };
        coveredToggle.Toggled += on => _controller.CoveredToggleActive = on;
        box.AddChild(coveredToggle);
```

`Main.RunUiTest` — after the existing flow: draw a below-ground road, switch to
Upgrade with the toggle on, click the edge, assert `network.Edges[...].Covered` and
`GD.Print("UITEST covered OK")` before the screenshot (mirror the file's existing
scripted-click style at line 300+).

- [ ] **Step 2: Build + run UITEST**

Run: `dotnet build citybuilder.sln` then `CITYBUILDER_UITEST=/tmp/claude-1000/-mnt-HDD-hobbies-projects-citybuilder/8896ec36-0752-4c61-9482-46eec8af3061/scratchpad/ui.png godot .`
Expected: console prints the UITEST OK lines incl. `UITEST covered OK`; read the PNG —
toolbar shows the new checkbox.

- [ ] **Step 3: Commit**

```bash
git add src/Game/ToolController.cs src/Game/Toolbar.cs src/Game/Main.cs
git commit -m "feat(game): upgrade-tool Covered toggle + UITEST coverage"
```

---

### Task 8: Fuzzer — negative elevation + ToggleCovered action

**Files:**
- Modify: `tests/Domain.Tests/Fuzzing/GestureFuzzer.cs` (pick table line 82-93, `DrawGesture` line 196, new `ToggleCovered`)
- Test: `tests/Domain.Tests/Fuzzing/FuzzRegressionTests.cs` (rerun all pins; triage any change)

**Interfaces:**
- Consumes: `SetCovered`, negative `CurrentElevation`.

- [ ] **Step 1: Extend the alphabet**

`DrawGesture` line 196 — signed elevations:

```csharp
        session.CurrentElevation = rng.Next(100) < 60 ? 0f
            : (rng.Next(2) == 0 ? 5f : -5f) * rng.Next(1, 11);
```

Pick table — carve ToggleCovered out of the undo/snap tail (full replacement block):

```csharp
                int pick = rng.Next(100);
                if (pick < 42) { undo.Checkpoint(); DrawGesture(session, network, rng, Log); }
                else if (pick < 56) { undo.Checkpoint(); Bulldoze(network, rng, Log); }
                else if (pick < 65) { undo.Checkpoint(); ConfigureJunctionAction(network, rng, Log); }
                else if (pick < 72) { undo.Checkpoint(); Retype(network, rng, Log); }
                else if (pick < 77) { undo.Checkpoint(); Flip(network, rng, Log); }
                else if (pick < 82) { undo.Checkpoint(); ConvertRoundabout(network, rng, Log); }
                else if (pick < 85) { undo.Checkpoint(); AdjustRoundaboutRadius(network, rng, Log); }
                else if (pick < 87) { undo.Checkpoint(); RemoveRoundaboutAction(network, rng, Log); }
                else if (pick < 90) { undo.Checkpoint(); ToggleCovered(network, rng, Log); }
                else if (pick < 95) UndoRedo(undo, session, rng, Log);
                else if (pick < 98) ToggleSnap(session, rng, Log);
                else StepBackCancel(session, network, rng, Log);
```

New action (mirror `Flip`'s shape at line 245-251):

```csharp
    private static void ToggleCovered(RoadNetwork network, Random rng, Action<string> log)
    {
        if (network.Edges.Count == 0) return;
        var edge = network.Edges.Values.ElementAt(rng.Next(network.Edges.Count));
        bool ok = network.SetCovered(edge.Id, !edge.Covered);
        log($"cover edge={edge.Id.Value} now={!edge.Covered} ok={ok}");
    }
```

- [ ] **Step 2: Rerun every pin + a medium sweep**

Run: `dotnet test --filter "FullyQualifiedName~FuzzRegressionTests"` then
`CITYBUILDER_FUZZ_ACTIONS=2000 dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"`
Expected: pins green (alphabet changes shift seed streams — that is the standing
convention from M8; the *deterministic* pins carry the true regressions). Sweep green.
Any finding = domain bug: fix at root cause + pin, per the triage protocol in
`docs/verification.md`.

- [ ] **Step 3: Commit**

```bash
git add tests/Domain.Tests/Fuzzing/GestureFuzzer.cs tests/Domain.Tests/Fuzzing/FuzzRegressionTests.cs
git commit -m "test(fuzz): signed elevations + ToggleCovered in the action alphabet"
```

---

### Task 9: Fuzz perf pass (queued since M8 — target ≥ 2×)

**Files:**
- Modify: `src/Domain/Network/Entities.cs` (lazy `MaxGradient` cache on `RoadEdge`)
- Modify: `src/Domain/Network/NetworkInvariants.cs` (use the cache, line 229; incremental crossing scan, line 66)
- Modify: `tests/Domain.Tests/Fuzzing/GestureFuzzer.cs` (hold the crossing cache across actions)

**Interfaces:**
- Produces: `RoadEdge.MaxGradient : float` (lazy, cached — edges are immutable-after-construction, `Covered` aside, which doesn't affect geometry); `NetworkInvariants.Check(RoadNetwork n, CrossingCache? cache = null)`.

- [ ] **Step 1: Measure the baseline**

Run: `time CITYBUILDER_FUZZ_ACTIONS=2000 dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"`
Record wall time in the working notes — it goes in `docs/health/M8.5.md`.

- [ ] **Step 2: Implement the two caches**

`RoadEdge` (curve is readonly ⇒ result is stable):

```csharp
    private float _maxGradient = float.NaN;
    /// <summary>Lazy cached max |dY/ds| — the invariant audit asks per action, the
    /// curve never changes (M8.5 fuzz perf pass).</summary>
    public float MaxGradient => float.IsNaN(_maxGradient)
        ? _maxGradient = VerticalRules.MaxGradient(Curve, ArcLength.TotalLength)
        : _maxGradient;
```

`NetworkInvariants.CheckEdgeGeometry` line 229: use `e.MaxGradient` instead of
recomputing. `RoadNetwork.RetypeEdge` line 946 and `TryHealNode` line 813 may also use
the cache where they already hold the edge.

Incremental crossing scan — `CheckEdgeCrossings` is O(E²) per fuzz action. Because
edges are immutable instances, a pair cleared once stays cleared until either instance
is replaced:

```csharp
/// <summary>Reusable memo for CheckEdgeCrossings: pairs keyed by EdgeId whose exact
/// RoadEdge INSTANCES have already been checked clean. Any edit that replaces an edge
/// (split, retype, flip, restore) yields a new instance, so staleness is impossible.
/// Violations are never cached — they must keep firing.</summary>
public sealed class CrossingCache
{
    internal readonly Dictionary<(int a, int b), (RoadEdge ea, RoadEdge eb)> Clean = new();
}
```

In the pair loop: skip when `cache.Clean` holds the same two instances
(`ReferenceEquals` both); on a clean result, store. `GestureFuzzer.Run` allocates one
`CrossingCache` before the action loop and passes it to every audit.

- [ ] **Step 3: Verify correctness, then measure**

Run: `dotnet test --filter "FullyQualifiedName!~Fuzz"` (green), the pins, then
`time CITYBUILDER_FUZZ_ACTIONS=2000 dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"`
Expected: identical findings (none), wall time ≥ 2× better than Step 1. If not, profile
what remains (`dotnet-trace` or timestamp logging around the audit) before adding
anything cleverer — no invariant weakening allowed.

- [ ] **Step 4: Commit**

```bash
git add src/Domain/Network/Entities.cs src/Domain/Network/NetworkInvariants.cs \
  src/Domain/Network/RoadNetwork.cs tests/Domain.Tests/Fuzzing/GestureFuzzer.cs
git commit -m "perf(fuzz): cached edge gradients + instance-keyed crossing memo (≥2x audit speedup)"
```

---

### Task 10: Certification — gallery, smoke, 3×10k fuzz, KPI, docs

**Files:**
- Modify: `src/Game/VisualShots.cs` (new scenarios), `src/Game/Main.cs` (`RunSmoke` ~line 428)
- Create: `docs/health/M8.5.md`
- Modify: `docs/manual/` (ch10 or new ch11), `docs/roadmap.md`, `docs/conventions.md` (new constants)

- [ ] **Step 1: Gallery scenarios** (follow the `Scenario`/`Shot` records, VisualShots.cs:167-183)

Add scenarios building with negative Y (same builder style as the `bridge_*` scenario
around line 205): `trench` (a −3 m road, walls + coping), `tunnel` (a −8 m covered road
with ramps — two portals), `tunnel_xray` (same network, `ApplyXRay(true)` via the
scenario's post-build hook, oblique + top shots), `underpass_pillars` (the M8 bridge
scenario plus a road under the deck — shots must show no pillar in the carriageway).
Run `CITYBUILDER_SHOTS=tests/visual/shots godot .` and **read every new PNG**: walls
reach exactly Y0, portals only at depth crossings, no pillar in the underpass, x-ray
shows the tunnel carriageway.

- [ ] **Step 2: Smoke scenario extension** (`RunSmoke`, mirror the bridge section at ~line 593-617)

Script: draw a −8 m road under an existing arterial (no junction may form — assert edge
counts), toggle covered via `_controller` (Upgrade + `CoveredToggleActive`), F5/F9
round-trip, assert the flag survived, toggle x-ray on/off, `GD.Print("SMOKE OK")`.
Run: `CITYBUILDER_SMOKE=1 godot --headless .` → SMOKE OK.

- [ ] **Step 3: Full certification runs**

```bash
CITYBUILDER_FUZZ_ACTIONS=10000 dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"  # 3×10k
dotnet test           # full suite incl. deep pins (~35 min)
```

Both green. Triage any fuzz finding per protocol (fix root cause, pin, rerun).

- [ ] **Step 4: KPI + health doc**

Regenerate the KPI baseline (commands in `docs/verification.md`; traffic KPIs should be
byte-identical — zero sim changes — which is itself the headline check). Write
`docs/health/M8.5.md`: KPI table vs M8, fuzz wall-clock before/after the perf pass
(Task 9 numbers), certification inventory.

- [ ] **Step 5: Docs drift**

Manual: extend ch10 (elevation) or add ch11 for trenches/tunnels — covered-flag
semantics, propagation rules, portal placement rule, x-ray, pillar awareness, the
"surface road over an uncovered deep trench" known limit. Update `docs/conventions.md`
(MaxDepth, PortalDepth, save v3) and `docs/roadmap.md` (M8.5 done entry + known
limits; next up: zoning).

- [ ] **Step 6: Final commit**

```bash
git add src/Game/VisualShots.cs src/Game/Main.cs docs/health/M8.5.md docs/manual/ \
  docs/roadmap.md docs/conventions.md tests/visual/
git commit -m "feat(m8.5): trenches & tunnels certified — gallery, smoke, 3x10k fuzz, KPI, manual"
```

---

## Self-Review Notes

- **Spec coverage:** covered flag + propagation (T1), save v3 (T2), editor unlock +
  badges (T3), cut/tunnel/portal structures (T4), x-ray auto+toggle (T5), pillar
  awareness (T6), upgrade-toolbar toggle + UITEST (T7), fuzz alphabet (T8), perf pass
  (T9), full DoD (T10). Spec's "portals only at internal depth crossings" rule is in
  T4; "mixed heal → uncovered" in T1; "ring edges refuse" in T1's SetCovered.
- **Type consistency:** `SetCovered(EdgeId, bool) : bool` everywhere (T1/T7/T8);
  `BuildStructures(curve, arc, width, covered, pillarObstructed)` (T4/T6);
  `CarriagewayObstructed(RoadNetwork, EdgeId?, Vector3)` (T6).
- Line numbers are anchors as of commit `48994c8` — re-grep if drifted.
