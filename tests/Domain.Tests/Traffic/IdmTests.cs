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
}
