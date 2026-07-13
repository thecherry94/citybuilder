using CityBuilder.Domain.Network;
using CityBuilder.Domain.Traffic;
using Godot;

namespace CityBuilder.Game;

/// <summary>Animated signal lamps: three small spheres per approach on every
/// lights-controlled junction, lit according to the sim's signal phase.</summary>
public partial class SignalLampView : Node3D
{
    private static readonly Mesh LampMesh = new SphereMesh
    {
        Radius = 0.11f,
        Height = 0.22f,
        RadialSegments = 12,
        Rings = 6,
    };

    private static readonly StandardMaterial3D LampOff = new()
    {
        AlbedoColor = new Color(0.10f, 0.10f, 0.11f),
        Roughness = 0.4f,
    };

    private RoadNetwork _network = null!;
    private TrafficSim _sim = null!;
    private readonly List<(NodeId Node, EdgeId Leg, MeshInstance3D[] Lamps)> _heads = new();
    private bool _dirty = true;
    private double _pollAccum;

    public void Bind(RoadNetwork network, TrafficSim sim)
    {
        _network = network;
        _sim = sim;
        network.Changed += _ => _dirty = true;
    }

    public override void _Process(double delta)
    {
        if (_dirty)
        {
            Rebuild();
            _dirty = false;
        }
        _pollAccum += delta;
        if (_pollAccum < 0.25)
            return;
        _pollAccum = 0;
        UpdatePhases();
    }

    private void Rebuild()
    {
        foreach (var child in GetChildren())
            child.QueueFree();
        _heads.Clear();
        _sim.EnsureSynced();

        foreach (var node in _network.Nodes.Values)
        {
            if (node.Edges.Count < 3)
                continue;
            if (JunctionControl.Resolve(node, _network.Edges).Mode != JunctionControlMode.TrafficLights)
                continue;
            foreach (var (leg, basePos, facing) in JunctionProps.ApproachAnchors(node, _network.Edges))
            {
                var centers = JunctionProps.SignalLampCenters(basePos, facing);
                var lamps = new MeshInstance3D[3];
                for (int i = 0; i < 3; i++)
                {
                    lamps[i] = new MeshInstance3D
                    {
                        Mesh = LampMesh,
                        MaterialOverride = LampOff,
                        Position = centers[i],
                    };
                    AddChild(lamps[i]);
                }
                _heads.Add((node.Id, leg, lamps));
            }
        }
        UpdatePhases();
    }

    private void UpdatePhases()
    {
        foreach (var (nodeId, leg, lamps) in _heads)
        {
            var phase = _sim.PhaseFor(nodeId, leg);
            lamps[0].MaterialOverride = phase == SignalPhase.Red ? Materials.LampRed : LampOff;
            lamps[1].MaterialOverride = phase == SignalPhase.Amber ? Materials.LampAmber : LampOff;
            lamps[2].MaterialOverride = phase == SignalPhase.Green ? Materials.LampGreen : LampOff;
        }
    }
}
