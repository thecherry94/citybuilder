using System.Linq;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Persistence;
using CityBuilder.Domain.Tests.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Persistence;

public class SaveLoadTests
{
    [Fact]
    public void RoundTripIsByteStableAndStructurallyIdentical()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), RoadCatalog.Avenue.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.Asymmetric.Id));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.TrafficLights, SizeOffset = 2f });

        string json = SaveLoad.Save(n);
        var loaded = SaveLoad.Load(json);
        Assert.Equal(json, SaveLoad.Save(loaded)); // byte-equal

        Assert.Equal(n.Nodes.Count, loaded.Nodes.Count);
        Assert.Equal(n.Edges.Count, loaded.Edges.Count);
        foreach (var (id, e) in n.Edges)
        {
            var le = loaded.Edges[id];
            Assert.Equal(e.Type, le.Type);
            Assert.Equal(e.Lanes.Select(l => l.Id), le.Lanes.Select(l => l.Id)); // lane ids verbatim
            Assert.Equal(e.Curve.P0, le.Curve.P0);
            Assert.Equal(e.Curve.P3, le.Curve.P3);
        }
        var lnode = loaded.Nodes[node.Id];
        Assert.Equal(JunctionControlMode.TrafficLights, lnode.Config.Mode);
        Assert.Equal(node.Connectors.Count, lnode.Connectors.Count); // derived data rebuilt
    }

    [Fact]
    public void RoundaboutSurvivesByteStableRoundTrip()
    {
        var n = RoundaboutTests.FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 22f).Id!.Value;

        string json = SaveLoad.Save(n);
        var loaded = SaveLoad.Load(json);
        Assert.Equal(json, SaveLoad.Save(loaded)); // byte-stable

        Assert.Single(loaded.Roundabouts);
        Assert.Equal(22f, loaded.Roundabouts[id].Radius);
        Assert.Equal(n.Roundabouts[id].RingNodes.Count, loaded.Roundabouts[id].RingNodes.Count);
        Assert.Contains(loaded.Nodes.Values, x => x.Ring == id);
        Assert.Empty(NetworkInvariants.Check(loaded));

        // and the restored roundabout is still live-editable
        Assert.True(loaded.SetRoundaboutRadius(id, 30f).Success);
        Assert.Empty(NetworkInvariants.Check(loaded));
    }

    [Fact]
    public void CoveredFlagRoundTripsAndStaysByteStable()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        var covered = n.Edges.Keys.OrderBy(id => id.Value).First();
        Assert.True(n.SetCovered(covered, true));

        string json = SaveLoad.Save(n);
        var loaded = SaveLoad.Load(json);
        Assert.Equal(json, SaveLoad.Save(loaded)); // byte-stable at v3
        Assert.True(loaded.Edges[covered].Covered);
        Assert.All(loaded.Edges.Values.Where(e => e.Id != covered),
            e => Assert.False(e.Covered));
    }

    [Fact]
    public void V2SaveWithoutCoveredLoadsAsUncovered()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        // regress a fresh v3 save to a v2 payload: strip the Covered property and
        // rewrite the version — exactly what a real v2 file looks like
        string v2 = SaveLoad.Save(n)
            .Replace("\"FormatVersion\":3", "\"FormatVersion\":2")
            .Replace(",\"Covered\":false", "");
        Assert.DoesNotContain("Covered", v2);
        var loaded = SaveLoad.Load(v2);
        Assert.All(loaded.Edges.Values, e => Assert.False(e.Covered));
    }

    [Fact]
    public void FormatV1SaveWithoutRoundaboutsStillLoads()
    {
        const string v1 = "{\"FormatVersion\":1,\"Nodes\":[],\"Edges\":[],\"NextNode\":1,\"NextEdge\":1,\"NextLane\":1}";
        var n = SaveLoad.Load(v1);
        Assert.Empty(n.Roundabouts);
        Assert.Empty(n.Nodes);
    }

    [Fact]
    public void CorruptRoundaboutDtoIsRejected()
    {
        var n = RoundaboutTests.FourWayJunction(out var center);
        n.ConvertToRoundabout(center, 22f);
        // point a ring node id at a non-existent node
        string json = SaveLoad.Save(n).Replace("\"RingNodeIds\":[", "\"RingNodeIds\":[99999,");
        Assert.Throws<SaveFormatException>(() => SaveLoad.Load(json));
    }

    private static (string Json, int FirstRingNode, int FirstLegEdge) SavedRoundabout()
    {
        var n = RoundaboutTests.FourWayJunction(out var center);
        var id = n.ConvertToRoundabout(center, 22f).Id!.Value;
        return (SaveLoad.Save(n), n.Roundabouts[id].RingNodes[0].Value,
            n.Roundabouts[id].LegFullCurves.Keys.Min(k => k.Value));
    }

    [Fact]
    public void DuplicateLegCurveKeysAreRejectedAsSaveFormat()
    {
        // duplicate LegCurveDto.Edge keys must throw the TYPED exception from
        // ValidateGame — never ArgumentException from ToDictionary mid-restore,
        // after the old network was already torn down
        var (json, _, legEdge) = SavedRoundabout();
        string dup = json.Replace($"\"LegCurves\":[{{\"Edge\":{legEdge},",
            $"\"LegCurves\":[{{\"Edge\":{legEdge},\"Curve\":[0,0,0,0,0,0,0,0,0,0,0,0]}},{{\"Edge\":{legEdge},");
        Assert.Throws<SaveFormatException>(() => SaveLoad.Load(dup));
    }

    [Fact]
    public void DuplicateRingNodeIdsAreRejected()
    {
        // same-length substitution (second ring node replaced by the first) — must be
        // caught by a uniqueness check, not merely the node/edge count comparison
        var (json, _, _) = SavedRoundabout();
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"RingNodeIds\":\\[(\\d+),(\\d+)");
        Assert.True(m.Success);
        string dup = json.Replace(m.Value, $"\"RingNodeIds\":[{m.Groups[1].Value},{m.Groups[1].Value}");
        Assert.Throws<SaveFormatException>(() => SaveLoad.Load(dup));
    }

    [Fact]
    public void RingNodeSharedAcrossRoundaboutsIsRejected()
    {
        // two junctions far apart, both converted; then corrupt the second roundabout
        // to also claim the first one's ring node — last-write-wins tagging would load
        // this into an invariant-violating network, so ValidateGame must refuse it
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-60, 0, 0), new(60, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -60), new(0, 0, 60)));
        Net.Commit(n, Net.Straight(new(440, 0, 0), new(560, 0, 0)));
        Net.Commit(n, Net.Straight(new(500, 0, -60), new(500, 0, 60)));
        var c1 = n.Nodes.Values.Single(x => System.Numerics.Vector3.Distance(x.Position, new(0, 0, 0)) < 0.1f);
        var c2 = n.Nodes.Values.Single(x => System.Numerics.Vector3.Distance(x.Position, new(500, 0, 0)) < 0.1f);
        var id1 = n.ConvertToRoundabout(c1.Id, 20f).Id!.Value;
        var id2 = n.ConvertToRoundabout(c2.Id, 20f).Id!.Value;
        int stolen = n.Roundabouts[id1].RingNodes[0].Value;
        int victim = n.Roundabouts[id2].RingNodes[0].Value;
        string json = SaveLoad.Save(n)
            .Replace($"\"RingNodeIds\":[{victim},", $"\"RingNodeIds\":[{stolen},");
        Assert.Throws<SaveFormatException>(() => SaveLoad.Load(json));
    }

    [Fact]
    public void LoadIntoReplacesInPlaceWithOneChangedEvent()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        string snapshot = SaveLoad.Save(n);
        Net.Commit(n, Net.Straight(new(0, 0, 50), new(100, 0, 50)));
        int events = 0;
        n.Changed += _ => events++;
        SaveLoad.LoadInto(snapshot, n);
        Assert.Equal(1, events);
        Assert.Single(n.Edges);
        Assert.Equal(snapshot, SaveLoad.Save(n));
    }

    [Fact]
    public void NewerFormatVersionThrows()
    {
        var n = Net.New();
        string json = SaveLoad.Save(n)
            .Replace($"\"FormatVersion\":{SaveLoad.FormatVersion}", "\"FormatVersion\":99");
        Assert.Throws<SaveFormatException>(() => SaveLoad.Load(json));
    }

    // --- malformed-but-parseable payloads must throw the TYPED exception, never NRE ---

    [Fact]
    public void MissingNodesAndEdgesThrowsSaveFormatException()
    {
        Assert.Throws<SaveFormatException>(() => SaveLoad.Load("{\"FormatVersion\":1}"));
    }

    [Fact]
    public void NullNodeConfigThrowsSaveFormatException()
    {
        const string json = "{\"FormatVersion\":1,\"Nodes\":[{\"Id\":1,\"X\":0,\"Y\":0,\"Z\":0,\"Config\":null}],"
            + "\"Edges\":[],\"NextNode\":2,\"NextEdge\":1,\"NextLane\":1}";
        Assert.Throws<SaveFormatException>(() => SaveLoad.Load(json));
    }

    [Fact]
    public void NullLaneIdsThrowsAndLeavesTargetNetworkUntouched()
    {
        const string json = "{\"FormatVersion\":1,\"Nodes\":["
            + "{\"Id\":1,\"X\":0,\"Y\":0,\"Z\":0,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}},"
            + "{\"Id\":2,\"X\":100,\"Y\":0,\"Z\":0,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}],"
            + "\"Edges\":[{\"Id\":1,\"Start\":1,\"End\":2,\"Type\":1,"
            + "\"Curve\":[0,0,0,33,0,0,66,0,0,100,0,0],\"LaneIds\":null}],"
            + "\"NextNode\":3,\"NextEdge\":2,\"NextLane\":3}";

        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 50), new(100, 0, 50)));
        string before = SaveLoad.Save(n);
        int events = 0;
        n.Changed += _ => events++;

        Assert.Throws<SaveFormatException>(() => SaveLoad.LoadInto(json, n));

        // failed load must not half-clear the target: no event, identical state
        Assert.Equal(0, events);
        Assert.Equal(before, SaveLoad.Save(n));
    }

    [Fact]
    public void ZeroCountersThrowSaveFormatException()
    {
        // Counters absent from a hand-edited save deserialize to 0. Accepting that would
        // let a subsequent mint hand out NodeId(0)/EdgeId(0)/LaneId(0), which is rejected
        // on load (ids must be > 0) — a self-poisoning save that can never load again.
        const string json = "{\"FormatVersion\":1,\"Nodes\":[],\"Edges\":[]}";
        Assert.Throws<SaveFormatException>(() => SaveLoad.Load(json));
    }

    [Fact]
    public void LoopEdgeThrowsSaveFormatException()
    {
        // Start == End can never occur organically (Validate/Commit forbid it), but a
        // hand-edited save could contain one; it must be rejected here rather than
        // crashing TryHealNode/AddEdgeInternal mid-batch with a KeyNotFoundException.
        const string json = "{\"FormatVersion\":1,\"Nodes\":["
            + "{\"Id\":1,\"X\":0,\"Y\":0,\"Z\":0,\"Config\":{\"Mode\":0,\"SizeOffset\":0,\"Roles\":[],\"LegOffsets\":[]}}],"
            + "\"Edges\":[{\"Id\":1,\"Start\":1,\"End\":1,\"Type\":1,"
            + "\"Curve\":[0,0,0,33,0,0,66,0,0,100,0,0],\"LaneIds\":[1,2]}],"
            + "\"NextNode\":2,\"NextEdge\":2,\"NextLane\":3}";
        Assert.Throws<SaveFormatException>(() => SaveLoad.Load(json));
    }
}
