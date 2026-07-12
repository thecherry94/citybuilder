namespace CityBuilder.Domain.Network;

/// <summary>Lane-level reachability over the network: lanes are vertices, connectors
/// are (directed) edges from a lane to the lanes it can flow into. This is the graph
/// traffic will eventually pathfind over.</summary>
public static class LaneGraph
{
    /// <summary>For each lane, the lanes reachable through the connector at its
    /// downstream node.</summary>
    public static Dictionary<LaneId, List<LaneId>> BuildAdjacency(RoadNetwork network)
    {
        var adjacency = new Dictionary<LaneId, List<LaneId>>();
        foreach (var edge in network.Edges.Values)
        foreach (var lane in edge.Lanes)
            adjacency[lane.Id] = new List<LaneId>();

        foreach (var node in network.Nodes.Values)
        foreach (var connector in node.Connectors)
            if (adjacency.TryGetValue(connector.From, out var list))
                list.Add(connector.To);

        return adjacency;
    }

    /// <summary>True if every lane can reach every other lane.</summary>
    public static bool IsStronglyConnected(RoadNetwork network)
    {
        var adjacency = BuildAdjacency(network);
        if (adjacency.Count == 0)
            return true;

        // BFS from one lane, then BFS on the reversed graph from the same lane
        var start = adjacency.Keys.First();
        if (Reach(adjacency, start).Count != adjacency.Count)
            return false;

        var reversed = adjacency.Keys.ToDictionary(k => k, _ => new List<LaneId>());
        foreach (var (from, tos) in adjacency)
        foreach (var to in tos)
            reversed[to].Add(from);
        return Reach(reversed, start).Count == adjacency.Count;
    }

    private static HashSet<LaneId> Reach(Dictionary<LaneId, List<LaneId>> adj, LaneId start)
    {
        var seen = new HashSet<LaneId> { start };
        var queue = new Queue<LaneId>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        foreach (var next in adj[queue.Dequeue()])
            if (seen.Add(next))
                queue.Enqueue(next);
        return seen;
    }
}
