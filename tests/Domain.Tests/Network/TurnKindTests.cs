using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class TurnKindTests
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
    public void FourWayIncomingLanesHaveLeftStraightRight()
    {
        var (_, center) = FourWay();
        foreach (var group in center.Connectors.GroupBy(c => c.From))
        {
            var kinds = group.Select(c => c.Turn).OrderBy(k => k).ToArray();
            Assert.Equal(new[] { TurnKind.Straight, TurnKind.Left, TurnKind.Right }.OrderBy(k => k), kinds);
        }
    }

    [Fact]
    public void EastboundRightTurnHeadsSouth()
    {
        // locks the sign convention: right-hand traffic, Y up, +Z is "south"
        var (n, center) = FourWay();
        var eastbound = n.Edges.Values
            .Single(e => e.Curve.Point(0.5f).X < -1 && MathF.Abs(e.Curve.Point(0.5f).Z) < 1)
            .Lanes.Single(l => l.Direction == LaneDirection.Forward); // travels +X into the center
        var right = center.Connectors.Single(c => c.From == eastbound.Id && c.Turn == TurnKind.Right);
        // right turn from +X heading must exit heading +Z
        var exitDir = right.Curve.Tangent(1);
        Assert.True(exitDir.Z > 0.9f, $"expected +Z exit, got {exitDir}");
    }

    [Fact]
    public void DeadEndConnectorIsUTurn()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var edge = n.Edges[r.CreatedEdges[0]];
        var c = Assert.Single(n.Nodes[edge.StartNode].Connectors);
        Assert.Equal(TurnKind.UTurn, c.Turn);
    }

    [Fact]
    public void TeeJunctionLanesGetOnlyPossibleMovements()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(0, 0, 100)));
        var tee = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);

        foreach (var group in tee.Connectors.GroupBy(c => c.From))
        {
            var kinds = group.Select(c => c.Turn).ToHashSet();
            Assert.Equal(2, kinds.Count); // every approach has exactly 2 of the 3 movements
            Assert.DoesNotContain(TurnKind.UTurn, kinds);
        }
    }
}
