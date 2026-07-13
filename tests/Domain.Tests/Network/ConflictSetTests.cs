using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class ConflictSetTests
{
    private static (RoadNetwork n, RoadNode node) Cross()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-80, 0, 0), new Vector3(80, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -80), new Vector3(0, 0, 80)));
        return (n, n.Nodes.Values.Single(x => x.Edges.Count == 4));
    }

    private static int FindConnector(RoadNetwork n, RoadNode node, Func<Vector3, bool> fromSide,
        Func<Vector3, bool> toSide, TurnKind turn)
    {
        var lanes = n.Edges.Values.SelectMany(e => e.Lanes).ToDictionary(l => l.Id, l => l);
        for (int i = 0; i < node.Connectors.Count; i++)
        {
            var c = node.Connectors[i];
            if (c.Turn != turn)
                continue;
            var fromEdge = n.Edges[lanes[c.From].Edge];
            var toEdge = n.Edges[lanes[c.To].Edge];
            if (fromSide(fromEdge.Curve.Point(0.5f)) && toSide(toEdge.Curve.Point(0.5f)))
                return i;
        }
        return -1;
    }

    [Fact]
    public void ConflictsAreSymmetricAndSized()
    {
        var (n, node) = Cross();
        Assert.Equal(node.Connectors.Count, node.ConnectorConflicts.Count);
        for (int i = 0; i < node.Connectors.Count; i++)
        foreach (var j in node.ConnectorConflicts[i])
            Assert.Contains(i, node.ConnectorConflicts[j]);
    }

    [Fact]
    public void CrossingStraightsConflict()
    {
        var (n, node) = Cross();
        int westToEast = FindConnector(n, node, p => p.X < -1, p => p.X > 1, TurnKind.Straight);
        int northToSouth = FindConnector(n, node, p => p.Z < -1, p => p.Z > 1, TurnKind.Straight);
        Assert.True(westToEast >= 0 && northToSouth >= 0);
        Assert.Contains(northToSouth, node.ConnectorConflicts[westToEast]);
    }

    [Fact]
    public void OppositeRightTurnsDoNotConflict()
    {
        var (n, node) = Cross();
        // west→south right turn and east→north right turn stay in opposite quadrants
        int wS = FindConnector(n, node, p => p.X < -1, p => p.Z > 1, TurnKind.Right);
        int eN = FindConnector(n, node, p => p.X > 1, p => p.Z < -1, TurnKind.Right);
        Assert.True(wS >= 0 && eN >= 0);
        Assert.DoesNotContain(eN, node.ConnectorConflicts[wS]);
    }

    [Fact]
    public void SameTargetLaneMergesConflict()
    {
        var (n, node) = Cross();
        var lanes = n.Edges.Values.SelectMany(e => e.Lanes).ToDictionary(l => l.Id, l => l);
        for (int i = 0; i < node.Connectors.Count; i++)
        for (int j = i + 1; j < node.Connectors.Count; j++)
        {
            if (node.Connectors[i].To == node.Connectors[j].To
                && node.Connectors[i].From != node.Connectors[j].From)
                Assert.Contains(j, node.ConnectorConflicts[i]);
        }
    }
}
