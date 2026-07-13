using System.Numerics;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

public class SignalTests
{
    private const float Dt = 1f / 30f;

    private static EdgeId EdgeAt(RoadNetwork n, Vector3 mid)
        => n.Edges.Values.Single(e => Vector3.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;

    private static (RoadNetwork n, RoadNode node) LightsCross()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-150, 0, 0), new Vector3(150, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -150), new Vector3(0, 0, 150)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.TrafficLights });
        return (n, node);
    }

    [Fact]
    public void OpposingLegsShareAPhaseAndAxesAlternate()
    {
        var (n, node) = LightsCross();
        var sim = new TrafficSim(n);
        var wEdge = EdgeAt(n, new Vector3(-75, 0, 0));
        var eEdge = EdgeAt(n, new Vector3(75, 0, 0));
        var nEdge = EdgeAt(n, new Vector3(0, 0, -75));
        var sEdge = EdgeAt(n, new Vector3(0, 0, 75));

        sim.Tick(Dt);
        Assert.Equal(sim.PhaseFor(node.Id, wEdge), sim.PhaseFor(node.Id, eEdge));
        Assert.Equal(sim.PhaseFor(node.Id, nEdge), sim.PhaseFor(node.Id, sEdge));

        var ewStart = sim.PhaseFor(node.Id, wEdge)!.Value;
        var nsStart = sim.PhaseFor(node.Id, nEdge)!.Value;
        Assert.NotEqual(ewStart, nsStart);
        Assert.Contains(SignalPhase.Green, new[] { ewStart, nsStart });

        // after half a cycle the other axis is green
        int halfCycleTicks = (int)((SignalController.GreenSec + SignalController.AmberSec
            + SignalController.AllRedSec) / Dt) + 2;
        for (int i = 0; i < halfCycleTicks; i++)
            sim.Tick(Dt);
        var ewLater = sim.PhaseFor(node.Id, wEdge)!.Value;
        Assert.NotEqual(ewStart == SignalPhase.Green, ewLater == SignalPhase.Green);
    }

    [Fact]
    public void VehicleEntersOnlyOnGreen()
    {
        var (n, node) = LightsCross();
        var sim = new TrafficSim(n);
        var nEdge = EdgeAt(n, new Vector3(0, 0, -75));
        var sEdge = EdgeAt(n, new Vector3(0, 0, 75));
        var v = sim.Spawn(nEdge, true, sEdge)!;

        SignalPhase? phaseAtEntry = null;
        bool waitedOnRed = false;
        for (int i = 0; i < 30 * 120 && phaseAtEntry is null; i++)
        {
            sim.Tick(Dt);
            var phase = sim.PhaseFor(node.Id, nEdge)!.Value;
            if (v.Crossing is not null)
                phaseAtEntry = phase;
            else if (phase == SignalPhase.Red && v.Speed < 0.1f && v.Lane is not null)
                waitedOnRed = true;
        }
        Assert.Equal(SignalPhase.Green, phaseAtEntry);
        Assert.True(waitedOnRed || sim.Time < 16f, "expected the vehicle to wait out a red");
    }
}
