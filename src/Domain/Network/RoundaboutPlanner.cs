using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

/// <summary>One former junction leg, as seen from the center. <see cref="EndsAtCenter"/>
/// is true when the edge's EndNode is the center (inner end = Curve.Point(1)); false when
/// its StartNode is the center (inner end = Curve.Point(0)).</summary>
public readonly record struct ApproachLeg(EdgeId Edge, Bezier3 Curve, bool EndsAtCenter, RoadTypeId Type);

/// <summary>A ring slot: where one approach meets the circle. <see cref="TrimmedLeg"/> is
/// the approach curve cut back to end on the circle; <see cref="TrimmedLegEndsAtCenter"/>
/// mirrors the source leg's orientation (which end is the inner/ring end).</summary>
public sealed record RingSlot(
    float Bearing, Vector3 Position, ApproachLeg Leg, Bezier3 TrimmedLeg, bool TrimmedLegEndsAtCenter);

/// <summary>Pure output of planning a roundabout. <see cref="RingArcs"/>[i] is the CCW arc
/// chain (1–2+ cubics; &gt;90° gaps split, sharing endpoints) from Slots[i] to
/// Slots[(i+1) % n]. A non-null <see cref="Error"/> means the plan is invalid and the
/// lists may be empty; no mutation should follow.</summary>
public sealed record RoundaboutPlan(
    Vector3 Center, float Radius,
    IReadOnlyList<RingSlot> Slots,
    IReadOnlyList<IReadOnlyList<Bezier3>> RingArcs,
    RoundaboutError? Error);

/// <summary>Pure geometry for roundabout conversion: given a center, a radius, and the
/// approach legs, produce ring slots, CCW ring arcs, and per-leg trims — or the first
/// blocking error. Right-hand traffic circulates counter-clockwise in XZ.</summary>
public static class RoundaboutPlanner
{
    private const float DegenerateGapRad = 1f * MathF.PI / 180f; // 1°
    private const float QuarterTurn = MathF.PI / 2f;

    public static RoundaboutPlan Plan(Vector3 center, float radius, IReadOnlyList<ApproachLeg> legs)
    {
        RoundaboutPlan Fail(RoundaboutError e) => new(center, radius, Array.Empty<RingSlot>(),
            Array.Empty<IReadOnlyList<Bezier3>>(), e);

        // outward bearing per leg (direction leaving the center along the leg)
        var ordered = legs
            .Select(leg => (leg, bearing: Bearing(OutwardDir(leg))))
            .OrderBy(x => x.bearing)
            .ToList();

        // adjacent (cyclic) bearings too close → cannot form distinct ring slots
        for (int i = 0; i < ordered.Count; i++)
        {
            float next = ordered[(i + 1) % ordered.Count].bearing + (i + 1 == ordered.Count ? MathF.Tau : 0);
            if (next - ordered[i].bearing < DegenerateGapRad)
                return Fail(RoundaboutError.DegenerateBearings);
        }

        // every leg must actually reach past the circle to be trimmable to it
        foreach (var (leg, _) in ordered)
            if (Vector3.Distance(OuterEnd(leg), center) <= radius)
                return Fail(RoundaboutError.LegInsideRing);

        // feasibility: the smallest sub-arc must clear OneWay.MinSegmentLength.
        // decomposition depends only on the bearings, not the radius.
        float minSubSpan = float.PositiveInfinity;
        for (int i = 0; i < ordered.Count; i++)
        {
            float span = Gap(ordered, i);
            int subs = Math.Max(1, (int)MathF.Ceiling(span / QuarterTurn - 1e-4f));
            minSubSpan = MathF.Min(minSubSpan, span / subs);
        }
        float minFeasible = RoadCatalog.OneWay.MinSegmentLength / minSubSpan;
        if (radius < minFeasible - 1e-3f)
            return Fail(RoundaboutError.RadiusTooTight);

        // trim each leg to the circle
        var slots = new List<RingSlot>(ordered.Count);
        foreach (var (leg, bearing) in ordered)
        {
            var pos = center + radius * new Vector3(MathF.Cos(bearing), 0, MathF.Sin(bearing));
            var trimmed = Trim(leg, center, radius);
            if (trimmed.Length() < RoadCatalog.Get(leg.Type).MinSegmentLength)
                return Fail(RoundaboutError.LegTooShort);
            slots.Add(new RingSlot(bearing, pos, leg, trimmed, leg.EndsAtCenter));
        }

        // CCW ring arc chains, gap by gap
        var arcs = new List<IReadOnlyList<Bezier3>>(slots.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            float a0 = slots[i].Bearing;
            float span = Gap(ordered, i);
            var chain = ArcChain(center, radius, a0, span);
            // pin exact endpoints onto the slot positions (kill trig round-trip noise)
            var startPos = slots[i].Position;
            var endPos = slots[(i + 1) % slots.Count].Position;
            chain[0] = new Bezier3(startPos, chain[0].P1, chain[0].P2, chain[0].P3);
            chain[^1] = new Bezier3(chain[^1].P0, chain[^1].P1, chain[^1].P2, endPos);
            arcs.Add(chain);
        }

        return new RoundaboutPlan(center, radius, slots, arcs, null);
    }

    /// <summary>Smallest radius at which every ring sub-arc clears OneWay.MinSegmentLength,
    /// given the leg bearings. PositiveInfinity if two bearings coincide.</summary>
    public static float MinFeasibleRadius(IReadOnlyList<ApproachLeg> legs, Vector3 center)
    {
        var ordered = legs
            .Select(leg => (leg, bearing: Bearing(OutwardDir(leg))))
            .OrderBy(x => x.bearing)
            .ToList();
        float minSubSpan = float.PositiveInfinity;
        for (int i = 0; i < ordered.Count; i++)
        {
            float span = Gap(ordered, i);
            if (span < DegenerateGapRad)
                return float.PositiveInfinity;
            int subs = Math.Max(1, (int)MathF.Ceiling(span / QuarterTurn - 1e-4f));
            minSubSpan = MathF.Min(minSubSpan, span / subs);
        }
        return RoadCatalog.OneWay.MinSegmentLength / minSubSpan;
    }

    private static Vector3 OutwardDir(ApproachLeg leg)
        => leg.EndsAtCenter ? -leg.Curve.Tangent(1) : leg.Curve.Tangent(0);

    private static Vector3 OuterEnd(ApproachLeg leg)
        => leg.EndsAtCenter ? leg.Curve.Point(0) : leg.Curve.Point(1);

    private static float Bearing(Vector3 dir) => MathF.Atan2(dir.Z, dir.X);

    // cyclic gap (radians, always positive) from ordered[i] to the next bearing CCW
    private static float Gap(List<(ApproachLeg leg, float bearing)> ordered, int i)
    {
        float next = ordered[(i + 1) % ordered.Count].bearing + (i + 1 == ordered.Count ? MathF.Tau : 0);
        return next - ordered[i].bearing;
    }

    private static Bezier3 Trim(ApproachLeg leg, Vector3 center, float radius)
    {
        // distance to center is monotonic along the leg between the outer end (>radius)
        // and the center end (0); bisect for the crossing parameter.
        float lo = 0f, hi = 1f;
        // orient so lo is the outer end (dist > radius) and hi is the center end (dist ~0)
        bool outerAtZero = leg.EndsAtCenter; // Point(0) is outer when the leg ends at center
        for (int it = 0; it < 48; it++)
        {
            float mid = 0.5f * (lo + hi);
            float d = Vector3.Distance(leg.Curve.Point(mid), center);
            bool midOutside = d > radius;
            // we want the parameter where d == radius; keep the [outside, inside] bracket
            if (outerAtZero)
            {
                if (midOutside) lo = mid; else hi = mid;
            }
            else
            {
                if (midOutside) hi = mid; else lo = mid;
            }
        }
        float tCut = 0.5f * (lo + hi);
        var (a, b) = leg.Curve.Split(tCut);
        return leg.EndsAtCenter ? a : b;
    }

    private static List<Bezier3> ArcChain(Vector3 center, float radius, float a0, float span)
    {
        int subs = Math.Max(1, (int)MathF.Ceiling(span / QuarterTurn - 1e-4f));
        float sub = span / subs;
        var chain = new List<Bezier3>(subs);
        float y = center.Y;
        for (int i = 0; i < subs; i++)
        {
            float s0 = a0 + sub * i;
            float s1 = a0 + sub * (i + 1);
            float k = 4f / 3f * MathF.Tan(sub / 4f) * radius;
            Vector3 P(float a) => center + radius * new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            Vector3 T(float a) => new(-MathF.Sin(a), 0, MathF.Cos(a)); // CCW unit tangent
            var p0 = P(s0); var p3 = P(s1);
            chain.Add(new Bezier3(p0, p0 + T(s0) * k, p3 - T(s1) * k, p3));
        }
        return chain;
    }
}
