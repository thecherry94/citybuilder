using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class SimInvariantsTests
{
    [Fact]
    public void BurstOnHealthyCrossIsClean()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-200, 0, 0), new(200, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -200), new(0, 0, 200)));
        Assert.Empty(SimInvariants.CheckBurst(n, seed: 5));
    }

    [Fact]
    public void BurstSurvivesAnEmptyNetwork()
    {
        Assert.Empty(SimInvariants.CheckBurst(Net.New(), seed: 5)); // no roads: nothing to do, no crash
    }
}
