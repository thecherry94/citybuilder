using System.Numerics;

namespace CityBuilder.Domain.Geometry;

/// <summary>
/// Immutable cubic Bézier curve. Every road edge is one of these; a straight
/// road is a degenerate cubic with control points at 1/3 and 2/3.
/// Y is up; road geometry lives in the XZ plane (Y carried for future elevation).
/// </summary>
public readonly struct Bezier3(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
{
    public readonly Vector3 P0 = p0, P1 = p1, P2 = p2, P3 = p3;

    public static Bezier3 Line(Vector3 a, Vector3 b)
        => new(a, a + (b - a) / 3f, a + (b - a) * (2f / 3f), b);

    /// <summary>Degree-elevate a quadratic (start, control, end) to a cubic.</summary>
    public static Bezier3 FromQuadratic(Vector3 a, Vector3 ctrl, Vector3 b)
        => new(a, a + (2f / 3f) * (ctrl - a), b + (2f / 3f) * (ctrl - b), b);

    public Vector3 Point(float t)
    {
        float u = 1 - t;
        return u * u * u * P0 + 3 * u * u * t * P1 + 3 * u * t * t * P2 + t * t * t * P3;
    }

    public Vector3 Derivative(float t)
    {
        float u = 1 - t;
        return 3 * u * u * (P1 - P0) + 6 * u * t * (P2 - P1) + 3 * t * t * (P3 - P2);
    }

    public Vector3 SecondDerivative(float t)
        => 6 * (1 - t) * (P2 - 2 * P1 + P0) + 6 * t * (P3 - 2 * P2 + P1);

    /// <summary>Normalized direction of travel at t. Degenerate-safe: zero
    /// derivatives (coincident control points) fall back to nearby samples,
    /// then to the chord.</summary>
    public Vector3 Tangent(float t)
    {
        var d = Derivative(t);
        if (d.LengthSquared() > GeoConstants.Eps * GeoConstants.Eps)
            return Vector3.Normalize(d);
        // sample slightly inside the curve
        const float h = 1e-3f;
        var pa = Point(Math.Clamp(t + h, 0f, 1f));
        var pb = Point(Math.Clamp(t - h, 0f, 1f));
        var diff = pa - pb;
        if (diff.LengthSquared() > GeoConstants.Eps * GeoConstants.Eps)
            return Vector3.Normalize(diff);
        var chord = P3 - P0;
        return chord.LengthSquared() > 0 ? Vector3.Normalize(chord) : Vector3.UnitX;
    }

    /// <summary>Right-side normal in the XZ plane: right = normalize(cross(tangent, +Y))
    /// with Y zeroed. For travel along +X this is +Z.</summary>
    public Vector3 NormalXZ(float t)
    {
        var tan = Tangent(t);
        var n = Vector3.Cross(tan, Vector3.UnitY);
        n.Y = 0;
        return n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
    }

    public Vector3 OffsetPoint(float t, float offset) => Point(t) + NormalXZ(t) * offset;

    /// <summary>de Casteljau split at t into two cubics covering [0,t] and [t,1].</summary>
    public (Bezier3 a, Bezier3 b) Split(float t)
    {
        var p01 = Vector3.Lerp(P0, P1, t);
        var p12 = Vector3.Lerp(P1, P2, t);
        var p23 = Vector3.Lerp(P2, P3, t);
        var p012 = Vector3.Lerp(p01, p12, t);
        var p123 = Vector3.Lerp(p12, p23, t);
        var mid = Vector3.Lerp(p012, p123, t);
        return (new Bezier3(P0, p01, p012, mid), new Bezier3(mid, p123, p23, P3));
    }

    /// <summary>Arc length via adaptive subdivision (relative tolerance ~1e-3).</summary>
    public float Length()
    {
        return Segment(this, 0);

        static float Segment(in Bezier3 c, int depth)
        {
            float chord = Vector3.Distance(c.P0, c.P3);
            float net = Vector3.Distance(c.P0, c.P1) + Vector3.Distance(c.P1, c.P2) + Vector3.Distance(c.P2, c.P3);
            if (depth >= 16 || net - chord < 1e-4f + 1e-3f * net)
                return (net + chord) / 2f;
            var (a, b) = c.Split(0.5f);
            return Segment(a, depth + 1) + Segment(b, depth + 1);
        }
    }

    public Bezier3 Reversed() => new(P3, P2, P1, P0);
}
