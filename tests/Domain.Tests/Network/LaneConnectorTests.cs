using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class LaneConnectorTests
{
    private static (RoadNetwork n, RoadNode center) FourWay()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        var center = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        return (n, center);
    }

    [Fact]
    public void FourWayTwoLaneHasTwelveConnectors()
    {
        var (_, center) = FourWay();
        // 4 incoming lanes × 3 outgoing on other edges
        Assert.Equal(12, center.Connectors.Count);
    }

    [Fact]
    public void NoUTurnConnectors()
    {
        var (n, center) = FourWay();
        var laneToEdge = n.Edges.Values.SelectMany(e => e.Lanes).ToDictionary(l => l.Id, l => l.Edge);
        Assert.All(center.Connectors, c => Assert.NotEqual(laneToEdge[c.From], laneToEdge[c.To]));
    }

    [Fact]
    public void ConnectorEndpointsSitOnLaneCutPoints()
    {
        var (n, center) = FourWay();
        foreach (var c in center.Connectors)
        {
            var fromLane = n.Edges.Values.SelectMany(e => e.Lanes).Single(l => l.Id == c.From);
            var edge = n.Edges[fromLane.Edge];
            float tCut = center.Junction.CutT[edge.Id];
            var expected = edge.Curve.OffsetPoint(tCut, fromLane.Offset);
            Assert.True(Vector3.Distance(c.Curve.Point(0), expected) < 1e-3f);
        }
    }

    [Fact]
    public void DeadEndNodeHasNoConnectors()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var edge = n.Edges[r.CreatedEdges[0]];
        Assert.Empty(n.Nodes[edge.StartNode].Connectors);
    }

    [Fact]
    public void GridNetworkLaneGraphIsStronglyConnected()
    {
        var n = Net.New();
        for (int i = 0; i <= 2; i++)
        {
            Net.Commit(n, Net.Straight(new(0, 0, i * 100), new(200, 0, i * 100)));
            Net.Commit(n, Net.Straight(new(i * 100, 0, 0), new(i * 100, 0, 200)));
        }
        Assert.True(n.Edges.Count >= 12, $"grid built {n.Edges.Count} edges");
        Assert.True(LaneGraph.IsStronglyConnected(n));
    }

    [Fact]
    public void FourLaneCrossFourLaneHasFullConnectorFanOut()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), RoadCatalog.FourLane.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.FourLane.Id));
        var center = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        // 8 incoming lanes (2 per approach) × 6 outgoing lanes on other edges
        Assert.Equal(48, center.Connectors.Count);
    }
}
