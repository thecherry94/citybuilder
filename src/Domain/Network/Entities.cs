using System.Numerics;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

public sealed class RoadNode
{
    public NodeId Id { get; }
    public Vector3 Position { get; }
    internal readonly HashSet<EdgeId> EdgeSet = new();

    public IReadOnlySet<EdgeId> Edges => EdgeSet;
    public JunctionGeometry Junction { get; internal set; } = JunctionGeometry.Empty;
    public IReadOnlyList<LaneConnector> Connectors { get; internal set; } = Array.Empty<LaneConnector>();

    internal RoadNode(NodeId id, Vector3 position)
    {
        Id = id;
        Position = position;
    }
}

public sealed class RoadEdge
{
    public EdgeId Id { get; }
    public NodeId StartNode { get; }
    public NodeId EndNode { get; }
    public Bezier3 Curve { get; }
    public RoadTypeId Type { get; }
    public IReadOnlyList<Lane> Lanes { get; internal set; } = Array.Empty<Lane>();
    public ArcLengthTable ArcLength { get; }

    internal RoadEdge(EdgeId id, NodeId start, NodeId end, in Bezier3 curve, RoadTypeId type)
    {
        Id = id;
        StartNode = start;
        EndNode = end;
        Curve = curve;
        Type = type;
        ArcLength = new ArcLengthTable(curve);
    }

    public NodeId OtherNode(NodeId n) => n == StartNode ? EndNode : StartNode;
}

public sealed class Lane
{
    public LaneId Id { get; }
    public EdgeId Edge { get; }
    public float Offset { get; }
    public LaneDirection Direction { get; }
    public float Width { get; }
    public LaneKind Kind { get; }

    internal Lane(LaneId id, EdgeId edge, float offset, LaneDirection direction, float width, LaneKind kind)
    {
        Id = id;
        Edge = edge;
        Offset = offset;
        Direction = direction;
        Width = width;
        Kind = kind;
    }
}

/// <summary>Where road meshes stop and the intersection surface begins, per node.
/// CutT maps each connected edge to the curve parameter where the junction starts
/// (measured in the edge's own parameterization; for edges *ending* at the node the
/// drawable part is [0, CutT], for edges *starting* here it is [CutT, 1]).
/// CutSegments lists the polygon segment indices (i → i+1) that are road
/// cross-sections; all other segments are outer boundary (renderers skirt them).</summary>
public sealed record JunctionGeometry(
    IReadOnlyDictionary<EdgeId, float> CutT,
    IReadOnlyList<Vector3> SurfacePolygon,
    IReadOnlySet<int> CutSegments)
{
    public static readonly JunctionGeometry Empty =
        new(new Dictionary<EdgeId, float>(), Array.Empty<Vector3>(), new HashSet<int>());
}

/// <summary>How a lane connector turns, relative to the incoming travel direction.
/// Right-hand traffic, Y up: positive rotation toward +Z is a right turn.</summary>
public enum TurnKind { Straight, Left, Right, UTurn }

/// <summary>Lane-level link across a node: which incoming lane can flow into which
/// outgoing lane, the curve vehicles would follow, and the movement it represents.</summary>
public sealed record LaneConnector(LaneId From, LaneId To, Bezier3 Curve, TurnKind Turn);

public sealed record NetworkDelta(
    IReadOnlySet<EdgeId> EdgesAdded,
    IReadOnlySet<EdgeId> EdgesRemoved,
    IReadOnlySet<NodeId> NodesAdded,
    IReadOnlySet<NodeId> NodesRemoved,
    IReadOnlySet<NodeId> NodesChanged);
