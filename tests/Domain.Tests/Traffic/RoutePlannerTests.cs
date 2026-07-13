using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class RoutePlannerTests
{
    private static EdgeId EdgeAt(RoadNetwork n, Vector3 mid)
        => n.Edges.Values.Single(e => Vector3.Distance(e.Curve.Point(0.5f), mid) < 3f).Id;

    [Fact]
    public void PlansStraightThrough()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(200, 0, 0)));

        var from = EdgeAt(n, new Vector3(50, 0, 0));
        var to = EdgeAt(n, new Vector3(150, 0, 0));
        var route = RoutePlanner.Plan(n, from, forward: true, to);

        Assert.NotNull(route);
        Assert.Equal(new[] { from, to }, route!.Steps.Select(s => s.Edge).ToArray());
        Assert.All(route.Steps, s => Assert.True(s.Forward));
    }

    [Fact]
    public void UnreachableReturnsNull()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 500), new Vector3(100, 0, 500)));

        var route = RoutePlanner.Plan(n,
            EdgeAt(n, new Vector3(50, 0, 0)), true,
            EdgeAt(n, new Vector3(50, 0, 500)));
        Assert.Null(route);
    }

    [Fact]
    public void ControlDelaySwaysRouteChoice()
    {
        // Symmetric diamond A→(B1|B2)→C with shallow (straight-classified) bends.
        // B1 carries a stub road and is set to all-way stop; B2 is a free bend.
        // The only cost difference is the 4 s stop delay → route must go via B2.
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-100, 0, 0), new Vector3(0, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, -25)));
        Net.Commit(n, Net.Straight(new Vector3(100, 0, -25), new Vector3(200, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 25)));
        Net.Commit(n, Net.Straight(new Vector3(100, 0, 25), new Vector3(200, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(200, 0, 0), new Vector3(300, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(100, 0, -25), new Vector3(100, 0, -100)));

        RoadNode NodeAt(Vector3 p) => n.Nodes.Values.Single(x => Vector3.Distance(x.Position, p) < 1f);
        // neutralize priority-role asymmetry at the forks; penalize only B1
        n.ConfigureJunction(NodeAt(new Vector3(0, 0, 0)).Id,
            JunctionConfig.Default with { Mode = JunctionControlMode.None });
        n.ConfigureJunction(NodeAt(new Vector3(200, 0, 0)).Id,
            JunctionConfig.Default with { Mode = JunctionControlMode.None });
        n.ConfigureJunction(NodeAt(new Vector3(100, 0, -25)).Id,
            JunctionConfig.Default with { Mode = JunctionControlMode.AllWayStop });

        var route = RoutePlanner.Plan(n,
            EdgeAt(n, new Vector3(-50, 0, 0)), true,
            EdgeAt(n, new Vector3(250, 0, 0)));

        Assert.NotNull(route);
        Assert.Contains(route!.Steps, s =>
            Vector3.Distance(n.Edges[s.Edge].Curve.Point(0.5f), new Vector3(50, 0, 12.5f)) < 5f);
        Assert.DoesNotContain(route.Steps, s =>
            Vector3.Distance(n.Edges[s.Edge].Curve.Point(0.5f), new Vector3(50, 0, -12.5f)) < 5f);
    }

    [Fact]
    public void PlansFromMidNetworkInEitherDirection()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(100, 0, 0), new Vector3(200, 0, 0)));

        var west = EdgeAt(n, new Vector3(50, 0, 0));
        var east = EdgeAt(n, new Vector3(150, 0, 0));
        // heading backward (toward -x) on the east edge, goal = west edge
        var route = RoutePlanner.Plan(n, east, forward: false, west);
        Assert.NotNull(route);
        Assert.False(route!.Steps[0].Forward);
        Assert.Equal(west, route.Steps[^1].Edge);
    }

    [Fact]
    public void TripToOwnEdgeIsSingleStep()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
        var e = n.Edges.Keys.Single();
        var route = RoutePlanner.Plan(n, e, true, e);
        Assert.NotNull(route);
        Assert.Single(route!.Steps);
    }
}
