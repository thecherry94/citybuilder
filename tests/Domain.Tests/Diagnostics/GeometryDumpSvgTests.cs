using System.Globalization;
using System.Xml.Linq;
using CityBuilder.Domain.Diagnostics;
using CityBuilder.Domain.Tests.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Diagnostics;

public class GeometryDumpSvgTests
{
    private static CityBuilder.Domain.Network.RoadNetwork CrossWithBridge()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        Net.Commit(n, Net.Straight(new(200, 0, 0), new(260, 6, 0)));
        return n;
    }

    [Fact]
    public void SvgIsWellFormedWithAllLayers()
    {
        var svg = GeometryDump.Svg(CrossWithBridge());
        var doc = XDocument.Parse(svg); // throws on malformed XML
        var ns = doc.Root!.Name.Namespace;
        var groupIds = doc.Root.Elements(ns + "g").Select(g => (string?)g.Attribute("id")).ToList();
        Assert.Equal(new[] { "edges", "junctions", "lanes", "conflicts", "labels" }, groupIds);
        Assert.Contains("svg x = world X", svg);
    }

    [Fact]
    public void SvgLabelsElevationAndIds()
    {
        var n = CrossWithBridge();
        var svg = GeometryDump.Svg(n);
        Assert.Contains("y=3", svg); // climbing edge midpoint sits at Y=3
        var firstNode = n.Nodes.Keys.Min(k => k.Value);
        var firstEdge = n.Edges.Keys.Min(k => k.Value);
        Assert.Contains($">n{firstNode}<", svg);
        Assert.Contains($"e{firstEdge} {CityBuilder.Domain.Catalog.RoadCatalog.TwoLane.Name}", svg);
    }

    [Fact]
    public void SvgConflictLayerPopulatedAtAJunction()
    {
        var svg = GeometryDump.Svg(CrossWithBridge());
        var doc = XDocument.Parse(svg);
        var ns = doc.Root!.Name.Namespace;
        var conflicts = doc.Root.Elements(ns + "g").Single(g => (string?)g.Attribute("id") == "conflicts");
        Assert.True(conflicts.Elements(ns + "circle").Any(), "4-way cross must have conflict points");
    }

    [Fact]
    public void SvgIsCultureInvariant()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var svg = GeometryDump.Svg(CrossWithBridge());
            var doc = XDocument.Parse(svg);
            var viewBox = ((string)doc.Root!.Attribute("viewBox")!).Split(' ');
            Assert.Equal(4, viewBox.Length);
            foreach (var part in viewBox)
                Assert.True(float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                    $"viewBox token '{part}' is not invariant-culture parseable");
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }
}
