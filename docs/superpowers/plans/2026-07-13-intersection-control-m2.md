# Intersection Control & Customization Implementation Plan (M2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Configurable junction right-of-way (priority/yield/stop/all-way/traffic lights) with matching paint, signs and light props, right-of-way tags on lane connectors, and Node-Controller-style junction resizing (per node + per leg) with an in-game inspector panel.

**Architecture:** Authored control state lives as `JunctionConfig` on `RoadNode` (domain source of truth); `JunctionControl.Resolve` computes effective mode + per-leg roles (heuristic when `Auto`). Geometry (`JunctionBuilder`), lane graph (`ConnectorBuilder`), paint (`JunctionMarkings`) and props (`JunctionProps`) all read resolved control. Mutations go through `RoadNetwork.ConfigureJunction` which raises the existing `Changed` delta so the Godot view rebuilds.

**Tech Stack:** C# (net8.0 domain, System.Numerics; Godot 4.6.2 mono view), xUnit (net10.0), screenshot harness (`CITYBUILDER_SHOTS`).

## Global Constraints

- Domain (`src/Domain`) must stay free of Godot dependencies.
- 1 unit = 1 m, Y up, right-hand traffic, lane offset > 0 = driver's right.
- Resize clamp: per-leg `extra = clamp(SizeOffset + LegOffsets[edge], −(CornerMargin−0.05), +12)`; the 30 %-of-edge-length clamp and `TightCuts` stay on top. Degree ≥ 2 only.
- Verify with `dotnet test`, `dotnet build citybuilder.sln`, `CITYBUILDER_SMOKE=1 godot --headless .`, and `CITYBUILDER_SHOTS=tests/visual/shots godot .` + Read of the PNGs for visual tasks.
- Commit after every task.

---

### Task 1: Control types + effective-control resolution

**Files:**
- Create: `src/Domain/Network/JunctionControl.cs`
- Modify: `src/Domain/Network/Entities.cs` (RoadNode.Config)
- Test: `tests/Domain.Tests/Network/JunctionControlTests.cs`

**Interfaces:**
- Produces: `JunctionControlMode { Auto, None, PrioritySigns, AllWayStop, TrafficLights }`, `LegRole { Main, Yield, Stop }`, `RightOfWay { Free, Yield, Stop, Signal }`, `record JunctionConfig(JunctionControlMode Mode, IReadOnlyDictionary<EdgeId, LegRole> RoleOverrides, float SizeOffset, IReadOnlyDictionary<EdgeId, float> LegOffsets)` with `static JunctionConfig Default`, `record EffectiveControl(JunctionControlMode Mode, IReadOnlyDictionary<EdgeId, LegRole> Roles)`, `static EffectiveControl JunctionControl.Resolve(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)`, `RoadNode.Config { get; internal set; } = JunctionConfig.Default`.

- [ ] **Step 1: Write failing tests** — degree-2 resolves `None` even when config says lights; Auto 4-way TwoLane×Street: wider street pair is Main, others Yield; Auto tee (straight pair same type): collinear pair Main, stub Yield; role override flips a Main leg to Stop; override for unknown edge ignored; AllWayStop mode roles unused but resolve returns mode.

```csharp
using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class JunctionControlTests
{
    private static RoadNetwork Cross(out RoadNode node, RoadTypeId? ns = null)
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-80, 0, 0), new Vector3(80, 0, 0), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -80), new Vector3(0, 0, 80), ns));
        node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        return n;
    }

    [Fact]
    public void BendResolvesToNone()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-50, 0, 0), new Vector3(0, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(0, 0, 40)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 2);
        node.Config = JunctionConfig.Default with { Mode = JunctionControlMode.TrafficLights };
        Assert.Equal(JunctionControlMode.None, JunctionControl.Resolve(node, n.Edges).Mode);
    }

    [Fact]
    public void AutoPicksWiderRoadAsMain()
    {
        var n = Cross(out var node); // Street (E-W) x TwoLane (N-S)
        var eff = JunctionControl.Resolve(node, n.Edges);
        Assert.Equal(JunctionControlMode.PrioritySigns, eff.Mode);
        foreach (var eid in node.Edges)
        {
            var isStreet = n.Edges[eid].Type == RoadCatalog.Street.Id;
            Assert.Equal(isStreet ? LegRole.Main : LegRole.Yield, eff.Roles[eid]);
        }
    }

    [Fact]
    public void AutoPicksStraightPairAsMainInTee()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-80, 0, 0), new Vector3(80, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(0, 0, 80)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 3);
        var eff = JunctionControl.Resolve(node, n.Edges);
        int mains = node.Edges.Count(e => eff.Roles[e] == LegRole.Main);
        Assert.Equal(2, mains);
        // the stub (leg pointing +Z from the node) must be the yielding one
        var stub = node.Edges.Single(e =>
        {
            var edge = n.Edges[e];
            return MathF.Abs(edge.Curve.P0.Z - edge.Curve.P3.Z) > 1f;
        });
        Assert.Equal(LegRole.Yield, eff.Roles[stub]);
    }

    [Fact]
    public void RoleOverrideWins()
    {
        var n = Cross(out var node);
        var main = node.Edges.First(e => n.Edges[e].Type == RoadCatalog.Street.Id);
        node.Config = JunctionConfig.Default with
        {
            RoleOverrides = new Dictionary<EdgeId, LegRole> { [main] = LegRole.Stop },
        };
        Assert.Equal(LegRole.Stop, JunctionControl.Resolve(node, n.Edges).Roles[main]);
    }
}
```

- [ ] **Step 2: Run** `dotnet test --filter JunctionControlTests` — expect compile FAIL (types missing).
- [ ] **Step 3: Implement** `JunctionControl.cs`:

```csharp
namespace CityBuilder.Domain.Network;

public enum JunctionControlMode { Auto, None, PrioritySigns, AllWayStop, TrafficLights }
public enum LegRole { Main, Yield, Stop }
public enum RightOfWay { Free, Yield, Stop, Signal }

public sealed record JunctionConfig(
    JunctionControlMode Mode,
    IReadOnlyDictionary<EdgeId, LegRole> RoleOverrides,
    float SizeOffset,
    IReadOnlyDictionary<EdgeId, float> LegOffsets)
{
    public static readonly JunctionConfig Default = new(
        JunctionControlMode.Auto,
        new Dictionary<EdgeId, LegRole>(),
        0f,
        new Dictionary<EdgeId, float>());
}

public sealed record EffectiveControl(
    JunctionControlMode Mode,
    IReadOnlyDictionary<EdgeId, LegRole> Roles);

public static class JunctionControl
{
    public static EffectiveControl Resolve(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)
    {
        var ids = node.Edges.OrderBy(e => e.Value).ToArray();
        var none = new EffectiveControl(JunctionControlMode.None,
            ids.ToDictionary(e => e, _ => LegRole.Main));
        if (ids.Length <= 2)
            return none;

        var mode = node.Config.Mode == JunctionControlMode.Auto
            ? JunctionControlMode.PrioritySigns
            : node.Config.Mode;
        if (mode != JunctionControlMode.PrioritySigns)
            return new EffectiveControl(mode, ids.ToDictionary(e => e, _ => LegRole.Main));

        var (mainA, mainB) = MainPair(node, edges, ids);
        var roles = ids.ToDictionary(
            e => e,
            e => e == mainA || e == mainB ? LegRole.Main : LegRole.Yield);
        foreach (var (eid, role) in node.Config.RoleOverrides)
            if (roles.ContainsKey(eid))
                roles[eid] = role;
        return new EffectiveControl(mode, roles);
    }

    private static (EdgeId, EdgeId) MainPair(
        RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges, EdgeId[] ids)
    {
        // widest carriageway pair wins; straightest continuation breaks ties
        (EdgeId, EdgeId) best = (ids[0], ids[1]);
        (float width, float straight) bestScore = (float.MinValue, float.MinValue);
        for (int i = 0; i < ids.Length; i++)
        for (int j = i + 1; j < ids.Length; j++)
        {
            float w = Catalog.RoadCatalog.Get(edges[ids[i]].Type).CarriagewayHalf
                    + Catalog.RoadCatalog.Get(edges[ids[j]].Type).CarriagewayHalf;
            float s = -System.Numerics.Vector2.Dot(LeavingDir(edges[ids[i]], node), LeavingDir(edges[ids[j]], node));
            if (w > bestScore.width + 0.01f
                || (MathF.Abs(w - bestScore.width) <= 0.01f && s > bestScore.straight))
            {
                bestScore = (w, s);
                best = (ids[i], ids[j]);
            }
        }
        return best;
    }

    private static System.Numerics.Vector2 LeavingDir(RoadEdge edge, RoadNode node)
    {
        var t = edge.StartNode == node.Id ? edge.Curve.Tangent(0) : -edge.Curve.Tangent(1);
        var d = new System.Numerics.Vector2(t.X, t.Z);
        return d.LengthSquared() > 0 ? System.Numerics.Vector2.Normalize(d) : System.Numerics.Vector2.UnitX;
    }
}
```

In `Entities.cs`, add to `RoadNode`: `public JunctionConfig Config { get; internal set; } = JunctionConfig.Default;` — note tests set it directly via `internal` (tests already have InternalsVisibleTo? If not, make the setter `public` — check `Domain.csproj`; if no InternalsVisibleTo exists, use a public setter and treat `ConfigureJunction` as the blessed path).

- [ ] **Step 4: Run** `dotnet test` — all pass.
- [ ] **Step 5: Commit** `feat(domain): junction control types + effective-control resolution`.

### Task 2: ConfigureJunction mutation + pruning

**Files:**
- Modify: `src/Domain/Network/RoadNetwork.cs`
- Test: `tests/Domain.Tests/Network/JunctionControlTests.cs` (extend)

**Interfaces:**
- Produces: `void RoadNetwork.ConfigureJunction(NodeId id, JunctionConfig config)` — throws on unknown node; prunes `RoleOverrides`/`LegOffsets` entries whose edge is not connected; stores config; rebuilds junction geometry + connectors for the node; increments `Version`; raises `Changed` with the node in `NodesChanged`.
- Also: topology changes that touch a node (split/heal/remove) prune stale entries from its config.

- [ ] **Step 1: Write failing tests** — ConfigureJunction raises Changed with node in NodesChanged and bumps Version; unknown-edge overrides pruned on store; bulldozing a leg prunes its override; unknown node throws.
- [ ] **Step 2: Run to verify FAIL.**
- [ ] **Step 3: Implement.** In `RoadNetwork`, add public method; find the existing per-node rebuild helper used by Commit/RemoveEdge (junction + connectors rebuild) and reuse it; in the topology paths where a node's edge set changes, add `node.Config = Prune(node.Config, node.EdgeSet)`.

```csharp
public void ConfigureJunction(NodeId id, JunctionConfig config)
{
    if (!_nodes.TryGetValue(id, out var node))
        throw new ArgumentException($"unknown node {id}");
    node.Config = Prune(config, node.EdgeSet);
    RebuildNode(node);          // existing junction+connector rebuild helper
    Version++;
    Changed?.Invoke(new NetworkDelta(
        new HashSet<EdgeId>(), new HashSet<EdgeId>(),
        new HashSet<NodeId>(), new HashSet<NodeId>(),
        new HashSet<NodeId> { id }));
}

private static JunctionConfig Prune(JunctionConfig c, IReadOnlySet<EdgeId> edges)
    => c with
    {
        RoleOverrides = c.RoleOverrides.Where(kv => edges.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value),
        LegOffsets = c.LegOffsets.Where(kv => edges.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value),
    };
```

(Adapt `RebuildNode` name to whatever the existing helper is; also ensure Changed batching used by Commit isn't required here — a direct delta is fine.)

- [ ] **Step 4: Run** `dotnet test` — pass.
- [ ] **Step 5: Commit** `feat(domain): ConfigureJunction with config pruning + change events`.

### Task 3: Junction resizing in JunctionBuilder

**Files:**
- Modify: `src/Domain/Network/JunctionBuilder.cs` (finalize-cuts loop)
- Test: `tests/Domain.Tests/Network/JunctionResizeTests.cs`

**Interfaces:**
- Consumes: `node.Config.SizeOffset`, `node.Config.LegOffsets`.
- Produces: cuts move by the clamped extra distance; constant `MaxExtra = 12f`.

- [ ] **Step 1: Write failing tests:**

```csharp
[Fact]
public void SizeOffsetGrowsCuts()
{
    var n = Cross(out var node);                       // helper as in JunctionControlTests
    float before = CutDist(n, node, node.Edges.First());
    n.ConfigureJunction(node.Id, node.Config with { SizeOffset = 4f });
    node = n.Nodes[node.Id];
    Assert.Equal(before + 4f, CutDist(n, node, node.Edges.First()), 1);
}

[Fact]
public void ShrinkFloorsAtSolvedCorner()
{
    var n = Cross(out var node);
    float before = CutDist(n, node, node.Edges.First());
    n.ConfigureJunction(node.Id, node.Config with { SizeOffset = -10f }); // clamped to margin
    Assert.True(CutDist(n, node, node.Edges.First()) >= before - JunctionBuilderMarginPlusEpsilon);
}

[Fact]
public void PerLegOffsetMovesOnlyThatLeg() { /* LegOffsets[legE]=6, others unchanged */ }

[Fact]
public void ThirtyPercentClampStillWins() { /* short edge + big offset → TightCuts contains edge */ }
```

(`CutDist` = distance from node position to `edge.Curve.Point(CutT[edge])`.)

- [ ] **Step 2: Run to verify FAIL** (cuts don't move yet).
- [ ] **Step 3: Implement** in the finalize-cuts loop of `Build`:

```csharp
private const float MaxExtra = 12f;
...
foreach (var leg in legs)
{
    float len = leg.Edge.ArcLength.TotalLength;
    float extra = node.Config.LegOffsets.TryGetValue(leg.Edge.Id, out var lo) ? lo : 0f;
    extra = Math.Clamp(node.Config.SizeOffset + extra, -(CornerMargin - 0.05f), MaxExtra);
    float wanted = leg.CutDistance + CornerMargin + extra;
    leg.CutDistance = MathF.Min(wanted, len * MaxCutFraction);
    if (leg.CutDistance < wanted - 1e-3f)
        tightCuts.Add(leg.Edge.Id);
    cutT[leg.Edge.Id] = leg.TAtCutDistance(leg.CutDistance);
}
```

Degree-1 path stays untouched (no resize on dead ends); update the spec's dead-end sentence to match.

- [ ] **Step 4: Run** `dotnet test` — pass, including all existing junction/corner tests.
- [ ] **Step 5: Commit** `feat(domain): junction resizing via SizeOffset + per-leg offsets`.

### Task 4: Right-of-way tags on lane connectors

**Files:**
- Modify: `src/Domain/Network/Entities.cs` (LaneConnector record), `src/Domain/Network/ConnectorBuilder.cs`
- Test: `tests/Domain.Tests/Network/ConnectorRowTests.cs`

**Interfaces:**
- Produces: `LaneConnector` gains `RightOfWay Row = RightOfWay.Free` (last positional param with default); connectors entering a node are tagged: None→Free, PrioritySigns→role map (Main→Free, Yield→Yield, Stop→Stop), AllWayStop→Stop, TrafficLights→Signal; dead-end U-turns stay Free.

- [ ] **Step 1: Write failing tests** — Auto cross: connectors from TwoLane legs are Yield, from Street legs Free; ConfigureJunction(AllWayStop) → all Stop; TrafficLights → all Signal; dead-end U-turn Free.
- [ ] **Step 2: Run to verify FAIL.**
- [ ] **Step 3: Implement** — in `ConnectorBuilder.Build` (per node), resolve `var eff = JunctionControl.Resolve(node, edges);` once, map the incoming lane's edge to `eff.Roles[edge]`, translate to `RightOfWay`, pass into the `LaneConnector` constructor.
- [ ] **Step 4: Run** `dotnet test` — pass.
- [ ] **Step 5: Commit** `feat(domain): right-of-way tags on lane connectors`.

### Task 5: Control paint — shark teeth, conditional stop lines

**Files:**
- Modify: `src/Game/JunctionMarkings.cs`
- Modify: `src/Game/VisualShots.cs` (scenarios `priority_tee`, `allway_cross`)

**Interfaces:**
- Consumes: `JunctionControl.Resolve`; existing `AddStopLine`-equivalent code path per incoming leg.
- Produces: per-leg paint dispatch — Main: no stop line; Yield: `AddYieldTeeth` (triangles base 0.6, height 0.7, gap 0.3, tip toward junction... pointing at incoming traffic means tips face away from the junction, toward the approaching driver); Stop/AllWayStop/TrafficLights: existing stop line; None: nothing.

- [ ] **Step 1: Add scenarios first** (visual TDD): `priority_tee` = Street E-W + TwoLane stub with default Auto; `allway_cross` = Street cross with `ConfigureJunction(AllWayStop)` — scenario Build delegates can call `n.ConfigureJunction(...)` after commits. Shots: top + oblique each.
- [ ] **Step 2: Run shots, confirm current state** (stop lines everywhere — the "failing" baseline).
- [ ] **Step 3: Implement paint dispatch.** Where stop lines are currently emitted per incoming leg, branch on the resolved role:

```csharp
var eff = JunctionControl.Resolve(node, edges);
...
switch (RoleFor(eff, edge.Id))
{
    case LegRole.Main when eff.Mode == JunctionControlMode.PrioritySigns:
        break;                                   // priority road: no line
    case LegRole.Yield when eff.Mode == JunctionControlMode.PrioritySigns:
        AddYieldTeeth(st, edge, type, tCut, startsHere);
        break;
    default:
        AddStopLine(...);                        // existing behavior
        break;
}
```

`AddYieldTeeth` mirrors the stop-line placement math (same lateral span across incoming
lanes at the cut) but emits triangles: along the span, tooth every 0.9 m (0.6 base + 0.3
gap), apex 0.7 m toward the approaching traffic (i.e. away from the junction interior).

- [ ] **Step 4: Run** `dotnet test` (still green), rebuild, rerun shots, Read `priority_tee_*` and `allway_cross_*` — teeth on the stub, nothing on the mains, four stop lines on all-way. Also Read `street_cross_*`/`asym_tee_*` to check defaults changed sensibly (mains lost their stop lines — intended).
- [ ] **Step 5: Commit** `feat(game): yield teeth + role-driven stop lines`.

### Task 6: Street furniture — signs and traffic lights

**Files:**
- Create: `src/Game/JunctionProps.cs`
- Modify: `src/Game/RoadNetworkView.cs` (build props with junction mesh), `src/Game/Materials.cs` (sign/pole materials), `src/Game/VisualShots.cs` (scenario `lights_cross`)

**Interfaces:**
- Produces: `static IEnumerable<MeshInstance3D> JunctionProps.Build(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)` — per incoming leg: PrioritySigns → yield triangle / stop octagon / main diamond on 2.4 m pole; TrafficLights → 4 m pole + 3-lamp head facing the leg. Placement: at the cut, driver's right, lateral `OuterHalf − 0.5` (or `CarriagewayHalf + 0.4` when `!HasSidewalks`).

- [ ] **Step 1: Add `lights_cross` scenario** (Street cross + `ConfigureJunction(TrafficLights)`), shots top/oblique/low (`new Shot("low", target, 26, -18f, 40f)`).
- [ ] **Step 2: Implement props** with SurfaceTool primitives: pole = 8-sided cylinder r=0.06; yield sign = triangle plate (two-sided) 0.75 side; stop sign = octagon r=0.4; main diamond = square plate 0.55 rotated 45°; light head = 0.3×0.9×0.25 box + three 0.1-radius disc lamps (red/amber/green emissive materials). Orient plates perpendicular to the leg direction, facing incoming traffic.
- [ ] **Step 3: Wire into `RoadNetworkView`** node rebuild (props children tracked and freed with the junction mesh).
- [ ] **Step 4: Verify** — `dotnet build citybuilder.sln` 0 errors; rerun shots; Read `lights_cross_*` (poles with 3 lamps at each corner, right side of each approach), `priority_tee_*` (triangle on stub, diamonds on mains). Check no z-fighting/floating (poles sit on sidewalk, y from SidewalkRise).
- [ ] **Step 5: Commit** `feat(game): junction signs and traffic light props`.

### Task 7: Junction inspector tool + panel

**Files:**
- Create: `src/Game/JunctionPanel.cs`
- Modify: `src/Game/ToolController.cs` (ToolMode.Inspect, node picking, selection events), `src/Game/Toolbar.cs` (button), `src/Game/Main.cs` (panel wiring + smoke extension)

**Interfaces:**
- Consumes: `RoadNetwork.ConfigureJunction`, `JunctionControl.Resolve`.
- Produces: `ToolMode.Inspect`; `event Action<NodeId?> ToolController.NodeSelected`; `JunctionPanel.Bind(RoadNetwork network)` + `Show(NodeId)`/`Hide()`; selection highlight (line strip over `SurfacePolygon`, ImmediateMesh, `MarkingY + 0.02`).

- [ ] **Step 1: ToolController** — `Inspect` mode: `HandleClickAt` picks `FindNodeNear(world, snapRadius)` (fall back: nearest node of `FindClosestEdge`); raises `NodeSelected`; hover shows node highlight. Mode switch or bulldoze of the node clears selection.
- [ ] **Step 2: JunctionPanel** (right-anchored `PanelContainer` in the UI CanvasLayer): mode `OptionButton` (Auto/Priority signs/All-way stop/Traffic lights/None), per-leg rows (`{roadtype} {bearing}`: role cycle `Button` Main→Yield→Stop, `SpinBox` −0.5…12 step 0.5 for leg offset), node `HSlider` −0.5…12 step 0.5 with value label, `Reset` button. Every change: build new `JunctionConfig`, call `ConfigureJunction`, refresh rows from `Resolve` so heuristic roles display live. Role buttons disabled unless mode == Priority signs (Auto shows resolved roles greyed).
- [ ] **Step 3: Smoke extension** in `Main.RunSmoke`: after the bulldoze step, `_network.ConfigureJunction(teeNode, JunctionConfig.Default with { Mode = TrafficLights, SizeOffset = 3f })`; assert every incoming connector at that node has `Row == Signal` and the cut distance grew by ~3 (read `CutT` before/after); assert `_view` rebuilt without errors (`FlushDirty`).
- [ ] **Step 4: Verify** — `dotnet test`, build, `CITYBUILDER_SMOKE=1 godot --headless .` prints SMOKE OK. Manual-equivalent: temporary shot? No — panel is interactive-only; verify via smoke + one screenshot scenario `resized_cross` in Task 8.
- [ ] **Step 5: Commit** `feat(game): junction inspector tool and panel`.

### Task 8: Resize visuals + full verification sweep

**Files:**
- Modify: `src/Game/VisualShots.cs` (scenarios `resized_cross`, `asym_resize`)
- Modify: `docs/superpowers/specs/2026-07-13-intersection-control-design.md` (dead-end resize note)

- [ ] **Step 1: Scenarios** — `resized_cross`: Street cross, `ConfigureJunction(SizeOffset: 6)`; `asym_resize`: Street cross, one leg `LegOffsets +6`. Shots top + oblique; plus corner close-up on `resized_cross`.
- [ ] **Step 2: Run the full harness** and Read every new/changed scenario (resized junction: bigger footprint, crosswalks/teeth/stop lines and corner zones follow the moved cuts, no gaps between edge mesh and junction at the new cut, guidance curves still sane) and spot-check old scenarios (`street_cross`, `avenue_mix`, `short_block`, `boulevard_grid`) for regressions.
- [ ] **Step 3: Fix what the screenshots reveal** (iterate until clean).
- [ ] **Step 4: Full gate** — `dotnet test`, `dotnet build citybuilder.sln`, smoke, shots all green.
- [ ] **Step 5: Commit** `feat: resizable junction scenarios + verification sweep`, update memory notes.

## Self-review

- Spec coverage: control types/resolution (T1), mutation+events (T2), resize (T3), lane-graph tags (T4), paint (T5), props (T6), UI (T7), visual scenarios + sweep (T8). Persistence/signal timing/lane-connector editing are spec non-goals. ✓
- Dead-end resize contradiction fixed by T3/T8 spec note. ✓
- Type consistency: `JunctionConfig` shape, `Resolve` signature, `Row` param used consistently across tasks. ✓
