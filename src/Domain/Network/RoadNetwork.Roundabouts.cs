using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Tools;

namespace CityBuilder.Domain.Network;

public sealed partial class RoadNetwork
{
    // roundabouts whose ring must be re-arced after the current edit's batch closes.
    // Drained by DrainDirtyRoundabouts (each regenerate opens its own batch).
    private readonly HashSet<RoundaboutId> _dirtyRoundabouts = new();
    private bool _draining;

    /// <summary>Re-arc every roundabout marked dirty by the just-closed edit batch. Runs
    /// each regenerate in its own batch; re-entrancy-guarded.</summary>
    internal void DrainDirtyRoundabouts()
    {
        if (_draining)
            return;
        _draining = true;
        try
        {
            while (_dirtyRoundabouts.Count > 0)
            {
                var id = _dirtyRoundabouts.First();
                _dirtyRoundabouts.Remove(id);
                if (_roundabouts.TryGetValue(id, out var rb)
                    && !Regenerate(id, rb.Radius).Success && _roundabouts.ContainsKey(id))
                {
                    // the edit already changed geometry, so a failed re-plan must not
                    // leave a registry pointing at missing/stale ring pieces — dissolve
                    // to a consistent (ring-less) state instead
                    Dissolve(id);
                }
            }
        }
        finally { _draining = false; }
    }

    /// <summary>A ring edge (both endpoints are ring nodes) — owned by a roundabout and
    /// not directly editable. Approach edges (one plain end) are ordinary roads.</summary>
    internal bool IsRingEdge(EdgeId id)
        => _edges.TryGetValue(id, out var e)
           && _nodes.TryGetValue(e.StartNode, out var s) && s.Ring != null
           && _nodes.TryGetValue(e.EndNode, out var t) && t.Ring != null;

    internal bool IsRingNode(NodeId id) => _nodes.TryGetValue(id, out var n) && n.Ring != null;

    /// <summary>An approach edge: exactly one end is a ring node. Roundabout-owned like
    /// ring edges — not split/crossed/redrawn in v1, only bulldozed (→ re-arc) or reached
    /// via the roundabout API.</summary>
    internal bool IsApproachEdge(EdgeId id)
    {
        if (!_edges.TryGetValue(id, out var e))
            return false;
        bool s = _nodes.TryGetValue(e.StartNode, out var sn) && sn.Ring != null;
        bool t = _nodes.TryGetValue(e.EndNode, out var tn) && tn.Ring != null;
        return s ^ t;
    }

    private bool IsRoundaboutEdge(EdgeId id) => IsRingEdge(id) || IsApproachEdge(id);

    /// <summary>True if this proposed curve would attach to, split, or cross any roundabout
    /// ring node or roundabout-owned edge — blocked in v1 (editing a live ring's approaches
    /// / drawing into a ring is a deferred feature). Mirrors ResolveBinding: a Free endpoint
    /// reuses a nearby node or splits a nearby edge.</summary>
    private bool TouchesRoundabout(ProposedCurve pc)
    {
        return BindingTouches(pc.Start, pc.Curve.Point(0)) || BindingTouches(pc.End, pc.Curve.Point(1))
            || CrossesRoundaboutEdge(pc.Curve);

        bool BindingTouches(EndpointBinding b, Vector3 pos) => b switch
        {
            EndpointBinding.AtNode(var nid) => IsRingNode(nid),
            EndpointBinding.OnEdge(var eid, _) => IsRoundaboutEdge(eid),
            EndpointBinding.Free =>
                (FindNodeNear(pos, NodeReuseRadius) is { } near && IsRingNode(near))
                || (FindClosestEdge(pos, NodeReuseRadius) is { } hit && IsRoundaboutEdge(hit.id)),
            _ => false,
        };
    }

    private bool CrossesRoundaboutEdge(in Bezier3 curve)
    {
        var a = curve.Point(0);
        var b = curve.Point(1);
        foreach (var e in _edges.Values)
        {
            if (!IsRoundaboutEdge(e.Id))
                continue;
            foreach (var (t1, _) in BezierOps.Intersections(curve, e.Curve))
            {
                var p = curve.Point(t1);
                if (Vector3.Distance(p, a) > NodeReuseRadius && Vector3.Distance(p, b) > NodeReuseRadius)
                    return true;
            }
        }
        return false;
    }

    /// <summary>True when any planned ring arc geometrically intersects a live edge that
    /// the conversion will NOT consume (not a leg being trimmed, not an old ring edge
    /// being replaced) — committing would stamp overlapping drivable geometry across an
    /// unrelated road with no junction node (the M7.5 review's top find). Trimmed legs
    /// need no check: they are sub-curves of already-committed edges, whose only contacts
    /// with the rest of the network are at shared nodes. Bounding-box prefilter keeps the
    /// scan cheap; conversion is a rare, user-initiated op.</summary>
    private bool RingObstructed(RoundaboutPlan plan, HashSet<EdgeId> excluded)
    {
        foreach (var e in _edges.Values)
        {
            if (excluded.Contains(e.Id))
                continue;
            // cheap reject: edge box vs ring circle box
            var c = e.Curve;
            var min = Vector3.Min(Vector3.Min(c.P0, c.P1), Vector3.Min(c.P2, c.P3));
            var max = Vector3.Max(Vector3.Max(c.P0, c.P1), Vector3.Max(c.P2, c.P3));
            if (min.X > plan.Center.X + plan.Radius || max.X < plan.Center.X - plan.Radius
                || min.Z > plan.Center.Z + plan.Radius || max.Z < plan.Center.Z - plan.Radius)
                continue;
            foreach (var chain in plan.RingArcs)
            foreach (var arc in chain)
                if (BezierOps.Intersections(arc, c).Count > 0)
                    return true;
        }
        return false;
    }

    /// <summary>Keep a flipped approach's captured full curve in the same orientation as
    /// the live edge, so regeneration re-trims the road the way the player left it.</summary>
    private void OnApproachFlipped(EdgeId id)
    {
        foreach (var rb in _roundabouts.Values)
            if (rb.LegFullCurves.TryGetValue(id, out var full))
            {
                var updated = new Dictionary<EdgeId, Bezier3>(rb.LegFullCurves) { [id] = full.Reversed() };
                _roundabouts[rb.Id] = rb with { LegFullCurves = updated };
                return;
            }
    }

    /// <summary>Approximate smallest workable ring radius for converting this junction —
    /// the inspector's spinner clamp. Based on tangent bearings (the planner's exact
    /// feasibility uses actual circle crossings, which depend on the radius itself), so a
    /// clamped value can still be refused for curved legs; callers must surface planner
    /// errors regardless. Null when the node isn't convertible at any radius.</summary>
    public float? ConversionMinRadius(NodeId center)
    {
        if (!_nodes.TryGetValue(center, out var node) || node.EdgeSet.Count < 3 || node.Ring != null)
            return null;
        var legs = new List<ApproachLeg>();
        foreach (var eid in node.EdgeSet)
        {
            var e = _edges[eid];
            if (_nodes[e.OtherNode(center)].Ring != null)
                return null; // foreign leg — not convertible
            legs.Add(new ApproachLeg(eid, e.Curve, e.EndNode == center, e.Type));
        }
        float r = RoundaboutPlanner.MinFeasibleRadius(legs, node.Position);
        return float.IsInfinity(r) ? null : r;
    }

    /// <summary>Same clamp for an existing roundabout's radius spinner.</summary>
    public float? RoundaboutMinRadius(RoundaboutId id)
    {
        if (!_roundabouts.TryGetValue(id, out var rb))
            return null;
        var legs = SurvivingLegs(rb, out _, out _);
        if (legs.Count < 3)
            return null;
        float r = RoundaboutPlanner.MinFeasibleRadius(legs, rb.Center);
        return float.IsInfinity(r) ? null : r;
    }

    /// <summary>Find the roundabout a node belongs to, if any (ring nodes only).</summary>
    public Roundabout? RoundaboutForNode(NodeId n)
        => _nodes.TryGetValue(n, out var node) && node.Ring is { } id && _roundabouts.TryGetValue(id, out var rb)
            ? rb : null;

    /// <summary>Find the roundabout an edge belongs to, if any (ring edges only).</summary>
    public Roundabout? RoundaboutForEdge(EdgeId e)
        => _edges.TryGetValue(e, out var edge)
           && _nodes.TryGetValue(edge.StartNode, out var s) && s.Ring is { } id
           && _nodes.TryGetValue(edge.EndNode, out var t) && t.Ring == id
           && _roundabouts.TryGetValue(id, out var rb)
            ? rb : null;

    /// <summary>Convert an existing junction (node of degree ≥ 3) into a live roundabout:
    /// its legs are trimmed in place to meet a CCW one-way ring, the center node is
    /// replaced, and each ring node yields the approach to circulating traffic. No mutation
    /// happens on failure. See <see cref="RoundaboutPlanner"/> for the geometry.</summary>
    public RoundaboutResult ConvertToRoundabout(NodeId center, float radius)
    {
        if (!_nodes.TryGetValue(center, out var centerNode) || centerNode.EdgeSet.Count < 3)
            return RoundaboutResult.Failed(RoundaboutError.NotAJunction);
        if (centerNode.Ring != null)
            return RoundaboutResult.Failed(RoundaboutError.AlreadyRoundabout);

        var legs = new List<ApproachLeg>();
        foreach (var eid in centerNode.EdgeSet)
        {
            var e = _edges[eid];
            var far = e.OtherNode(center);
            if (_nodes[far].Ring != null)
                return RoundaboutResult.Failed(RoundaboutError.ForeignLeg);
            legs.Add(new ApproachLeg(eid, e.Curve, e.EndNode == center, e.Type));
        }

        var plan = RoundaboutPlanner.Plan(centerNode.Position, radius, legs);
        if (plan.Error is { } err)
            return RoundaboutResult.Failed(err);
        if (RingObstructed(plan, legs.Select(l => l.Edge).ToHashSet()))
            return RoundaboutResult.Failed(RoundaboutError.Obstructed);

        var id = new RoundaboutId(_nextRoundabout);
        var innerNodes = legs.ToDictionary(l => l.Edge, _ => center); // every leg currently meets the center
        BeginBatch();
        var built = BuildRing(id, plan, innerNodes);
        _nextRoundabout++;

        var legFullCurves = plan.Slots.ToDictionary(s => s.Leg.Edge, s => s.Leg.Curve);
        _roundabouts[id] = new Roundabout(id, plan.Center, plan.Radius,
            built.RingNodes, built.RingEdges, legFullCurves);

        // center node is now edgeless (all legs re-bound to ring nodes) — drop it
        if (_nodes.TryGetValue(center, out var stale) && stale.EdgeSet.Count == 0)
        {
            _nodes.Remove(center);
            _batch!.NodesRemoved.Add(center);
        }

        EndBatch();
        return RoundaboutResult.Ok(id);
    }

    /// <summary>Change a roundabout's radius, re-arcing the ring from the legs' full
    /// (pre-conversion) curves so the edit is lossless. No mutation on planner failure.</summary>
    public RoundaboutResult SetRoundaboutRadius(RoundaboutId id, float radius)
    {
        if (!_roundabouts.ContainsKey(id))
            return RoundaboutResult.Failed(RoundaboutError.UnknownRoundabout);
        return Regenerate(id, radius);
    }

    /// <summary>Dissolve a roundabout: remove its ring, leaving each approach as a
    /// free-ended stub where the ring was. Does not reconstruct the original junction —
    /// undo is the route back to that.</summary>
    public void RemoveRoundabout(RoundaboutId id)
    {
        if (_roundabouts.ContainsKey(id))
            Dissolve(id);
    }

    /// <summary>Tear down and rebuild a roundabout's ring at <paramref name="radius"/> from
    /// the surviving approach legs. Auto-dissolves when fewer than 3 legs remain (a 2-leg
    /// ring is just a bend). No mutation before planning, so a planner failure is clean.</summary>
    private RoundaboutResult Regenerate(RoundaboutId id, float radius)
    {
        var rb = _roundabouts[id];
        var legs = SurvivingLegs(rb, out var fullCurves, out var innerNodes);

        if (legs.Count < 3)
        {
            Dissolve(id);
            return RoundaboutResult.Ok(id);
        }

        var plan = RoundaboutPlanner.Plan(rb.Center, radius, legs);
        if (plan.Error is { } err)
            return RoundaboutResult.Failed(err);
        var excluded = legs.Select(l => l.Edge).ToHashSet();
        excluded.UnionWith(rb.RingEdges); // the old ring is being replaced
        if (RingObstructed(plan, excluded))
            return RoundaboutResult.Failed(RoundaboutError.Obstructed);

        BeginBatch();
        foreach (var re in rb.RingEdges)
            if (_edges.TryGetValue(re, out var edge))
                RemoveEdgeInternal(edge);

        var built = BuildRing(id, plan, innerNodes); // creates new ring nodes/edges, re-trims approaches in place
        _roundabouts[id] = rb with
        {
            Radius = radius, RingNodes = built.RingNodes, RingEdges = built.RingEdges, LegFullCurves = fullCurves,
        };

        // old ring nodes are now edgeless (ring edges removed, approaches re-bound onto new nodes)
        foreach (var rn in rb.RingNodes)
            if (_nodes.TryGetValue(rn, out var node) && node.EdgeSet.Count == 0)
            {
                _nodes.Remove(rn);
                _batch!.NodesRemoved.Add(rn);
            }

        EndBatch();
        return RoundaboutResult.Ok(id);
    }

    private void Dissolve(RoundaboutId id)
    {
        var rb = _roundabouts[id];
        BeginBatch();
        foreach (var re in rb.RingEdges)
            if (_edges.TryGetValue(re, out var edge))
                RemoveEdgeInternal(edge);
        foreach (var rn in rb.RingNodes)
            if (_nodes.TryGetValue(rn, out var node))
            {
                node.Ring = null;
                if (node.EdgeSet.Count == 0)
                {
                    _nodes.Remove(rn);
                    _batch!.NodesRemoved.Add(rn);
                }
                else
                {
                    node.Config = JunctionConfig.Default; // approach stub, no longer a ring junction
                    _batch!.Touched.Add(rn);
                }
            }
        _roundabouts.Remove(id);
        EndBatch();
    }

    /// <summary>Approach legs of a roundabout that still exist and are still bound to it,
    /// rebuilt from their captured full (pre-conversion) curves so a re-plan trims from the
    /// original shape. <paramref name="fullCurves"/> collects the surviving captures.</summary>
    private List<ApproachLeg> SurvivingLegs(Roundabout rb,
        out Dictionary<EdgeId, Bezier3> fullCurves, out Dictionary<EdgeId, NodeId> innerNodes)
    {
        // Discover approaches structurally (any non-ring edge on a ring node) rather than
        // from the stored leg list, so an approach whose EdgeId changed since conversion
        // (split, heal) is still found and re-arced. Full curves are re-used when tracked
        // (they reach the center for lossless radius re-trim) and otherwise synthesized as
        // a radial line to the center for a curve that only reaches the current ring.
        // innerNodes records which current node each approach attaches to on the ring, so
        // the re-trim rebinds the correct end regardless of the edge's stored orientation.
        var legs = new List<ApproachLeg>();
        fullCurves = new Dictionary<EdgeId, Bezier3>();
        innerNodes = new Dictionary<EdgeId, NodeId>();
        var seen = new HashSet<EdgeId>();
        foreach (var rnId in rb.RingNodes)
        {
            if (!_nodes.TryGetValue(rnId, out var rn) || rn.Ring != rb.Id)
                continue;
            foreach (var eid in rn.EdgeSet.ToArray())
            {
                if (!seen.Add(eid))
                    continue;
                var e = _edges[eid];
                var other = e.OtherNode(rnId);
                if (_nodes.TryGetValue(other, out var on) && on.Ring == rb.Id)
                    continue; // ring edge, not an approach
                var outerEnd = e.StartNode == rnId ? e.Curve.Point(1) : e.Curve.Point(0);
                var full = rb.LegFullCurves.TryGetValue(eid, out var tracked)
                    ? tracked
                    : Bezier3.Line(outerEnd, rb.Center);
                bool endsAtCenter = Vector3.Distance(full.Point(1), rb.Center)
                                  < Vector3.Distance(full.Point(0), rb.Center);
                legs.Add(new ApproachLeg(eid, full, endsAtCenter, e.Type));
                fullCurves[eid] = full;
                innerNodes[eid] = rnId;
            }
        }
        return legs;
    }

    private readonly record struct BuiltRing(IReadOnlyList<NodeId> RingNodes, IReadOnlyList<EdgeId> RingEdges);

    /// <summary>Realize a plan as graph surgery inside the current batch: create ring
    /// nodes (slots + intermediates for wide gaps), wire CCW one-way arcs, trim each leg
    /// in place onto its slot, and set the yield-on-entry control. Returns the CCW ring
    /// node/edge lists for the <see cref="Roundabout"/> record.</summary>
    private BuiltRing BuildRing(RoundaboutId id, RoundaboutPlan plan, IReadOnlyDictionary<EdgeId, NodeId> innerNodes)
    {
        int n = plan.Slots.Count;
        // one ring node per slot (carries the approach leg)
        var slotNodes = new NodeId[n];
        for (int i = 0; i < n; i++)
        {
            var node = AddNodeInternal(plan.Slots[i].Position);
            node.Ring = id;
            slotNodes[i] = node.Id;
        }

        var ringNodesCcw = new List<NodeId>();
        var ringEdgesCcw = new List<EdgeId>();
        for (int i = 0; i < n; i++)
        {
            ringNodesCcw.Add(slotNodes[i]);
            var chain = plan.RingArcs[i];
            var prev = slotNodes[i];
            for (int j = 0; j < chain.Count; j++)
            {
                NodeId to;
                if (j == chain.Count - 1)
                {
                    to = slotNodes[(i + 1) % n];
                }
                else
                {
                    var mid = AddNodeInternal(chain[j].P3);
                    mid.Ring = id;
                    to = mid.Id;
                    ringNodesCcw.Add(to);
                }
                var edge = AddEdgeInternal(prev, to, chain[j], RoadCatalog.OneWay.Id);
                ringEdgesCcw.Add(edge.Id);
                prev = to;
            }
        }

        // trim each leg in place onto its slot ring node. Yield-on-entry control is set
        // by RebuildDerived (RingNodeConfig) at EndBatch, derived from ring membership.
        for (int i = 0; i < n; i++)
        {
            var slot = plan.Slots[i];
            TrimLegInto(_edges[slot.Leg.Edge], innerNodes[slot.Leg.Edge], slotNodes[i],
                slot.TrimmedLeg, slot.TrimmedLegEndsAtCenter);
        }

        return new BuiltRing(ringNodesCcw, ringEdgesCcw);
    }

    /// <summary>Derived control for a ring node: PrioritySigns with every ring leg (an edge
    /// to another ring node of the same roundabout) the main road and every approach a
    /// yield. Recomputed on each RebuildDerived, so approach EdgeId churn can't strand it.</summary>
    private JunctionConfig RingNodeConfig(RoadNode node)
    {
        var roles = new Dictionary<EdgeId, LegRole>();
        foreach (var eid in node.EdgeSet)
        {
            var other = _edges[eid].OtherNode(node.Id);
            bool ringLeg = _nodes.TryGetValue(other, out var on) && on.Ring == node.Ring;
            roles[eid] = ringLeg ? LegRole.Main : LegRole.Yield;
        }
        return new JunctionConfig(JunctionControlMode.PrioritySigns, roles, 0f, new Dictionary<EdgeId, float>());
    }

    /// <summary>Replace a leg edge in place (same EdgeId) with its trimmed curve, rebinding
    /// its inner end from <paramref name="currentInner"/> (the old center or old ring node it
    /// currently attaches to) to <paramref name="ringNode"/>. The outer end is whichever end
    /// is NOT currentInner — taken from the live edge, so a synthesized full curve's
    /// orientation can't mis-bind it. Lanes regenerate; the edge is marked changed so
    /// renderers re-mesh it. <paramref name="trimmedEndsAtCenter"/> says which end of the
    /// trimmed curve is the inner/ring end, so the curve stays geometrically aligned.</summary>
    private void TrimLegInto(RoadEdge old, NodeId currentInner, NodeId ringNode,
        in Bezier3 trimmed, bool trimmedEndsAtCenter)
    {
        NodeId outer = old.OtherNode(currentInner);
        NodeId start = trimmedEndsAtCenter ? outer : ringNode;
        NodeId end = trimmedEndsAtCenter ? ringNode : outer;

        _nodes[old.StartNode].EdgeSet.Remove(old.Id);
        _nodes[old.EndNode].EdgeSet.Remove(old.Id);

        var replacement = new RoadEdge(old.Id, start, end, trimmed, old.Type);
        replacement.Lanes = RoadCatalog.Get(old.Type).Lanes
            .Select(spec => new Lane(new LaneId(_nextLane++), replacement.Id, spec.Offset, spec.Direction, spec.Width, spec.Kind))
            .ToArray();
        _edges[old.Id] = replacement;
        _nodes[start].EdgeSet.Add(old.Id);
        _nodes[end].EdgeSet.Add(old.Id);

        _batch!.EdgesChanged.Add(old.Id);
        _batch!.Touched.Add(start);
        _batch!.Touched.Add(end);
        _batch!.Touched.Add(outer);
    }
}
