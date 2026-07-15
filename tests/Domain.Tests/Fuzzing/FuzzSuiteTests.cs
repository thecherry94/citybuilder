using Xunit;

namespace CityBuilder.Domain.Tests.Fuzzing;

/// <summary>The default gesture-fuzz sweep. No meaningful red gate exists for a
/// fuzzer — its first full run against the real editor surface IS the gate; any
/// finding here is triaged as a domain bug (fixed + pinned into
/// <see cref="FuzzRegressionTests"/>), not weakened away.</summary>
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
        Assert.True(result.Ok,
            $"seed {seed} failed at action {result.FailedAtAction}: {result.Failure}\n" +
            string.Join("\n", result.ActionTail));
    }
}
