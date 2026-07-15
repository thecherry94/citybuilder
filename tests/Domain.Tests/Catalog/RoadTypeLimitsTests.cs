using CityBuilder.Domain.Catalog;
using Xunit;

namespace CityBuilder.Domain.Tests.Catalog;

public class RoadTypeLimitsTests
{
    [Fact]
    public void EveryTypeHasPositiveGeometryLimits()
    {
        foreach (var t in RoadCatalog.All)
        {
            Assert.True(t.MinRadius > 0, $"{t.Name} MinRadius");
            Assert.True(t.MinSegmentLength >= 8f, $"{t.Name} MinSegmentLength floor");
            Assert.True(t.MinSegmentLength >= t.Width, $"{t.Name} MinSegmentLength >= width");
        }
    }

    [Fact]
    public void SpecValues()
    {
        Assert.Equal(20f, RoadCatalog.TwoLane.MinRadius);
        Assert.Equal(35f, RoadCatalog.FourLane.MinRadius);
        Assert.Equal(10f, RoadCatalog.Street.MinRadius);
        Assert.Equal(25f, RoadCatalog.Avenue.MinRadius);
        Assert.Equal(8f, RoadCatalog.TwoLane.MinSegmentLength);   // width 8
        Assert.Equal(16f, RoadCatalog.FourLane.MinSegmentLength); // width 16
        Assert.Equal(12f, RoadCatalog.Street.MinSegmentLength);   // width 12
        Assert.Equal(21f, RoadCatalog.Avenue.MinSegmentLength);   // width 21
    }

    [Fact]
    public void NewTypesHaveExpectedLaneProfiles()
    {
        Assert.Equal(2, RoadCatalog.OneWay.ForwardCount);
        Assert.Equal(0, RoadCatalog.OneWay.BackwardCount);
        Assert.True(RoadCatalog.OneWay.IsDirectionAsymmetric);
        Assert.Equal(2, RoadCatalog.Asymmetric.ForwardCount);
        Assert.Equal(1, RoadCatalog.Asymmetric.BackwardCount);
        Assert.False(RoadCatalog.TwoLane.IsDirectionAsymmetric);
        Assert.Contains(RoadCatalog.OneWay, RoadCatalog.All);
        Assert.Contains(RoadCatalog.Asymmetric, RoadCatalog.All);
        Assert.Equal(10f, RoadCatalog.OneWay.MinRadius);
        Assert.Equal(20f, RoadCatalog.Asymmetric.MinRadius);
    }
}
