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
    public const float LaunchA = 3.5f;        // standstill launch, m/s² (VISSIM CC8-style)
    public const float LaunchFadeSpeed = 5f;  // m/s at which launch boost has fully faded

    /// <param name="v">own speed m/s</param>
    /// <param name="v0">desired speed m/s</param>
    /// <param name="gap">bumper-to-bumper distance to leader (FreeGap = none)</param>
    /// <param name="dv">closing speed = v − vLead</param>
    public static float Accel(float v, float v0, float gap, float dv)
    {
        float free = 1f - MathF.Pow(v / MathF.Max(v0, 0.1f), 4);
        if (gap >= FreeGap / 2)
            return EffectiveA(v) * free; // free < 0 only above v0, where EffectiveA == A anyway
        float sStar = S0 + MathF.Max(0, v * T + v * dv / (2f * MathF.Sqrt(A * B)));
        float ratio = sStar / MathF.Max(gap, 0.1f);
        float m = MathF.Min(free, 1f - ratio * ratio);    // IDM+ (Schakel et al.)
        // Launch boost applies to acceleration only: a negative model output means
        // braking, and boosting that gain would make crawling vehicles brake up to
        // 35% harder near obstacles — the boost models standing-start acceleration
        // (VISSIM CC8), not braking.
        return (m >= 0f ? EffectiveA(v) : A) * m;
    }

    // Speed-dependent launch acceleration (VISSIM CC8/CC9 pattern): a standstill
    // launch is stronger than the cruise cap, fading linearly to A by LaunchFadeSpeed.
    // The interaction term (sStar via sqrt(A*B)) intentionally keeps the base A —
    // only the leading multiplier uses the boosted value, and only when accelerating.
    private static float EffectiveA(float v)
        => v >= LaunchFadeSpeed ? A : LaunchA + (A - LaunchA) * (v / LaunchFadeSpeed);
}
