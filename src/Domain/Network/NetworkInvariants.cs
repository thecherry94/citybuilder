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

        return o;
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
    /// between their outward (away-from-node) directions.</summary>
    public static void CheckLegAngles(NodeId node, IReadOnlyList<Vector3> legDirs, List<string> o)
    {
        float min = RoadNetwork.MinJunctionAngleDeg - 0.5f;
        for (int i = 0; i < legDirs.Count; i++)
        for (int j = i + 1; j < legDirs.Count; j++)
        {
            float deg = AngleDegXZ(legDirs[i], legDirs[j]);
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

    /// <summary>Every arriving driving lane at a real junction (>= 2 edges) must have
    /// at least one outgoing connector — otherwise traffic reaching that lane has
    /// nowhere to go.</summary>
    public static void CheckLaneCoverage(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges, List<string> o)
    {
        var withConnectors = node.Connectors.Select(c => c.From).ToHashSet();
        foreach (var edgeId in node.Edges)
        {
            var edge = edges[edgeId];
            bool startsHere = edge.StartNode == node.Id;
            foreach (var lane in edge.Lanes.Where(l => l.Kind == LaneKind.Driving
                && ((l.Direction == LaneDirection.Forward) ? !startsHere : startsHere)))
            {
                if (!withConnectors.Contains(lane.Id))
                    o.Add($"node {node.Id.Value}: lane {lane.Id.Value} on edge {edge.Id.Value} arrives with no outgoing connector");
            }
        }
    }

    /// <summary>The standing guard behind the M5 arrow bug: whatever the mix of road
    /// types, an approach never sends more straight connectors into an arm than that
    /// arm has receiving driving lanes.</summary>
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
            if (sources > capacity)
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
