namespace CityBuilder.Domain.Geometry;

/// <summary>Cumulative arc-length lookup for a curve: maps between parameter t and
/// distance along the curve. Used for dashes, cut distances, and even spacing.</summary>
public sealed class ArcLengthTable
{
    private readonly float[] _cumulative; // distance at t = i / (N-1)

    public float TotalLength { get; }

    public ArcLengthTable(in Bezier3 curve, int samples = 128)
    {
        _cumulative = new float[samples + 1];
        var prev = curve.Point(0);
        for (int i = 1; i <= samples; i++)
        {
            var p = curve.Point(i / (float)samples);
            _cumulative[i] = _cumulative[i - 1] + System.Numerics.Vector3.Distance(prev, p);
            prev = p;
        }
        TotalLength = _cumulative[samples];
    }

    public float DistanceAtT(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float f = t * (_cumulative.Length - 1);
        int i = Math.Min((int)f, _cumulative.Length - 2);
        return float.Lerp(_cumulative[i], _cumulative[i + 1], f - i);
    }

    public float TAtDistance(float d)
    {
        if (d <= 0) return 0;
        if (d >= TotalLength) return 1;
        int lo = 0, hi = _cumulative.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_cumulative[mid] < d) lo = mid; else hi = mid;
        }
        float span = _cumulative[hi] - _cumulative[lo];
        float frac = span > 0 ? (d - _cumulative[lo]) / span : 0;
        return (lo + frac) / (_cumulative.Length - 1);
    }
}
