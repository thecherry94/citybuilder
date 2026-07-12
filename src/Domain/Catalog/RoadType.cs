using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Catalog;

/// <summary>Lane blueprint within a road type. Offset is signed meters from the edge
/// centerline in the edge's start→end frame (positive = right looking along the curve).
/// Right-hand traffic: forward lanes at positive offsets.</summary>
public sealed record LaneSpec(float Offset, LaneDirection Direction, float Width, LaneKind Kind);

public sealed record RoadType(
    RoadTypeId Id,
    string Name,
    float Width,
    IReadOnlyList<LaneSpec> Lanes,
    float DesignSpeedKmh);

public static class RoadCatalog
{
    public const float LaneWidth = 3.5f;

    public static readonly RoadType TwoLane = new(
        new RoadTypeId(1), "Two-Lane Road", 8f,
        new LaneSpec[]
        {
            new(+1.75f, LaneDirection.Forward, LaneWidth, LaneKind.Driving),
            new(-1.75f, LaneDirection.Backward, LaneWidth, LaneKind.Driving),
        },
        50f);

    public static readonly RoadType FourLane = new(
        new RoadTypeId(2), "Four-Lane Road", 16f,
        new LaneSpec[]
        {
            new(+1.75f, LaneDirection.Forward, LaneWidth, LaneKind.Driving),
            new(+5.25f, LaneDirection.Forward, LaneWidth, LaneKind.Driving),
            new(-1.75f, LaneDirection.Backward, LaneWidth, LaneKind.Driving),
            new(-5.25f, LaneDirection.Backward, LaneWidth, LaneKind.Driving),
        },
        60f);

    public static readonly IReadOnlyList<RoadType> All = new[] { TwoLane, FourLane };

    public static RoadType Get(RoadTypeId id)
        => All.FirstOrDefault(t => t.Id == id)
           ?? throw new KeyNotFoundException($"Unknown road type {id.Value}");
}
