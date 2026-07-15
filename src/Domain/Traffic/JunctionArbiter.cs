using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Traffic;

/// <summary>
/// Junction entry arbitration: whether a vehicle at its lane's end may start its
/// connector. Driven by the connector's RightOfWay, the node's conflict sets, and
/// (for lights) the signal phase. Vehicles already inside a junction always finish.
/// </summary>
public sealed partial class TrafficSim
{
    private const float StopLineZone = 3f;      // "at the line" distance
    private const float ApproachHorizon = 60f;  // how far to scan for priority traffic
    private const float ClearMargin = 0.5f;     // rear-bumper clearance past a conflict point
    private const float DeadlockBreakSec = 6f;  // waited this long → ignore a stale equal-rank rival

    private bool MayEnter(Vehicle v, NodeId nodeId, int ci)
    {
        var node = _network.Nodes[nodeId];
        var conn = node.Connectors[ci];

        // spillback: target lane entry must have space
        var target = _laneVehicles[conn.To];
        if (target.Count > 0 && target[^1].S < SpawnClearance)
            return false;

        // a conflicting occupant blocks only until its rear bumper clears our crossing point
        foreach (var cp in node.ConnectorConflicts[ci])
        {
            var occupants = _connectorVehicles[(nodeId, cp.Other)];
            for (int k = 0; k < occupants.Count; k++)
                if (occupants[k].S < cp.STheirs + Vehicle.Length + ClearMargin)
                    return false;
        }

        switch (conn.Row)
        {
            case RightOfWay.Free:
                return ConflictApproachClear(node, nodeId, ci, v);

            case RightOfWay.Signal:
                return IsGreen(nodeId, LaneRunEdge(v)) && ConflictApproachClear(node, nodeId, ci, v);

            case RightOfWay.Yield:
                return ConflictApproachClear(node, nodeId, ci, v);

            case RightOfWay.Stop:
                if (!AtLineStopped(v))
                    return false;
                return _controls[nodeId].Mode == JunctionControlMode.AllWayStop
                    ? FifoTurn(v, nodeId)
                    : ConflictApproachClear(node, nodeId, ci, v);

            default:
                return true;
        }
    }

    private EdgeId LaneRunEdge(Vehicle v) => _runs[v.Lane!.Value].Edge;

    /// <summary>Stop compliance: the latch sets only within the stop-line zone.</summary>
    private bool AtLineStopped(Vehicle v)
    {
        if (v.Lane is not { } laneId)
            return true;
        float remaining = _runs[laneId].Length - v.S;
        if (remaining > StopLineZone)
            return false;
        if (v.Speed < 0.1f)
            v.HasStopped = true;
        if (v.HasStopped && v.WaitArrivalOrder == 0)
            v.WaitArrivalOrder = Time + v.Id * 1e-6f; // FIFO ticket, unique
        return v.HasStopped;
    }

    /// <summary>Gap a vehicle will accept before entering: 2.8 s fresh, shrinking to a
    /// 2.2 s floor as it waits longer at the line (impatience).</summary>
    private static float AcceptedGap(Vehicle v) => MathF.Max(2.2f, 2.8f - 0.03f * v.JunctionWait);

    /// <summary>Movement priority for right-of-way comparisons: (leg role, turn kind),
    /// higher wins on both axes. A Free/Signal(green) leg outranks Yield, which
    /// outranks Stop; within a leg, Straight outranks Right outranks Left.</summary>
    private static (int Row, int Turn) MovementRank(LaneConnector conn) =>
        (conn.Row switch
        {
            RightOfWay.Free or RightOfWay.Signal => 3, // Signal only reaches here when green
            RightOfWay.Yield => 2,
            _ => 1,
        },
        conn.Turn switch
        {
            TurnKind.Straight => 3,
            TurnKind.Right => 2,
            TurnKind.Left => 1,
            _ => 0,
        });

    /// <summary>Right-hand rule: does the other movement approach from my right?
    /// Signed angle from my approach direction to theirs in (−150°, −30°).</summary>
    private static bool ApproachesFromMyRight(LaneConnector mine, LaneConnector other)
    {
        var m = mine.Curve.Tangent(0);
        var o = other.Curve.Tangent(0);
        float cross = m.X * o.Z - m.Z * o.X;
        float dot = m.X * o.X + m.Z * o.Z;
        float deg = MathF.Atan2(cross, dot) * 180f / MathF.PI;
        return deg > -150f && deg < -30f;
    }

    /// <summary>No higher-priority (or equal-priority from the right) traffic about to
    /// use a conflicting connector within this driver's accepted gap. Vehicles that have
    /// waited past DeadlockBreakSec ignore stationary equal-rank rivals with later
    /// arrival tickets — four cars at an uncontrolled cross must never freeze.</summary>
    private bool ConflictApproachClear(RoadNode node, NodeId nodeId, int ci, Vehicle me)
    {
        var mine = node.Connectors[ci];
        var myRank = MovementRank(mine);
        float accepted = AcceptedGap(me);

        foreach (var cp in node.ConnectorConflicts[ci])
        {
            var other = node.Connectors[cp.Other];
            if (other.Row == RightOfWay.Signal && !IsGreen(nodeId, _lanes[other.From].Edge))
                continue; // red: that movement is not coming
            var theirRank = MovementRank(other);
            int cmp = theirRank.CompareTo(myRank);
            bool mustYield = cmp > 0 || (cmp == 0 && ApproachesFromMyRight(mine, other));
            if (!mustYield)
                continue;

            var feed = _laneVehicles[other.From];
            for (int k = 0; k < feed.Count; k++)
            {
                var rival = feed[k];
                if (rival.PlannedConnector is not { } pc || pc.Node != nodeId || pc.Connector != cp.Other)
                    continue;
                float dist = _runs[other.From].Length - rival.S;
                if (dist > ApproachHorizon)
                    continue;
                float tta = dist / MathF.Max(rival.Speed, 0.5f);
                if (tta >= accepted)
                    continue;
                if (cmp == 0 && DeadlockBreak(me, rival, dist))
                    continue; // stale standoff: earliest ticket goes
                return false;
            }
        }
        return true;
    }

    private static bool DeadlockBreak(Vehicle me, Vehicle rival, float rivalDistToLine)
        => me.JunctionWait > DeadlockBreakSec
           && rival.Speed < 0.5f
           && rivalDistToLine < StopLineZone + 2f
           && me.WaitArrivalOrder > 0
           && (rival.WaitArrivalOrder == 0 || me.WaitArrivalOrder < rival.WaitArrivalOrder);

    /// <summary>All-way stop: strict arrival order among vehicles waiting at the
    /// node's stop lines.</summary>
    private bool FifoTurn(Vehicle v, NodeId nodeId)
    {
        if (!_incomingLanes.TryGetValue(nodeId, out var lanes))
            return true;
        foreach (var laneId in lanes)
        {
            var queue = _laneVehicles[laneId];
            if (queue.Count == 0)
                continue;
            var front = queue[0];
            if (front == v)
                continue;
            if (front.WaitArrivalOrder > 0 && front.WaitArrivalOrder < v.WaitArrivalOrder)
                return false;
        }
        return true;
    }

    private readonly Dictionary<NodeId, SignalController> _signals = new();

    private bool IsGreen(NodeId node, EdgeId leg)
        => !_signals.TryGetValue(node, out var s) || s.Phase(leg) == SignalPhase.Green;

    /// <summary>Signal phase for a leg at a lights-controlled node (view + arbiter).</summary>
    public SignalPhase? PhaseFor(NodeId node, EdgeId leg)
        => _signals.TryGetValue(node, out var s) ? s.Phase(leg) : null;

    private void AdvanceSignals(float dt)
    {
        foreach (var s in _signals.Values)
            s.Advance(dt);
    }

    /// <summary>Keep signal controllers in sync with lights-controlled nodes,
    /// preserving phase timers across unrelated network edits.</summary>
    private void SyncSignals()
    {
        foreach (var id in _signals.Keys.Where(id =>
            !_controls.TryGetValue(id, out var c) || c.Mode != JunctionControlMode.TrafficLights).ToArray())
            _signals.Remove(id);
        foreach (var (id, control) in _controls)
            if (control.Mode == JunctionControlMode.TrafficLights && !_signals.ContainsKey(id))
                _signals[id] = new SignalController(_network.Nodes[id], _network);
    }
}
