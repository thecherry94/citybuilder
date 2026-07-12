using CityBuilder.Domain.Network;
using Godot;

namespace CityBuilder.Game;

/// <summary>Debug view of the lane graph: green forward lanes, orange backward lanes,
/// cyan connectors, with direction arrowheads. Toggled from the toolbar.</summary>
public partial class LaneDebugOverlay : Node3D
{
    private static readonly Color Forward = new(0.2f, 0.95f, 0.3f);
    private static readonly Color Backward = new(1f, 0.6f, 0.15f);
    private static readonly Color Connector = new(0.2f, 0.85f, 1f);
    private const float Y = 0.18f;

    private RoadNetwork _network = null!;
    private ImmediateMesh _mesh = null!;
    private bool _dirty = true;

    public void Bind(RoadNetwork network)
    {
        _network = network;
        network.Changed += _ => _dirty = true;
        Visible = false;
    }

    public override void _Ready()
    {
        _mesh = new ImmediateMesh();
        AddChild(new MeshInstance3D { Mesh = _mesh, MaterialOverride = Materials.DebugLines });
    }

    public void SetShown(bool shown)
    {
        Visible = shown;
        if (shown)
            _dirty = true;
    }

    public override void _Process(double delta)
    {
        if (!Visible || !_dirty)
            return;
        _dirty = false;
        Rebuild();
    }

    private void Rebuild()
    {
        _mesh.ClearSurfaces();
        bool any = false;

        void EnsureBegun()
        {
            if (!any)
            {
                _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                any = true;
            }
        }

        foreach (var edge in _network.Edges.Values)
        foreach (var lane in edge.Lanes)
        {
            EnsureBegun();
            var color = lane.Kind switch
            {
                LaneKind.Bicycle => new Color(0.75f, 0.35f, 1f),
                LaneKind.Sidewalk => new Color(0.8f, 0.8f, 0.8f),
                _ => lane.Direction == LaneDirection.Forward ? Forward : Backward,
            };
            DrawCurve(t => edge.Curve.OffsetPoint(t, lane.Offset).ToGodot(), color,
                lane.Direction == LaneDirection.Backward);
        }

        foreach (var node in _network.Nodes.Values)
        foreach (var c in node.Connectors)
        {
            EnsureBegun();
            DrawCurve(t => c.Curve.Point(t).ToGodot(), Connector, reversed: false);
        }

        if (any)
            _mesh.SurfaceEnd();
    }

    private void DrawCurve(Func<float, Vector3> sample, Color color, bool reversed)
    {
        const int segments = 16;
        var up = Vector3.Up * Y;
        for (int i = 0; i < segments; i++)
        {
            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(sample(i / (float)segments) + up);
            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(sample((i + 1) / (float)segments) + up);
        }
        // arrowhead at 70% along travel direction
        float tHead = reversed ? 0.3f : 0.7f;
        float tBack = reversed ? 0.34f : 0.66f;
        var head = sample(tHead) + up;
        var back = sample(tBack) + up;
        var dir = head - back;
        if (dir.LengthSquared() < 1e-8f)
            return;
        dir = dir.Normalized();
        var side = dir.Cross(Vector3.Up) * 0.6f;
        foreach (var wing in new[] { back + side, back - side })
        {
            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(head);
            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(wing);
        }
    }
}
