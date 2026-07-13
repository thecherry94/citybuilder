using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class ConnectorRowTests
{
    private static IEnumerable<LaneConnector> DrivingConnectorsFrom(
        RoadNetwork n, RoadNode node, Func<RoadEdge, bool> fromEdge)
    {
        var laneToEdge = n.Edges.Values
            .SelectMany(e => e.Lanes)
            .ToDictionary(l => l.Id, l => l);
        return node.Connectors.Where(c =>
            laneToEdge[c.From].Kind == LaneKind.Driving
            && fromEdge(n.Edges[laneToEdge[c.From].Edge]));
    }

    [Fact]
    public void AutoPriorityTagsYieldOnMinorLegs()
    {
        var n = JunctionControlTests.Cross(out var node); // Street (wider? see catalog) x TwoLane
        var eff = JunctionControl.Resolve(node, n.Edges);

        foreach (var eid in node.Edges)
        {
            var expected = eff.Roles[eid] == LegRole.Main ? RightOfWay.Free : RightOfWay.Yield;
            var conns = DrivingConnectorsFrom(n, node, e => e.Id == eid).ToArray();
            Assert.NotEmpty(conns);
            Assert.All(conns, c => Assert.Equal(expected, c.Row));
        }
    }

    [Fact]
    public void AllWayStopTagsEverythingStop()
    {
        var n = JunctionControlTests.Cross(out var node);
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.AllWayStop });
        var conns = DrivingConnectorsFrom(n, node, _ => true).ToArray();
        Assert.NotEmpty(conns);
        Assert.All(conns, c => Assert.Equal(RightOfWay.Stop, c.Row));
    }

    [Fact]
    public void TrafficLightsTagSignal()
    {
        var n = JunctionControlTests.Cross(out var node);
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.TrafficLights });
        var conns = DrivingConnectorsFrom(n, node, _ => true).ToArray();
        Assert.NotEmpty(conns);
        Assert.All(conns, c => Assert.Equal(RightOfWay.Signal, c.Row));
    }

    [Fact]
    public void DeadEndUTurnStaysFree()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(40, 0, 0)));
        var deadEnd = n.Nodes.Values.First(x => x.Edges.Count == 1);
        Assert.NotEmpty(deadEnd.Connectors);
        Assert.All(deadEnd.Connectors, c => Assert.Equal(RightOfWay.Free, c.Row));
    }
}
