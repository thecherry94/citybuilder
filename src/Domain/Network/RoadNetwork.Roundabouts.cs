using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

public sealed partial class RoadNetwork
{
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

        var id = new RoundaboutId(_nextRoundabout);
        BeginBatch();
        var built = BuildRing(id, plan);
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
        var legs = SurvivingLegs(rb, out var fullCurves);

        if (legs.Count < 3)
        {
            Dissolve(id);
            return RoundaboutResult.Ok(id);
        }

        var plan = RoundaboutPlanner.Plan(rb.Center, radius, legs);
        if (plan.Error is { } err)
            return RoundaboutResult.Failed(err);

        BeginBatch();
        foreach (var re in rb.RingEdges)
            if (_edges.TryGetValue(re, out var edge))
                RemoveEdgeInternal(edge);

        var built = BuildRing(id, plan); // creates new ring nodes/edges, re-trims approaches in place
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
    private List<ApproachLeg> SurvivingLegs(Roundabout rb, out Dictionary<EdgeId, Bezier3> fullCurves)
    {
        var legs = new List<ApproachLeg>();
        fullCurves = new Dictionary<EdgeId, Bezier3>();
        foreach (var (edgeId, full) in rb.LegFullCurves)
        {
            if (!_edges.TryGetValue(edgeId, out var e))
                continue; // bulldozed
            bool startRing = _nodes.TryGetValue(e.StartNode, out var sn) && sn.Ring == rb.Id;
            bool endRing = _nodes.TryGetValue(e.EndNode, out var en) && en.Ring == rb.Id;
            if (startRing == endRing)
                continue; // not (or no longer) an approach of this roundabout
            bool endsAtCenter = Vector3.Distance(full.Point(1), rb.Center)
                              < Vector3.Distance(full.Point(0), rb.Center);
            legs.Add(new ApproachLeg(edgeId, full, endsAtCenter, e.Type));
            fullCurves[edgeId] = full;
        }
        return legs;
    }

    private readonly record struct BuiltRing(IReadOnlyList<NodeId> RingNodes, IReadOnlyList<EdgeId> RingEdges);

    /// <summary>Realize a plan as graph surgery inside the current batch: create ring
    /// nodes (slots + intermediates for wide gaps), wire CCW one-way arcs, trim each leg
    /// in place onto its slot, and set the yield-on-entry control. Returns the CCW ring
    /// node/edge lists for the <see cref="Roundabout"/> record.</summary>
    private BuiltRing BuildRing(RoundaboutId id, RoundaboutPlan plan)
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

        // trim each leg in place onto its slot ring node
        for (int i = 0; i < n; i++)
        {
            var slot = plan.Slots[i];
            TrimLegInto(_edges[slot.Leg.Edge], slotNodes[i], slot.TrimmedLeg, slot.TrimmedLegEndsAtCenter);
        }

        // yield-on-entry: approach leg yields, ring legs are the main road
        for (int i = 0; i < n; i++)
        {
            var node = _nodes[slotNodes[i]];
            var approach = plan.Slots[i].Leg.Edge;
            node.Config = new JunctionConfig(
                JunctionControlMode.PrioritySigns,
                node.EdgeSet.ToDictionary(e => e, e => e == approach ? LegRole.Yield : LegRole.Main),
                0f,
                new Dictionary<EdgeId, float>());
        }

        return new BuiltRing(ringNodesCcw, ringEdgesCcw);
    }

    /// <summary>Replace a leg edge in place (same EdgeId) with its trimmed curve, rebinding
    /// its inner end from the old center node to <paramref name="ringNode"/>. Lanes
    /// regenerate; the edge is marked changed so renderers re-mesh it.</summary>
    private void TrimLegInto(RoadEdge old, NodeId ringNode, in Bezier3 trimmed, bool endsAtCenter)
    {
        NodeId outer = endsAtCenter ? old.StartNode : old.EndNode;
        NodeId start = endsAtCenter ? outer : ringNode;
        NodeId end = endsAtCenter ? ringNode : outer;

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
