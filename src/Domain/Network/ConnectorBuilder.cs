using System.Numerics;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

/// <summary>
/// Generates lane connectors at a node: for every lane arriving at the node, one
/// connector curve to every departing lane on a different edge (no U-turns at
/// junctions; dead ends allow U-turns so cul-de-sacs stay navigable).
/// Turn restrictions will later filter this set; the geometry is what vehicles follow.
/// Junction geometry must be up to date first — connector endpoints sit at the
/// junction cuts.
/// </summary>
public static class ConnectorBuilder
{
    public static IReadOnlyList<LaneConnector> Build(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)
    {
        if (node.Edges.Count == 0)
            return Array.Empty<LaneConnector>();
        bool deadEnd = node.Edges.Count == 1;

        var incoming = new List<(Lane lane, Vector3 pos, Vector3 dir)>();
        var outgoing = new List<(Lane lane, Vector3 pos, Vector3 dir)>();

        foreach (var edgeId in node.Edges)
        {
            var edge = edges[edgeId];
            bool startsHere = edge.StartNode == node.Id;
            float tCut = node.Junction.CutT.TryGetValue(edgeId, out var t) ? t : (startsHere ? 0f : 1f);
            var tangent = edge.Curve.Tangent(tCut);

            foreach (var lane in edge.Lanes)
            {
                var pos = edge.Curve.OffsetPoint(tCut, lane.Offset);
                bool arrives = lane.Direction == LaneDirection.Forward ? !startsHere : startsHere;
                var travelDir = lane.Direction == LaneDirection.Forward ? tangent : -tangent;
                if (arrives)
                    incoming.Add((lane, pos, travelDir));
                else
                    outgoing.Add((lane, pos, travelDir));
            }
        }

        var control = JunctionControl.Resolve(node, edges);

        var connectors = new List<LaneConnector>(incoming.Count * Math.Max(0, outgoing.Count - 1));
        foreach (var (inLane, inPos, inDir) in incoming)
        foreach (var (outLane, outPos, outDir) in outgoing)
        {
            if (inLane.Kind != outLane.Kind)
                continue; // bikes connect to bikes, sidewalks to sidewalks
            if (inLane.Edge == outLane.Edge && !deadEnd)
                continue; // no U-turns except at dead ends
            float reach = MathF.Max(Vector3.Distance(inPos, outPos) / 3f, 0.1f);
            var curve = new Bezier3(inPos, inPos + inDir * reach, outPos - outDir * reach, outPos);
            connectors.Add(new LaneConnector(
                inLane.Id, outLane.Id, curve, Classify(inDir, outDir), RowFor(control, inLane.Edge)));
        }
        return connectors;
    }

    /// <summary>Pairwise conflicts between a node's connectors: curves that cross in
    /// XZ, or two different sources merging into the same target lane. Connectors
    /// sharing the source lane are queue-ordered, not conflicting.</summary>
    public static IReadOnlyList<int[]> BuildConflicts(IReadOnlyList<LaneConnector> connectors)
    {
        var sets = new List<int>[connectors.Count];
        for (int i = 0; i < connectors.Count; i++)
            sets[i] = new List<int>();

        for (int i = 0; i < connectors.Count; i++)
        for (int j = i + 1; j < connectors.Count; j++)
        {
            var a = connectors[i];
            var b = connectors[j];
            if (a.From == b.From)
                continue;
            bool conflict = a.To == b.To
                || BezierOps.Intersections(a.Curve, b.Curve).Count > 0;
            if (conflict)
            {
                sets[i].Add(j);
                sets[j].Add(i);
            }
        }
        return sets.Select(s => s.ToArray()).ToArray();
    }

    /// <summary>Right-of-way class for a connector entering the node from the given
    /// edge, per the resolved junction control.</summary>
    private static RightOfWay RowFor(EffectiveControl control, EdgeId fromEdge)
        => control.Mode switch
        {
            JunctionControlMode.AllWayStop => RightOfWay.Stop,
            JunctionControlMode.TrafficLights => RightOfWay.Signal,
            JunctionControlMode.PrioritySigns => control.Roles.GetValueOrDefault(fromEdge) switch
            {
                LegRole.Yield => RightOfWay.Yield,
                LegRole.Stop => RightOfWay.Stop,
                _ => RightOfWay.Free,
            },
            _ => RightOfWay.Free,
        };

    /// <summary>Movement classification from the turn angle between entry and exit
    /// directions. Straight within ±30°, U-turn beyond ±150°, else left/right.</summary>
    private static TurnKind Classify(Vector3 inDir, Vector3 outDir)
    {
        float cross = inDir.X * outDir.Z - inDir.Z * outDir.X;
        float dot = inDir.X * outDir.X + inDir.Z * outDir.Z;
        float deg = MathF.Atan2(cross, dot) * 180f / MathF.PI;
        return MathF.Abs(deg) switch
        {
            < 30f => TurnKind.Straight,
            > 150f => TurnKind.UTurn,
            _ => deg > 0 ? TurnKind.Right : TurnKind.Left,
        };
    }
}
