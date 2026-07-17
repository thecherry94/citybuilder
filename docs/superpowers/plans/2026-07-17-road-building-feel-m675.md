# Road-Building Feel (M6.75) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** CS2-grade road-building feel: hard sticky node capture with hysteresis, 8 m cell-length ticks, long-range perpendicular guides, per-kind snap indicators, pooled ghost rendering, and the project's first audio.

**Architecture:** All snapping logic stays in the pure domain (`SnapEngine`/`DraftSession`, `src/Domain/Tools/`); the Godot layer (`GhostView`, `ToolController`, `AudioFx`, `Toolbar`) only renders state and maps state transitions to sounds. Spec: `docs/superpowers/specs/2026-07-17-road-building-feel-design.md`.

**Tech Stack:** C# / .NET 8 domain + xUnit (net10.0 tests), Godot 4.6.2 mono game layer. No new dependencies.

## Global Constraints

- Domain purity: `src/Domain` never references Godot (golden rule 1).
- Constants (spec §1–3): `NodeCaptureFraction = 0.6f`, `ReleaseFactor = 1.4f`, `CellLength = 8f`, `WeightCellLength = 1.2f`, `MaxGuidelines = 48`.
- Hysteresis applies to **node captures only**; the engine stays stateless — `DraftSession` threads the held node through `SnapContext`.
- Angle snap stays a hard 15°-step **angular** quantization; committed directions are exactly on the snapped ray.
- No easing/interpolation on the ghost — decisive means instant.
- Audio: exactly five one-shot SFX, Game layer only, WAVs generated in-repo (`tools/sfxgen`), CC0.
- Verify per `docs/verification.md`: `dotnet test`, `dotnet build citybuilder.sln`, then the matching harness. Commit at every green step.
- `DraftSession.EnabledSnaps` default stays `SnapTypes.All & ~SnapTypes.Grid & ~SnapTypes.CellLength` (domain tests construct raw sessions; the Toolbar turns CellLength ON for the game, satisfying the spec's "default ON" user-facing).

---

### Task 1: Hard node capture (SnapEngine)

**Files:**
- Modify: `src/Domain/Tools/Snapping/SnapEngine.cs`
- Test: `tests/Domain.Tests/Tools/SnapEngineTests.cs`

**Interfaces:**
- Consumes: existing `SnapEngine.Resolve(Vector3 raw, float radius, SnapTypes enabled, SnapContext ctx)`.
- Produces: constants `SnapEngine.NodeCaptureFraction = 0.6f` (Task 2 adds `ReleaseFactor`). Behavior: any node within `NodeCaptureFraction * radius` of the raw cursor wins outright (nearest node if several); between that and the full radius the old weight scoring applies unchanged.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Domain.Tests/Tools/SnapEngineTests.cs`:

```csharp
[Fact]
public void NodeCaptureBeatsEdgeOnLeg()
{
    // THE T-junction complaint: cursor ON the edge (dist 0.5) but 3.04 m from the
    // node — inside the hard-capture ring (0.6 × 6 = 3.6). Node must win outright;
    // the old weight scoring gave the edge score 0.25 vs node 0.76 and slid forever.
    var (n, snap) = Setup();
    var result = snap.Resolve(new Vector3(97f, 0, 0.5f), 6f, SnapTypes.All, SnapContext.Empty);
    Assert.Equal(SnapKind.Node, result.Kind);
    Assert.Equal(new Vector3(100, 0, 0), result.Position);
}

[Fact]
public void SoftZonePreservesEdgeForMidSpanIntent()
{
    // node 4.90 m away — outside the capture ring (3.6), inside the resolve radius:
    // weight scoring still lets the dead-on edge win, so mid-span splits near (but
    // not at) a junction remain reachable.
    var (_, snap) = Setup();
    var result = snap.Resolve(new Vector3(95.2f, 0, 1.0f), 6f, SnapTypes.All, SnapContext.Empty);
    Assert.Equal(SnapKind.Edge, result.Kind);
}
```

- [ ] **Step 2: Run tests to verify the first fails**

Run: `dotnet test --filter "FullyQualifiedName~SnapEngineTests" -v q`
Expected: `NodeCaptureBeatsEdgeOnLeg` FAILS (Edge != Node); `SoftZonePreservesEdgeForMidSpanIntent` passes (documents existing behavior).

- [ ] **Step 3: Implement hard capture**

In `src/Domain/Tools/Snapping/SnapEngine.cs`, add constants next to the weights:

```csharp
    // Hard node capture (spec §1): within this fraction of the resolve radius a node
    // wins outright over every soft candidate — the T-junction "slides along the
    // leg" fix. CS2 does the same tier-then-distance architecture (net candidates
    // score at a hard higher tier than guides/grid; see the M6.75 research notes).
    public const float NodeCaptureFraction = 0.6f;
```

In `Resolve`, insert the hard-capture check between guideline collection and the candidate loop (guidelines are collected first so the winning snap can still report nearby guides):

```csharp
        var candidates = new List<SnapCandidate>();
        if ((enabled & SnapTypes.Nodes) != 0)
        {
            if (HardNodeCapture(raw, radius, ctx) is { } captured)
                return new SnapResult(captured.Position, SnapKind.Node, captured.Id, null, null,
                    NearbyGuides(guidelines, captured.Position, radius));
            AddNodeCandidates(raw, radius, candidates);
        }
```

(replacing the old `if ((enabled & SnapTypes.Nodes) != 0) AddNodeCandidates(...);` line) and add the producer:

```csharp
    /// <summary>Nearest node inside the hard-capture ring, or null. Task 2 extends
    /// this with hysteresis via <see cref="SnapContext.HeldNode"/>.</summary>
    private (NodeId Id, Vector3 Position)? HardNodeCapture(Vector3 raw, float radius, SnapContext ctx)
    {
        float captureR = NodeCaptureFraction * radius;
        (NodeId Id, Vector3 Position)? best = null;
        float bestDist = float.MaxValue;
        foreach (var n in network.Nodes.Values)
        {
            float d = Vector3.Distance(n.Position, raw);
            if (d <= captureR && d < bestDist)
            {
                bestDist = d;
                best = (n.Id, n.Position);
            }
        }
        return best;
    }
```

(`ctx` is unused until Task 2 — keep the parameter now so Task 2 only edits the body.)

- [ ] **Step 4: Run the full domain suite**

Run: `dotnet test -v q`
Expected: all green — the traces in the spec confirm every existing SnapEngine test keeps its outcome (`NodeBeatsEdgeWhenBothInRange` and `NodeStillBeatsGridWhenBothClose` now win via the hard ring, same result; `DeadOnWeakSnapBeatsBarelyInRangeStrongSnap` has the node at 4.6 m > 3.0 ring, unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Snapping/SnapEngine.cs tests/Domain.Tests/Tools/SnapEngineTests.cs
git commit -m "feat(domain): hard node capture ring — node beats edge inside 0.6x snap radius"
```

---

### Task 2: Node hysteresis (SnapContext.HeldNode + session threading)

**Files:**
- Modify: `src/Domain/Tools/Snapping/SnapTypes.cs` (SnapContext)
- Modify: `src/Domain/Tools/Snapping/SnapEngine.cs`
- Modify: `src/Domain/Tools/Draft/DraftSession.cs`
- Test: `tests/Domain.Tests/Tools/SnapEngineTests.cs`, `tests/Domain.Tests/Tools/DraftSessionTests.cs`

**Interfaces:**
- Produces: `SnapContext` gains `NodeId? HeldNode = null` (5th positional, defaulted — existing call sites unaffected). `SnapEngine.ReleaseFactor = 1.4f`. `DraftSession` passes `HeldNode: LastSnap.Kind == SnapKind.Node ? LastSnap.Node : null` on every resolve.

- [ ] **Step 1: Write the failing tests**

Append to `SnapEngineTests.cs`:

```csharp
[Fact]
public void HysteresisHoldsInsideReleaseRing()
{
    // held node 4.90 m away: outside capture (3.6) but inside release (1.4 × 3.6 =
    // 5.04) — the hold keeps it winning over the dead-on edge (contrast with
    // SoftZonePreservesEdgeForMidSpanIntent, same cursor without a hold).
    var (n, snap) = Setup();
    var held = n.Nodes.Values.Single(x => x.Position == new Vector3(100, 0, 0)).Id;
    var ctx = SnapContext.Empty with { HeldNode = held };
    var result = snap.Resolve(new Vector3(95.2f, 0, 1.0f), 6f, SnapTypes.All, ctx);
    Assert.Equal(SnapKind.Node, result.Kind);
    Assert.Equal(held, result.Node);
}

[Fact]
public void HysteresisReleasesBeyondRing()
{
    // 6.08 m from the held node > release ring 5.04 — hold drops, edge wins again.
    var (n, snap) = Setup();
    var held = n.Nodes.Values.Single(x => x.Position == new Vector3(100, 0, 0)).Id;
    var ctx = SnapContext.Empty with { HeldNode = held };
    var result = snap.Resolve(new Vector3(94f, 0, 1.0f), 6f, SnapTypes.All, ctx);
    Assert.Equal(SnapKind.Edge, result.Kind);
}

[Fact]
public void HysteresisTransfersToNearerCapturedNode()
{
    // a different node inside the capture ring and strictly closer than the held
    // one takes over — no dead zone between adjacent junctions.
    var (n, snap) = Setup();
    Net.Commit(n, Net.Straight(new(100, 0, 6), new(200, 0, 6)));
    var held = n.Nodes.Values.Single(x => x.Position == new Vector3(100, 0, 0)).Id;
    var other = n.Nodes.Values.Single(x => x.Position == new Vector3(100, 0, 6)).Id;
    var ctx = SnapContext.Empty with { HeldNode = held };
    var result = snap.Resolve(new Vector3(100f, 0, 4f), 6f, SnapTypes.All, ctx);
    Assert.Equal(SnapKind.Node, result.Kind);
    Assert.Equal(other, result.Node);
}

[Fact]
public void StaleHeldNodeIsIgnored()
{
    // held node bulldozed mid-gesture: the id no longer resolves — no crash,
    // normal resolution.
    var (_, snap) = Setup();
    var ctx = SnapContext.Empty with { HeldNode = new NodeId(9999) };
    var result = snap.Resolve(new Vector3(50f, 0, 3f), 5f, SnapTypes.All, ctx);
    Assert.Equal(SnapKind.Edge, result.Kind);
}
```

Append to `DraftSessionTests.cs`:

```csharp
[Fact]
public void SessionHoldsNodeSnapWhileCursorDriftsAlongLeg()
{
    // capture the node, then drift 4.9 m away along the edge: the session-threaded
    // hold keeps the node; drifting past the release ring lets go.
    var (n, s) = Setup();
    Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
    s.SetMode(DraftMode.Straight);
    s.PointerMoved(new Vector3(98f, 0, 0.5f), 6f);
    Assert.Equal(SnapKind.Node, s.LastSnap.Kind);
    s.PointerMoved(new Vector3(95.2f, 0, 1.0f), 6f);
    Assert.Equal(SnapKind.Node, s.LastSnap.Kind);
    s.PointerMoved(new Vector3(90f, 0, 1.0f), 6f);
    Assert.Equal(SnapKind.Edge, s.LastSnap.Kind);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SnapEngineTests|FullyQualifiedName~DraftSessionTests" -v q`
Expected: the four new SnapEngine tests fail to compile until `HeldNode` exists — add the record change first if the compiler blocks; then hysteresis tests FAIL (Edge != Node), `StaleHeldNodeIsIgnored` may already pass once it compiles.

- [ ] **Step 3: Implement**

`SnapTypes.cs` — extend the context record:

```csharp
public sealed record SnapContext(
    Vector3? Anchor,
    Vector3? ReferenceTangent,
    GridConfig? Grid = null,
    RoadTypeId? DrawingType = null,
    NodeId? HeldNode = null)
{
    public static readonly SnapContext Empty = new(null, null);
}
```

`SnapEngine.cs` — add the constant and use `ctx` in `HardNodeCapture`:

```csharp
    // Hysteresis (spec §1): a captured node only releases when the cursor leaves
    // ReleaseFactor × the capture ring — kills candidate flicker, CS2's top
    // complaint. Node captures only; the engine stays stateless (the session
    // remembers the held node between resolves).
    public const float ReleaseFactor = 1.4f;
```

```csharp
    private (NodeId Id, Vector3 Position)? HardNodeCapture(Vector3 raw, float radius, SnapContext ctx)
    {
        float captureR = NodeCaptureFraction * radius;
        (NodeId Id, Vector3 Position)? best = null;
        float bestDist = float.MaxValue;
        foreach (var n in network.Nodes.Values)
        {
            float d = Vector3.Distance(n.Position, raw);
            if (d <= captureR && d < bestDist)
            {
                bestDist = d;
                best = (n.Id, n.Position);
            }
        }
        // the held node survives out to the release ring and wins ties; a different
        // node captured strictly closer transfers the hold
        if (ctx.HeldNode is { } heldId && network.Nodes.TryGetValue(heldId, out var held))
        {
            float dHeld = Vector3.Distance(held.Position, raw);
            if (dHeld <= ReleaseFactor * captureR && dHeld <= bestDist)
                best = (heldId, held.Position);
        }
        return best;
    }
```

`DraftSession.cs` — thread the hold in `Resolve`:

```csharp
        var ctx = new SnapContext(anchor, reference,
            (EnabledSnaps & SnapTypes.Grid) != 0 ? Grid : null, RoadType,
            HeldNode: LastSnap.Kind == SnapKind.Node ? LastSnap.Node : null);
```

- [ ] **Step 4: Run the full domain suite**

Run: `dotnet test -v q`
Expected: all green (fuzz default sweep included — the threading is deterministic).

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Snapping/SnapTypes.cs src/Domain/Tools/Snapping/SnapEngine.cs src/Domain/Tools/Draft/DraftSession.cs tests/Domain.Tests/Tools/SnapEngineTests.cs tests/Domain.Tests/Tools/DraftSessionTests.cs
git commit -m "feat(domain): node snap hysteresis — held node releases only beyond 1.4x capture ring"
```

---

### Task 3: Angle-snap exactness + length-independence guards (test-only)

**Files:**
- Test: `tests/Domain.Tests/Tools/SnapEngineTests.cs`

**Interfaces:** none new — pins existing behavior (spec §1: "Angle-snap exactness, now guarded").

- [ ] **Step 1: Write the tests**

```csharp
[Theory]
[InlineData(40f)]
[InlineData(400f)]
public void AngleSnapIsExactAndLengthIndependent(float length)
{
    // CS2 accepts within a constant *lateral* band, so its angular window shrinks
    // with length and long roads commit 179.6°. Ours is angular: a ~6°-off cursor
    // snaps to the exact 15° ray at ANY length, and the snapped direction is
    // exactly on the ray (cross product ~0).
    var (_, snap) = Setup();
    var anchor = new Vector3(100, 0, 0);
    var ctx = new SnapContext(anchor, new Vector3(1, 0, 0));
    float offRad = 51f * MathF.PI / 180f; // 6° off the 45° ray
    var raw = anchor + length * new Vector3(MathF.Cos(offRad), 0, MathF.Sin(offRad));
    var result = snap.Resolve(raw, 2f, SnapTypes.Angle, ctx);
    Assert.Equal(SnapKind.Angle, result.Kind);
    Assert.Equal(45f, result.SnappedAngleDeg!.Value, 3);
    var dir = Vector3.Normalize(result.Position - anchor);
    var exact = new Vector3(MathF.Cos(MathF.PI / 4), 0, MathF.Sin(MathF.PI / 4));
    float cross = MathF.Abs(dir.X * exact.Z - dir.Z * exact.X);
    Assert.True(cross < 1e-5f, $"direction off the exact ray by cross={cross}");
}
```

- [ ] **Step 2: Run — expect PASS (it's a guard, not a change)**

Run: `dotnet test --filter "FullyQualifiedName~AngleSnapIsExactAndLengthIndependent" -v q`
Expected: PASS ×2. If it fails, angle snap has a real bug — stop and investigate before proceeding.

- [ ] **Step 3: Commit**

```bash
git add tests/Domain.Tests/Tools/SnapEngineTests.cs
git commit -m "test(domain): pin angle-snap exactness + length independence (anti-CS2-lateral-band guard)"
```

---

### Task 4: Cell-length ticks (SnapTypes.CellLength)

**Files:**
- Modify: `src/Domain/Tools/Snapping/SnapTypes.cs` (SnapKind + SnapTypes)
- Modify: `src/Domain/Tools/Snapping/SnapEngine.cs`
- Modify: `src/Domain/Tools/Draft/DraftSession.cs` (default snaps)
- Modify: `tests/Domain.Tests/Fuzzing/GestureFuzzer.cs` (action alphabet)
- Test: `tests/Domain.Tests/Tools/SnapEngineTests.cs`

**Interfaces:**
- Produces: `SnapKind.CellLength`; `SnapTypes.CellLength = 128` (member of `All`); `SnapEngine.CellLength = 8f`, `SnapEngine.WeightCellLength = 1.2f`. With an anchor: a weak candidate at the 8 m-quantized length along the raw direction; the angle-snap fallback's length is quantized too when the flag is on. `DraftSession.EnabledSnaps` default becomes `All & ~Grid & ~CellLength`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void CellLengthQuantizesFreeDrag()
{
    // anchor set, empty surroundings: 27.3 m raw drag ratchets to the 24 m tick
    var (_, snap) = Setup();
    var anchor = new Vector3(300, 0, 300);
    var ctx = new SnapContext(anchor, null);
    var result = snap.Resolve(new Vector3(327.3f, 0, 300f), 6f, SnapTypes.CellLength, ctx);
    Assert.Equal(SnapKind.CellLength, result.Kind);
    Assert.Equal(324f, result.Position.X, 3);
    Assert.Equal(300f, result.Position.Z, 3);
}

[Fact]
public void CellLengthComposesWithAngleSnap()
{
    // angle fallback fires (46°→45°) AND the length lands on an 8 m tick (27.3→24):
    // the CS2 rhythm — clean diagonals on both the ray and the tick.
    var (_, snap) = Setup();
    var anchor = new Vector3(300, 0, 300);
    var ctx = new SnapContext(anchor, new Vector3(1, 0, 0));
    float rad = 46f * MathF.PI / 180f;
    var raw = anchor + 27.3f * new Vector3(MathF.Cos(rad), 0, MathF.Sin(rad));
    var result = snap.Resolve(raw, 2f, SnapTypes.Angle | SnapTypes.CellLength, ctx);
    Assert.Equal(SnapKind.Angle, result.Kind);
    Assert.Equal(45f, result.SnappedAngleDeg!.Value, 3);
    Assert.Equal(24f, Vector3.Distance(result.Position, anchor), 3);
}

[Fact]
public void CellLengthNeedsAnchor()
{
    var (_, snap) = Setup();
    var result = snap.Resolve(new Vector3(327.3f, 0, 300f), 6f, SnapTypes.CellLength, SnapContext.Empty);
    Assert.Equal(SnapKind.Free, result.Kind);
}
```

- [ ] **Step 2: Run to verify compile failure / test failure**

Run: `dotnet test --filter "FullyQualifiedName~CellLength" -v q`
Expected: compile error (`SnapTypes.CellLength` missing) — that is the failing state for enum additions.

- [ ] **Step 3: Implement**

`SnapTypes.cs`:

```csharp
public enum SnapKind { Free, Node, Edge, GuidelineIntersection, Guideline, Angle, GridPoint, GridLine, Perpendicular, CellLength }

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
    CellLength = 128,
    All = Nodes | Edges | Angle | Guidelines | Grid | Parallel | Perpendicular | CellLength,
}
```

`SnapEngine.cs` — constants:

```csharp
    // CS2's zoning-cell rhythm (Game.Zones.ZoneUtils.CELL_SIZE = 8f): with an anchor,
    // segment length ratchets in 8 m ticks. Weak — loses to any geometry snap nearby.
    public const float CellLength = 8f;
    public const float WeightCellLength = 1.2f;
```

producer registration in `Resolve` (after the grid producer line):

```csharp
        if ((enabled & SnapTypes.CellLength) != 0 && ctx.Anchor is { } cellAnchor)
            AddCellLengthCandidates(raw, radius, cellAnchor, candidates);
```

producer + fallback composition:

```csharp
    private static void AddCellLengthCandidates(Vector3 raw, float radius, Vector3 anchor,
        List<SnapCandidate> outList)
    {
        if (QuantizeToCell(raw, anchor) is { } pos && Vector3.Distance(pos, raw) <= radius)
            outList.Add(new SnapCandidate(pos, SnapKind.CellLength, WeightCellLength));
    }

    /// <summary>Position at the 8 m-quantized distance from the anchor along
    /// anchor→p, or null when degenerate/zero-length.</summary>
    private static Vector3? QuantizeToCell(Vector3 p, Vector3 anchor)
    {
        var v = p - anchor;
        v.Y = 0;
        float d = v.Length();
        if (d < GeoConstants.Eps)
            return null;
        float q = MathF.Round(d / CellLength) * CellLength;
        if (q < CellLength)
            return null;
        return anchor + v / d * q;
    }
```

and change the angle-fallback line at the end of `Resolve`:

```csharp
        if ((enabled & SnapTypes.Angle) != 0 && ctx.Anchor is { } a2 && AngleSnap(raw, a2, ctx) is { } angled)
            return (enabled & SnapTypes.CellLength) != 0 && QuantizeToCell(angled.Position, a2) is { } ticked
                ? angled with { Position = ticked }
                : angled;
```

`DraftSession.cs`:

```csharp
    public SnapTypes EnabledSnaps { get; set; } = SnapTypes.All & ~SnapTypes.Grid & ~SnapTypes.CellLength;
```

`GestureFuzzer.cs` — extend the alphabet:

```csharp
    private static readonly SnapTypes[] SnapFlags =
    {
        SnapTypes.Nodes, SnapTypes.Edges, SnapTypes.Angle, SnapTypes.Guidelines,
        SnapTypes.Grid, SnapTypes.Parallel, SnapTypes.Perpendicular, SnapTypes.CellLength,
    };
```

- [ ] **Step 4: Run the full domain suite**

Run: `dotnet test -v q`
Expected: all green (session default excludes CellLength, so no existing session/fuzz-seed geometry shifts outside the fuzzer's own toggling, which just explores new behavior under the same invariants).

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Snapping/SnapTypes.cs src/Domain/Tools/Snapping/SnapEngine.cs src/Domain/Tools/Draft/DraftSession.cs tests/Domain.Tests/Fuzzing/GestureFuzzer.cs tests/Domain.Tests/Tools/SnapEngineTests.cs
git commit -m "feat(domain): 8 m cell-length ticks — CS2 zoning-cell rhythm, composes with angle snap"
```

---

### Task 5: Perpendicular guides + guide cap

**Files:**
- Modify: `src/Domain/Tools/Snapping/SnapEngine.cs` (`CollectGuidelines`)
- Test: `tests/Domain.Tests/Tools/SnapEngineTests.cs`

**Interfaces:**
- Produces: every node leg emits, besides the tangent continuation, two perpendicular guides (±90°, origin at the node, `GuidelineReach`). `SnapEngine.MaxGuidelines = 48` caps collection to the nearest by origin distance.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void PerpendicularGuidesConnectDistantRoadsAt90()
{
    // road A ends at (100,0,0); road B starts at (150,0,60) running +X. B's
    // continuation guide (line z=60 toward -X) crosses A's new perpendicular guide
    // (line x=100) at (100,0,60) — the "connect two far roads at a right angle"
    // assist. Cursor near the crossing snaps to the guide intersection.
    var (n, engine) = Setup(); // Setup commits road A: (0,0,0)→(100,0,0)
    Net.Commit(n, Net.Straight(new Vector3(150, 0, 60), new Vector3(250, 0, 60)));
    var result = engine.Resolve(new Vector3(101f, 0, 59f), 6f,
        SnapTypes.Guidelines, SnapContext.Empty);
    Assert.Equal(SnapKind.GuidelineIntersection, result.Kind);
    Assert.True(Vector3.Distance(result.Position, new Vector3(100, 0, 60)) < 0.1f,
        $"crossing at {result.Position}");
}

[Fact]
public void GuidelineCapKeepsNearestGuides()
{
    // 16 parallel roads → 32 nodes × 3 guides = 96 raw guides, over the 48 cap.
    // The nearest node's continuation guide must survive the cap: a cursor dead on
    // it still snaps Guideline.
    var n = Net.New();
    for (int i = 0; i < 16; i++)
        Net.Commit(n, Net.Straight(new Vector3(0, 0, i * 20f), new Vector3(60, 0, i * 20f)));
    var engine = new SnapEngine(n);
    var result = engine.Resolve(new Vector3(80f, 0, 0.4f), 6f,
        SnapTypes.Guidelines, SnapContext.Empty);
    Assert.Equal(SnapKind.Guideline, result.Kind);
    Assert.Equal(0f, result.Position.Z, 2);
}
```

- [ ] **Step 2: Run to verify the first fails**

Run: `dotnet test --filter "FullyQualifiedName~PerpendicularGuidesConnectDistantRoadsAt90|FullyQualifiedName~GuidelineCapKeepsNearest" -v q`
Expected: `PerpendicularGuidesConnectDistantRoadsAt90` FAILS (no perpendicular guide yet → Free or Guideline, not intersection). `GuidelineCapKeepsNearestGuides` likely passes already — it guards the cap you're about to add.

- [ ] **Step 3: Implement**

`SnapEngine.cs` — constant:

```csharp
    // Guide-count cap: perp guides triple the per-leg count and the pairwise
    // intersection scan is O(G²); keep the nearest guides only (far ones are
    // visual noise anyway).
    public const int MaxGuidelines = 48;
```

In `CollectGuidelines`, extend the per-leg block and cap the result:

```csharp
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
                var dir = Vector3.Normalize(-leaving);
                guides.Add(new Guideline(node.Position, dir, GuidelineReach));
                // perpendicular helpers (spec §3): ±90° off the leg, for connecting
                // distant roads at right angles via guide intersections
                var perp = new Vector3(dir.Z, 0, -dir.X);
                guides.Add(new Guideline(node.Position, perp, GuidelineReach));
                guides.Add(new Guideline(node.Position, -perp, GuidelineReach));
            }
        }
        if ((enabled & SnapTypes.Parallel) != 0 && ctx.DrawingType is { } drawType)
            AddParallelGuides(near, drawType, guides);
        if (guides.Count > MaxGuidelines)
            guides = guides.OrderBy(g => Vector3.Distance(g.Origin, near)).Take(MaxGuidelines).ToList();
        return guides;
    }
```

- [ ] **Step 4: Run the full domain suite**

Run: `dotnet test -v q`
Expected: all green. Watch `ParallelGuideSitsCurbToCurb` and `GuidelineExtensionSnapsPastTheNode` specifically (perp guides are far from those cursors by construction — traced in the spec).

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Snapping/SnapEngine.cs tests/Domain.Tests/Tools/SnapEngineTests.cs
git commit -m "feat(domain): perpendicular node guides + nearest-48 guide cap"
```

---

### Task 6: DraftSession audio events

**Files:**
- Modify: `src/Domain/Tools/Draft/DraftSession.cs`
- Test: `tests/Domain.Tests/Tools/DraftSessionTests.cs`

**Interfaces:**
- Produces: `public event Action? HandlePlaced;` (a click appended a handle), `public event Action? Committed;` (network commit succeeded), `public event Action? Rejected;` (completion or commit refused). Plain C# events — domain stays pure; Task 10's ToolController maps them to sounds.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void EventsFireOnPlaceAndCommit()
{
    var (n, s) = Setup();
    int placed = 0, committed = 0, rejected = 0;
    s.HandlePlaced += () => placed++;
    s.Committed += () => committed++;
    s.Rejected += () => rejected++;
    s.SetMode(DraftMode.Straight);
    ClickAt(s, 0, 0);
    ClickAt(s, 100, 0);
    Assert.Equal(2, placed);
    Assert.Equal(1, committed);
    Assert.Equal(0, rejected);
    Assert.Single(n.Edges);
}

[Fact]
public void RejectedFiresOnInvalidCompletion()
{
    var (n, s) = Setup();
    int rejected = 0;
    s.Rejected += () => rejected++;
    s.SetMode(DraftMode.Straight);
    ClickAt(s, 0, 0);
    ClickAt(s, 5, 0); // TooShort → Adjustable
    Assert.Equal(1, rejected);
    Assert.Empty(n.Edges);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~EventsFireOnPlaceAndCommit|FullyQualifiedName~RejectedFiresOnInvalidCompletion" -v q`
Expected: compile error (events missing).

- [ ] **Step 3: Implement**

In `DraftSession`, next to `Flashed`:

```csharp
    public event Action<string>? Flashed;
    /// <summary>A click appended a handle to the draft (game layer: placement click).</summary>
    public event Action? HandlePlaced;
    /// <summary>A proposal committed to the network (game layer: commit plop).</summary>
    public event Action? Committed;
    /// <summary>Completion or commit was refused (game layer: error blip).</summary>
    public event Action? Rejected;
```

In `Click`, after `d.AddHandle(s, ...)`:

```csharp
        d.AddHandle(s, d.Handles.Count == 0 ? BoundTangent(s) : null);
        HandlePlaced?.Invoke();
```

In `CompleteDraft`, the invalid/adjust branch — fire only for genuine rejection, not the voluntary adjust-mode hold:

```csharp
        if (validated is null || !validated.IsValid || AdjustMode)
        {
            if (validated is null)
                Flashed?.Invoke("shape is not buildable here");
            else if (!validated.IsValid)
                Flashed?.Invoke("invalid placement: " + string.Join(", ", validated.Errors));
            if (validated is null || !validated.IsValid)
                Rejected?.Invoke();
            State = SessionState.Adjustable;
            return;
        }
```

In `TryCommit`: after `var result = network.Commit(validated);` success path (right before the `DroppedSegments` flash block) add `Committed?.Invoke();` — and in both failure branches (`validated is null || !validated.IsValid` and `!result.Success`) add `Rejected?.Invoke();` after the `Flashed` call.

- [ ] **Step 4: Run the full domain suite**

Run: `dotnet test -v q` — all green.

- [ ] **Step 5: Commit**

```bash
git add src/Domain/Tools/Draft/DraftSession.cs tests/Domain.Tests/Tools/DraftSessionTests.cs
git commit -m "feat(domain): DraftSession HandlePlaced/Committed/Rejected events for game-layer feedback"
```

---

### Task 7: GhostView pooling + frame-cost probe

**Files:**
- Modify: `src/Game/GhostView.cs` (full rework — pooled)
- Modify: `src/Game/ToolController.cs` (probe)
- Modify: `src/Game/Main.cs` (UITEST hover loop)

**Interfaces:**
- Produces: `GhostView.Show` keeps its current signature this task (Task 8 extends it). Pooled `MeshInstance3D`s — hidden, never freed, grow-on-demand. Strip meshes rebuilt only when the `ValidatedPlacement` reference changed (implementation note: this replaces the spec's session revision counter — same effect, self-contained; recorded in the spec amendment, Task 12). Probe: with env `CITYBUILDER_GHOSTPROBE=1`, ToolController prints `GHOSTPROBE avg_us=<n> over 300 renders` every 300 `RenderGhost` calls.

- [ ] **Step 1: Add the probe FIRST (to capture before-numbers)**

`ToolController.cs` — add fields and wrap `RenderGhost`:

```csharp
    private static readonly bool GhostProbe = OS.GetEnvironment("CITYBUILDER_GHOSTPROBE") == "1";
    private long _probeTicks;
    private int _probeCount;
```

```csharp
    private void RenderGhost()
    {
        long t0 = GhostProbe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var handles = _session.Draft?.Handles.Select(h => h.Position).ToArray();
        _ghost.Show(_session.Ghost, _session.LastSnap, handles, _session.DraggingHandle);
        ReadoutChanged?.Invoke(_session.Readout is { } r
            ? r.RadiusM is { } rad && rad < 10000f
                ? $"{r.LengthM:0.#} m   {NormalizeDeg(r.AngleDeg):0.#}°   R {rad:0} m"
                : $"{r.LengthM:0.#} m   {NormalizeDeg(r.AngleDeg):0.#}°"
            : "");
        if (GhostProbe)
        {
            _probeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (++_probeCount == 300)
            {
                GD.Print($"GHOSTPROBE avg_us={_probeTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency / 300} over 300 renders");
                _probeTicks = 0;
                _probeCount = 0;
            }
        }
    }
```

`Main.cs` `RunUiTest` — insert a hover-storm right before the `Input.WarpMouse` line (exercises the probe and the pooled path under a live draft):

```csharp
            // ghost stress: 900 hovers on an active draft — with CITYBUILDER_GHOSTPROBE=1
            // this prints avg RenderGhost cost (the M6.75 before/after pooling evidence)
            _controller.SetMode(ToolMode.Straight);
            _controller.HandleClickAt(V(-80, -60));
            for (int i = 0; i < 900; i++)
                _controller.HandleHoverAt(V(-80 + i * 0.15f, -60 + (i % 7) * 0.3f));
            _controller.CancelGesture();
```

- [ ] **Step 2: Build + measure BEFORE numbers**

Run: `dotnet build citybuilder.sln` → green, then:
`$env:CITYBUILDER_GHOSTPROBE = "1"; $env:CITYBUILDER_UITEST = "$env:TEMP\ui_probe.png"; godot .` (needs a window)
Expected: `GHOSTPROBE avg_us=…` ×3 then `UITEST OK …`. **Record the number** — it goes into `docs/health/M6.75.md` (Task 12). Unset the env vars after (`Remove-Item Env:CITYBUILDER_GHOSTPROBE, Env:CITYBUILDER_UITEST`).

- [ ] **Step 3: Rework GhostView to pooled rendering**

Replace the pool-relevant parts of `src/Game/GhostView.cs` (keep `AddGhostArrows` as is; full class after rework):

```csharp
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

/// <summary>Renders the placement preview: ghost road strips (blue = valid, red =
/// invalid), guide lines, crossing markers, and the snap indicator. All scene nodes
/// are pooled — hidden rather than freed — so continuous mouse motion never
/// allocates or frees nodes; strip meshes rebuild only when the validated placement
/// actually changed.</summary>
public partial class GhostView : Node3D
{
    private readonly List<MeshInstance3D> _strips = new();
    private int _stripsUsed;
    private readonly List<MeshInstance3D> _handles = new();
    private int _handlesUsed;
    private MeshInstance3D _lines = null!;
    private ImmediateMesh _linesMesh = null!;
    private MeshInstance3D _snapDot = null!;
    private ValidatedPlacement? _lastPlacement;

    public override void _Ready()
    {
        _linesMesh = new ImmediateMesh();
        _lines = new MeshInstance3D { Name = "guides", Mesh = _linesMesh, MaterialOverride = Materials.DebugLines };
        AddChild(_lines);
        _snapDot = new MeshInstance3D
        {
            Name = "snap",
            Mesh = new SphereMesh { Radius = 0.9f, Height = 1.8f },
            MaterialOverride = Materials.SnapIndicator,
            Visible = false,
        };
        AddChild(_snapDot);
    }

    public void Clear()
    {
        HideFrom(_strips, 0);
        _stripsUsed = 0;
        HideFrom(_handles, 0);
        _handlesUsed = 0;
        _linesMesh.ClearSurfaces();
        _snapDot.Visible = false;
        _lastPlacement = null;
    }

    private static void HideFrom(List<MeshInstance3D> pool, int from)
    {
        for (int i = from; i < pool.Count; i++)
            pool[i].Visible = false;
    }

    private MeshInstance3D Pooled(List<MeshInstance3D> pool, ref int used)
    {
        if (used == pool.Count)
        {
            var inst = new MeshInstance3D();
            AddChild(inst);
            pool.Add(inst);
        }
        var node = pool[used++];
        node.Visible = true;
        return node;
    }

    public void Show(ValidatedPlacement? placement, SnapResult snap,
        IReadOnlyList<System.Numerics.Vector3>? handles = null, int hotHandle = -1)
    {
        // snap indicator
        _snapDot.Visible = snap.Kind != SnapKind.Free;
        if (_snapDot.Visible)
            _snapDot.Position = snap.Position.ToGodot() + Vector3.Up * 0.4f;

        bool anyLines = false;
        _linesMesh.ClearSurfaces();

        if (snap.ActiveGuidelines.Count > 0)
        {
            _linesMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
            anyLines = true;
            foreach (var g in snap.ActiveGuidelines)
            {
                // dashed: 4 m on, 4 m off
                for (float s = 0; s < g.Length; s += 8f)
                {
                    float s1 = MathF.Min(s + 4f, g.Length);
                    _linesMesh.SurfaceSetColor(new Color(1f, 0.85f, 0.2f, 0.8f));
                    _linesMesh.SurfaceAddVertex(g.PointAt(s).ToGodot() + Vector3.Up * 0.15f);
                    _linesMesh.SurfaceSetColor(new Color(1f, 0.85f, 0.2f, 0.8f));
                    _linesMesh.SurfaceAddVertex(g.PointAt(s1).ToGodot() + Vector3.Up * 0.15f);
                }
            }
        }

        if (placement is not null)
        {
            // strip meshes rebuild only when the placement changed; the session
            // produces a fresh ValidatedPlacement whenever geometry/validity moved,
            // so reference identity is the dirty flag
            if (!ReferenceEquals(placement, _lastPlacement))
            {
                int used = 0;
                var material = placement.IsValid ? Materials.GhostValid : Materials.GhostInvalid;
                foreach (var pc in placement.Proposal.Curves)
                {
                    float width = RoadCatalog.Get(placement.Proposal.Type).Width;
                    var mesh = MeshBuilders.BuildGhostStrip(pc.Curve, width);
                    if (mesh is null)
                        continue;
                    var inst = Pooled(_strips, ref used);
                    inst.Mesh = mesh;
                    inst.MaterialOverride = material;
                }
                HideFrom(_strips, used);
                _stripsUsed = used;
            }

            // direction arrows: drawing an asymmetric type, show which way it will flow
            var ghostType = RoadCatalog.Get(placement.Proposal.Type);
            if (ghostType.IsDirectionAsymmetric)
            {
                if (!anyLines)
                {
                    _linesMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                    anyLines = true;
                }
                foreach (var pc in placement.Proposal.Curves)
                    AddGhostArrows(pc.Curve);
            }

            // crossing markers
            if (placement.CrossingPoints.Count > 0)
            {
                if (!anyLines)
                {
                    _linesMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                    anyLines = true;
                }
                foreach (var p in placement.CrossingPoints)
                {
                    var c = p.ToGodot() + Vector3.Up * 0.3f;
                    var col = new Color(0.3f, 1f, 0.5f);
                    _linesMesh.SurfaceSetColor(col);
                    _linesMesh.SurfaceAddVertex(c + new Vector3(-2, 0, -2));
                    _linesMesh.SurfaceSetColor(col);
                    _linesMesh.SurfaceAddVertex(c + new Vector3(2, 0, 2));
                    _linesMesh.SurfaceSetColor(col);
                    _linesMesh.SurfaceAddVertex(c + new Vector3(-2, 0, 2));
                    _linesMesh.SurfaceSetColor(col);
                    _linesMesh.SurfaceAddVertex(c + new Vector3(2, 0, -2));
                }
            }
        }
        else
        {
            HideFrom(_strips, 0);
            _stripsUsed = 0;
        }
        _lastPlacement = placement;

        if (anyLines)
            _linesMesh.SurfaceEnd();

        ShowHandles(handles, hotHandle);
    }

    private void ShowHandles(IReadOnlyList<System.Numerics.Vector3>? handles, int hot)
    {
        int used = 0;
        if (handles is not null)
        {
            for (int i = 0; i < handles.Count; i++)
            {
                var inst = Pooled(_handles, ref used);
                inst.Mesh ??= new SphereMesh { Radius = 1.4f, Height = 2.8f };
                inst.MaterialOverride = i == hot ? Materials.SnapIndicator : Materials.GhostValid;
                inst.Position = handles[i].ToGodot() + Vector3.Up * 0.5f;
            }
        }
        HideFrom(_handles, used);
        _handlesUsed = used;
    }
```

(`AddGhostArrows` unchanged at the bottom of the class.)

- [ ] **Step 4: Build + measure AFTER numbers + harness check**

Run: `dotnet build citybuilder.sln` → green.
`$env:CITYBUILDER_GHOSTPROBE = "1"; $env:CITYBUILDER_UITEST = "$env:TEMP\ui_probe.png"; godot .` → `GHOSTPROBE avg_us=…` (record; expect a clear drop) + `UITEST OK`. Then plain smoke: `$env:CITYBUILDER_SMOKE = "1"; godot --headless .` → `SMOKE OK`. Read `$env:TEMP\ui_probe.png` — toolbar + ghost visuals intact.

- [ ] **Step 5: Commit**

```bash
git add src/Game/GhostView.cs src/Game/ToolController.cs src/Game/Main.cs
git commit -m "perf(game): pooled ghost rendering + GHOSTPROBE frame-cost probe (before/after evidence for M6.75 health)"
```

---

### Task 8: Per-kind snap indicators

**Files:**
- Modify: `src/Game/Materials.cs`
- Modify: `src/Game/GhostView.cs`
- Modify: `src/Game/ToolController.cs`

**Interfaces:**
- Produces: `GhostView.Show(placement, snap, handles = null, hotHandle = -1, System.Numerics.Vector3? edgeTangent = null, System.Numerics.Vector3? referenceDir = null, System.Numerics.Vector3? anchor = null)`. ToolController supplies: `edgeTangent` (tangent at `snap.Edge`), `referenceDir` (`_session.Draft?.StartTangent`), `anchor` (last fixed handle, same rule as the session's snap anchor). Indicator per `SnapKind` (spec §4 table); guide-crossing dots on active guides; cell ticks along the final segment; angle label. `Materials.SnapNode`, `Materials.SnapAccent`.

- [ ] **Step 1: Add materials**

Append to `Materials.cs`:

```csharp
    public static readonly StandardMaterial3D SnapNode = new()
    {
        // node lock ring — the decisive-capture signal, brighter than everything else
        AlbedoColor = new Color(0.35f, 0.95f, 1f, 0.95f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D SnapAccent = new()
    {
        AlbedoColor = new Color(0.55f, 0.8f, 1f, 0.9f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
```

- [ ] **Step 2: Add indicator nodes + logic to GhostView**

Fields (replace `_snapDot`-only setup; `_snapDot` stays as the fallback):

```csharp
    private MeshInstance3D _nodeRing = null!;
    private MeshInstance3D _edgeTick = null!;
    private Node3D _perpGlyph = null!;
    private MeshInstance3D _gridQuad = null!;
    private Label3D _angleLabel = null!;
    private readonly List<MeshInstance3D> _crossDots = new();
    private int _crossDotsUsed;
```

In `_Ready()` after `_snapDot`:

```csharp
        _nodeRing = new MeshInstance3D
        {
            Name = "snap_node",
            Mesh = new TorusMesh { InnerRadius = 2.0f, OuterRadius = 2.7f },
            MaterialOverride = Materials.SnapNode,
            Visible = false,
        };
        AddChild(_nodeRing);
        _edgeTick = new MeshInstance3D
        {
            Name = "snap_edge",
            Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.15f, 5.5f) },
            MaterialOverride = Materials.SnapAccent,
            Visible = false,
        };
        AddChild(_edgeTick);
        _perpGlyph = new Node3D { Name = "snap_perp", Visible = false };
        _perpGlyph.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.35f, 0.15f, 3.0f) },
            Position = new Vector3(0, 0, 1.5f),
            MaterialOverride = Materials.SnapAccent,
        });
        _perpGlyph.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(3.0f, 0.15f, 0.35f) },
            Position = new Vector3(1.5f, 0, 0),
            MaterialOverride = Materials.SnapAccent,
        });
        AddChild(_perpGlyph);
        _gridQuad = new MeshInstance3D
        {
            Name = "snap_grid",
            Mesh = new BoxMesh { Size = new Vector3(1.6f, 0.1f, 1.6f) },
            MaterialOverride = Materials.SnapAccent,
            Visible = false,
        };
        AddChild(_gridQuad);
        _angleLabel = new Label3D
        {
            Name = "snap_angle",
            FontSize = 64,
            PixelSize = 0.05f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = new Color(0.55f, 0.85f, 1f),
            OutlineSize = 12,
            Visible = false,
        };
        AddChild(_angleLabel);
```

Extend `Clear()` with:

```csharp
        _nodeRing.Visible = false;
        _edgeTick.Visible = false;
        _perpGlyph.Visible = false;
        _gridQuad.Visible = false;
        _angleLabel.Visible = false;
        HideFrom(_crossDots, 0);
        _crossDotsUsed = 0;
```

Change `Show`'s signature and replace the `_snapDot` block:

```csharp
    public void Show(ValidatedPlacement? placement, SnapResult snap,
        IReadOnlyList<System.Numerics.Vector3>? handles = null, int hotHandle = -1,
        System.Numerics.Vector3? edgeTangent = null,
        System.Numerics.Vector3? referenceDir = null,
        System.Numerics.Vector3? anchor = null)
    {
        ShowIndicator(snap, edgeTangent, anchor);
```

and add (plus the crossings/ticks helpers, called from the lines section — see below):

```csharp
    private void ShowIndicator(SnapResult snap,
        System.Numerics.Vector3? edgeTangent, System.Numerics.Vector3? anchor)
    {
        _nodeRing.Visible = false;
        _edgeTick.Visible = false;
        _perpGlyph.Visible = false;
        _gridQuad.Visible = false;
        _angleLabel.Visible = false;
        _snapDot.Visible = false;
        var pos = snap.Position.ToGodot() + Vector3.Up * 0.25f;
        switch (snap.Kind)
        {
            case SnapKind.Node:
                _nodeRing.Visible = true;
                _nodeRing.Position = pos;
                break;
            case SnapKind.Edge when edgeTangent is { } tan:
                _edgeTick.Visible = true;
                _edgeTick.Position = pos;
                _edgeTick.Rotation = new Vector3(0, MathF.Atan2(tan.X, tan.Z) + MathF.PI / 2, 0);
                break;
            case SnapKind.Perpendicular when snap.DirectionConstraint is { } arrive:
                _perpGlyph.Visible = true;
                _perpGlyph.Position = pos;
                _perpGlyph.Rotation = new Vector3(0, MathF.Atan2(arrive.X, arrive.Z), 0);
                break;
            case SnapKind.GridPoint or SnapKind.GridLine:
                _gridQuad.Visible = true;
                _gridQuad.Position = pos;
                break;
            case SnapKind.Angle when snap.SnappedAngleDeg is { } deg:
                _angleLabel.Visible = true;
                _angleLabel.Position = pos + Vector3.Up * 2.5f;
                _angleLabel.Text = $"{NormalizeDeg(deg):0}°";
                if (anchor is { } a)
                    _snapDotFallback(pos); // small dot at the snapped tip too
                break;
            case SnapKind.GuidelineIntersection or SnapKind.Guideline or SnapKind.CellLength:
                _snapDotFallback(pos);
                break;
        }
    }

    private void _snapDotFallback(Vector3 pos)
    {
        _snapDot.Visible = true;
        _snapDot.Position = pos + Vector3.Up * 0.15f;
    }

    private static float NormalizeDeg(float deg)
    {
        deg %= 360f;
        if (deg < 0) deg += 360f;
        return deg;
    }
```

Guide-crossing dots + cell ticks — inside the lines section of `Show`, after the dashed-guides loop (still inside `if (snap.ActiveGuidelines.Count > 0)`), add crossing dots; and after the placement block, add angle-arc + cell ticks into the same lines surface:

```csharp
            // dots where active guides cross — the snappable intersections (CS2 shows these)
            int dotsUsed = 0;
            for (int i = 0; i < snap.ActiveGuidelines.Count; i++)
            for (int j = i + 1; j < snap.ActiveGuidelines.Count; j++)
            {
                var a = snap.ActiveGuidelines[i];
                var b = snap.ActiveGuidelines[j];
                if (!CityBuilder.Domain.Geometry.BezierOps.SegmentIntersect(
                        new System.Numerics.Vector2(a.Origin.X, a.Origin.Z),
                        new System.Numerics.Vector2(a.PointAt(a.Length).X, a.PointAt(a.Length).Z),
                        new System.Numerics.Vector2(b.Origin.X, b.Origin.Z),
                        new System.Numerics.Vector2(b.PointAt(b.Length).X, b.PointAt(b.Length).Z),
                        out float u, out _))
                    continue;
                var cross = a.PointAt(u * a.Length).ToGodot() + Vector3.Up * 0.3f;
                var dot = Pooled(_crossDots, ref dotsUsed);
                dot.Mesh ??= new CylinderMesh { TopRadius = 0.7f, BottomRadius = 0.7f, Height = 0.15f };
                dot.MaterialOverride = Materials.SnapAccent;
                dot.Position = cross;
            }
            HideFrom(_crossDots, dotsUsed);
            _crossDotsUsed = dotsUsed;
```

(when `ActiveGuidelines.Count == 0`, also `HideFrom(_crossDots, 0)` in the else path)

```csharp
        // cell ticks: length landed on the 8 m rhythm — cross-ticks along the last
        // stretch toward the anchor (CellLength snap, or Angle with quantized length)
        if (anchor is { } anch
            && snap.Kind is SnapKind.CellLength or SnapKind.Angle
            && System.Numerics.Vector3.Distance(snap.Position, anch) is > 1f and var len
            && MathF.Abs(len / 8f - MathF.Round(len / 8f)) < 0.01f)
        {
            if (!anyLines)
            {
                _linesMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                anyLines = true;
            }
            var dirN = System.Numerics.Vector3.Normalize(snap.Position - anch);
            var side = new System.Numerics.Vector3(dirN.Z, 0, -dirN.X) * 0.9f;
            int ticks = (int)MathF.Round(len / 8f);
            var col = new Color(0.55f, 0.8f, 1f, 0.9f);
            for (int k = Math.Max(1, ticks - 4); k <= ticks; k++)
            {
                var p = (anch + dirN * (k * 8f)).ToGodot() + Vector3.Up * 0.25f;
                _linesMesh.SurfaceSetColor(col);
                _linesMesh.SurfaceAddVertex(p + side.ToGodot());
                _linesMesh.SurfaceSetColor(col);
                _linesMesh.SurfaceAddVertex(p - side.ToGodot());
            }
        }
```

- [ ] **Step 3: Feed the new parameters from ToolController**

In `RenderGhost` (inside the probe wrapper from Task 7), replace the `Show` call:

```csharp
        var s = _session.LastSnap;
        System.Numerics.Vector3? edgeTan = null;
        if (s.Edge is { } eh && _network.Edges.TryGetValue(eh.Edge, out var hitEdge))
            edgeTan = hitEdge.Curve.Tangent(eh.T);
        System.Numerics.Vector3? anchor = null;
        if (_session.Draft is { } dft && dft.Handles.Count > 0)
            anchor = (_session.DraggingHandle > 0 ? dft.Handles[0] : dft.Handles[^1]).Position;
        _ghost.Show(_session.Ghost, s, handles, _session.DraggingHandle,
            edgeTan, _session.Draft?.StartTangent, anchor);
```

- [ ] **Step 4: Build + eyeball via UITEST**

`dotnet build citybuilder.sln` → green. `$env:CITYBUILDER_UITEST = "$env:TEMP\ui_ind.png"; godot .` → `UITEST OK`; read the PNG (grid + ghost visible, no visual wreckage). Full indicator-kind verification comes from the Task 11 shot gallery.

- [ ] **Step 5: Commit**

```bash
git add src/Game/Materials.cs src/Game/GhostView.cs src/Game/ToolController.cs
git commit -m "feat(game): per-kind snap indicators — node ring, edge tick, perp glyph, angle badge, cell ticks, guide-crossing dots"
```

---

### Task 9: Toolbar 8 m toggle

**Files:**
- Modify: `src/Game/Toolbar.cs`

**Interfaces:**
- Consumes: `SnapTypes.CellLength` (Task 4), `ToolController.SetSnapType`.
- Produces: an "8 m" checkbox, ON by default in the game (the session default excludes it, so the toolbar must push the initial state for every flag whose initial differs).

- [ ] **Step 1: Implement**

In `Toolbar._Ready()`, extend the snap-row array and always push the initial state:

```csharp
        foreach (var (label, flag, initial) in new[]
        {
            ("Nodes", SnapTypes.Nodes, true),
            ("Edges", SnapTypes.Edges, true),
            ("Angle", SnapTypes.Angle, true),
            ("Guides", SnapTypes.Guidelines, true),
            ("Parallel", SnapTypes.Parallel, true),
            ("Perp", SnapTypes.Perpendicular, true),
            ("8 m", SnapTypes.CellLength, true),
            ("Grid", SnapTypes.Grid, false),
        })
        {
            var cb = new CheckBox { Text = label, ButtonPressed = initial };
            cb.Toggled += on => _controller.SetSnapType(flag, on);
            snapRow.AddChild(cb);
            _controller.SetSnapType(flag, initial);
        }
```

- [ ] **Step 2: Build + smoke + UI screenshot**

`dotnet build citybuilder.sln` → green. `$env:CITYBUILDER_SMOKE = "1"; godot --headless .` → `SMOKE OK` (the smoke traces in the spec confirm cell ticks don't shift any scripted click — nodes/guides/edges win everywhere it matters). `$env:CITYBUILDER_UITEST = "$env:TEMP\ui_8m.png"; godot .` → read the PNG: the "8 m" checkbox is visible and checked.

- [ ] **Step 3: Commit**

```bash
git add src/Game/Toolbar.cs
git commit -m "feat(game): 8 m cell-length snap toggle in the toolbar, on by default"
```

---

### Task 10: First audio — sfx generator, AudioFx, wiring

**Files:**
- Create: `tools/sfxgen/sfxgen.csproj`, `tools/sfxgen/Program.cs`
- Create: `assets/audio/LICENSE.md` (+ five generated `.wav` files)
- Create: `src/Game/AudioFx.cs`
- Modify: `src/Game/ToolController.cs`, `src/Game/Main.cs`

**Interfaces:**
- Produces: `enum Sfx { SnapTick, Place, Commit, Reject, Bulldoze }`; `AudioFx : Node` with `void Play(Sfx sfx)` and `int LoadedCount`; `ToolController.BindAudio(AudioFx audio)`. Sounds fire on: snap target change (rate-limited ≥60 ms), `HandlePlaced`, `Committed`, `Rejected`, bulldoze click. sfxgen is NOT added to `citybuilder.sln` (standalone tool, run via `dotnet run --project`).

- [ ] **Step 1: Write the generator**

`tools/sfxgen/sfxgen.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

`tools/sfxgen/Program.cs`:

```csharp
// Generates the five M6.75 UI sounds as 16-bit mono 44.1 kHz WAVs. Deterministic,
// dependency-free — regenerate any time with:
//   dotnet run --project tools/sfxgen -- assets/audio
const int Rate = 44100;
string outDir = args.Length > 0 ? args[0] : "assets/audio";
Directory.CreateDirectory(outDir);

Write("tick.wav", Synth(0.045, t =>
    Math.Sin(2 * Math.PI * 1900 * t) * Env(t, attack: 0.002, tau: 0.010) * 0.5));
Write("click.wav", Synth(0.06, t =>
    (Math.Sin(2 * Math.PI * 950 * t) + 0.4 * Math.Sin(2 * Math.PI * 1400 * t))
    * Env(t, attack: 0.001, tau: 0.015) * 0.4));
Write("plop.wav", Synth(0.14, t =>
{
    double f = 380 - (380 - 160) * (t / 0.14); // downward sweep
    return Math.Sin(2 * Math.PI * f * t) * Env(t, attack: 0.004, tau: 0.05) * 0.55;
}));
Write("blip.wav", Synth(0.16, t =>
{
    double f = t < 0.07 ? 290 : 220; // two-tone descending error
    return Math.Tanh(2.2 * Math.Sin(2 * Math.PI * f * t)) * Env(t, attack: 0.002, tau: 0.06) * 0.35;
}));
var rng = new Random(675); // fixed seed: byte-stable output
double brown = 0;
Write("crunch.wav", Synth(0.22, t =>
{
    brown = 0.94 * brown + 0.35 * (rng.NextDouble() * 2 - 1); // integrated noise
    return brown * Env(t, attack: 0.003, tau: 0.07) * 0.8;
}));
Console.WriteLine($"sfxgen: wrote 5 wavs to {outDir}");
return;

static double Env(double t, double attack, double tau)
    => (t < attack ? t / attack : Math.Exp(-(t - attack) / tau));

static short[] Synth(double seconds, Func<double, double> f)
{
    int n = (int)(seconds * Rate);
    var samples = new short[n];
    for (int i = 0; i < n; i++)
    {
        double v = Math.Clamp(f(i / (double)Rate), -1, 1);
        samples[i] = (short)(v * short.MaxValue);
    }
    return samples;
}

void Write(string name, short[] samples)
{
    using var fs = File.Create(Path.Combine(outDir, name));
    using var w = new BinaryWriter(fs);
    int dataLen = samples.Length * 2;
    w.Write("RIFF"u8);
    w.Write(36 + dataLen);
    w.Write("WAVE"u8);
    w.Write("fmt "u8);
    w.Write(16);
    w.Write((short)1);          // PCM
    w.Write((short)1);          // mono
    w.Write(Rate);
    w.Write(Rate * 2);          // byte rate
    w.Write((short)2);          // block align
    w.Write((short)16);         // bits
    w.Write("data"u8);
    w.Write(dataLen);
    foreach (var s in samples)
        w.Write(s);
}
```

- [ ] **Step 2: Generate the assets + license note**

Run: `dotnet run --project tools/sfxgen -- assets/audio`
Expected: `sfxgen: wrote 5 wavs to assets/audio` and five `.wav` files exist.

`assets/audio/LICENSE.md`:

```markdown
# assets/audio

Five UI one-shots (tick, click, plop, blip, crunch), synthesized in-repo by
`tools/sfxgen` (fixed seed, byte-stable). No third-party material — effectively
CC0/public domain. Regenerate: `dotnet run --project tools/sfxgen -- assets/audio`.
```

- [ ] **Step 3: AudioFx node**

`src/Game/AudioFx.cs`:

```csharp
using Godot;

namespace CityBuilder.Game;

public enum Sfx { SnapTick, Place, Commit, Reject, Bulldoze }

/// <summary>The project's sound effects: five one-shots on a small round-robin
/// player pool. Streams load from loose WAVs (AudioStreamWav.LoadFromFile) so the
/// import pipeline is not involved; snap ticks are rate-limited so guide-hopping
/// doesn't machine-gun. Headless runs get the dummy audio driver — Play is a
/// safe no-op there.</summary>
public partial class AudioFx : Node
{
    private readonly Dictionary<Sfx, AudioStream> _streams = new();
    private readonly Dictionary<Sfx, float> _volumeDb = new();
    private readonly List<AudioStreamPlayer> _players = new();
    private int _next;
    private ulong _lastTickMs;

    public int LoadedCount => _streams.Count;

    public override void _Ready()
    {
        foreach (var (sfx, file, db) in new (Sfx, string, float)[]
        {
            (Sfx.SnapTick, "tick.wav", -14f),
            (Sfx.Place, "click.wav", -8f),
            (Sfx.Commit, "plop.wav", -6f),
            (Sfx.Reject, "blip.wav", -9f),
            (Sfx.Bulldoze, "crunch.wav", -7f),
        })
        {
            var path = ProjectSettings.GlobalizePath($"res://assets/audio/{file}");
            if (AudioStreamWav.LoadFromFile(path) is { } stream)
            {
                _streams[sfx] = stream;
                _volumeDb[sfx] = db;
            }
            else
            {
                GD.PushWarning($"AudioFx: could not load {path}");
            }
        }
        for (int i = 0; i < 4; i++)
        {
            var p = new AudioStreamPlayer { Name = $"sfx{i}" };
            AddChild(p);
            _players.Add(p);
        }
    }

    public void Play(Sfx sfx)
    {
        if (sfx == Sfx.SnapTick)
        {
            ulong now = Time.GetTicksMsec();
            if (now - _lastTickMs < 60)
                return;
            _lastTickMs = now;
        }
        if (!_streams.TryGetValue(sfx, out var stream))
            return;
        var p = _players[_next];
        _next = (_next + 1) % _players.Count;
        p.Stream = stream;
        p.VolumeDb = _volumeDb[sfx];
        p.Play();
    }
}
```

- [ ] **Step 4: Wire ToolController + Main**

`ToolController.cs` — field, binder, event mapping, snap-tick detection, bulldoze crunch:

```csharp
    private AudioFx? _audio;
    private (SnapKind Kind, int Id) _lastSnapSig = (SnapKind.Free, -1);

    public void BindAudio(AudioFx audio)
    {
        _audio = audio;
        _session.HandlePlaced += () => _audio?.Play(Sfx.Place);
        _session.Committed += () => _audio?.Play(Sfx.Commit);
        _session.Rejected += () => _audio?.Play(Sfx.Reject);
    }
```

In `RenderGhost`, after the `Show` call:

```csharp
        var sig = (s.Kind, s.Node?.Value ?? s.Edge?.Edge.Value ?? (int)s.Kind);
        if (sig != _lastSnapSig && s.Kind != SnapKind.Free)
            _audio?.Play(Sfx.SnapTick);
        _lastSnapSig = sig;
```

In `HandleClickAt`'s bulldoze branch, after `_network.RemoveEdge(target);`:

```csharp
                _audio?.Play(Sfx.Bulldoze);
```

`Main.cs` — field `private AudioFx _audio = null!;`; in `_Ready()` after `_controller.Bind(...)` (BindAudio subscribes session events, so Bind must run first):

```csharp
        _audio = new AudioFx { Name = "AudioFx" };
        AddChild(_audio);
        _controller.BindAudio(_audio);
```

In `RunSmoke`, right before `GD.Print("SMOKE OK");`:

```csharp
            Expect(_audio.LoadedCount == 5, $"audio streams loaded {_audio.LoadedCount}/5");
```

- [ ] **Step 5: Build + smoke + listen**

`dotnet build citybuilder.sln` → green. `$env:CITYBUILDER_SMOKE = "1"; godot --headless .` → `SMOKE OK` (proves the five streams load headlessly). Launch `godot .` briefly interactively if audio hardware is available — draw a road, expect tick/click/plop; bulldoze, expect crunch (best-effort manual check; the load assert is the gate).

- [ ] **Step 6: Commit**

```bash
git add tools/sfxgen assets/audio src/Game/AudioFx.cs src/Game/ToolController.cs src/Game/Main.cs
git commit -m "feat(game): first audio — five synthesized UI one-shots (snap tick, place, commit, reject, bulldoze)"
```

---

### Task 11: VisualShots snap-indicator gallery

**Files:**
- Modify: `src/Game/VisualShots.cs`

**Interfaces:**
- Consumes: `GhostView.Show(placement, snap, handles, hot, edgeTangent, referenceDir, anchor)` (Task 8), `DraftSession`, `SnapEngine`.
- Produces: `Scenario` gains `Func<RoadNetwork, Node>? Extra = null` (a node added after Build, freed with the scenario); scenario `m675_snap_gallery` with five stations 200 m apart, one close-up shot each.

- [ ] **Step 1: Extend the Scenario record + Run loop**

Record:

```csharp
    private sealed record Scenario(string Name, Action<RoadNetwork> Build, Shot[] Shots, bool ShowLanes = false,
        Action<RoadNetwork, CityBuilder.Domain.Traffic.TrafficSim>? Traffic = null, int WarmupTicks = 0,
        Action<RoadNetwork, CityBuilder.Domain.Traffic.TrafficSim?, RoadNetworkView>? PostBuild = null,
        // extra scene content (e.g. GhostViews showing live snap states) added after
        // Build and freed with the scenario
        Func<RoadNetwork, Node>? Extra = null);
```

In `Run()`, after `scenario.PostBuild?.Invoke(...)`:

```csharp
                Node? extra = null;
                if (scenario.Extra is not null)
                {
                    extra = scenario.Extra(network);
                    AddChild(extra);
                }
```

and in the teardown block:

```csharp
                extra?.QueueFree();
```

- [ ] **Step 2: Add the gallery scenario**

Append to `Scenarios()` (before the closing brace) — five stations; each drives a real `DraftSession` into the exact snap state and hands its ghost state to its own `GhostView`:

```csharp
        yield return new Scenario("m675_snap_gallery", n =>
        {
            // station 1+2 (x≈0): node-capture ring + edge tick on a T junction
            Commit(n, Straight(new(-60, 0, 0), new(60, 0, 0)));
            Commit(n, Straight(new(0, 0, 0), new(0, 0, 60)));
            // station 3 (x≈200): two distant roads for the perpendicular guide crossing
            Commit(n, Straight(new(140, 0, 0), new(200, 0, 0)));
            Commit(n, Straight(new(250, 0, 60), new(330, 0, 60)));
            // station 4 (x≈400): edge for the perpendicular-arrival glyph
            Commit(n, Straight(new(360, 0, 0), new(460, 0, 0)));
        }, new[]
        {
            new Shot("node_ring", new NVec(55, 0, 2), 30, -50f, 20f),
            new Shot("edge_tick", new NVec(-30, 0, 0), 30, -50f, 20f),
            new Shot("guide_cross", new NVec(215, 0, 45), 70, -55f, 15f),
            new Shot("perp_glyph", new NVec(410, 0, 20), 55, -55f, 15f),
            new Shot("angle_ticks", new NVec(600, 0, 20), 60, -55f, 15f),
        }, Extra: n =>
        {
            var root = new Node3D { Name = "snap_gallery" };
            GhostView Station(Action<DraftSession> drive,
                Func<DraftSession, (NVec? edgeTan, NVec? refDir, NVec? anchor)>? extras = null)
            {
                var session = new DraftSession(n, new SnapEngine(n));
                session.SetMode(DraftMode.Straight);
                drive(session);
                var ghost = new GhostView();
                root.AddChild(ghost);
                // _Ready must run before Show — defer via a one-shot callable
                ghost.Ready += () =>
                {
                    var (tan, rd, anch) = extras?.Invoke(session) ?? (null, null, null);
                    ghost.Show(session.Ghost, session.LastSnap,
                        session.Draft?.Handles.Select(h => h.Position).ToArray(),
                        -1, tan, rd, anch);
                };
                return ghost;
            }

            // 1: hard node capture — cursor on the leg 3 m from the T node
            Station(s => s.PointerMoved(new NVec(57, 0, 0.5f), 6f));
            // 2: edge tick mid-span
            Station(s => s.PointerMoved(new NVec(-30, 0, 2f), 6f),
                s => (s.LastSnap.Edge is { } e ? n.Edges[e.Edge].Curve.Tangent(e.T) : null, null, null));
            // 3: guide intersection — perp guide off (200,0,0) × continuation of (250,0,60)
            Station(s => s.PointerMoved(new NVec(201, 0, 59), 6f));
            // 4: perpendicular arrival — draft started above the road, cursor near the foot
            Station(s =>
            {
                s.Click(new NVec(400, 0, 60), 6f);
                s.PointerMoved(new NVec(403, 0, 0.5f), 6f);
            }, s => (null, null, new NVec(400, 0, 60)));
            // 5: angle badge + 8 m cell ticks — free-space draft, 46° drag quantized
            Station(s =>
            {
                s.EnabledSnaps |= SnapTypes.CellLength;
                s.Click(new NVec(580, 0, 0), 6f);
                float rad = 46f * MathF.PI / 180f;
                s.PointerMoved(new NVec(580, 0, 0) + 27.3f * new NVec(MathF.Cos(rad), 0, MathF.Sin(rad)), 3f);
            }, s => (null, new NVec(1, 0, 0), new NVec(580, 0, 0)));

            return root;
        });
```

- [ ] **Step 3: Run the screenshot harness and READ the gallery shots**

`dotnet build citybuilder.sln` → green. `$env:CITYBUILDER_SHOTS = "tests/visual/shots"; godot .` → `SHOTS OK <N>` (N grows by 5). Read all five `m675_snap_gallery_*.png` with the Read tool and verify: (1) cyan ring around the T node, (2) tick bar across the road mid-span, (3) dashed guides + crossing dot + snap dot, (4) L-glyph at the perpendicular foot with the ghost strip arriving, (5) angle label "45°" + ≥1 cell tick crossing the ray. Fix and re-shoot until each reads correctly — screenshots are the evidence.

- [ ] **Step 4: Commit**

```bash
git add src/Game/VisualShots.cs tests/visual/shots
git commit -m "test(visual): m675_snap_gallery — five-station screenshot coverage of the snap indicators"
```

(If `tests/visual/shots` is gitignored, commit only the code — check `git status` first.)

---

### Task 12: Certification + docs (quality-stack DoD)

**Files:**
- Modify: `tests/Domain.Tests/Kpi/KpiSuiteTests.cs` (Milestone const)
- Create: `docs/health/M6.75.md` (generated) — plus regenerated `docs/health/kpi-latest.json`
- Modify: `docs/manual/06-drafting-snapping.md`, `docs/manual/07-rendering-markings.md`, `docs/manual/00-overview.md`, `docs/manual/glossary.md`
- Modify: `docs/conventions.md` (constants table), `docs/roadmap.md`
- Modify: `docs/superpowers/specs/2026-07-17-road-building-feel-design.md` (implementation-notes)

- [ ] **Step 1: Certification fuzz sweep**

Run: `$env:CITYBUILDER_FUZZ_ACTIONS = "10000"; dotnet test --filter "FullyQualifiedName~FuzzSuiteTests" -v n` (about 8 min)
Expected: 3 seeds × 10k green. Any finding: root-cause fix in domain code + pin in `FuzzRegressionTests.cs` before proceeding (see docs/verification.md).
Then `Remove-Item Env:CITYBUILDER_FUZZ_ACTIONS`.

- [ ] **Step 2: KPI regen**

In `KpiSuiteTests.cs` change `private const string Milestone = "M6.5";` → `"M6.75"`. Run `dotnet test --filter "FullyQualifiedName~KpiSuiteTests" -v n` → green (traffic untouched; expect ~zero drift vs baseline). `docs/health/M6.75.md` + `kpi-latest.json` regenerate. Append a "Ghost render cost" section to `docs/health/M6.75.md` with the Task 7 before/after `GHOSTPROBE avg_us` numbers.

- [ ] **Step 3: Manual + conventions + roadmap + spec notes**

- `docs/manual/06-drafting-snapping.md`: document hard capture ring (0.6×, nearest node wins outright), hysteresis (1.4× release, session-threaded `HeldNode`), cell-length ticks (8 m, `SnapKind.CellLength`, angle composition), perpendicular guides (+cap 48), the new default-snaps note, and the `HandlePlaced/Committed/Rejected` events.
- `docs/manual/07-rendering-markings.md`: GhostView pooling model (hidden-not-freed, placement-reference dirty flag) + indicator table (mirror spec §4).
- `docs/manual/00-overview.md`: add `AudioFx` to the Game module list (one line).
- `docs/manual/glossary.md`: entries for *capture ring*, *release ring / hysteresis*, *cell tick*.
- `docs/conventions.md` constants table — add rows:
  `SnapEngine.NodeCaptureFraction = 0.6`, `SnapEngine.ReleaseFactor = 1.4`, `SnapEngine.CellLength = 8 m`, `SnapEngine.WeightCellLength = 1.2`, `SnapEngine.MaxGuidelines = 48`.
- `docs/roadmap.md`: add the **M6.75 — Road-building feel** Done entry (date, the five features, headline: T-junction hard capture + hysteresis, before/after ghost µs, "first audio"), and renumber/adjust the "Next up" intro (M7 undo/redo + upgrade unchanged).
- Spec: append an **Implementation notes** section: (a) ghost dirty-skip implemented as placement-reference cache in GhostView instead of a session revision counter (same effect, fewer moving parts); (b) audio synthesized in-repo via `tools/sfxgen` instead of Kenney download (deterministic, license-trivial); (c) angle badge rendered as label + cell ticks (no arc mesh).

- [ ] **Step 4: Full gate run**

`dotnet test -v q` → all green. `dotnet build citybuilder.sln` → green. `$env:CITYBUILDER_SMOKE = "1"; godot --headless .` → `SMOKE OK`. `$env:CITYBUILDER_UITEST = "$env:TEMP\ui_final.png"; godot .` → `UITEST OK`, read the PNG. `$env:CITYBUILDER_SHOTS = "tests/visual/shots"; godot .` → `SHOTS OK`.

- [ ] **Step 5: Final commit**

```bash
git add -A
git commit -m "docs+cert: M6.75 road-building feel — fuzz 3x10k green, KPI M6.75 health report, manual/conventions/roadmap drift"
```

---

## Self-Review

- **Spec coverage:** §1 hard capture → T1, hysteresis → T2, angle guard → T3; §2 cell ticks → T4 (+toolbar T9, fuzzer T4); §3 perp guides + cap → T5; §4 indicators → T8 (+gallery T11); §5 pooling + probe → T7; §6 audio → T6 (events) + T10; testing/DoD → every task's steps + T12. Deviations recorded in T12's spec-notes step.
- **Type consistency:** `SnapContext.HeldNode` (T2) matches T2's session threading; `Sfx`/`AudioFx.Play`/`BindAudio` names consistent across T10; `GhostView.Show` 7-parameter form defined in T8, consumed in T8 (ToolController) and T11 (gallery); `QuantizeToCell` defined and used only in T4.
- **Placeholder scan:** none — all code inline.
