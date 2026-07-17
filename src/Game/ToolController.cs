using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

public enum ToolMode { Straight, SimpleCurve, ComplexCurve, Arc, Continuous, Grid, Bulldoze, Inspect, SpawnVehicle }

/// <summary>Thin adapter: raycasts input into the domain DraftSession and renders its
/// ghost state. All world mutations flow through the session (roads) or
/// RoadNetwork.RemoveEdge (bulldoze).</summary>
public partial class ToolController : Node
{
    private RoadNetwork _network = null!;
    private DraftSession _session = null!;
    private CameraRig _camera = null!;
    private GhostView _ghost = null!;
    private RoadNetworkView _view = null!;

    private ToolMode _mode = ToolMode.Straight;
    private EdgeId? _bulldozeTarget;
    private NodeId? _selectedNode;
    private CityBuilder.Domain.Traffic.TrafficSim? _traffic;
    private (EdgeId Edge, bool Forward)? _spawnOrigin;

    // CITYBUILDER_GHOSTPROBE=1: print avg RenderGhost cost every 300 calls — the
    // before/after evidence for the M6.75 ghost-pooling work (docs/health/M6.75.md)
    private static readonly bool GhostProbe = OS.GetEnvironment("CITYBUILDER_GHOSTPROBE") == "1";
    private long _probeTicks;
    private int _probeCount;

    public event Action<string>? StatusFlashed;
    public event Action<string>? ReadoutChanged;
    public event Action<NodeId?>? NodeSelected;

    public ToolMode Mode => _mode;
    public DraftSession Session => _session;

    public void BindTraffic(CityBuilder.Domain.Traffic.TrafficSim traffic) => _traffic = traffic;

    public void Bind(RoadNetwork network, DraftSession session, CameraRig camera,
        GhostView ghost, RoadNetworkView view)
    {
        _network = network;
        _session = session;
        _camera = camera;
        _ghost = ghost;
        _view = view;
        _session.Flashed += m => StatusFlashed?.Invoke(m);
    }

    public void SetMode(ToolMode mode)
    {
        _mode = mode;
        if (DraftModeOf(mode) is { } dm)
            _session.SetMode(dm);
        else
            _session.Cancel();
        _ghost.Clear();
        _view.HighlightEdge(null);
        _bulldozeTarget = null;
        _spawnOrigin = null;
        if (mode != ToolMode.Inspect)
            SelectNode(null);
    }

    private static DraftMode? DraftModeOf(ToolMode m) => m switch
    {
        ToolMode.Straight => DraftMode.Straight,
        ToolMode.SimpleCurve => DraftMode.QuadCurve,
        ToolMode.ComplexCurve => DraftMode.CubicCurve,
        ToolMode.Arc => DraftMode.Arc,
        ToolMode.Continuous => DraftMode.Chain,
        ToolMode.Grid => DraftMode.GridStamp,
        _ => null,
    };

    private bool IsRoadMode => DraftModeOf(_mode) is not null;

    private void SelectNode(NodeId? id)
    {
        if (_selectedNode == id)
            return;
        _selectedNode = id;
        NodeSelected?.Invoke(id);
    }

    public void SetRoadType(RoadTypeId type) => _session.RoadType = type;

    public void SetSnapType(SnapTypes flag, bool enabled)
        => _session.EnabledSnaps = enabled ? _session.EnabledSnaps | flag : _session.EnabledSnaps & ~flag;

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
                if (_camera.MouseGroundPoint() is { } down)
                    HandleMouseDownAt(down.ToNumerics());
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                if (IsRoadMode)
                    HandleMouseUpAt();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                StepBack();
                break;
            case InputEventKey { Keycode: Key.Escape, Pressed: true }:
                CancelGesture();
                break;
            case InputEventKey { Keycode: Key.Enter, Pressed: true }:
                ConfirmDraft();
                break;
            case InputEventKey { Keycode: Key.T, Pressed: true }:
                ReleaseTangentLock();
                break;
        }
    }

    // ---------------------------------------------------- world-space handlers

    /// <summary>Mouse-down: near a draft handle starts a drag, otherwise a click.</summary>
    public void HandleMouseDownAt(System.Numerics.Vector3 world)
    {
        if (IsRoadMode
            && _session.State != SessionState.Idle
            && _session.TryBeginHandleDrag(world, MathF.Max(3f, _camera.SnapRadius() * 0.6f)))
        {
            RenderGhost();
            return;
        }
        HandleClickAt(world);
    }

    public void HandleMouseUpAt()
    {
        _session.EndHandleDrag();
        RenderGhost();
    }

    public void ConfirmDraft()
    {
        if (!IsRoadMode)
            return;
        _session.Confirm();
        RenderGhost();
    }

    /// <summary>T key: release the G1 start-tangent lock on the current draft.</summary>
    public void ReleaseTangentLock()
    {
        if (!IsRoadMode)
            return;
        _session.ReleaseTangentLock();
        RenderGhost();
    }

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

        _session.PointerMoved(world, _camera.SnapRadius());
        RenderGhost();
    }

    public void HandleClickAt(System.Numerics.Vector3 world)
    {
        if (_mode == ToolMode.Inspect) { SelectNode(PickNode(world)); return; }
        if (_mode == ToolMode.SpawnVehicle) { HandleSpawnClick(world); return; }

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

        _session.Click(world, _camera.SnapRadius());
        RenderGhost();
    }

    public void StepBack()
    {
        if (!IsRoadMode)
            return;
        _session.StepBack();
        RenderGhost();
    }

    public void CancelGesture()
    {
        _session.Cancel();
        _ghost.Clear();
        ReadoutChanged?.Invoke("");
    }

    /// <summary>Drop every reference to network entities — draft, hover highlight,
    /// bulldoze/spawn targets, and the inspected node. Call after a load replaces the
    /// graph wholesale: surviving ids may describe entirely different geometry, and
    /// SetMode alone keeps the selection while in Inspect mode.</summary>
    public void ClearTransientState()
    {
        _session.Cancel();
        _ghost.Clear();
        _view.HighlightEdge(null);
        _bulldozeTarget = null;
        _spawnOrigin = null;
        SelectNode(null);
        ReadoutChanged?.Invoke("");
    }

    private void RenderGhost()
    {
        long t0 = GhostProbe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var handles = _session.Draft?.Handles.Select(h => h.Position).ToArray();
        var s = _session.LastSnap;
        System.Numerics.Vector3? edgeTan = null;
        if (s.Edge is { } eh && _network.Edges.TryGetValue(eh.Edge, out var hitEdge))
            edgeTan = hitEdge.Curve.Tangent(eh.T);
        System.Numerics.Vector3? anchor = null;
        if (_session.Draft is { } dft && dft.Handles.Count > 0)
            anchor = (_session.DraggingHandle > 0 ? dft.Handles[0] : dft.Handles[^1]).Position;
        _ghost.Show(_session.Ghost, s, handles, _session.DraggingHandle,
            edgeTan, _session.Draft?.StartTangent, anchor);
        if (GhostProbe)
        {
            _probeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (++_probeCount == 300)
            {
                GD.Print($"GHOSTPROBE avg_us={_probeTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency / 300} over 300 renders");
                _probeTicks = 0;
                _probeCount = 0;
            }
        }
        ReadoutChanged?.Invoke(_session.Readout is { } r
            ? r.RadiusM is { } rad && rad < 10000f
                ? $"{r.LengthM:0.#} m   {NormalizeDeg(r.AngleDeg):0.#}°   R {rad:0} m"
                : $"{r.LengthM:0.#} m   {NormalizeDeg(r.AngleDeg):0.#}°"
            : "");
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

    private static float NormalizeDeg(float deg)
    {
        deg %= 360f;
        if (deg < 0) deg += 360f;
        return deg;
    }
}
