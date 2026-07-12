using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

public enum ToolMode { Straight, SimpleCurve, ComplexCurve, Continuous, Grid, Bulldoze }

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

    public event Action<string>? StatusFlashed;
    public event Action<string>? ReadoutChanged;

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
