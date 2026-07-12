using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class CornerZoneTests
{
    private static RoadNode Cross(RoadTypeId a, RoadTypeId b, out RoadNetwork n)
    {
        n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), a));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), b));
        return n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
    }

    [Fact]
    public void RuralJunctionHasNoCornerZones()
    {
        var center = Cross(RoadCatalog.TwoLane.Id, RoadCatalog.TwoLane.Id, out _);
        Assert.Empty(center.Junction.Corners);
        Assert.DoesNotContain(JunctionSegmentKind.Curbed, center.Junction.SegmentKinds);
    }

    [Fact]
    public void StreetCrossHasFourRaisedCorners()
    {
        var center = Cross(RoadCatalog.Street.Id, RoadCatalog.Street.Id, out _);
        Assert.Equal(4, center.Junction.Corners.Count);
        foreach (var zone in center.Junction.Corners)
        {
            Assert.True(zone.Polygon.Count >= 4);
            Assert.InRange(zone.InnerCount, 2, zone.Polygon.Count - 2);
        }
        Assert.Contains(JunctionSegmentKind.Curbed, center.Junction.SegmentKinds);
    }

    [Fact]
    public void AsphaltPolygonSpansCarriagewayNotFullWidth()
    {
        var center = Cross(RoadCatalog.Street.Id, RoadCatalog.Street.Id, out _);
        float cw = RoadCatalog.Street.CarriagewayHalf;
        // cut cross-section points must sit at carriageway width from the axes
        var poly = center.Junction.SurfacePolygon;
        var kinds = center.Junction.SegmentKinds;
        for (int i = 0; i < poly.Count; i++)
        {
            if (kinds[i] != JunctionSegmentKind.Cut)
                continue;
            float lateral = MathF.Min(MathF.Abs(poly[i].X), MathF.Abs(poly[i].Z));
            Assert.Equal(cw, lateral, 1);
        }
    }

    [Fact]
    public void MixedRuralUrbanCrossStillProducesZones()
    {
        var center = Cross(RoadCatalog.TwoLane.Id, RoadCatalog.Avenue.Id, out _);
        // avenue sides carry sidewalk bands, so wedges are non-degenerate
        Assert.Equal(4, center.Junction.Corners.Count);
    }

    [Fact]
    public void ShortEdgeBetweenJunctionsIsMarkedTight()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -60), new(0, 0, 60), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new(12, 0, -60), new(12, 0, 60), RoadCatalog.Street.Id));

        // the 12 m horizontal piece between the two crossings cannot host two full
        // junction cuts: both end junctions must flag it
        var shortEdge = n.Edges.Values.Single(e =>
            e.ArcLength.TotalLength < 15 && MathF.Abs(e.Curve.Point(0.5f).Z) < 0.5f);
        foreach (var nodeId in new[] { shortEdge.StartNode, shortEdge.EndNode })
            Assert.Contains(shortEdge.Id, n.Nodes[nodeId].Junction.TightCuts);

        // the long approaches stay unflagged
        var longEdge = n.Edges.Values.First(e => e.ArcLength.TotalLength > 50);
        Assert.All(new[] { longEdge.StartNode, longEdge.EndNode },
            id => Assert.DoesNotContain(longEdge.Id, n.Nodes[id].Junction.TightCuts));
    }

    [Fact]
    public void SegmentKindsAlignWithPolygon()
    {
        foreach (var typeA in new[] { RoadCatalog.TwoLane.Id, RoadCatalog.Street.Id, RoadCatalog.Avenue.Id })
        {
            var center = Cross(typeA, RoadCatalog.Street.Id, out _);
            Assert.Equal(center.Junction.SurfacePolygon.Count, center.Junction.SegmentKinds.Count);
            Assert.Equal(4, center.Junction.SegmentKinds.Count(k => k == JunctionSegmentKind.Cut));
        }
    }
}
