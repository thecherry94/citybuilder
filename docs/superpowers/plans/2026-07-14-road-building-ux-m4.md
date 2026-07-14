# Road-Building UX (M4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the click-state-machine placement tools with an editable draft/gesture model, upgrade snapping to candidate-scored resolution with grid/perpendicular/parallel support, and hard-block degenerate geometry (slivers, sharp angles, tight curves, kinks).

**Architecture:** Domain-pure `RoadDraft` + `IDraftShape` strategies + `DraftSession` state machine in `src/Domain/Tools/Draft/`; `SnapEngine` with position-candidate scoring + direction fallback in `src/Domain/Tools/Snapping/`; new validation rules in `RoadNetwork.Validate`. The Godot layer (`ToolController`, `GhostView`, `Toolbar`, new `GridOverlay`) becomes a thin adapter/renderer. Spec: `docs/superpowers/specs/2026-07-14-road-building-ux-design.md`.

**Tech Stack:** C# (Domain net8.0, System.Numerics only — NO Godot references in `src/Domain`), xUnit (tests net10.0), Godot 4.6.2 mono for the game layer.

## Global Constraints

- `src/Domain` must never reference Godot (golden rule #1).
- New files under `src/Domain/Tools/Draft/` and `src/Domain/Tools/Snapping/` keep namespace `CityBuilder.Domain.Tools` (folders organize, namespace stays — avoids churn in Game usings).
- Verification loop per task: `dotnet test` (all green), `dotnet build citybuilder.sln`. Game-layer tasks additionally: `CITYBUILDER_SMOKE=1 godot --headless .` must print `SMOKE OK`.
- Commit after every green task with a conventional-commit message.
- Constants (exact values): `MinJunctionAngleDeg = 25f`, `RoadType.MinSegmentLength = max(8, Width)`, `MinRadius`: TwoLane 20, FourLane 35, Street 10, Avenue 25. Arc sweep hard cap 175°, two-cubic split above 90°. Grid cell sizes 4/8/16/32, default 8, grid snap OFF by default.
- `ToolController.SetMode / HandleClickAt / HandleHoverAt` keep their exact signatures — the smoke and UI tests in `Main.cs` call them.
- Test helper `Net` (in `tests/Domain.Tests/Network/NetworkBasicsTests.cs`) is the canonical way to build test networks: `Net.New()`, `Net.Straight(a, b, type?, start?, end?)`, `Net.Commit(n, proposal)`.

---

### Task 1: Curvature — `Bezier3.SecondDerivative` + `BezierOps.MinRadius`

**Files:**
- Modify: `src/Domain/Geometry/Bezier3.cs` (add method after `Derivative`, ~line 31)
- Modify: `src/Domain/Geometry/BezierOps.cs` (add method after `SelfIntersects`)
- Test: `tests/Domain.Tests/Geometry/BezierOpsTests.cs` (append tests)

**Interfaces:**
- Consumes: existing `Bezier3` (`Point`, `Derivative`, `FromQuadratic`, `Line`).
- Produces: `Bezier3.SecondDerivative(float t) : Vector3`; `BezierOps.MinRadius(in Bezier3 c, int samples = 32) : float` — smallest XZ radius of curvature over the curve; `float.PositiveInfinity` for straight lines. Task 4 (validation) and Task 14 (readout) rely on these exact names.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Domain.Tests/Geometry/BezierOpsTests.cs` (inside the existing test class):

```csharp
[Fact]
public void MinRadiusOfStraightLineIsInfinite()
{
    var line = Bezier3.Line(new Vector3(0, 0, 0), new Vector3(100, 0, 0));
    Assert.Equal(float.PositiveInfinity, BezierOps.MinRadius(line));
}

[Fact]
public void MinRadiusRecoversCircleRadius()
{
    // quarter circle of radius 50 approximated by one cubic: kappa constant ~1/50
    const float r = 50f;
    float k = 4f / 3f * MathF.Tan(MathF.PI / 8f) * r; // standard 90° arc handle length
    var arc = new Bezier3(
        new Vector3(r, 0, 0),
        new Vector3(r, 0, k),
        new Vector3(k, 0, r),
        new Vector3(0, 0, r));
    float min = BezierOps.MinRadius(arc);
    Assert.InRange(min, r * 0.98f, r * 1.02f);
}

[Fact]
public void MinRadiusOfTightBendIsSmallerThanWideBend()
{
    var wide = Bezier3.FromQuadratic(new Vector3(0, 0, 0), new Vector3(50, 0, 10), new Vector3(100, 0, 0));
    var tight = Bezier3.FromQuadratic(new Vector3(0, 0, 0), new Vector3(50, 0, 60), new Vector3(100, 0, 0));
    Assert.True(BezierOps.MinRadius(tight) < BezierOps.MinRadius(wide));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~BezierOpsTests" 2>&1 | tail -5`
Expected: compile error — `SecondDerivative`/`MinRadius` not defined.

- [ ] **Step 3: Implement**

In `src/Domain/Geometry/Bezier3.cs`, after `Derivative`:

```csharp
public Vector3 SecondDerivative(float t)
    => 6 * (1 - t) * (P2 - 2 * P1 + P0) + 6 * t * (P3 - 2 * P2 + P1);
```

In `src/Domain/Geometry/BezierOps.cs`, after `SelfIntersects`:

```csharp
/// <summary>Smallest radius of curvature in the XZ plane, sampled. Straight
/// (zero-curvature) curves return +infinity.</summary>
public static float MinRadius(in Bezier3 c, int samples = 32)
{
    float maxK = 0f;
    for (int i = 0; i <= samples; i++)
    {
        float t = i / (float)samples;
        var d = c.Derivative(t);
        var dd = c.SecondDerivative(t);
        float speedSq = d.X * d.X + d.Z * d.Z;
        if (speedSq < 1e-6f)
            continue; // degenerate spot; neighbors cover it
        float k = MathF.Abs(d.X * dd.Z - d.Z * dd.X) / (speedSq * MathF.Sqrt(speedSq));
        maxK = MathF.Max(maxK, k);
    }
    return maxK < 1e-9f ? float.PositiveInfinity : 1f / maxK;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~BezierOpsTests" 2>&1 | tail -5`
Expected: PASS (all, including pre-existing).

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Geometry/Bezier3.cs src/Domain/Geometry/BezierOps.cs tests/Domain.Tests/Geometry/BezierOpsTests.cs
git commit -m "feat(domain): XZ curvature sampling — Bezier3.SecondDerivative + BezierOps.MinRadius"
```

---

### Task 2: `BezierOps.ArcFromTangent` — constant-radius arcs as béziers

**Files:**
- Modify: `src/Domain/Geometry/BezierOps.cs` (append)
- Test: `tests/Domain.Tests/Geometry/BezierOpsTests.cs` (append)

**Interfaces:**
- Consumes: `Bezier3`, `GeoConstants.Eps`, `MinRadius` from Task 1.
- Produces: `BezierOps.ArcFromTangent(Vector3 start, Vector3 tangent, Vector3 end) : (Bezier3[] Curves, float Radius)?` — circular arc from `start` leaving along `tangent` (XZ) ending at `end`. One cubic for sweep ≤ 90°, two cubics above, `null` when sweep > 175° or the tangent points away from a collinear end. Collinear-ahead returns a straight line with `Radius = +infinity`. Task 12 (ArcShape) consumes this exact signature.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Domain.Tests/Geometry/BezierOpsTests.cs`:

```csharp
[Fact]
public void ArcFromTangentCollinearAheadIsStraightLine()
{
    var r = BezierOps.ArcFromTangent(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(80, 0, 0));
    Assert.NotNull(r);
    Assert.Equal(float.PositiveInfinity, r.Value.Radius);
    var c = Assert.Single(r.Value.Curves);
    Assert.Equal(new Vector3(80, 0, 0), c.P3);
}

[Fact]
public void ArcFromTangentCollinearBehindIsNull()
{
    Assert.Null(BezierOps.ArcFromTangent(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(-80, 0, 0)));
}

[Fact]
public void QuarterArcHasConstantRadiusAndCorrectEndpoints()
{
    // start at origin heading +X, end at (50, 0, 50): quarter circle radius 50
    var r = BezierOps.ArcFromTangent(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(50, 0, 50));
    Assert.NotNull(r);
    Assert.InRange(r.Value.Radius, 49.9f, 50.1f);
    var c = Assert.Single(r.Value.Curves);
    Assert.True(Vector3.Distance(c.P0, new Vector3(0, 0, 0)) < 1e-3f);
    Assert.True(Vector3.Distance(c.P3, new Vector3(50, 0, 50)) < 1e-3f);
    // start tangent preserved (G1 with the requested direction)
    Assert.True(Vector3.Dot(c.Tangent(0), new Vector3(1, 0, 0)) > 0.999f);
    // constant curvature within 2%
    Assert.InRange(BezierOps.MinRadius(c), 49f, 51f);
}

[Fact]
public void WideSweepSplitsIntoTwoCubics()
{
    // ~135° sweep: start +X, end behind-left of the circle
    var r = BezierOps.ArcFromTangent(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(-35f, 0, 85f));
    Assert.NotNull(r);
    Assert.Equal(2, r.Value.Curves.Length);
    // G1 at the joint: end tangent of first == start tangent of second
    var a = r.Value.Curves[0];
    var b = r.Value.Curves[1];
    Assert.True(Vector3.Distance(a.P3, b.P0) < 1e-3f);
    Assert.True(Vector3.Dot(a.Tangent(1), b.Tangent(0)) > 0.999f);
}

[Fact]
public void SweepBeyondCapIsNull()
{
    // end almost directly behind start but offset — sweep > 175°
    Assert.Null(BezierOps.ArcFromTangent(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(-80f, 0, 1f)));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~BezierOpsTests" 2>&1 | tail -5`
Expected: compile error — `ArcFromTangent` not defined.

- [ ] **Step 3: Implement**

Append to `src/Domain/Geometry/BezierOps.cs`:

```csharp
/// <summary>Circular arc in XZ from <paramref name="start"/> leaving along
/// <paramref name="tangent"/>, ending at <paramref name="end"/>. Unique circle;
/// one cubic ≤ 90° sweep, two cubics above; null when the sweep exceeds 175° or the
/// end lies collinearly behind the start. Collinear ahead → straight line, radius ∞.</summary>
public static (Bezier3[] Curves, float Radius)? ArcFromTangent(Vector3 start, Vector3 tangent, Vector3 end)
{
    const float maxSweepRad = 175f * MathF.PI / 180f;
    var d2 = new Vector2(tangent.X, tangent.Z);
    if (d2.LengthSquared() < 1e-12f)
        return null;
    d2 = Vector2.Normalize(d2);
    var s = new Vector2(start.X, start.Z);
    var e = new Vector2(end.X, end.Z);
    var m = e - s;
    float mLen = m.Length();
    if (mLen < GeoConstants.Eps)
        return null;

    var n = new Vector2(-d2.Y, d2.X); // left normal of the travel direction
    float h = Vector2.Dot(m, n);      // signed lateral offset of the end point
    if (MathF.Abs(h) < 1e-3f * mLen)  // collinear
        return Vector2.Dot(m, d2) > 0
            ? (new[] { Bezier3.Line(start, end) }, float.PositiveInfinity)
            : null;

    float rho = mLen * mLen / (2f * h);     // signed radius (+ = center left, CCW travel)
    float r = MathF.Abs(rho);
    var c = s + n * rho;

    var v0 = s - c;
    var v1 = e - c;
    float ang = MathF.Atan2(v0.X * v1.Y - v0.Y * v1.X, Vector2.Dot(v0, v1)); // signed [-π, π]
    // travel direction: CCW when rho > 0 → sweep must have the sign of rho
    float sweep = rho > 0
        ? (ang >= 0 ? ang : ang + 2 * MathF.PI)
        : (ang <= 0 ? ang : ang - 2 * MathF.PI);
    if (MathF.Abs(sweep) > maxSweepRad)
        return null;

    int segments = MathF.Abs(sweep) > MathF.PI / 2f ? 2 : 1;
    float theta0 = MathF.Atan2(v0.Y, v0.X);
    float y = start.Y;
    var curves = new Bezier3[segments];
    for (int i = 0; i < segments; i++)
    {
        float a0 = theta0 + sweep * i / segments;
        float a1 = theta0 + sweep * (i + 1) / segments;
        float delta = a1 - a0;
        float k = 4f / 3f * MathF.Tan(MathF.Abs(delta) / 4f) * r;
        Vector2 P(float a) => c + r * new Vector2(MathF.Cos(a), MathF.Sin(a));
        // unit travel tangent at angle a: CCW = (-sin, cos), CW = (sin, -cos)
        Vector2 T(float a) => sweep > 0
            ? new Vector2(-MathF.Sin(a), MathF.Cos(a))
            : new Vector2(MathF.Sin(a), -MathF.Cos(a));
        var p0 = P(a0); var p3 = P(a1);
        var p1 = p0 + T(a0) * k;
        var p2 = p3 - T(a1) * k;
        curves[i] = new Bezier3(
            new Vector3(p0.X, y, p0.Y), new Vector3(p1.X, y, p1.Y),
            new Vector3(p2.X, y, p2.Y), new Vector3(p3.X, y, p3.Y));
    }
    // pin exact endpoints (float noise from the trig round-trip)
    curves[0] = new Bezier3(start, curves[0].P1, curves[0].P2, curves[0].P3);
    int last = segments - 1;
    curves[last] = new Bezier3(curves[last].P0, curves[last].P1, curves[last].P2, end);
    return (curves, r);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~BezierOpsTests" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Geometry/BezierOps.cs tests/Domain.Tests/Geometry/BezierOpsTests.cs
git commit -m "feat(domain): constant-radius arc construction (BezierOps.ArcFromTangent)"
```

---

### Task 3: Catalog — `RoadType.MinRadius` + `MinSegmentLength`

**Files:**
- Modify: `src/Domain/Catalog/RoadType.cs`
- Test: `tests/Domain.Tests/Catalog/` — add `RoadTypeLimitsTests.cs` (the folder exists)

**Interfaces:**
- Consumes: nothing new.
- Produces: `RoadType.MinRadius : float` (new positional record parameter after `DesignSpeedKmh`) and `RoadType.MinSegmentLength : float` (computed `max(8, Width)`). Tasks 4, 5, 14 consume these exact names via `RoadCatalog.Get(type)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Domain.Tests/Catalog/RoadTypeLimitsTests.cs`:

```csharp
using CityBuilder.Domain.Catalog;
using Xunit;

namespace CityBuilder.Domain.Tests.Catalog;

public class RoadTypeLimitsTests
{
    [Fact]
    public void EveryTypeHasPositiveGeometryLimits()
    {
        foreach (var t in RoadCatalog.All)
        {
            Assert.True(t.MinRadius > 0, $"{t.Name} MinRadius");
            Assert.True(t.MinSegmentLength >= 8f, $"{t.Name} MinSegmentLength floor");
            Assert.True(t.MinSegmentLength >= t.Width, $"{t.Name} MinSegmentLength >= width");
        }
    }

    [Fact]
    public void SpecValues()
    {
        Assert.Equal(20f, RoadCatalog.TwoLane.MinRadius);
        Assert.Equal(35f, RoadCatalog.FourLane.MinRadius);
        Assert.Equal(10f, RoadCatalog.Street.MinRadius);
        Assert.Equal(25f, RoadCatalog.Avenue.MinRadius);
        Assert.Equal(8f, RoadCatalog.TwoLane.MinSegmentLength);   // width 8
        Assert.Equal(16f, RoadCatalog.FourLane.MinSegmentLength); // width 16
        Assert.Equal(12f, RoadCatalog.Street.MinSegmentLength);   // width 12
        Assert.Equal(21f, RoadCatalog.Avenue.MinSegmentLength);   // width 21
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RoadTypeLimitsTests" 2>&1 | tail -5`
Expected: compile error — `MinRadius` not defined.

- [ ] **Step 3: Implement**

In `src/Domain/Catalog/RoadType.cs`, change the record header and add the computed property:

```csharp
public sealed record RoadType(
    RoadTypeId Id,
    string Name,
    float Width,
    IReadOnlyList<LaneSpec> Lanes,
    float DesignSpeedKmh,
    float MinRadius)
{
    /// <summary>Shortest committable edge of this type; junction spacing floor.</summary>
    public float MinSegmentLength => MathF.Max(8f, Width);
    // ... existing members unchanged
```

Update the four catalog constructors — append the `MinRadius` argument after the speed: `TwoLane … 80f, 20f)`, `FourLane … 100f, 35f)`, `Street … 50f, 10f)`, `Avenue … 60f, 25f)`.

- [ ] **Step 4: Run the full test suite** (constructor arity changed — everything must still compile)

Run: `dotnet test 2>&1 | tail -5`
Expected: PASS (149 + 2 new).

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Catalog/RoadType.cs tests/Domain.Tests/Catalog/RoadTypeLimitsTests.cs
git commit -m "feat(domain): per-type MinRadius and MinSegmentLength on RoadType"
```

---

### Task 4: Validation — per-type `TooShort` + `RadiusTooTight`

**Files:**
- Modify: `src/Domain/Tools/PlacementProposal.cs:27` (extend `PlacementError`)
- Modify: `src/Domain/Network/RoadNetwork.cs:63-98` (`Validate`)
- Test: `tests/Domain.Tests/Network/PlacementTests.cs` (append + fix any fixtures shorter than the new minimums)

**Interfaces:**
- Consumes: Task 1 `BezierOps.MinRadius`, Task 3 `RoadType.MinSegmentLength/MinRadius`.
- Produces: `PlacementError.RadiusTooTight`, `PlacementError.SharpAngle`, `PlacementError.Kinked` enum members (SharpAngle/Kinked implemented in Task 5, declared now so the enum changes once). `Validate` blocks curves shorter than the proposal type's `MinSegmentLength` and curves whose `MinRadius` is below the type's.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Domain.Tests/Network/PlacementTests.cs`:

```csharp
[Fact]
public void SegmentShorterThanTypeMinimumIsRejected()
{
    var n = Net.New();
    // 10 m FourLane: above the old 4 m floor, below FourLane's 16 m minimum
    var v = n.Validate(Net.Straight(new(0, 0, 0), new(10, 0, 0), RoadCatalog.FourLane.Id));
    Assert.False(v.IsValid);
    Assert.Contains(PlacementError.TooShort, v.Errors);
}

[Fact]
public void SegmentAtTypeMinimumIsAccepted()
{
    var n = Net.New();
    var v = n.Validate(Net.Straight(new(0, 0, 0), new(16.5f, 0, 0), RoadCatalog.FourLane.Id));
    Assert.True(v.IsValid, string.Join(",", v.Errors));
}

[Fact]
public void CurveTighterThanTypeMinRadiusIsRejected()
{
    var n = Net.New();
    // hairpin quadratic: control far off-axis → radius well under TwoLane's 20 m
    var curve = Bezier3.FromQuadratic(new(0, 0, 0), new(15, 0, 40), new(30, 0, 0));
    var v = n.Validate(new PlacementProposal(
        new[] { new ProposedCurve(curve, EndpointBinding.None, EndpointBinding.None) },
        RoadCatalog.TwoLane.Id));
    Assert.False(v.IsValid);
    Assert.Contains(PlacementError.RadiusTooTight, v.Errors);
}

[Fact]
public void GentleCurveIsAccepted()
{
    var n = Net.New();
    var curve = Bezier3.FromQuadratic(new(0, 0, 0), new(60, 0, 15), new(120, 0, 0));
    var v = n.Validate(new PlacementProposal(
        new[] { new ProposedCurve(curve, EndpointBinding.None, EndpointBinding.None) },
        RoadCatalog.TwoLane.Id));
    Assert.True(v.IsValid, string.Join(",", v.Errors));
}
```

Add missing usings at the top of the file if absent: `using CityBuilder.Domain.Catalog;`, `using CityBuilder.Domain.Geometry;`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PlacementTests" 2>&1 | tail -5`
Expected: FAIL — `RadiusTooTight` not defined / 10 m segment currently valid.

- [ ] **Step 3: Implement**

`src/Domain/Tools/PlacementProposal.cs:27`:

```csharp
public enum PlacementError
{
    TooShort, SelfIntersecting, Overlapping, CrossingTooShallow,
    RadiusTooTight, SharpAngle, Kinked,
}
```

In `RoadNetwork.Validate` (`src/Domain/Network/RoadNetwork.cs`), replace the length check and add the radius check inside the `foreach (var pc in proposal.Curves)` loop:

```csharp
var type = RoadCatalog.Get(proposal.Type);
foreach (var pc in proposal.Curves)
{
    if (pc.Curve.Length() < type.MinSegmentLength)
        errors.Add(PlacementError.TooShort);

    if (BezierOps.MinRadius(pc.Curve) < type.MinRadius)
        errors.Add(PlacementError.RadiusTooTight);
    // ... existing SelfIntersects / Overlaps / crossing checks unchanged
```

(`type` hoisted before the loop; `using CityBuilder.Domain.Catalog;` is already imported.)

- [ ] **Step 4: Run the full suite; fix broken fixtures**

Run: `dotnet test 2>&1 | tail -20`
Expected: the 4 new tests pass. Some existing tests may fail if a fixture commits an edge shorter than its type minimum (e.g. 5–15 m FourLane stubs) or a too-tight curve. For each failure: lengthen the fixture geometry (keep the *shape* of the scenario, scale distances up) — do NOT weaken the assertion. Grep candidates first: `grep -rn "Straight(new" tests/Domain.Tests | grep -v "0, 0, 0"` and inspect distances. Re-run until green.

- [ ] **Step 5: Commit**

```bash
git add -A tests/Domain.Tests src/Domain
git commit -m "feat(domain): per-type TooShort + RadiusTooTight placement validation"
```

---

### Task 5: Validation — `SharpAngle`, `Kinked`, crossing-spacing slivers

**Files:**
- Modify: `src/Domain/Network/RoadNetwork.cs` (`Validate` + new private helpers; `MinJunctionAngleDeg` const)
- Test: `tests/Domain.Tests/Network/PlacementTests.cs` (append), `tests/Domain.Tests/Network/GeometryGuardTests.cs` (new — invariant test)

**Interfaces:**
- Consumes: Tasks 3–4.
- Produces: `RoadNetwork.MinJunctionAngleDeg = 25f` public const. `Validate` additionally blocks: (a) new-edge-vs-existing-edge leg angles < 25° at bound/near endpoints (`SharpAngle`); (b) shared-endpoint curve pairs within one proposal meeting < 25° (`Kinked`); (c) crossings that would leave a segment (on the new curve or the crossed edge) shorter than the applicable type minimum (`TooShort`); (d) `OnEdge` endpoint bindings landing within the edge type's minimum of that edge's end (unless within `NodeReuseRadius` — that's a node connection).

- [ ] **Step 1: Write the failing tests**

Append to `tests/Domain.Tests/Network/PlacementTests.cs`:

```csharp
[Fact]
public void NearParallelStubOffExistingNodeIsRejected()
{
    var n = Net.New();
    var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
    var end = n.Edges[r.CreatedEdges[0]].EndNode;
    // leaves the node at ~11° to the existing edge — a protruding bump
    var v = n.Validate(Net.Straight(new(100, 0, 0), new(60, 0, 8),
        start: new EndpointBinding.AtNode(end)));
    Assert.False(v.IsValid);
    Assert.Contains(PlacementError.SharpAngle, v.Errors);
}

[Fact]
public void StraightContinuationOffExistingNodeIsAccepted()
{
    var n = Net.New();
    var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
    var end = n.Edges[r.CreatedEdges[0]].EndNode;
    var v = n.Validate(Net.Straight(new(100, 0, 0), new(200, 0, 0),
        start: new EndpointBinding.AtNode(end)));
    Assert.True(v.IsValid, string.Join(",", v.Errors));
}

[Fact]
public void SharpAngleAlsoCaughtForUnboundEndpointNearNode()
{
    var n = Net.New();
    Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
    // free endpoint 0.3 m from the node at (100,0,0): commit would reuse the node
    var v = n.Validate(Net.Straight(new(100.3f, 0, 0), new(60, 0, 8)));
    Assert.False(v.IsValid);
    Assert.Contains(PlacementError.SharpAngle, v.Errors);
}

[Fact]
public void ZigzagDoubleBackWithinProposalIsRejected()
{
    var n = Net.New();
    // two segments sharing an endpoint, second doubles back at ~14°
    var v = n.Validate(new PlacementProposal(new[]
    {
        new ProposedCurve(Bezier3.Line(new(0, 0, 0), new(50, 0, 0)), EndpointBinding.None, EndpointBinding.None),
        new ProposedCurve(Bezier3.Line(new(50, 0, 0), new(10, 0, 10)), EndpointBinding.None, EndpointBinding.None),
    }, RoadCatalog.TwoLane.Id));
    Assert.False(v.IsValid);
    Assert.Contains(PlacementError.Kinked, v.Errors);
}

[Fact]
public void CrossingTooCloseToExistingJunctionIsRejected()
{
    var n = Net.New();
    Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
    // crosses the edge 3 m from its end node → 3 m sliver on the existing TwoLane (min 8)
    var v = n.Validate(Net.Straight(new(97, 0, -50), new(97, 0, 50)));
    Assert.False(v.IsValid);
    Assert.Contains(PlacementError.TooShort, v.Errors);
}

[Fact]
public void TwoCrossingsTooCloseTogetherAreRejected()
{
    var n = Net.New();
    Net.Commit(n, Net.Straight(new(40, 0, -50), new(40, 0, 50)));
    Net.Commit(n, Net.Straight(new(45, 0, -50), new(45, 0, 50)));
    // new road crosses both verticals 5 m apart → 5 m sliver between junctions
    var v = n.Validate(Net.Straight(new(0, 0, 0), new(100, 0, 0)));
    Assert.False(v.IsValid);
    Assert.Contains(PlacementError.TooShort, v.Errors);
}

[Fact]
public void EndpointOnEdgeTooCloseToItsEndIsRejected()
{
    var n = Net.New();
    var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
    var edge = r.CreatedEdges[0];
    // T-connection landing 3 m from the edge's end node (t≈0.97, TwoLane min 8)
    var v = n.Validate(Net.Straight(new(97, 0, 50), new(97, 0, 0),
        end: new EndpointBinding.OnEdge(edge, 0.97f)));
    Assert.False(v.IsValid);
    Assert.Contains(PlacementError.TooShort, v.Errors);
}
```

Create `tests/Domain.Tests/Network/GeometryGuardTests.cs` — the standing invariant:

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

/// <summary>Invariants the M4 geometry guards must keep: whatever sequence of valid
/// commits happens, the network never contains sliver edges or sharp junction legs.</summary>
public class GeometryGuardTests
{
    [Fact]
    public void CommittedNetworkHasNoSliversAndNoSharpLegs()
    {
        var n = new RoadNetwork();
        // a representative editing session: grid-ish mesh, diagonals, T-connections
        TryCommit(n, Net.Straight(new(0, 0, 0), new(200, 0, 0)));
        TryCommit(n, Net.Straight(new(0, 0, 100), new(200, 0, 100)));
        TryCommit(n, Net.Straight(new(50, 0, -50), new(50, 0, 150)));
        TryCommit(n, Net.Straight(new(150, 0, -50), new(150, 0, 150)));
        TryCommit(n, Net.Straight(new(0, 0, -30), new(200, 0, 130)));       // diagonal, crosses several
        TryCommit(n, Net.Straight(new(52, 0, -48), new(50, 0, 150)));       // near-duplicate — must be rejected, not committed
        TryCommit(n, Net.Straight(new(148, 0, 2), new(152, 0, 98)));        // sliver-ish vertical near existing
        foreach (var e in n.Edges.Values)
        {
            float min = RoadCatalog.Get(e.Type).MinSegmentLength;
            Assert.True(e.Curve.Length() >= min - 0.1f,
                $"edge {e.Id} length {e.Curve.Length():F1} < min {min}");
        }
        foreach (var node in n.Nodes.Values)
        {
            var legs = node.EdgeSet.Select(id =>
            {
                var e = n.Edges[id];
                return e.StartNode == node.Id ? e.Curve.Tangent(0) : -e.Curve.Tangent(1);
            }).ToArray();
            for (int i = 0; i < legs.Length; i++)
            for (int j = i + 1; j < legs.Length; j++)
            {
                float cross = MathF.Abs(legs[i].X * legs[j].Z - legs[i].Z * legs[j].X);
                float dot = legs[i].X * legs[j].X + legs[i].Z * legs[j].Z;
                float deg = MathF.Atan2(cross, dot) * 180f / MathF.PI;
                Assert.True(deg >= RoadNetwork.MinJunctionAngleDeg - 0.5f,
                    $"node {node.Id}: legs {deg:F1}° apart");
            }
        }
    }

    private static void TryCommit(RoadNetwork n, PlacementProposal p)
    {
        var v = n.Validate(p);
        if (v.IsValid)
            n.Commit(v);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PlacementTests|FullyQualifiedName~GeometryGuardTests" 2>&1 | tail -10`
Expected: new tests FAIL (rules absent), `MinJunctionAngleDeg` compile error.

- [ ] **Step 3: Implement**

In `src/Domain/Network/RoadNetwork.cs` add next to `MinCrossingAngleDeg`:

```csharp
/// <summary>Minimum angle between any two legs meeting at a node.</summary>
public const float MinJunctionAngleDeg = 25f;
```

Extend `Validate`. Full replacement of the method body (keeping existing checks, adding the new ones):

```csharp
public ValidatedPlacement Validate(PlacementProposal proposal)
{
    var errors = new List<PlacementError>();
    var crossings = new List<Vector3>();
    var type = RoadCatalog.Get(proposal.Type);

    foreach (var pc in proposal.Curves)
    {
        if (pc.Curve.Length() < type.MinSegmentLength)
            errors.Add(PlacementError.TooShort);

        if (BezierOps.MinRadius(pc.Curve) < type.MinRadius)
            errors.Add(PlacementError.RadiusTooTight);

        if (BezierOps.SelfIntersects(pc.Curve))
            errors.Add(PlacementError.SelfIntersecting);

        if (OverlapsExisting(pc, proposal.Type))
            errors.Add(PlacementError.Overlapping);

        Vector3 a = pc.Curve.Point(0), b = pc.Curve.Point(1);
        bool shallow = false, sliver = false;
        var crossParams = new List<float>();
        foreach (var e in _edges.Values)
        foreach (var (t1, t2) in BezierOps.Intersections(pc.Curve, e.Curve))
        {
            var p = pc.Curve.Point(t1);
            if (Vector3.Distance(p, a) <= NodeReuseRadius || Vector3.Distance(p, b) <= NodeReuseRadius)
                continue; // connection at an endpoint, not a crossing
            crossings.Add(p);
            crossParams.Add(t1);
            if (CrossingAngleDeg(pc.Curve.Tangent(t1), e.Curve.Tangent(t2)) < MinCrossingAngleDeg)
                shallow = true;
            // crossing must not leave a sliver on the existing edge
            float dAlong = e.ArcLength.DistanceAtT(t2);
            float eMin = RoadCatalog.Get(e.Type).MinSegmentLength;
            if (dAlong < eMin || e.ArcLength.TotalLength - dAlong < eMin)
                sliver = true;
        }
        if (shallow)
            errors.Add(PlacementError.CrossingTooShallow);

        // consecutive stops along the new curve (ends + crossings) must be ≥ min apart
        if (crossParams.Count > 0)
        {
            crossParams.Sort();
            float totalLen = pc.Curve.Length();
            float prev = 0f;
            foreach (var t in crossParams.Concat(new[] { 1f }))
            {
                if ((t - prev) * totalLen < type.MinSegmentLength - 0.1f)
                    sliver = true; // chord-scaled approximation; exact enough at these sizes
                prev = t;
            }
        }

        // OnEdge endpoint bindings must not land a sliver from the edge's ends
        sliver |= BindingLeavesSliver(pc.Start) || BindingLeavesSliver(pc.End);
        if (sliver)
            errors.Add(PlacementError.TooShort);

        // sharp legs against the existing network at both ends
        if (HasSharpLeg(pc.Start, a, pc.Curve.Tangent(0))
            || HasSharpLeg(pc.End, b, -pc.Curve.Tangent(1)))
            errors.Add(PlacementError.SharpAngle);
    }

    // kinks between curves of the same proposal that share an endpoint
    if (HasInternalKink(proposal))
        errors.Add(PlacementError.Kinked);

    errors = errors.Distinct().ToList();
    return new ValidatedPlacement(proposal, errors.Count == 0, errors, crossings, Version);
}

private bool BindingLeavesSliver(EndpointBinding binding)
{
    if (binding is not EndpointBinding.OnEdge(var edgeId, var t) || !_edges.TryGetValue(edgeId, out var e))
        return false;
    float d = e.ArcLength.DistanceAtT(t);
    float min = RoadCatalog.Get(e.Type).MinSegmentLength;
    // within reuse radius of an end = clean node connection, not a split
    if (d <= NodeReuseRadius || e.ArcLength.TotalLength - d <= NodeReuseRadius)
        return false;
    return d < min || e.ArcLength.TotalLength - d < min;
}

private bool HasSharpLeg(EndpointBinding binding, Vector3 pos, Vector3 newLeaving)
{
    foreach (var leg in ExistingLegDirections(binding, pos))
        if (AngleDegXZ(newLeaving, leg) < MinJunctionAngleDeg)
            return true;
    return false;
}

private IEnumerable<Vector3> ExistingLegDirections(EndpointBinding binding, Vector3 pos)
{
    NodeId? nodeId = binding switch
    {
        EndpointBinding.AtNode(var id) when _nodes.ContainsKey(id) => id,
        _ => null,
    };
    if (nodeId is null && binding is EndpointBinding.OnEdge(var eid, var t) && _edges.TryGetValue(eid, out var onEdge))
    {
        var tan = onEdge.Curve.Tangent(t);
        yield return tan;
        yield return -tan;
        yield break;
    }
    nodeId ??= FindNodeNear(pos, NodeReuseRadius);
    if (nodeId is { } id2 && _nodes.TryGetValue(id2, out var node))
    {
        foreach (var legEdge in node.EdgeSet)
        {
            var e = _edges[legEdge];
            yield return e.StartNode == id2 ? e.Curve.Tangent(0) : -e.Curve.Tangent(1);
        }
        yield break;
    }
    if (FindClosestEdge(pos, NodeReuseRadius) is { } hit)
    {
        var tan = _edges[hit.id].Curve.Tangent(hit.t);
        yield return tan;
        yield return -tan;
    }
}

private static bool SharedEndpoint(Vector3 x, Vector3 y) => Vector3.Distance(x, y) <= NodeReuseRadius;

private bool HasInternalKink(PlacementProposal proposal)
{
    var curves = proposal.Curves;
    for (int i = 0; i < curves.Count; i++)
    for (int j = i + 1; j < curves.Count; j++)
    {
        var ci = curves[i].Curve;
        var cj = curves[j].Curve;
        // leaving directions away from the shared point, all 4 endpoint pairings
        foreach (var (li, lj, shared) in new[]
        {
            (ci.Tangent(0), cj.Tangent(0), SharedEndpoint(ci.P0, cj.P0)),
            (ci.Tangent(0), -cj.Tangent(1), SharedEndpoint(ci.P0, cj.P3)),
            (-ci.Tangent(1), cj.Tangent(0), SharedEndpoint(ci.P3, cj.P0)),
            (-ci.Tangent(1), -cj.Tangent(1), SharedEndpoint(ci.P3, cj.P3)),
        })
        {
            if (shared && AngleDegXZ(li, lj) < MinJunctionAngleDeg)
                return true;
        }
    }
    return false;
}

private static float AngleDegXZ(Vector3 u, Vector3 v)
{
    float cross = MathF.Abs(u.X * v.Z - u.Z * v.X);
    float dot = u.X * v.X + u.Z * v.Z; // signed: 0° = same direction, 180° = opposite
    return MathF.Atan2(cross, dot) * 180f / MathF.PI;
}
```

Note `AngleDegXZ` uses the **signed** dot (unlike `CrossingAngleDeg`) — leg directions pointing the *same* way are 0° (sharp), opposite ways are 180° (fine).

- [ ] **Step 4: Run the full suite; fix broken fixtures**

Run: `dotnet test 2>&1 | tail -20`
Expected: new tests pass. Fixtures that commit legs < 25° apart or crossings near ends will fail — widen the fixture geometry, keep the assertions. Re-run until green.

- [ ] **Step 5: Commit**

```bash
git add -A src/Domain tests/Domain.Tests
git commit -m "feat(domain): sharp-angle, kink, and sliver-crossing geometry guards"
```

---

### Task 6: `SnapEngine` — candidate-scored resolution (port of existing snap kinds)

**Files:**
- Create: `src/Domain/Tools/Snapping/SnapEngine.cs`
- Modify: `src/Domain/Tools/SnapService.cs` (extend shared records `SnapContext`, `SnapResult`, `SnapTypes`, `SnapKind` — they stay in this file for now; `SnapService` class untouched and still used by the game until Task 15)
- Test: `tests/Domain.Tests/Tools/SnapEngineTests.cs` (new; port the behaviors of `SnapServiceTests`)

**Interfaces:**
- Consumes: `RoadNetwork.FindNodeNear/FindClosestEdge`, existing `Guideline`/`SnapResult` records.
- Produces:
  - `SnapKind` gains `GridPoint`, `GridLine`, `Perpendicular`.
  - `SnapTypes` gains `Grid = 16`, `Parallel = 32`, `Perpendicular = 64`; `All` includes them.
  - `SnapResult` gains `Vector3? DirectionConstraint = null` (trailing optional — existing positional constructions keep compiling).
  - `SnapContext` becomes `(Vector3? Anchor, Vector3? ReferenceTangent, GridConfig? Grid = null, RoadTypeId? DrawingType = null)`.
  - `GridConfig(float CellSize)` record with `static GridConfig Default => new(8f)`.
  - `SnapEngine(RoadNetwork network)` with `Resolve(Vector3 raw, float radius, SnapTypes enabled, SnapContext ctx) : SnapResult` — same facade as `SnapService.Resolve`. Tasks 7–9 add producers; Task 14 consumes.

- [ ] **Step 1: Write the failing tests**

Create `tests/Domain.Tests/Tools/SnapEngineTests.cs`:

```csharp
using System.Numerics;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class SnapEngineTests
{
    private static (RoadNetwork n, SnapEngine snap) Setup()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        return (n, new SnapEngine(n));
    }

    [Fact]
    public void NodeBeatsEdgeWhenBothInRange()
    {
        var (n, snap) = Setup();
        var result = snap.Resolve(new Vector3(98.5f, 0, 1.2f), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Node, result.Kind);
        Assert.Equal(n.Nodes[result.Node!.Value].Position, result.Position);
    }

    [Fact]
    public void EdgeSnapProjectsOntoCenterline()
    {
        var (_, snap) = Setup();
        var result = snap.Resolve(new Vector3(50, 0, 3), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Edge, result.Kind);
        Assert.True(Vector3.Distance(result.Position, new Vector3(50, 0, 0)) < 0.1f);
    }

    [Fact]
    public void GuidelineExtensionSnapsPastTheNode()
    {
        var (_, snap) = Setup();
        var result = snap.Resolve(new Vector3(130, 0, 2.5f), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Guideline, result.Kind);
        Assert.True(Vector3.Distance(result.Position, new Vector3(130, 0, 0)) < 0.1f);
    }

    [Fact]
    public void DeadOnWeakSnapBeatsBarelyInRangeStrongSnap()
    {
        var (_, snap) = Setup();
        // cursor exactly on the guideline extension, 4.6 m from the node: guideline wins
        var result = snap.Resolve(new Vector3(104.6f, 0, 0.01f), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Guideline, result.Kind);
    }

    [Fact]
    public void AngleSnapMeasuresFromReferenceTangent()
    {
        var (_, snap) = Setup();
        // anchor at the far node, reference tangent +X (extending the road):
        // cursor ~46° up-right must snap to the 45° ray FROM +X, not from world axes
        var ctx = new SnapContext(new Vector3(100, 0, 0), new Vector3(1, 0, 0));
        var raw = new Vector3(100, 0, 0) + 40f * new Vector3(MathF.Cos(0.82f), 0, MathF.Sin(0.82f));
        var result = snap.Resolve(raw, 2f, SnapTypes.Angle, ctx);
        Assert.Equal(SnapKind.Angle, result.Kind);
        Assert.Equal(45f, result.SnappedAngleDeg!.Value, 1);
        var dir = Vector3.Normalize(result.Position - new Vector3(100, 0, 0));
        Assert.Equal(MathF.Cos(MathF.PI / 4), dir.X, 2);
        Assert.Equal(MathF.Sin(MathF.PI / 4), dir.Z, 2);
    }

    [Fact]
    public void FreeWhenNothingInRange()
    {
        var (_, snap) = Setup();
        var raw = new Vector3(500, 0, 500);
        var result = snap.Resolve(raw, 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Free, result.Kind);
        Assert.Equal(raw, result.Position);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SnapEngineTests" 2>&1 | tail -5`
Expected: compile error — `SnapEngine` not defined.

- [ ] **Step 3: Extend the shared records in `src/Domain/Tools/SnapService.cs`**

```csharp
public enum SnapKind { Free, Node, Edge, GuidelineIntersection, Guideline, Angle, GridPoint, GridLine, Perpendicular }

[Flags]
public enum SnapTypes
{
    None = 0,
    Nodes = 1,
    Edges = 2,
    Angle = 4,
    Guidelines = 8,
    Grid = 16,
    Parallel = 32,
    Perpendicular = 64,
    All = Nodes | Edges | Angle | Guidelines | Grid | Parallel | Perpendicular,
}

/// <summary>World-aligned square snapping grid.</summary>
public sealed record GridConfig(float CellSize)
{
    public static readonly GridConfig Default = new(8f);
}

public sealed record SnapContext(
    Vector3? Anchor,
    Vector3? ReferenceTangent,
    GridConfig? Grid = null,
    RoadTypeId? DrawingType = null)
{
    public static readonly SnapContext Empty = new(null, null);
}

public sealed record SnapResult(
    Vector3 Position,
    SnapKind Kind,
    NodeId? Node,
    (EdgeId Edge, float T)? Edge,
    float? SnappedAngleDeg,
    IReadOnlyList<Guideline> ActiveGuidelines,
    Vector3? DirectionConstraint = null)
{
    public static SnapResult Free(Vector3 p)
        => new(p, SnapKind.Free, null, null, null, Array.Empty<Guideline>());
}
```

(Everything else in the file — `Guideline`, `SnapService` — unchanged. `ToolController`'s default `SnapTypes.All` now includes the new flags; harmless until the engine cutover, since `SnapService` ignores them.)

- [ ] **Step 4: Implement `SnapEngine`**

Create `src/Domain/Tools/Snapping/SnapEngine.cs`:

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tools;

/// <summary>One potential snap target. Score = distance / weight; lowest wins,
/// so heavier kinds win ties but a dead-on weak snap beats a distant strong one.</summary>
public readonly record struct SnapCandidate(
    Vector3 Position,
    SnapKind Kind,
    float Weight,
    NodeId? Node = null,
    (EdgeId Edge, float T)? Edge = null,
    Vector3? Direction = null,
    Guideline? Guide = null,
    Guideline? Guide2 = null);

/// <summary>Candidate-scored snap resolution: every enabled producer emits position
/// candidates, the best score wins; with no winner, angle snap (relative to the
/// context's reference tangent) is the directional fallback, then Free.</summary>
public sealed class SnapEngine(RoadNetwork network)
{
    public const float GuidelineReach = 200f;
    public const float GuidelineSearch = 200f;
    public const float AngleStepDeg = 15f;

    // node is 4.0 (not the spec's sketched 3.0): with 3.0, a node 1.9 m away
    // loses to the edge underneath it 1.2 m away — the ported NodeBeatsEdge test fails
    public const float WeightNode = 4.0f;
    public const float WeightGuideIntersection = 2.5f;
    public const float WeightPerpendicular = 2.2f;
    public const float WeightEdge = 2.0f;
    public const float WeightGuideline = 1.5f;
    public const float WeightGridPoint = 1.2f;
    public const float WeightGridLine = 1.0f;

    public SnapResult Resolve(Vector3 raw, float radius, SnapTypes enabled, SnapContext ctx)
    {
        var guidelines = (enabled & SnapTypes.Guidelines) != 0
            ? CollectGuidelines(raw, enabled, ctx)
            : new List<Guideline>();

        var candidates = new List<SnapCandidate>();
        if ((enabled & SnapTypes.Nodes) != 0)
            AddNodeCandidates(raw, radius, candidates);
        if ((enabled & SnapTypes.Edges) != 0)
            AddEdgeCandidates(raw, radius, candidates);
        if ((enabled & SnapTypes.Guidelines) != 0)
            AddGuidelineCandidates(guidelines, raw, radius, candidates);
        if ((enabled & SnapTypes.Perpendicular) != 0 && ctx.Anchor is { } anchor)
            AddPerpendicularCandidates(raw, radius, anchor, candidates);
        if ((enabled & SnapTypes.Grid) != 0 && ctx.Grid is { } grid)
            AddGridCandidates(raw, radius, grid, candidates);

        SnapCandidate? best = null;
        float bestScore = float.MaxValue;
        foreach (var c in candidates)
        {
            float d = Vector3.Distance(c.Position, raw);
            if (d > radius)
                continue;
            float score = d / c.Weight;
            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        if (best is { } win)
        {
            var active = win.Guide is { } g
                ? (win.Guide2 is { } g2 ? new[] { g, g2 } : new[] { g })
                : NearbyGuides(guidelines, win.Position, radius);
            return new SnapResult(win.Position, win.Kind, win.Node, win.Edge, null, active, win.Direction);
        }

        if ((enabled & SnapTypes.Angle) != 0 && ctx.Anchor is { } a2 && AngleSnap(raw, a2, ctx) is { } angled)
            return angled;

        return SnapResult.Free(raw);
    }

    // ------------------------------------------------------------- producers

    private void AddNodeCandidates(Vector3 raw, float radius, List<SnapCandidate> outList)
    {
        foreach (var n in network.Nodes.Values)
            if (Vector3.Distance(n.Position, raw) <= radius)
                outList.Add(new SnapCandidate(n.Position, SnapKind.Node, WeightNode, Node: n.Id));
    }

    private void AddEdgeCandidates(Vector3 raw, float radius, List<SnapCandidate> outList)
    {
        if (network.FindClosestEdge(raw, radius) is { } hit)
        {
            var pos = network.Edges[hit.id].Curve.Point(hit.t);
            outList.Add(new SnapCandidate(pos, SnapKind.Edge, WeightEdge, Edge: (hit.id, hit.t)));
        }
    }

    private static void AddGuidelineCandidates(List<Guideline> guides, Vector3 raw, float radius,
        List<SnapCandidate> outList)
    {
        // pairwise intersections
        for (int i = 0; i < guides.Count; i++)
        for (int j = i + 1; j < guides.Count; j++)
        {
            var a = guides[i];
            var b = guides[j];
            if (!BezierOps.SegmentIntersect(
                    new Vector2(a.Origin.X, a.Origin.Z),
                    new Vector2(a.PointAt(a.Length).X, a.PointAt(a.Length).Z),
                    new Vector2(b.Origin.X, b.Origin.Z),
                    new Vector2(b.PointAt(b.Length).X, b.PointAt(b.Length).Z),
                    out float u, out _))
                continue;
            var p = a.PointAt(u * a.Length);
            if (Vector3.Distance(p, raw) <= radius)
                outList.Add(new SnapCandidate(p, SnapKind.GuidelineIntersection, WeightGuideIntersection,
                    Guide: a, Guide2: b));
        }
        // projections
        foreach (var g in guides)
            if (ProjectOntoGuide(g, raw) is { } p)
                outList.Add(new SnapCandidate(p, SnapKind.Guideline, WeightGuideline, Guide: g));
    }

    private void AddPerpendicularCandidates(Vector3 raw, float radius, Vector3 anchor,
        List<SnapCandidate> outList)
    {
        // Task 8 implements; empty here so Task 6 compiles.
    }

    private static void AddGridCandidates(Vector3 raw, float radius, GridConfig grid,
        List<SnapCandidate> outList)
    {
        // Task 7 implements; empty here so Task 6 compiles.
    }

    // ---------------------------------------------------------------- guides

    private List<Guideline> CollectGuidelines(Vector3 near, SnapTypes enabled, SnapContext ctx)
    {
        var guides = new List<Guideline>();
        foreach (var node in network.Nodes.Values)
        {
            if (Vector3.Distance(node.Position, near) > GuidelineSearch)
                continue;
            foreach (var edgeId in node.Edges)
            {
                var edge = network.Edges[edgeId];
                bool startsHere = edge.StartNode == node.Id;
                var leaving = startsHere ? edge.Curve.Tangent(0) : -edge.Curve.Tangent(1);
                leaving.Y = 0;
                if (leaving.LengthSquared() < GeoConstants.Eps)
                    continue;
                guides.Add(new Guideline(node.Position, Vector3.Normalize(-leaving), GuidelineReach));
            }
        }
        // Task 9 adds parallel guides here when (enabled & SnapTypes.Parallel) != 0.
        return guides;
    }

    private static Vector3? ProjectOntoGuide(Guideline g, Vector3 p)
    {
        float s = Vector3.Dot(p - g.Origin, g.Direction);
        if (s < 0 || s > g.Length)
            return null;
        return g.PointAt(s);
    }

    private static IReadOnlyList<Guideline> NearbyGuides(List<Guideline> guides, Vector3 pos, float radius)
        => guides.Where(g => ProjectOntoGuide(g, pos) is { } p && Vector3.Distance(p, pos) <= radius).ToArray();

    // ----------------------------------------------------------------- angle

    private static SnapResult? AngleSnap(Vector3 raw, Vector3 anchor, SnapContext ctx)
    {
        var v = raw - anchor;
        v.Y = 0;
        float len = v.Length();
        if (len <= GeoConstants.Eps)
            return null;
        var reference = ctx.ReferenceTangent is { } rt && new Vector2(rt.X, rt.Z).LengthSquared() > 0
            ? Vector3.Normalize(new Vector3(rt.X, 0, rt.Z))
            : Vector3.UnitX;
        float rel = SignedAngleDeg(reference, v / len);
        float snapped = MathF.Round(rel / AngleStepDeg) * AngleStepDeg;
        var dir = RotateXZ(reference, snapped * MathF.PI / 180f);
        return new SnapResult(anchor + dir * len, SnapKind.Angle, null, null, snapped,
            Array.Empty<Guideline>());
    }

    private static float SignedAngleDeg(Vector3 from, Vector3 to)
    {
        float cross = from.X * to.Z - from.Z * to.X;
        float dot = from.X * to.X + from.Z * to.Z;
        return MathF.Atan2(cross, dot) * 180f / MathF.PI;
    }

    private static Vector3 RotateXZ(Vector3 v, float rad)
    {
        float c = MathF.Cos(rad), s = MathF.Sin(rad);
        return new Vector3(v.X * c - v.Z * s, 0, v.X * s + v.Z * c);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass** (old `SnapServiceTests` must also still pass — `SnapService` is untouched)

Run: `dotnet test --filter "FullyQualifiedName~SnapEngineTests|FullyQualifiedName~SnapServiceTests" 2>&1 | tail -5`
Expected: PASS. Then full sweep: `dotnet test 2>&1 | tail -3` and `dotnet build citybuilder.sln 2>&1 | tail -3` (Game still compiles against the extended records).

- [ ] **Step 6: Commit**

```bash
git add src/Domain/Tools tests/Domain.Tests/Tools/SnapEngineTests.cs
git commit -m "feat(domain): SnapEngine — candidate-scored snap resolution"
```

---

### Task 7: Grid snapping

**Files:**
- Modify: `src/Domain/Tools/Snapping/SnapEngine.cs` (fill `AddGridCandidates`)
- Test: `tests/Domain.Tests/Tools/SnapEngineTests.cs` (append)

**Interfaces:**
- Consumes: Task 6 `GridConfig`, `SnapCandidate`.
- Produces: `SnapKind.GridPoint` / `SnapKind.GridLine` results when `SnapTypes.Grid` is enabled and `ctx.Grid` is set.

- [ ] **Step 1: Write the failing tests**

Append to `SnapEngineTests`:

```csharp
[Fact]
public void GridPointSnapsToNearestIntersection()
{
    var (_, snap) = Setup();
    var ctx = SnapContext.Empty with { Grid = new GridConfig(8f) };
    var result = snap.Resolve(new Vector3(302.2f, 0, 297.9f), 5f, SnapTypes.Grid, ctx);
    Assert.Equal(SnapKind.GridPoint, result.Kind);
    Assert.Equal(new Vector3(304, 0, 296), result.Position);
}

[Fact]
public void GridLineWinsWhenIntersectionIsFar()
{
    var (_, snap) = Setup();
    var ctx = SnapContext.Empty with { Grid = new GridConfig(8f) };
    // 0.4 m off the x=304 line but 3.9 m from the nearest intersection:
    // line score 0.4/1.0 = 0.4 < point score 3.92/1.2 ≈ 3.3
    var result = snap.Resolve(new Vector3(304.4f, 0, 300f), 5f, SnapTypes.Grid, ctx);
    Assert.Equal(SnapKind.GridLine, result.Kind);
    Assert.Equal(304f, result.Position.X, 3);
    Assert.Equal(300f, result.Position.Z, 3);
}

[Fact]
public void NodeStillBeatsGridWhenBothClose()
{
    var (n, snap) = Setup();
    var ctx = SnapContext.Empty with { Grid = new GridConfig(8f) };
    // cursor past the road end so the edge can't shadow the node:
    // node (100,0,0) 1.80 m → score .45; guideline proj 1.0 m → .67;
    // grid point (104,0,0) 2.69 m → 2.24; grid lines ≥ 1.0 → node wins
    var result = snap.Resolve(new Vector3(101.5f, 0, 1.0f), 5f, SnapTypes.All, ctx);
    Assert.Equal(SnapKind.Node, result.Kind);
}

[Fact]
public void GridIgnoredWithoutConfig()
{
    var (_, snap) = Setup();
    var result = snap.Resolve(new Vector3(302.2f, 0, 297.9f), 5f, SnapTypes.Grid, SnapContext.Empty);
    Assert.Equal(SnapKind.Free, result.Kind);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SnapEngineTests" 2>&1 | tail -5`
Expected: the 3 grid tests FAIL (producer is empty → Free).

- [ ] **Step 3: Implement `AddGridCandidates`**

Replace the stub in `SnapEngine.cs`:

```csharp
private static void AddGridCandidates(Vector3 raw, float radius, GridConfig grid,
    List<SnapCandidate> outList)
{
    float cs = grid.CellSize;
    float gx = MathF.Round(raw.X / cs) * cs;
    float gz = MathF.Round(raw.Z / cs) * cs;

    var point = new Vector3(gx, raw.Y, gz);
    if (Vector3.Distance(point, raw) <= radius)
        outList.Add(new SnapCandidate(point, SnapKind.GridPoint, WeightGridPoint));

    // nearest grid line: keep the closer axis projection
    var lineX = new Vector3(gx, raw.Y, raw.Z);   // vertical line x = gx
    var lineZ = new Vector3(raw.X, raw.Y, gz);   // horizontal line z = gz
    var line = MathF.Abs(raw.X - gx) <= MathF.Abs(raw.Z - gz) ? lineX : lineZ;
    if (Vector3.Distance(line, raw) <= radius)
        outList.Add(new SnapCandidate(line, SnapKind.GridLine, WeightGridLine));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SnapEngineTests" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Snapping/SnapEngine.cs tests/Domain.Tests/Tools/SnapEngineTests.cs
git commit -m "feat(domain): grid point/line snapping"
```

---

### Task 8: Perpendicular snapping

**Files:**
- Modify: `src/Domain/Tools/Snapping/SnapEngine.cs` (fill `AddPerpendicularCandidates`)
- Test: `tests/Domain.Tests/Tools/SnapEngineTests.cs` (append)

**Interfaces:**
- Consumes: Task 6 scaffold; `ctx.Anchor`.
- Produces: `SnapKind.Perpendicular` candidates on edges where the chord from the anchor meets the edge at exactly 90°; the `SnapResult.DirectionConstraint` carries the arrival direction (unit vector anchor→foot for straight chords; consumed by curve shapes in Task 11 as an end tangent).

- [ ] **Step 1: Write the failing tests**

Append to `SnapEngineTests`:

```csharp
[Fact]
public void PerpendicularFootSnapsToExact90Degrees()
{
    var (n, snap) = Setup(); // edge (0,0,0)→(100,0,0)
    var anchor = new Vector3(40, 0, 60);
    // cursor near the edge but 3 m off the true foot (40, 0, 0)
    var ctx = new SnapContext(anchor, null);
    var result = snap.Resolve(new Vector3(43, 0, 0.5f), 5f, SnapTypes.Perpendicular, ctx);
    Assert.Equal(SnapKind.Perpendicular, result.Kind);
    Assert.True(Vector3.Distance(result.Position, new Vector3(40, 0, 0)) < 0.05f,
        $"foot at {result.Position}");
    Assert.NotNull(result.DirectionConstraint);
    // arrival direction points from anchor to foot: (0, 0, -1)
    Assert.Equal(-1f, result.DirectionConstraint!.Value.Z, 2);
}

[Fact]
public void PerpendicularNeedsAnchor()
{
    var (_, snap) = Setup();
    var result = snap.Resolve(new Vector3(43, 0, 0.5f), 5f, SnapTypes.Perpendicular, SnapContext.Empty);
    Assert.Equal(SnapKind.Free, result.Kind);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SnapEngineTests" 2>&1 | tail -5`
Expected: first test FAILS (stub emits nothing).

- [ ] **Step 3: Implement `AddPerpendicularCandidates`**

Replace the stub in `SnapEngine.cs`:

```csharp
private void AddPerpendicularCandidates(Vector3 raw, float radius, Vector3 anchor,
    List<SnapCandidate> outList)
{
    foreach (var e in network.Edges.Values)
    {
        // f(t) = (P(t) − anchor) · T(t) is zero where the chord is perpendicular
        const int coarse = 32;
        float F(float t)
        {
            var p = e.Curve.Point(t) - anchor;
            var tan = e.Curve.Tangent(t);
            return p.X * tan.X + p.Z * tan.Z;
        }
        float f0 = F(0);
        for (int i = 1; i <= coarse; i++)
        {
            float t1 = i / (float)coarse;
            float f1 = F(t1);
            if (f0 * f1 <= 0 && (f0 != 0 || f1 != 0))
            {
                float lo = (i - 1) / (float)coarse, hi = t1;
                for (int k = 0; k < 24; k++)
                {
                    float mid = (lo + hi) / 2;
                    if (F(lo) * F(mid) <= 0) hi = mid;
                    else lo = mid;
                }
                float tm = (lo + hi) / 2;
                var foot = e.Curve.Point(tm);
                var dir = foot - anchor;
                dir.Y = 0;
                if (Vector3.Distance(foot, raw) <= radius && dir.LengthSquared() > 1e-6f)
                    outList.Add(new SnapCandidate(foot, SnapKind.Perpendicular, WeightPerpendicular,
                        Edge: (e.Id, tm), Direction: Vector3.Normalize(dir)));
            }
            f0 = f1;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SnapEngineTests" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Snapping/SnapEngine.cs tests/Domain.Tests/Tools/SnapEngineTests.cs
git commit -m "feat(domain): perpendicular snap with arrival-direction constraint"
```

---

### Task 9: Parallel guidelines

**Files:**
- Modify: `src/Domain/Tools/Snapping/SnapEngine.cs` (extend `CollectGuidelines`)
- Test: `tests/Domain.Tests/Tools/SnapEngineTests.cs` (append)

**Interfaces:**
- Consumes: `RoadType.OuterHalf` (existing), `ctx.DrawingType` from Task 6.
- Produces: straight-ish edges spawn `Guideline`s offset by `existing.OuterHalf + drawing.OuterHalf` on both sides when `SnapTypes.Parallel` is enabled and `ctx.DrawingType` set. They participate in guideline projection + intersection snapping automatically.

- [ ] **Step 1: Write the failing tests**

Append to `SnapEngineTests`:

```csharp
[Fact]
public void ParallelGuideSitsCurbToCurb()
{
    var (_, snap) = Setup(); // TwoLane (width 8, OuterHalf 4) along z=0
    var ctx = SnapContext.Empty with { DrawingType = RoadCatalog.TwoLane.Id };
    // expected guide at z = ±(4 + 4) = ±8; cursor near z=7.4 above mid-edge
    var result = snap.Resolve(new Vector3(50, 0, 7.4f), 3f, SnapTypes.Parallel | SnapTypes.Guidelines, ctx);
    Assert.Equal(SnapKind.Guideline, result.Kind);
    Assert.Equal(8f, result.Position.Z, 2);
}

[Fact]
public void CurvedEdgeSpawnsNoParallelGuide()
{
    var n = Net.New();
    var bend = Bezier3.FromQuadratic(new(0, 0, 0), new(50, 0, 40), new(100, 0, 0));
    Net.Commit(n, new PlacementProposal(
        new[] { new ProposedCurve(bend, EndpointBinding.None, EndpointBinding.None) },
        RoadCatalog.TwoLane.Id));
    var snap = new SnapEngine(n);
    var ctx = SnapContext.Empty with { DrawingType = RoadCatalog.TwoLane.Id };
    var result = snap.Resolve(new Vector3(50, 0, 28f), 3f, SnapTypes.Parallel | SnapTypes.Guidelines, ctx);
    Assert.NotEqual(SnapKind.Guideline, result.Kind);
}
```

Add usings to the test file if missing: `using CityBuilder.Domain.Catalog;`, `using CityBuilder.Domain.Geometry;`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SnapEngineTests" 2>&1 | tail -5`
Expected: first test FAILS (no guide at z=8).

- [ ] **Step 3: Implement**

In `SnapEngine.CollectGuidelines`, replace the Task-9 comment with:

```csharp
if ((enabled & SnapTypes.Parallel) != 0 && ctx.DrawingType is { } drawType)
    AddParallelGuides(near, drawType, guides);
```

And add the method:

```csharp
private void AddParallelGuides(Vector3 near, RoadTypeId drawType, List<Guideline> guides)
{
    float newHalf = RoadCatalog.Get(drawType).OuterHalf;
    foreach (var e in network.Edges.Values)
    {
        var chord = e.Curve.P3 - e.Curve.P0;
        chord.Y = 0;
        float len = chord.Length();
        if (len < 10f)
            continue;
        var dir = chord / len;
        // straightness: both control points within 0.5 m of the chord line (XZ)
        if (DistToLineXZ(e.Curve.P1, e.Curve.P0, dir) > 0.5f
            || DistToLineXZ(e.Curve.P2, e.Curve.P0, dir) > 0.5f)
            continue;
        if (BezierOps.ClosestPoint(e.Curve, near).dist > GuidelineSearch)
            continue;
        float off = RoadCatalog.Get(e.Type).OuterHalf + newHalf;
        var n = Vector3.Cross(dir, Vector3.UnitY);
        n.Y = 0;
        if (n.LengthSquared() < GeoConstants.Eps)
            continue;
        n = Vector3.Normalize(n);
        guides.Add(new Guideline(e.Curve.P0 + n * off, dir, len));
        guides.Add(new Guideline(e.Curve.P0 - n * off, dir, len));
    }
}

private static float DistToLineXZ(Vector3 p, Vector3 origin, Vector3 dir)
{
    var rel = p - origin;
    return MathF.Abs(rel.X * dir.Z - rel.Z * dir.X);
}
```

`CollectGuidelines` already receives `enabled` and `ctx` (signature from Task 6).

- [ ] **Step 4: Run tests + full suite**

Run: `dotnet test 2>&1 | tail -3`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Snapping/SnapEngine.cs tests/Domain.Tests/Tools/SnapEngineTests.cs
git commit -m "feat(domain): parallel guidelines off straight edges (curb-to-curb offsets)"
```

---

### Task 10: Draft core — `DraftHandle`, `RoadDraft`, `IDraftShape`, `StraightShape`

**Files:**
- Create: `src/Domain/Tools/Draft/RoadDraft.cs` (handle + draft + shape interface)
- Create: `src/Domain/Tools/Draft/Shapes.cs` (`StraightShape` now; Tasks 11–13 add the rest here)
- Test: `tests/Domain.Tests/Tools/RoadDraftTests.cs`

**Interfaces:**
- Consumes: `SnapResult`, `Bezier3`, `PlacementProposal`/`ProposedCurve`/`EndpointBinding`.
- Produces (Tasks 11–14 consume these exact members):

```csharp
public enum HandleRole { Endpoint, Control, Direction }
public sealed record DraftHandle(HandleRole Role, SnapResult Snap) { public Vector3 Position { get; } }

public interface IDraftShape
{
    int RequiredHandles(bool tangentLocked);
    HandleRole RoleOf(int index, bool tangentLocked);
    /// <summary>Curves for the given handles (may be a prefix + hover); null if too few.</summary>
    IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent);
}

public sealed class RoadDraft(IDraftShape shape, RoadTypeId type)
{
    IReadOnlyList<DraftHandle> Handles { get; }
    Vector3? StartTangent { get; }          // set via AddHandle/MoveHandle boundTangent on index 0
    bool TangentLocked { get; }
    bool IsComplete { get; }
    RoadTypeId Type { get; set; }
    void AddHandle(SnapResult snap, Vector3? boundTangent = null);
    void MoveHandle(int index, SnapResult snap, Vector3? boundTangent = null);
    bool RemoveLastHandle();                // false when empty
    PlacementProposal? BuildProposal();     // null until curves exist
    PlacementProposal? Preview(SnapResult hover); // handles + hover appended (no mutation)
    float? MinRadius();                     // of the current curves, null if none
}
```

- Binding rules in `BuildProposal`: first curve's start ← `Handles[0].Snap` (Node → `AtNode`, Edge/Perpendicular with edge payload → `OnEdge`, else `Free`); last curve's end ← last Endpoint handle's snap; interior joints `EndpointBinding.None`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Domain.Tests/Tools/RoadDraftTests.cs`:

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class RoadDraftTests
{
    private static SnapResult Free(float x, float z) => SnapResult.Free(new Vector3(x, 0, z));

    [Fact]
    public void StraightDraftCompletesAfterTwoHandles()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        Assert.False(d.IsComplete);
        d.AddHandle(Free(0, 0));
        Assert.False(d.IsComplete);
        d.AddHandle(Free(100, 0));
        Assert.True(d.IsComplete);
        var p = d.BuildProposal();
        Assert.NotNull(p);
        var curve = Assert.Single(p!.Curves);
        Assert.Equal(new Vector3(0, 0, 0), curve.Curve.P0);
        Assert.Equal(new Vector3(100, 0, 0), curve.Curve.P3);
    }

    [Fact]
    public void MoveHandleReshapesTheProposal()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        d.AddHandle(Free(0, 0));
        d.AddHandle(Free(100, 0));
        d.MoveHandle(1, Free(100, 50));
        Assert.Equal(new Vector3(100, 0, 50), d.BuildProposal()!.Curves[0].Curve.P3);
    }

    [Fact]
    public void PreviewAppendsHoverWithoutMutating()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        d.AddHandle(Free(0, 0));
        var p = d.Preview(Free(60, 0));
        Assert.NotNull(p);
        Assert.Equal(new Vector3(60, 0, 0), p!.Curves[0].Curve.P3);
        Assert.Single(d.Handles); // unchanged
    }

    [Fact]
    public void RemoveLastHandleStepsBack()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        d.AddHandle(Free(0, 0));
        Assert.True(d.RemoveLastHandle());
        Assert.False(d.RemoveLastHandle());
        Assert.Empty(d.Handles);
    }

    [Fact]
    public void NodeSnapBecomesAtNodeBinding()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        var nodeSnap = new SnapResult(new Vector3(0, 0, 0), SnapKind.Node, new NodeId(7), null, null,
            Array.Empty<Guideline>());
        d.AddHandle(nodeSnap);
        d.AddHandle(Free(100, 0));
        var p = d.BuildProposal()!;
        var start = Assert.IsType<EndpointBinding.AtNode>(p.Curves[0].Start);
        Assert.Equal(new NodeId(7), start.Node);
        Assert.IsType<EndpointBinding.Free>(p.Curves[0].End);
    }

    [Fact]
    public void BoundTangentOnFirstHandleLocksTheDraft()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        d.AddHandle(Free(0, 0), boundTangent: new Vector3(1, 0, 0));
        Assert.True(d.TangentLocked);
        d.MoveHandle(0, Free(5, 0), boundTangent: null);
        Assert.False(d.TangentLocked); // moving off the edge releases the lock
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RoadDraftTests" 2>&1 | tail -5`
Expected: compile error — types not defined.

- [ ] **Step 3: Implement**

Create `src/Domain/Tools/Draft/RoadDraft.cs`:

```csharp
using System.Numerics;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tools;

public enum HandleRole { Endpoint, Control, Direction }

/// <summary>A draggable point of an in-progress road gesture, remembering how it
/// snapped (bindings and direction constraints travel with it).</summary>
public sealed record DraftHandle(HandleRole Role, SnapResult Snap)
{
    public Vector3 Position => Snap.Position;
}

/// <summary>Maps a draft's handles to curve geometry. Stateless.</summary>
public interface IDraftShape
{
    int RequiredHandles(bool tangentLocked);
    HandleRole RoleOf(int index, bool tangentLocked);
    /// <summary>Curves for the given handles (possibly a prefix + hover); null if too few.</summary>
    IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent);
}

/// <summary>An editable in-progress road gesture: ordered handles + a shape strategy.
/// First-class replacement for the old click-state-machine tools — handles can be
/// added, moved, and removed until the proposal is committed.</summary>
public sealed class RoadDraft(IDraftShape shape, RoadTypeId type)
{
    private readonly List<DraftHandle> _handles = new();

    public IDraftShape Shape => shape;
    public RoadTypeId Type { get; set; } = type;
    public Vector3? StartTangent { get; private set; }
    public bool TangentLocked => StartTangent is not null;
    public IReadOnlyList<DraftHandle> Handles => _handles;
    public bool IsComplete => _handles.Count >= shape.RequiredHandles(TangentLocked);

    /// <summary>Seed the lock for chained segments (continuous mode).</summary>
    public void LockStartTangent(Vector3 tangent) => StartTangent = tangent;

    public void AddHandle(SnapResult snap, Vector3? boundTangent = null)
    {
        if (_handles.Count == 0 && boundTangent is { } t)
            StartTangent = Normalized(t);
        _handles.Add(new DraftHandle(shape.RoleOf(_handles.Count, TangentLocked), snap));
    }

    public void MoveHandle(int index, SnapResult snap, Vector3? boundTangent = null)
    {
        if (index < 0 || index >= _handles.Count)
            return;
        if (index == 0)
            StartTangent = boundTangent is { } t ? Normalized(t) : null;
        _handles[index] = _handles[index] with { Snap = snap };
    }

    public bool RemoveLastHandle()
    {
        if (_handles.Count == 0)
            return false;
        _handles.RemoveAt(_handles.Count - 1);
        if (_handles.Count == 0)
            StartTangent = null;
        return true;
    }

    public PlacementProposal? BuildProposal() => Proposal(_handles);

    public PlacementProposal? Preview(SnapResult hover)
    {
        if (_handles.Count == 0)
            return null;
        if (IsComplete)
            return BuildProposal();
        var withHover = new List<DraftHandle>(_handles)
        {
            new(shape.RoleOf(_handles.Count, TangentLocked), hover),
        };
        var full = Proposal(withHover);
        if (full is not null)
            return full;
        // not enough handles for the real shape yet: straight hint from the last handle
        var from = _handles[^1].Position;
        if (Vector3.Distance(from, hover.Position) < GeoConstants.Eps)
            return null;
        return new PlacementProposal(new[]
        {
            new ProposedCurve(Bezier3.Line(from, hover.Position),
                _handles.Count == 1 ? BindingOf(_handles[0].Snap) : EndpointBinding.None,
                BindingOf(hover)),
        }, Type);
    }

    public float? MinRadius()
    {
        var curves = shape.Curves(_handles, StartTangent);
        if (curves is null || curves.Count == 0)
            return null;
        float min = float.PositiveInfinity;
        foreach (var c in curves)
            min = MathF.Min(min, BezierOps.MinRadius(c));
        return min;
    }

    private PlacementProposal? Proposal(IReadOnlyList<DraftHandle> handles)
    {
        var curves = shape.Curves(handles, StartTangent);
        if (curves is null || curves.Count == 0)
            return null;
        var endHandle = handles[^1];
        var list = new List<ProposedCurve>(curves.Count);
        for (int i = 0; i < curves.Count; i++)
            list.Add(new ProposedCurve(curves[i],
                i == 0 ? BindingOf(handles[0].Snap) : EndpointBinding.None,
                i == curves.Count - 1 ? BindingOf(endHandle.Snap) : EndpointBinding.None));
        return new PlacementProposal(list, Type);
    }

    internal static EndpointBinding BindingOf(SnapResult s) => s.Kind switch
    {
        SnapKind.Node when s.Node is { } n => new EndpointBinding.AtNode(n),
        SnapKind.Edge when s.Edge is { } e => new EndpointBinding.OnEdge(e.Edge, e.T),
        SnapKind.Perpendicular when s.Edge is { } e => new EndpointBinding.OnEdge(e.Edge, e.T),
        _ => EndpointBinding.None,
    };

    private static Vector3 Normalized(Vector3 v)
    {
        v.Y = 0;
        return v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitX;
    }
}
```

Create `src/Domain/Tools/Draft/Shapes.cs`:

```csharp
using System.Numerics;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Tools;

/// <summary>Two endpoints. Tangent lock is irrelevant to a straight line's handle
/// count; validation's SharpAngle guard rejects non-tangential exits anyway.</summary>
public sealed class StraightShape : IDraftShape
{
    public int RequiredHandles(bool tangentLocked) => 2;
    public HandleRole RoleOf(int index, bool tangentLocked) => HandleRole.Endpoint;

    public IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent)
    {
        if (handles.Count < 2)
            return null;
        var a = handles[0].Position;
        var b = handles[1].Position;
        return Vector3.Distance(a, b) < GeoConstants.Eps ? null : new[] { Bezier3.Line(a, b) };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RoadDraftTests" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Draft tests/Domain.Tests/Tools/RoadDraftTests.cs
git commit -m "feat(domain): RoadDraft editable gesture model + StraightShape"
```

---

### Task 11: `QuadCurveShape` + `CubicCurveShape` — tangent lock and arrival constraints

**Files:**
- Modify: `src/Domain/Tools/Draft/Shapes.cs` (append both shapes + shared helpers)
- Test: `tests/Domain.Tests/Tools/DraftShapeTests.cs` (new)

**Interfaces:**
- Consumes: Task 10 `IDraftShape`, `DraftHandle`.
- Produces: `QuadCurveShape` (3 handles: start/control/end; locked: 2 handles, control implied at 40 % of chord along the start tangent) and `CubicCurveShape` (4 handles: start/c1/c2/end; locked: 3 — c1 implied at chord/3 along the tangent). Both honor the *end* handle's `SnapResult.DirectionConstraint` by re-aiming the last control point along the arrival direction. Static helper `ShapeUtil.Orient(Vector3 tangent, Vector3 toward)` returns `±tangent` facing `toward` (used by Task 12 too).

- [ ] **Step 1: Write the failing tests**

Create `tests/Domain.Tests/Tools/DraftShapeTests.cs`:

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class DraftShapeTests
{
    private static SnapResult Free(float x, float z) => SnapResult.Free(new Vector3(x, 0, z));

    private static RoadDraft Draft(IDraftShape shape, Vector3? lockTangent, params SnapResult[] snaps)
    {
        var d = new RoadDraft(shape, RoadCatalog.TwoLane.Id);
        for (int i = 0; i < snaps.Length; i++)
            d.AddHandle(snaps[i], i == 0 ? lockTangent : null);
        return d;
    }

    [Fact]
    public void UnlockedQuadNeedsThreeHandles()
    {
        var d = Draft(new QuadCurveShape(), null, Free(0, 0), Free(50, 30), Free(100, 0));
        Assert.True(d.IsComplete);
        var c = d.BuildProposal()!.Curves[0].Curve;
        // quadratic through the control: curve bends toward (50,30)
        Assert.True(c.Point(0.5f).Z > 10f);
    }

    [Fact]
    public void LockedQuadNeedsTwoHandlesAndLeavesG1()
    {
        var tangent = new Vector3(1, 0, 0);
        var d = Draft(new QuadCurveShape(), tangent, Free(0, 0), Free(80, 40));
        Assert.True(d.IsComplete);
        var c = d.BuildProposal()!.Curves[0].Curve;
        // G1: start tangent parallel to the lock
        Assert.True(Vector3.Dot(c.Tangent(0), tangent) > 0.999f);
        Assert.Equal(new Vector3(80, 0, 40), c.P3);
    }

    [Fact]
    public void LockedQuadFlipsTangentTowardTheEnd()
    {
        // lock points +X but the end is behind: shape must leave along −X
        var d = Draft(new QuadCurveShape(), new Vector3(1, 0, 0), Free(0, 0), Free(-80, 40));
        var c = d.BuildProposal()!.Curves[0].Curve;
        Assert.True(Vector3.Dot(c.Tangent(0), new Vector3(-1, 0, 0)) > 0.999f);
    }

    [Fact]
    public void EndDirectionConstraintIsHonored()
    {
        var arrival = Vector3.Normalize(new Vector3(0, 0, -1));
        var endSnap = new SnapResult(new Vector3(80, 0, 40), SnapKind.Perpendicular, null, null, null,
            Array.Empty<Guideline>(), arrival);
        var d = Draft(new QuadCurveShape(), new Vector3(1, 0, 0), Free(0, 0), endSnap);
        var c = d.BuildProposal()!.Curves[0].Curve;
        Assert.True(Vector3.Dot(c.Tangent(1), arrival) > 0.99f,
            $"arrival tangent {c.Tangent(1)} vs {arrival}");
    }

    [Fact]
    public void LockedCubicNeedsThreeHandles()
    {
        var d = Draft(new CubicCurveShape(), new Vector3(1, 0, 0), Free(0, 0), Free(70, 25), Free(120, 10));
        Assert.True(d.IsComplete);
        var c = d.BuildProposal()!.Curves[0].Curve;
        Assert.True(Vector3.Dot(c.Tangent(0), new Vector3(1, 0, 0)) > 0.999f);
        Assert.Equal(new Vector3(120, 0, 10), c.P3);
    }

    [Fact]
    public void UnlockedCubicUsesFourHandles()
    {
        var d = Draft(new CubicCurveShape(), null, Free(0, 0), Free(30, 30), Free(70, 30), Free(100, 0));
        Assert.True(d.IsComplete);
        var c = d.BuildProposal()!.Curves[0].Curve;
        Assert.Equal(new Vector3(30, 0, 30), c.P1);
        Assert.Equal(new Vector3(70, 0, 30), c.P2);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DraftShapeTests" 2>&1 | tail -5`
Expected: compile error — shapes not defined.

- [ ] **Step 3: Implement**

Append to `src/Domain/Tools/Draft/Shapes.cs`:

```csharp
internal static class ShapeUtil
{
    /// <summary>±tangent, whichever faces <paramref name="toward"/> (XZ).</summary>
    public static Vector3 Orient(Vector3 tangent, Vector3 toward)
    {
        float d = tangent.X * toward.X + tangent.Z * toward.Z;
        return d >= 0 ? tangent : -tangent;
    }

    /// <summary>Re-aim the last control point along the end handle's arrival
    /// direction, preserving its distance to the endpoint (G1 at the far end).</summary>
    public static Bezier3 ApplyArrival(Bezier3 c, DraftHandle end)
    {
        if (end.Snap.DirectionConstraint is not { } dir)
            return c;
        float reach = Vector3.Distance(c.P3, c.P2);
        if (reach < GeoConstants.Eps)
            reach = Vector3.Distance(c.P0, c.P3) / 3f;
        var arrive = Vector3.Normalize(new Vector3(dir.X, 0, dir.Z));
        return new Bezier3(c.P0, c.P1, c.P3 - arrive * reach, c.P3);
    }
}

/// <summary>Start, control, end (3 clicks). Tangent-locked: start, end (control
/// implied on the tangent ray at 40 % of the chord, like the old continuous tool).</summary>
public sealed class QuadCurveShape : IDraftShape
{
    public int RequiredHandles(bool tangentLocked) => tangentLocked ? 2 : 3;

    public HandleRole RoleOf(int index, bool tangentLocked)
        => !tangentLocked && index == 1 ? HandleRole.Control : HandleRole.Endpoint;

    public IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent)
    {
        if (startTangent is { } t0)
        {
            if (handles.Count < 2)
                return null;
            var a = handles[0].Position;
            var b = handles[^1].Position;
            float chord = Vector3.Distance(a, b);
            if (chord < GeoConstants.Eps)
                return null;
            var dir = ShapeUtil.Orient(t0, b - a);
            var curve = Bezier3.FromQuadratic(a, a + dir * (0.4f * chord), b);
            return new[] { ShapeUtil.ApplyArrival(curve, handles[^1]) };
        }
        if (handles.Count < 3)
            return null;
        var q = Bezier3.FromQuadratic(handles[0].Position, handles[1].Position, handles[2].Position);
        return new[] { ShapeUtil.ApplyArrival(q, handles[2]) };
    }
}

/// <summary>Start, two controls, end (4 clicks). Tangent-locked: start, control,
/// end — the first control is implied at a third of the chord along the tangent.</summary>
public sealed class CubicCurveShape : IDraftShape
{
    public int RequiredHandles(bool tangentLocked) => tangentLocked ? 3 : 4;

    public HandleRole RoleOf(int index, bool tangentLocked)
    {
        int last = RequiredHandles(tangentLocked) - 1;
        return index == 0 || index >= last ? HandleRole.Endpoint : HandleRole.Control;
    }

    public IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent)
    {
        if (startTangent is { } t0)
        {
            if (handles.Count < 3)
                return null;
            var a = handles[0].Position;
            var c2 = handles[1].Position;
            var b = handles[^1].Position;
            float chord = Vector3.Distance(a, b);
            if (chord < GeoConstants.Eps)
                return null;
            var dir = ShapeUtil.Orient(t0, b - a);
            var curve = new Bezier3(a, a + dir * (chord / 3f), c2, b);
            return new[] { ShapeUtil.ApplyArrival(curve, handles[^1]) };
        }
        if (handles.Count < 4)
            return null;
        var full = new Bezier3(handles[0].Position, handles[1].Position,
            handles[2].Position, handles[3].Position);
        return new[] { ShapeUtil.ApplyArrival(full, handles[3]) };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DraftShapeTests" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Draft/Shapes.cs tests/Domain.Tests/Tools/DraftShapeTests.cs
git commit -m "feat(domain): quad/cubic curve shapes with tangent lock + arrival constraints"
```

---

### Task 12: `ArcShape`

**Files:**
- Modify: `src/Domain/Tools/Draft/Shapes.cs` (append)
- Test: `tests/Domain.Tests/Tools/DraftShapeTests.cs` (append)

**Interfaces:**
- Consumes: Task 2 `BezierOps.ArcFromTangent`, Task 10/11 scaffold.
- Produces: `ArcShape` — unlocked handles: start (Endpoint), direction (Direction), end (Endpoint); locked: start, end. `ArcShape.LastRadius : float?` exposes the most recently built arc's radius for the readout (Task 14 reads it via `RoadDraft.MinRadius()` anyway — LastRadius is not required; do NOT add it, use MinRadius).

- [ ] **Step 1: Write the failing tests**

Append to `DraftShapeTests`:

```csharp
[Fact]
public void UnlockedArcUsesDirectionHandle()
{
    // start at origin, direction handle straight +X, end at (50,0,50) → quarter arc r=50
    var d = Draft(new ArcShape(), null, Free(0, 0), Free(20, 0), Free(50, 50));
    Assert.True(d.IsComplete);
    var curves = d.BuildProposal()!.Curves;
    var c = Assert.Single(curves).Curve;
    Assert.InRange(BezierOps.MinRadius(c), 49f, 51f);
    Assert.True(Vector3.Dot(c.Tangent(0), new Vector3(1, 0, 0)) > 0.999f);
}

[Fact]
public void LockedArcNeedsOnlyStartAndEnd()
{
    var d = Draft(new ArcShape(), new Vector3(1, 0, 0), Free(0, 0), Free(50, 50));
    Assert.True(d.IsComplete);
    Assert.InRange(d.MinRadius()!.Value, 49f, 51f);
}

[Fact]
public void ArcBeyondSweepCapHasNoProposal()
{
    // end nearly behind the start: > 175° sweep
    var d = Draft(new ArcShape(), new Vector3(1, 0, 0), Free(0, 0), Free(-80, 1));
    Assert.True(d.IsComplete);      // handles are all there…
    Assert.Null(d.BuildProposal()); // …but the geometry refuses
}

[Fact]
public void WideArcEmitsTwoG1Curves()
{
    var d = Draft(new ArcShape(), new Vector3(1, 0, 0), Free(0, 0), Free(-35, 85));
    var curves = d.BuildProposal()!.Curves;
    Assert.Equal(2, curves.Count);
    Assert.True(Vector3.Dot(curves[0].Curve.Tangent(1), curves[1].Curve.Tangent(0)) > 0.999f);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DraftShapeTests" 2>&1 | tail -5`
Expected: compile error — `ArcShape` not defined.

- [ ] **Step 3: Implement**

Append to `src/Domain/Tools/Draft/Shapes.cs`:

```csharp
/// <summary>Constant-radius circular arc. Unlocked: start, a direction handle the
/// arc leaves toward, end. Tangent-locked: start, end (the lock is the direction).
/// The most predictable curve primitive — radius shown live in the readout.</summary>
public sealed class ArcShape : IDraftShape
{
    public int RequiredHandles(bool tangentLocked) => tangentLocked ? 2 : 3;

    public HandleRole RoleOf(int index, bool tangentLocked)
        => !tangentLocked && index == 1 ? HandleRole.Direction : HandleRole.Endpoint;

    public IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent)
    {
        int needed = RequiredHandles(startTangent is not null);
        if (handles.Count < needed)
            return null;
        var start = handles[0].Position;
        var end = handles[^1].Position;
        Vector3 tangent;
        if (startTangent is { } t0)
        {
            tangent = ShapeUtil.Orient(t0, end - start);
        }
        else
        {
            tangent = handles[1].Position - start;
            tangent.Y = 0;
            if (tangent.LengthSquared() < GeoConstants.Eps * GeoConstants.Eps)
                return null;
            tangent = Vector3.Normalize(tangent);
        }
        return BezierOps.ArcFromTangent(start, tangent, end)?.Curves;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DraftShapeTests" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Draft/Shapes.cs tests/Domain.Tests/Tools/DraftShapeTests.cs
git commit -m "feat(domain): constant-radius ArcShape (2 clicks when tangent-locked)"
```

---

### Task 13: `GridStampShape`

**Files:**
- Modify: `src/Domain/Tools/Draft/Shapes.cs` (append)
- Test: `tests/Domain.Tests/Tools/DraftShapeTests.cs` (append)

**Interfaces:**
- Consumes: Task 10 scaffold.
- Produces: `GridStampShape` with `public const float CellSize = 48f` — port of the old `GridTool.BuildProposal` geometry: 3 endpoint handles (corner, axis extent, perpendicular extent), lines both ways, whole 48 m cells only. Returns null when either axis has < 1 whole cell.

- [ ] **Step 1: Write the failing tests**

Append to `DraftShapeTests`:

```csharp
[Fact]
public void GridStampBuildsWholeCellsBothWays()
{
    // 2 × 2 cells of 48 m: (2+1) + (2+1) = 6 lines
    var d = Draft(new GridStampShape(), null, Free(0, 0), Free(96, 0), Free(96, 96));
    Assert.True(d.IsComplete);
    Assert.Equal(6, d.BuildProposal()!.Curves.Count);
}

[Fact]
public void GridStampWithLessThanOneCellHasNoProposal()
{
    var d = Draft(new GridStampShape(), null, Free(0, 0), Free(40, 0), Free(40, 40));
    Assert.True(d.IsComplete);
    Assert.Null(d.BuildProposal());
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DraftShapeTests" 2>&1 | tail -5`
Expected: compile error — `GridStampShape` not defined.

- [ ] **Step 3: Implement**

Append to `src/Domain/Tools/Draft/Shapes.cs` (logic ported verbatim from the old `GridTool`):

```csharp
/// <summary>Three endpoint handles: corner, extent along the first axis,
/// perpendicular extent. Stamps straight roads along both axes at 48 m spacing;
/// only whole cells are kept.</summary>
public sealed class GridStampShape : IDraftShape
{
    public const float CellSize = 48f;

    public int RequiredHandles(bool tangentLocked) => 3;
    public HandleRole RoleOf(int index, bool tangentLocked) => HandleRole.Endpoint;

    public IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent)
    {
        if (handles.Count < 3)
            return null;

        var origin = handles[0].Position;
        var axis1 = handles[1].Position - origin;
        axis1.Y = 0;
        if (axis1.Length() < GeoConstants.Eps)
            return null;
        var dir1 = Vector3.Normalize(axis1);
        int n1 = (int)MathF.Floor(axis1.Length() / CellSize);

        var raw2 = handles[2].Position - origin;
        raw2.Y = 0;
        var perp = raw2 - dir1 * Vector3.Dot(raw2, dir1);
        if (perp.Length() < GeoConstants.Eps)
            return null;
        var dir2 = Vector3.Normalize(perp);
        int n2 = (int)MathF.Floor(perp.Length() / CellSize);

        if (n1 < 1 || n2 < 1)
            return null;

        var curves = new List<Bezier3>();
        for (int i = 0; i <= n1; i++)
        {
            var a = origin + dir1 * (i * CellSize);
            curves.Add(Bezier3.Line(a, a + dir2 * (n2 * CellSize)));
        }
        for (int j = 0; j <= n2; j++)
        {
            var a = origin + dir2 * (j * CellSize);
            curves.Add(Bezier3.Line(a, a + dir1 * (n1 * CellSize)));
        }
        return curves;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DraftShapeTests" 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Draft/Shapes.cs tests/Domain.Tests/Tools/DraftShapeTests.cs
git commit -m "feat(domain): GridStampShape (draggable 3-handle grid stamp)"
```

---

### Task 14: `DraftSession` — the tool state machine (deletes the old tools)

**Files:**
- Create: `src/Domain/Tools/Draft/DraftSession.cs`
- Delete: `src/Domain/Tools/PlacementTools.cs`, `tests/Domain.Tests/Tools/PlacementToolTests.cs` (superseded; their behavioral coverage lives in Tasks 10–13 + this task's tests)
- Test: `tests/Domain.Tests/Tools/DraftSessionTests.cs` (new)

**Interfaces:**
- Consumes: everything above — `RoadNetwork` (Validate/Commit), `SnapEngine`, `RoadDraft`, shapes.
- Produces (Task 15's ToolController adapter consumes these exact members):

```csharp
public enum DraftMode { Straight, QuadCurve, CubicCurve, Arc, Chain, GridStamp }
public enum SessionState { Idle, Placing, Adjustable }

public sealed class DraftSession(RoadNetwork network, SnapEngine snap)
{
    DraftMode Mode { get; }                 // set via SetMode(DraftMode) — resets state
    SessionState State { get; }
    RoadTypeId RoadType { get; set; }       // applies to the current draft too
    SnapTypes EnabledSnaps { get; set; }    // default All & ~Grid
    GridConfig Grid { get; set; }           // default GridConfig.Default
    bool AdjustMode { get; set; }           // complete drafts wait for Confirm
    RoadDraft? Draft { get; }
    ValidatedPlacement? Ghost { get; }      // last preview/proposal validation
    SnapResult LastSnap { get; }
    int DraggingHandle { get; }             // -1 when not dragging
    (float LengthM, float AngleDeg, float? RadiusM)? Readout { get; }

    event Action<NetworkDelta>?  — none; commit events flow via network.Changed
    event Action<string>? Flashed;          // user-facing rejection messages

    void SetMode(DraftMode mode);
    void PointerMoved(Vector3 raw, float radius);   // hover OR drag-move
    void Click(Vector3 raw, float radius);          // add handle / complete
    bool TryBeginHandleDrag(Vector3 raw, float pickRadius); // true = drag started
    void EndHandleDrag();
    void StepBack();                                 // remove last handle
    void Cancel();                                   // discard draft
    void Confirm();                                  // commit an Adjustable draft
}
```

- Behavior contract: instant commit on completion when valid and `!AdjustMode`; invalid completion → `Adjustable` (handles fixable) with a `Flashed` message; `Chain` mode starts the next draft at the committed end with the end tangent locked; `Confirm` on a still-invalid draft flashes and stays; snap context passes `Anchor` = last endpoint position, `ReferenceTangent` = draft start tangent (fixes the world-X angle-snap bug), `Grid`, and `DrawingType`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Domain.Tests/Tools/DraftSessionTests.cs`:

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class DraftSessionTests
{
    private static (RoadNetwork n, DraftSession s) Setup()
    {
        var n = Net.New();
        return (n, new DraftSession(n, new SnapEngine(n)));
    }

    private static void ClickAt(DraftSession s, float x, float z) => s.Click(new Vector3(x, 0, z), 5f);

    [Fact]
    public void StraightRoadCommitsInstantlyOnSecondClick()
    {
        var (n, s) = Setup();
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        Assert.Equal(SessionState.Placing, s.State);
        ClickAt(s, 100, 0);
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Single(n.Edges);
    }

    [Fact]
    public void AdjustModeHoldsTheDraftUntilConfirm()
    {
        var (n, s) = Setup();
        s.AdjustMode = true;
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        ClickAt(s, 100, 0);
        Assert.Equal(SessionState.Adjustable, s.State);
        Assert.Empty(n.Edges);
        s.Confirm();
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Single(n.Edges);
    }

    [Fact]
    public void InvalidCompletionBecomesAdjustableAndFixable()
    {
        var (n, s) = Setup();
        s.SetMode(DraftMode.Straight);
        string? flashed = null;
        s.Flashed += m => flashed = m;
        ClickAt(s, 0, 0);
        ClickAt(s, 5, 0); // 5 m TwoLane: TooShort
        Assert.Equal(SessionState.Adjustable, s.State);
        Assert.NotNull(flashed);
        Assert.Empty(n.Edges);
        // drag the end handle out to a valid length, then confirm
        Assert.True(s.TryBeginHandleDrag(new Vector3(5, 0, 0), 3f));
        s.PointerMoved(new Vector3(100, 0, 0), 5f);
        s.EndHandleDrag();
        s.Confirm();
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Single(n.Edges);
    }

    [Fact]
    public void ChainModeLocksNextSegmentToEndTangent()
    {
        var (n, s) = Setup();
        s.SetMode(DraftMode.Chain);
        ClickAt(s, 0, 0);
        ClickAt(s, 50, 30);   // control
        ClickAt(s, 100, 30);  // first segment commits
        Assert.Single(n.Edges);
        Assert.Equal(SessionState.Placing, s.State); // chain continues
        Assert.True(s.Draft!.TangentLocked);
        ClickAt(s, 180, 30);  // second segment: 2 clicks thanks to the lock
        Assert.Equal(2, n.Edges.Count);
        // G1 across the chain joint
        var joint = n.Nodes.Values.Single(nd => nd.EdgeSet.Count == 2);
        var tans = joint.EdgeSet.Select(id =>
        {
            var e = n.Edges[id];
            return e.StartNode == joint.Id ? e.Curve.Tangent(0) : -e.Curve.Tangent(1);
        }).ToArray();
        Assert.True(Vector3.Dot(tans[0], tans[1]) < -0.99f, "chain joint must be straight-through");
    }

    [Fact]
    public void StepBackAndCancelUnwindTheDraft()
    {
        var (_, s) = Setup();
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        s.StepBack();
        Assert.Equal(SessionState.Idle, s.State);
        ClickAt(s, 0, 0);
        s.Cancel();
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Null(s.Draft);
    }

    [Fact]
    public void HoverProducesGhostAndReadoutWithRadius()
    {
        var (_, s) = Setup();
        s.SetMode(DraftMode.Arc);
        ClickAt(s, 0, 0);
        ClickAt(s, 20, 0); // direction handle
        s.PointerMoved(new Vector3(50, 0, 50), 5f);
        Assert.NotNull(s.Ghost);
        Assert.NotNull(s.Readout);
        Assert.NotNull(s.Readout!.Value.RadiusM);
        Assert.InRange(s.Readout.Value.RadiusM!.Value, 45f, 55f);
    }

    [Fact]
    public void StartingOnAnExistingEdgeLocksTheTangent()
    {
        var (n, s) = Setup();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        s.SetMode(DraftMode.QuadCurve);
        s.Click(new Vector3(50, 0, 1f), 5f); // snaps onto the edge
        Assert.True(s.Draft!.TangentLocked);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DraftSessionTests" 2>&1 | tail -5`
Expected: compile error — `DraftSession` not defined.

- [ ] **Step 3: Implement**

Create `src/Domain/Tools/Draft/DraftSession.cs`:

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tools;

public enum DraftMode { Straight, QuadCurve, CubicCurve, Arc, Chain, GridStamp }

public enum SessionState { Idle, Placing, Adjustable }

/// <summary>Domain-side drawing-tool state machine. Owns the current draft, resolves
/// snapping with full context (anchor, reference tangent, grid), validates and commits
/// against the network. The Godot layer only forwards input and renders this state.</summary>
public sealed class DraftSession(RoadNetwork network, SnapEngine snap)
{
    public DraftMode Mode { get; private set; } = DraftMode.Straight;
    public SessionState State { get; private set; } = SessionState.Idle;
    public RoadTypeId RoadType
    {
        get => _roadType;
        set { _roadType = value; if (Draft is { } d) d.Type = value; Revalidate(); }
    }
    private RoadTypeId _roadType = RoadCatalog.TwoLane.Id;

    public SnapTypes EnabledSnaps { get; set; } = SnapTypes.All & ~SnapTypes.Grid;
    public GridConfig Grid { get; set; } = GridConfig.Default;
    public bool AdjustMode { get; set; }

    public RoadDraft? Draft { get; private set; }
    public ValidatedPlacement? Ghost { get; private set; }
    public SnapResult LastSnap { get; private set; } = SnapResult.Free(default);
    public int DraggingHandle { get; private set; } = -1;
    public (float LengthM, float AngleDeg, float? RadiusM)? Readout { get; private set; }

    public event Action<string>? Flashed;

    public void SetMode(DraftMode mode)
    {
        Mode = mode;
        Cancel();
    }

    public void Cancel()
    {
        Draft = null;
        Ghost = null;
        Readout = null;
        DraggingHandle = -1;
        _chainTangent = null;
        State = SessionState.Idle;
    }

    public void StepBack()
    {
        if (Draft is not { } d)
            return;
        if (State == SessionState.Adjustable)
        {
            State = SessionState.Placing; // back to editing clicks
            return;
        }
        if (!d.RemoveLastHandle() || d.Handles.Count == 0)
            Cancel();
        else
            Revalidate();
    }

    public void PointerMoved(Vector3 raw, float radius)
    {
        var s = Resolve(raw, radius, forHandleIndex: DraggingHandle);
        LastSnap = s;
        if (Draft is not { } d)
            return;
        if (DraggingHandle >= 0)
        {
            d.MoveHandle(DraggingHandle, s, DraggingHandle == 0 ? BoundTangent(s) : null);
            Revalidate();
            return;
        }
        if (State != SessionState.Placing)
            return;
        var proposal = d.Preview(s);
        Ghost = proposal is null ? null : network.Validate(proposal);
        UpdateReadout(d, s);
    }

    public void Click(Vector3 raw, float radius)
    {
        if (State == SessionState.Adjustable)
            return; // handles are dragged, commit is Confirm()
        var s = Resolve(raw, radius, forHandleIndex: -1);
        LastSnap = s;
        var d = Draft;
        if (d is null)
        {
            d = Draft = new RoadDraft(ShapeOf(Mode), RoadType);
            if (_chainTangent is { } inherited)
            {
                d.LockStartTangent(inherited);
                _chainTangent = null;
            }
            State = SessionState.Placing;
        }
        d.AddHandle(s, d.Handles.Count == 0 ? BoundTangent(s) : null);
        if (!d.IsComplete)
        {
            Revalidate();
            return;
        }
        CompleteDraft(d);
    }

    public void Confirm()
    {
        if (State != SessionState.Adjustable || Draft is not { } d)
            return;
        TryCommit(d);
    }

    public bool TryBeginHandleDrag(Vector3 raw, float pickRadius)
    {
        if (Draft is not { } d)
            return false;
        int best = -1;
        float bestD = pickRadius;
        for (int i = 0; i < d.Handles.Count; i++)
        {
            float dist = Vector3.Distance(d.Handles[i].Position, raw);
            if (dist <= bestD)
            {
                bestD = dist;
                best = i;
            }
        }
        if (best < 0)
            return false;
        DraggingHandle = best;
        return true;
    }

    public void EndHandleDrag() => DraggingHandle = -1;

    // ------------------------------------------------------------------ internal

    private Vector3? _chainTangent;

    private void CompleteDraft(RoadDraft d)
    {
        var proposal = d.BuildProposal();
        var validated = proposal is null ? null : network.Validate(proposal);
        Ghost = validated;
        if (validated is null || !validated.IsValid || AdjustMode)
        {
            if (validated is { IsValid: false })
                Flashed?.Invoke("invalid placement: " + string.Join(", ", validated.Errors));
            State = SessionState.Adjustable;
            return;
        }
        TryCommit(d);
    }

    private void TryCommit(RoadDraft d)
    {
        var proposal = d.BuildProposal();
        var validated = proposal is null ? null : network.Validate(proposal);
        Ghost = validated;
        if (validated is null || !validated.IsValid)
        {
            Flashed?.Invoke(validated is null
                ? "shape is not buildable here"
                : "invalid placement: " + string.Join(", ", validated.Errors));
            State = SessionState.Adjustable;
            return;
        }
        var result = network.Commit(validated);
        if (!result.Success)
        {
            Flashed?.Invoke(result.FailureReason ?? "could not build");
            State = SessionState.Adjustable;
            return;
        }
        var endSnap = d.Handles[^1].Snap;
        var lastCurve = validated.Proposal.Curves[^1].Curve;
        Draft = null;
        Ghost = null;
        Readout = null;
        State = SessionState.Idle;
        if (Mode == DraftMode.Chain)
        {
            // chain continues: next segment starts at the committed end, G1-locked
            _chainTangent = lastCurve.Tangent(1);
            var next = new RoadDraft(ShapeOf(Mode), RoadType);
            next.LockStartTangent(_chainTangent.Value);
            _chainTangent = null;
            next.AddHandle(endSnap);
            Draft = next;
            State = SessionState.Placing;
        }
    }

    private void Revalidate()
    {
        if (Draft is not { } d)
            return;
        var proposal = d.BuildProposal();
        Ghost = proposal is null ? null : network.Validate(proposal);
        if (Ghost is not null && d.Handles.Count > 0)
            UpdateReadout(d, d.Handles[^1].Snap);
    }

    private void UpdateReadout(RoadDraft d, SnapResult tip)
    {
        if (d.Handles.Count == 0)
        {
            Readout = null;
            return;
        }
        var from = d.Handles[0].Position;
        var v = tip.Position - from;
        float len = v.Length();
        float angle = MathF.Atan2(v.Z, v.X) * 180f / MathF.PI;
        float? radius = null;
        var curves = Ghost?.Proposal.Curves;
        if (curves is { Count: > 0 })
        {
            float min = float.PositiveInfinity;
            foreach (var pc in curves)
                min = MathF.Min(min, BezierOps.MinRadius(pc.Curve));
            if (!float.IsPositiveInfinity(min))
                radius = min;
        }
        Readout = (len, angle, radius);
    }

    private SnapResult Resolve(Vector3 raw, float radius, int forHandleIndex)
    {
        Vector3? anchor = null;
        Vector3? reference = null;
        if (Draft is { } d && d.Handles.Count > 0 && forHandleIndex != 0)
        {
            anchor = d.Handles[^1].Position;
            reference = d.StartTangent;
        }
        var ctx = new SnapContext(anchor, reference,
            (EnabledSnaps & SnapTypes.Grid) != 0 ? Grid : null, RoadType);
        return snap.Resolve(raw, radius, EnabledSnaps, ctx);
    }

    private Vector3? BoundTangent(SnapResult s)
    {
        switch (s.Kind)
        {
            case SnapKind.Edge or SnapKind.Perpendicular when s.Edge is { } e
                && network.Edges.TryGetValue(e.Edge, out var edge):
                return edge.Curve.Tangent(e.T);
            case SnapKind.Node when s.Node is { } id
                && network.Nodes.TryGetValue(id, out var node) && node.EdgeSet.Count == 1:
            {
                var e = network.Edges[node.EdgeSet.First()];
                // continuation direction: away from the existing edge
                return e.StartNode == id ? -e.Curve.Tangent(0) : e.Curve.Tangent(1);
            }
            default:
                return null;
        }
    }

    private static IDraftShape ShapeOf(DraftMode mode) => mode switch
    {
        DraftMode.Straight => new StraightShape(),
        DraftMode.QuadCurve or DraftMode.Chain => new QuadCurveShape(),
        DraftMode.CubicCurve => new CubicCurveShape(),
        DraftMode.Arc => new ArcShape(),
        DraftMode.GridStamp => new GridStampShape(),
        _ => new StraightShape(),
    };
}
```

Delete the superseded files:

```bash
git rm src/Domain/Tools/PlacementTools.cs tests/Domain.Tests/Tools/PlacementToolTests.cs
rm -f src/Domain/Tools/PlacementTools.cs.uid tests/Domain.Tests/Tools/PlacementToolTests.cs.uid
```

**Note:** the Game project still references `IPlacementTool` (ToolController) — `dotnet test` (domain + tests only) must pass after this step, but `dotnet build citybuilder.sln` will fail until Task 15. That is the ONE allowed intermediate red on the solution build; Tasks 14 and 15 must land in immediate succession (same session).

- [ ] **Step 4: Run domain tests**

Run: `dotnet test 2>&1 | tail -3`
Expected: PASS (all domain tests, including the new session tests).

- [ ] **Step 5: Commit**

```bash
git add -A src/Domain/Tools tests/Domain.Tests/Tools
git commit -m "feat(domain): DraftSession state machine; retire IPlacementTool click tools"
```

---

### Task 15: Game cutover — ToolController adapter, GhostView handles, Main wiring

**Files:**
- Modify: `src/Game/ToolController.cs` (full rewrite of the road-tool paths; bulldoze/inspect/spawn unchanged)
- Modify: `src/Game/GhostView.cs` (handle rendering)
- Modify: `src/Game/Main.cs:38` (`SnapService` → `SnapEngine` + `DraftSession`), `Main.cs:76` (`Bind` call)
- Delete: `src/Domain/Tools/SnapService.cs` (class + file — the shared records move), `tests/Domain.Tests/Tools/SnapServiceTests.cs` (ported to `SnapEngineTests` in Task 6)
- Create: `src/Domain/Tools/Snapping/SnapTypes.cs` (new home for the shared records currently in `SnapService.cs`)

**Interfaces:**
- Consumes: Task 14 `DraftSession` exactly as specified there.
- Produces: `ToolController` keeps `SetMode(ToolMode)`, `SetRoadType`, `SetSnapType`, `HandleHoverAt(Vector3)`, `HandleClickAt(Vector3)` (smoke + UI test compatibility), and adds `HandleMouseDownAt(Vector3)`, `HandleMouseUpAt(Vector3)`, `ConfirmDraft()`, plus `ToolMode.Arc`. `GhostView.Show(ValidatedPlacement?, SnapResult, IReadOnlyList<System.Numerics.Vector3>? handles = null, int hotHandle = -1)`. Task 16 consumes `ToolController.Session` (expose the `DraftSession` as a read-only property for Toolbar/GridOverlay).

- [ ] **Step 1: Move the shared snap records**

Create `src/Domain/Tools/Snapping/SnapTypes.cs` containing (verbatim from Task 6's versions, cut-paste out of `SnapService.cs`): `SnapKind`, `SnapTypes`, `Guideline`, `GridConfig`, `SnapContext`, `SnapResult`. Then `git rm src/Domain/Tools/SnapService.cs tests/Domain.Tests/Tools/SnapServiceTests.cs` (and their `.uid` files via plain `rm`). Namespace stays `CityBuilder.Domain.Tools` — no `using` churn anywhere.

Run: `dotnet test 2>&1 | tail -3`
Expected: PASS (domain never referenced the `SnapService` class after Task 14; only the game did).

- [ ] **Step 2: Rewrite `ToolController` road paths**

Replace the road-tool parts of `src/Game/ToolController.cs`. Complete new file (bulldoze/inspect/spawn/PickNode/NormalizeDeg bodies are verbatim from the current file — marked below):

```csharp
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

public enum ToolMode { Straight, SimpleCurve, ComplexCurve, Arc, Continuous, Grid, Bulldoze, Inspect, SpawnVehicle }

/// <summary>Thin adapter: raycasts input into the domain DraftSession and renders its
/// ghost state. All world mutations flow through the session (roads) or
/// RoadNetwork.RemoveEdge (bulldoze).</summary>
public partial class ToolController : Node
{
    private RoadNetwork _network = null!;
    private DraftSession _session = null!;
    private CameraRig _camera = null!;
    private GhostView _ghost = null!;
    private RoadNetworkView _view = null!;

    private ToolMode _mode = ToolMode.Straight;
    private EdgeId? _bulldozeTarget;
    private NodeId? _selectedNode;
    private CityBuilder.Domain.Traffic.TrafficSim? _traffic;
    private (EdgeId Edge, bool Forward)? _spawnOrigin;

    public event Action<string>? StatusFlashed;
    public event Action<string>? ReadoutChanged;
    public event Action<NodeId?>? NodeSelected;

    public ToolMode Mode => _mode;
    public DraftSession Session => _session;

    public void BindTraffic(CityBuilder.Domain.Traffic.TrafficSim traffic) => _traffic = traffic;

    public void Bind(RoadNetwork network, DraftSession session, CameraRig camera,
        GhostView ghost, RoadNetworkView view)
    {
        _network = network;
        _session = session;
        _camera = camera;
        _ghost = ghost;
        _view = view;
        _session.Flashed += m => StatusFlashed?.Invoke(m);
    }

    public void SetMode(ToolMode mode)
    {
        _mode = mode;
        if (DraftModeOf(mode) is { } dm)
            _session.SetMode(dm);
        else
            _session.Cancel();
        _ghost.Clear();
        _view.HighlightEdge(null);
        _bulldozeTarget = null;
        _spawnOrigin = null;
        if (mode != ToolMode.Inspect)
            SelectNode(null);
    }

    private static DraftMode? DraftModeOf(ToolMode m) => m switch
    {
        ToolMode.Straight => DraftMode.Straight,
        ToolMode.SimpleCurve => DraftMode.QuadCurve,
        ToolMode.ComplexCurve => DraftMode.CubicCurve,
        ToolMode.Arc => DraftMode.Arc,
        ToolMode.Continuous => DraftMode.Chain,
        ToolMode.Grid => DraftMode.GridStamp,
        _ => null,
    };

    private bool IsRoadMode => DraftModeOf(_mode) is not null;

    private void SelectNode(NodeId? id) { /* verbatim from current file */ }

    public void SetRoadType(RoadTypeId type) => _session.RoadType = type;

    public void SetSnapType(SnapTypes flag, bool enabled)
        => _session.EnabledSnaps = enabled ? _session.EnabledSnaps | flag : _session.EnabledSnaps & ~flag;

    // ------------------------------------------------------------------- input

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion:
                if (_camera.MouseGroundPoint() is { } hover)
                    HandleHoverAt(hover.ToNumerics());
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                if (_camera.MouseGroundPoint() is { } down)
                    HandleMouseDownAt(down.ToNumerics());
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                if (IsRoadMode)
                    HandleMouseUpAt();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                StepBack();
                break;
            case InputEventKey { Keycode: Key.Escape, Pressed: true }:
                CancelGesture();
                break;
            case InputEventKey { Keycode: Key.Enter, Pressed: true }:
                ConfirmDraft();
                break;
        }
    }

    // ---------------------------------------------------- world-space handlers

    /// <summary>Mouse-down: near a draft handle starts a drag, otherwise a click.</summary>
    public void HandleMouseDownAt(System.Numerics.Vector3 world)
    {
        if (IsRoadMode
            && _session.State != SessionState.Idle
            && _session.TryBeginHandleDrag(world, MathF.Max(3f, _camera.SnapRadius() * 0.6f)))
        {
            RenderGhost();
            return;
        }
        HandleClickAt(world);
    }

    public void HandleMouseUpAt()
    {
        _session.EndHandleDrag();
        RenderGhost();
    }

    public void ConfirmDraft()
    {
        if (!IsRoadMode)
            return;
        _session.Confirm();
        RenderGhost();
    }

    public void HandleHoverAt(System.Numerics.Vector3 world)
    {
        if (_mode == ToolMode.Inspect) { /* verbatim from current file */ return; }
        if (_mode == ToolMode.SpawnVehicle) { /* verbatim from current file */ return; }
        if (_mode == ToolMode.Bulldoze) { /* verbatim from current file */ return; }

        _session.PointerMoved(world, _camera.SnapRadius());
        RenderGhost();
    }

    public void HandleClickAt(System.Numerics.Vector3 world)
    {
        if (_mode == ToolMode.Inspect) { SelectNode(PickNode(world)); return; }
        if (_mode == ToolMode.SpawnVehicle) { HandleSpawnClick(world); return; }
        if (_mode == ToolMode.Bulldoze) { /* verbatim from current file */ return; }

        _session.Click(world, _camera.SnapRadius());
        RenderGhost();
    }

    public void StepBack()
    {
        if (!IsRoadMode)
            return;
        _session.StepBack();
        RenderGhost();
    }

    public void CancelGesture()
    {
        _session.Cancel();
        _ghost.Clear();
        ReadoutChanged?.Invoke("");
    }

    private void RenderGhost()
    {
        var handles = _session.Draft?.Handles.Select(h => h.Position).ToArray();
        _ghost.Show(_session.Ghost, _session.LastSnap, handles, _session.DraggingHandle);
        ReadoutChanged?.Invoke(_session.Readout is { } r
            ? r.RadiusM is { } rad && rad < 10000f
                ? $"{r.LengthM:0.#} m   {NormalizeDeg(r.AngleDeg):0.#}°   R {rad:0} m"
                : $"{r.LengthM:0.#} m   {NormalizeDeg(r.AngleDeg):0.#}°"
            : "");
    }

    private void HandleSpawnClick(System.Numerics.Vector3 world) { /* verbatim */ }
    private NodeId? PickNode(System.Numerics.Vector3 world) { /* verbatim */ }
    private static float NormalizeDeg(float deg) { /* verbatim */ }
}
```

(“verbatim from current file” = copy the existing method body unchanged — those flows are not part of M4.)

- [ ] **Step 3: GhostView handles**

In `src/Game/GhostView.cs`: add a handle pool and the extended `Show`:

```csharp
private readonly List<MeshInstance3D> _handles = new();

public void Show(ValidatedPlacement? placement, SnapResult snap,
    IReadOnlyList<System.Numerics.Vector3>? handles = null, int hotHandle = -1)
{
    // existing body unchanged, then at the end:
    ShowHandles(handles, hotHandle);
}

private void ShowHandles(IReadOnlyList<System.Numerics.Vector3>? handles, int hot)
{
    foreach (var h in _handles)
        h.QueueFree();
    _handles.Clear();
    if (handles is null)
        return;
    for (int i = 0; i < handles.Count; i++)
    {
        var inst = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1.4f, Height = 2.8f },
            MaterialOverride = i == hot ? Materials.SnapIndicator : Materials.GhostValid,
            Position = handles[i].ToGodot() + Vector3.Up * 0.5f,
        };
        AddChild(inst);
        _handles.Add(inst);
    }
}
```

Also clear `_handles` inside the existing `Clear()` the same way.

- [ ] **Step 4: Main wiring**

`src/Game/Main.cs:38` — replace `var snap = new SnapService(_network);` with:

```csharp
var snap = new SnapEngine(_network);
var session = new DraftSession(_network, snap);
```

`Main.cs:76` — `_controller.Bind(_network, session, camera, ghost, _view);`

- [ ] **Step 5: Build + full verification**

```bash
dotnet test 2>&1 | tail -3                       # PASS
dotnet build citybuilder.sln 2>&1 | tail -3      # Build succeeded (solution green again)
CITYBUILDER_SMOKE=1 godot --headless . 2>&1 | grep -E "SMOKE|FAIL"   # SMOKE OK
```

If smoke fails on edge counts, the validation rules changed legitimate behavior — inspect the message, adjust the *scenario geometry* in `Main.RunSmoke` only if the old geometry violates the new rules (it should not: 48 m cells, 90° crossings, ≥ 96 m segments).

- [ ] **Step 6: Commit**

```bash
git add -A src tests
git commit -m "feat(game): DraftSession cutover — draggable handles, Enter-confirm, SnapEngine wiring"
```

---

### Task 16: GridOverlay + Toolbar controls + UI test drag

**Files:**
- Create: `src/Game/GridOverlay.cs`
- Modify: `src/Game/Toolbar.cs` (Arc button, Grid/Parallel/Perp toggles, cell-size picker, Adjust toggle)
- Modify: `src/Game/Main.cs` (instantiate GridOverlay; extend `RunUiTest` with a handle drag)

**Interfaces:**
- Consumes: `ToolController.Session` (Task 15), `CameraRig.MouseGroundPoint()`.
- Produces: visible grid while `SnapTypes.Grid` is enabled; toolbar rows for the new toggles.

- [ ] **Step 1: GridOverlay**

Create `src/Game/GridOverlay.cs`:

```csharp
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

/// <summary>Faint snapping-grid lines around the cursor while grid snap is active.</summary>
public partial class GridOverlay : Node3D
{
    private const int HalfCells = 12;
    private ToolController _controller = null!;
    private CameraRig _camera = null!;
    private MeshInstance3D _inst = null!;
    private ImmediateMesh _mesh = null!;

    public void Bind(ToolController controller, CameraRig camera)
    {
        _controller = controller;
        _camera = camera;
    }

    public override void _Ready()
    {
        _mesh = new ImmediateMesh();
        _inst = new MeshInstance3D { Mesh = _mesh, MaterialOverride = Materials.DebugLines };
        AddChild(_inst);
    }

    public override void _Process(double delta)
    {
        var session = _controller.Session;
        bool on = (session.EnabledSnaps & SnapTypes.Grid) != 0
            && _camera.MouseGroundPoint() is not null;
        _inst.Visible = on;
        if (!on)
            return;
        var center = _camera.MouseGroundPoint()!.Value;
        float cs = session.Grid.CellSize;
        float cx = Mathf.Round(center.X / cs) * cs;
        float cz = Mathf.Round(center.Z / cs) * cs;
        float extent = HalfCells * cs;
        _mesh.ClearSurfaces();
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int i = -HalfCells; i <= HalfCells; i++)
        {
            // fade toward the rim
            float a = 0.35f * (1f - MathF.Abs(i) / (float)(HalfCells + 1));
            var col = new Color(1f, 1f, 1f, a);
            _mesh.SurfaceSetColor(col);
            _mesh.SurfaceAddVertex(new Vector3(cx + i * cs, 0.1f, cz - extent));
            _mesh.SurfaceSetColor(col);
            _mesh.SurfaceAddVertex(new Vector3(cx + i * cs, 0.1f, cz + extent));
            _mesh.SurfaceSetColor(col);
            _mesh.SurfaceAddVertex(new Vector3(cx - extent, 0.1f, cz + i * cs));
            _mesh.SurfaceSetColor(col);
            _mesh.SurfaceAddVertex(new Vector3(cx + extent, 0.1f, cz + i * cs));
        }
        _mesh.SurfaceEnd();
    }
}
```

In `Main._Ready`, after the `GhostView` is added:

```csharp
var gridOverlay = new GridOverlay { Name = "GridOverlay" };
gridOverlay.Bind(_controller, camera);   // move this line BELOW _controller construction
AddChild(gridOverlay);
```

(Place construction after `_controller` exists, around `Main.cs:78`.)

- [ ] **Step 2: Toolbar additions**

In `src/Game/Toolbar.cs`:

1. Add `("Arc", ToolMode.Arc),` to the modes array after `("Curve+", ToolMode.ComplexCurve),`.
2. Extend the snap row array with the new toggles — Grid starts OFF, the rest ON:

```csharp
foreach (var (label, flag, initial) in new[]
{
    ("Nodes", SnapTypes.Nodes, true),
    ("Edges", SnapTypes.Edges, true),
    ("Angle", SnapTypes.Angle, true),
    ("Guides", SnapTypes.Guidelines, true),
    ("Parallel", SnapTypes.Parallel, true),
    ("Perp", SnapTypes.Perpendicular, true),
    ("Grid", SnapTypes.Grid, false),
})
{
    var cb = new CheckBox { Text = label, ButtonPressed = initial };
    cb.Toggled += on => _controller.SetSnapType(flag, on);
    snapRow.AddChild(cb);
    if (!initial)
        _controller.SetSnapType(flag, false);
}
var cellPick = new OptionButton();
foreach (var size in new[] { 4, 8, 16, 32 })
    cellPick.AddItem($"{size} m", size);
cellPick.Selected = 1; // 8 m
cellPick.ItemSelected += idx =>
    _controller.Session.Grid = new GridConfig(cellPick.GetItemId((int)idx));
snapRow.AddChild(cellPick);
```

3. Add an adjust-mode toggle under the snap row:

```csharp
var adjust = new CheckBox { Text = "Adjust before commit" };
adjust.Toggled += on => _controller.Session.AdjustMode = on;
box.AddChild(adjust);
```

4. Update the hint label: `"LMB place/drag handle · RMB step back · Enter confirm · Esc cancel · WASD pan · wheel zoom · Q/E rotate"`.

Note: `Toolbar.Bind` runs before `_Ready` (see `Main._Ready` order) — `_controller` is available; the initial `SetSnapType(Grid, false)` call aligns the session with the unchecked box (the session default already excludes Grid; the call is belt-and-braces).

- [ ] **Step 3: Extend the UI test with a drag**

In `Main.RunUiTest`, after the junction click and before the vehicle spawn, insert:

```csharp
// draft-handle drag: place a straight draft in adjust mode, drag its end, confirm
_controller.Session.AdjustMode = true;
_controller.SetMode(ToolMode.Straight);
_controller.HandleClickAt(V(-80, 60));
_controller.HandleClickAt(V(0, 60));       // complete → Adjustable
_controller.HandleMouseDownAt(V(0, 60));   // grab end handle
_controller.HandleHoverAt(V(40, 60));      // drag
_controller.HandleMouseUpAt();
_controller.ConfirmDraft();                // commit
_controller.Session.AdjustMode = false;
Expect(_network.Edges.Values.Any(e =>
        System.Numerics.Vector3.Distance(e.Curve.P3, V(40, 60)) < 1f
        || System.Numerics.Vector3.Distance(e.Curve.P0, V(40, 60)) < 1f),
    "dragged draft endpoint not committed at (40, 60)");
```

- [ ] **Step 4: Build + headless checks**

```bash
dotnet build citybuilder.sln 2>&1 | tail -3          # Build succeeded
CITYBUILDER_SMOKE=1 godot --headless . 2>&1 | grep -E "SMOKE|FAIL"    # SMOKE OK
CITYBUILDER_UITEST=/tmp/claude-ui-m4.png godot . 2>&1 | grep -E "UITEST"  # UITEST OK (needs a window)
```

Read `/tmp/claude-ui-m4.png` and verify: toolbar shows Arc + new snap toggles, the readout, and the built roads.

- [ ] **Step 5: Commit**

```bash
git add src/Game
git commit -m "feat(game): grid overlay, arc/snap/adjust toolbar controls, UI-test handle drag"
```

---

### Task 17: Verification sweep, visual scenarios, docs

**Files:**
- Modify: `src/Game/VisualShots.cs` (add an M4 drafting scene — read the file first and follow its existing scenario pattern)
- Modify: `docs/roadmap.md`, `docs/conventions.md`, `CLAUDE.md` (test count), `docs/verification.md` if it names the deleted tools

**Interfaces:** none new — this is the milestone gate.

- [ ] **Step 1: Add a visual scenario**

Read `src/Game/VisualShots.cs`. Following its existing pattern, add a scenario `m4-drafting` that: builds a straight road, an arc road off its end node (tangent-locked), and a parallel road snapped curb-to-curb; enables grid snap so the overlay is visible; positions the camera to frame everything; screenshots. If VisualShots drives a `ToolController`, use the session API from Task 15; if it builds networks directly, commit proposals built with the new shapes.

- [ ] **Step 2: Full verification sweep**

```bash
dotnet test 2>&1 | tail -3                                        # all green
dotnet build citybuilder.sln 2>&1 | tail -3                       # Build succeeded
CITYBUILDER_SMOKE=1 godot --headless . 2>&1 | grep -E "SMOKE"     # SMOKE OK
CITYBUILDER_SHOTS=tests/visual/shots godot . 2>&1 | tail -5       # screenshot harness
```

**Read the produced screenshots** (Read tool on the PNGs in `tests/visual/shots/`), especially the new `m4-drafting` shot: grid lines visible, arc tangent-smooth at the junction (no kink), parallel road exactly adjacent, handles rendered. Fix and re-shoot if anything looks wrong — a screenshot nobody reads is not verification.

- [ ] **Step 3: Update docs**

- `docs/roadmap.md`: move item 1's road-UX half into **Done** as *M4 — Road-building UX (2026-07-14)*: draft/gesture model with draggable handles, candidate-scored snapping (grid/perpendicular/parallel), tangent-locked curves + arc mode, geometry guards (per-type min length/radius, 25° junction floor, kink/sliver blocks). Renumber "Next up" so undo/redo + upgrade-in-place is item 1 (M5).
- `docs/conventions.md`: add the new constants (`MinJunctionAngleDeg 25°`, per-type `MinSegmentLength = max(8, Width)`, `MinRadius` values 20/35/10/25, grid default 8 m, snap weights).
- `CLAUDE.md`: update the domain-test count in the Quick commands section to the real number after this milestone (`dotnet test` prints it).
- `docs/verification.md` and `docs/gotchas.md`: grep for `IPlacementTool`, `SnapService`, `PlacementTools` — rewrite any mention to the DraftSession/SnapEngine equivalents.

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "feat: M4 road-building UX — draft editing, snapping suite, geometry guards"
```

---

## Plan self-review (done at authoring time)

- **Spec coverage:** draft model (T10–14), tangent lock (T10/11/14), arc (T2/12), radius readout (T14/15), editable handles incl. GridStamp (T10/13/15), instant-commit + Adjustable policy (T14), snap engine + scoring (T6), grid (T7 + overlay T16), perpendicular w/ arrival constraint (T8→T11), parallel guides (T9), angle-snap reference fix (T6 test + T14 context), per-type minimums (T3), TooShort/Radius (T4), SharpAngle/Kinked/sliver rules (T5), invariant test (T5), toolbar/UI (T15/16), visual verification + docs (T17). Non-goals respected: no undo/redo, no anarchy, no curved parallels, no grid rotation.
- **Known intermediate red:** solution build is red between T14 and T15 by design (domain tests stay green); the two tasks must be executed back-to-back.
- **Type consistency:** `DraftSession.Readout` tuple members are `LengthM/AngleDeg/RadiusM` (T14) and consumed with those names in T15. `SnapEngine.Resolve` signature identical across T6–T14. `GridConfig` construction `new GridConfig(cellSize)` everywhere.
