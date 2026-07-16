using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

/// <summary>Curvature-based turn speeds: v = sqrt(LateralComfort * Rmin) replacing the
/// old fixed per-turn-kind connector speeds. Straights are unaffected (priority
/// traffic doesn't brake for junctions); turns and U-turns now scale with the actual
/// geometry of the connector curve.</summary>
public class TurnSpeedTests
{
    /// <summary>Small, tight 4-way cross (narrow Street-type arms, perpendicular
    /// approaches close to the node): every turn connector sweeps a full 90 deg over
    /// a short reach, so its minimum radius of curvature is small.</summary>
    private static (RoadNetwork n, RoadNode center) TightCross()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-30, 0, 0), new(30, 0, 0), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new(0, 0, -30), new(0, 0, 30), RoadCatalog.Street.Id));
        var center = n.Nodes.Values.Single(node => Vector3.Distance(node.Position, Vector3.Zero) < 0.1f);
        return (n, center);
    }

    /// <summary>Wide, shallow-angle Y: FourLane roads (wide junction footprint, so
    /// connectors are long) with a branch departing at only 35 deg from the through
    /// direction — a gentle, sweeping turn whose minimum radius (~12-18 m) sits well
    /// above the tight cross's corner radii.</summary>
    private static (RoadNetwork n, RoadNode center) SweepingY()
    {
        var n = Net.New();
        var west = Net.Commit(n, Net.Straight(new(-300, 0, 0), new(0, 0, 0), RoadCatalog.FourLane.Id));
        var hub = n.Edges[west.CreatedEdges[0]].EndNode;
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(300, 0, 0), RoadCatalog.FourLane.Id,
            start: new EndpointBinding.AtNode(hub)));
        float rad = 35f * MathF.PI / 180f;
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(300 * MathF.Cos(rad), 0, 300 * MathF.Sin(rad)),
            RoadCatalog.FourLane.Id, start: new EndpointBinding.AtNode(hub)));
        var center = n.Nodes[hub];
        return (n, center);
    }

    private static (RoadNetwork n, RoadNode center, int index) FindRightConnector(
        (RoadNetwork n, RoadNode center) fixture)
    {
        var (n, center) = fixture;
        for (int i = 0; i < center.Connectors.Count; i++)
            if (center.Connectors[i].Turn == TurnKind.Right)
                return (n, center, i);
        throw new InvalidOperationException("no Right connector found in fixture");
    }

    [Fact]
    public void TightTurnIsSlowerThanSweepingTurn()
    {
        var (tightNet, tightCenter, tightIdx) = FindRightConnector(TightCross());
        var (sweepNet, sweepCenter, sweepIdx) = FindRightConnector(SweepingY());

        var tightSim = new TrafficSim(tightNet);
        var sweepSim = new TrafficSim(sweepNet);

        float tightSpeed = tightSim.ConnectorSpeedFor(tightCenter.Id, tightIdx);
        float sweepSpeed = sweepSim.ConnectorSpeedFor(sweepCenter.Id, sweepIdx);

        float tightStraight = StraightSpeedFor(tightNet, tightCenter, tightIdx);
        float sweepStraight = StraightSpeedFor(sweepNet, sweepCenter, sweepIdx);

        Assert.True(tightSpeed < sweepSpeed,
            $"tight cross right-turn speed ({tightSpeed:F2} m/s) should be slower than " +
            $"the sweeping Y's right-turn speed ({sweepSpeed:F2} m/s)");
        Assert.InRange(tightSpeed, 4f, tightStraight);
        Assert.InRange(sweepSpeed, 4f, sweepStraight);
    }

    /// <summary>min(from,to run speed limit) for the given connector — the same
    /// straight-speed bound ConnectorSpeed clamps against.</summary>
    private static float StraightSpeedFor(RoadNetwork n, RoadNode center, int index)
    {
        var conn = center.Connectors[index];
        var fromLane = n.Edges.Values.SelectMany(e => e.Lanes).Single(l => l.Id == conn.From);
        var toLane = n.Edges.Values.SelectMany(e => e.Lanes).Single(l => l.Id == conn.To);
        float fromLimit = RoadCatalog.Get(n.Edges[fromLane.Edge].Type).SpeedLimit;
        float toLimit = RoadCatalog.Get(n.Edges[toLane.Edge].Type).SpeedLimit;
        return MathF.Min(fromLimit, toLimit);
    }
}
