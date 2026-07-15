namespace CityBuilder.Domain.Traffic;

/// <summary>Intelligent Driver Model: acceleration from own speed, desired speed,
/// gap to the leader, and closing speed. The single behavioral primitive of the
/// tactical layer — each vehicle only ever looks at its leader.</summary>
public static class Idm
{
    public const float T = 0.95f;     // desired time headway, s (assertive)
    public const float S0 = 2f;       // standstill gap, m
    public const float A = 2.6f;      // max acceleration, m/s² (snappy, game feel)
    public const float B = 2.8f;      // comfortable braking, m/s²
    public const float FreeGap = 1e9f;

    /// <param name="v">own speed m/s</param>
    /// <param name="v0">desired speed m/s</param>
    /// <param name="gap">bumper-to-bumper distance to leader (FreeGap = none)</param>
    /// <param name="dv">closing speed = v − vLead</param>
    public static float Accel(float v, float v0, float gap, float dv)
    {
        float free = 1f - MathF.Pow(v / MathF.Max(v0, 0.1f), 4);
        if (gap >= FreeGap / 2)
            return A * free;
        float sStar = S0 + MathF.Max(0, v * T + v * dv / (2f * MathF.Sqrt(A * B)));
        float ratio = sStar / MathF.Max(gap, 0.1f);
        return A * (free - ratio * ratio);
    }
}
