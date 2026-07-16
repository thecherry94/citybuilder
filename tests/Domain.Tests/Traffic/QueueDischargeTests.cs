using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Traffic;

/// <summary>Queue-discharge headway (M6.5 task 4): once a light turns green, queued
/// vehicles should discharge at a realistic per-position headway instead of each
/// follower braking to a dead stop at the line behind a leader that is already
/// pulling away. Reuses the signal_discharge layout/queue-buildup from KpiScenarios
/// (lights junction, vehicles placed bumper-to-bumper behind a red stop line) and
/// asserts on the same quantity the KPI harness reports as diag.signal.h2-h4:
/// consecutive connector-entry gaps by queue position.
///
/// CALIBRATION (measured, per task discipline): on pre-fix main this scenario's
/// h2/h3/h4 measured 3.367/3.333/3.400 s (mean 3.367 — matching the committed M6.5
/// KPI baseline almost exactly), so the 3.0 s gate below was genuinely RED. The
/// bottleneck, found by trajectory instrumentation, was NOT car-following: it was
/// MayEnter's spillback check treating a discharging leader on the exit lane
/// (S &lt; SpawnClearance but moving at 8+ m/s) as a static obstruction, forcing every
/// follower to a full stop at the line (~0.75 s hold + relaunch from zero). The fix
/// is the SpillbackAnticipationSec projection in JunctionArbiter.MayEnter. Two
/// gap-side "leader-start anticipation" IDM tweaks were tried first and measured as
/// no-ops or regressions on this metric (see .superpowers/sdd/task-4-report.md);
/// an earlier draft of this test asserted first-movement times, a proxy that was
/// never red (0.667-0.800 s/vehicle pre-fix) and was replaced by this stop-line
/// assertion.</summary>
public class QueueDischargeTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void QueueDischargesBelowThreeSecondHeadway()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-130, 0, 0), new Vector3(130, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -130), new Vector3(0, 0, 130)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.TrafficLights });

        var west = EdgeAt(n, new Vector3(-65, 0, 0));
        var east = EdgeAt(n, new Vector3(65, 0, 0));

        var sim = new TrafficSim(n, seed: 41);

        // run until a red onset on the west leg (same probe as KpiScenarios.SignalDischarge)
        var prevPhase = sim.PhaseFor(node.Id, west);
        for (int i = 0; i < 60 * 120; i++)
        {
            sim.Tick(Dt);
            var phase = sim.PhaseFor(node.Id, west);
            if (prevPhase != SignalPhase.Red && phase == SignalPhase.Red)
                break;
            prevPhase = phase;
        }
        Assert.Equal(SignalPhase.Red, sim.PhaseFor(node.Id, west));

        // place 5 vehicles bumper-to-bumper behind the stop line: spawn each at the
        // lane's entry (handles route/PlannedConnector setup), then reposition it
        // directly onto its slot in the stationary queue.
        const float frontS = 121f;
        float spacing = Vehicle.Length + Idm.S0;
        var queuedIds = new List<int>();
        for (int i = 0; i < 5; i++)
        {
            var v = sim.Spawn(west, true, east)
                ?? throw new InvalidOperationException($"failed to place queued vehicle {i}");
            v.S = frontS - i * spacing;
            v.Speed = 0f;
            queuedIds.Add(v.Id);
        }

        // run to the next green onset on the west leg
        float greenOnset = -1f;
        prevPhase = sim.PhaseFor(node.Id, west);
        for (int i = 0; i < 60 * 40; i++)
        {
            sim.Tick(Dt);
            var phase = sim.PhaseFor(node.Id, west);
            if (prevPhase != SignalPhase.Green && phase == SignalPhase.Green)
            {
                greenOnset = sim.Time;
                break;
            }
            prevPhase = phase;
        }
        Assert.True(greenOnset >= 0f, "green never arrived after queueing");

        // record each queued vehicle's connector-entry time (Lane -> Crossing) — the
        // same per-position discharge signal as diag.signal.h1..h5. Budget: one green
        // must clear at least positions 1-4 (it does today; the assertion below would
        // surface a regression to 3+ s headways long before this window is the limit).
        var entryTimes = new Dictionary<int, float>();
        for (int i = 0; i < 60 * 15 && entryTimes.Count < 4; i++)
        {
            sim.Tick(Dt);
            foreach (var v in sim.Vehicles)
                if (v.Crossing is not null && queuedIds.Contains(v.Id) && !entryTimes.ContainsKey(v.Id))
                    entryTimes[v.Id] = sim.Time;
        }
        Assert.True(entryTimes.Count >= 4,
            $"only {entryTimes.Count}/4 queued vehicles entered the junction within the 15s window");

        var sorted = entryTimes.Values.OrderBy(t => t).ToArray();
        var h = new float[3]; // h2..h4
        for (int i = 0; i < 3; i++)
            h[i] = sorted[i + 1] - sorted[i];
        float mean = h.Average();
        Assert.True(mean < 3.0f,
            $"mean discharge headway h2..h4 = {mean:F2}s ({string.Join(", ", h.Select(x => x.ToString("F2")))}) — want < 3.0s");
    }

    /// <summary>The safety half of the SpillbackAnticipationSec projection: a STOPPED
    /// occupant inside the exit lane's clearance window projects to itself
    /// (S + 0·τ = S), so it must keep blocking junction entry exactly like the
    /// unprojected check did — the anticipation only ever waves through vehicles
    /// whose occupant is genuinely moving out of the window.</summary>
    [Fact]
    public void StoppedExitLaneOccupantStillBlocksEntry()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(-130, 0, 0), new Vector3(130, 0, 0)));
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -130), new Vector3(0, 0, 130)));
        var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
        // uncontrolled (Free rows): with no rival traffic, spillback is the only
        // thing that can hold the approacher at the line — a clean discriminator
        n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.None });

        var west = EdgeAt(n, new Vector3(-65, 0, 0));
        var east = EdgeAt(n, new Vector3(65, 0, 0));

        var sim = new TrafficSim(n, seed: 7);

        // stalled car just inside the exit lane's clearance window (S=2 < SpawnClearance)
        var occupant = sim.Spawn(east, true, east)
            ?? throw new InvalidOperationException("failed to spawn exit-lane occupant");
        occupant.S = 2f;
        occupant.Speed = 0f;

        var approacher = sim.Spawn(west, true, east)
            ?? throw new InvalidOperationException("failed to spawn approacher");
        Assert.NotNull(approacher.PlannedConnector);
        var exitLane = n.Nodes[node.Id].Connectors[approacher.PlannedConnector!.Value.Connector].To;
        Assert.Equal(exitLane, occupant.Lane);

        // phase 1: occupant pinned stopped — the approacher must reach the stop line
        // and hold there, never entering the junction
        for (int i = 0; i < 60 * 12; i++)
        {
            sim.Tick(Dt);
            occupant.S = 2f;
            occupant.Speed = 0f;
            Assert.True(approacher.Crossing is null,
                $"approacher entered the junction at t={sim.Time:F2} despite a stopped occupant on the exit lane");
        }
        Assert.True(approacher.Lane is not null && approacher.S > 120f && approacher.Speed < 0.5f,
            $"approacher should be held at the stop line (S={approacher.S:F1}, v={approacher.Speed:F2})");

        // phase 2: occupant released — it drives off, the window clears, entry resumes
        bool entered = false;
        for (int i = 0; i < 60 * 15 && !entered; i++)
        {
            sim.Tick(Dt);
            entered = approacher.Crossing is not null;
        }
        Assert.True(entered, "approacher never entered the junction after the exit lane cleared");
    }

    private static EdgeId EdgeAt(RoadNetwork n, Vector3 mid)
        => n.Edges.Values.Single(e => Vector3.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;
}
