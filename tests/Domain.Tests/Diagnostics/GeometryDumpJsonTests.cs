using System.Numerics;
using System.Text.Json;
using CityBuilder.Domain.Diagnostics;
using CityBuilder.Domain.Tests.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Diagnostics;

public class GeometryDumpJsonTests
{
    [Fact]
    public void JsonRoundTripsCountsAndCoordinates()
    {
        var n = Net.New();
        // 4-way cross + a detached climbing edge (10 % on a 60 m run, under every cap)
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        Net.Commit(n, Net.Straight(new(200, 0, 0), new(260, 6, 0)));

        using var doc = JsonDocument.Parse(GeometryDump.Json(n));
        var root = doc.RootElement;

        Assert.Equal(n.Nodes.Count, root.GetProperty("nodes").GetArrayLength());
        Assert.Equal(n.Edges.Count, root.GetProperty("edges").GetArrayLength());
        Assert.Equal(0, root.GetProperty("roundabouts").GetArrayLength());
        Assert.Equal(n.Version, root.GetProperty("version").GetInt32());

        // every edge polyline starts at P0 and ends at P3 (2-decimal rounding)
        foreach (var e in root.GetProperty("edges").EnumerateArray())
        {
            var id = e.GetProperty("id").GetInt32();
            var curve = n.Edges[new(id)].Curve;
            var poly = e.GetProperty("polyline");
            AssertPoint(curve.P0, poly[0]);
            AssertPoint(curve.P3, poly[poly.GetArrayLength() - 1]);
            Assert.True(e.GetProperty("lanes").GetArrayLength() > 0);
            Assert.False(e.GetProperty("covered").GetBoolean());
        }

        // node positions survive
        foreach (var nd in root.GetProperty("nodes").EnumerateArray())
        {
            var id = nd.GetProperty("id").GetInt32();
            AssertPoint(n.Nodes[new(id)].Position, nd.GetProperty("position"));
        }
    }

    [Fact]
    public void JsonListsRoundaboutMembership()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-80, 0, 0), new(80, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -80), new(0, 0, 80)));
        var center = n.Nodes.Values.Single(nd => nd.Position.Length() < 1f);
        var res = n.ConvertToRoundabout(center.Id, 16f);
        Assert.True(res.Success, res.Error?.ToString());

        using var doc = JsonDocument.Parse(GeometryDump.Json(n));
        var rb = Assert.Single(doc.RootElement.GetProperty("roundabouts").EnumerateArray());
        Assert.Equal(16f, rb.GetProperty("radius").GetSingle(), 2);
        Assert.True(rb.GetProperty("ringEdges").GetArrayLength() >= 4);
    }

    private static void AssertPoint(Vector3 expected, JsonElement actual)
    {
        Assert.Equal(expected.X, actual[0].GetSingle(), 1);
        Assert.Equal(expected.Y, actual[1].GetSingle(), 1);
        Assert.Equal(expected.Z, actual[2].GetSingle(), 1);
    }
}
