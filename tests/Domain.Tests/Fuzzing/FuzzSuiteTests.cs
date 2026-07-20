using Xunit;

namespace CityBuilder.Domain.Tests.Fuzzing;

/// <summary>The default gesture-fuzz sweep. No meaningful red gate exists for a
/// fuzzer — its first full run against the real editor surface IS the gate; any
/// finding here is triaged as a domain bug (fixed + pinned into
/// <see cref="FuzzRegressionTests"/>), not weakened away.
///
/// <para><b>10k-action certification (2026-07-16):</b> all three seeds (101, 202,
/// 303) run clean at <c>CITYBUILDER_FUZZ_ACTIONS=10000</c> (30,000 actions total),
/// wall time 7m47s on a 12-core box (well under the "minutes not hours" gate — no
/// BurstEvery profiling/tuning was needed). This run found and fixed four false
/// negatives in <c>RoadNetwork.Validate</c>'s sharp-angle gate and
/// <c>BezierOps.MinRadius</c>'s fixed sampling resolution, pinned in
/// <see cref="FuzzRegressionTests"/>; see the task 6 report for full root-cause
/// writeups.</para></summary>
public class FuzzSuiteTests
{
    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    [InlineData(303)]
    public void DefaultSweepHoldsAllInvariants(int seed)
    {
        int actions = int.TryParse(Environment.GetEnvironmentVariable("CITYBUILDER_FUZZ_ACTIONS"), out var a) ? a : 300;
        var result = GestureFuzzer.Run(new FuzzOptions(seed, actions));
        var artifacts = result.Ok ? "" :
            FuzzArtifacts.DumpOnFailure(GestureFuzzer.LastNetwork, $"seed{seed}_action{result.FailedAtAction}");
        Assert.True(result.Ok,
            $"seed {seed} failed at action {result.FailedAtAction}: {result.Failure}\n" +
            string.Join("\n", result.ActionTail) + artifacts);
    }
}
