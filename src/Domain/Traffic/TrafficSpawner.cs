using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Traffic;

/// <summary>
/// Vehicle lifecycle: ambient spawning toward a target population (origins prefer the
/// network fringe), stuck-vehicle replanning, and route revalidation after network
/// edits. Replanning is a fresh A* from the vehicle's current edge — cheap and rare.
/// </summary>
public sealed partial class TrafficSim
{
    private const float SpawnCooldownSec = 0.25f;
    private const float StuckReplanSec = 20f;

    private readonly List<EdgeId> _fringeEdges = new();
    private readonly List<EdgeId> _allEdges = new();
    private float _spawnCooldown;

    /// <summary>Ambient traffic: the sim spawns until this many vehicles exist.</summary>
    public int TargetPopulation { get; set; }

    private void SpawnerTick(float dt)
    {
        ReplanStuck();

        _spawnCooldown -= dt;
        if (_spawnCooldown > 0 || _vehicles.Count >= TargetPopulation || _allEdges.Count < 2)
            return;
        _spawnCooldown = SpawnCooldownSec;

        var origins = _fringeEdges.Count > 0 ? _fringeEdges : _allEdges;
        var from = origins[_rng.Next(origins.Count)];
        var to = _allEdges[_rng.Next(_allEdges.Count)];
        if (to == from)
            return;
        Spawn(from, _rng.Next(2) == 0, to);
    }

    /// <summary>Front-of-queue vehicles stalled too long get a fresh route; if no
    /// route exists, they give up and despawn.</summary>
    private void ReplanStuck()
    {
        for (int i = _vehicles.Count - 1; i >= 0; i--)
        {
            var v = _vehicles[i];
            if (v.StuckTime < StuckReplanSec || v.Lane is not { } laneId)
                continue;
            if (_laneVehicles[laneId].Count > 0 && _laneVehicles[laneId][0] != v)
                continue; // queued behind someone else's problem
            v.StuckTime = 0;
            if (!Replan(v))
            {
                RemoveFromQueues(v);
                _vehicles.RemoveAt(i);
            }
        }
    }

    private bool Replan(Vehicle v)
    {
        if (v.Lane is not { } laneId)
            return true;
        var run = _runs[laneId];
        var goal = v.Route.Steps[^1].Edge;
        var route = RoutePlanner.Plan(_network, run.Edge, run.Forward, goal);
        if (route is null)
            return false;
        v.Route = route;
        v.StepIndex = 0;
        v.PlannedConnector = PickConnector(v, laneId);
        return true;
    }

    /// <summary>After a network edit: rebuild spawn pools, drop vehicles caught inside
    /// rebuilt junctions (connector indices are no longer meaningful), and replan any
    /// route that references a removed edge.</summary>
    private void RevalidateAfterNetworkChange()
    {
        _fringeEdges.Clear();
        _allEdges.Clear();
        foreach (var edge in _network.Edges.Values)
        {
            _allEdges.Add(edge.Id);
            bool fringe =
                (_network.Nodes.TryGetValue(edge.StartNode, out var sn) && sn.Edges.Count == 1)
                || (_network.Nodes.TryGetValue(edge.EndNode, out var en) && en.Edges.Count == 1);
            if (fringe)
                _fringeEdges.Add(edge.Id);
        }
        _allEdges.Sort((a, b) => a.Value.CompareTo(b.Value));
        _fringeEdges.Sort((a, b) => a.Value.CompareTo(b.Value));

        for (int i = _vehicles.Count - 1; i >= 0; i--)
        {
            var v = _vehicles[i];
            if (v.Crossing is not null)
            {
                RemoveFromQueues(v);
                _vehicles.RemoveAt(i);
                continue;
            }
            bool broken = false;
            for (int s = v.StepIndex; s < v.Route.Steps.Count && !broken; s++)
                broken = !_network.Edges.ContainsKey(v.Route.Steps[s].Edge);
            if (broken && !Replan(v))
            {
                RemoveFromQueues(v);
                _vehicles.RemoveAt(i);
                continue;
            }
            // connector indices are per-rebuild: recompute even for intact routes,
            // and drop pose history pointing at rebuilt geometry
            v.PrevCrossing = null;
            v.PrevLane = null;
            if (v.Lane is { } laneId)
                v.PlannedConnector = PickConnector(v, laneId);
        }
        foreach (var queue in _connectorVehicles.Values)
            queue.Clear();
    }
}
