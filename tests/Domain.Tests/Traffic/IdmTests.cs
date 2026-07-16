using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

/// <summary>Direct unit tests of the IDM/IDM+ acceleration primitive, isolated from
/// the rest of the simulation — no network, no vehicles, just the math.</summary>
public class IdmTests
{
    [Fact]
    public void EquilibriumGapIsExactlyS0PlusVT()
    {
        // At equilibrium (accel == 0, dv == 0) IDM+ must hold gap == S0 + v*T.
        // Plain IDM inflates this gap by 1/sqrt(1-(v/v0)^4) — the sluggishness root cause.
        float v = 10f, v0 = 14f;
        float eq = Idm.S0 + v * Idm.T;
        float accel = Idm.Accel(v, v0, gap: eq, dv: 0f);
        Assert.True(MathF.Abs(accel) < 0.05f,
            $"IDM+ should be ~zero accel at gap==s0+vT, got {accel:F3}");
        Assert.True(Idm.Accel(v, v0, eq * 1.3f, 0f) > 0.1f, "should accelerate when gap is generous");
        Assert.True(Idm.Accel(v, v0, eq * 0.7f, 0f) < -0.1f, "should brake when gap is tight");
    }

    [Fact]
    public void LaunchAccelerationExceedsCruiseAcceleration()
    {
        float atRest = Idm.Accel(v: 0f, v0: 14f, gap: Idm.FreeGap, dv: 0f);
        float atSpeed = Idm.Accel(v: 6f, v0: 14f, gap: Idm.FreeGap, dv: 0f);
        Assert.True(atRest > 3.2f, $"standstill launch should use ~3.5 m/s², got {atRest:F2}");
        Assert.True(atSpeed < 2.6f * 1.01f, "above fade speed the cruise cap must hold");
    }

    [Fact]
    public void LaunchBoostNeverAmplifiesBraking()
    {
        // A crawling vehicle hard against an obstacle (ratio > 1 → negative model output)
        // must brake with the BASE A gain, not the launch-boosted one: the boost models
        // stronger standing-start acceleration (VISSIM CC8), not stronger braking.
        float v = 2f, v0 = 14f, gap = 2f;
        float free = 1f - MathF.Pow(v / MathF.Max(v0, 0.1f), 4);
        float sStar = Idm.S0 + MathF.Max(0, v * Idm.T);
        float ratio = sStar / MathF.Max(gap, 0.1f);
        float plain = Idm.A * MathF.Min(free, 1f - ratio * ratio); // hand-computed base-A value
        float actual = Idm.Accel(v, v0, gap, dv: 0f);
        Assert.True(plain < 0f, "fixture must exercise the braking branch");
        Assert.Equal(plain, actual, 5);
        // ...while the standstill free-road launch keeps the boost.
        Assert.True(Idm.Accel(0f, 14f, Idm.FreeGap, 0f) > 3.2f, "launch boost must survive the fix");
    }

    [Fact]
    public void FreeRoadBrakingAboveDesiredSpeedUsesBaseAGain()
    {
        // v0 < v < LaunchFadeSpeed can now happen for real: curvature-based turn
        // speeds floor a tight turn's v0 at 4 m/s, below LaunchFadeSpeed (5). A
        // vehicle still coasting at 4.9 m/s when it enters that turn (v0 = 4) is
        // decelerating on the free-road branch (no leader) — that must use the base
        // A gain, not EffectiveA(v), which would still be launch-boosted here and so
        // over-brake (the same failure mode the min-form's sign guard already avoids).
        const float v = 4.9f, v0 = 4f;
        float free = 1f - MathF.Pow(v / v0, 4);
        Assert.True(free < 0f, "fixture must exercise the free-road braking branch");
        float expected = Idm.A * free; // base-A computation, no launch boost
        float actual = Idm.Accel(v, v0, gap: Idm.FreeGap, dv: 0f);
        Assert.Equal(expected, actual, 5);
    }
}
