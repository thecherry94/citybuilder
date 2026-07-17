# Roundabout Conversion (M7.5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert an existing N-way junction into a live roundabout entity — a one-way ring built from ordinary graph edges — with editable radius and automatic re-arcing when an approach is bulldozed.

**Architecture:** A pure `RoundaboutPlanner` computes all ring geometry from `(center, radius, legs)`. A network-owned `RoundaboutRegistry` (partial class of `RoadNetwork`) applies a plan as graph surgery in one batch, reusing the existing node/edge/batch primitives. Ring edges use the existing `OneWay` catalog type and ring nodes get `PrioritySigns` control with the approach leg forced to `Yield`, so traffic/routing/rendering/fuzzer need zero changes and yield-on-entry emerges from M2/M5 machinery.

**Tech Stack:** C# (net8.0 domain, System.Numerics only — no Godot), Godot 4.7 mono for the game layer, xUnit (net10.0 tests).

## Global Constraints

- **Domain purity:** `src/Domain` never references Godot; System.Numerics only (golden rule 1).
- **Verification per change:** `dotnet test`, then `dotnet build citybuilder.sln`, then the matching harness (golden rule 2).
- **Milestone DoD:** fuzz suite green (extended), regenerated KPI baseline + `docs/health/M7.5.md`, drift-updated manual, current roadmap.
- **Invariants over examples:** prefer regression tests asserting invariants (`NetworkInvariants.Check` empty) over example asserts (golden rule 4).
- **Right-hand traffic:** roundabouts circulate **counter-clockwise** (CCW) in the XZ plane, Y up.
- **Ring cross-section:** always `RoadCatalog.OneWay` (id 5, 12 m, MinSegmentLength 12, MinRadius 10).
- **Commit at every green step.** End commit messages with the Co-Authored-By / Claude-Session trailers used across this repo.

---

### Task 1: Roundabout value types

**Files:**
- Modify: `src/Domain/Network/Ids.cs` (add `RoundaboutId`)
- Create: `src/Domain/Network/Roundabout.cs` (entity + result/error types)
- Modify: `src/Domain/Network/Entities.cs` (add `RoundaboutId? Ring` to `RoadNode`)
- Test: `tests/Domain.Tests/Network/RoundaboutTypesTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct RoundaboutId(int Value)`
  - `enum RoundaboutError { RadiusTooTight, LegTooShort, LegInsideRing, DegenerateBearings, NotAJunction, AlreadyRoundabout, ForeignLeg, UnknownRoundabout }`
  - `sealed record Roundabout(RoundaboutId Id, Vector3 Center, float Radius, IReadOnlyList<NodeId> RingNodes, IReadOnlyList<EdgeId> RingEdges)`
  - `sealed record RoundaboutResult(bool Success, RoundaboutId? Id, RoundaboutError? Error)` with `static Failed(RoundaboutError)` and `static Ok(RoundaboutId)`
  - `RoadNode.Ring` — `RoundaboutId?` internal-set property, default null.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class RoundaboutTypesTests
{
    [Fact]
    public void ResultHelpersCarryOutcome()
    {
        var ok = RoundaboutResult.Ok(new RoundaboutId(3));
        Assert.True(ok.Success);
        Assert.Equal(new RoundaboutId(3), ok.Id);
        Assert.Null(ok.Error);

        var bad = RoundaboutResult.Failed(RoundaboutError.RadiusTooTight);
        Assert.False(bad.Success);
        Assert.Null(bad.Id);
        Assert.Equal(RoundaboutError.RadiusTooTight, bad.Error);
    }

    [Fact]
    public void RoundaboutRecordHoldsRingMembership()
    {
        var rb = new Roundabout(new RoundaboutId(1), Vector3.Zero, 20f,
            new[] { new NodeId(2), new NodeId(3), new NodeId(4) },
            new[] { new EdgeId(5), new EdgeId(6), new EdgeId(7) });
        Assert.Equal(3, rb.RingNodes.Count);
        Assert.Equal(20f, rb.Radius);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RoundaboutTypesTests"`
Expected: FAIL — types `RoundaboutResult`, `Roundabout` not defined (compile error).

- [ ] **Step 3: Write minimal implementation**

Add to `src/Domain/Network/Ids.cs`:
```csharp
public readonly record struct RoundaboutId(int Value);
```

Create `src/Domain/Network/Roundabout.cs`:
```csharp
using System.Numerics;

namespace CityBuilder.Domain.Network;

/// <summary>Why a roundabout conversion or edit was refused. No mutation happens on failure.</summary>
public enum RoundaboutError
{
    RadiusTooTight, LegTooShort, LegInsideRing, DegenerateBearings,
    NotAJunction, AlreadyRoundabout, ForeignLeg, UnknownRoundabout,
}

/// <summary>A live roundabout: a CCW one-way ring owning its ring nodes and edges.
/// RingEdges[i] runs RingNodes[i] -> RingNodes[(i+1) % n], counter-clockwise.</summary>
public sealed record Roundabout(
    RoundaboutId Id, Vector3 Center, float Radius,
    IReadOnlyList<NodeId> RingNodes, IReadOnlyList<EdgeId> RingEdges);

public sealed record RoundaboutResult(bool Success, RoundaboutId? Id, RoundaboutError? Error)
{
    public static RoundaboutResult Ok(RoundaboutId id) => new(true, id, null);
    public static RoundaboutResult Failed(RoundaboutError error) => new(false, null, error);
}
```

Add to `RoadNode` in `src/Domain/Network/Entities.cs` (after `Config`):
```csharp
    /// <summary>Set when this node is part of a roundabout ring; null for plain nodes.
    /// Ring nodes are exempt from TryHealNode.</summary>
    public RoundaboutId? Ring { get; internal set; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RoundaboutTypesTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Network/Ids.cs src/Domain/Network/Roundabout.cs src/Domain/Network/Entities.cs tests/Domain.Tests/Network/RoundaboutTypesTests.cs
git commit -m "feat(domain): roundabout value types — RoundaboutId, Roundabout, result/error"
```

---

### Task 2: `RoundaboutPlanner` — pure ring geometry

**Files:**
- Create: `src/Domain/Network/RoundaboutPlanner.cs`
- Test: `tests/Domain.Tests/Network/RoundaboutPlannerTests.cs`

**Interfaces:**
- Consumes: `Bezier3`, `BezierOps.ArcFromTangent`, `RoadCatalog.OneWay`, `RoadCatalog.Get`.
- Produces:
  - `readonly record struct ApproachLeg(EdgeId Edge, Bezier3 Curve, bool EndsAtCenter, RoadTypeId Type)` — `EndsAtCenter` true when the edge's `EndNode` is the center (so its inner end is `Curve.Point(1)`), false when its `StartNode` is the center.
  - `sealed record RingSlot(float Bearing, Vector3 Position, ApproachLeg Leg, Bezier3 TrimmedLeg, bool TrimmedLegEndsAtCenter)` — one per approach, sorted CCW by bearing.
  - `sealed record RoundaboutPlan(Vector3 Center, float Radius, IReadOnlyList<RingSlot> Slots, IReadOnlyList<Bezier3> RingArcs, RoundaboutError? Error)` — `RingArcs[i]` connects `Slots[i]` → `Slots[(i+1)%n]`, CCW `OneWay`. `Error` non-null ⇒ plan invalid, other lists may be empty.
  - `static RoundaboutPlan RoundaboutPlanner.Plan(Vector3 center, float radius, IReadOnlyList<ApproachLeg> legs)`
  - `static float MinFeasibleRadius(IReadOnlyList<ApproachLeg> legs, Vector3 center)` — smallest radius at which every adjacent CCW bearing gap yields a ring arc ≥ `OneWay.MinSegmentLength`. Returns `float.PositiveInfinity` if two bearings coincide.

**Design notes for the implementer:**
- **Bearing** of a leg = `atan2(dz, dx)` of the leg's *departure direction from the center* (the tangent leaving the center outward). For `EndsAtCenter` the inner end is `Point(1)`, outward tangent is `-Curve.Tangent(1)`; else inner end is `Point(0)`, outward tangent is `Curve.Tangent(0)`.
- **Ring slot position** = `center + R*(cos θ, 0, sin θ)`.
- **CCW arc between adjacent slots:** the tangent leaving slot i CCW is perpendicular to its radius, rotated +90°: for radius direction `(cosθ, 0, sinθ)` the CCW tangent is `(-sinθ, 0, cosθ)`. Feed `ArcFromTangent(slotI.Position, ccwTangent, slotJ.Position)`; it returns one or two cubics on the circle. Concatenate the two-cubic case into the arc list is **not** needed if you keep `RingArcs` as one `Bezier3` per gap — for gaps > 90° use the FIRST returned cubic only is WRONG. Instead: v1 guarantees each gap ≤ ~170° by requiring ≥ 3 legs, but a 3-leg ring can have a 240° gap. **Therefore `RingArcs` must allow a slot pair to produce 1–2 cubics.** Change the type: `IReadOnlyList<IReadOnlyList<Bezier3>> RingArcs` where `RingArcs[i]` is the 1-or-2 cubic chain for gap i. Adjust the record accordingly. (Two adjacent cubics share a midpoint ring node — see Task 3 for how the registry inserts intermediate ring nodes.)
- **Feasibility / errors** (first blocking wins): `radius < MinFeasibleRadius` → `RadiusTooTight`; any two bearings within `1°` → `DegenerateBearings`; a leg whose inner endpoint is closer to center than `radius` → `LegInsideRing`; a leg whose trimmed remainder length `< RoadCatalog.Get(leg.Type).MinSegmentLength` → `LegTooShort`.
- **Trim:** find `tCut` on the leg where `|Curve.Point(t) - center| == radius` by bisection (distance is monotonic from the outer end down to 0 at center). Then `TrimmedLeg` = the sub-curve from the outer end to `tCut` via `Bezier3.Split`, oriented to still end at the ring slot; `TrimmedLegEndsAtCenter` mirrors the original `EndsAtCenter`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class RoundaboutPlannerTests
{
    // Four straight legs N/E/S/W into origin, each 60 m long, ending at center.
    private static IReadOnlyList<ApproachLeg> FourWay()
    {
        var dirs = new[] { new Vector3(60,0,0), new Vector3(0,0,60), new Vector3(-60,0,0), new Vector3(0,0,-60) };
        var legs = new List<ApproachLeg>();
        int id = 10;
        foreach (var d in dirs)
            legs.Add(new ApproachLeg(new EdgeId(id++), Bezier3.Line(d, Vector3.Zero), true, RoadCatalog.TwoLane.Id));
        return legs;
    }

    [Fact]
    public void FourWayProducesFourSlotsInCcwOrder()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, FourWay());
        Assert.Null(plan.Error);
        Assert.Equal(4, plan.Slots.Count);
        for (int i = 0; i + 1 < plan.Slots.Count; i++)
            Assert.True(plan.Slots[i].Bearing < plan.Slots[i + 1].Bearing);
    }

    [Fact]
    public void SlotsSitOnTheCircle()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, FourWay());
        foreach (var s in plan.Slots)
            Assert.Equal(20f, Vector3.Distance(s.Position, Vector3.Zero), 2);
    }

    [Fact]
    public void RingArcsLieOnTheCircle()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, FourWay());
        foreach (var chain in plan.RingArcs)
        foreach (var arc in chain)
            for (float t = 0; t <= 1f; t += 0.25f)
                Assert.Equal(20f, Vector3.Distance(arc.Point(t), Vector3.Zero), 1);
    }

    [Fact]
    public void TrimmedLegsEndOnTheCircleNotCenter()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, FourWay());
        foreach (var s in plan.Slots)
        {
            var inner = s.TrimmedLegEndsAtCenter ? s.TrimmedLeg.Point(1) : s.TrimmedLeg.Point(0);
            Assert.Equal(20f, Vector3.Distance(inner, Vector3.Zero), 1);
        }
    }

    [Fact]
    public void RadiusBelowFeasibleFails()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 1f, FourWay());
        Assert.Equal(RoundaboutError.RadiusTooTight, plan.Error);
    }

    [Fact]
    public void CoincidentBearingsFail()
    {
        var legs = new[]
        {
            new ApproachLeg(new EdgeId(1), Bezier3.Line(new(60,0,0), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
            new ApproachLeg(new EdgeId(2), Bezier3.Line(new(61,0,0), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
            new ApproachLeg(new EdgeId(3), Bezier3.Line(new(0,0,60), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
        };
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, legs);
        Assert.Equal(RoundaboutError.DegenerateBearings, plan.Error);
    }

    [Fact]
    public void ThreeLegTeeSucceeds()
    {
        var legs = new[]
        {
            new ApproachLeg(new EdgeId(1), Bezier3.Line(new(60,0,0), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
            new ApproachLeg(new EdgeId(2), Bezier3.Line(new(-60,0,0), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
            new ApproachLeg(new EdgeId(3), Bezier3.Line(new(0,0,60), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
        };
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, legs);
        Assert.Null(plan.Error);
        Assert.Equal(3, plan.Slots.Count);
    }
}
```

- [ ] **Step 2: Run to verify it fails** — Run: `dotnet test --filter "FullyQualifiedName~RoundaboutPlannerTests"`; Expected: FAIL (compile — `RoundaboutPlanner` undefined).

- [ ] **Step 3: Implement `RoundaboutPlanner`**

Create `src/Domain/Network/RoundaboutPlanner.cs`. Implement per the design notes: compute bearings, sort CCW, positions on the circle, trim by bisection, CCW arc chains via `ArcFromTangent`, feasibility/error checks, `MinFeasibleRadius`. Keep it pure (static, no network access). Use `RingArcs` as `IReadOnlyList<IReadOnlyList<Bezier3>>`.

- [ ] **Step 4: Run to verify it passes** — Run: `dotnet test --filter "FullyQualifiedName~RoundaboutPlannerTests"`; Expected: PASS (7 tests). Iterate on the geometry until green.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Network/RoundaboutPlanner.cs tests/Domain.Tests/Network/RoundaboutPlannerTests.cs
git commit -m "feat(domain): RoundaboutPlanner — pure CCW ring geometry, trim + feasibility"
```

---

### Task 3: `ConvertToRoundabout` — registry + graph surgery

**Files:**
- Create: `src/Domain/Network/RoadNetwork.Roundabouts.cs` (partial class)
- Modify: `src/Domain/Network/RoadNetwork.cs` (add `_roundabouts` dict + `_nextRoundabout` counter; expose `IReadOnlyDictionary<RoundaboutId, Roundabout> Roundabouts`; exempt ring nodes from `TryHealNode`)
- Test: `tests/Domain.Tests/Network/RoundaboutTests.cs`

**Interfaces:**
- Consumes: `RoundaboutPlanner.Plan`, `ApproachLeg`, batch primitives (`BeginBatch`/`EndBatch`/`AddNodeInternal`/`AddEdgeInternal`/`RemoveEdgeInternal`), `NetworkInvariants.Check`.
- Produces:
  - `RoundaboutResult ConvertToRoundabout(NodeId center, float radius)`
  - `Roundabout? RoundaboutForNode(NodeId n)` / `Roundabout? RoundaboutForEdge(EdgeId e)`
  - `IReadOnlyDictionary<RoundaboutId, Roundabout> Roundabouts { get; }`

**Design notes:**
- Preconditions → early `Failed`: node missing / degree < 3 → `NotAJunction`; node already a ring node (`node.Ring != null`) → `AlreadyRoundabout`; any leg whose far node is a ring node → `ForeignLeg`.
- Build `ApproachLeg`s from `center.EdgeSet`. `EndsAtCenter = edge.EndNode == center`.
- Call the planner; on `Error` return `Failed(error)` — **no mutation yet**.
- Mutate in one batch:
  1. For each slot: `AddNodeInternal(slot.Position)` → ring node; set its `Ring = newId`.
  2. Insert any intermediate ring nodes for 2-cubic arc chains (a chain of 2 cubics needs a midpoint node at the shared point; add it, `Ring = newId`, and it has degree 2 = two ring legs, no approach).
  3. For each gap, `AddEdgeInternal(fromRingNode, toRingNode, arc, OneWay.Id)` per cubic in the chain (through the intermediate node when 2 cubics). Collect ring edge ids in CCW order.
  4. Trim each leg **in place** (keep `EdgeId`): remove old edge from center's EdgeSet, build a new `RoadEdge` with same id, the trimmed curve, outer node unchanged, inner node = slot ring node; regenerate its lanes; update `_edges`, the outer & ring node EdgeSets, and batch bookkeeping. (Write a private `TrimLegInto(RoadEdge old, NodeId ringNode, Bezier3 trimmed, bool endsAtCenter)` helper — do NOT call `ReplaceEdgeInPlace`, which fires its own event.)
  5. Delete the center node (its EdgeSet is now empty): `_nodes.Remove(center)`, batch `NodesRemoved`.
  6. Set each ring node's `JunctionConfig`: `Mode = PrioritySigns`, `RoleOverrides = { approachEdge → Yield, ringInEdge → Main, ringOutEdge → Main }`. (Intermediate ring nodes are degree-2 → control resolves to `None` automatically; leave `Default`.)
  7. Register the `Roundabout` in `_roundabouts`; `_nextRoundabout++`.
- Ring node heal exemption: in `TryHealNode`, add `if (node.Ring != null) return;` at the top.
- `EndBatch` rebuilds derived data for the ring nodes (connectors, control) → yield-on-entry.

- [ ] **Step 1: Failing test**

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class RoundaboutTests
{
    private static RoadNetwork FourWayJunction(out NodeId center)
    {
        var n = new RoadNetwork();
        Net.Commit(n, Net.Straight(new(-60,0,0), new(60,0,0)));
        Net.Commit(n, Net.Straight(new(0,0,-60), new(0,0,60)));
        center = n.Nodes.Values.Single(x => Vector3.Distance(x.Position, Vector3.Zero) < 0.1f).Id;
        return n;
    }

    [Fact]
    public void ConvertFourWayBuildsValidRing()
    {
        var n = FourWayJunction(out var center);
        var res = n.ConvertToRoundabout(center, 20f);
        Assert.True(res.Success);
        Assert.False(n.Nodes.ContainsKey(center)); // center replaced
        Assert.Single(n.Roundabouts);
        Assert.Empty(NetworkInvariants.Check(n));
        // 4 ring nodes each degree 3, one approach + two ring legs
        var ringNodes = n.Nodes.Values.Where(x => x.Ring != null).ToList();
        Assert.Equal(4, ringNodes.Count);
        Assert.All(ringNodes, x => Assert.Equal(3, x.Edges.Count));
    }

    [Fact]
    public void ConvertRefusesDegreeTwo()
    {
        var n = new RoadNetwork();
        var r = Net.Commit(n, Net.Straight(new(-60,0,0), new(60,0,0)));
        // split into a degree-2 bend by adding a collinear continuation? Instead pick an end node (degree 1)
        var end = n.Nodes.Values.First(x => x.Edges.Count == 1).Id;
        Assert.Equal(RoundaboutError.NotAJunction, n.ConvertToRoundabout(end, 20f).Error);
    }

    [Fact]
    public void ConvertPreservesLegEdgeIds()
    {
        var n = FourWayJunction(out var center);
        var before = n.Edges.Keys.ToHashSet();
        n.ConvertToRoundabout(center, 20f);
        // the four legs kept their ids (trim was in place); ring edges are new
        Assert.True(before.All(id => n.Edges.ContainsKey(id) || true)); // legs survive; see stronger check below
        var survivingLegs = n.Edges.Values.Count(e => before.Contains(e.Id));
        Assert.Equal(4, survivingLegs);
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test --filter "FullyQualifiedName~RoundaboutTests"`; Expected FAIL (`ConvertToRoundabout` undefined). Note: `NetworkInvariants` roundabout rules land in Task 6; until then `Check` returns only existing violations — the `Assert.Empty` should already pass if the ring is geometrically sound. If it flags the ring as sharp/sliver, that's a real geometry bug to fix here.

- [ ] **Step 3: Implement** `RoadNetwork.Roundabouts.cs` + the `RoadNetwork.cs` field/counter/property/heal-exemption changes per the design notes.

- [ ] **Step 4: Run to verify it passes** — `dotnet test --filter "FullyQualifiedName~RoundaboutTests"`; Expected PASS (3 tests). Then `dotnet test` (full suite) + `dotnet build citybuilder.sln` — no regressions.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Network/RoadNetwork.Roundabouts.cs src/Domain/Network/RoadNetwork.cs tests/Domain.Tests/Network/RoundaboutTests.cs
git commit -m "feat(domain): ConvertToRoundabout — registry + one-batch ring surgery, legs trimmed in place"
```

---

### Task 4: Radius edit, regenerate, dissolve, remove

**Files:**
- Modify: `src/Domain/Network/RoadNetwork.Roundabouts.cs`
- Test: `tests/Domain.Tests/Network/RoundaboutTests.cs` (add cases)

**Interfaces:**
- Produces:
  - `RoundaboutResult SetRoundaboutRadius(RoundaboutId id, float radius)`
  - `void RemoveRoundabout(RoundaboutId id)` — delete ring nodes/edges; approach legs become free-ended stubs at their trimmed ends (no reconstruction of the old junction).
  - internal `RoundaboutResult Regenerate(RoundaboutId id, float radius)` — shared tear-down + rebuild from current legs; **auto-dissolves** (calls the same teardown, no new ring) and returns `Ok` with the id removed when surviving legs < 3.

**Design notes:**
- `Regenerate`: gather current approach legs (non-ring edges incident to the ring nodes), un-trim conceptually is NOT needed — the approach legs already end on the *old* ring; re-plan uses each leg's current curve/outer-tangent, which still points at the center, so bearings are stable. Tear down ring nodes+edges (they have `Ring == id`), then re-run the planner+surgery at the new radius against the same legs, re-trimming from the leg's outer end. **Important:** trimming must always measure from the outer end down; when shrinking radius the leg *grows*, when growing radius it *shrinks* — the leg's stored curve is only the trimmed remainder, so keep the original full leg? No: store enough to re-trim. Simplest correct approach: a leg's outer node + outer tangent + the center define a ray; re-trim by extending/cutting a straight-or-curved segment from the outer node to the new ring point. For v1 (legs are arbitrary curves) the robust move is to **rebuild the inner portion as a straight segment from the leg's current inner end tangent** — but that changes curve shape. To avoid drift, **store the pre-conversion full leg curve** in the `Roundabout` (or re-derive by treating the leg as fixed and only moving its inner endpoint along its own tangent). Decision: extend `Roundabout` with `IReadOnlyDictionary<EdgeId, Bezier3> LegFullCurves` capturing each leg's curve *as it entered the center at conversion time*; regeneration always re-trims from these, so radius changes are lossless and idempotent. Update Task 1's record + Task 8's DTO accordingly.
- `SetRoundaboutRadius`: precondition id exists (`UnknownRoundabout`); plan at new radius from `LegFullCurves`; on planner error return `Failed` **without mutating**; else regenerate.
- `RemoveRoundabout`: one batch — remove all ring edges via `RemoveEdgeInternal`, remove ring nodes, clear their `Ring`, drop from `_roundabouts`. Approach legs remain (their inner end node is deleted → they'd dangle). **Fix:** before deleting a ring node that carries an approach, leave the approach edge ending at a fresh plain node at the ring point (convert the ring node to a plain node instead of deleting it when it has an approach leg). So: ring nodes WITH an approach become plain nodes (clear `Ring`, degree drops to 1 as ring edges go); intermediate ring nodes (degree 2, no approach) are deleted and their absence is fine because ring edges are gone.

- [ ] **Step 1: Failing tests**

```csharp
[Fact]
public void RadiusChangeReArcsRing()
{
    var n = FourWayJunction(out var center);
    var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
    Assert.True(n.SetRoundaboutRadius(id, 30f).Success);
    Assert.Equal(30f, n.Roundabouts[id].Radius);
    Assert.Empty(NetworkInvariants.Check(n));
    foreach (var rn in n.Nodes.Values.Where(x => x.Ring == id))
        Assert.Equal(30f, Vector3.Distance(rn.Position, n.Roundabouts[id].Center), 1);
}

[Fact]
public void RemoveRoundaboutLeavesCleanStubs()
{
    var n = FourWayJunction(out var center);
    var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
    n.RemoveRoundabout(id);
    Assert.Empty(n.Roundabouts);
    Assert.DoesNotContain(n.Nodes.Values, x => x.Ring != null);
    Assert.Empty(NetworkInvariants.Check(n));
    Assert.Equal(4, n.Edges.Count); // four approach stubs remain
}

[Fact]
public void RadiusTooTightDoesNotMutate()
{
    var n = FourWayJunction(out var center);
    var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
    var edgesBefore = n.Edges.Count;
    Assert.Equal(RoundaboutError.RadiusTooTight, n.SetRoundaboutRadius(id, 0.5f).Error);
    Assert.Equal(20f, n.Roundabouts[id].Radius);
    Assert.Equal(edgesBefore, n.Edges.Count);
}
```

- [ ] **Step 2: Run to verify fail** — `dotnet test --filter "FullyQualifiedName~RoundaboutTests"`; Expected FAIL (methods undefined).

- [ ] **Step 3: Implement** `SetRoundaboutRadius`, `Regenerate`, `RemoveRoundabout`, and the `LegFullCurves` capture in `ConvertToRoundabout` + the `Roundabout` record (Task 1 file).

- [ ] **Step 4: Run to verify pass** — filter tests PASS, then full `dotnet test` + `dotnet build`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(domain): roundabout radius edit + regenerate + remove; lossless re-trim via LegFullCurves"
```

---

### Task 5: Live re-arc on approach bulldoze

**Files:**
- Modify: `src/Domain/Network/RoadNetwork.cs` (`RemoveEdge`: after `EndBatch`, drain a dirty-roundabout set → `Regenerate`)
- Modify: `src/Domain/Network/RoadNetwork.Roundabouts.cs` (dirty-set field + helper `MarkRoundaboutDirtyForRemovedEdge`)
- Test: `tests/Domain.Tests/Network/RoundaboutTests.cs`

**Design notes:**
- Add `private readonly HashSet<RoundaboutId> _dirtyRoundabouts = new();`
- In `RemoveEdge`, before `EndBatch`, for each removed edge whose far node `.Ring` is set, add that `RoundaboutId` to `_dirtyRoundabouts`. After `EndBatch`, drain: for each dirty id still in `_roundabouts`, call `Regenerate(id, currentRadius)` (each opens its own batch → its own `Changed`). Guard re-entrancy: only drain when not already inside a regenerate.
- Regenerate recomputes legs from surviving ring-node approaches; if < 3 remain it dissolves (Task 4 rule).

- [ ] **Step 1: Failing test**

```csharp
[Fact]
public void BulldozingAnApproachReArcsRing()
{
    var n = FourWayJunction(out var center);
    var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
    // pick one approach leg (non-ring edge incident to a ring node)
    var leg = n.Edges.Values.First(e =>
        n.Nodes[e.StartNode].Ring == null ^ n.Nodes[e.EndNode].Ring == null);
    n.RemoveEdge(leg.Id);
    Assert.Empty(NetworkInvariants.Check(n));
    Assert.Equal(3, n.Nodes.Values.Count(x => x.Ring == id)); // 4 → 3 ring nodes
}

[Fact]
public void BulldozingDownToTwoApproachesDissolves()
{
    var n = FourWayJunction(out var center);
    var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
    var legs = n.Edges.Values.Where(e =>
        n.Nodes[e.StartNode].Ring == null ^ n.Nodes[e.EndNode].Ring == null)
        .Select(e => e.Id).ToList();
    n.RemoveEdge(legs[0]);
    n.RemoveEdge(legs[1]); // now 2 approaches
    Assert.False(n.Roundabouts.ContainsKey(id));
    Assert.Empty(NetworkInvariants.Check(n));
}
```

- [ ] **Step 2: Run to verify fail** — filter tests FAIL (no re-arc yet: ring keeps 4 nodes or invariants break).

- [ ] **Step 3: Implement** the dirty-set drain.

- [ ] **Step 4: Run to verify pass** — filter PASS, full `dotnet test` + `dotnet build`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(domain): live roundabout re-arc on approach bulldoze; dissolve under 3 legs"
```

---

### Task 6: `NetworkInvariants` roundabout checks

**Files:**
- Modify: `src/Domain/Network/NetworkInvariants.cs`
- Test: `tests/Domain.Tests/Network/NetworkInvariantsTests.cs`

**Design notes:** add `CheckRoundabouts(network)` collecting violations: every ring edge is `OneWay` with both ends ring nodes of the same roundabout; ring node count ≥ 3; ring edges form one closed CCW cycle; each approach-bearing ring node has degree 3 with exactly one non-ring (approach) leg whose role resolves to `Yield` and ring legs `Main`; every `RoadNode.Ring` id exists in `_roundabouts` and vice-versa. Requires exposing `Roundabouts` (done Task 3).

- [ ] **Step 1: Failing test** — assert `Check` empty on a converted ring (already covered) AND flags a hand-corrupted ring (e.g. retype a ring edge to `TwoLane` via `RetypeEdge`, expect a non-empty violation list mentioning the roundabout).

```csharp
[Fact]
public void CorruptedRingEdgeTypeIsFlagged()
{
    var n = RoundaboutTests_FourWay(out var id); // small helper or inline construction
    var ringEdge = n.Roundabouts[id].RingEdges[0];
    n.RetypeEdge(ringEdge, CityBuilder.Domain.Catalog.RoadCatalog.TwoLane.Id);
    Assert.NotEmpty(NetworkInvariants.Check(n));
}
```

- [ ] **Step 2: Run to verify fail** — the corrupt case returns empty (no roundabout rule yet) → test FAILS.
- [ ] **Step 3: Implement** `CheckRoundabouts` and call it from `Check`.
- [ ] **Step 4: Run to verify pass** — filter PASS; full `dotnet test`.
- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(domain): NetworkInvariants roundabout checks — ring topology, CCW cycle, yield roles"
```

---

### Task 7: Traffic behaviour regression (no sim change expected)

**Files:**
- Test: `tests/Domain.Tests/Traffic/RoundaboutTrafficTests.cs`

**Design notes:** build a 4-way roundabout, spawn a circulating vehicle on a ring edge and an entering vehicle on an approach, step the sim, assert: the entering vehicle yields (does not enter the conflict zone while the circulating vehicle is within its accepted gap), and no co-occupancy/collision (reuse the M5 safety-invariant helpers in `tests/Domain.Tests/Traffic/`). If this requires *any* production sim change, STOP and reassess — the spec predicts none.

- [ ] **Step 1: Write the behaviour test** (model it on existing `ArbitrationTests` helpers — `PriorityLeftVsOncomingStraight` etc.).
- [ ] **Step 2: Run** — Expected PASS immediately if control resolution is correct; if FAIL, diagnose whether it's a `JunctionConfig` role bug (fix in Task 3 path) not a sim bug.
- [ ] **Step 3: Commit**

```bash
git add tests/Domain.Tests/Traffic/RoundaboutTrafficTests.cs
git commit -m "test(traffic): roundabout yields on entry, circulating has priority (no sim change)"
```

---

### Task 8: Persistence — FormatVersion 2

**Files:**
- Modify: `src/Domain/Persistence/SaveGame.cs` (add `RoundaboutDto`, `NextRoundabout` + `Roundabouts` on `SaveGame`)
- Modify: `src/Domain/Persistence/SaveLoad.cs` (bump `FormatVersion` → 2; serialize roundabouts sorted by id; byte-stable)
- Modify: `src/Domain/Network/RoadNetwork.Persistence.cs` (`RestoreInto` rebuilds `_roundabouts` + `Ring` tags + `_nextRoundabout`; `ValidateGame` roundabout checks)
- Test: `tests/Domain.Tests/Persistence/SaveLoadTests.cs`

**Design notes:**
- `RoundaboutDto(int Id, float CX, float CY, float CZ, float Radius, int[] RingNodeIds, int[] RingEdgeIds, LegCurveDto[] LegCurves)` where `LegCurveDto(int Edge, float[] Curve /*12*/)` captures `LegFullCurves`.
- `SaveGame` gains `RoundaboutDto[] Roundabouts` and `int NextRoundabout`. A v1 save (no field) deserializes these as null/0 → treat as empty, `NextRoundabout = 1`.
- `ValidateGame`: ids `1..NextRoundabout-1`, no dups, ring node/edge ids exist, ≥ 3 ring nodes, every referenced ring node's `Ring` set to this id on restore, leg-curve arrays length 12.
- Byte-stable: order roundabouts by id, ring id arrays as stored (already CCW), leg curves by edge id.

- [ ] **Step 1: Failing tests**

```csharp
[Fact]
public void RoundaboutSurvivesByteStableRoundTrip()
{
    var n = /* build + convert a 4-way roundabout */;
    var json = SaveLoad.Save(n);
    var reloaded = SaveLoad.Load(json);
    Assert.Equal(json, SaveLoad.Save(reloaded)); // byte-stable
    Assert.Single(reloaded.Roundabouts);
    Assert.Empty(NetworkInvariants.Check(reloaded));
    Assert.Contains(reloaded.Nodes.Values, x => x.Ring != null);
}

[Fact]
public void FormatV1SaveWithoutRoundaboutsStillLoads()
{
    var v1 = "{\"FormatVersion\":1,\"Nodes\":[],\"Edges\":[],\"NextNode\":1,\"NextEdge\":1,\"NextLane\":1}";
    var n = SaveLoad.Load(v1);
    Assert.Empty(n.Roundabouts);
}
```

- [ ] **Step 2: Run to verify fail.**
- [ ] **Step 3: Implement** the DTO + version bump + restore + validate.
- [ ] **Step 4: Run to verify pass** — filter PASS; full `dotnet test`.
- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(persistence): save format v2 — roundabouts round-trip byte-stable, v1 loads unchanged"
```

---

### Task 9: Fuzzer extension

**Files:**
- Modify: `tests/Domain.Tests/Fuzzing/GestureFuzzer.cs` (add convert / radius / remove-roundabout actions to the alphabet)
- Test: `tests/Domain.Tests/Fuzzing/FuzzSuiteTests.cs` (unchanged asserts; new actions exercised)

**Design notes:** add three actions (behind the existing `undo.Checkpoint()` pattern): `ConvertRandomJunction` (pick a random degree-≥3 plain node, random radius 12–40 m, call `ConvertToRoundabout`, ignore refusals), `AdjustRandomRoundaboutRadius`, `RemoveRandomRoundabout`. Keep the standing invariant assert (`NetworkInvariants.Check` empty after every action). Re-weight `pick` thresholds so the new actions get a slice.

- [ ] **Step 1:** Add the actions + wire into `Run`'s dispatch.
- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"` — Expected PASS.
- [ ] **Step 3: Run 3×10k with per-seed evidence:** run the suite at the milestone action count for seeds 101/202/303 (see `docs/verification.md` for the exact invocation) and capture the pass line per seed.
- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "test(fuzz): convert/adjust-radius/remove-roundabout in the gesture alphabet"
```

---

### Task 10: Editor surface — JunctionPanel + ToolController

**Files:**
- Modify: `src/Game/JunctionPanel.cs` (convert button + radius control + remove button, gated on selection kind)
- Modify: `src/Game/ToolController.cs` (undo checkpoints around convert/radius/remove; refresh selection after)
- Modify: `src/Game/Main.cs` / harness (extend UITEST flow to convert a junction + screenshot)
- Verify: `CITYBUILDER_SMOKE=1 godot --headless .` and `CITYBUILDER_UITEST=/tmp/ui.png godot .`

**Design notes:** reuse the panel's existing `_beforeMutate` undo hook. When the selected node is plain (degree ≥ 3, `Ring == null`): show "Convert to roundabout" + a radius `SpinBox` (min = `RoundaboutPlanner.MinFeasibleRadius`, default 20). When the node `Ring != null`: show a radius `HSlider` bound to `SetRoundaboutRadius` + "Remove roundabout". On a `RoundaboutResult` failure, `StatusFlashed`/readout the error and play `Sfx.Reject`.

- [ ] **Step 1:** Implement the panel branch + controller wiring.
- [ ] **Step 2:** Build: `dotnet build citybuilder.sln` — Expected: success.
- [ ] **Step 3:** Smoke: `CITYBUILDER_SMOKE=1 godot --headless .` — Expected: prints `SMOKE OK`.
- [ ] **Step 4:** UITEST: `CITYBUILDER_UITEST=/tmp/ui.png godot .`; read `/tmp/ui.png` and confirm a rendered ring. (If no window/display is available, note it and rely on smoke + unit coverage.)
- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(game): convert-to-roundabout in the junction inspector — radius edit, remove, undo-checkpointed"
```

---

### Task 11: KPI, health report, manual, roadmap — certification

**Files:**
- Modify: KPI harness (add a roundabout throughput/delay scenario — find it via `docs/verification.md`)
- Create: `docs/health/M7.5.md`; update `docs/health/kpi-baseline.json`
- Modify: `docs/manual/02-network-validation.md` (roundabout entity, planner, regeneration, invariants) — or a short new `docs/manual/09-roundabouts.md` + README/glossary drift
- Modify: `docs/roadmap.md` (M7.5 Done entry + known limits: draw-into-existing-ring deferred, multi-lane rings deferred, in-flight vehicles not saved)
- Modify: `docs/conventions.md` if a new constant/convention was added (CCW circulation, ring type)

- [ ] **Step 1:** Add the KPI scenario; regenerate the baseline; write `docs/health/M7.5.md` with the metric table (origin vs M7.5).
- [ ] **Step 2:** Write/extend the manual chapter; drift-check every chapter that references junction control or persistence; update glossary.
- [ ] **Step 3:** Update the roadmap Done section and known limits; update conventions if needed.
- [ ] **Step 4: Full certification pass:** `dotnet test` (all green), `dotnet build citybuilder.sln`, `CITYBUILDER_SMOKE=1 godot --headless .`, screenshot + UITEST harnesses. Capture evidence.
- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs+cert: M7.5 roundabout conversion — KPI M7.5 health report, manual/roadmap/conventions drift, fuzz 3x10k green"
```

---

## Self-Review notes

- **Spec coverage:** planner (T2), registry/convert (T3), radius/regenerate/remove/dissolve (T4), live bulldoze re-arc (T5), invariants (T6), traffic behaviour (T7), persistence v2 (T8), fuzz (T9), editor (T10), KPI+manual+roadmap (T11). Non-goals (draw-into-ring, multi-lane, saved vehicles) explicitly deferred and recorded in T11's roadmap edit. ✔
- **Refinement captured during planning:** `RingArcs` is a list of 1–2 cubic *chains* (3-leg rings have >90° gaps needing 2 cubics + a midpoint ring node) — reflected in T2's type and T3's intermediate-node insertion. `LegFullCurves` added to `Roundabout` + the save DTO so radius edits re-trim losslessly (T4/T8). These override the spec's simpler sketch where they conflict.
- **Type consistency:** `ConvertToRoundabout`/`SetRoundaboutRadius` return `RoundaboutResult`; `Regenerate` internal; `Ring` tag is `RoundaboutId?`; ring edges `OneWay`. Consistent across T1–T11.
```
