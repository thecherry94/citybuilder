using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Tools;

namespace CityBuilder.Domain.Network;

/// <summary>
/// Source of truth for the road graph. All mutations go through
/// <see cref="Validate"/>/<see cref="Commit"/>/<see cref="RemoveEdge"/>, which keep
/// the invariants: no two edges cross without a shared node, no sliver edges next to
/// nodes, derived data (lanes, junction geometry, lane connectors) always fresh.
/// Raises one aggregated <see cref="Changed"/> event per mutation batch.
/// </summary>
public sealed class RoadNetwork
{
    /// <summary>Distance under which a free endpoint picks up an existing node.</summary>
    public const float NodeReuseRadius = 0.5f;

    /// <summary>Minimum angle (between centerline tangents) at which two roads may cross.</summary>
    public const float MinCrossingAngleDeg = 15f;

    private readonly Dictionary<NodeId, RoadNode> _nodes = new();
    private readonly Dictionary<EdgeId, RoadEdge> _edges = new();
    private int _nextNode = 1, _nextEdge = 1, _nextLane = 1;
    private Batch? _batch;

    public event Action<NetworkDelta>? Changed;

    public int Version { get; private set; }

    public IReadOnlyDictionary<NodeId, RoadNode> Nodes => _nodes;
    public IReadOnlyDictionary<EdgeId, RoadEdge> Edges => _edges;

    // ---------------------------------------------------------------- queries

    public NodeId? FindNodeNear(Vector3 p, float radius)
    {
        NodeId? best = null;
        float bestD = radius;
        foreach (var n in _nodes.Values)
        {
            float d = Vector3.Distance(n.Position, p);
            if (d <= bestD) { bestD = d; best = n.Id; }
        }
        return best;
    }

    public (EdgeId id, float t, float dist)? FindClosestEdge(Vector3 p, float maxDist)
    {
        (EdgeId, float, float)? best = null;
        float bestD = maxDist;
        foreach (var e in _edges.Values)
        {
            var (t, d) = BezierOps.ClosestPoint(e.Curve, p);
            if (d <= bestD) { bestD = d; best = (e.Id, t, d); }
        }
        return best;
    }

    // ------------------------------------------------------------- validation

    public ValidatedPlacement Validate(PlacementProposal proposal)
    {
        var errors = new List<PlacementError>();
        var crossings = new List<Vector3>();

        foreach (var pc in proposal.Curves)
        {
            if (pc.Curve.Length() < GeoConstants.MinEdgeLength)
                errors.Add(PlacementError.TooShort);

            if (BezierOps.SelfIntersects(pc.Curve))
                errors.Add(PlacementError.SelfIntersecting);

            if (OverlapsExisting(pc, proposal.Type))
                errors.Add(PlacementError.Overlapping);

            Vector3 a = pc.Curve.Point(0), b = pc.Curve.Point(1);
            bool shallow = false;
            foreach (var e in _edges.Values)
            foreach (var (t1, t2) in BezierOps.Intersections(pc.Curve, e.Curve))
            {
                var p = pc.Curve.Point(t1);
                // connections at the proposal's own endpoints are not crossings
                if (Vector3.Distance(p, a) <= NodeReuseRadius || Vector3.Distance(p, b) <= NodeReuseRadius)
                    continue;
                crossings.Add(p);
                // near-tangential crossings produce unbuildable junction geometry
                if (CrossingAngleDeg(pc.Curve.Tangent(t1), e.Curve.Tangent(t2)) < MinCrossingAngleDeg)
                    shallow = true;
            }
            if (shallow)
                errors.Add(PlacementError.CrossingTooShallow);
        }

        return new ValidatedPlacement(proposal, errors.Count == 0, errors, crossings, Version);
    }

    private static float CrossingAngleDeg(Vector3 tanA, Vector3 tanB)
    {
        float cross = MathF.Abs(tanA.X * tanB.Z - tanA.Z * tanB.X);
        float dot = MathF.Abs(tanA.X * tanB.X + tanA.Z * tanB.Z);
        return MathF.Atan2(cross, dot) * 180f / MathF.PI;
    }

    private bool OverlapsExisting(ProposedCurve pc, RoadTypeId type)
    {
        const int samples = 32;
        float halfNew = RoadCatalog.Get(type).Width / 2;
        int flagged = 0, counted = 0;
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            var p = pc.Curve.Point(t);
            var tangent = pc.Curve.Tangent(t);
            counted++;
            foreach (var e in _edges.Values)
            {
                var (et, dist) = BezierOps.ClosestPoint(e.Curve, p);
                if (et < 0.02f || et > 0.98f)
                    continue; // clamped at an endpoint: lateral proximity to a node, not overlap
                float halfOld = RoadCatalog.Get(e.Type).Width / 2;
                if (dist < (halfNew + halfOld) * 0.8f
                    && MathF.Abs(Vector3.Dot(tangent, e.Curve.Tangent(et))) > 0.95f)
                {
                    flagged++;
                    break;
                }
            }
        }
        return flagged > counted / 2;
    }

    // ----------------------------------------------------------------- commit

    public CommitResult Commit(ValidatedPlacement placement)
    {
        if (placement.NetworkVersion != Version)
        {
            placement = Validate(placement.Proposal);
            if (!placement.IsValid)
                return CommitResult.Failed("placement no longer valid: " + string.Join(", ", placement.Errors));
        }
        if (!placement.IsValid)
            return CommitResult.Failed("invalid placement: " + string.Join(", ", placement.Errors));

        var createdEdges = new List<EdgeId>();
        var createdNodes = new List<NodeId>();

        BeginBatch();
        foreach (var pc in placement.Proposal.Curves)
            CommitCurve(pc, placement.Proposal.Type, createdEdges, createdNodes);
        EndBatch();

        // report only survivors (an edge created for one grid line may be split by the next)
        createdEdges.RemoveAll(e => !_edges.ContainsKey(e));
        createdNodes.RemoveAll(n => !_nodes.ContainsKey(n));
        return new CommitResult(true, createdEdges, createdNodes, null);
    }

    private void CommitCurve(ProposedCurve pc, RoadTypeId type,
        List<EdgeId> createdEdges, List<NodeId> createdNodes)
    {
        var curve = pc.Curve;
        var startNode = ResolveBinding(pc.Start, curve.Point(0), createdNodes);
        var endNode = ResolveBinding(pc.End, curve.Point(1), createdNodes);

        // --- find crossings against every current edge, splitting them as we go
        var crossings = new List<(float tNew, NodeId node)>();
        var hitsByEdge = new Dictionary<EdgeId, List<(float t1, float t2)>>();
        Vector3 startPos = _nodes[startNode].Position, endPos = _nodes[endNode].Position;

        foreach (var e in _edges.Values.ToList())
        {
            foreach (var hit in BezierOps.Intersections(curve, e.Curve))
            {
                var p = curve.Point(hit.t1);
                if (Vector3.Distance(p, startPos) <= NodeReuseRadius
                    || Vector3.Distance(p, endPos) <= NodeReuseRadius)
                    continue; // connection at an endpoint, not a crossing
                if (!hitsByEdge.TryGetValue(e.Id, out var list))
                    hitsByEdge[e.Id] = list = new List<(float, float)>();
                list.Add(hit);
            }
        }

        foreach (var (edgeId, hits) in hitsByEdge)
        {
            hits.Sort((x, y) => x.t2.CompareTo(y.t2));
            EdgeId cur = edgeId;
            float consumed = 0; // param of original edge where `cur` begins
            foreach (var (t1, t2) in hits)
            {
                float tLocal = (t2 - consumed) / (1 - consumed);
                // split children of existing edges are side effects, not "created" edges
                var (node, after) = SplitEdgeWithReuse(cur, tLocal, createdNodes);
                crossings.Add((t1, node));
                if (after is { } a)
                {
                    cur = a;
                    consumed = t2;
                }
            }
        }

        // --- split the new curve into segments between consecutive crossing params
        crossings.Sort((x, y) => x.tNew.CompareTo(y.tNew));
        var stops = new List<(float t, NodeId node)> { (0, startNode) };
        stops.AddRange(crossings);
        stops.Add((1, endNode));

        for (int i = 0; i + 1 < stops.Count; i++)
        {
            var (ta, na) = stops[i];
            var (tb, nb) = stops[i + 1];
            if (na == nb || tb - ta < 1e-5f)
                continue;
            var seg = SubCurve(curve, ta, tb, _nodes[na].Position, _nodes[nb].Position);
            if (seg.Length() < GeoConstants.Eps)
                continue;
            createdEdges.Add(AddEdgeInternal(na, nb, seg, type).Id);
        }
    }

    private NodeId ResolveBinding(EndpointBinding binding, Vector3 pos, List<NodeId> createdNodes)
    {
        switch (binding)
        {
            case EndpointBinding.AtNode(var id) when _nodes.ContainsKey(id):
                return id;

            case EndpointBinding.OnEdge(var edgeId, var t):
            {
                if (!_edges.ContainsKey(edgeId))
                {
                    // edge was consumed by an earlier split in this batch: re-locate
                    var hit = FindClosestEdge(pos, 2f);
                    if (hit is null)
                        break;
                    (edgeId, t) = (hit.Value.id, hit.Value.t);
                }
                var (node, _) = SplitEdgeWithReuse(edgeId, t, createdNodes);
                return node;
            }
        }

        // free endpoint (or unresolvable binding): reuse a nearby node, else connect
        // to an edge passing through this point, else create a fresh node
        if (FindNodeNear(pos, NodeReuseRadius) is { } near)
            return near;
        if (FindClosestEdge(pos, NodeReuseRadius) is { } onEdge)
        {
            var (node, _) = SplitEdgeWithReuse(onEdge.id, onEdge.t, createdNodes);
            return node;
        }
        var created = AddNodeInternal(pos);
        createdNodes.Add(created.Id);
        return created.Id;
    }

    /// <summary>Split an edge at local parameter t, unless the split point is within
    /// MinEdgeLength of an end — then that end's node is reused and nothing is split.
    /// Returns the node at the split point and, if a split happened, the edge covering
    /// the part after t (for remapping subsequent split params).</summary>
    private (NodeId node, EdgeId? after) SplitEdgeWithReuse(EdgeId edgeId, float t,
        List<NodeId>? createdNodes)
    {
        var edge = _edges[edgeId];
        float d = edge.ArcLength.DistanceAtT(t);
        if (d < GeoConstants.MinEdgeLength)
            return (edge.StartNode, null);
        if (edge.ArcLength.TotalLength - d < GeoConstants.MinEdgeLength)
            return (edge.EndNode, null);

        var (a, b) = edge.Curve.Split(t);
        var mid = AddNodeInternal(edge.Curve.Point(t));
        createdNodes?.Add(mid.Id);

        RemoveEdgeInternal(edge);
        AddEdgeInternal(edge.StartNode, mid.Id, a, edge.Type);
        var eb = AddEdgeInternal(mid.Id, edge.EndNode, b, edge.Type);
        return (mid.Id, eb.Id);
    }

    /// <summary>Extract curve range [ta, tb] and pin its endpoints to the given
    /// node positions (which may differ slightly after node reuse).</summary>
    private static Bezier3 SubCurve(in Bezier3 curve, float ta, float tb, Vector3 posA, Vector3 posB)
    {
        var (_, rest) = curve.Split(ta);
        float tbLocal = ta >= 1f ? 1f : (tb - ta) / (1 - ta);
        var (seg, _) = rest.Split(Math.Clamp(tbLocal, 0f, 1f));
        return new Bezier3(posA, seg.P1, seg.P2, posB);
    }

    // ---------------------------------------------------------------- removal

    public void RemoveEdge(EdgeId id)
    {
        if (!_edges.TryGetValue(id, out var edge))
            return;

        BeginBatch();
        RemoveEdgeInternal(edge);
        foreach (var nodeId in new[] { edge.StartNode, edge.EndNode })
            HandleNodeAfterRemoval(nodeId);
        EndBatch();
    }

    private void HandleNodeAfterRemoval(NodeId nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var node))
            return;
        if (node.EdgeSet.Count == 0)
        {
            _nodes.Remove(nodeId);
            _batch!.NodesRemoved.Add(nodeId);
            return;
        }
        TryHealNode(node);
    }

    /// <summary>Degree-2 nodes left by a removal try to merge back into one edge.
    /// Implemented in the healing task; the base implementation keeps the node.</summary>
    private void TryHealNode(RoadNode node)
    {
        if (node.EdgeSet.Count != 2)
            return;
        var edges = node.EdgeSet.Select(e => _edges[e]).ToArray();
        if (edges[0].Type != edges[1].Type)
            return;

        var (merged, maxError) = CurveFit.FitComposite(edges[0], edges[1], node.Id, _nodes);
        if (maxError > GeoConstants.MergeTolerance)
            return;

        var farA = edges[0].OtherNode(node.Id);
        var farB = edges[1].OtherNode(node.Id);
        if (farA == farB)
            return; // would create a loop edge; keep the node

        var type = edges[0].Type;
        RemoveEdgeInternal(edges[0]);
        RemoveEdgeInternal(edges[1]);
        _nodes.Remove(node.Id);
        _batch!.NodesRemoved.Add(node.Id);
        AddEdgeInternal(farA, farB, merged, type);
    }

    // ------------------------------------------------------- low-level mutate

    private RoadNode AddNodeInternal(Vector3 pos)
    {
        var node = new RoadNode(new NodeId(_nextNode++), pos);
        _nodes[node.Id] = node;
        _batch!.NodesAdded.Add(node.Id);
        return node;
    }

    private RoadEdge AddEdgeInternal(NodeId start, NodeId end, in Bezier3 curve, RoadTypeId type)
    {
        var edge = new RoadEdge(new EdgeId(_nextEdge++), start, end, curve, type);
        edge.Lanes = RoadCatalog.Get(type).Lanes
            .Select(spec => new Lane(new LaneId(_nextLane++), edge.Id, spec.Offset, spec.Direction, spec.Width, spec.Kind))
            .ToArray();
        _edges[edge.Id] = edge;
        _nodes[start].EdgeSet.Add(edge.Id);
        _nodes[end].EdgeSet.Add(edge.Id);
        _batch!.EdgesAdded.Add(edge.Id);
        _batch!.Touched.Add(start);
        _batch!.Touched.Add(end);
        return edge;
    }

    private void RemoveEdgeInternal(RoadEdge edge)
    {
        _edges.Remove(edge.Id);
        _nodes[edge.StartNode].EdgeSet.Remove(edge.Id);
        _nodes[edge.EndNode].EdgeSet.Remove(edge.Id);
        _batch!.EdgesRemoved.Add(edge.Id);
        _batch!.Touched.Add(edge.StartNode);
        _batch!.Touched.Add(edge.EndNode);
    }

    // ----------------------------------------------------------------- events

    private sealed class Batch
    {
        public readonly HashSet<EdgeId> EdgesAdded = new(), EdgesRemoved = new();
        public readonly HashSet<NodeId> NodesAdded = new(), NodesRemoved = new();
        public readonly HashSet<NodeId> Touched = new();
    }

    private void BeginBatch() => _batch = new Batch();

    private void EndBatch()
    {
        var b = _batch!;

        // rebuild derived data on every touched surviving node
        foreach (var nodeId in b.Touched)
            if (_nodes.TryGetValue(nodeId, out var node))
                RebuildDerived(node);

        // reconcile add+remove within the same batch
        var bothE = b.EdgesAdded.Intersect(b.EdgesRemoved).ToArray();
        b.EdgesAdded.ExceptWith(bothE);
        b.EdgesRemoved.ExceptWith(bothE);
        var bothN = b.NodesAdded.Intersect(b.NodesRemoved).ToArray();
        b.NodesAdded.ExceptWith(bothN);
        b.NodesRemoved.ExceptWith(bothN);

        var changed = new HashSet<NodeId>(b.Touched.Where(_nodes.ContainsKey));
        changed.ExceptWith(b.NodesAdded);

        _batch = null;
        Version++;
        Changed?.Invoke(new NetworkDelta(b.EdgesAdded, b.EdgesRemoved, b.NodesAdded, b.NodesRemoved, changed));
    }

    /// <summary>Apply an authored junction configuration (control mode, per-leg roles,
    /// size offsets). Overrides for edges no longer connected are pruned. Rebuilds the
    /// node's derived data and raises a change event.</summary>
    public void ConfigureJunction(NodeId id, JunctionConfig config)
    {
        if (!_nodes.TryGetValue(id, out var node))
            throw new ArgumentException($"unknown node {id}");
        node.Config = Prune(config, node.EdgeSet);
        RebuildDerived(node);
        Version++;
        Changed?.Invoke(new NetworkDelta(
            new HashSet<EdgeId>(), new HashSet<EdgeId>(),
            new HashSet<NodeId>(), new HashSet<NodeId>(),
            new HashSet<NodeId> { id }));
    }

    private static JunctionConfig Prune(JunctionConfig c, IReadOnlySet<EdgeId> edges)
    {
        if (c.RoleOverrides.Keys.All(edges.Contains) && c.LegOffsets.Keys.All(edges.Contains))
            return c;
        return c with
        {
            RoleOverrides = c.RoleOverrides.Where(kv => edges.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            LegOffsets = c.LegOffsets.Where(kv => edges.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        };
    }

    /// <summary>Regenerate junction geometry and lane connectors for a node.
    /// Order matters: connectors start at the junction cuts.</summary>
    private void RebuildDerived(RoadNode node)
    {
        node.Config = Prune(node.Config, node.EdgeSet);
        node.Junction = JunctionBuilder.Build(node, _edges);
        node.Connectors = ConnectorBuilder.Build(node, _edges);
    }
}
