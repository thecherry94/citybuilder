using System.Numerics;

namespace CityBuilder.Domain.Geometry;

/// <summary>How two roads relate where their XZ projections cross (M8). The three
/// bands are the semantic heart of elevation: coplanar crossings junction exactly as
/// pre-M8, cleared crossings pass over/under with no junction, and the band between is
/// never legal. ONE classifier feeds Validate, Commit, the commit-side segment recheck,
/// and NetworkInvariants — thresholds must never be re-derived at call sites.</summary>
public enum CrossingKind { Junction, GradeSeparated, VerticalClash }

public static class VerticalRules
{
    public static CrossingKind ClassifyCrossing(float yNew, float yExisting)
    {
        float dy = MathF.Abs(yNew - yExisting);
        if (dy < GeoConstants.JunctionYTolerance)
            return CrossingKind.Junction;
        return dy >= GeoConstants.MinClearance ? CrossingKind.GradeSeparated : CrossingKind.VerticalClash;
    }

    /// <summary>Max |dY/ds| sampled along the curve (s = horizontal run); sample count
    /// scales with arc length (~one per 8 m, floor 32) like BezierOps.MinRadius, so
    /// short and long ramps get equal resolution.</summary>
    public static float MaxGradient(in Bezier3 c) => MaxGradient(c, c.Length());

    /// <summary>Overload for hot paths that already know the arc length (the invariant
    /// checker runs per edge per fuzz action — <c>Curve.Length()</c> is adaptive
    /// subdivision and must not be recomputed there). Flat curves (the common case)
    /// short-circuit to 0 from the control points alone: a cubic's Y is bounded by its
    /// control net, so equal-Y control points ⇒ constant Y.</summary>
    public static float MaxGradient(in Bezier3 c, float arcLength)
    {
        float y0 = c.P0.Y;
        if (MathF.Abs(c.P1.Y - y0) < GeoConstants.Eps
            && MathF.Abs(c.P2.Y - y0) < GeoConstants.Eps
            && MathF.Abs(c.P3.Y - y0) < GeoConstants.Eps)
            return 0f;

        int samples = Math.Max(32, (int)(arcLength / 8f));
        float worst = 0f;
        var prev = c.Point(0);
        for (int i = 1; i <= samples; i++)
        {
            var p = c.Point(i / (float)samples);
            float run = new Vector2(p.X - prev.X, p.Z - prev.Z).Length();
            if (run > GeoConstants.Eps)
                worst = MathF.Max(worst, MathF.Abs(p.Y - prev.Y) / run);
            prev = p;
        }
        return worst;
    }
}
