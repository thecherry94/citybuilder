using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

public enum ToolMode { Straight, SimpleCurve, ComplexCurve, Continuous, Grid, Bulldoze, Inspect, SpawnVehicle }

/// <summary>Translates input into the domain tool state machines and keeps the ghost
/// preview in sync. All world mutations flow through RoadNetwork.Commit/RemoveEdge.</summary>
public partial class ToolController : Node
{
    private RoadNetwork _network = null!;
    private SnapService _snap = null!;
    private CameraRig _camera = null!;
    private GhostView _ghost = null!;
    private RoadNetworkView _view = null!;

    private readonly Dictionary<ToolMode, IPlacementTool> _tools = new()
    {
        [ToolMode.Straight] = new StraightTool(),
        [ToolMode.SimpleCurve] = new SimpleCurveTool(),
        [ToolMode.ComplexCurve] = new ComplexCurveTool(),
        [ToolMode.Continuous] = new ContinuousTool(),
        [ToolMode.Grid] = new GridTool(),
    };

    private ToolMode _mode = ToolMode.Straight;
    private SnapTypes _snapTypes = SnapTypes.All;
    private System.Numerics.Vector3? _anchor;
    private EdgeId? _bulldozeTarget;
    private NodeId? _selectedNode;
    private CityBuilder.Domain.Traffic.TrafficSim? _traffic;
    private (EdgeId Edge, bool Forward)? _spawnOrigin;

    public event Action<string>? StatusFlashed;
    public event Action<string>? ReadoutChanged;
    public event Action<NodeId?>? NodeSelected;

    public void BindTraffic(CityBuilder.Domain.Traffic.TrafficSim traffic) => _traffic = traffic;

    public ToolMode Mode => _mode;

    public void Bind(RoadNetwork network, SnapService snap, CameraRig camera, GhostView ghost, RoadNetworkView view)
    {
        _network = network;
        _snap = snap;
        _camera = camera;
        _ghost = ghost;
        _view = view;
    }

    public void SetMode(ToolMode mode)
    {
        CurrentTool?.Reset();
        _mode = mode;
        _anchor = null;
        _ghost.Clear();
        _view.HighlightEdge(null);
        _bulldozeTarget = null;
        _spawnOrigin = null;
        if (mode != ToolMode.Inspect)
            SelectNode(null);
    }

    private void SelectNode(NodeId? id)
    {
        if (_selectedNode == id)
            return;
        _selectedNode = id;
        NodeSelected?.Invoke(id);
    }

    public void SetRoadType(RoadTypeId type)
    {
        foreach (var tool in _tools.Values)
            tool.RoadType = type;
    }

    public void SetSnapType(SnapTypes flag, bool enabled)
        => _snapTypes = enabled ? _snapTypes | flag : _snapTypes & ~flag;

    private IPlacementTool? CurrentTool => _tools.GetValueOrDefault(_mode);

    // ------------------------------------------------------------------- input

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion:
                if (_camera.MouseGroundPoint() is { } hover)
                    HandleHoverAt(hover.ToNumerics());
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                if (_camera.MouseGroundPoint() is { } click)
                    HandleClickAt(click.ToNumerics());
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                StepBack();
                break;
            case InputEventKey { Keycode: Key.Escape, Pressed: true }:
                CancelGesture();
                break;
        }
    }

    // ---------------------------------------------------- world-space handlers

    public void HandleHoverAt(System.Numerics.Vector3 world)
    {
        if (_mode == ToolMode.Inspect)
        {
            _ghost.Clear();
            ReadoutChanged?.Invoke(PickNode(world) is null ? "" : "click to configure junction");
            return;
        }

        if (_mode == ToolMode.SpawnVehicle)
        {
            var hit = _network.FindClosestEdge(world, MathF.Max(6f, _camera.SnapRadius()));
            _view.HighlightEdge(hit?.id);
            ReadoutChanged?.Invoke(hit is null ? ""
                : _spawnOrigin is null ? "click origin road" : "click destination road");
            return;
        }

        if (_mode == ToolMode.Bulldoze)
        {
            var hit = _network.FindClosestEdge(world, MathF.Max(6f, _camera.SnapRadius()));
            _bulldozeTarget = hit?.id;
            _view.HighlightEdge(_bulldozeTarget);
            _ghost.Clear();
            ReadoutChanged?.Invoke(_bulldozeTarget is null ? "" : "click to demolish");
            return;
        }

        var tool = CurrentTool;
        if (tool is null)
            return;

        var snap = ResolveSnap(world);
        var proposal = tool.Preview(snap);
        ValidatedPlacement? validated = proposal is null ? null : _network.Validate(proposal);
        _ghost.Show(validated, snap);

        var readout = tool.Readout(snap);
        ReadoutChanged?.Invoke(readout is { } r
            ? $"{r.lengthM:0.#} m   {NormalizeDeg(r.angleDeg):0.#}°"
            : "");
    }

    public void HandleClickAt(System.Numerics.Vector3 world)
    {
        if (_mode == ToolMode.Inspect)
        {
            SelectNode(PickNode(world));
            return;
        }

        if (_mode == ToolMode.SpawnVehicle)
        {
            HandleSpawnClick(world);
            return;
        }

        if (_mode == ToolMode.Bulldoze)
        {
            HandleHoverAt(world); // refresh target under the cursor
            if (_bulldozeTarget is { } target)
            {
                _network.RemoveEdge(target);
                _view.HighlightEdge(null);
                _bulldozeTarget = null;
            }
            return;
        }

        var tool = CurrentTool;
        if (tool is null)
            return;

        var snap = ResolveSnap(world);
        var proposal = tool.AddClick(snap);
        _anchor = snap.Position;

        if (proposal is not null)
        {
            var validated = _network.Validate(proposal);
            if (validated.IsValid)
            {
                var result = _network.Commit(validated);
                if (!result.Success)
                    StatusFlashed?.Invoke(result.FailureReason ?? "could not build");
            }
            else
            {
                StatusFlashed?.Invoke("invalid placement: " + string.Join(", ", validated.Errors));
            }
            if (tool.ClickCount == 0)
                _anchor = null;
            _ghost.Clear();
        }

        HandleHoverAt(world);
    }

    public void StepBack()
    {
        var tool = CurrentTool;
        if (tool is null)
            return;
        tool.StepBack();
        if (tool.ClickCount == 0)
        {
            _anchor = null;
            _ghost.Clear();
        }
    }

    public void CancelGesture()
    {
        CurrentTool?.Reset();
        _anchor = null;
        _ghost.Clear();
    }

    /// <summary>Two-click vehicle spawn: origin road (travel direction from the
    /// clicked side, right-hand traffic), then destination road.</summary>
    private void HandleSpawnClick(System.Numerics.Vector3 world)
    {
        if (_traffic is null)
            return;
        var hit = _network.FindClosestEdge(world, MathF.Max(6f, _camera.SnapRadius()));
        if (hit is null)
            return;

        if (_spawnOrigin is null)
        {
            // clicked side decides direction: +offset side is forward-travel's right
            var edge = _network.Edges[hit.Value.id];
            var point = edge.Curve.Point(hit.Value.t);
            var normal = edge.Curve.NormalXZ(hit.Value.t);
            bool forward = System.Numerics.Vector3.Dot(world - point, normal) >= 0;
            _spawnOrigin = (hit.Value.id, forward);
            StatusFlashed?.Invoke("origin set — click destination road");
            return;
        }

        var origin = _spawnOrigin.Value;
        _spawnOrigin = null;
        var vehicle = _traffic.Spawn(origin.Edge, origin.Forward, hit.Value.id);
        StatusFlashed?.Invoke(vehicle is null ? "no route or entry blocked" : "vehicle dispatched");
    }

    /// <summary>Nearest node within a generous click radius; falls back to the closer
    /// endpoint of the closest edge so clicks inside a junction surface still land.</summary>
    private NodeId? PickNode(System.Numerics.Vector3 world)
    {
        float radius = MathF.Max(8f, _camera.SnapRadius());
        if (_network.FindNodeNear(world, radius) is { } direct)
            return direct;
        if (_network.FindClosestEdge(world, radius) is { } hit)
        {
            var edge = _network.Edges[hit.id];
            var sn = _network.Nodes[edge.StartNode];
            var en = _network.Nodes[edge.EndNode];
            return System.Numerics.Vector3.Distance(world, sn.Position)
                 <= System.Numerics.Vector3.Distance(world, en.Position) ? sn.Id : en.Id;
        }
        return null;
    }

    private SnapResult ResolveSnap(System.Numerics.Vector3 world)
        => _snap.Resolve(world, _camera.SnapRadius(), _snapTypes,
            new SnapContext(CurrentTool?.ClickCount > 0 ? _anchor : null, null));

    private static float NormalizeDeg(float deg)
    {
        deg %= 360f;
        if (deg < 0) deg += 360f;
        return deg;
    }
}
