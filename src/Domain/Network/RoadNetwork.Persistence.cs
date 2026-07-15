using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Persistence;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

public sealed partial class RoadNetwork
{
    // Exposed for SaveLoad (same assembly): the counters are private state, but the
    // save format stores them verbatim so replayed ids never collide with restored ones.
    internal int NextNodeCounter => _nextNode;
    internal int NextEdgeCounter => _nextEdge;
    internal int NextLaneCounter => _nextLane;

    /// <summary>Replace the entire graph with the contents of <paramref name="game"/>,
    /// inside one mutation batch (one <see cref="Changed"/> event). Nodes/edges/lanes are
    /// restored with their EXACT saved ids; derived data (junction geometry, connectors)
    /// is rebuilt fresh by <see cref="EndBatch"/>, never taken from the save file.</summary>
    internal void RestoreInto(SaveGame game)
    {
        ValidateGame(game);

        BeginBatch();

        // Remove everything currently in the network via the same internal removal
        // path normal edits use, so the batch records the departures. Nodes are
        // cleared directly (not via HandleNodeAfterRemoval/TryHealNode — restoring
        // a snapshot must not trigger healing).
        foreach (var edge in _edges.Values.ToList())
            RemoveEdgeInternal(edge);
        foreach (var node in _nodes.Values.ToList())
        {
            _nodes.Remove(node.Id);
            _batch!.NodesRemoved.Add(node.Id);
            _batch!.Touched.Add(node.Id);
        }

        // Re-add nodes with their saved ids/positions/configs. Configs are pruned
        // against the (not yet populated) edge set by EndBatch's RebuildDerived once
        // edges below have repopulated each node's EdgeSet.
        foreach (var nd in game.Nodes)
        {
            var node = new RoadNode(new NodeId(nd.Id), new Vector3(nd.X, nd.Y, nd.Z))
            {
                Config = ToConfig(nd.Config),
            };
            _nodes[node.Id] = node;
            _batch!.NodesAdded.Add(node.Id);
            _batch!.Touched.Add(node.Id);
        }

        // Re-add edges with their saved ids/curves/types, lanes reassigned verbatim
        // from LaneIds in catalog order (the order AddEdgeInternal enumerates type.Lanes).
        foreach (var ed in game.Edges)
        {
            var curve = ToCurve(ed.Curve);
            var edge = new RoadEdge(new EdgeId(ed.Id), new NodeId(ed.Start), new NodeId(ed.End), curve, new RoadTypeId(ed.Type));
            var specs = RoadCatalog.Get(edge.Type).Lanes;
            var lanes = new Lane[specs.Count];
            for (int i = 0; i < specs.Count; i++)
            {
                var spec = specs[i];
                lanes[i] = new Lane(new LaneId(ed.LaneIds[i]), edge.Id, spec.Offset, spec.Direction, spec.Width, spec.Kind);
            }
            edge.Lanes = lanes;

            _edges[edge.Id] = edge;
            _nodes[edge.StartNode].EdgeSet.Add(edge.Id);
            _nodes[edge.EndNode].EdgeSet.Add(edge.Id);
            _batch!.EdgesAdded.Add(edge.Id);
            _batch!.Touched.Add(edge.StartNode);
            _batch!.Touched.Add(edge.EndNode);
        }

        _nextNode = game.NextNode;
        _nextEdge = game.NextEdge;
        _nextLane = game.NextLane;

        EndBatch();
    }

    private static void ValidateGame(SaveGame game)
    {
        // System.Text.Json leaves absent/null JSON fields as null even on non-nullable
        // record properties — every reference-typed field is guarded here so nothing
        // null-related (and no NRE) can ever reach the mutation phase below.
        if (game.Nodes is null)
            throw new SaveFormatException("save data has no Nodes array");
        if (game.Edges is null)
            throw new SaveFormatException("save data has no Edges array");

        var nodeIds = new HashSet<int>();
        foreach (var nd in game.Nodes)
        {
            if (nd is null)
                throw new SaveFormatException("Nodes array contains a null entry");
            if (nd.Id <= 0 || nd.Id >= game.NextNode)
                throw new SaveFormatException($"node id {nd.Id} is not below NextNode counter {game.NextNode}");
            if (!nodeIds.Add(nd.Id))
                throw new SaveFormatException($"duplicate node id {nd.Id}");
            if (nd.Config is null)
                throw new SaveFormatException($"node {nd.Id} has no Config");
            if (nd.Config.Roles is null)
                throw new SaveFormatException($"node {nd.Id} config has no Roles array");
            if (nd.Config.Roles.Any(r => r is null))
                throw new SaveFormatException($"node {nd.Id} config Roles contains a null entry");
            if (nd.Config.Roles.Select(r => r.Edge).Distinct().Count() != nd.Config.Roles.Length)
                throw new SaveFormatException($"node {nd.Id} config Roles has a duplicate edge key");
            if (nd.Config.LegOffsets is null)
                throw new SaveFormatException($"node {nd.Id} config has no LegOffsets array");
            if (nd.Config.LegOffsets.Any(l => l is null))
                throw new SaveFormatException($"node {nd.Id} config LegOffsets contains a null entry");
            if (nd.Config.LegOffsets.Select(l => l.Edge).Distinct().Count() != nd.Config.LegOffsets.Length)
                throw new SaveFormatException($"node {nd.Id} config LegOffsets has a duplicate edge key");
        }

        var edgeIds = new HashSet<int>();
        var laneIds = new HashSet<int>();
        foreach (var ed in game.Edges)
        {
            if (ed is null)
                throw new SaveFormatException("Edges array contains a null entry");
            if (ed.Id <= 0 || ed.Id >= game.NextEdge)
                throw new SaveFormatException($"edge id {ed.Id} is not below NextEdge counter {game.NextEdge}");
            if (!edgeIds.Add(ed.Id))
                throw new SaveFormatException($"duplicate edge id {ed.Id}");
            if (ed.Curve is null)
                throw new SaveFormatException($"edge {ed.Id} has no Curve array");
            if (ed.LaneIds is null)
                throw new SaveFormatException($"edge {ed.Id} has no LaneIds array");
            if (ed.Curve.Length != 12)
                throw new SaveFormatException($"edge {ed.Id} curve must have 12 floats, got {ed.Curve.Length}");
            if (!nodeIds.Contains(ed.Start))
                throw new SaveFormatException($"edge {ed.Id} references unknown start node {ed.Start}");
            if (!nodeIds.Contains(ed.End))
                throw new SaveFormatException($"edge {ed.Id} references unknown end node {ed.End}");

            RoadType type;
            try { type = RoadCatalog.Get(new RoadTypeId(ed.Type)); }
            catch (KeyNotFoundException) { throw new SaveFormatException($"edge {ed.Id} has unknown road type {ed.Type}"); }

            if (ed.LaneIds.Length != type.Lanes.Count)
                throw new SaveFormatException(
                    $"edge {ed.Id} has {ed.LaneIds.Length} lane ids but type {ed.Type} needs {type.Lanes.Count}");
            foreach (var laneId in ed.LaneIds)
            {
                if (laneId <= 0 || laneId >= game.NextLane)
                    throw new SaveFormatException($"lane id {laneId} is not below NextLane counter {game.NextLane}");
                if (!laneIds.Add(laneId))
                    throw new SaveFormatException($"duplicate lane id {laneId}");
            }
        }
    }

    private static Bezier3 ToCurve(float[] c) => new(
        new Vector3(c[0], c[1], c[2]),
        new Vector3(c[3], c[4], c[5]),
        new Vector3(c[6], c[7], c[8]),
        new Vector3(c[9], c[10], c[11]));

    private static JunctionConfig ToConfig(ConfigDto dto) => new(
        (JunctionControlMode)dto.Mode,
        dto.Roles.ToDictionary(r => new EdgeId(r.Edge), r => (LegRole)r.Role),
        dto.SizeOffset,
        dto.LegOffsets.ToDictionary(l => new EdgeId(l.Edge), l => l.Offset));
}
