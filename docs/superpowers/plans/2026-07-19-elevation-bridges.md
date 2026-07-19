# Elevation & Bridges (M8) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Roads gain CS2-style step-authored elevation with per-type gradient limits; XZ crossings classify by vertical separation so a bridge passes over a road with no junction; bridges render as decks with pillars.

**Architecture:** Derived structures over signed Y — elevation lives in the node/curve Y the domain already carries and serializes; a single `VerticalRules` classification helper drives Validate, Commit, the commit-side segment recheck, and the invariant identically; rendering derives embankment/deck/pillar spans per edge from height above the flat Y=0 ground. No new entities, no save-format change, no expected sim change.

**Tech Stack:** C# (net8.0 domain, System.Numerics only), Godot 4.7 mono game layer, xUnit (net10.0).

## Global Constraints

- Domain purity: `src/Domain` never references Godot (golden rule 1).
- Verify per change: `dotnet test` → `dotnet build citybuilder.sln` → matching harness (golden rule 2).
- Milestone DoD: fuzz 3×10k green (alphabet extended with elevation), KPI baseline + `docs/health/M8.md`, manual drift + new ch10, roadmap current.
- Constants (spec-locked): `JunctionYTolerance = 0.6f`, `MinClearance = 4.7f`, `EmbankmentMax = 1.0f`, `MaxElevation = 50f`, steps 5 m / 1 m (Ctrl).
- Gradients: Street/OneWay **0.10**, TwoLane/Asymmetric **0.08**, FourLane/Avenue **0.06** (`RoadType.MaxGradient`).
- Editor clamps elevation to [0, `MaxElevation`] in M8; the domain accepts signed Y.
- Ground is the Y=0 plane (flat-world assumption; phrase rules against "ground" so terrain can slot in later).
- Commit at every green step; explicit `git add` paths (no `git add -A` — user backup files sit untracked in the tree).

---

### Task 1: Vertical constants, `MaxGradient`, and the `VerticalRules` classifier

**Files:**
- Modify: `src/Domain/Geometry/GeoConstants.cs` (add vertical constants)
- Modify: `src/Domain/Catalog/RoadType.cs` (add `MaxGradient` positional param; update all six catalog entries)
- Create: `src/Domain/Geometry/VerticalRules.cs` (pure classification + gradient sampling)
- Modify: `src/Domain/Tools/PlacementProposal.cs` (`PlacementError.TooSteep`, `PlacementError.VerticalClash`)
- Test: `tests/Domain.Tests/Geometry/VerticalRulesTests.cs`

**Interfaces:**
- Produces:
  - `GeoConstants.JunctionYTolerance = 0.6f`, `.MinClearance = 4.7f`, `.EmbankmentMax = 1.0f`, `.MaxElevation = 50f`
  - `RoadType.MaxGradient` (float, after `MinRadius` in the positional record)
  - `enum CrossingKind { Junction, GradeSeparated, VerticalClash }`
  - `static CrossingKind VerticalRules.ClassifyCrossing(float yNew, float yExisting)` — |Δ| < JunctionYTolerance → Junction; ≥ MinClearance → GradeSeparated; else VerticalClash
  - `static float VerticalRules.MaxGradient(in Bezier3 c)` — max sampled |dY/ds| (adaptive count like `BezierOps.MinRadius`: one sample per ~8 m arc, floor 32)
  - `PlacementError.TooSteep`, `PlacementError.VerticalClash`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using CityBuilder.Domain.Geometry;
using Xunit;

namespace CityBuilder.Domain.Tests.Geometry;

public class VerticalRulesTests
{
    [Theory]
    [InlineData(0f, 0f, CrossingKind.Junction)]
    [InlineData(0.59f, 0f, CrossingKind.Junction)]
    [InlineData(0.61f, 0f, CrossingKind.VerticalClash)]
    [InlineData(4.69f, 0f, CrossingKind.VerticalClash)]
    [InlineData(4.7f, 0f, CrossingKind.GradeSeparated)]
    [InlineData(0f, 12f, CrossingKind.GradeSeparated)]
    [InlineData(10f, 12f, CrossingKind.VerticalClash)]
    public void ClassifiesByVerticalSeparation(float yNew, float yOld, CrossingKind expected)
        => Assert.Equal(expected, VerticalRules.ClassifyCrossing(yNew, yOld));

    [Fact]
    public void FlatCurveHasZeroGradient()
        => Assert.Equal(0f, VerticalRules.MaxGradient(Bezier3.Line(new(0, 0, 0), new(100, 0, 0))), 3);

    [Fact]
    public void UniformRampGradientMatchesRiseOverRun()
    {
        // 100 m run, 8 m rise, linear Y → gradient ~0.08 everywhere
        var c = new Bezier3(new(0, 0, 0), new(33.3f, 2.667f, 0), new(66.7f, 5.333f, 0), new(100, 8, 0));
        Assert.Equal(0.08f, VerticalRules.MaxGradient(c), 2);
    }

    [Fact]
    public void EndLoadedProfileExceedsItsAverageGradient()
    {
        // all 8 m of rise crammed into the last third → local gradient >> 8%
        var c = new Bezier3(new(0, 0, 0), new(33.3f, 0, 0), new(66.7f, 0, 0), new(100, 8, 0));
        Assert.True(VerticalRules.MaxGradient(c) > 0.15f);
    }
}
```

- [ ] **Step 2: Run to verify fail** — `dotnet test --filter "FullyQualifiedName~VerticalRulesTests"`; FAIL (compile: `VerticalRules`/`CrossingKind` undefined).

- [ ] **Step 3: Implement**

`GeoConstants.cs` — append:
```csharp
    /// <summary>Crossings/legs within this ΔY are coplanar: they junction (M8).</summary>
    public const float JunctionYTolerance = 0.6f;

    /// <summary>ΔY at an XZ crossing at or above this is grade-separated: legal, no junction (M8).</summary>
    public const float MinClearance = 4.7f;

    /// <summary>Deck height above ground below which a road renders as embankment, above as bridge (M8).</summary>
    public const float EmbankmentMax = 1.0f;

    /// <summary>Editor elevation clamp (domain itself is unclamped/signed) (M8).</summary>
    public const float MaxElevation = 50f;
```

`VerticalRules.cs`:
```csharp
using System.Numerics;

namespace CityBuilder.Domain.Geometry;

/// <summary>How two roads relate where their XZ projections cross (M8). The three
/// bands are the semantic heart of elevation: coplanar crossings junction exactly as
/// pre-M8, cleared crossings pass over/under with no junction, and the band between is
/// never legal. ONE classifier feeds Validate, Commit, the commit-side segment recheck,
/// and NetworkInvariants — thresholds must never be re-derived at call sites.</summary>
public enum CrossingKind { Junction, GradeSeparated, VerticalClash }

public static class VerticalRules
{
    public static CrossingKind ClassifyCrossing(float yNew, float yExisting)
    {
        float dy = MathF.Abs(yNew - yExisting);
        if (dy < GeoConstants.JunctionYTolerance)
            return CrossingKind.Junction;
        return dy >= GeoConstants.MinClearance ? CrossingKind.GradeSeparated : CrossingKind.VerticalClash;
    }

    /// <summary>Max |dY/ds| sampled along the curve; sample count scales with arc
    /// length (~one per 8 m, floor 32) like BezierOps.MinRadius, so short and long
    /// ramps get equal resolution.</summary>
    public static float MaxGradient(in Bezier3 c)
    {
        int samples = Math.Max(32, (int)(c.Length() / 8f));
        float worst = 0f;
        var prev = c.Point(0);
        for (int i = 1; i <= samples; i++)
        {
            var p = c.Point(i / (float)samples);
            float run = new Vector2(p.X - prev.X, p.Z - prev.Z).Length();
            if (run > GeoConstants.Eps)
                worst = MathF.Max(worst, MathF.Abs(p.Y - prev.Y) / run);
            prev = p;
        }
        return worst;
    }
}
```

`RoadType.cs` — add `float MaxGradient` after `MinRadius` in the record and per entry: TwoLane `0.08f`, FourLane `0.06f`, Street `0.10f`, Avenue `0.06f`, OneWay `0.10f`, Asymmetric `0.08f`.

`PlacementProposal.cs` — extend the enum: `..., Kinked, TouchesRoundabout, TooSteep, VerticalClash,`.

- [ ] **Step 4: Run to verify pass** — filter PASS; then `dotnet build citybuilder.sln` (catalog constructor updates compile everywhere).

- [ ] **Step 5: Commit**
```bash
git add src/Domain/Geometry/GeoConstants.cs src/Domain/Geometry/VerticalRules.cs src/Domain/Catalog/RoadType.cs src/Domain/Tools/PlacementProposal.cs tests/Domain.Tests/Geometry/VerticalRulesTests.cs
git commit -m "feat(domain): vertical constants, per-type MaxGradient, VerticalRules crossing classifier"
```

---

### Task 2: Validate — gradients, crossing classification, coplanar endpoints

**Files:**
- Modify: `src/Domain/Network/RoadNetwork.cs` (Validate: TooSteep; classify crossings; endpoint-Y check in `BindingLeavesSliver`-adjacent flow)
- Test: `tests/Domain.Tests/Network/ElevationValidationTests.cs`

**Interfaces:**
- Consumes: `VerticalRules.ClassifyCrossing/MaxGradient`, `RoadCatalog.Get(type).MaxGradient`.
- Produces: Validate emits `TooSteep` when any proposed curve exceeds its type's gradient; per XZ intersection: `Junction` → exactly today's checks (angle floor, slivers), `GradeSeparated` → intersection fully ignored (no crossing entry, no sliver, no ghost marker), `VerticalClash` → `PlacementError.VerticalClash`. An `AtNode`/`OnEdge`/near-node-resolving endpoint whose curve end Y differs from the target's Y beyond `JunctionYTolerance` → `VerticalClash`.

- [ ] **Step 1: Failing tests**

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class ElevationValidationTests
{
    private static Bezier3 Ramp(Vector3 a, Vector3 b) => new(
        a, a + (b - a) / 3f, a + (b - a) * (2f / 3f), b); // linear XYZ interpolation

    private static PlacementProposal One(Bezier3 c, RoadTypeId? type = null,
        EndpointBinding? start = null, EndpointBinding? end = null)
        => new(new[] { new ProposedCurve(c, start ?? EndpointBinding.None, end ?? EndpointBinding.None) },
            type ?? RoadCatalog.TwoLane.Id);

    [Fact]
    public void GentleRampIsValid()
    {
        var n = Net.New();
        var v = n.Validate(One(Ramp(new(0, 0, 0), new(100, 6, 0)))); // 6%
        Assert.True(v.IsValid, string.Join(",", v.Errors));
    }

    [Fact]
    public void SteepRampIsTooSteep()
    {
        var n = Net.New();
        var v = n.Validate(One(Ramp(new(0, 0, 0), new(100, 12, 0)))); // 12% > TwoLane 8%
        Assert.Contains(PlacementError.TooSteep, v.Errors);
    }

    [Fact]
    public void GradientLimitIsPerType()
    {
        var n = Net.New();
        // 9% is TooSteep for TwoLane (8%) but fine for Street (10%)
        Assert.Contains(PlacementError.TooSteep,
            n.Validate(One(Ramp(new(0, 0, 0), new(100, 9, 0)), RoadCatalog.TwoLane.Id)).Errors);
        Assert.True(n.Validate(One(Ramp(new(0, 0, 0), new(100, 9, 0)), RoadCatalog.Street.Id)).IsValid);
    }

    [Fact]
    public void BridgeOverRoadIsValidWithNoCrossingMarker()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        // level +6 m bridge crossing above the road
        var v = n.Validate(One(Ramp(new(0, 6, -80), new(0, 6, 80))));
        Assert.True(v.IsValid, string.Join(",", v.Errors));
        Assert.Empty(v.CrossingPoints); // grade-separated: not a crossing at all
    }

    [Fact]
    public void ClashBandIsRefused()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var v = n.Validate(One(Ramp(new(0, 2, -80), new(0, 2, 80)))); // 2 m over: clash
        Assert.Contains(PlacementError.VerticalClash, v.Errors);
    }

    [Fact]
    public void CoplanarCrossingStillJunctions()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var v = n.Validate(One(Ramp(new(0, 0.3f, -80), new(0, 0.3f, 80)))); // within 0.6 m
        Assert.True(v.IsValid, string.Join(",", v.Errors));
        Assert.Single(v.CrossingPoints);
    }

    [Fact]
    public void EndpointBindingMustMeetNodeElevation()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var node = n.Edges[r.CreatedEdges[0]].StartNode; // ground node at (-100,0,0)
        // a curve ending 8 m above that node cannot bind to it
        var v = n.Validate(One(Ramp(new(-100, 8, 80), new(-100, 8, 0)),
            end: new EndpointBinding.AtNode(node)));
        Assert.Contains(PlacementError.VerticalClash, v.Errors);
    }
}
```

- [ ] **Step 2: Run to verify fail** — bridge/clash/coplanar/binding tests FAIL (bridge case today reports a crossing or sliver; clash case passes wrongly; TooSteep undefined behavior).

- [ ] **Step 3: Implement in `Validate`**

In the per-curve prologue (beside the MinRadius check):
```csharp
            if (VerticalRules.MaxGradient(pc.Curve) > type.MaxGradient + 0.001f)
                errors.Add(PlacementError.TooSteep);
```

In the crossing loop, immediately after the endpoint-proximity `continue` and BEFORE any use of the hit:
```csharp
                var kind = VerticalRules.ClassifyCrossing(p.Y, e.Curve.Point(t2).Y);
                if (kind == CrossingKind.GradeSeparated)
                    continue; // passes over/under: not a crossing in any sense
                if (kind == CrossingKind.VerticalClash)
                {
                    clash = true;
                    continue; // no junction math for an illegal band
                }
```
with `bool clash = false` beside `shallow`, and after the loop:
```csharp
            if (clash)
                errors.Add(PlacementError.VerticalClash);
```

Endpoint coplanarity — extend the existing endpoint checks (`HasSharpLeg` call site) with:
```csharp
            if (BindingElevationClash(pc.Start, a) || BindingElevationClash(pc.End, b))
                errors.Add(PlacementError.VerticalClash);
```
and the helper beside `BindingLeavesSliver` (mirrors its resolution logic):
```csharp
    /// <summary>An endpoint that will connect to an existing node/edge must arrive at
    /// its elevation (within JunctionYTolerance) — legs of a junction are coplanar (M8).</summary>
    private bool BindingElevationClash(EndpointBinding binding, Vector3 pos)
    {
        float? targetY = binding switch
        {
            EndpointBinding.AtNode(var id) when _nodes.TryGetValue(id, out var node) => node.Position.Y,
            EndpointBinding.OnEdge(var eid, var t) when _edges.TryGetValue(eid, out var e) => e.Curve.Point(t).Y,
            EndpointBinding.Free => FindNodeNear(pos, NodeReuseRadius) is { } near
                ? _nodes[near].Position.Y
                : FindClosestEdge(pos, NodeReuseRadius) is { } hit
                    ? _edges[hit.id].Curve.Point(hit.t).Y
                    : null,
            _ => null,
        };
        return targetY is { } y && MathF.Abs(pos.Y - y) > GeoConstants.JunctionYTolerance;
    }
```
Note: `FindNodeNear`/`FindClosestEdge` measure 3D distance, so an endpoint 8 m above a
node won't "find" it as Free — the explicit `AtNode`/`OnEdge` arms carry the check.

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter "FullyQualifiedName~ElevationValidationTests"` PASS, then full `PlacementTests|GeometryGuardTests` for regressions.

- [ ] **Step 5: Commit**
```bash
git add src/Domain/Network/RoadNetwork.cs tests/Domain.Tests/Network/ElevationValidationTests.cs
git commit -m "feat(domain): Validate learns elevation — TooSteep, crossing classification, coplanar endpoint bindings"
```

---

### Task 3: Commit-side classification + stacked nodes

**Files:**
- Modify: `src/Domain/Network/RoadNetwork.cs` (`CommitCurve` crossing loop, `SegmentCrossesLiveEdgeOffNode`)
- Test: `tests/Domain.Tests/Network/ElevationCommitTests.cs`

**Interfaces:**
- Produces: committing a grade-separated curve creates **no junction, no split, no shared node** (edge counts prove it); a clash band surviving to commit (live-network divergence) drops the segment (`DroppedSegments`); `SegmentCrossesLiveEdgeOffNode` ignores grade-separated coincidences (XZ-coinciding hits whose ΔY ≥ MinClearance) so the recheck doesn't drop legal bridges.

- [ ] **Step 1: Failing tests**

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class ElevationCommitTests
{
    private static Bezier3 Ramp(Vector3 a, Vector3 b) => new(
        a, a + (b - a) / 3f, a + (b - a) * (2f / 3f), b);

    private static PlacementProposal One(Bezier3 c) => new(
        new[] { new ProposedCurve(c, EndpointBinding.None, EndpointBinding.None) },
        RoadCatalog.TwoLane.Id);

    [Fact]
    public void BridgeCommitCreatesNoJunction()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var r = Net.Commit(n, One(Ramp(new(0, 6, -80), new(0, 6, 80))));
        Assert.True(r.Success);
        Assert.Equal(0, r.DroppedSegments);
        // 1 ground edge + 1 bridge edge — the ground road was NOT split
        Assert.Equal(2, n.Edges.Count);
        Assert.Equal(4, n.Nodes.Count);
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void RampToBridgeToRampOverRoadIsBuildable()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        // up-ramp, level bridge over the road, down-ramp — drawn as three gestures
        Net.Commit(n, One(Ramp(new(0, 0, -160), new(0, 6, -60))));   // 6%
        Net.Commit(n, One(Ramp(new(0, 6, -60), new(0, 6, 60))));     // level, over the road
        Net.Commit(n, One(Ramp(new(0, 6, 60), new(0, 0, 160))));     // down
        Assert.Equal(4, n.Edges.Count); // ground road intact + 3 bridge pieces
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void StackedNodesAtSameXZAreDistinct()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, One(Ramp(new(-100, 8, 0), new(100, 8, 0)))); // directly above, +8 m
        // endpoints at the same XZ as the ground road's ends must NOT have reused them
        Assert.Equal(2, n.Edges.Count);
        Assert.Equal(4, n.Nodes.Count);
        Assert.Empty(NetworkInvariants.Check(n));
    }
}
```

- [ ] **Step 2: Run to verify fail** — bridge commit today splits the ground road (4+ edges) or the invariant flags the crossing.

- [ ] **Step 3: Implement**

`CommitCurve` crossing-collection loop — after the endpoint-proximity filter, before the ring-edge guard:
```csharp
                var kind = VerticalRules.ClassifyCrossing(p.Y, e.Curve.Point(hit.t2).Y);
                if (kind == CrossingKind.GradeSeparated)
                    continue; // passes over/under this edge: no split, no stop
                if (kind == CrossingKind.VerticalClash)
                {
                    droppedSegments++;
                    return; // never commit into the illegal band (live-divergence path)
                }
```

`SegmentCrossesLiveEdgeOffNode` — after the coincidence check (`> 0.5f continue`):
```csharp
                if (VerticalRules.ClassifyCrossing(p.Y, c.Point(t2).Y) == CrossingKind.GradeSeparated)
                    continue; // legal over/under pass, not a drive-through crossing
```
(the coincidence check uses 3D distance, so truly separated curves rarely coincide —
this guard covers the boundary where XZ-projected `Intersections` still reports a pair.
NOTE at implementation time: `Intersections` is XZ-projected, so `p` and `c.Point(t2)`
can differ in Y while coinciding in XZ — the 0.5 m coincidence check must become an
XZ-distance check here and in `NetworkInvariants.CheckEdgeCrossings`, with the Y
difference feeding `ClassifyCrossing` instead.)

- [ ] **Step 4: Run to verify pass** — new tests + `PlacementTests` + `RoundaboutTests` green; `dotnet build`.

- [ ] **Step 5: Commit**
```bash
git add src/Domain/Network/RoadNetwork.cs tests/Domain.Tests/Network/ElevationCommitTests.cs
git commit -m "feat(domain): grade-separated commits — no junction over cleared crossings, clash drops, stacked nodes"
```

---

### Task 4: Invariants — clearance clause + gradient rule

**Files:**
- Modify: `src/Domain/Network/NetworkInvariants.cs` (`CheckEdgeCrossings` XZ/Y split + clearance; new `CheckGradients` inside `CheckEdgeGeometry`'s loop)
- Test: `tests/Domain.Tests/Network/NetworkInvariantsTests.cs` (add cases)

**Interfaces:**
- Produces: `CheckEdgeCrossings` measures coincidence in XZ and classifies Y — off-node XZ intersections are violations only for `Junction`/`VerticalClash` kinds; committed edges exceeding `type.MaxGradient + 0.005` are flagged (`CheckEdgeGeometry` extension, same slack philosophy as floors).

- [ ] **Step 1: Failing tests** (in `NetworkInvariantsTests`)

```csharp
    [Fact]
    public void GradeSeparatedCrossingIsNotFlagged()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var bridge = new Bezier3(new(0, 6, -80), new(0, 6, -26.7f), new(0, 6, 26.7f), new(0, 6, 80));
        Net.Commit(n, new PlacementProposal(
            new[] { new ProposedCurve(bridge, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        Assert.DoesNotContain(NetworkInvariants.Check(n), v => v.Contains("without a shared node"));
    }

    [Fact]
    public void ClashBandCrossingInACorruptSaveIsFlagged()
    {
        // hand-crafted save: ground road + a +2 m road crossing it (never committable)
        const string corrupt = "{\"FormatVersion\":2," +
            "\"Nodes\":[" +
            "{\"Id\":1,\"X\":-50,\"Y\":0,\"Z\":0,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}," +
            "{\"Id\":2,\"X\":50,\"Y\":0,\"Z\":0,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}," +
            "{\"Id\":3,\"X\":0,\"Y\":2,\"Z\":-50,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}," +
            "{\"Id\":4,\"X\":0,\"Y\":2,\"Z\":50,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}]," +
            "\"Edges\":[" +
            "{\"Id\":1,\"Start\":1,\"End\":2,\"Type\":1,\"Curve\":[-50,0,0,-16.6667,0,0,16.6667,0,0,50,0,0],\"LaneIds\":[1,2]}," +
            "{\"Id\":2,\"Start\":3,\"End\":4,\"Type\":1,\"Curve\":[0,2,-50,0,2,-16.6667,0,2,16.6667,0,2,50],\"LaneIds\":[3,4]}]," +
            "\"NextNode\":5,\"NextEdge\":3,\"NextLane\":5}";
        var n = CityBuilder.Domain.Persistence.SaveLoad.Load(corrupt);
        Assert.Contains(NetworkInvariants.Check(n), v => v.Contains("without a shared node"));
    }

    [Fact]
    public void OverSteepEdgeInACorruptSaveIsFlagged()
    {
        const string corrupt = "{\"FormatVersion\":2," +
            "\"Nodes\":[" +
            "{\"Id\":1,\"X\":0,\"Y\":0,\"Z\":0,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}," +
            "{\"Id\":2,\"X\":60,\"Y\":12,\"Z\":0,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}]," +
            "\"Edges\":[" +
            "{\"Id\":1,\"Start\":1,\"End\":2,\"Type\":1,\"Curve\":[0,0,0,20,4,0,40,8,0,60,12,0],\"LaneIds\":[1,2]}]," +
            "\"NextNode\":3,\"NextEdge\":2,\"NextLane\":3}";
        var n = CityBuilder.Domain.Persistence.SaveLoad.Load(corrupt); // 20% on a TwoLane
        Assert.Contains(NetworkInvariants.Check(n), v => v.Contains("gradient"));
    }
```

- [ ] **Step 2: Run to verify fail.**

- [ ] **Step 3: Implement** — in `CheckEdgeCrossings`, change the coincidence check to XZ (`new Vector2(p.X - q.X, p.Z - q.Z).Length() > 0.5f → continue`), then classify: `if (VerticalRules.ClassifyCrossing(p.Y, q.Y) == CrossingKind.GradeSeparated) continue;`. In `CheckEdgeGeometry`, append:
```csharp
        float grad = VerticalRules.MaxGradient(e.Curve);
        if (grad > type.MaxGradient + 0.005f)
            outViolations.Add($"edge {e.Id.Value}: gradient {grad:P1} > max {type.MaxGradient:P0}");
```

- [ ] **Step 4: Run to verify pass** + full invariants class.
- [ ] **Step 5: Commit**
```bash
git add src/Domain/Network/NetworkInvariants.cs tests/Domain.Tests/Network/NetworkInvariantsTests.cs
git commit -m "feat(domain): invariants learn elevation — clearance clause on crossings, committed-gradient rule"
```

---

### Task 5: Heal + roundabout elevation compatibility

**Files:**
- Modify: `src/Domain/Network/RoadNetwork.cs` (`TryHealNode`: healed-curve gradient recheck alongside the merge-tolerance gate)
- Modify: `src/Domain/Network/RoadNetwork.Roundabouts.cs` (`RingObstructed`: classify by Y before refusing)
- Test: `tests/Domain.Tests/Network/ElevationNetworkTests.cs`

**Interfaces:**
- Produces: healing two ramp halves keeps the profile and refuses a merge whose fit exceeds the type gradient; an elevated roundabout (all legs coplanar at node Y) converts; a ground ring is NOT obstructed by a bridge ≥ MinClearance above its circle, and vice versa.

- [ ] **Step 1: Failing tests**

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class ElevationNetworkTests
{
    private static Bezier3 Ramp(Vector3 a, Vector3 b) => new(
        a, a + (b - a) / 3f, a + (b - a) * (2f / 3f), b);

    private static PlacementProposal One(Bezier3 c, RoadTypeId? t = null) => new(
        new[] { new ProposedCurve(c, EndpointBinding.None, EndpointBinding.None) },
        t ?? RoadCatalog.TwoLane.Id);

    [Fact]
    public void HealingASplitRampKeepsItsProfile()
    {
        var n = Net.New();
        Net.Commit(n, One(Ramp(new(0, 0, 0), new(60, 4, 0))));
        Net.Commit(n, One(Ramp(new(60, 4, 0), new(120, 8, 0))));
        Assert.Equal(2, n.Edges.Count);
        // crossing road splits nothing here; bulldoze path: remove nothing — instead
        // force a heal by adding+removing a third arm at the shared node
        var arm = Net.Commit(n, One(Ramp(new(60, 4, 0), new(60, 4, 80))));
        n.RemoveEdge(arm.CreatedEdges[0]);
        Assert.Single(n.Edges); // healed into one ramp
        var healed = n.Edges.Values.Single();
        Assert.Equal(8f, healed.Curve.Point(1).Y, 1);
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void ElevatedRoundaboutConverts()
    {
        var n = Net.New();
        Net.Commit(n, One(Ramp(new(-60, 10, 0), new(60, 10, 0)), RoadCatalog.Street.Id));
        Net.Commit(n, One(Ramp(new(0, 10, -60), new(0, 10, 60)), RoadCatalog.Street.Id));
        var center = n.Nodes.Values.Single(x =>
            Vector3.Distance(x.Position, new(0, 10, 0)) < 0.1f);
        var res = n.ConvertToRoundabout(center.Id, 20f);
        Assert.True(res.Success, $"convert failed: {res.Error}");
        Assert.Empty(NetworkInvariants.Check(n));
        // ring nodes sit on the +10 m plane
        Assert.All(n.Nodes.Values.Where(x => x.Ring != null),
            x => Assert.Equal(10f, x.Position.Y, 1));
    }

    [Fact]
    public void GroundRoundaboutIsNotObstructedByABridgeAbove()
    {
        var n = RoundaboutTests.FourWayJunction(out var center);
        // +8 m bridge crossing straight over the future ring area
        Net.Commit(n, One(Ramp(new(-80, 8, 10), new(80, 8, 10))));
        var res = n.ConvertToRoundabout(center, 20f);
        Assert.True(res.Success, $"convert failed: {res.Error}");
        Assert.Empty(NetworkInvariants.Check(n));
    }
}
```

- [ ] **Step 2: Run to verify fail** (obstruction test fails today: `RingObstructed` is planar).

- [ ] **Step 3: Implement** — `TryHealNode`: after the `MergeTolerance` gate add
`if (VerticalRules.MaxGradient(merged) > RoadCatalog.Get(type).MaxGradient + 0.005f) return;`
(compute `type` before use). `RingObstructed`: for each XZ intersection, evaluate both
curves' Y at the hit and `continue` when `ClassifyCrossing` says `GradeSeparated`;
`VerticalClash` and `Junction` kinds still obstruct (a coplanar bystander through the
ring area must still refuse). The planner's ring arcs live on the center's Y plane —
build them at `center.Y` (they already inherit `center.Y` via slot positions; verify
`ArcChain` uses `center.Y` for `y`, it does).

- [ ] **Step 4: Run to verify pass** + `RoundaboutTests`/`HealingTests` regression sweep.
- [ ] **Step 5: Commit**
```bash
git add src/Domain/Network/RoadNetwork.cs src/Domain/Network/RoadNetwork.Roundabouts.cs tests/Domain.Tests/Network/ElevationNetworkTests.cs
git commit -m "feat(domain): heal keeps ramp profiles; roundabouts convert at elevation; obstruction is clearance-aware"
```

---

### Task 6: Traffic over a bridge (expected zero sim changes)

**Files:**
- Test: `tests/Domain.Tests/Traffic/ElevationTrafficTests.cs`

- [ ] **Step 1: Write the tests** (should pass immediately; if not, STOP and investigate — the spec predicts no sim change)

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class ElevationTrafficTests
{
    [Fact]
    public void VehiclesOnABridgeAndTheRoadBelowNeverArbitrate()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-150, 0, 0), new(150, 0, 0)));
        var bridge = new Bezier3(new(0, 6, -150), new(0, 6, -50), new(0, 6, 50), new(0, 6, 150));
        Net.Commit(n, new PlacementProposal(
            new[] { new ProposedCurve(bridge, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));

        var sim = new TrafficSim(n, seed: 5);
        var ground = n.Edges.Values.First(e => e.Curve.Point(0.5f).Y < 1f);
        var deck = n.Edges.Values.First(e => e.Curve.Point(0.5f).Y > 5f);
        var a = sim.Spawn(ground.Id, true, ground.Id);
        var b = sim.Spawn(deck.Id, true, deck.Id);
        Assert.NotNull(a);
        Assert.NotNull(b);
        for (int i = 0; i < 60 * 30; i++)
            sim.Tick(1f / 60f);
        // both completed their trips: no junction existed to arbitrate, nobody waited
        Assert.True(sim.Arrived >= 2, $"arrived={sim.Arrived}");
    }

    [Fact]
    public void GradeSeparatedNetworkBurstIsSafe()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-150, 0, 0), new(150, 0, 0)));
        Net.Commit(n, Net.Straight(new(-150, 0, 60), new(150, 0, 60)));
        var bridge = new Bezier3(new(0, 6, -150), new(0, 6, -50), new(0, 6, 50), new(0, 6, 150));
        Net.Commit(n, new PlacementProposal(
            new[] { new ProposedCurve(bridge, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        Assert.Empty(SimInvariants.CheckBurst(n, seed: 11, ticks: 400, population: 10));
    }
}
```

(`TrafficSim.Arrived` exists — the smoke test uses it. If `Spawn(edge, true, sameEdge)`
is rejected for same-edge trips, use two connected edges per level instead.)

- [ ] **Step 2: Run** — expected PASS. Investigate before touching sim code otherwise.
- [ ] **Step 3: Commit**
```bash
git add tests/Domain.Tests/Traffic/ElevationTrafficTests.cs
git commit -m "test(traffic): bridge and ground traffic never arbitrate; grade-separated burst safe (no sim change)"
```

---

### Task 7: DraftSession elevation state + Y profiles

**Files:**
- Modify: `src/Domain/Tools/Draft/DraftSession.cs` (`CurrentElevation` property; apply Y profile to built proposals before Validate)
- Test: `tests/Domain.Tests/Tools/DraftElevationTests.cs` (create `Tools` test dir if absent)

**Interfaces:**
- Produces: `DraftSession.CurrentElevation` (float, clamped [0, `GeoConstants.MaxElevation`] by the setter); every proposal the session validates/commits gets endpoint Ys — snapped `AtNode`/`OnEdge` endpoints adopt the target's Y, `Free` endpoints take `CurrentElevation` — with control-point Y linearly interpolated (P1 at 1/3, P2 at 2/3 of ΔY). The fuzzer drives `CurrentElevation` directly.

- [ ] **Step 1: Failing tests**

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using CityBuilder.Domain.Tools.Snapping;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class DraftElevationTests
{
    private static (RoadNetwork n, DraftSession s) NewSession()
    {
        var n = Net.New();
        var s = new DraftSession(n, new SnapEngine(n));
        s.SetMode(DraftMode.Straight);
        return (n, s);
    }

    [Fact]
    public void FreeEndpointsTakeTheCurrentElevation()
    {
        var (n, s) = NewSession();
        s.CurrentElevation = 10f;
        s.Click(new Vector3(0, 0, 0), 6f);   // clicks arrive as ground picks (y=0)
        s.Click(new Vector3(100, 0, 0), 6f);
        Assert.Single(n.Edges);
        var e = n.Edges.Values.Single();
        Assert.Equal(10f, e.Curve.P0.Y, 1);
        Assert.Equal(10f, e.Curve.P3.Y, 1);
    }

    [Fact]
    public void SnappedEndpointAdoptsTargetElevationAndRampsToCurrent()
    {
        var (n, s) = NewSession();
        s.CurrentElevation = 8f;
        // pre-existing ground road; start the new draft snapped to its end node
        Net.Commit(n, Net.Straight(new Vector3(-100, 0, 0), new Vector3(0, 0, 0)));
        s.Click(new Vector3(0, 0, 0), 6f);      // snaps to the ground node → Y 0
        s.Click(new Vector3(120, 0, 0), 6f);    // free → Y 8 (6.7% on TwoLane, legal)
        Assert.Equal(2, n.Edges.Count);
        var ramp = n.Edges.Values.First(e => e.Curve.Point(1).Y > 4f || e.Curve.Point(0).Y > 4f);
        float y0 = MathF.Min(ramp.Curve.P0.Y, ramp.Curve.P3.Y);
        float y1 = MathF.Max(ramp.Curve.P0.Y, ramp.Curve.P3.Y);
        Assert.Equal(0f, y0, 1);
        Assert.Equal(8f, y1, 1);
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void ElevationSetterClampsToEditorRange()
    {
        var (_, s) = NewSession();
        s.CurrentElevation = 999f;
        Assert.Equal(CityBuilder.Domain.Geometry.GeoConstants.MaxElevation, s.CurrentElevation);
        s.CurrentElevation = -5f;
        Assert.Equal(0f, s.CurrentElevation);
    }
}
```

- [ ] **Step 2: Run to verify fail** (`CurrentElevation` undefined).

- [ ] **Step 3: Implement** — in `DraftSession`: a `float _currentElevation` with clamped
property; one private `PlacementProposal ApplyElevation(PlacementProposal p)` mapping
each `ProposedCurve` to an elevated copy:
```csharp
    private float ResolveY(EndpointBinding b, Vector3 pos) => b switch
    {
        EndpointBinding.AtNode(var id) when network.Nodes.TryGetValue(id, out var nd) => nd.Position.Y,
        EndpointBinding.OnEdge(var eid, var t) when network.Edges.TryGetValue(eid, out var e) => e.Curve.Point(t).Y,
        _ => CurrentElevation,
    };

    private ProposedCurve Elevate(ProposedCurve pc)
    {
        float y0 = ResolveY(pc.Start, pc.Curve.P0), y3 = ResolveY(pc.End, pc.Curve.P3);
        var c = pc.Curve;
        var lifted = new Bezier3(
            new Vector3(c.P0.X, y0, c.P0.Z),
            new Vector3(c.P1.X, y0 + (y3 - y0) / 3f, c.P1.Z),
            new Vector3(c.P2.X, y0 + (y3 - y0) * 2f / 3f, c.P2.Z),
            new Vector3(c.P3.X, y3, c.P3.Z));
        return pc with { Curve = lifted };
    }
```
Call `ApplyElevation` at every `BuildProposal()` call site inside the session (all the
`Ghost = network.Validate(...)`/commit paths — grep `BuildProposal`). Chain mode: the
next segment's anchor is the previous committed endpoint; its `AtNode`/`OnEdge`
binding (or the tangent-lock anchor position, which now carries Y) resolves it.

- [ ] **Step 4: Run to verify pass** + `DraftSession`-touching suites (`FuzzRegressionTests` quick seeds, `GeometryGuardTests`).
- [ ] **Step 5: Commit**
```bash
git add src/Domain/Tools/Draft/DraftSession.cs tests/Domain.Tests/Tools/DraftElevationTests.cs
git commit -m "feat(domain): DraftSession.CurrentElevation — snapped ends adopt target Y, free ends ramp to current"
```

---

### Task 8: Fuzzer elevation

**Files:**
- Modify: `tests/Domain.Tests/Fuzzing/GestureFuzzer.cs`

- [ ] **Step 1: Extend the alphabet** — in `DrawGesture`, before the click loop:
```csharp
        // elevation: mostly ground (70%), else a step multiple in [5, 50] — grade
        // separation and clash refusals both get organic coverage
        session.CurrentElevation = rng.Next(100) < 70 ? 0f : 5f * rng.Next(1, 11);
```
and include the elevation in the action log line. Reset to 0 in `StepBackCancel` never — persistence across gestures is the editor behavior.
- [ ] **Step 2: Run** `CITYBUILDER_FUZZ_ACTIONS=2500 dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"` — triage any finding as a domain bug (fix + pin), not a fuzzer bug.
- [ ] **Step 3: Commit**
```bash
git add tests/Domain.Tests/Fuzzing/GestureFuzzer.cs
git commit -m "test(fuzz): elevation steps in the gesture alphabet"
```

---

### Task 9: Editor input + readout + ghost

**Files:**
- Modify: `src/Game/ToolController.cs` (PgUp/PgDn → `Session.CurrentElevation` steps; readout gains `+Ym` and gradient %)
- Modify: `src/Game/Main.cs` (key routing if input lands there — follow the Ctrl+Z pattern)
- Modify: `src/Game/GhostView.cs` (verify ghost renders at curve Y; fix flat-Y assumptions)
- Modify: `src/Game/Toolbar.cs` (hint line mentions PgUp/PgDn)

- [ ] **Step 1: Wire input** — follow the existing undo hotkey path (grep `KeyLabel.Z` or `Undo` handling in `Main.cs`/`ToolController.cs`): PgUp → `+5` (`Ctrl` → `+1`), PgDn mirror; clamp happens in the domain setter. Update the readout string with `Session.CurrentElevation` when > 0 and the active draft's segment gradient (`VerticalRules.MaxGradient` of the ghost's first curve).
- [ ] **Step 2: Verify ghost** — `GhostView` builds ribbons from domain curves; confirm it uses curve points verbatim (3D) and markings/z-offsets are curve-relative. Fix any `y=0` literal.
- [ ] **Step 3: Build + UITEST** — `dotnet build citybuilder.sln`; extend `RunUiTest` in `Main.cs`: set elevation via `_controller` steps, draw a short elevated road, assert the committed edge's `Curve.P0.Y` ≈ 10, reset elevation to 0.
- [ ] **Step 4: Commit**
```bash
git add src/Game/ToolController.cs src/Game/Main.cs src/Game/GhostView.cs src/Game/Toolbar.cs
git commit -m "feat(game): PgUp/PgDn elevation stepping, elevation+gradient readout, elevated ghost"
```

---

### Task 10: Structure rendering — decks, pillars, vehicle pitch

**Files:**
- Create: `src/Game/StructureView.cs` (embankment skirts, girder fascia, pillars; driven by `NetworkDelta` like `RoadNetworkView`)
- Modify: `src/Game/RoadNetworkView.cs` (instantiate/forward deltas — follow how `SignalLampView` is wired in `Main.cs` and mirror it)
- Modify: `src/Game/TrafficView.cs` (vehicle basis from the 3D tangent — pitch)
- Modify: `src/Game/Materials.cs` or equivalent (concrete/earth materials; grep existing material definitions and follow the pattern)

- [ ] **Step 1: Implement `StructureView`** — per edge, sample the curve every ~4 m; classify spans by `midY - 0` against `GeoConstants.EmbankmentMax`: embankment spans emit two side quads from deck edge (±`type.Width/2` lateral offset, reuse the offset math from the road mesh builder — grep `MeshBuilders`) down to ground; bridge spans emit a fascia strip (deck edge down 1.2 m) plus pillar cylinders (`CylinderMesh` or a thin box) every 24 m of arc where `y ≥ 2`. Rebuild on the same dirty-edge sets `RoadNetworkView` uses (subscribe to `RoadNetwork.Changed`, rebuild `EdgesAdded ∪ EdgesChanged`, drop `EdgesRemoved`).
- [ ] **Step 2: Vehicle pitch** — in `TrafficView`, the basis currently comes from a forward vector; ensure the forward passed to `Basis.LookingAt` keeps its Y component (remove any flattening; if the pose API already returns 3D tangent, this may be a no-op — verify by reading `sim.Pose`).
- [ ] **Step 3: Screenshot evidence** — add a bridge scene to the screenshot harness (`CITYBUILDER_SHOTS`, see `docs/verification.md` for the shot-list registration) and READ the produced image: deck visibly above ground road, pillars present, vehicles on both levels in the motion composite if covered.
- [ ] **Step 4: Build + smoke** — extend `RunSmoke`: build the Task-3 bridge scenario via `_controller` (set elevation, draw across the existing road), `Expect` edge count proves no junction; `_view.FlushDirty()` + structure rebuild must not throw; vehicles tick on both levels.
- [ ] **Step 5: Commit**
```bash
git add src/Game/StructureView.cs src/Game/RoadNetworkView.cs src/Game/TrafficView.cs src/Game/Main.cs
git commit -m "feat(game): bridge decks, pillars, embankment skirts; vehicles pitch along ramps"
```

---

### Task 11: KPI `gradesep` scenario + health report

**Files:**
- Modify: `tests/Domain.Tests/Kpi/KpiScenarios.cs` (new `GradeSeparation()`)
- Modify: `tests/Domain.Tests/Kpi/KpiSuiteTests.cs` (`Milestone = "M8"`, expected keys, scenario call)
- Modify: `docs/health/kpi-baseline.json` (add the new keys after first run)

- [ ] **Step 1: Scenario** — two crossing arterials (TwoLane, 300 m), variant A: at-grade junction (auto control), variant B: one arterial bridged at +6 m. Same demand pattern on both (pulse spawns as in `Yield4Way`, 120 sim-seconds). Emit `gradesep.at_grade_delay`, `gradesep.bridged_delay` (mean trip delay via `TripLog`), `gradesep.bridged_speedup` (ratio). The bridged delay should be ~0 — the KPI documents elevation's payoff.
- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~KpiSuiteTests"` → report regenerates as `docs/health/M8.md`; add the three keys to `kpi-baseline.json` from `kpi-latest.json`; re-run → 0% deltas.
- [ ] **Step 3: Commit**
```bash
git add tests/Domain.Tests/Kpi/KpiScenarios.cs tests/Domain.Tests/Kpi/KpiSuiteTests.cs docs/health/kpi-baseline.json docs/health/kpi-latest.json docs/health/M8.md
git commit -m "feat(kpi): gradesep scenario — bridged vs at-grade delay; M8 health report"
```

---

### Task 12: Certification + docs

**Files:**
- Create: `docs/manual/10-elevation.md`; Modify: manual `README.md`, `01-geometry.md`, `02-network-validation.md`, `06-drafting-snapping.md`, `07-rendering-markings.md` (drift stamps + M8 hooks)
- Modify: `docs/conventions.md` (vertical constants table), `docs/gotchas.md` (XZ-projected Intersections vs 3D coincidence, if bitten), `docs/roadmap.md` (M8 Done entry, M8.5 next)

- [ ] **Step 1: Full cert** — `dotnet test` (all), `CITYBUILDER_FUZZ_ACTIONS=10000` 3-seed run, `dotnet build`, `CITYBUILDER_SMOKE=1 godot --headless .`, UITEST, screenshot harness. All green with evidence captured.
- [ ] **Step 2: Manual ch10** — planner-quality chapter: vertical constants, classification bands, authoring flow, derived structures, invariants, "How to verify". Drift-stamp touched chapters; TOC row in README.
- [ ] **Step 3: Roadmap** — M8 Done entry (known limits: no grade speed effect, no vertical node retrofit, editor-clamped ≥0, M8.5 = trenches/tunnels next up).
- [ ] **Step 4: Commit**
```bash
git add docs/
git commit -m "docs+cert: M8 elevation & bridges — fuzz 3x10k green, KPI M8, manual ch10, roadmap"
```

## Self-Review notes

- **Spec coverage:** constants/gradients (T1), Validate three-band + coplanar endpoints (T2), commit-side + stacked nodes (T3), invariant clause + gradient rule (T4), heal/roundabout (T5), traffic no-change proof (T6), authoring (T7 domain, T9 input/readout/ghost), fuzz (T8), rendering + pitch + screenshots (T10), KPI (T11), smoke/UITEST (T9/T10 steps), docs/cert (T12). Non-goals stay out.
- **Known risk called out in-plan:** `Intersections` is XZ-projected, so 3D-coincidence checks in `SegmentCrossesLiveEdgeOffNode`/`CheckEdgeCrossings` must switch to XZ distance + Y classification (T3/T4 carry the note) — this is the likeliest source of surprise findings; the fuzzer (T8) audits it.
- **Type consistency:** `VerticalRules.ClassifyCrossing(float, float)`, `MaxGradient(in Bezier3)`, `RoadType.MaxGradient`, `DraftSession.CurrentElevation` used identically across tasks.
```
