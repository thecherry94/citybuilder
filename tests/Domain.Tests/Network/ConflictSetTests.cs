using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
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
        foreach (var cp in node.ConnectorConflicts[i])
            Assert.Contains(i, node.ConnectorConflicts[cp.Other].Select(c => c.Other));
    }

    [Fact]
    public void CrossingStraightsConflict()
    {
        var (n, node) = Cross();
        int westToEast = FindConnector(n, node, p => p.X < -1, p => p.X > 1, TurnKind.Straight);
        int northToSouth = FindConnector(n, node, p => p.Z < -1, p => p.Z > 1, TurnKind.Straight);
        Assert.True(westToEast >= 0 && northToSouth >= 0);
        Assert.Contains(northToSouth, node.ConnectorConflicts[westToEast].Select(c => c.Other));
    }

    [Fact]
    public void OppositeRightTurnsDoNotConflict()
    {
        var (n, node) = Cross();
        // west→south right turn and east→north right turn stay in opposite quadrants
        int wS = FindConnector(n, node, p => p.X < -1, p => p.Z > 1, TurnKind.Right);
        int eN = FindConnector(n, node, p => p.X > 1, p => p.Z < -1, TurnKind.Right);
        Assert.True(wS >= 0 && eN >= 0);
        Assert.DoesNotContain(eN, node.ConnectorConflicts[wS].Select(c => c.Other));
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
                Assert.Contains(j, node.ConnectorConflicts[i].Select(c => c.Other));
        }
    }

    [Fact]
    public void ConflictPointsAreSymmetricAndOnBothCurves()
    {
        var (n, node) = Cross();
        for (int i = 0; i < node.Connectors.Count; i++)
        foreach (var cp in node.ConnectorConflicts[i])
        {
            var mirror = node.ConnectorConflicts[cp.Other].Single(c => c.Other == i);
            Assert.Equal(cp.SMine, mirror.STheirs, 2);
            Assert.Equal(cp.STheirs, mirror.SMine, 2);
            // the stored point lies on my curve at the stored arc distance
            var myCurve = node.Connectors[i].Curve;
            var table = new ArcLengthTable(myCurve, 24);
            Assert.InRange(cp.SMine, -0.01f, table.TotalLength + 0.01f);
            if (node.Connectors[i].To != node.Connectors[cp.Other].To) // crossing, not merge
            {
                var mine = myCurve.Point(table.TAtDistance(cp.SMine));
                var theirCurve = node.Connectors[cp.Other].Curve;
                var theirTable = new ArcLengthTable(theirCurve, 24);
                var theirs = theirCurve.Point(theirTable.TAtDistance(cp.STheirs));
                Assert.True(Vector3.Distance(mine, theirs) < 0.6f,
                    $"conflict point mismatch: {mine} vs {theirs}");
            }
        }
    }

    [Fact]
    public void MergeConflictsUseCurveEnds()
    {
        var (n, node) = Cross();
        for (int i = 0; i < node.Connectors.Count; i++)
        foreach (var cp in node.ConnectorConflicts[i])
        {
            if (node.Connectors[i].To != node.Connectors[cp.Other].To)
                continue;
            Assert.Equal(new ArcLengthTable(node.Connectors[i].Curve, 24).TotalLength, cp.SMine, 1);
        }
    }
}
