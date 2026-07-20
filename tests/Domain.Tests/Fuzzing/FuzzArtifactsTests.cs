using CityBuilder.Domain.Tests.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Fuzzing;

public class FuzzArtifactsTests
{
    [Fact]
    public void WritesSvgAndJsonAndReturnsPathSuffix()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));

        var msg = FuzzArtifacts.DumpOnFailure(n, "unit_test_probe");

        Assert.Contains("geometry dumps:", msg);
        var baseName = Path.Combine(Directory.GetCurrentDirectory(), "fuzz-artifacts", "unit_test_probe");
        Assert.True(File.Exists(baseName + ".svg"));
        Assert.True(File.Exists(baseName + ".json"));
        File.Delete(baseName + ".svg");
        File.Delete(baseName + ".json");
    }

    [Fact]
    public void NullNetworkIsANoOp()
    {
        Assert.Equal("", FuzzArtifacts.DumpOnFailure(null, "never_written"));
    }
}
