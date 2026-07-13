using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Traffic;

/// <summary>
/// Strategic layer: A* over (edge, direction) states. Transitions are junction
/// movements (any lane connector linking the two edges in travel direction); costs are
/// travel time plus turn and control-delay penalties, so traffic prefers priority
/// roads over stop-controlled shortcuts. Replanning is just Plan() from the current
/// edge — vehicles never leave the lane graph.
/// </summary>
public static class RoutePlanner
{
    private const float LeftPenalty = 4f, RightPenalty = 1.5f, UTurnPenalty = 8f;
    private const float YieldDelay = 2f, StopDelay = 4f, SignalDelay = 5f;
    private const float MaxSpeed = 27.8f; // heuristic speed (fastest road)

    public static Route? Plan(RoadNetwork n, EdgeId fromEdge, bool forward, EdgeId toEdge)
    {
        if (!n.Edges.ContainsKey(fromEdge) || !n.Edges.ContainsKey(toEdge))
            return null;
        if (fromEdge == toEdge)
            return new Route(new[] { new RouteStep(fromEdge, forward) });

        var lanes = new Dictionary<LaneId, Lane>();
        foreach (var e in n.Edges.Values)
        foreach (var l in e.Lanes)
            lanes[l.Id] = l;

        var goalMid = n.Edges[toEdge].Curve.Point(0.5f);
        float Heuristic(RouteStep s)
        {
            var node = ExitNode(n, s);
            return Vector3.Distance(node.Position, goalMid) / MaxSpeed;
        }

        var open = new PriorityQueue<RouteStep, (float f, int edge, int fwd)>();
        var g = new Dictionary<RouteStep, float>();
        var parent = new Dictionary<RouteStep, RouteStep>();
        var start = new RouteStep(fromEdge, forward);
        g[start] = EdgeTime(n, fromEdge);
        open.Enqueue(start, (g[start] + Heuristic(start), fromEdge.Value, forward ? 1 : 0));
        var closed = new HashSet<RouteStep>();

        while (open.TryDequeue(out var current, out _))
        {
            if (!closed.Add(current))
                continue;
            if (current.Edge == toEdge)
                return Reconstruct(parent, current);

            var node = ExitNode(n, current);
            foreach (var (next, moveCost) in Movements(n, node, current, lanes))
            {
                float tentative = g[current] + moveCost + EdgeTime(n, next.Edge);
                if (g.TryGetValue(next, out var known) && known <= tentative)
                    continue;
                g[next] = tentative;
                parent[next] = current;
                open.Enqueue(next, (tentative + Heuristic(next), next.Edge.Value, next.Forward ? 1 : 0));
            }
        }
        return null;
    }

    private static float EdgeTime(RoadNetwork n, EdgeId id)
    {
        var e = n.Edges[id];
        return e.ArcLength.TotalLength / RoadCatalog.Get(e.Type).SpeedLimit;
    }

    private static RoadNode ExitNode(RoadNetwork n, RouteStep s)
    {
        var e = n.Edges[s.Edge];
        return n.Nodes[s.Forward ? e.EndNode : e.StartNode];
    }

    /// <summary>Movements available at the node when arriving via `from`: distinct
    /// (edge, direction) targets with the cheapest connector's turn + control cost.</summary>
    private static IEnumerable<(RouteStep next, float cost)> Movements(
        RoadNetwork n, RoadNode node, RouteStep from, Dictionary<LaneId, Lane> lanes)
    {
        var best = new Dictionary<RouteStep, float>();
        foreach (var c in node.Connectors)
        {
            var fromLane = lanes[c.From];
            if (fromLane.Edge != from.Edge)
                continue;
            bool laneTravelsForward = fromLane.Direction == LaneDirection.Forward;
            if (laneTravelsForward != from.Forward)
                continue;
            if (fromLane.Kind != LaneKind.Driving)
                continue;

            var toLane = lanes[c.To];
            var next = new RouteStep(toLane.Edge, toLane.Direction == LaneDirection.Forward);
            float cost = TurnCost(c.Turn) + RowCost(c.Row);
            if (!best.TryGetValue(next, out var known) || cost < known)
                best[next] = cost;
        }
        foreach (var (next, cost) in best)
            yield return (next, cost);
    }

    private static float TurnCost(TurnKind turn) => turn switch
    {
        TurnKind.Left => LeftPenalty,
        TurnKind.Right => RightPenalty,
        TurnKind.UTurn => UTurnPenalty,
        _ => 0f,
    };

    private static float RowCost(RightOfWay row) => row switch
    {
        RightOfWay.Yield => YieldDelay,
        RightOfWay.Stop => StopDelay,
        RightOfWay.Signal => SignalDelay,
        _ => 0f,
    };

    private static Route Reconstruct(Dictionary<RouteStep, RouteStep> parent, RouteStep last)
    {
        var steps = new List<RouteStep> { last };
        while (parent.TryGetValue(last, out var prev))
        {
            steps.Add(prev);
            last = prev;
        }
        steps.Reverse();
        return new Route(steps);
    }
}
