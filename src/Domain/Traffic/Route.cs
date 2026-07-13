using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Traffic;

/// <summary>One leg of a planned trip: traverse an edge in the given direction
/// (Forward = curve P0→P3). Consecutive steps are linked by a junction movement.</summary>
public readonly record struct RouteStep(EdgeId Edge, bool Forward);

/// <summary>An edge-level route. Lanes are chosen tactically while driving.</summary>
public sealed record Route(IReadOnlyList<RouteStep> Steps);
