using CityBuilder.Domain.Catalog;
using Xunit;

namespace CityBuilder.Domain.Tests.Catalog;

public class MarkingLayoutTests
{
    [Fact]
    public void AsymmetricCenterLineSitsAtTheOpposingBoundary()
    {
        var lines = MarkingRules.Layout(RoadCatalog.Asymmetric).ToList();
        // boundary between backward (−4.25, w3.5 → edge −2.5) and forward (−0.75 →
        // edge −2.5): double solid at −2.5 ± 0.18
        Assert.Contains(lines, l => !l.Dashed && MathF.Abs(l.Offset - (-2.68f)) < 0.01f);
        Assert.Contains(lines, l => !l.Dashed && MathF.Abs(l.Offset - (-2.32f)) < 0.01f);
        // forward-forward separator dashed at +1.0
        Assert.Contains(lines, l => l.Dashed && MathF.Abs(l.Offset - 1.0f) < 0.01f);
    }

    [Fact]
    public void OneWayHasNoOpposingSeparationLine()
    {
        var lines = MarkingRules.Layout(RoadCatalog.OneWay).ToList();
        // single dashed separator between the two same-way lanes at offset 0
        Assert.Contains(lines, l => l.Dashed && MathF.Abs(l.Offset) < 0.01f);
        // and no double-solid pair anywhere
        Assert.DoesNotContain(lines, l => !l.Dashed && MathF.Abs(MathF.Abs(l.Offset) - 0.18f) < 0.05f);
    }
}
