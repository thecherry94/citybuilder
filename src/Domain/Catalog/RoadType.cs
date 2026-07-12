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
    float DesignSpeedKmh)
{
    /// <summary>Half-width of the paved carriageway: up to the sidewalks' inner
    /// edges, or the full half-width when there are none.</summary>
    public float CarriagewayHalf
    {
        get
        {
            var walks = Lanes.Where(l => l.Kind == LaneKind.Sidewalk).ToArray();
            return walks.Length == 0
                ? Width / 2
                : walks.Min(l => MathF.Abs(l.Offset) - l.Width / 2);
        }
    }

    public bool HasSidewalks => Lanes.Any(l => l.Kind == LaneKind.Sidewalk);
}

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

    /// <summary>Urban two-lane street: driving lanes flanked by raised sidewalks.</summary>
    public static readonly RoadType Street = new(
        new RoadTypeId(3), "Street", 13f,
        new LaneSpec[]
        {
            new(+1.75f, LaneDirection.Forward, LaneWidth, LaneKind.Driving),
            new(-1.75f, LaneDirection.Backward, LaneWidth, LaneKind.Driving),
            new(+4.75f, LaneDirection.Forward, 2.5f, LaneKind.Sidewalk),
            new(-4.75f, LaneDirection.Backward, 2.5f, LaneKind.Sidewalk),
        },
        50f);

    /// <summary>Urban four-lane avenue with bicycle lanes and sidewalks.</summary>
    public static readonly RoadType Avenue = new(
        new RoadTypeId(4), "Avenue", 21f,
        new LaneSpec[]
        {
            new(+1.75f, LaneDirection.Forward, LaneWidth, LaneKind.Driving),
            new(+5.25f, LaneDirection.Forward, LaneWidth, LaneKind.Driving),
            new(-1.75f, LaneDirection.Backward, LaneWidth, LaneKind.Driving),
            new(-5.25f, LaneDirection.Backward, LaneWidth, LaneKind.Driving),
            new(+7.75f, LaneDirection.Forward, 1.5f, LaneKind.Bicycle),
            new(-7.75f, LaneDirection.Backward, 1.5f, LaneKind.Bicycle),
            new(+9.5f, LaneDirection.Forward, 2f, LaneKind.Sidewalk),
            new(-9.5f, LaneDirection.Backward, 2f, LaneKind.Sidewalk),
        },
        50f);

    public static readonly IReadOnlyList<RoadType> All = new[] { TwoLane, FourLane, Street, Avenue };

    public static RoadType Get(RoadTypeId id)
        => All.FirstOrDefault(t => t.Id == id)
           ?? throw new KeyNotFoundException($"Unknown road type {id.Value}");
}
