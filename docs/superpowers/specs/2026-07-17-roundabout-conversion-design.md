# M7.5 — Roundabout conversion (design)

**Date:** 2026-07-17
**Status:** approved (Approach A, live entity)
**Milestone:** M7.5 mini-milestone, before M8 (Elevation & bridges)

## Problem

Today the only way to make a roundabout is to hand-build a ring out of individual
one-way arc segments (the CS1 workflow) and then wire approach roads into it. That is
fiddly and — as the user hit directly — often produces **red, unbuildable ghosts**: an
approach landing on a ring segment splits it near an existing node, tripping the
sliver/sharp-leg guards in `RoadNetwork.Validate` even when the geometry looks fine. The
root cause is that the generic edge-split validation was never meant to model "join a
ring"; it sees a short leftover segment or a shallow leg and refuses.

CS2 solves this with a different workflow entirely: **you build a normal intersection
first, then convert it to a roundabout in place.** This spec adopts that model.

## Goal

A junction (any node of degree ≥ 3) can be **converted to a roundabout**: its center
node is replaced by a circular one-way ring, each former leg becomes a clean approach
that yields on entry, and circulating traffic has priority. The roundabout is a **live
entity** — its radius is editable afterward, and bulldozing an approach re-arcs the ring
automatically — not a one-shot desugaring the player then has to maintain by hand.

Crucially, this is built **on top of the existing road graph**: ring nodes and edges are
ordinary `RoadNode`/`RoadEdge` records (ring edges use the existing `OneWay` catalog
type), so traffic routing, IDM following, junction arbitration, rendering, and the fuzzer
all work with **zero changes** to the simulation. Roundabout behaviour (yield-on-entry,
circulating priority) falls out of the M2 junction-control and M5 arbitration machinery
already shipped. All new risk is concentrated in one place: the conversion/regeneration
**geometry**.

## Non-goals (this milestone)

- **Adding a brand-new approach to an *existing* ring by drawing a road into it.** This is
  the trickiest live trigger (it needs roundabout-aware draft validation to avoid the very
  red-ghost path we are escaping) and is **deferred to a fast-follow**. To change the leg
  set in v1 the player bulldozes an approach (ring re-arcs) or removes and re-converts.
  Conversion itself already covers the user's original pain (build a 4-way, convert → four
  clean approaches).
- Dedicated multi-lane ring cross-sections, turbo-roundabouts, or a mini-roundabout style.
  Every ring uses `OneWay` (2 same-direction driving lanes + sidewalks, 12 m). Lane-count
  variety is a later milestone.
- Pedestrian crossings / zebra placement around the ring (rides on the future Citizens
  milestone).
- Saving in-flight vehicles across a conversion (vehicles resync as after a quickload,
  consistent with M7 undo).

## Architecture

Three isolated units, each independently testable:

### 1. `RoundaboutPlanner` — pure geometry (no mutation)

`src/Domain/Network/RoundaboutPlanner.cs`. A pure function:

```
RoundaboutPlan Plan(Vector3 center, float radius, IReadOnlyList<ApproachLeg> legs)
```

`ApproachLeg` describes one former leg as seen from the center: its `EdgeId`, the curve,
which end sits at the center, and its **bearing** θ (the angle around the center at which
it departs). `Plan` produces a `RoundaboutPlan`:

- **Ring nodes**, one per leg, positioned on the circle of `radius` at each leg's bearing,
  returned in **counter-clockwise order** (right-hand traffic circulates CCW).
- **Ring arcs**: one `OneWay` `Bezier3` per adjacent ring-node pair, directed CCW
  (`RingEdges[i]` runs `RingNodes[i] → RingNodes[i+1]`, wrapping). Arc curves come from
  `BezierOps.ArcFromTangent` / a circular-arc cubic so they lie on the circle.
- **Per-leg trim**: each approach edge trimmed (de Casteljau, reusing `SubCurve`) so its
  inner end lands exactly on its ring node instead of the old center.
- **Feasibility**: the minimum radius at which every ring segment clears
  `OneWay.MinSegmentLength` (12 m) given the bearings, plus per-leg validity (a leg whose
  trimmed remainder falls below its type's `MinSegmentLength`, or whose endpoint sits
  inside the ring circle, is flagged). `Plan` returns a `RoundaboutError?` describing the
  first blocking problem (`RadiusTooTight`, `LegTooShort`, `LegInsideRing`,
  `DegenerateBearings` when two legs share a bearing within tolerance).

This is where all the hard geometry correctness lives, and it is a pure function of
`(center, radius, legs)` — unit-tested exhaustively (even spacing, uneven spacing, T
junction, near-collinear legs, radius clamping) without ever touching the network.

### 2. `RoundaboutRegistry` — network-owned entities + graph surgery

A new partial class file `src/Domain/Network/RoadNetwork.Roundabouts.cs` plus a
`Roundabout` entity and `RoundaboutId`.

```
public readonly record struct RoundaboutId(int Value);           // Ids.cs

public sealed record Roundabout(
    RoundaboutId Id, Vector3 Center, float Radius,
    IReadOnlyList<NodeId> RingNodes,   // CCW
    IReadOnlyList<EdgeId> RingEdges);  // CCW, RingEdges[i]: RingNodes[i] -> RingNodes[i+1]
```

The network gains `Dictionary<RoundaboutId, Roundabout> _roundabouts`, a monotonic
`_nextRoundabout` counter, and a membership tag `RoundaboutId? Ring` on `RoadNode`
(marking ring nodes so edits can find their owner). Public API, all batched exactly like
`Commit`/`RemoveEdge` (one `Changed` event per call):

- `RoundaboutResult ConvertToRoundabout(NodeId center, float radius)` — preconditions:
  `center` exists, degree ≥ 3, is not already a ring node, and none of its legs belong to
  another roundabout. Gathers the legs, calls `RoundaboutPlanner.Plan`; on a planner error
  returns `RoundaboutResult.Failed(reason)` **without mutating**. On success, in one batch:
  trim each leg edge in place (same `EdgeId`, preserving junction authoring — the M7
  philosophy), create the ring nodes/edges, delete the old center node, set each ring
  node's `JunctionConfig` to make the approach leg **Yield** and the two ring legs **Main**
  (`PrioritySigns` mode), register the `Roundabout`, and tag the ring nodes.
- `RoundaboutResult SetRoundaboutRadius(RoundaboutId id, float radius)` — regenerate at a
  new radius, same legs.
- `void RemoveRoundabout(RoundaboutId id)` — dissolve: delete ring edges/nodes, leave the
  approach legs as free-ended stubs (they simply end where the ring was). Does **not**
  attempt to reconstruct the original single junction — undo is the way back to that.
- Internal `Regenerate(id)` — the shared tear-down-and-rebuild used by radius change and
  by leg removal; also the **dissolve-on-underflow** rule: if regeneration finds fewer
  than 3 surviving legs the roundabout auto-dissolves (a 2-leg "roundabout" is just a bend).
- `Roundabout? RoundaboutForNode(NodeId)` / `RoundaboutForEdge(EdgeId)` — lookups for the
  trigger layer and the inspector.

**Live re-arcing on bulldoze.** `RemoveEdge` already runs `HandleNodeAfterRemoval` on the
affected nodes. When a removed edge was an **approach leg** of a roundabout (its far node
is a ring node), the registry marks that roundabout dirty and regenerates it in a **second
batch** after the removal batch closes (avoids nested batches / re-entrancy). Ring nodes
are exempt from `TryHealNode` — a degree-2 ring node is normal and must never heal its two
ring arcs into one.

### 3. Editor surface — `JunctionPanel` + `ToolController`

No new tool mode; roundabouts are authored from the existing junction **Inspect** flow:

- When a plain node of degree ≥ 3 is selected, `JunctionPanel` shows a **"Convert to
  roundabout"** button and a radius spinner (default 20 m, min = planner's feasible
  minimum, max ~60 m).
- When a **ring node / roundabout** is selected, the panel instead shows the **radius
  slider** (live `SetRoundaboutRadius`) and a **"Remove roundabout"** button.
- Every conversion / radius change / removal is `UndoStack.Checkpoint()`-ed before the
  mutation (wired through the panel's existing `_beforeMutate` hook, same as junction
  config edits today). Snapshot undo therefore covers roundabouts for free once they are in
  the save format.

## Data flow

```
Inspect-select node ──> JunctionPanel
   │  (degree ≥ 3, plain)         │  (ring node)
   ▼                              ▼
"Convert" + radius            radius slider / "Remove"
   │                              │
   ▼                              ▼
checkpoint(); ConvertToRoundabout / SetRoundaboutRadius / RemoveRoundabout
   │
   ▼
RoundaboutPlanner.Plan (pure)  ──error──> RoundaboutResult.Failed (no mutation, status flash)
   │ ok
   ▼
one batch: trim legs (same EdgeId) · add ring nodes/edges (OneWay, CCW) ·
           delete center · set ring JunctionConfigs (approach=Yield, ring=Main) ·
           register Roundabout · tag ring nodes
   ▼
EndBatch → RebuildDerived(ring nodes) → connectors + arbitration (existing M4/M5 code)
   ▼
one NetworkDelta → RoadNetworkView re-meshes → yield-on-entry behaviour emerges
```

Bulldoze of an approach: `RemoveEdge` batch closes → registry sees a dirty roundabout →
`Regenerate` in a second batch (or auto-dissolve if < 3 legs remain).

## Traffic behaviour (no sim changes — verified, not built)

At each ring node the resolved control is `PrioritySigns` with the approach leg = `Yield`,
both ring legs = `Main`. The existing `ConnectorBuilder` + M5 arbitration then give:

- approach-in → ring: `RightOfWay.Yield` (must accept a gap) — **yield on entry**;
- ring-in → ring-out: `Free` — **circulating priority**;
- ring-in → approach-out: the exit movement.

The entry and circulating-through movements merge onto the same outbound ring edge; the M5
merge/conflict-point machinery already arbitrates that. We add a **behaviour regression
test** (a car on the ring is never yielded to by circulating traffic; an entering car
waits for a gap) and a **KPI scenario** (roundabout throughput / delay), but no new sim
code is expected. If a gap in control resolution shows up, the fix is a `JunctionConfig`
adjustment, not new arbitration.

## Persistence

`SaveGame.FormatVersion` → **2**. Add `RoundaboutDto[] Roundabouts` to `SaveGame`:

```
RoundaboutDto(int Id, float CX, float CY, float CZ, float Radius,
              int[] RingNodeIds /* CCW */, int[] RingEdgeIds /* CCW */)
```

Ring nodes/edges already serialize as ordinary nodes/edges; the DTO records only the
roundabout's identity, geometry, and membership (leg edges are re-derived as non-ring
edges incident to ring nodes). `NextRoundabout` counter joins the other counters.
`ValidateGame` gains roundabout checks (ids below counter, no duplicates, every referenced
ring node/edge exists, ring edges connect consecutive ring nodes, ring node count ≥ 3,
`Ring` tags reconstructed on load). Format-v1 saves load unchanged (absent array → no
roundabouts). Byte-stable round-trip extended to cover roundabouts.

## Error handling

- `ConvertToRoundabout` / `SetRoundaboutRadius` return `RoundaboutResult` carrying a
  `RoundaboutError?`; the panel flashes a human message (`"radius too tight for these
  approaches — minimum N m"`, `"approach too short to convert"`) and plays `Sfx.Reject`.
  No partial mutation on failure — the planner runs fully before any graph edit.
- The radius spinner's minimum is clamped to the planner's feasible minimum so the common
  case can't even be requested below the floor.
- Regeneration that would leave < 3 legs auto-dissolves rather than building a degenerate
  ring.
- `NetworkInvariants.Check` gains roundabout invariants (below) so any corrupt ring — from
  a bad edit, a bug, or a hand-crafted save — is caught by the same health probe the fuzzer
  and tests already run after every mutation.

## New invariants (`NetworkInvariants`)

- Every ring edge is `OneWay`, both endpoints are ring nodes of the same roundabout,
  directed consistently CCW around the center.
- Every ring node has degree exactly 3: two ring legs plus exactly one approach (v1 places
  one approach per ring node, so degree 3 always).
- Ring node count ≥ 3; ring edges form a single closed cycle.
- Each ring node's `JunctionConfig` marks its approach leg `Yield` and ring legs `Main`.
- All existing invariants (no slivers, no sharp legs, lane coverage) continue to hold on
  the produced graph — the planner's floor checks guarantee ring edges clear
  `MinSegmentLength`/`MinRadius`.

## Testing & quality stack (definition of done)

1. **Unit — planner** (`RoundaboutPlannerTests`): ring node count/spacing/order, CCW
   direction, arc radius fidelity, radius clamping, T-junction (3 legs), near-collinear and
   coincident-bearing rejection, per-leg trim length.
2. **Unit — conversion/registry** (`RoundaboutTests`): 4-way and T convert to a valid ring
   (assert `NetworkInvariants.Check` empty); leg `EdgeId`s and their junction configs
   survive the trim; radius change re-arcs; bulldoze an approach re-arcs; bulldoze down to 2
   legs dissolves; `RemoveRoundabout` leaves clean stubs; convert refuses degree-2 / already-
   ring / foreign-owned legs.
3. **Unit — behaviour** (`RoundaboutTrafficTests`): entering car yields, circulating car has
   priority (built on the M5 arbitration test harness).
4. **Persistence** (`SaveLoadTests` additions): byte-stable round-trip with a roundabout;
   v1 save still loads; `ValidateGame` rejects a corrupt roundabout DTO.
5. **Fuzz**: extend `GestureFuzzer` alphabet with convert / adjust-radius / remove-
   roundabout actions; the standing invariant (`NetworkInvariants.Check` empty after every
   action) now covers roundabout edits. Re-run 3 × 10k green with per-seed evidence.
6. **KPI**: add a roundabout scenario to the harness; regenerate baseline + `docs/health/
   M7.5.md`.
7. **Manual**: new/updated chapter (extend ch02 network-validation or a short ch09) on the
   roundabout entity, planner, regeneration, and invariants; drift-check touched chapters.
8. **Harnesses**: `dotnet test`, `dotnet build`, `CITYBUILDER_SMOKE=1`, and the UITEST flow
   extended to convert a junction and screenshot the ring.
9. **Roadmap** updated: M7.5 entry with known limits (deferred: draw-into-existing-ring,
   multi-lane rings, saved in-flight vehicles).

## Open implementation questions (resolved in the plan, not blocking)

- Exact circular-arc cubic construction for ring segments > 90° (use two cubics, as
  `ArcFromTangent` already does).
- Whether leg trimming reuses `ReplaceEdgeInPlace` directly or a new
  `TrimEdgeInPlace(edgeId, newInnerNode, newCurve)` helper (leaning: a small dedicated
  helper, since the inner endpoint node changes, which `ReplaceEdgeInPlace` already
  supports).
- Second-batch regeneration trigger plumbing on `RemoveEdge` (a dirty-set drained after
  `EndBatch`).
```
