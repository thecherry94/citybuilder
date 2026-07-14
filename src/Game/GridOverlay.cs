using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

/// <summary>Faint snapping-grid lines around the cursor while grid snap is active.</summary>
public partial class GridOverlay : Node3D
{
    private const int HalfCells = 12;
    private ToolController _controller = null!;
    private CameraRig _camera = null!;
    private MeshInstance3D _inst = null!;
    private ImmediateMesh _mesh = null!;

    public void Bind(ToolController controller, CameraRig camera)
    {
        _controller = controller;
        _camera = camera;
    }

    public override void _Ready()
    {
        _mesh = new ImmediateMesh();
        _inst = new MeshInstance3D { Mesh = _mesh, MaterialOverride = Materials.DebugLines };
        AddChild(_inst);
    }

    public override void _Process(double delta)
    {
        var session = _controller.Session;
        bool on = (session.EnabledSnaps & SnapTypes.Grid) != 0
            && _camera.MouseGroundPoint() is not null;
        _inst.Visible = on;
        if (!on)
            return;
        var center = _camera.MouseGroundPoint()!.Value;
        float cs = session.Grid.CellSize;
        float cx = Mathf.Round(center.X / cs) * cs;
        float cz = Mathf.Round(center.Z / cs) * cs;
        float extent = HalfCells * cs;
        _mesh.ClearSurfaces();
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int i = -HalfCells; i <= HalfCells; i++)
        {
            // fade toward the rim
            float a = 0.35f * (1f - MathF.Abs(i) / (float)(HalfCells + 1));
            var col = new Color(1f, 1f, 1f, a);
            _mesh.SurfaceSetColor(col);
            _mesh.SurfaceAddVertex(new Vector3(cx + i * cs, 0.1f, cz - extent));
            _mesh.SurfaceSetColor(col);
            _mesh.SurfaceAddVertex(new Vector3(cx + i * cs, 0.1f, cz + extent));
            _mesh.SurfaceSetColor(col);
            _mesh.SurfaceAddVertex(new Vector3(cx - extent, 0.1f, cz + i * cs));
            _mesh.SurfaceSetColor(col);
            _mesh.SurfaceAddVertex(new Vector3(cx + extent, 0.1f, cz + i * cs));
        }
        _mesh.SurfaceEnd();
    }
}
