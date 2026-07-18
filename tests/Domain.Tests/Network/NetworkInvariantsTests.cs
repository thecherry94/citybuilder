using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class NetworkInvariantsTests
{
    [Fact]
    public void ConvertedRoundaboutHasNoViolations()
    {
        var n = RoundaboutTests.FourWayJunction(out var center);
        n.ConvertToRoundabout(center, 20f);
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void CorruptedRingEdgeTypeIsFlagged()
    {
        // Ring edges are locked against RetypeEdge, so corrupt one via a hand-edited save
        // (Street and OneWay share a 4-lane profile, so the load stays structurally valid).
        var n = RoundaboutTests.FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 20f).Id!.Value;
        var ringEdge = n.Roundabouts[id].RingEdges[0].Value;

        var game = System.Text.Json.JsonSerializer.Deserialize<CityBuilder.Domain.Persistence.SaveGame>(
            CityBuilder.Domain.Persistence.SaveLoad.Save(n))!;
        var edges = game.Edges
            .Select(e => e.Id == ringEdge ? e with { Type = RoadCatalog.Street.Id.Value } : e)
            .ToArray();
        var corrupt = System.Text.Json.JsonSerializer.Serialize(game with { Edges = edges });

        var loaded = CityBuilder.Domain.Persistence.SaveLoad.Load(corrupt);
        Assert.Contains(NetworkInvariants.Check(loaded), v => v.Contains("not OneWay"));
    }

    [Fact]
    public void DisjointCrossingEdgesAreFlagged()
    {
        // Two straight TwoLane edges crossing at the origin with NO shared node — never
        // constructible via Validate/Commit (which splits crossings), but reachable through
        // raw surgery or a corrupt save. ValidateGame doesn't do geometry, so the load
        // succeeds; the invariant checker must be the layer that sees it.
        const string corrupt = "{\"FormatVersion\":2," +
            "\"Nodes\":[" +
            "{\"Id\":1,\"X\":-50,\"Y\":0,\"Z\":0,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}," +
            "{\"Id\":2,\"X\":50,\"Y\":0,\"Z\":0,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}," +
            "{\"Id\":3,\"X\":0,\"Y\":0,\"Z\":-50,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}," +
            "{\"Id\":4,\"X\":0,\"Y\":0,\"Z\":50,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}]," +
            "\"Edges\":[" +
            "{\"Id\":1,\"Start\":1,\"End\":2,\"Type\":1,\"Curve\":[-50,0,0,-16.6667,0,0,16.6667,0,0,50,0,0],\"LaneIds\":[1,2]}," +
            "{\"Id\":2,\"Start\":3,\"End\":4,\"Type\":1,\"Curve\":[0,0,-50,0,0,-16.6667,0,0,16.6667,0,0,50],\"LaneIds\":[3,4]}]," +
            "\"NextNode\":5,\"NextEdge\":3,\"NextLane\":5}";
        var n = CityBuilder.Domain.Persistence.SaveLoad.Load(corrupt);
        Assert.Contains(NetworkInvariants.Check(n), v => v.Contains("without a shared node"));
    }

    [Fact]
    public void EdgesMeetingAtASharedNodeAreNotFlaggedAsCrossing()
    {
        // proper commits split crossings into shared nodes — the crossing rule must not
        // fire on the resulting junction geometry
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        Assert.DoesNotContain(NetworkInvariants.Check(n), v => v.Contains("without a shared node"));
    }

    [Fact]
    public void HealthyMixedNetworkHasNoViolations()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(500, 0, 0), RoadCatalog.FourLane.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.TwoLane.Id));
        Net.Commit(n, Net.Straight(new(200, 0, -100), new(200, 0, 100), RoadCatalog.Asymmetric.Id));
        Net.Commit(n, Net.Straight(new(400, 0, -100), new(400, 0, 100), RoadCatalog.OneWay.Id));
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void LegAngleRuleFlagsSharpPairs()
    {
        var o = new List<string>();
        NetworkInvariants.CheckLegAngles(new NodeId(1), new[]
        {
            new Vector3(1, 0, 0),
            Vector3.Normalize(new Vector3(1, 0, 0.2f)), // ~11 deg apart
        }, o);
        Assert.NotEmpty(o);
        o.Clear();
        NetworkInvariants.CheckLegAngles(new NodeId(1), new[]
        {
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 0, 1),
        }, o);
        Assert.Empty(o);
    }

    [Fact]
    public void EdgeGeometryRuleFlagsShortAndTight()
    {
        var o = new List<string>();
        var shortEdge = new RoadEdge(new EdgeId(1), new NodeId(1), new NodeId(2),
            Bezier3.Line(new(0, 0, 0), new(3, 0, 0)), RoadCatalog.TwoLane.Id);
        NetworkInvariants.CheckEdgeGeometry(shortEdge, RoadCatalog.TwoLane, o);
        Assert.NotEmpty(o);
    }

    /// <summary>The standing guard behind the M5 arrow bug: whatever the mix of road
    /// types, an approach never sends more straight connectors into an arm than that
    /// arm has receiving driving lanes, and no arriving driving lane is left with
    /// zero movements. Lifted from LaneConnectorTests so the checker and the
    /// regression test share one source of truth.</summary>
    [Fact]
    public void StraightCapacityInvariantAcrossMixedTypes()
    {
        var n = Net.New();
        // one long FourLane spine crossed by every catalog type
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(500, 0, 0), RoadCatalog.FourLane.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.TwoLane.Id));
        Net.Commit(n, Net.Straight(new(100, 0, -100), new(100, 0, 100), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new(200, 0, -100), new(200, 0, 100), RoadCatalog.Avenue.Id));
        Net.Commit(n, Net.Straight(new(300, 0, -100), new(300, 0, 100), RoadCatalog.OneWay.Id));
        Net.Commit(n, Net.Straight(new(400, 0, -100), new(400, 0, 100), RoadCatalog.Asymmetric.Id));

        Assert.Empty(NetworkInvariants.Check(n));
    }
}
