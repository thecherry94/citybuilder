namespace CityBuilder.Domain.Tests.Fuzzing;

/// <summary>Seed-pinned regressions harvested from <see cref="GestureFuzzer"/> findings.
/// Starts empty by design: every fact added here corresponds to a real bug the fuzzer
/// found, minimized to the smallest (seed, action count) that reproduces it, fixed at
/// the root cause in domain code, and pinned permanently — so the exact scenario that
/// once broke <see cref="CityBuilder.Domain.Network.NetworkInvariants"/> stays covered
/// even if the general fuzz sweep's seeds or default action count later change. Each
/// fact should carry a comment naming the root cause it guards against.</summary>
public class FuzzRegressionTests
{
}
