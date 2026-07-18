using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

/// <summary>
/// Shared health checker for a <see cref="RoadNetwork"/>: everything a regression test,
/// a debug overlay, or a fuzz harness would want to assert about geometry and lane
/// wiring, in one place. <see cref="Check"/> is the entry point; the rule methods below
/// it are pure and individually unit-testable. Thresholds mirror the ones
/// <see cref="RoadNetwork.Validate"/> already enforces at commit time — this is a
/// second, independent pass over already-committed state (e.g. after edits, merges, or
/// procedural generation), not a duplicate of validation logic.
/// </summary>
public static class NetworkInvariants
{
    /// <summary>All violations in the network; empty = healthy. Messages contain
    /// ids and numbers, suitable for direct assertion output.</summary>
    public static IReadOnlyList<string> Check(RoadNetwork n)
    {
        var o = new List<string>();

        foreach (var e in n.Edges.Values)
            CheckEdgeGeometry(e, RoadCatalog.Get(e.Type), o);

        foreach (var node in n.Nodes.Values)
        {
            CheckJunctionData(node, o);

            if (node.Edges.Count >= 2)
            {
                var legDirs = node.Edges.Select(edgeId =>
                {
                    var e = n.Edges[edgeId];
                    return e.StartNode == node.Id ? e.Curve.Tangent(0) : -e.Curve.Tangent(1);
                }).ToArray();
                CheckLegAngles(node.Id, legDirs, o);
                CheckLaneCoverage(node, n.Edges, o);
            }

            if (node.Edges.Count >= 3)
                CheckStraightCapacity(node, n.Edges, o);
        }

        foreach (var type in RoadCatalog.All)
        {
            float half = type.Width / 2;
            foreach (var (offset, _) in MarkingRules.Layout(type))
                if (MathF.Abs(offset) > half)
                    o.Add($"type {type.Id.Value}: marking offset {offset:F2} outside +/-{half:F2}");
        }

        CheckRoundabouts(n, o);
        CheckEdgeCrossings(n, o);

        return o;
    }

    /// <summary>No two edges may geometrically cross except at a node they share — the
    /// commit pipeline turns every crossing into a shared node by splitting, so any other
    /// intersection is corrupt geometry (raw surgery, a bad conversion, a corrupt save)
    /// that vehicles would drive straight through. AABB prefilter (Bézier hull property)
    /// keeps the pairwise scan affordable for the fuzzer's per-action checks; the shared-
    /// node tolerance covers junction-area contact between legs of the same node.</summary>
    public static void CheckEdgeCrossings(RoadNetwork n, List<string> o)
    {
        const float sharedNodeTolerance = 1.0f;
        var edges = n.Edges.Values.ToArray();
        var min = new Vector3[edges.Length];
        var max = new Vector3[edges.Length];
        for (int i = 0; i < edges.Length; i++)
        {
            var c = edges[i].Curve;
            min[i] = Vector3.Min(Vector3.Min(c.P0, c.P1), Vector3.Min(c.P2, c.P3));
            max[i] = Vector3.Max(Vector3.Max(c.P0, c.P1), Vector3.Max(c.P2, c.P3));
        }

        for (int i = 0; i < edges.Length; i++)
        for (int j = i + 1; j < edges.Length; j++)
        {
            if (min[i].X > max[j].X || min[j].X > max[i].X
                || min[i].Z > max[j].Z || min[j].Z > max[i].Z)
                continue;
            var a = edges[i];
            var b = edges[j];
            foreach (var (t1, t2) in BezierOps.Intersections(a.Curve, b.Curve))
            {
                var p = a.Curve.Point(t1);
                // Intersections can report spurious hits with garbage parameters for
                // near-collinear curves touching at a shared endpoint (chain segments) —
                // a real intersection has both curves at the same place; anything else
                // is numerical noise, not geometry (fuzz seed 303 @ 72, flip edge=59)
                if (Vector3.Distance(p, b.Curve.Point(t2)) > 0.5f)
                    continue;
                // Grazing contact is not a transversal crossing: two legs leaving a shared
                // node within the G1 tangent-continuation exemption legally run near-
                // parallel and can graze-cross as they diverge (fuzz seed 303 @ 179).
                // That class belongs to OverlapsExisting's heuristic, not this rule —
                // only crossings at a genuine angle are the drive-through corruption
                // this invariant exists to catch.
                var ta = a.Curve.Tangent(t1);
                var tb = b.Curve.Tangent(t2);
                float crossDeg = MathF.Acos(Math.Clamp(MathF.Abs(
                    Vector3.Dot(Vector3.Normalize(ta), Vector3.Normalize(tb))), 0f, 1f)) * 180f / MathF.PI;
                if (crossDeg < 5f)
                    continue;
                bool nearShared = false;
                foreach (var nid in new[] { a.StartNode, a.EndNode })
                    if ((nid == b.StartNode || nid == b.EndNode)
                        && n.Nodes.TryGetValue(nid, out var node)
                        && Vector3.Distance(p, node.Position) <= sharedNodeTolerance)
                    {
                        nearShared = true;
                        break;
                    }
                if (!nearShared)
                {
                    o.Add($"edges {a.Id.Value}/{b.Id.Value}: cross at ({p.X:F1},{p.Z:F1}) without a shared node");
                    break; // one report per pair is enough
                }
            }
        }
    }

    /// <summary>Structural health of every roundabout: ring nodes/edges consistent with
    /// the registry, ring edges a single OneWay CCW cycle, and each approach-carrying ring
    /// node yields its approach to circulating traffic. Ring-tagged nodes must map back to
    /// a roundabout that lists them (no orphan tags).</summary>
    public static void CheckRoundabouts(RoadNetwork n, List<string> o)
    {
        // membership sets up front — the fuzzer runs this after every action, so the
        // orphan-tag sweep must not be O(ringNodes²) List.Contains scans
        var ringNodeSets = n.Roundabouts.Values
            .ToDictionary(rb => rb.Id, rb => rb.RingNodes.ToHashSet());

        foreach (var node in n.Nodes.Values)
            if (node.Ring is { } rid)
            {
                if (!ringNodeSets.TryGetValue(rid, out var members))
                    o.Add($"node {node.Id.Value}: Ring={rid.Value} not in registry");
                else if (!members.Contains(node.Id))
                    o.Add($"node {node.Id.Value}: Ring={rid.Value} but absent from its RingNodes");
            }

        foreach (var rb in n.Roundabouts.Values)
        {
            if (rb.RingNodes.Count < 3)
                o.Add($"roundabout {rb.Id.Value}: only {rb.RingNodes.Count} ring nodes (< 3)");
            if (rb.RingEdges.Count != rb.RingNodes.Count)
                o.Add($"roundabout {rb.Id.Value}: {rb.RingEdges.Count} ring edges vs {rb.RingNodes.Count} ring nodes (not a simple cycle)");

            var ringEdgeSet = rb.RingEdges.ToHashSet();
            var ringNodeSet = rb.RingNodes.ToHashSet();

            foreach (var reId in rb.RingEdges)
            {
                if (!n.Edges.TryGetValue(reId, out var re))
                {
                    o.Add($"roundabout {rb.Id.Value}: ring edge {reId.Value} missing");
                    continue;
                }
                if (re.Type != RoadCatalog.OneWay.Id)
                    o.Add($"roundabout {rb.Id.Value}: ring edge {reId.Value} type {re.Type.Value} is not OneWay");
                if (!ringNodeSet.Contains(re.StartNode) || !ringNodeSet.Contains(re.EndNode))
                    o.Add($"roundabout {rb.Id.Value}: ring edge {reId.Value} has a non-ring endpoint");
            }

            foreach (var rnId in rb.RingNodes)
            {
                if (!n.Nodes.TryGetValue(rnId, out var rn))
                {
                    o.Add($"roundabout {rb.Id.Value}: ring node {rnId.Value} missing");
                    continue;
                }
                if (rn.Ring != rb.Id)
                    o.Add($"roundabout {rb.Id.Value}: ring node {rnId.Value} not tagged to it");

                // single pass, no allocation — this runs per ring node on every fuzz action
                int ringLegs = 0, approachCount = 0;
                EdgeId firstApproach = default;
                foreach (var e in rn.Edges)
                {
                    if (ringEdgeSet.Contains(e))
                        ringLegs++;
                    else if (approachCount++ == 0)
                        firstApproach = e;
                }
                if (ringLegs != 2)
                    o.Add($"roundabout {rb.Id.Value}: ring node {rnId.Value} has {ringLegs} ring legs (expected 2)");
                if (approachCount > 1)
                    o.Add($"roundabout {rb.Id.Value}: ring node {rnId.Value} has {approachCount} approaches (expected 0 or 1)");
                if (approachCount == 1
                    && (rn.Config.Mode != JunctionControlMode.PrioritySigns
                        || !rn.Config.RoleOverrides.TryGetValue(firstApproach, out var role) || role != LegRole.Yield))
                    o.Add($"roundabout {rb.Id.Value}: ring node {rnId.Value} approach {firstApproach.Value} does not yield");
            }
        }
    }

    /// <summary>Edge must meet its road type's minimum committable length and minimum
    /// curvature radius (with the same 0.1 m slack <see cref="RoadNetwork.Validate"/> allows).</summary>
    public static void CheckEdgeGeometry(RoadEdge e, RoadType type, List<string> outViolations)
    {
        float len = e.Curve.Length();
        float minLen = type.MinSegmentLength - 0.1f;
        if (len < minLen)
            outViolations.Add($"edge {e.Id.Value}: length {len:F1} < min {minLen:F1}");

        float radius = BezierOps.MinRadius(e.Curve);
        float minRadius = type.MinRadius - 0.1f;
        if (radius < minRadius)
            outViolations.Add($"edge {e.Id.Value}: radius {radius:F1} < min {minRadius:F1}");
    }

    /// <summary>No two legs meeting at a node may be closer than
    /// <see cref="RoadNetwork.MinJunctionAngleDeg"/> (with 0.5° slack) apart, measured
    /// between their outward (away-from-node) directions — EXCEPT a pair within
    /// <see cref="RoadNetwork.TangentContinuationDeg"/> (+ slack) of each other, which
    /// mirrors <c>RoadNetwork.Validate</c>'s G1 ramp-exit exemption for departures from
    /// an <c>OnEdge</c> binding (see <c>HasSharpLeg</c>/<c>ExistingLegDirections</c>):
    /// splitting an edge to grow a tangential ramp legitimately leaves the ramp 0° from
    /// the split half it continues, and that is the ONLY way a validly committed
    /// network ever gets two legs this close — anything wider than the tolerance but
    /// still under the minimum was never legal and stays flagged.</summary>
    public static void CheckLegAngles(NodeId node, IReadOnlyList<Vector3> legDirs, List<string> o)
    {
        float min = RoadNetwork.MinJunctionAngleDeg - 0.5f;
        float tangentContinuation = RoadNetwork.TangentContinuationDeg + 0.5f;
        for (int i = 0; i < legDirs.Count; i++)
        for (int j = i + 1; j < legDirs.Count; j++)
        {
            float deg = AngleDegXZ(legDirs[i], legDirs[j]);
            if (deg <= tangentContinuation)
                continue;
            if (deg < min)
                o.Add($"node {node.Value}: legs {i}/{j} angle {deg:F1} < min {min:F1}");
        }
    }

    /// <summary>Junction geometry sanity: every cut parameter lies within the edge's
    /// own [0,1] range, and connector conflicts are symmetric (if i conflicts with j,
    /// j conflicts with i).</summary>
    public static void CheckJunctionData(RoadNode node, List<string> o)
    {
        foreach (var (edgeId, t) in node.Junction.CutT)
            if (t < 0f || t > 1f)
                o.Add($"node {node.Id.Value}: CutT[{edgeId.Value}] = {t:F2} outside [0,1]");

        var conflicts = node.ConnectorConflicts;
        for (int i = 0; i < conflicts.Count; i++)
        foreach (var cp in conflicts[i])
        {
            int j = cp.Other;
            if (j < 0 || j >= conflicts.Count)
            {
                o.Add($"node {node.Id.Value}: connector {i} conflict references out-of-range index {j}");
                continue;
            }
            if (!conflicts[j].Any(back => back.Other == i))
                o.Add($"node {node.Id.Value}: connector conflict {i}->{j} is not symmetric");
        }
    }

    /// <summary>Every arriving driving lane at a node with >= 2 edges must have at
    /// least one outgoing connector IF AND ONLY IF the node offers >= 1 departing
    /// driving lane on a DIFFERENT edge (U-turns are not junction movements, so
    /// same-edge departures don't count). When no other edge can receive, the lane
    /// is LEGALLY stranded — spec amendment 2026-07-16 (CS2-style, user-decided):
    /// direction-asymmetric road types can create lanes with categorically zero
    /// destinations (e.g. a two-way continuing past a one-way's end, or bulldozing
    /// arms off a junction); such placements commit, routing simply never uses the
    /// stranded lane, and a later milestone adds visual feedback. Stranding a lane
    /// when receiving capacity DOES exist remains a hard violation.</summary>
    public static void CheckLaneCoverage(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges, List<string> o)
    {
        var withConnectors = node.Connectors.Select(c => c.From).ToHashSet();

        // driving lanes departing the node, per edge (a lane departs if travelling
        // away from this node: Forward lanes on edges starting here, Backward on
        // edges ending here)
        var departingByEdge = node.Edges.ToDictionary(edgeId => edgeId, edgeId =>
        {
            var e = edges[edgeId];
            bool startsHere = e.StartNode == node.Id;
            return e.Lanes.Count(l => l.Kind == LaneKind.Driving
                && ((l.Direction == LaneDirection.Forward) ? startsHere : !startsHere));
        });

        foreach (var edgeId in node.Edges)
        {
            var edge = edges[edgeId];
            bool startsHere = edge.StartNode == node.Id;
            bool otherEdgeReceives = departingByEdge.Any(kv => kv.Key != edgeId && kv.Value > 0);
            foreach (var lane in edge.Lanes.Where(l => l.Kind == LaneKind.Driving
                && ((l.Direction == LaneDirection.Forward) ? !startsHere : startsHere)))
            {
                if (otherEdgeReceives && !withConnectors.Contains(lane.Id))
                    o.Add($"node {node.Id.Value}: lane {lane.Id.Value} on edge {edge.Id.Value} arrives with no outgoing connector");
            }
        }
    }

    /// <summary>The standing guard behind the M5 arrow bug: whatever the mix of road
    /// types, an approach never sends more straight connectors into an arm than that
    /// arm has receiving driving lanes — UNLESS the approach had no left/right
    /// alternative to shed the surplus into. This mirrors ConnectorBuilder's own
    /// documented drop logic exactly: surplus straight lanes drop into a dedicated
    /// left (then right) turn when such an arm with receiving capacity exists, and
    /// "lanes with neither alternative keep a merge-straight rather than going dead"
    /// (never-strand, spec amendment 2026-07-16). So surplus is only a violation when
    /// the assignment demonstrably could have done better: allowed sources =
    /// max(receiving capacity, approach lanes − availableLeft − availableRight).</summary>
    public static void CheckStraightCapacity(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges, List<string> o)
    {
        var laneById = node.Edges.SelectMany(edgeId => edges[edgeId].Lanes).ToDictionary(l => l.Id, l => l);

        foreach (var group in node.Connectors
            .Where(c => c.Turn == TurnKind.Straight && laneById[c.From].Kind == LaneKind.Driving)
            .GroupBy(c => (From: laneById[c.From].Edge, To: laneById[c.To].Edge)))
        {
            int sources = group.Select(c => c.From).Distinct().Count();
            var target = edges[group.Key.To];
            bool leavesAtNode = target.StartNode == node.Id;
            int capacity = target.Lanes.Count(l => l.Kind == LaneKind.Driving
                && (l.Direction == LaneDirection.Forward) == leavesAtNode);
            if (sources <= capacity)
                continue;

            // merge-fallback allowance: a left/right arm exists for this approach
            // iff the builder emitted a Left/Right driving connector from it (it
            // always does when such an arm has receiving capacity — the leftmost/
            // rightmost lane is unconditionally entitled to it)
            var approach = edges[group.Key.From];
            bool arrivesForward = approach.EndNode == node.Id;
            int n = approach.Lanes.Count(l => l.Kind == LaneKind.Driving
                && (l.Direction == LaneDirection.Forward) == arrivesForward);
            bool hasLeft = node.Connectors.Any(c => c.Turn == TurnKind.Left
                && laneById[c.From].Kind == LaneKind.Driving && laneById[c.From].Edge == group.Key.From);
            bool hasRight = node.Connectors.Any(c => c.Turn == TurnKind.Right
                && laneById[c.From].Kind == LaneKind.Driving && laneById[c.From].Edge == group.Key.From);
            int allowed = Math.Max(capacity, n - (hasLeft ? 1 : 0) - (hasRight ? 1 : 0));
            if (sources > allowed)
                o.Add($"node {node.Id.Value}: {sources} straight source lanes into {capacity} receiving on edge {target.Id.Value}");
        }
    }

    private static float AngleDegXZ(Vector3 u, Vector3 v)
    {
        float cross = MathF.Abs(u.X * v.Z - u.Z * v.X);
        float dot = u.X * v.X + u.Z * v.Z; // signed: 0 deg = same direction, 180 deg = opposite
        return MathF.Atan2(cross, dot) * 180f / MathF.PI;
    }
}
