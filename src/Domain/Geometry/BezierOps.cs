using System.Numerics;

namespace CityBuilder.Domain.Geometry;

/// <summary>Curve algorithms that operate on <see cref="Bezier3"/> in the XZ plane.</summary>
public static class BezierOps
{
    private const float FlatTol = 1e-3f;
    private const int MaxDepth = 28;

    /// <summary>Adaptive tessellation: returns increasing t values (always including 0 and 1)
    /// whose chords deviate from the curve by at most <paramref name="chordTolerance"/>.</summary>
    public static List<float> Tessellate(in Bezier3 c, float chordTolerance)
    {
        var ts = new List<float> { 0f };
        Recurse(c, 0f, 1f, 0);
        ts.Add(1f);
        return ts;

        void Recurse(in Bezier3 curve, float t0, float t1, int depth)
        {
            float tm = (t0 + t1) / 2;
            var chordMid = (curve.Point(t0) + curve.Point(t1)) / 2;
            var curveMid = curve.Point(tm);
            if (depth >= 12 || Vector3.Distance(chordMid, curveMid) <= chordTolerance)
                return;
            Recurse(curve, t0, tm, depth + 1);
            ts.Add(tm);
            Recurse(curve, tm, t1, depth + 1);
        }
    }

    /// <summary>Closest point on the curve: coarse sampling + local refinement.</summary>
    public static (float t, float dist) ClosestPoint(in Bezier3 c, Vector3 p)
    {
        const int samples = 64;
        float bestT = 0, bestD = float.MaxValue;
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            float d = Vector3.DistanceSquared(c.Point(t), p);
            if (d < bestD) { bestD = d; bestT = t; }
        }

        float lo = MathF.Max(0, bestT - 1f / samples);
        float hi = MathF.Min(1, bestT + 1f / samples);
        for (int i = 0; i < 40; i++)
        {
            float m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (Vector3.DistanceSquared(c.Point(m1), p) < Vector3.DistanceSquared(c.Point(m2), p))
                hi = m2;
            else
                lo = m1;
        }
        float tFinal = (lo + hi) / 2;
        return (tFinal, Vector3.Distance(c.Point(tFinal), p));
    }

    /// <summary>All crossings of two curves projected to the XZ plane, as parameter pairs.
    /// Recursive subdivision on control-point AABBs; deduplicates near-identical hits.</summary>
    public static List<(float t1, float t2)> Intersections(in Bezier3 a, in Bezier3 b)
    {
        var results = new List<(float t1, float t2)>();
        Recurse(a, 0, 1, b, 0, 1, 0);
        return results;

        void Recurse(in Bezier3 ca, float a0, float a1, in Bezier3 cb, float b0, float b1, int depth)
        {
            if (!AabbOverlap(ca, cb))
                return;

            bool aFlat = IsFlat(ca);
            bool bFlat = IsFlat(cb);
            if ((aFlat && bFlat) || depth >= MaxDepth)
            {
                if (SegmentIntersect(ToXZ(ca.P0), ToXZ(ca.P3), ToXZ(cb.P0), ToXZ(cb.P3), out float u, out float v))
                {
                    float t1 = a0 + u * (a1 - a0);
                    float t2 = b0 + v * (b1 - b0);
                    if (!results.Any(r => MathF.Abs(r.t1 - t1) < 1e-3f && MathF.Abs(r.t2 - t2) < 1e-3f))
                        results.Add((t1, t2));
                }
                return;
            }

            if (!aFlat && (bFlat || Extent(ca) >= Extent(cb)))
            {
                var (l, r) = ca.Split(0.5f);
                float am = (a0 + a1) / 2;
                Recurse(l, a0, am, cb, b0, b1, depth + 1);
                Recurse(r, am, a1, cb, b0, b1, depth + 1);
            }
            else
            {
                var (l, r) = cb.Split(0.5f);
                float bm = (b0 + b1) / 2;
                Recurse(ca, a0, a1, l, b0, bm, depth + 1);
                Recurse(ca, a0, a1, r, bm, b1, depth + 1);
            }
        }
    }

    /// <summary>True if the curve crosses itself in the XZ plane.</summary>
    public static bool SelfIntersects(in Bezier3 c)
    {
        const int spans = 32;
        var pts = new Vector2[spans + 1];
        for (int i = 0; i <= spans; i++)
            pts[i] = ToXZ(c.Point(i / (float)spans));
        for (int i = 0; i < spans; i++)
        for (int j = i + 2; j < spans; j++)
        {
            if (i == 0 && j == spans - 1)
                continue; // closing pair shares no endpoint but adjacent-wrap tolerance not needed for open curves
            if (SegmentIntersect(pts[i], pts[i + 1], pts[j], pts[j + 1], out _, out _))
                return true;
        }
        return false;
    }

    /// <summary>Smallest radius of curvature in the XZ plane, sampled. Straight
    /// (zero-curvature) curves return +infinity.</summary>
    public static float MinRadius(in Bezier3 c, int samples = 32)
    {
        float maxK = 0f;
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            var d = c.Derivative(t);
            var dd = c.SecondDerivative(t);
            float speedSq = d.X * d.X + d.Z * d.Z;
            if (speedSq < 1e-6f)
                continue; // degenerate spot; neighbors cover it
            float k = MathF.Abs(d.X * dd.Z - d.Z * dd.X) / (speedSq * MathF.Sqrt(speedSq));
            maxK = MathF.Max(maxK, k);
        }
        return maxK < 1e-9f ? float.PositiveInfinity : 1f / maxK;
    }

    private static Vector2 ToXZ(Vector3 v) => new(v.X, v.Z);

    private static bool IsFlat(in Bezier3 c)
    {
        Vector2 p0 = ToXZ(c.P0), p1 = ToXZ(c.P1), p2 = ToXZ(c.P2), p3 = ToXZ(c.P3);
        var chord = p3 - p0;
        float len = chord.Length();
        if (len < GeoConstants.Eps)
            return Vector2.Distance(p1, p0) < FlatTol && Vector2.Distance(p2, p0) < FlatTol;
        var dir = chord / len;
        return DistToLine(p1) < FlatTol && DistToLine(p2) < FlatTol;

        float DistToLine(Vector2 p)
        {
            var rel = p - p0;
            return MathF.Abs(rel.X * dir.Y - rel.Y * dir.X);
        }
    }

    private static float Extent(in Bezier3 c)
    {
        var (min, max) = Bounds(c);
        return (max - min).Length();
    }

    private static (Vector2 min, Vector2 max) Bounds(in Bezier3 c)
    {
        var min = Vector2.Min(Vector2.Min(ToXZ(c.P0), ToXZ(c.P1)), Vector2.Min(ToXZ(c.P2), ToXZ(c.P3)));
        var max = Vector2.Max(Vector2.Max(ToXZ(c.P0), ToXZ(c.P1)), Vector2.Max(ToXZ(c.P2), ToXZ(c.P3)));
        return (min, max);
    }

    private static bool AabbOverlap(in Bezier3 a, in Bezier3 b)
    {
        var (amin, amax) = Bounds(a);
        var (bmin, bmax) = Bounds(b);
        const float pad = GeoConstants.Eps;
        return amin.X <= bmax.X + pad && bmin.X <= amax.X + pad
            && amin.Y <= bmax.Y + pad && bmin.Y <= amax.Y + pad;
    }

    /// <summary>2D segment intersection: p1→p2 vs q1→q2. u, v are the fractional positions.</summary>
    internal static bool SegmentIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2, out float u, out float v)
    {
        u = v = 0;
        var r = p2 - p1;
        var s = q2 - q1;
        float denom = r.X * s.Y - r.Y * s.X;
        if (MathF.Abs(denom) < 1e-12f)
            return false; // parallel/collinear: overlap is handled by placement validation, not here
        var qp = q1 - p1;
        u = (qp.X * s.Y - qp.Y * s.X) / denom;
        v = (qp.X * r.Y - qp.Y * r.X) / denom;
        return u >= -1e-6f && u <= 1 + 1e-6f && v >= -1e-6f && v <= 1 + 1e-6f;
    }
}
