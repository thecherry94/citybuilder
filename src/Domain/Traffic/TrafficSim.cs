using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Traffic;

/// <summary>
/// Fixed-step traffic simulation over a road network. Strategic routes come from
/// RoutePlanner; per tick every vehicle follows its lane leader (IDM), enters
/// junction connectors when arbitration allows, and despawns at its goal.
/// Pure C#: deterministic under a fixed dt and seed.
/// </summary>
public sealed partial class TrafficSim
{
    private const float LookAheadHorizon = 120f;
    private const float SpawnClearance = Vehicle.Length + Idm.S0;

    private readonly RoadNetwork _network;
    private readonly Random _rng;
    private readonly List<Vehicle> _vehicles = new();
    private int _nextId = 1;
    private int _syncedVersion = -1;

    // caches, rebuilt when the network version changes
    private readonly Dictionary<LaneId, Lane> _lanes = new();
    private readonly Dictionary<LaneId, LaneRun> _runs = new();
    private readonly Dictionary<(NodeId, int), ArcLengthTable> _connectorLength = new();
    private readonly Dictionary<LaneId, List<Vehicle>> _laneVehicles = new();
    private readonly Dictionary<(NodeId, int), List<Vehicle>> _connectorVehicles = new();
    private readonly Dictionary<NodeId, EffectiveControl> _controls = new();
    private readonly Dictionary<NodeId, List<LaneId>> _incomingLanes = new();
    private readonly Dictionary<LaneId, (LaneId? Left, LaneId? Right)> _adjacent = new();

    public TrafficSim(RoadNetwork network, int seed = 1)
    {
        _network = network;
        _rng = new Random(seed);
        Sync();
    }

    public IReadOnlyList<Vehicle> Vehicles => _vehicles;
    public int Arrived { get; private set; }
    public float Time { get; private set; }
    public RoadNetwork Network => _network;
    internal Random Rng => _rng;

    /// <summary>One completed trip's KPI-relevant stats, logged on genuine arrival at
    /// the destination (not on stranding/bulldoze despawns). Null (default) means no
    /// recording and no per-tick allocation — enable only for KPI-harness scenarios.</summary>
    public sealed record TripRecord(int VehicleId, float SpawnTime, float ArrivalTime, float FreeFlowTime, int Stops);

    public List<TripRecord>? TripLog { get; set; }

    /// <summary>Travel frame of a lane: entry cut, drawable length, direction.</summary>
    internal readonly record struct LaneRun(
        EdgeId Edge, bool Forward, float DStart, float Length, float SpeedLimit);

    // ------------------------------------------------------------------ spawn

    public Vehicle? Spawn(EdgeId fromEdge, bool forward, EdgeId toEdge)
    {
        Sync();
        var route = RoutePlanner.Plan(_network, fromEdge, forward, toEdge);
        if (route is null || !_network.Edges.TryGetValue(fromEdge, out var edge))
            return null;

        foreach (var lane in RankedEntryLanes(edge, forward, route))
        {
            var queue = _laneVehicles[lane.Id];
            if (queue.Count > 0 && queue[^1].S < SpawnClearance + 1f)
                continue; // entry occupied
            var v = new Vehicle { Id = _nextId++, Route = route };
            v.Lane = lane.Id;
            v.S = 0;
            v.SpawnTime = Time;
            // per-driver personality: drawn from the sim's own seeded RNG, in spawn
            // order, so replays stay bit-identical for a fixed seed (both the ambient
            // spawner and manual Spawn() callers funnel through this one construction
            // site — there is only one place a Vehicle is ever created)
            v.Profile = (float)_rng.NextDouble();
            // FreeFlowTime starts at 0 and accumulates in HandleTransitions as the
            // vehicle actually completes lane runs and connectors — exact for the
            // route driven, robust to any number of replans (no route-based estimate
            // to double-count distance already covered).
            v.Speed = MathF.Min(_runs[lane.Id].SpeedLimit * 0.7f, 12f);
            if (queue.Count > 0)
            {
                // never spawn faster than we could brake behind the tail vehicle
                var tail = queue[^1];
                float gap = MathF.Max(0, tail.S - Vehicle.Length - Idm.S0);
                float safe = MathF.Sqrt(tail.Speed * tail.Speed + 2f * Idm.B * gap);
                v.Speed = MathF.Min(v.Speed, safe);
            }
            v.PlannedConnector = PickConnector(v, lane.Id);
            queue.Add(v);
            SortQueue(queue);
            _vehicles.Add(v);
            return v;
        }
        return null;
    }

    private IEnumerable<Lane> RankedEntryLanes(RoadEdge edge, bool forward, Route route)
        => edge.Lanes
            .Where(l => l.Kind == LaneKind.Driving
                && (l.Direction == LaneDirection.Forward) == forward)
            .OrderByDescending(l => route.Steps.Count == 1 || ServesNextMovement(l.Id, route, 0))
            .ThenBy(l => l.Id.Value);

    /// <summary>Refresh caches/signal controllers after network edits without
    /// advancing the simulation (views need phases while the sim is paused).</summary>
    public void EnsureSynced() => Sync();

    /// <summary>Test hook: left/right neighbor lanes cached for `id`'s direction.</summary>
    internal (LaneId? Left, LaneId? Right) AdjacentOf(LaneId id) => _adjacent[id];

    /// <summary>Test hook: teleport a vehicle onto a specific lane at its current S.</summary>
    internal void ForceLane(Vehicle v, LaneId lane)
    {
        RemoveFromQueues(v);
        v.Lane = lane;
        v.Crossing = null;
        v.ChangeFrom = null;
        v.ChangeProgress = 0;
        v.PlannedConnector = PickConnector(v, lane);
        _laneVehicles[lane].Add(v);
        SortQueue(_laneVehicles[lane]);
    }

    /// <summary>Test hook: vehicles currently occupying a given node/connector pair.</summary>
    internal IReadOnlyList<Vehicle> VehiclesOnConnector(NodeId node, int connector)
        => _connectorVehicles.TryGetValue((node, connector), out var q) ? q : Array.Empty<Vehicle>();

    /// <summary>Invariant-checking hook: every lane and connector queue in the sim,
    /// each already sorted front-to-back (see <see cref="SortQueue"/>) — lets a burst
    /// checker scan for penetration without knowing about lane/connector storage.</summary>
    internal IEnumerable<IReadOnlyList<Vehicle>> AllQueues()
    {
        foreach (var q in _laneVehicles.Values)
            yield return q;
        foreach (var q in _connectorVehicles.Values)
            yield return q;
    }

    /// <summary>Test hook: place a vehicle on a junction connector at arc position s.</summary>
    internal void ForceConnector(Vehicle v, NodeId node, int connector, float s)
    {
        RemoveFromQueues(v);
        v.Lane = null;
        v.ChangeFrom = null;
        v.ChangeProgress = 0;
        v.PlannedConnector = null;
        v.Crossing = (node, connector);
        v.S = s;
        _connectorVehicles[(node, connector)].Add(v);
        SortQueue(_connectorVehicles[(node, connector)]);
    }

    public void Despawn(Vehicle v)
    {
        RemoveFromQueues(v);
        _vehicles.Remove(v);
    }

    // ------------------------------------------------------------------- tick

    public void Tick(float dt)
    {
        Sync();
        Time += dt;
        AdvanceSignals(dt);

        foreach (var v in _vehicles)
        {
            v.BlockedAtLine = false;
            v.Accel = ComputeAccel(v);
        }

        foreach (var v in _vehicles)
        {
            // captured before the update — Accel*dt back-solve is wrong post-clamp;
            // a stop induced purely by the EnforceNoPenetration failsafe clamp (which
            // rewrites Speed after this loop) is not seen as a crossing and goes uncounted
            float prevSpeed = v.Speed;
            v.Speed = MathF.Max(0, v.Speed + v.Accel * dt);
            if (v.Speed > 2f)
                v.HasMoved = true;
            if (v.HasMoved && prevSpeed >= 0.5f && v.Speed < 0.5f)
                v.Stops++;
            v.S += v.Speed * dt;
            v.StuckTime = v.Speed < 0.1f ? v.StuckTime + dt : 0f;
            if (v.BlockedAtLine)
                v.JunctionWait += dt;
            UpdateLaneChange(v, dt);
        }

        EnforceNoPenetration();
        HandleTransitions();
        AfterTick(dt);

        foreach (var queue in _laneVehicles.Values)
            SortQueue(queue);
        foreach (var queue in _connectorVehicles.Values)
            SortQueue(queue);
    }

    private void AfterTick(float dt) => SpawnerTick(dt);

    // --------------------------------------------------------------- behavior

    private float ComputeAccel(Vehicle v)
    {
        float v0 = DesiredSpeed(v);
        var (gap, dv) = LeaderGap(v);

        // junction stop line: a static wall at the lane end when we may not enter
        if (v.Lane is { } laneId && !v.OnLastStep && v.PlannedConnector is { } pc)
        {
            // hold at the painted stop line, just short of the cut
            float remaining = _runs[laneId].Length - 0.4f - v.S;
            if (remaining < 40f && !MayEnter(v, pc.Node, pc.Connector))
            {
                if (remaining < StopLineZone)
                {
                    v.BlockedAtLine = true;
                    if (v.WaitArrivalOrder == 0)
                        v.WaitArrivalOrder = Time + v.Id * 1e-6f; // FIFO ticket, unique
                }
                if (remaining < gap)
                {
                    gap = MathF.Max(remaining, 0.05f);
                    dv = v.Speed;
                }
            }
        }
        else if (v.Lane is { } lid && !v.OnLastStep && v.PlannedConnector is null)
        {
            // wrong lane for the next movement and no escape yet: stop at the end
            float remaining = _runs[lid].Length - v.S;
            if (remaining < gap)
            {
                gap = MathF.Max(remaining, 0.05f);
                dv = v.Speed;
            }
        }

        return Idm.Accel(v.Speed, v0, gap, dv);
    }

    private float DesiredSpeed(Vehicle v)
    {
        // per-driver personality: 0 (timid) -> 0.85x, 0.5 (neutral) -> 1.0x, 1 (assertive) -> 1.2x
        float personality = 0.85f + 0.35f * v.Profile;
        if (v.Lane is { } laneId)
        {
            var run = _runs[laneId];
            float v0 = run.SpeedLimit;
            // approach upcoming turns along a comfortable braking envelope instead
            // of hitting the connector at full speed
            if (v.PlannedConnector is { } pc)
            {
                float dist = MathF.Max(0, run.Length - v.S);
                if (dist < 40f)
                {
                    float turnV = ConnectorSpeed(pc);
                    float envelope = MathF.Sqrt(turnV * turnV + 2f * Idm.B * dist);
                    v0 = MathF.Min(v0, envelope);
                }
            }
            return v0 * personality;
        }
        return (v.Crossing is { } cr ? ConnectorSpeed(cr) : 8f) * personality;
    }

    /// <summary>Comfortable speed through a junction, by movement geometry. Straights
    /// flow at the road's limit — priority traffic doesn't brake for junctions.</summary>
    private float ConnectorSpeed((NodeId Node, int Connector) key)
    {
        var conn = _network.Nodes[key.Node].Connectors[key.Connector];
        return conn.Turn switch
        {
            TurnKind.Straight => MathF.Min(_runs[conn.From].SpeedLimit, _runs[conn.To].SpeedLimit),
            TurnKind.Right => 9f,
            TurnKind.Left => 10f,
            _ => 5f, // u-turns
        };
    }

    /// <summary>Bumper gap and closing speed to the nearest leader within the
    /// look-ahead horizon, walking lane → connector → next lane.</summary>
    private (float gap, float dv) LeaderGap(Vehicle v)
    {
        var queue = Occupants(v);
        int idx = queue.IndexOf(v);
        if (idx > 0)
        {
            var lead = queue[idx - 1];
            return (lead.S - Vehicle.Length - v.S, v.Speed - lead.Speed);
        }

        float dist = RunLength(v) - v.S;
        // follow the chain ahead
        if (v.Lane is not null)
        {
            if (v.OnLastStep || v.PlannedConnector is not { } pc)
                return (Idm.FreeGap, 0);
            if (FirstOn(_connectorVehicles[(pc.Node, pc.Connector)]) is { } onConn)
                return (dist + onConn.S - Vehicle.Length, v.Speed - onConn.Speed);
            dist += _connectorLength[(pc.Node, pc.Connector)].TotalLength;
            var toLane = _network.Nodes[pc.Node].Connectors[pc.Connector].To;
            if (FirstOn(_laneVehicles[toLane]) is { } onLane)
                return (dist + onLane.S - Vehicle.Length, v.Speed - onLane.Speed);
        }
        else if (v.Crossing is { } cr)
        {
            var toLane = _network.Nodes[cr.Node].Connectors[cr.Connector].To;
            if (FirstOn(_laneVehicles[toLane]) is { } onLane)
                return (dist + onLane.S - Vehicle.Length, v.Speed - onLane.Speed);
        }
        return (Idm.FreeGap, 0);
    }

    private static Vehicle? FirstOn(List<Vehicle> queue)
        => queue.Count > 0 ? queue[^1] : null; // sorted desc by S → last = closest to entry

    /// <summary>Diagnostic-only counter: how many times <see cref="EnforceNoPenetration"/>
    /// actually moved a vehicle's S or clamped its Speed this run (never reset —
    /// callers read it once at the end of a scenario). Not part of any behavior,
    /// purely for KPI-harness visibility into how often the failsafe clamp fires.</summary>
    internal int PenetrationClampCount { get; private set; }

    /// <summary>Hard failsafe: whatever the models did this tick, a follower never
    /// ends up inside its leader. Queues are still in last tick's order (no in-lane
    /// overtaking), so a single front-to-back pass suffices.</summary>
    private void EnforceNoPenetration()
    {
        foreach (var queue in _laneVehicles.Values)
            ClampQueue(queue);
        foreach (var queue in _connectorVehicles.Values)
            ClampQueue(queue);
    }

    private void ClampQueue(List<Vehicle> queue)
    {
        for (int i = 1; i < queue.Count; i++)
        {
            float limit = queue[i - 1].S - Vehicle.Length - 0.1f;
            if (queue[i].S > limit)
            {
                // S > limit guarantees setting S = limit is a real change, so the
                // counter increments unconditionally here (never on no-op passes)
                queue[i].S = limit;
                queue[i].Speed = MathF.Min(queue[i].Speed, queue[i - 1].Speed);
                PenetrationClampCount++;
            }
        }
    }

    // ------------------------------------------------------------ transitions

    private void HandleTransitions()
    {
        for (int i = _vehicles.Count - 1; i >= 0; i--)
        {
            var v = _vehicles[i];
            float len = RunLength(v);
            if (v.S <= len)
                continue;

            if (v.Lane is { } laneId)
            {
                if (v.OnLastStep)
                {
                    // arrival fires only past the run's end (v.S > len), so the final
                    // run was driven in full — credit it like any completed run
                    v.FreeFlowTime += len / _runs[laneId].SpeedLimit;
                    RemoveFromQueues(v);
                    _vehicles.RemoveAt(i);
                    Arrived++;
                    TripLog?.Add(new TripRecord(v.Id, v.SpawnTime, Time, v.FreeFlowTime, v.Stops));
                    continue;
                }
                if (v.ChangeFrom is not null)
                {
                    // never enter a junction mid-lane-change: hold at the line
                    v.S = len;
                    v.Speed = 0;
                }
                else if (v.PlannedConnector is { } pc && MayEnter(v, pc.Node, pc.Connector))
                {
                    // completed this lane run at its full length (overshoot belongs
                    // to the connector's own S frame, no double count)
                    v.FreeFlowTime += len / _runs[laneId].SpeedLimit;
                    float overshoot = v.S - len;
                    RemoveFromQueues(v);
                    v.PrevLane = laneId;
                    v.PrevCrossing = null;
                    v.PrevLength = len;
                    v.Lane = null;
                    v.ChangeFrom = null;
                    v.ChangeProgress = 0;
                    v.Crossing = pc;
                    v.S = overshoot;
                    v.HasStopped = false;
                    v.WaitArrivalOrder = 0;
                    v.JunctionWait = 0;
                    _connectorVehicles[pc].Add(v);
                }
                else
                {
                    v.S = len;
                    v.Speed = 0;
                }
            }
            else if (v.Crossing is { } cr)
            {
                // completed a junction connector: free-flow dwell approximated as a
                // fixed 8 m (typical corner-to-corner arc) over the same turn speed
                // the runtime targets (ConnectorSpeed) — a KPI estimate, not the
                // measured arc length
                v.FreeFlowTime += 8f / ConnectorSpeed(cr);
                float overshoot = v.S - len;
                var toLane = _network.Nodes[cr.Node].Connectors[cr.Connector].To;
                RemoveFromQueues(v);
                v.PrevCrossing = cr;
                v.PrevLane = null;
                v.PrevLength = len;
                v.Crossing = null;
                v.Lane = toLane;
                v.S = overshoot;
                v.StepIndex++;
                v.PlannedConnector = v.OnLastStep ? null : PickConnector(v, toLane);
                _laneVehicles[toLane].Add(v);
            }
        }
    }

    /// <summary>Connector from this lane serving the route's next movement, if any.</summary>
    internal (NodeId Node, int Connector)? PickConnector(Vehicle v, LaneId laneId)
    {
        if (v.OnLastStep)
            return null;
        var run = _runs[laneId];
        var edge = _network.Edges[run.Edge];
        var node = _network.Nodes[run.Forward ? edge.EndNode : edge.StartNode];
        var next = v.Route.Steps[v.StepIndex + 1];
        for (int i = 0; i < node.Connectors.Count; i++)
        {
            var c = node.Connectors[i];
            if (c.From != laneId)
                continue;
            var toLane = _lanes[c.To];
            if (toLane.Edge == next.Edge && (toLane.Direction == LaneDirection.Forward) == next.Forward)
                return (node.Id, i);
        }
        return null;
    }

    internal bool ServesNextMovement(LaneId laneId, Route route, int stepIndex)
    {
        if (stepIndex >= route.Steps.Count - 1)
            return true;
        var run = _runs[laneId];
        var edge = _network.Edges[run.Edge];
        var node = _network.Nodes[run.Forward ? edge.EndNode : edge.StartNode];
        var next = route.Steps[stepIndex + 1];
        foreach (var c in node.Connectors)
        {
            if (c.From != laneId)
                continue;
            var toLane = _lanes[c.To];
            if (toLane.Edge == next.Edge && (toLane.Direction == LaneDirection.Forward) == next.Forward)
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- caches

    private void Sync()
    {
        if (_syncedVersion == _network.Version)
            return;
        _syncedVersion = _network.Version;

        _lanes.Clear();
        _runs.Clear();
        _connectorLength.Clear();
        var oldLaneQueues = new HashSet<LaneId>(_laneVehicles.Keys);
        var oldConnQueues = new HashSet<(NodeId, int)>(_connectorVehicles.Keys);

        foreach (var edge in _network.Edges.Values)
        {
            float tStart = 0f, tEnd = 1f;
            if (_network.Nodes.TryGetValue(edge.StartNode, out var sn)
                && sn.Junction.CutT.TryGetValue(edge.Id, out var a))
                tStart = a;
            if (_network.Nodes.TryGetValue(edge.EndNode, out var en)
                && en.Junction.CutT.TryGetValue(edge.Id, out var b))
                tEnd = b;
            float dA = edge.ArcLength.DistanceAtT(tStart);
            float dB = edge.ArcLength.DistanceAtT(tEnd);
            float limit = RoadCatalog.Get(edge.Type).SpeedLimit;

            foreach (var lane in edge.Lanes)
            {
                _lanes[lane.Id] = lane;
                bool fwd = lane.Direction == LaneDirection.Forward;
                _runs[lane.Id] = new LaneRun(edge.Id, fwd, fwd ? dA : dB,
                    MathF.Max(0.5f, dB - dA), limit);
                if (!_laneVehicles.ContainsKey(lane.Id))
                    _laneVehicles[lane.Id] = new List<Vehicle>();
                oldLaneQueues.Remove(lane.Id);
            }
        }
        _controls.Clear();
        _incomingLanes.Clear();
        foreach (var node in _network.Nodes.Values)
        {
            _controls[node.Id] = JunctionControl.Resolve(node, _network.Edges);
            for (int i = 0; i < node.Connectors.Count; i++)
            {
                _connectorLength[(node.Id, i)] = new ArcLengthTable(node.Connectors[i].Curve, 24);
                if (!_connectorVehicles.ContainsKey((node.Id, i)))
                    _connectorVehicles[(node.Id, i)] = new List<Vehicle>();
                oldConnQueues.Remove((node.Id, i));
            }
        }
        foreach (var (laneId, run) in _runs)
        {
            var edge = _network.Edges[run.Edge];
            var exit = run.Forward ? edge.EndNode : edge.StartNode;
            if (!_incomingLanes.TryGetValue(exit, out var list))
                _incomingLanes[exit] = list = new List<LaneId>();
            list.Add(laneId);
        }

        // adjacency (left/right neighbor in travel frame) among same-direction
        // driving lanes of each edge, ordered left → right by signed offset per the
        // travel direction (forward travel: left = −offset; backward travel: left =
        // +offset — |offset| ordering gets this backwards whenever lanes span 0)
        _adjacent.Clear();
        foreach (var edge in _network.Edges.Values)
        foreach (var group in edge.Lanes
                     .Where(l => l.Kind == LaneKind.Driving)
                     .GroupBy(l => l.Direction))
        {
            var ordered = (group.Key == LaneDirection.Forward
                ? group.OrderBy(l => l.Offset)               // forward travel: left = −offset
                : group.OrderByDescending(l => l.Offset))    // backward travel: left = +offset
                .ToArray();
            for (int i = 0; i < ordered.Length; i++)
                _adjacent[ordered[i].Id] = (
                    i > 0 ? ordered[i - 1].Id : null,
                    i < ordered.Length - 1 ? ordered[i + 1].Id : null);
        }

        // drop vehicles stranded on removed lanes/connectors
        foreach (var key in oldLaneQueues)
        {
            foreach (var v in _laneVehicles[key])
                _vehicles.Remove(v);
            _laneVehicles.Remove(key);
        }
        foreach (var key in oldConnQueues)
        {
            foreach (var v in _connectorVehicles[key])
                _vehicles.Remove(v);
            _connectorVehicles.Remove(key);
        }
        OnNetworkChanged();
    }

    private void OnNetworkChanged()
    {
        SyncSignals();
        RevalidateAfterNetworkChange();
    }

    private float RunLength(Vehicle v)
        => v.Lane is { } laneId
            ? _runs[laneId].Length
            : _connectorLength[(v.Crossing!.Value.Node, v.Crossing.Value.Connector)].TotalLength;

    private List<Vehicle> Occupants(Vehicle v)
        => v.Lane is { } laneId ? _laneVehicles[laneId] : _connectorVehicles[v.Crossing!.Value];

    private void RemoveFromQueues(Vehicle v)
    {
        if (v.Lane is { } laneId && _laneVehicles.TryGetValue(laneId, out var lq))
            lq.Remove(v);
        if (v.Crossing is { } cr && _connectorVehicles.TryGetValue(cr, out var cq))
            cq.Remove(v);
        if (v.ChangeFrom is { } from && _laneVehicles.TryGetValue(from, out var fq))
            fq.Remove(v);
    }

    private static void SortQueue(List<Vehicle> queue)
        => queue.Sort(static (a, b) => b.S.CompareTo(a.S));

    // ------------------------------------------------------------------- pose

    /// <summary>World position (vehicle centre) and heading for rendering/tests.
    /// S is the front bumper, so the centre trails by half a car length — including
    /// across segment boundaries: while the centre is still behind the current
    /// segment's start it renders on the segment the vehicle just left, so poses
    /// never jump at junctions (or dead-end u-turns).</summary>
    public (Vector3 Pos, Vector3 Forward) Pose(Vehicle v)
    {
        float sMid = v.S - Vehicle.Length / 2;

        if (sMid < 0)
        {
            if (v.PrevCrossing is { } pcr && _connectorLength.ContainsKey(pcr))
                return PoseOnConnector(pcr, MathF.Max(0, v.PrevLength + sMid));
            if (v.PrevLane is { } prev && _runs.ContainsKey(prev))
                return PoseOnLane(prev, v.PrevLength + sMid, _lanes[prev].Offset);
            sMid = 0; // fresh spawn: no history yet
        }

        if (v.Lane is { } laneId)
        {
            var lane = _lanes[laneId];
            float offset = lane.Offset;
            if (v.ChangeFrom is { } from)
            {
                float p = v.ChangeProgress;
                float smooth = p * p * (3 - 2 * p);
                offset = _lanes[from].Offset + (lane.Offset - _lanes[from].Offset) * smooth;
            }
            return PoseOnLane(laneId, sMid, offset);
        }
        return PoseOnConnector(v.Crossing!.Value, sMid);
    }

    private (Vector3 Pos, Vector3 Forward) PoseOnLane(LaneId laneId, float s, float offset)
    {
        var run = _runs[laneId];
        var edge = _network.Edges[run.Edge];
        float d = Math.Clamp(run.Forward ? run.DStart + s : run.DStart - s,
            0f, edge.ArcLength.TotalLength);
        float t = edge.ArcLength.TAtDistance(d);
        var pos = edge.Curve.OffsetPoint(t, offset);
        var fwd = edge.Curve.Tangent(t);
        return (pos, run.Forward ? fwd : -fwd);
    }

    private (Vector3 Pos, Vector3 Forward) PoseOnConnector((NodeId Node, int Connector) key, float s)
    {
        var curve = _network.Nodes[key.Node].Connectors[key.Connector].Curve;
        float tc = _connectorLength[key].TAtDistance(MathF.Min(s, _connectorLength[key].TotalLength));
        return (curve.Point(tc), curve.Tangent(tc));
    }
}
