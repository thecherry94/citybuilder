using System.Numerics;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

/// <summary>Why a roundabout conversion or edit was refused. No mutation happens on failure.</summary>
public enum RoundaboutError
{
    RadiusTooTight, LegTooShort, LegInsideRing, DegenerateBearings,
    NotAJunction, AlreadyRoundabout, ForeignLeg, UnknownRoundabout,
    ApproachTooTangential, Obstructed, LegTooSteep,
}

/// <summary>A live roundabout: a CCW one-way ring owning its ring nodes and edges.
/// <see cref="RingEdges"/>[i] runs <see cref="RingNodes"/>[i] → RingNodes[(i+1) % n],
/// counter-clockwise (right-hand traffic). <see cref="LegFullCurves"/> captures each
/// approach leg's curve as it entered the center at conversion time, keyed by the leg's
/// (stable) EdgeId, so radius changes re-trim losslessly and idempotently.</summary>
public sealed record Roundabout(
    RoundaboutId Id, Vector3 Center, float Radius,
    IReadOnlyList<NodeId> RingNodes, IReadOnlyList<EdgeId> RingEdges,
    IReadOnlyDictionary<EdgeId, Bezier3> LegFullCurves);

public sealed record RoundaboutResult(bool Success, RoundaboutId? Id, RoundaboutError? Error)
{
    public static RoundaboutResult Ok(RoundaboutId id) => new(true, id, null);
    public static RoundaboutResult Failed(RoundaboutError error) => new(false, null, error);
}
