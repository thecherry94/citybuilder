using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Traffic;

/// <summary>
/// Junction entry arbitration: whether a vehicle at its lane's end may start its
/// connector. Driven by the connector's RightOfWay, the node's conflict sets, and
/// (for lights) the signal phase. Vehicles already inside a junction always finish.
/// </summary>
public sealed partial class TrafficSim
{
    private const float GapAcceptanceSec = 4f;
    private const float StopLineZone = 3f;      // "at the line" distance
    private const float ApproachHorizon = 60f;  // how far to scan for priority traffic

    private bool MayEnter(Vehicle v, NodeId nodeId, int ci)
    {
        var node = _network.Nodes[nodeId];
        var conn = node.Connectors[ci];

        // spillback: target lane entry must have space
        var target = _laneVehicles[conn.To];
        if (target.Count > 0 && target[^1].S < SpawnClearance)
            return false;

        // never enter while a conflicting path is occupied
        foreach (var j in node.ConnectorConflicts[ci])
            if (_connectorVehicles[(nodeId, j)].Count > 0)
                return false;

        switch (conn.Row)
        {
            case RightOfWay.Free:
                return true;

            case RightOfWay.Signal:
                return IsGreen(nodeId, LaneRunEdge(v)) && ConflictApproachClear(node, nodeId, ci, freeOnly: true);

            case RightOfWay.Yield:
                return ConflictApproachClear(node, nodeId, ci, freeOnly: true);

            case RightOfWay.Stop:
                if (!AtLineStopped(v))
                    return false;
                return _controls[nodeId].Mode == JunctionControlMode.AllWayStop
                    ? FifoTurn(v, nodeId)
                    : ConflictApproachClear(node, nodeId, ci, freeOnly: true);

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

    /// <summary>No priority traffic about to use a conflicting connector within the
    /// gap-acceptance window.</summary>
    private bool ConflictApproachClear(RoadNode node, NodeId nodeId, int ci, bool freeOnly)
    {
        foreach (var j in node.ConnectorConflicts[ci])
        {
            var other = node.Connectors[j];
            if (freeOnly && other.Row != RightOfWay.Free)
                continue;
            var feed = _laneVehicles[other.From];
            for (int k = 0; k < feed.Count; k++)
            {
                var rival = feed[k];
                if (rival.PlannedConnector is not { } pc || pc.Node != nodeId || pc.Connector != j)
                    continue;
                float dist = _runs[other.From].Length - rival.S;
                if (dist > ApproachHorizon)
                    continue;
                float tta = dist / MathF.Max(rival.Speed, 0.5f);
                if (tta < GapAcceptanceSec)
                    return false;
            }
        }
        return true;
    }

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
