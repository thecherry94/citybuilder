using Xunit;

namespace CityBuilder.Domain.Tests.Fuzzing;

/// <summary>Seed-pinned regressions harvested from <see cref="GestureFuzzer"/> findings.
/// Every fact here corresponds to a real bug the fuzzer found, minimized to the
/// smallest (seed, action count) that reproduces it, fixed at the root cause in domain
/// code, and pinned permanently — so the exact scenario that once broke
/// <see cref="CityBuilder.Domain.Network.NetworkInvariants"/> stays covered even if the
/// general fuzz sweep's seeds or default action count later change. Each fact carries a
/// comment naming the root cause it guards against.</summary>
public class FuzzRegressionTests
{
    /// <summary>Root cause: a Chain-mode gesture split an existing edge at a point
    /// where the new curve's start tangent landed within
    /// <see cref="CityBuilder.Domain.Network.RoadNetwork.TangentContinuationDeg"/> of
    /// the split edge's own tangent — a legitimate G1 ramp-exit that
    /// <c>RoadNetwork.Validate</c> already allows and commits (see the passing,
    /// pre-existing <c>PlacementTests.TangentialDepartureFromMidEdgeIsValidAndCommits</c>).
    /// <see cref="CityBuilder.Domain.Network.NetworkInvariants.CheckLegAngles"/> didn't
    /// know about that exemption and flagged the resulting 0 deg leg pair as
    /// <c>SharpAngle</c>-violating, even though the network was built exactly per
    /// contract. Fixed by mirroring the exemption: pairs within
    /// <c>TangentContinuationDeg</c> of each other are skipped, since that's the only
    /// way a validly committed network can ever have two legs that close together.</summary>
    [Fact]
    public void Seed202RampContinuationIsNotAFalseSharpAngle()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(202, 5));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }
}
