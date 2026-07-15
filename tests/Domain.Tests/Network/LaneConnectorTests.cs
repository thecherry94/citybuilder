using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class LaneConnectorTests
{
    private static (RoadNetwork n, RoadNode center) FourWay()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        var center = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        return (n, center);
    }

    /// <summary>4-way cross where ONLY the south leg is a 2+1 Asymmetric road, drawn
    /// toward the node so its two Forward lanes arrive there; the straight
    /// continuation (north leg) and the east–west road are Two-Lane. This is the
    /// neck-down from the M5 arrow report: two lanes arrive, one lane receives.</summary>
    private static (RoadNetwork n, RoadNode center, EdgeId southArm) BuildCrossWithAsymmetricSouthArm()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, 100), new(0, 0, 0), RoadCatalog.Asymmetric.Id));
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(0, 0, -100)));
        var center = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        var southArm = n.Edges.Values
            .Single(e => e.Type == RoadCatalog.Asymmetric.Id && e.EndNode == center.Id).Id;
        return (n, center, southArm);
    }

    /// <summary>True when connector `c` originates from the driving lane of `edge`
    /// at the given (signed) centerline offset.</summary>
    private static bool IsFromLane(RoadNetwork n, LaneConnector c, EdgeId edge, float offset)
    {
        var lane = n.Edges.Values.SelectMany(e => e.Lanes).Single(l => l.Id == c.From);
        return lane.Edge == edge && MathF.Abs(lane.Offset - offset) < 0.01f;
    }

    [Fact]
    public void FourWayTwoLaneHasTwelveConnectors()
    {
        var (_, center) = FourWay();
        // 4 incoming lanes × 3 outgoing on other edges
        Assert.Equal(12, center.Connectors.Count);
    }

    [Fact]
    public void NoUTurnConnectors()
    {
        var (n, center) = FourWay();
        var laneToEdge = n.Edges.Values.SelectMany(e => e.Lanes).ToDictionary(l => l.Id, l => l.Edge);
        Assert.All(center.Connectors, c => Assert.NotEqual(laneToEdge[c.From], laneToEdge[c.To]));
    }

    [Fact]
    public void ConnectorEndpointsSitOnLaneCutPoints()
    {
        var (n, center) = FourWay();
        foreach (var c in center.Connectors)
        {
            var fromLane = n.Edges.Values.SelectMany(e => e.Lanes).Single(l => l.Id == c.From);
            var edge = n.Edges[fromLane.Edge];
            float tCut = center.Junction.CutT[edge.Id];
            var expected = edge.Curve.OffsetPoint(tCut, fromLane.Offset);
            Assert.True(Vector3.Distance(c.Curve.Point(0), expected) < 1e-3f);
        }
    }

    [Fact]
    public void DeadEndNodeAllowsUTurn()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var edge = n.Edges[r.CreatedEdges[0]];
        // the backward lane arrives at the start node and may turn into the forward lane
        var c = Assert.Single(n.Nodes[edge.StartNode].Connectors);
        Assert.NotEqual(c.From, c.To);
    }

    [Fact]
    public void GridNetworkLaneGraphIsStronglyConnected()
    {
        var n = Net.New();
        for (int i = 0; i <= 2; i++)
        {
            Net.Commit(n, Net.Straight(new(0, 0, i * 100), new(200, 0, i * 100)));
            Net.Commit(n, Net.Straight(new(i * 100, 0, 0), new(i * 100, 0, 200)));
        }
        Assert.True(n.Edges.Count >= 12, $"grid built {n.Edges.Count} edges");
        Assert.True(LaneGraph.IsStronglyConnected(n));
    }

    [Fact]
    public void FourLaneCrossFourLaneConnectorBudget()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), RoadCatalog.FourLane.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.FourLane.Id));
        var center = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        // turn-lane assignment per approach (2 lanes): straights aligned 1:1 (2),
        // lefts only from the leftmost (fan to 2), rights only from the rightmost
        // (fan to 2) → 6 per approach, 24 total
        Assert.Equal(24, center.Connectors.Count);

        // and the assignment itself: no left from the right lane, no right from left
        var lanes = n.Edges.Values.SelectMany(e => e.Lanes).ToDictionary(l => l.Id, l => l);
        foreach (var c in center.Connectors)
        {
            var from = lanes[c.From];
            if (c.Turn == TurnKind.Left)
                Assert.Equal(1.75f, MathF.Abs(from.Offset), 2);
            if (c.Turn == TurnKind.Right)
                Assert.Equal(5.25f, MathF.Abs(from.Offset), 2);
        }
    }

    [Fact]
    public void TurnLaneAssignmentOnAsymmetricApproach()
    {
        // 2+1 approaching a 4-way whose straight continuation is a Two-Lane road
        // (ONE receiving forward lane): straight capacity forces the inner forward
        // lane to become a dedicated left — never two straight arrows into one lane.
        var (n, center, southArm) = BuildCrossWithAsymmetricSouthArm();
        var leftLaneConn = center.Connectors.Where(c => IsFromLane(n, c, southArm, offset: -0.75f)).ToArray();
        var rightLaneConn = center.Connectors.Where(c => IsFromLane(n, c, southArm, offset: +2.75f)).ToArray();
        Assert.All(leftLaneConn, c => Assert.Equal(TurnKind.Left, c.Turn));   // left ONLY
        Assert.NotEmpty(leftLaneConn);
        Assert.Contains(rightLaneConn, c => c.Turn == TurnKind.Straight);
        Assert.Contains(rightLaneConn, c => c.Turn == TurnKind.Right);
        Assert.DoesNotContain(rightLaneConn, c => c.Turn == TurnKind.Left);
    }

    [Fact]
    public void NeckDownWithoutLeftArmDedicatesOuterLaneToRight()
    {
        // T-junction: 2+1 forward lanes arrive; straight continuation is Two-Lane
        // (1 receiving), a right-turn arm exists, no left arm. Surplus straight
        // drops from the RIGHT side this time: outer = right only, inner = straight.
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 100), new(0, 0, 0), RoadCatalog.Asymmetric.Id));
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(0, 0, -100)));
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var center = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        var southArm = n.Edges.Values.Single(e => e.Type == RoadCatalog.Asymmetric.Id).Id;
        var inner = center.Connectors.Where(c => IsFromLane(n, c, southArm, offset: -0.75f)).ToArray();
        var outer = center.Connectors.Where(c => IsFromLane(n, c, southArm, offset: +2.75f)).ToArray();
        Assert.All(inner, c => Assert.Equal(TurnKind.Straight, c.Turn));
        Assert.NotEmpty(inner);
        Assert.All(outer, c => Assert.Equal(TurnKind.Right, c.Turn));
        Assert.NotEmpty(outer);
    }

    [Fact]
    public void EqualCountStraightsAreAlignedNotCrossing()
    {
        // FourLane × FourLane: straights map 1:1 (inner→inner, outer→outer) instead
        // of a 2×2 fan whose diagonal connectors cross inside the junction.
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0), RoadCatalog.FourLane.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100), RoadCatalog.FourLane.Id));
        var center = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        var lanes = n.Edges.Values.SelectMany(e => e.Lanes).ToDictionary(l => l.Id, l => l);

        var straightGroups = center.Connectors
            .Where(c => c.Turn == TurnKind.Straight)
            .GroupBy(c => lanes[c.From].Edge);
        foreach (var group in straightGroups)
        {
            var conns = group.ToArray();
            Assert.Equal(2, conns.Length); // one per lane, not a 2×2 fan
            foreach (var c in conns)
                Assert.Equal(MathF.Abs(lanes[c.From].Offset), MathF.Abs(lanes[c.To].Offset), 2);
            Assert.Empty(BezierOps.Intersections(conns[0].Curve, conns[1].Curve));
        }
    }

    /// <summary>The standing guard behind the M5 arrow bug: whatever the mix of road
    /// types, an approach never sends more straight connectors into an arm than that
    /// arm has receiving driving lanes, and no arriving driving lane is left with
    /// zero movements.</summary>
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

        var laneById = n.Edges.Values.SelectMany(e => e.Lanes).ToDictionary(l => l.Id, l => l);
        foreach (var node in n.Nodes.Values.Where(x => x.Edges.Count >= 3))
        {
            // capacity: per (approach edge, target edge), straight connectors from
            // driving lanes never exceed the target's receiving driving lanes
            foreach (var group in node.Connectors
                .Where(c => c.Turn == TurnKind.Straight && laneById[c.From].Kind == LaneKind.Driving)
                .GroupBy(c => (From: laneById[c.From].Edge, To: laneById[c.To].Edge)))
            {
                int sources = group.Select(c => c.From).Distinct().Count();
                var target = n.Edges[group.Key.To];
                bool leavesAtNode = target.StartNode == node.Id;
                int capacity = target.Lanes.Count(l => l.Kind == LaneKind.Driving
                    && (l.Direction == LaneDirection.Forward) == leavesAtNode);
                Assert.True(sources <= capacity,
                    $"node {node.Id}: {sources} straight source lanes into {capacity} receiving");
            }

            // no dead lanes: every arriving driving lane keeps at least one movement
            var withConnectors = node.Connectors.Select(c => c.From).ToHashSet();
            foreach (var edgeId in node.Edges)
            {
                var edge = n.Edges[edgeId];
                bool startsHere = edge.StartNode == node.Id;
                foreach (var lane in edge.Lanes.Where(l => l.Kind == LaneKind.Driving
                    && ((l.Direction == LaneDirection.Forward) ? !startsHere : startsHere)))
                    Assert.Contains(lane.Id, withConnectors);
            }
        }
    }
}
