using System.Numerics;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

/// <summary>A conflict between two connectors of one node: the other connector's
/// index and the arc distance to the crossing point along each curve (curve ends
/// for same-target merges).</summary>
public readonly record struct ConflictPoint(int Other, float SMine, float STheirs);

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
        bool junction = node.Edges.Count >= 3;

        // left→right rank of each incoming driving lane within its edge approach,
        // for turn-lane assignment (lefts from the leftmost, rights from the rightmost).
        // Incoming lanes from one edge share a travel direction, so signed offset
        // orders them correctly (|offset| ordering gets it backwards whenever the
        // lane set spans 0 — see TrafficSim._adjacent for the same latent bug).
        var laneRank = new Dictionary<LaneId, (int index, int count)>();
        foreach (var group in incoming
                     .Where(x => x.lane.Kind == LaneKind.Driving)
                     .GroupBy(x => x.lane.Edge))
        {
            var ordered = (group.First().lane.Direction == LaneDirection.Forward
                ? group.OrderBy(x => x.lane.Offset)
                : group.OrderByDescending(x => x.lane.Offset)).ToArray();
            for (int i = 0; i < ordered.Length; i++)
                laneRank[ordered[i].lane.Id] = (i, ordered.Length);
        }

        // receiving order within each arm (same direction-aware ordering)
        var outRank = new Dictionary<LaneId, (int index, int count)>();
        foreach (var group in outgoing
                     .Where(x => x.lane.Kind == LaneKind.Driving)
                     .GroupBy(x => x.lane.Edge))
        {
            var ordered = (group.First().lane.Direction == LaneDirection.Forward
                ? group.OrderBy(x => x.lane.Offset)
                : group.OrderByDescending(x => x.lane.Offset)).ToArray();
            for (int i = 0; i < ordered.Length; i++)
                outRank[ordered[i].lane.Id] = (i, ordered.Length);
        }

        // movement class per approach→arm pair: all lanes of a group share a travel
        // direction, so representative directions classify the whole pair
        var repIn = incoming.Where(x => x.lane.Kind == LaneKind.Driving)
            .GroupBy(x => x.lane.Edge).ToDictionary(g => g.Key, g => g.First().dir);
        var repOut = outgoing.Where(x => x.lane.Kind == LaneKind.Driving)
            .GroupBy(x => x.lane.Edge).ToDictionary(g => g.Key, g => (Dir: g.First().dir, Count: g.Count()));
        var armTurn = new Dictionary<(EdgeId From, EdgeId To), TurnKind>();
        foreach (var (a, inDirA) in repIn)
        foreach (var (b, rep) in repOut)
            if (a != b)
                armTurn[(a, b)] = Classify(inDirA, rep.Dir);

        // capacity-aware straight blocks: an approach never sends more straight
        // lanes into an arm than the arm can receive. Surplus drops from the left
        // first when a left arm exists (inner lanes become dedicated lefts), then
        // from the right when a right arm exists; lanes with neither alternative
        // keep a merge-straight rather than going dead.
        //
        // One approach can have more than one simultaneous "Straight" target: a
        // genuine fork/wye (two separate roads splitting off, each its own driver
        // choice — see ControlDelaySwaysRouteChoice, where a single lane must reach
        // BOTH branches) or a tangent-continuation ramp landing within Classify()'s
        // +-30 deg window right alongside the "real" through-arm (see
        // RoadNetwork.TangentContinuationDeg: a new edge legitimately departs an
        // OnEdge split within ~1 deg of the split edge's own tangent). Sizing every
        // pair independently against the full incoming count is right for the fork
        // (each branch's own capacity already covers the shared lane) but wrong for
        // the ramp: a target with less capacity than the approach would then still
        // claim every source lane, double-booking lanes the "real" arm already
        // covers in full. So each target is capped to its OWN receiving capacity
        // whenever more than one simultaneous Straight target exists — a lane can
        // still be eligible for several targets at once (that's the fork), but no
        // single target is ever handed more lanes than it can receive. With only one
        // target, the existing uncapped merge-straight fallback is untouched (a
        // narrowing road must not leave a lane with zero connectors).
        var straightBlock = new Dictionary<(EdgeId From, EdgeId To), (int Start, int End)>();
        foreach (var srcGroup in armTurn.Where(kv => kv.Value == TurnKind.Straight).GroupBy(kv => kv.Key.From))
        {
            var a = srcGroup.Key;
            var targets = srcGroup.Select(kv => kv.Key.To).ToArray();
            int n = incoming.Count(x => x.lane.Edge == a && x.lane.Kind == LaneKind.Driving);
            bool hasLeft = armTurn.Any(kv => kv.Key.From == a
                && kv.Value == TurnKind.Left && repOut[kv.Key.To].Count > 0);
            bool hasRight = armTurn.Any(kv => kv.Key.From == a
                && kv.Value == TurnKind.Right && repOut[kv.Key.To].Count > 0);

            foreach (var b in targets)
            {
                int r = repOut[b].Count;
                int surplus = Math.Max(0, n - r);
                int dropLeft = hasLeft && surplus > 0 ? 1 : 0;
                surplus -= dropLeft;
                int dropRight = hasRight && surplus > 0 ? 1 : 0;
                int start = dropLeft;
                int end = n - dropRight;
                if (targets.Length > 1)
                    end = Math.Min(end, start + r);
                straightBlock[(a, b)] = (start, end);
            }
        }

        // straight emission: the lane must sit inside its approach's straight block
        // for this arm, and block↔receiving lanes pair off aligned — 1:1 when counts
        // match (no crossing connectors), the edge lane fans out on widening, and
        // surplus fallback lanes merge into the last receiving lane
        bool StraightAllowed(Lane inLane, Lane outLane, (int index, int count) rank)
        {
            if (!straightBlock.TryGetValue((inLane.Edge, outLane.Edge), out var block)
                || !outRank.TryGetValue(outLane.Id, out var o))
                return true; // no driving metadata: keep permissive legacy behavior
            if (rank.index < block.Start || rank.index >= block.End)
                return false;
            int k = rank.index - block.Start;
            int blockCount = block.End - block.Start;
            return o.index == Math.Min(k, o.count - 1)
                || k == Math.Min(o.index, blockCount - 1);
        }

        var connectors = new List<LaneConnector>(incoming.Count * Math.Max(0, outgoing.Count - 1));
        foreach (var (inLane, inPos, inDir) in incoming)
        foreach (var (outLane, outPos, outDir) in outgoing)
        {
            if (inLane.Kind != outLane.Kind)
                continue; // bikes connect to bikes, sidewalks to sidewalks
            if (inLane.Edge == outLane.Edge && !deadEnd)
                continue; // no U-turns except at dead ends
            var turn = Classify(inDir, outDir);
            // turn-lane assignment at real junctions (driving lanes only):
            // lefts/u-turns from the leftmost lane, rights from the rightmost,
            // straights capacity-limited and aligned — traffic pre-sorts via
            // lane changes
            if (junction && inLane.Kind == LaneKind.Driving
                && laneRank.TryGetValue(inLane.Id, out var rank))
            {
                bool allowed = turn switch
                {
                    TurnKind.Left or TurnKind.UTurn => rank.index == 0,
                    TurnKind.Right => rank.index == rank.count - 1,
                    TurnKind.Straight => StraightAllowed(inLane, outLane, rank),
                    _ => true,
                };
                if (!allowed)
                    continue;
            }
            float reach = MathF.Max(Vector3.Distance(inPos, outPos) / 3f, 0.1f);
            var curve = new Bezier3(inPos, inPos + inDir * reach, outPos - outDir * reach, outPos);
            connectors.Add(new LaneConnector(
                inLane.Id, outLane.Id, curve, turn, RowFor(control, inLane.Edge)));
        }

        // never-strand guarantee (hard invariant, spec amendment 2026-07-16): the
        // turn-lane rules above are heuristics for clean lane assignment, not
        // reachability contracts — with direction-asymmetric road types in the mix,
        // an arriving driving lane's ONLY geometric destination can be an arm its
        // rank isn't entitled to (e.g. a second-from-left lane whose sole target
        // classifies as Left). Whenever a driving lane ends up with zero connectors
        // while the node still has departing driving lanes on OTHER edges, relax the
        // rank rules for that lane and connect it to its geometrically nearest such
        // departure — same philosophy as the straight-block merge fallback ("lanes
        // with neither alternative keep a merge-straight rather than going dead").
        // Lanes with categorically zero destinations (no departing driving lane on
        // any other edge) stay unconnected: that is the legal stranded state
        // NetworkInvariants.CheckLaneCoverage now permits.
        var connectedFrom = connectors.Select(c => c.From).ToHashSet();
        foreach (var (inLane, inPos, inDir) in incoming)
        {
            if (inLane.Kind != LaneKind.Driving || connectedFrom.Contains(inLane.Id))
                continue;
            (Lane lane, Vector3 pos, Vector3 dir)? best = null;
            float bestD = float.MaxValue;
            foreach (var cand in outgoing)
            {
                if (cand.lane.Kind != LaneKind.Driving || cand.lane.Edge == inLane.Edge)
                    continue;
                float d = Vector3.Distance(inPos, cand.pos);
                if (d < bestD || (d == bestD && best is { } b && cand.lane.Id.Value < b.lane.Id.Value))
                {
                    bestD = d;
                    best = cand;
                }
            }
            if (best is not { } target)
                continue; // legally stranded — no receiving capacity on another edge
            float reach = MathF.Max(Vector3.Distance(inPos, target.pos) / 3f, 0.1f);
            var curve = new Bezier3(inPos, inPos + inDir * reach, target.pos - target.dir * reach, target.pos);
            connectors.Add(new LaneConnector(inLane.Id, target.lane.Id, curve,
                Classify(inDir, target.dir), RowFor(control, inLane.Edge)));
        }
        return connectors;
    }

    /// <summary>Pairwise conflicts between a node's connectors — where, not just
    /// whether: crossing points carry arc distances along both curves so arbitration
    /// can tell "approaching my path" from "already past it". Same-target merges use
    /// both curve ends. Connectors sharing the source lane are queue-ordered, not
    /// conflicting.</summary>
    public static IReadOnlyList<ConflictPoint[]> BuildConflicts(IReadOnlyList<LaneConnector> connectors)
    {
        var tables = new ArcLengthTable[connectors.Count];
        for (int i = 0; i < connectors.Count; i++)
            tables[i] = new ArcLengthTable(connectors[i].Curve, 24);

        var sets = new List<ConflictPoint>[connectors.Count];
        for (int i = 0; i < connectors.Count; i++)
            sets[i] = new List<ConflictPoint>();

        for (int i = 0; i < connectors.Count; i++)
        for (int j = i + 1; j < connectors.Count; j++)
        {
            var a = connectors[i];
            var b = connectors[j];
            if (a.From == b.From)
                continue;
            if (a.To == b.To)
            {
                sets[i].Add(new ConflictPoint(j, tables[i].TotalLength, tables[j].TotalLength));
                sets[j].Add(new ConflictPoint(i, tables[j].TotalLength, tables[i].TotalLength));
                continue;
            }
            var hits = BezierOps.Intersections(a.Curve, b.Curve);
            if (hits.Count == 0)
                continue;
            var (t1, t2) = hits.OrderBy(h => h.t1).First(); // first crossing along my travel
            sets[i].Add(new ConflictPoint(j, tables[i].DistanceAtT(t1), tables[j].DistanceAtT(t2)));
            sets[j].Add(new ConflictPoint(i, tables[j].DistanceAtT(t2), tables[i].DistanceAtT(t1)));
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
