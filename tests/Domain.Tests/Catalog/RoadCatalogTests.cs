using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Catalog;

public class RoadCatalogTests
{
    [Fact]
    public void TwoLaneHasOneLanePerDirection()
    {
        var t = RoadCatalog.TwoLane;
        Assert.Equal(2, t.Lanes.Count);
        Assert.Single(t.Lanes, l => l.Direction == LaneDirection.Forward);
        Assert.Single(t.Lanes, l => l.Direction == LaneDirection.Backward);
    }

    [Fact]
    public void FourLaneHasTwoLanesPerDirection()
    {
        var t = RoadCatalog.FourLane;
        Assert.Equal(4, t.Lanes.Count);
        Assert.Equal(2, t.Lanes.Count(l => l.Direction == LaneDirection.Forward));
        Assert.Equal(2, t.Lanes.Count(l => l.Direction == LaneDirection.Backward));
    }

    [Fact]
    public void ForwardLanesSitOnPositiveOffsets()
    {
        foreach (var type in RoadCatalog.All)
        foreach (var lane in type.Lanes)
            Assert.True(lane.Direction == LaneDirection.Forward ? lane.Offset > 0 : lane.Offset < 0,
                $"{type.Name} lane offset {lane.Offset} direction {lane.Direction}");
    }

    [Fact]
    public void LanesFitInsideRoadWidth()
    {
        foreach (var type in RoadCatalog.All)
        foreach (var lane in type.Lanes)
            Assert.True(System.MathF.Abs(lane.Offset) + lane.Width / 2 <= type.Width / 2,
                $"{type.Name} lane at {lane.Offset} exceeds width {type.Width}");
    }

    [Fact]
    public void GetResolvesByIdAndThrowsOnUnknown()
    {
        Assert.Same(RoadCatalog.FourLane, RoadCatalog.Get(RoadCatalog.FourLane.Id));
        Assert.Throws<KeyNotFoundException>(() => RoadCatalog.Get(new RoadTypeId(999)));
    }
}
