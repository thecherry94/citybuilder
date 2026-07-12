using System.Numerics;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

/// <summary>Cubic fitting used by bulldoze healing: can two edges that meet at a
/// degree-2 node be replaced by a single cubic without visible deviation?
/// Schneider's algorithm (Graphics Gems): endpoints and end tangent directions are
/// fixed (preserving G1 continuity with the rest of the network), only the two
/// control-point distances are solved by least squares, with closest-point
/// reparameterization iterations.</summary>
public static class CurveFit
{
    /// <summary>Fit one cubic through the composite of two edges sharing
    /// <paramref name="sharedNode"/>. The fit runs from edge a's far end to edge b's
    /// far end. Returns the fitted curve and the max deviation of the composite
    /// samples from it (meters).</summary>
    public static (Bezier3 curve, float maxError) FitComposite(
        RoadEdge a, RoadEdge b, NodeId sharedNode, IReadOnlyDictionary<NodeId, RoadNode> nodes)
    {
        var ca = a.EndNode == sharedNode ? a.Curve : a.Curve.Reversed();
        var cb = b.StartNode == sharedNode ? b.Curve : b.Curve.Reversed();

        float lenA = a.ArcLength.TotalLength, lenB = b.ArcLength.TotalLength;
        float total = lenA + lenB;
        var tableA = a.EndNode == sharedNode ? a.ArcLength : new ArcLengthTable(ca);
        var tableB = b.StartNode == sharedNode ? b.ArcLength : new ArcLengthTable(cb);

        const int K = 64;
        var samples = new Vector3[K + 1];
        for (int i = 0; i <= K; i++)
        {
            float d = total * i / K;
            samples[i] = d <= lenA
                ? ca.Point(tableA.TAtDistance(d))
                : cb.Point(tableB.TAtDistance(d - lenA));
        }

        var tHat1 = ca.Tangent(0);       // leaving the composite start
        var tHat2 = -cb.Tangent(1);      // pointing back into the composite at its end

        // reparameterization converges linearly (error roughly halves per iteration),
        // so allow enough rounds and bail out once well inside the merge tolerance
        var fitted = FitCubic(samples, tHat1, tHat2);
        float maxError = MaxError(fitted, samples);
        for (int iter = 0; iter < 16 && maxError > GeoConstants.MergeTolerance / 4; iter++)
        {
            var u = new float[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                u[i] = BezierOps.ClosestPoint(fitted, samples[i]).t;
            u[0] = 0;
            u[^1] = 1;
            var next = FitCubic(samples, tHat1, tHat2, u);
            float nextError = MaxError(next, samples);
            if (nextError >= maxError)
                break; // stalled
            fitted = next;
            maxError = nextError;
        }
        return (fitted, maxError);
    }

    private static float MaxError(in Bezier3 fit, Vector3[] samples)
    {
        float maxError = 0;
        foreach (var q in samples)
        {
            var (_, dist) = BezierOps.ClosestPoint(fit, q);
            maxError = MathF.Max(maxError, dist);
        }
        return maxError;
    }

    /// <summary>Least-squares cubic through the samples with fixed endpoints and end
    /// tangent directions; solves for the two control distances (Schneider).
    /// Uses chord-length parameterization unless explicit parameters are given.</summary>
    public static Bezier3 FitCubic(IReadOnlyList<Vector3> q, Vector3 tHat1, Vector3 tHat2, float[]? parameters = null)
    {
        Vector3 p0 = q[0], p3 = q[^1];
        int n = q.Count;
        float chord = Vector3.Distance(p0, p3);
        if (chord < GeoConstants.Eps)
            return Bezier3.Line(p0, p3);

        var u = parameters ?? new float[n];
        if (parameters is null)
        {
            for (int i = 1; i < n; i++)
                u[i] = u[i - 1] + Vector3.Distance(q[i], q[i - 1]);
            float totalChord = u[n - 1];
            if (totalChord < GeoConstants.Eps)
                return Bezier3.Line(p0, p3);
            for (int i = 0; i < n; i++)
                u[i] /= totalChord;
        }

        float c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        for (int i = 0; i < n; i++)
        {
            float t = u[i], s = 1 - t;
            float b0 = s * s * s, b1 = 3 * s * s * t, b2 = 3 * s * t * t, b3 = t * t * t;
            var a0 = tHat1 * b1;
            var a1 = tHat2 * b2;
            var x = q[i] - ((b0 + b1) * p0 + (b2 + b3) * p3);
            c00 += Vector3.Dot(a0, a0);
            c01 += Vector3.Dot(a0, a1);
            c11 += Vector3.Dot(a1, a1);
            x0 += Vector3.Dot(a0, x);
            x1 += Vector3.Dot(a1, x);
        }

        float det = c00 * c11 - c01 * c01;
        float alpha, beta;
        if (MathF.Abs(det) < 1e-9f)
        {
            alpha = beta = chord / 3f;
        }
        else
        {
            alpha = (x0 * c11 - x1 * c01) / det;
            beta = (x1 * c00 - x0 * c01) / det;
            if (!float.IsFinite(alpha) || !float.IsFinite(beta) || alpha <= GeoConstants.Eps || beta <= GeoConstants.Eps)
                alpha = beta = chord / 3f;
        }

        return new Bezier3(p0, p0 + tHat1 * alpha, p3 + tHat2 * beta, p3);
    }
}
