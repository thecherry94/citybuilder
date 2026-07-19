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

        // every leg must actually reach past the circle to be trimmable to it
        // (the ring circle lives in XZ at the center's plane — M8)
        foreach (var leg in legs)
            if (DistXZ(OuterEnd(leg), center) <= radius)
                return Fail(RoundaboutError.LegInsideRing);

        // Trim FIRST, and put each slot at the point where the leg's curve actually
        // crosses the circle. A curved leg's tangent bearing at the center can differ
        // from its crossing bearing by several degrees — placing slots at the tangent
        // bearing bound approaches to nodes their curves missed by metres (drifting
        // endpoints, ring arcs crossing the dangling curve; M7.5 hardening find).
        var slots = new List<RingSlot>(legs.Count);
        foreach (var leg in legs)
        {
            var trimmed = Trim(leg, center, radius);
            if (trimmed.Length() < RoadCatalog.Get(leg.Type).MinSegmentLength)
                return Fail(RoundaboutError.LegTooShort);
            // defense in depth: whatever the cut, a trimmed leg must never re-enter the
            // circle (the ring arcs live there) — refuse rather than emit piercing geometry
            for (int s = 0; s <= 32; s++)
                if (DistXZ(trimmed.Point(s / 32f), center) < radius - 0.5f)
                    return Fail(RoundaboutError.LegInsideRing);

            // slot = the cut point projected exactly onto the circle; pin the trimmed
            // curve's inner endpoint onto it so approach curve and ring node coincide
            var cut = leg.EndsAtCenter ? trimmed.P3 : trimmed.P0;
            var dir = cut - center;
            float bearing = MathF.Atan2(dir.Z, dir.X);
            var pos = center + radius * new Vector3(MathF.Cos(bearing), 0, MathF.Sin(bearing));

            // The ring is planar at the center's Y, but a ramping leg meets the circle
            // ABOVE/BELOW that plane — re-profile the trimmed leg's Y linearly from its
            // outer end down onto the ring plane (pinning only the endpoint kinks the
            // tail steep: fuzz 303@241 committed 10.2% on an 8% type), and refuse the
            // conversion when even that uniform descent exceeds the leg's gradient.
            float yOuter = (leg.EndsAtCenter ? trimmed.P0 : trimmed.P3).Y;
            trimmed = leg.EndsAtCenter
                ? Reprofile(new Bezier3(trimmed.P0, trimmed.P1, trimmed.P2, pos), yOuter, pos.Y)
                : Reprofile(new Bezier3(pos, trimmed.P1, trimmed.P2, trimmed.P3), pos.Y, yOuter);
            if (VerticalRules.MaxGradient(trimmed) > RoadCatalog.Get(leg.Type).MaxGradient + 0.001f)
                return Fail(RoundaboutError.LegTooSteep);

            // the approach must meet the ring clear of the ring tangent, or the ring node
            // would carry two legs closer than the junction floor (a sharp leg)
            if (!MeetsRingCleanly(trimmed, leg.EndsAtCenter, bearing))
                return Fail(RoundaboutError.ApproachTooTangential);
            slots.Add(new RingSlot(bearing, pos, leg, trimmed, leg.EndsAtCenter));
        }
        slots.Sort((a, b) => a.Bearing.CompareTo(b.Bearing));

        // adjacent (cyclic) crossings too close → cannot form distinct ring slots
        for (int i = 0; i < slots.Count; i++)
            if (SlotGap(slots, i) < DegenerateGapRad)
                return Fail(RoundaboutError.DegenerateBearings);

        // feasibility: the smallest sub-arc must clear OneWay.MinSegmentLength
        for (int i = 0; i < slots.Count; i++)
        {
            float span = SlotGap(slots, i);
            int subs = Math.Max(1, (int)MathF.Ceiling(span / QuarterTurn - 1e-4f));
            if (radius * (span / subs) < RoadCatalog.OneWay.MinSegmentLength - 1e-3f)
                return Fail(RoundaboutError.RadiusTooTight);
        }

        // CCW ring arc chains, gap by gap
        var arcs = new List<IReadOnlyList<Bezier3>>(slots.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            var chain = ArcChain(center, radius, slots[i].Bearing, SlotGap(slots, i));
            // pin exact endpoints onto the slot positions (kill trig round-trip noise)
            var startPos = slots[i].Position;
            var endPos = slots[(i + 1) % slots.Count].Position;
            chain[0] = new Bezier3(startPos, chain[0].P1, chain[0].P2, chain[0].P3);
            chain[^1] = new Bezier3(chain[^1].P0, chain[^1].P1, chain[^1].P2, endPos);
            arcs.Add(chain);
        }

        return new RoundaboutPlan(center, radius, slots, arcs, null);
    }

    // cyclic gap (radians, always positive) from slots[i] to the next slot CCW
    private static float SlotGap(List<RingSlot> slots, int i)
    {
        float next = slots[(i + 1) % slots.Count].Bearing + (i + 1 == slots.Count ? MathF.Tau : 0);
        return next - slots[i].Bearing;
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

    // approach leg direction leaving the ring node, vs the ring tangent at that bearing:
    // must clear the junction angle floor from BOTH tangent directions, else conversion
    // would produce a sharp leg at the ring node.
    private static bool MeetsRingCleanly(in Bezier3 trimmed, bool endsAtCenter, float bearing)
    {
        var outward = endsAtCenter ? -trimmed.Tangent(1) : trimmed.Tangent(0);
        var tangent = new Vector3(-MathF.Sin(bearing), 0, MathF.Cos(bearing)); // CCW ring tangent
        float a = AngleDegXZ(outward, tangent);
        float b = AngleDegXZ(outward, -tangent);
        return MathF.Min(a, b) >= RoadNetwork.MinJunctionAngleDeg;
    }

    private static float AngleDegXZ(Vector3 u, Vector3 v)
    {
        var a = new Vector2(u.X, u.Z);
        var b = new Vector2(v.X, v.Z);
        if (a.LengthSquared() < 1e-12f || b.LengthSquared() < 1e-12f)
            return 180f;
        float cos = Math.Clamp(Vector2.Dot(Vector2.Normalize(a), Vector2.Normalize(b)), -1f, 1f);
        return MathF.Acos(cos) * 180f / MathF.PI;
    }

    private static float DistXZ(Vector3 a, Vector3 b)
        => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>Replace a curve's Y profile with a linear interpolation from
    /// <paramref name="yStart"/> (at P0) to <paramref name="yEnd"/> (at P3).</summary>
    private static Bezier3 Reprofile(in Bezier3 c, float yStart, float yEnd)
        => new(
            new Vector3(c.P0.X, yStart, c.P0.Z),
            new Vector3(c.P1.X, yStart + (yEnd - yStart) / 3f, c.P1.Z),
            new Vector3(c.P2.X, yStart + (yEnd - yStart) * 2f / 3f, c.P2.Z),
            new Vector3(c.P3.X, yEnd, c.P3.Z));

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
        // Distance to center is NOT guaranteed monotonic along the leg — a committable
        // hook can cross the radius three times (out–in–out–in). The correct cut is the
        // FIRST crossing seen from the outer end: march inward to bracket it, then bisect
        // inside that bracket only. A whole-span bisection converges to an arbitrary
        // crossing and can leave the trimmed leg piercing the ring (M7.5 review find).
        const int marchSteps = 128;
        bool outerAtZero = leg.EndsAtCenter; // Point(0) is outer when the leg ends at center
        float T(int step) => outerAtZero ? step / (float)marchSteps : 1f - step / (float)marchSteps;

        float lo = T(0), hi = T(marchSteps); // lo = outer end, hi = center end (either order in t)
        for (int s = 1; s <= marchSteps; s++)
        {
            float t = T(s);
            if (DistXZ(leg.Curve.Point(t), center) <= radius)
            {
                lo = T(s - 1);
                hi = t;
                break;
            }
        }
        for (int it = 0; it < 48; it++)
        {
            float mid = 0.5f * (lo + hi);
            bool midOutside = DistXZ(leg.Curve.Point(mid), center) > radius;
            if (midOutside) lo = mid; else hi = mid;
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
