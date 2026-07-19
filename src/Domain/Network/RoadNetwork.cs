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
public sealed partial class RoadNetwork
{
    /// <summary>Distance under which a free endpoint picks up an existing node.</summary>
    public const float NodeReuseRadius = 0.5f;

    /// <summary>Minimum angle between any two legs meeting at a node. Also the floor
    /// for proposal-vs-existing crossings (`CrossingTooShallow`) — a crossing *is* a
    /// future junction, so it obeys the same 25° rule.</summary>
    public const float MinJunctionAngleDeg = 25f;

    /// <summary>Departures from an OnEdge binding within this angle of the edge tangent
    /// are G1 continuations (ramp exits), not sharp bumps — SharpAngle exempts them.</summary>
    public const float TangentContinuationDeg = 1f;

    private readonly Dictionary<NodeId, RoadNode> _nodes = new();
    private readonly Dictionary<EdgeId, RoadEdge> _edges = new();
    private readonly Dictionary<RoundaboutId, Roundabout> _roundabouts = new();
    private int _nextNode = 1, _nextEdge = 1, _nextLane = 1, _nextRoundabout = 1;
    private Batch? _batch;

    public event Action<NetworkDelta>? Changed;

    public int Version { get; private set; }

    public IReadOnlyDictionary<NodeId, RoadNode> Nodes => _nodes;
    public IReadOnlyDictionary<EdgeId, RoadEdge> Edges => _edges;
    public IReadOnlyDictionary<RoundaboutId, Roundabout> Roundabouts => _roundabouts;

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
        var type = RoadCatalog.Get(proposal.Type);
        // crossing arc-distances per EXISTING edge, aggregated across ALL proposal
        // curves — consecutive crossings on the same edge must not leave slivers
        var hitsPerExistingEdge = new Dictionary<EdgeId, List<float>>();

        foreach (var pc in proposal.Curves)
        {
            if (pc.Curve.Length() < type.MinSegmentLength)
                errors.Add(PlacementError.TooShort);

            if (BezierOps.MinRadius(pc.Curve) < type.MinRadius)
                errors.Add(PlacementError.RadiusTooTight);

            if (VerticalRules.MaxGradient(pc.Curve) > type.MaxGradient + 0.001f)
                errors.Add(PlacementError.TooSteep);

            if (BezierOps.SelfIntersects(pc.Curve))
                errors.Add(PlacementError.SelfIntersecting);

            if (OverlapsExisting(pc, proposal.Type))
                errors.Add(PlacementError.Overlapping);

            if (BindingTouchesRoundabout(pc))
                errors.Add(PlacementError.TouchesRoundabout);

            Vector3 a = pc.Curve.Point(0), b = pc.Curve.Point(1);
            bool shallow = false, sliver = false, touchesOwned = false, clash = false;
            var crossParams = new List<float>();
            foreach (var e in _edges.Values)
            foreach (var (t1, t2) in BezierOps.Intersections(pc.Curve, e.Curve))
            {
                var p = pc.Curve.Point(t1);
                if (Vector3.Distance(p, a) <= NodeReuseRadius || Vector3.Distance(p, b) <= NodeReuseRadius)
                    continue; // connection at an endpoint, not a crossing
                // Intersections is XZ-projected: classify the crossing by vertical
                // separation before any junction math (M8)
                switch (VerticalRules.ClassifyCrossing(p.Y, e.Curve.Point(t2).Y))
                {
                    case CrossingKind.GradeSeparated:
                        continue; // passes over/under: not a crossing in any sense
                    case CrossingKind.VerticalClash:
                        clash = true;
                        continue; // illegal band — no junction math either
                }
                // crossing a RING edge is blocked in v1, as is a crossing that would
                // attach directly to a ring node (a new leg on the ring is deferred).
                // Approaches elsewhere are ordinary roads: crossings split them, with
                // the captured leg curve re-keyed onto the inner child (OnApproachSplit).
                if (_roundabouts.Count > 0)
                {
                    if (IsRingEdge(e.Id))
                        touchesOwned = true;
                    else if ((_nodes[e.StartNode].Ring != null
                              && Vector3.Distance(p, _nodes[e.StartNode].Position) <= NodeReuseRadius)
                          || (_nodes[e.EndNode].Ring != null
                              && Vector3.Distance(p, _nodes[e.EndNode].Position) <= NodeReuseRadius))
                        touchesOwned = true;
                }
                crossings.Add(p);
                crossParams.Add(t1);
                if (CrossingAngleDeg(pc.Curve.Tangent(t1), e.Curve.Tangent(t2)) < MinJunctionAngleDeg)
                    shallow = true;
                // crossing must not leave a sliver on the existing edge — unless it
                // lands within reuse distance of that edge's own end, which extends the
                // node already there (a junction, not a new short edge)
                float dAlong = e.ArcLength.DistanceAtT(t2);
                bool atExistingEnd = dAlong <= NodeReuseRadius || e.ArcLength.TotalLength - dAlong <= NodeReuseRadius;
                if (!atExistingEnd)
                {
                    float eMin = RoadCatalog.Get(e.Type).MinSegmentLength;
                    if (dAlong < eMin || e.ArcLength.TotalLength - dAlong < eMin)
                        sliver = true;
                    if (!hitsPerExistingEdge.TryGetValue(e.Id, out var along))
                        hitsPerExistingEdge[e.Id] = along = new List<float>();
                    along.Add(dAlong);
                }
            }
            if (shallow)
                errors.Add(PlacementError.CrossingTooShallow);
            if (touchesOwned)
                errors.Add(PlacementError.TouchesRoundabout);
            if (clash)
                errors.Add(PlacementError.VerticalClash);

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

            // endpoint bindings must not land a sliver from the edge's ends — for
            // OnEdge bindings directly, and for Free endpoints that commit would
            // resolve onto an edge (mirrors ResolveBinding's node/edge fallback)
            sliver |= BindingLeavesSliver(pc.Start, a) || BindingLeavesSliver(pc.End, b);
            if (sliver)
                errors.Add(PlacementError.TooShort);

            // sharp legs against the existing network at both ends
            if (HasSharpLeg(pc.Start, a, pc.Curve.Tangent(0))
                || HasSharpLeg(pc.End, b, -pc.Curve.Tangent(1)))
                errors.Add(PlacementError.SharpAngle);

            // junction legs are coplanar: an endpoint connecting to existing geometry
            // must arrive at its elevation (M8)
            if (BindingElevationClash(pc.Start, a) || BindingElevationClash(pc.End, b))
                errors.Add(PlacementError.VerticalClash);
        }

        // consecutive crossings landing on the SAME existing edge (one curve crossing
        // twice, or several curves of one proposal — e.g. a grid stamp) must be at
        // least that edge type's minimum apart, or commit would manufacture slivers
        foreach (var (edgeId, along) in hitsPerExistingEdge)
        {
            if (along.Count < 2 || !_edges.TryGetValue(edgeId, out var e))
                continue;
            float eMin = RoadCatalog.Get(e.Type).MinSegmentLength;
            along.Sort();
            for (int i = 0; i + 1 < along.Count; i++)
                if (along[i + 1] - along[i] < eMin - 0.1f)
                {
                    errors.Add(PlacementError.TooShort);
                    break;
                }
        }

        // kinks between curves of the same proposal that share an endpoint
        if (HasInternalKink(proposal))
            errors.Add(PlacementError.Kinked);

        errors = errors.Distinct().ToList();
        return new ValidatedPlacement(proposal, errors.Count == 0, errors, crossings, Version);
    }

    private bool BindingLeavesSliver(EndpointBinding binding, Vector3 pos)
    {
        switch (binding)
        {
            case EndpointBinding.OnEdge(var edgeId, var t) when _edges.TryGetValue(edgeId, out var e):
                return SplitLeavesSliver(e, t);
            case EndpointBinding.Free:
                // mirror ResolveBinding: a free endpoint near a node reuses it (clean
                // connection); near an edge it splits that edge, so sliver-check it
                if (FindNodeNear(pos, NodeReuseRadius) is not null)
                    return false;
                if (FindClosestEdge(pos, NodeReuseRadius) is { } hit)
                    return SplitLeavesSliver(_edges[hit.id], hit.t);
                return false;
            default:
                return false;
        }
    }

    /// <summary>An endpoint that will connect to an existing node/edge must arrive at
    /// its elevation (within JunctionYTolerance) — legs of a junction are coplanar (M8).
    /// Free endpoints resolve like ResolveBinding would (3D proximity, so a genuinely
    /// stacked endpoint finds nothing and is clean); explicit bindings carry the check.</summary>
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

    private static bool SplitLeavesSliver(RoadEdge e, float t)
    {
        float d = e.ArcLength.DistanceAtT(t);
        float min = RoadCatalog.Get(e.Type).MinSegmentLength;
        // within reuse radius of an end = clean node connection, not a split
        if (d <= NodeReuseRadius || e.ArcLength.TotalLength - d <= NodeReuseRadius)
            return false;
        return d < min || e.ArcLength.TotalLength - d < min;
    }

    /// <summary>Node-level half of <see cref="HasSharpLeg"/>'s rule, reusable at
    /// commit time once the actual attaching node is known. Carries the same
    /// TangentContinuationDeg exemption <see cref="NetworkInvariants.CheckLegAngles"/>
    /// applies unconditionally (not just <see cref="HasSharpLeg"/>'s fromEdge-only
    /// version): a segment landing here can itself be the ramp continuing tangentially
    /// off a just-split edge, and the near-0 deg pair that produces against the split's
    /// own other half is the same legal G1 shape as any other tangential departure, not
    /// a bump against an unrelated leg.</summary>
    private bool HasSharpLegAtNode(NodeId nodeId, Vector3 newLeaving)
    {
        if (!_nodes.TryGetValue(nodeId, out var node))
            return false;
        foreach (var legEdge in node.EdgeSet)
        {
            var e = _edges[legEdge];
            var leg = e.StartNode == nodeId ? e.Curve.Tangent(0) : -e.Curve.Tangent(1);
            float deg = AngleDegXZ(newLeaving, leg);
            if (deg <= TangentContinuationDeg)
                continue;
            if (deg < MinJunctionAngleDeg)
                return true;
        }
        return false;
    }

    private bool HasSharpLeg(EndpointBinding binding, Vector3 pos, Vector3 newLeaving)
    {
        foreach (var (leg, fromEdge) in ExistingLegDirections(binding, pos))
        {
            float deg = AngleDegXZ(newLeaving, leg);
            // near-tangential departure from mid-edge is a G1 continuation (ramp
            // exit), not a bump — legal. AtNode legs stay fully strict.
            if (fromEdge && deg <= TangentContinuationDeg)
                continue;
            if (deg < MinJunctionAngleDeg)
                return true;
        }
        return false;
    }

    private IEnumerable<(Vector3 dir, bool fromEdge)> ExistingLegDirections(EndpointBinding binding, Vector3 pos)
    {
        NodeId? nodeId = binding switch
        {
            EndpointBinding.AtNode(var id) when _nodes.ContainsKey(id) => id,
            _ => null,
        };
        if (nodeId is null && binding is EndpointBinding.OnEdge(var eid, var t) && _edges.TryGetValue(eid, out var onEdge))
        {
            var tan = onEdge.Curve.Tangent(t);
            yield return (tan, true);
            yield return (-tan, true);
            yield break;
        }
        nodeId ??= FindNodeNear(pos, NodeReuseRadius);
        if (nodeId is { } id2 && _nodes.TryGetValue(id2, out var node))
        {
            foreach (var legEdge in node.EdgeSet)
            {
                var e = _edges[legEdge];
                yield return (e.StartNode == id2 ? e.Curve.Tangent(0) : -e.Curve.Tangent(1), false);
            }
            yield break;
        }
        if (FindClosestEdge(pos, NodeReuseRadius) is { } hit)
        {
            var tan = _edges[hit.id].Curve.Tangent(hit.t);
            yield return (tan, true);
            yield return (-tan, true);
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
        int droppedSegments = 0;

        BeginBatch();
        foreach (var pc in placement.Proposal.Curves)
            CommitCurve(pc, placement.Proposal.Type, createdEdges, createdNodes, ref droppedSegments);
        // nodes created for stops whose every adjacent segment was dropped by the
        // sliver guard would linger edgeless — prune them before the delta fires
        foreach (var id in _batch!.NodesAdded
                     .Where(id => _nodes.TryGetValue(id, out var nd) && nd.EdgeSet.Count == 0).ToList())
        {
            _nodes.Remove(id);
            _batch!.NodesRemoved.Add(id);
        }
        EndBatch();

        // report only survivors (an edge created for one grid line may be split by the next)
        createdEdges.RemoveAll(e => !_edges.ContainsKey(e));
        createdNodes.RemoveAll(n => !_nodes.ContainsKey(n));
        return new CommitResult(true, createdEdges, createdNodes, null, droppedSegments);
    }

    private void CommitCurve(ProposedCurve pc, RoadTypeId type,
        List<EdgeId> createdEdges, List<NodeId> createdNodes, ref int droppedSegments)
    {
        var curve = pc.Curve;
        var startNode = ResolveBinding(pc.Start, curve.Point(0), createdNodes);
        var endNode = ResolveBinding(pc.End, curve.Point(1), createdNodes);
        // no resolution path may attach a new leg directly to a ring node (deferred) —
        // relocation can land here even when Validate's snapshot said otherwise
        if (IsRingNode(startNode) || IsRingNode(endNode))
        {
            droppedSegments++;
            return;
        }

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
                // vertical classification against the LIVE geometry (M8): cleared
                // crossings pass over/under with no split; the illegal band never
                // commits — drop, the standing policy for live-divergence
                switch (VerticalRules.ClassifyCrossing(p.Y, e.Curve.Point(hit.t2).Y))
                {
                    case CrossingKind.GradeSeparated:
                        continue;
                    case CrossingKind.VerticalClash:
                        droppedSegments++;
                        return;
                }
                // Validate blocks ring-edge crossings against its snapshot, but reuse
                // absorption can relocate endpoints far enough that a crossing it
                // exempted lands here against the live network — drop the whole curve
                // instead, same policy as the floor guards. (Approach crossings are
                // legal: the split re-keys the captured leg curve via OnApproachSplit.)
                if (IsRingEdge(e.Id))
                {
                    droppedSegments++;
                    return;
                }
                if (!hitsByEdge.TryGetValue(e.Id, out var list))
                    hitsByEdge[e.Id] = list = new List<(float, float)>();
                list.Add(hit);
            }
        }

        // a crossing on an approach whose split point would ABSORB into the ring node
        // (within the edge type's MinSegmentLength of that end) must not proceed —
        // absorption would attach this curve's stop directly to the ring. Checked
        // before any surgery so the whole curve drops cleanly.
        foreach (var (edgeId, hits) in hitsByEdge)
        {
            if (_roundabouts.Count == 0 || !_edges.TryGetValue(edgeId, out var he))
                continue;
            float eMin = RoadCatalog.Get(he.Type).MinSegmentLength;
            foreach (var (_, t2) in hits)
            {
                float dAlong = he.ArcLength.DistanceAtT(t2);
                if ((_nodes[he.StartNode].Ring != null && dAlong < eMin)
                    || (_nodes[he.EndNode].Ring != null && he.ArcLength.TotalLength - dAlong < eMin))
                {
                    droppedSegments++;
                    return;
                }
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

        var floors = RoadCatalog.Get(type);
        for (int i = 0; i + 1 < stops.Count; i++)
        {
            var (ta, na) = stops[i];
            var (tb, nb) = stops[i + 1];
            if (na == nb || tb - ta < 1e-5f)
                continue;
            var seg = SubCurve(curve, ta, tb, _nodes[na].Position, _nodes[nb].Position);
            if (seg.Length() < GeoConstants.Eps)
                continue;
            // Commit's documented contract: no sliver edges. Stop relocation from
            // reuse absorption (up to NodeReuseRadius against edges the validation
            // pass saw, up to the split edge's own MinSegmentLength against
            // same-batch siblings validation NEVER saw — e.g. one grid-stamp line
            // crossing another already-committed line of the same proposal) can
            // shrink a validated segment below the type's floors; such segments are
            // not built at all rather than committed corrupt. Thresholds mirror
            // NetworkInvariants.CheckEdgeGeometry's 0.1 slack.
            if (seg.Length() < floors.MinSegmentLength - 0.1f
                || BezierOps.MinRadius(seg) < floors.MinRadius - 0.1f)
            {
                droppedSegments++;
                continue;
            }
            // Same reuse-absorption hazard, for leg angles rather than length/radius:
            // SplitEdgeWithReuse snaps a crossing/endpoint to an existing end node
            // whenever it lands within that edge's OWN MinSegmentLength of the end —
            // up to tens of metres, far past the tight NodeReuseRadius Validate's
            // sharp-angle check assumed, and far enough for a merely MinRadius-curved
            // edge to have swung its tangent by well over MinJunctionAngleDeg between
            // the checked point and the actual end. Same hazard again for multi-curve
            // proposals (grid stamps): a sibling curve earlier in this same Commit can
            // create or populate a node that Validate's single pre-batch snapshot
            // never saw at all. Either way, Validate can wave through a leg that is
            // sharp against the node Commit is actually attaching to — checked here,
            // against the live network, where the true attachment point is known;
            // dropped rather than committed corrupt, same policy as the floors above.
            if (HasSharpLegAtNode(na, seg.Tangent(0)) || HasSharpLegAtNode(nb, -seg.Tangent(1)))
            {
                droppedSegments++;
                continue;
            }
            // Third member of the reuse-absorption recheck family (floors and leg angles
            // above): SubCurve's displacement blending toward an absorbed stop can drag
            // the segment far enough sideways that it RE-crosses the very edge whose
            // crossing was just absorbed into a node — committing an off-node crossing
            // no later pass would ever split (fuzz seeds 101@8321, 202@8673, visible
            // once the no-crossing invariant existed). Same policy: drop, never commit
            // corrupt.
            if (SegmentCrossesLiveEdgeOffNode(seg, na, nb))
            {
                droppedSegments++;
                continue;
            }
            createdEdges.Add(AddEdgeInternal(na, nb, seg, type).Id);
        }
    }

    /// <summary>True when the candidate segment genuinely crosses a live edge away from
    /// its own junction connections — the same coincidence/grazing filters as
    /// <see cref="NetworkInvariants.CheckEdgeCrossings"/> (Intersections emits garbage
    /// parameters for near-collinear contacts; sub-5° grazing between G1 pairs is not a
    /// transversal crossing). Endpoint contact is exempt ONLY for edges incident to that
    /// endpoint's node: an edge merely passing near the node (fuzz seed 202@8673: 0.46 m —
    /// inside Validate's 0.5 m endpoint filter) is a real drive-through crossing, not a
    /// connection, because ResolveBinding bound the endpoint to the node and never split
    /// the passing edge.</summary>
    private bool SegmentCrossesLiveEdgeOffNode(in Bezier3 seg, NodeId na, NodeId nb)
    {
        Vector3 aPos = _nodes[na].Position, bPos = _nodes[nb].Position;
        var segMin = Vector3.Min(Vector3.Min(seg.P0, seg.P1), Vector3.Min(seg.P2, seg.P3));
        var segMax = Vector3.Max(Vector3.Max(seg.P0, seg.P1), Vector3.Max(seg.P2, seg.P3));
        foreach (var e in _edges.Values)
        {
            var c = e.Curve;
            var min = Vector3.Min(Vector3.Min(c.P0, c.P1), Vector3.Min(c.P2, c.P3));
            var max = Vector3.Max(Vector3.Max(c.P0, c.P1), Vector3.Max(c.P2, c.P3));
            if (min.X > segMax.X || segMin.X > max.X || min.Z > segMax.Z || segMin.Z > max.Z)
                continue;
            bool incidentA = e.StartNode == na || e.EndNode == na;
            bool incidentB = e.StartNode == nb || e.EndNode == nb;
            foreach (var (t1, t2) in BezierOps.Intersections(seg, c))
            {
                var p = seg.Point(t1);
                var q = c.Point(t2);
                // Intersections is XZ-projected: coincidence must be measured in XZ,
                // with the Y difference feeding the vertical classification (M8) —
                // a 3D distance here would silently exempt every clash-band crossing
                if (new Vector2(p.X - q.X, p.Z - q.Z).Length() > 0.5f)
                    continue; // spurious hit, curves not actually at the same XZ place
                if (VerticalRules.ClassifyCrossing(p.Y, q.Y) == CrossingKind.GradeSeparated)
                    continue; // legal over/under pass, not a drive-through crossing
                if (incidentA && Vector3.Distance(p, aPos) <= 1f)
                    continue; // junction contact with an edge sharing the start node
                if (incidentB && Vector3.Distance(p, bPos) <= 1f)
                    continue; // junction contact with an edge sharing the end node
                var ta = seg.Tangent(t1);
                var tb = c.Tangent(t2);
                float cos = MathF.Abs(Vector3.Dot(Vector3.Normalize(ta), Vector3.Normalize(tb)));
                if (MathF.Acos(Math.Clamp(cos, 0f, 1f)) * 180f / MathF.PI < 5f)
                    continue; // grazing/parallel contact, not a transversal crossing
                return true;
            }
        }
        return false;
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
                // never split a RING edge (Validate blocks this against its snapshot;
                // the stale-binding fallback above could dodge that) — fall through to
                // the free-endpoint path, which is ownership-guarded too. Approaches
                // split like ordinary roads (a resolution landing ON the ring node is
                // caught by CommitCurve's post-resolve guard).
                if (!IsRingEdge(edgeId))
                {
                    var (node, _) = SplitEdgeWithReuse(edgeId, t, createdNodes);
                    return node;
                }
                break;
            }
        }

        // free endpoint (or unresolvable binding): reuse a nearby node, else connect
        // to an edge passing through this point, else create a fresh node.
        // Ring nodes and ring edges are never attachment targets.
        if (FindNodeNear(pos, NodeReuseRadius) is { } near && !IsRingNode(near))
            return near;
        if (FindClosestEdge(pos, NodeReuseRadius) is { } onEdge && !IsRingEdge(onEdge.id))
        {
            var (node, _) = SplitEdgeWithReuse(onEdge.id, onEdge.t, createdNodes);
            return node;
        }
        var created = AddNodeInternal(pos);
        createdNodes.Add(created.Id);
        return created.Id;
    }

    /// <summary>Split an edge at local parameter t, unless the split point is within
    /// the edge type's MinSegmentLength of an end — then that end's node is reused and
    /// nothing is split. Returns the node at the split point and, if a split happened,
    /// the edge covering the part after t (for remapping subsequent split params).</summary>
    private (NodeId node, EdgeId? after) SplitEdgeWithReuse(EdgeId edgeId, float t,
        List<NodeId>? createdNodes)
    {
        var edge = _edges[edgeId];
        float minLen = RoadCatalog.Get(edge.Type).MinSegmentLength;
        float d = edge.ArcLength.DistanceAtT(t);
        if (d < minLen)
            return (edge.StartNode, null);
        if (edge.ArcLength.TotalLength - d < minLen)
            return (edge.EndNode, null);

        var (a, b) = edge.Curve.Split(t);
        var mid = AddNodeInternal(edge.Curve.Point(t));
        createdNodes?.Add(mid.Id);

        RemoveEdgeInternal(edge);
        var ea = AddEdgeInternal(edge.StartNode, mid.Id, a, edge.Type);
        var eb = AddEdgeInternal(mid.Id, edge.EndNode, b, edge.Type);
        // splitting a tracked roundabout approach re-keys its captured full curve onto
        // the child still attached to the ring, so regeneration stays lossless
        if (_roundabouts.Count > 0)
            OnApproachSplit(edge.Id, ea, eb, mid.Position);
        return (mid.Id, eb.Id);
    }

    /// <summary>Extract curve range [ta, tb] and pin its endpoints to the given
    /// node positions (which may differ after node reuse). The displacement is
    /// blended linearly across the whole control net — moving only P0/P3 while
    /// keeping the interior control points bends the curve (a straight source line
    /// can come out kinked with a near-zero radius when a stop is relocated by
    /// reuse absorption); the blend keeps straight lines straight and preserves
    /// the curve's shape under endpoint relocation.</summary>
    private static Bezier3 SubCurve(in Bezier3 curve, float ta, float tb, Vector3 posA, Vector3 posB)
    {
        var (_, rest) = curve.Split(ta);
        float tbLocal = ta >= 1f ? 1f : (tb - ta) / (1 - ta);
        var (seg, _) = rest.Split(Math.Clamp(tbLocal, 0f, 1f));
        var dA = posA - seg.P0;
        var dB = posB - seg.P3;
        return new Bezier3(
            posA,
            seg.P1 + dA * (2f / 3f) + dB * (1f / 3f),
            seg.P2 + dA * (1f / 3f) + dB * (2f / 3f),
            posB);
    }

    // ---------------------------------------------------------------- removal

    public void RemoveEdge(EdgeId id)
    {
        if (!_edges.TryGetValue(id, out var edge))
            return;

        // any roundabout touched by this removal (approach or ring edge) re-arcs afterward
        foreach (var nid in new[] { edge.StartNode, edge.EndNode })
            if (_nodes.TryGetValue(nid, out var nd) && nd.Ring is { } rid)
                _dirtyRoundabouts.Add(rid);

        BeginBatch();
        RemoveEdgeInternal(edge);
        foreach (var nodeId in new[] { edge.StartNode, edge.EndNode })
            HandleNodeAfterRemoval(nodeId);
        EndBatch();

        DrainDirtyRoundabouts();
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
        if (node.Ring != null)
            return; // ring nodes are structural — a degree-2 ring node must never merge its arcs
        if (node.EdgeSet.Count != 2)
            return;
        // deterministic pair order (HashSet iteration order decided merge
        // orientation before M7 — the one-way reversal bug)
        var edges = node.EdgeSet.Select(e => _edges[e]).OrderBy(e => e.Id.Value).ToArray();
        if (edges[0].Type != edges[1].Type)
            return;

        if (RoadCatalog.Get(edges[0].Type).IsDirectionAsymmetric)
        {
            // heal only when flow is continuous through the node: exactly one edge
            // ends here (upstream) and the other starts here (downstream); merge
            // upstream-first so the healed curve keeps the travel direction
            bool in0 = edges[0].EndNode == node.Id;
            bool in1 = edges[1].EndNode == node.Id;
            if (in0 == in1)
                return; // both inbound or both outbound: flows oppose, keep the node
            if (!in0)
                (edges[0], edges[1]) = (edges[1], edges[0]);
        }

        var (merged, maxError) = CurveFit.FitComposite(edges[0], edges[1], node.Id, _nodes);
        if (maxError > GeoConstants.MergeTolerance)
            return;
        // the fit constrains shape, not slope: a composite over a crest could exceed
        // the type's gradient — refuse the merge rather than heal corrupt (M8)
        if (VerticalRules.MaxGradient(merged) > RoadCatalog.Get(edges[0].Type).MaxGradient + 0.005f)
            return;

        var farA = edges[0].OtherNode(node.Id);
        var farB = edges[1].OtherNode(node.Id);
        if (farA == farB)
            return; // would create a loop edge; keep the node
        if (_nodes[farA].Ring != null || _nodes[farB].Ring != null)
            return; // merged edge would attach to a ring node — a roundabout-owned approach
                    // must not be recreated with a new EdgeId; keep the node

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
        public readonly HashSet<EdgeId> EdgesAdded = new(), EdgesRemoved = new(), EdgesChanged = new();
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

        // edges edited in place this batch (trimmed roundabout legs): re-mesh like adds,
        // but only those that still exist and weren't already added/removed this batch
        var edgesChanged = new HashSet<EdgeId>(b.EdgesChanged
            .Where(id => _edges.ContainsKey(id) && !b.EdgesAdded.Contains(id) && !b.EdgesRemoved.Contains(id)));

        _batch = null;
        Version++;
        Changed?.Invoke(new NetworkDelta(b.EdgesAdded, b.EdgesRemoved, b.NodesAdded, b.NodesRemoved, changed)
        { EdgesChanged = edgesChanged });
    }

    /// <summary>Apply an authored junction configuration (control mode, per-leg roles,
    /// size offsets). Overrides for edges no longer connected are pruned. Rebuilds the
    /// node's derived data and raises a change event.</summary>
    public void ConfigureJunction(NodeId id, JunctionConfig config)
    {
        if (!_nodes.TryGetValue(id, out var node))
            throw new ArgumentException($"unknown node {id}");
        if (node.Ring != null)
            return; // ring-node control is owned by the roundabout, not hand-editable
        node.Config = Prune(config, node.EdgeSet);
        RebuildDerived(node);
        Version++;
        Changed?.Invoke(new NetworkDelta(
            new HashSet<EdgeId>(), new HashSet<EdgeId>(),
            new HashSet<NodeId>(), new HashSet<NodeId>(),
            new HashSet<NodeId> { id }));
    }

    /// <summary>Change a road's type in place (M7 upgrade tool). The EdgeId — and
    /// therefore every EdgeId-keyed junction override — survives; lanes regenerate
    /// with fresh ids (vehicles on them are dropped by TrafficSim.Sync, like CS2
    /// despawning on replace). Returns null on success.</summary>
    public RetypeError? RetypeEdge(EdgeId id, RoadTypeId newType)
    {
        if (!_edges.TryGetValue(id, out var edge))
            return RetypeError.UnknownEdge;
        if (IsRingEdge(id))
            return RetypeError.Locked; // ring edges are owned by the roundabout
        if (edge.Type == newType)
            return RetypeError.SameType;
        var type = RoadCatalog.Get(newType);
        if (edge.ArcLength.TotalLength < type.MinSegmentLength)
            return RetypeError.TooShort;
        if (BezierOps.MinRadius(edge.Curve) < type.MinRadius)
            return RetypeError.TooTight;

        ReplaceEdgeInPlace(edge, edge.StartNode, edge.EndNode, edge.Curve, newType);
        return null;
    }

    /// <summary>Reverse a road's travel direction in place (M7 upgrade tool, the
    /// one-way flip). Same-id replacement — junction configs survive; lanes
    /// regenerate. Symmetric types re-derive an equivalent road.</summary>
    public bool FlipEdge(EdgeId id)
    {
        if (!_edges.TryGetValue(id, out var edge))
            return false;
        if (IsRingEdge(id))
            return false; // ring edges are owned by the roundabout
        ReplaceEdgeInPlace(edge, edge.EndNode, edge.StartNode, edge.Curve.Reversed(), edge.Type);
        // an approach's captured full curve must track the flip, or regeneration would
        // re-trim from the pre-flip orientation and silently reverse the road back
        OnApproachFlipped(id);
        return true;
    }

    /// <summary>Swap a same-id RoadEdge into the network (retype/flip), regenerate
    /// its lanes, rebuild both end nodes, and raise an EdgesChanged delta.</summary>
    private void ReplaceEdgeInPlace(RoadEdge old, NodeId start, NodeId end,
        in Bezier3 curve, RoadTypeId type)
    {
        var replacement = new RoadEdge(old.Id, start, end, curve, type);
        replacement.Lanes = RoadCatalog.Get(type).Lanes
            .Select(spec => new Lane(new LaneId(_nextLane++), replacement.Id,
                spec.Offset, spec.Direction, spec.Width, spec.Kind))
            .ToArray();
        _edges[old.Id] = replacement;
        // EdgeSets key by id — nothing to update there even when start/end swap (flip)
        foreach (var nodeId in new[] { start, end }.Distinct())
            if (_nodes.TryGetValue(nodeId, out var node))
                RebuildDerived(node);
        Version++;
        Changed?.Invoke(new NetworkDelta(
            new HashSet<EdgeId>(), new HashSet<EdgeId>(),
            new HashSet<NodeId>(), new HashSet<NodeId>(),
            new HashSet<NodeId> { start, end })
        { EdgesChanged = new HashSet<EdgeId> { old.Id } });
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
        // Ring-node control is derived fresh from ring membership every rebuild (not stored
        // per-edge), so any approach churn — split, heal, retype, flip — that changes an
        // approach's EdgeId still yields on entry after the node is touched.
        node.Config = node.Ring != null ? RingNodeConfig(node) : Prune(node.Config, node.EdgeSet);
        node.Junction = JunctionBuilder.Build(node, _edges);
        node.Connectors = ConnectorBuilder.Build(node, _edges);
        node.ConnectorConflicts = ConnectorBuilder.BuildConflicts(node.Connectors);
    }
}
