using System.Numerics;
using CityBuilder.Domain.Geometry;
using Xunit;

namespace CityBuilder.Domain.Tests.Geometry;

public class VerticalRulesTests
{
    [Theory]
    [InlineData(0f, 0f, CrossingKind.Junction)]
    [InlineData(0.59f, 0f, CrossingKind.Junction)]
    [InlineData(0.61f, 0f, CrossingKind.VerticalClash)]
    [InlineData(4.69f, 0f, CrossingKind.VerticalClash)]
    [InlineData(4.7f, 0f, CrossingKind.GradeSeparated)]
    [InlineData(0f, 12f, CrossingKind.GradeSeparated)]
    [InlineData(10f, 12f, CrossingKind.VerticalClash)]
    public void ClassifiesByVerticalSeparation(float yNew, float yOld, CrossingKind expected)
        => Assert.Equal(expected, VerticalRules.ClassifyCrossing(yNew, yOld));

    [Fact]
    public void FlatCurveHasZeroGradient()
        => Assert.Equal(0f, VerticalRules.MaxGradient(Bezier3.Line(new(0, 0, 0), new(100, 0, 0))), 3);

    [Fact]
    public void UniformRampGradientMatchesRiseOverRun()
    {
        // 100 m run, 8 m rise, linear Y → gradient ~0.08 everywhere
        var c = new Bezier3(new(0, 0, 0), new(33.3f, 2.667f, 0), new(66.7f, 5.333f, 0), new(100, 8, 0));
        Assert.Equal(0.08f, VerticalRules.MaxGradient(c), 2);
    }

    [Fact]
    public void EndLoadedProfileExceedsItsAverageGradient()
    {
        // all 8 m of rise crammed into the last third → local gradient >> 8%
        var c = new Bezier3(new(0, 0, 0), new(33.3f, 0, 0), new(66.7f, 0, 0), new(100, 8, 0));
        Assert.True(VerticalRules.MaxGradient(c) > 0.15f);
    }
}
