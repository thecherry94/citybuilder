using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class UrbanRoadTests
{
    [Fact]
    public void AdjacentLanesNeverOverlapInAnyType()
    {
        foreach (var type in RoadCatalog.All)
        {
            var sorted = type.Lanes.OrderBy(l => l.Offset).ToArray();
            for (int i = 0; i + 1 < sorted.Length; i++)
            {
                float rightEdge = sorted[i].Offset + sorted[i].Width / 2;
                float leftEdge = sorted[i + 1].Offset - sorted[i + 1].Width / 2;
                Assert.True(rightEdge <= leftEdge + 0.01f,
                    $"{type.Name}: lanes at {sorted[i].Offset} and {sorted[i + 1].Offset} overlap");
            }
        }
    }

    [Fact]
    public void UrbanTypesCarrySidewalks()
    {
        Assert.Contains(RoadCatalog.Street.Lanes, l => l.Kind == LaneKind.Sidewalk);
        Assert.Contains(RoadCatalog.Avenue.Lanes, l => l.Kind == LaneKind.Sidewalk);
        Assert.Contains(RoadCatalog.Avenue.Lanes, l => l.Kind == LaneKind.Bicycle);
    }

    [Fact]
    public void ConnectorsNeverMixLaneKinds()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), RoadCatalog.Avenue.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.Street.Id));

        var lanes = n.Edges.Values.SelectMany(e => e.Lanes).ToDictionary(l => l.Id);
        foreach (var node in n.Nodes.Values)
        foreach (var c in node.Connectors)
            Assert.Equal(lanes[c.From].Kind, lanes[c.To].Kind);
    }

    [Fact]
    public void EachLaneKindGraphIsStronglyConnectedOnAvenueCross()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), RoadCatalog.Avenue.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.Avenue.Id));

        Assert.True(LaneGraph.IsStronglyConnected(n, LaneKind.Driving), "driving graph");
        Assert.True(LaneGraph.IsStronglyConnected(n, LaneKind.Bicycle), "bicycle graph");
        Assert.True(LaneGraph.IsStronglyConnected(n, LaneKind.Sidewalk), "sidewalk graph");
    }

    [Fact]
    public void MixedKindNetworkIsNotConnectedAcrossKinds()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), RoadCatalog.Avenue.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.Avenue.Id));
        // whole-graph connectivity fails by design: kinds are separate networks
        Assert.False(LaneGraph.IsStronglyConnected(n));
    }

    [Fact]
    public void StreetCrossingAvenueKeepsDrivingAndSidewalkGraphsConnected()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), RoadCatalog.Avenue.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.Street.Id));

        Assert.True(LaneGraph.IsStronglyConnected(n, LaneKind.Driving), "driving graph");
        Assert.True(LaneGraph.IsStronglyConnected(n, LaneKind.Sidewalk), "sidewalk graph");
        // bicycle lanes exist only on the avenue; they still form their own loop via dead-end U-turns
        Assert.True(LaneGraph.IsStronglyConnected(n, LaneKind.Bicycle), "bicycle graph");
    }
}
