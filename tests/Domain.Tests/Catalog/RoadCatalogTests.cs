using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Catalog;

public class RoadCatalogTests
{
    [Fact]
    public void SpeedLimitsAreSensible()
    {
        foreach (var t in RoadCatalog.All)
            Assert.True(t.SpeedLimit > 5f, $"{t.Name} speed limit {t.SpeedLimit}");
        // country roads are faster than urban streets
        Assert.Equal(100f / 3.6f, RoadCatalog.FourLane.SpeedLimit, 2);
        Assert.Equal(50f / 3.6f, RoadCatalog.Street.SpeedLimit, 2);
        Assert.True(RoadCatalog.TwoLane.SpeedLimit > RoadCatalog.Avenue.SpeedLimit);
    }

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
        // Asymmetric roads (one-way, 2+1) intentionally break the spatial convention
        // where drawing direction differs from traffic direction; skip them.
        foreach (var type in RoadCatalog.All.Where(t => !t.IsDirectionAsymmetric))
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
