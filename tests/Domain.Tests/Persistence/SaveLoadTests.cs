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
        string json = SaveLoad.Save(n).Replace("\"FormatVersion\":2", "\"FormatVersion\":99");
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
