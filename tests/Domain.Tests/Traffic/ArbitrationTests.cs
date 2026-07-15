using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class ArbitrationTests
{
    private const float Dt = 1f / 30f;

    private static EdgeId EdgeAt(RoadNetwork n, Vector3 mid)
        => n.Edges.Values.Single(e => Vector3.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;

    /// <summary>Street (E-W, main) crossed by TwoLane (N-S, yield under Auto).</summary>
    private static RoadNetwork MixedCross()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0), RoadCatalog.Street.Id));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150), RoadCatalog.TwoLane.Id));
        return n;
    }

    [Fact]
    public void YieldWaitsForPriorityTraffic()
    {
        var n = MixedCross();
        var sim = new TrafficSim(n);
        var wEdge = EdgeAt(n, new Vector3(-75, 0, 0));
        var eEdge = EdgeAt(n, new Vector3(75, 0, 0));
        var nEdge = EdgeAt(n, new Vector3(0, 0, -75));
        var sEdge = EdgeAt(n, new Vector3(0, 0, 75));

        // steady main-road platoon
        var mains = new List<Vehicle>();
        for (int i = 0; i < 4; i++)
        {
            Vehicle? m = null;
            for (int t = 0; t < 900 && m is null; t++)
            {
                m = sim.Spawn(wEdge, true, eEdge);
                if (m is null)
                    sim.Tick(Dt);
            }
            Assert.NotNull(m);
            mains.Add(m!);
        }
        var minor = sim.Spawn(nEdge, true, sEdge)!;

        bool minorCrossed = false;
        for (int i = 0; i < 30 * 120 && !minorCrossed; i++)
        {
            sim.Tick(Dt);
            if (minor.Crossing is not null && !minorCrossed)
            {
                minorCrossed = true;
                // rule: no priority vehicle within 25 m of the junction moving fast
                foreach (var m in mains.Where(m => sim.Vehicles.Contains(m) && m.Lane is not null))
                {
                    var (pos, _) = sim.Pose(m);
                    float dist = Vector3.Distance(pos, new Vector3(0, 0, 0));
                    bool approaching = pos.X < -6f; // west of the junction
                    if (approaching && m.Speed > 3f)
                        Assert.True(dist > 25f,
                            $"minor entered while main {m.Id} was {dist:F1} m away at {m.Speed:F1} m/s");
                }
            }
        }
        Assert.True(minorCrossed, "minor vehicle never crossed");
    }

    [Fact]
    public void AllWayStopIsFifo()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.AllWayStop });

        var sim = new TrafficSim(n);
        var west = sim.Spawn(EdgeAt(n, new Vector3(-75, 0, 0)), true, EdgeAt(n, new Vector3(75, 0, 0)))!;
        // north vehicle starts 40 m behind → arrives later
        for (int i = 0; i < 30 * 3; i++)
            sim.Tick(Dt);
        var north = sim.Spawn(EdgeAt(n, new Vector3(0, 0, -75)), true, EdgeAt(n, new Vector3(0, 0, 75)))!;

        float westEnter = -1, northEnter = -1;
        for (int i = 0; i < 30 * 120 && (westEnter < 0 || northEnter < 0); i++)
        {
            sim.Tick(Dt);
            if (westEnter < 0 && west.Crossing is not null) westEnter = sim.Time;
            if (northEnter < 0 && north.Crossing is not null) northEnter = sim.Time;
        }
        Assert.True(westEnter > 0 && northEnter > 0, "both must eventually cross");
        Assert.True(westEnter < northEnter, $"west {westEnter:F1} should precede north {northEnter:F1}");
    }

    [Fact]
    public void ConflictingConnectorsNeverOverlap()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);

        var sim = new TrafficSim(n);
        // two opposing left turns: W→N and E→S (their connector curves cross)
        var a = sim.Spawn(EdgeAt(n, new Vector3(-75, 0, 0)), true, EdgeAt(n, new Vector3(0, 0, -75)))!;
        var b = sim.Spawn(EdgeAt(n, new Vector3(75, 0, 0)), false, EdgeAt(n, new Vector3(0, 0, 75)))!;

        for (int i = 0; i < 30 * 120 && sim.Arrived < 2; i++)
        {
            sim.Tick(Dt);
            if (a.Crossing is { } ca && b.Crossing is { } cb && ca.Node == cb.Node)
            {
                var conflicts = node.ConnectorConflicts[ca.Connector].Select(c => c.Other).ToArray();
                Assert.DoesNotContain(cb.Connector, conflicts);
            }
        }
        Assert.Equal(2, sim.Arrived);
    }

    [Fact]
    public void StopRoleRequiresFullStop()
    {
        var n = MixedCross();
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        var nEdge = EdgeAt(n, new Vector3(0, 0, -75));
        n.ConfigureJunction(node.Id, node.Config with
        {
            Mode = JunctionControlMode.PrioritySigns,
            RoleOverrides = new Dictionary<EdgeId, LegRole> { [nEdge] = LegRole.Stop },
        });

        var sim = new TrafficSim(n);
        var v = sim.Spawn(nEdge, true, EdgeAt(n, new Vector3(0, 0, 75)))!;
        float minSpeedNearLine = float.MaxValue;
        bool crossed = false;
        for (int i = 0; i < 30 * 90 && !crossed; i++)
        {
            sim.Tick(Dt);
            if (v.Crossing is not null)
                crossed = true;
            else if (v.Lane is not null && sim.Vehicles.Contains(v))
            {
                var (pos, _) = sim.Pose(v);
                if (Vector3.Distance(pos, node.Position) < 18f)
                    minSpeedNearLine = MathF.Min(minSpeedNearLine, v.Speed);
            }
        }
        Assert.True(crossed, "stop vehicle never crossed");
        Assert.True(minSpeedNearLine < 0.1f, $"min speed near line was {minSpeedNearLine:F2}");
    }
}
