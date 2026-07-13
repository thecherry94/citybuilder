using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class JunctionControlTests
{
    internal static RoadNetwork Cross(out RoadNode node, RoadTypeId? northSouth = null)
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-80, 0, 0), new Vector3(80, 0, 0), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -80), new Vector3(0, 0, 80), northSouth));
        node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        return n;
    }

    [Fact]
    public void BendResolvesToNone()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-50, 0, 0), new Vector3(0, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(0, 0, 40)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 2);
        node.Config = JunctionConfig.Default with { Mode = JunctionControlMode.TrafficLights };
        Assert.Equal(JunctionControlMode.None, JunctionControl.Resolve(node, n.Edges).Mode);
    }

    [Fact]
    public void AutoPicksWiderRoadAsMain()
    {
        // Street (E-W, carriageway half 3.5) crossing TwoLane (N-S, half 4.0)?
        // TwoLane is 8 m wide with no sidewalks: carriageway half 4.0 — so the
        // *TwoLane* pair is the wider carriageway here; assert against catalog data
        var n = Cross(out var node);
        var eff = JunctionControl.Resolve(node, n.Edges);
        Assert.Equal(JunctionControlMode.PrioritySigns, eff.Mode);

        float streetHalf = RoadCatalog.Street.CarriagewayHalf;
        float twoLaneHalf = RoadCatalog.TwoLane.CarriagewayHalf;
        var widerType = twoLaneHalf > streetHalf ? RoadCatalog.TwoLane.Id : RoadCatalog.Street.Id;
        foreach (var eid in node.Edges)
        {
            var isWider = n.Edges[eid].Type == widerType;
            Assert.Equal(isWider ? LegRole.Main : LegRole.Yield, eff.Roles[eid]);
        }
    }

    [Fact]
    public void AutoPicksStraightPairAsMainInTee()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-80, 0, 0), new Vector3(80, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(0, 0, 80)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 3);
        var eff = JunctionControl.Resolve(node, n.Edges);
        Assert.Equal(2, node.Edges.Count(e => eff.Roles[e] == LegRole.Main));

        // the stub is the leg that runs north-south
        var stub = node.Edges.Single(e =>
            MathF.Abs(n.Edges[e].Curve.P0.Z - n.Edges[e].Curve.P3.Z) > 1f);
        Assert.Equal(LegRole.Yield, eff.Roles[stub]);
    }

    [Fact]
    public void RoleOverrideWins()
    {
        var n = Cross(out var node);
        var eff = JunctionControl.Resolve(node, n.Edges);
        var main = node.Edges.First(e => eff.Roles[e] == LegRole.Main);
        node.Config = JunctionConfig.Default with
        {
            RoleOverrides = new Dictionary<EdgeId, LegRole> { [main] = LegRole.Stop },
        };
        Assert.Equal(LegRole.Stop, JunctionControl.Resolve(node, n.Edges).Roles[main]);
    }

    [Fact]
    public void UnknownEdgeOverrideIsIgnored()
    {
        var n = Cross(out var node);
        node.Config = JunctionConfig.Default with
        {
            RoleOverrides = new Dictionary<EdgeId, LegRole> { [new EdgeId(9999)] = LegRole.Stop },
        };
        var eff = JunctionControl.Resolve(node, n.Edges);
        Assert.Equal(2, node.Edges.Count(e => eff.Roles[e] == LegRole.Main));
        Assert.DoesNotContain(new EdgeId(9999), eff.Roles.Keys);
    }

    [Fact]
    public void ExplicitModesApplyAtJunctions()
    {
        var n = Cross(out var node);
        node.Config = JunctionConfig.Default with { Mode = JunctionControlMode.AllWayStop };
        Assert.Equal(JunctionControlMode.AllWayStop, JunctionControl.Resolve(node, n.Edges).Mode);

        node.Config = JunctionConfig.Default with { Mode = JunctionControlMode.None };
        Assert.Equal(JunctionControlMode.None, JunctionControl.Resolve(node, n.Edges).Mode);
    }
}
