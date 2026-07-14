using System.Numerics;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tools;

public enum SnapKind { Free, Node, Edge, GuidelineIntersection, Guideline, Angle, GridPoint, GridLine, Perpendicular }

[Flags]
public enum SnapTypes
{
    None = 0,
    Nodes = 1,
    Edges = 2,
    Angle = 4,
    Guidelines = 8,
    Grid = 16,
    Parallel = 32,
    Perpendicular = 64,
    All = Nodes | Edges | Angle | Guidelines | Grid | Parallel | Perpendicular,
}

/// <summary>A construction guide: the tangent of an existing road extended past its
/// node, used both for snapping and for rendering dashed guide lines.</summary>
public sealed record Guideline(Vector3 Origin, Vector3 Direction, float Length)
{
    public Vector3 PointAt(float s) => Origin + Direction * s;
}

/// <summary>World-aligned square snapping grid.</summary>
public sealed record GridConfig(float CellSize)
{
    public static readonly GridConfig Default = new(8f);
}

/// <summary>Drawing context that influences snapping: the previous click (anchor) and the
/// direction angle snap measures from (e.g. the tangent of the road being extended).</summary>
public sealed record SnapContext(
    Vector3? Anchor,
    Vector3? ReferenceTangent,
    GridConfig? Grid = null,
    RoadTypeId? DrawingType = null)
{
    public static readonly SnapContext Empty = new(null, null);
}

public sealed record SnapResult(
    Vector3 Position,
    SnapKind Kind,
    NodeId? Node,
    (EdgeId Edge, float T)? Edge,
    float? SnappedAngleDeg,
    IReadOnlyList<Guideline> ActiveGuidelines,
    Vector3? DirectionConstraint = null)
{
    public static SnapResult Free(Vector3 p)
        => new(p, SnapKind.Free, null, null, null, Array.Empty<Guideline>());
}
