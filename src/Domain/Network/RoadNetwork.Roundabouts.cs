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
