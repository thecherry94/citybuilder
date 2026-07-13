using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class JunctionResizeTests
{
    private static float CutDist(RoadNetwork n, RoadNode node, EdgeId eid)
    {
        var e = n.Edges[eid];
        return Vector3.Distance(e.Curve.Point(node.Junction.CutT[eid]), node.Position);
    }

    [Fact]
    public void SizeOffsetGrowsAllCuts()
    {
        var n = JunctionControlTests.Cross(out var node);
        var before = node.Edges.ToDictionary(e => e, e => CutDist(n, node, e));

        n.ConfigureJunction(node.Id, node.Config with { SizeOffset = 4f });

        foreach (var eid in node.Edges)
            Assert.Equal(before[eid] + 4f, CutDist(n, node, eid), 1);
    }

    [Fact]
    public void ShrinkFloorsAtSolvedCorner()
    {
        var n = JunctionControlTests.Cross(out var node);
        var before = node.Edges.ToDictionary(e => e, e => CutDist(n, node, e));

        n.ConfigureJunction(node.Id, node.Config with { SizeOffset = -10f });

        foreach (var eid in node.Edges)
        {
            float d = CutDist(n, node, eid);
            // clamp eats at most the corner margin (0.5), minus a small safety epsilon
            Assert.True(d >= before[eid] - 0.5f && d < before[eid],
                $"cut {d:F2} not within margin below {before[eid]:F2}");
        }
    }

    [Fact]
    public void PerLegOffsetMovesOnlyThatLeg()
    {
        var n = JunctionControlTests.Cross(out var node);
        var before = node.Edges.ToDictionary(e => e, e => CutDist(n, node, e));
        var grown = node.Edges.First();

        n.ConfigureJunction(node.Id, node.Config with
        {
            LegOffsets = new Dictionary<EdgeId, float> { [grown] = 6f },
        });

        foreach (var eid in node.Edges)
            Assert.Equal(before[eid] + (eid == grown ? 6f : 0f), CutDist(n, node, eid), 1);
    }

    [Fact]
    public void ThirtyPercentClampStillWinsAndFlagsTight()
    {
        // 20 m legs: 30% = 6 m < solved corner (6) + margin + offset
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-20, 0, 0), new Vector3(20, 0, 0), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -20), new Vector3(0, 0, 20), RoadCatalog.Street.Id));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);

        n.ConfigureJunction(node.Id, node.Config with { SizeOffset = 8f });

        foreach (var eid in node.Edges)
        {
            float len = n.Edges[eid].ArcLength.TotalLength;
            Assert.True(CutDist(n, node, eid) <= len * 0.3f + 0.01f);
            Assert.Contains(eid, node.Junction.TightCuts);
        }
    }
}
