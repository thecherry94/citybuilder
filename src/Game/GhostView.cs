using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

/// <summary>Renders the placement preview: ghost road strips (blue = valid, red =
/// invalid), guide lines, crossing markers, and the snap indicator.</summary>
public partial class GhostView : Node3D
{
    private readonly List<MeshInstance3D> _strips = new();
    private readonly List<MeshInstance3D> _handles = new();
    private MeshInstance3D _lines = null!;
    private ImmediateMesh _linesMesh = null!;
    private MeshInstance3D _snapDot = null!;

    public override void _Ready()
    {
        _linesMesh = new ImmediateMesh();
        _lines = new MeshInstance3D { Name = "guides", Mesh = _linesMesh, MaterialOverride = Materials.DebugLines };
        AddChild(_lines);
        _snapDot = new MeshInstance3D
        {
            Name = "snap",
            Mesh = new SphereMesh { Radius = 0.9f, Height = 1.8f },
            MaterialOverride = Materials.SnapIndicator,
            Visible = false,
        };
        AddChild(_snapDot);
    }

    public void Clear()
    {
        foreach (var s in _strips)
            s.QueueFree();
        _strips.Clear();
        foreach (var h in _handles)
            h.QueueFree();
        _handles.Clear();
        _linesMesh.ClearSurfaces();
        _snapDot.Visible = false;
    }

    public void Show(ValidatedPlacement? placement, SnapResult snap,
        IReadOnlyList<System.Numerics.Vector3>? handles = null, int hotHandle = -1)
    {
        Clear();

        // snap indicator
        if (snap.Kind != SnapKind.Free)
        {
            _snapDot.Visible = true;
            _snapDot.Position = snap.Position.ToGodot() + Vector3.Up * 0.4f;
        }

        bool anyLines = false;
        _linesMesh.ClearSurfaces();

        if (snap.ActiveGuidelines.Count > 0)
        {
            _linesMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
            anyLines = true;
            foreach (var g in snap.ActiveGuidelines)
            {
                // dashed: 4 m on, 4 m off
                for (float s = 0; s < g.Length; s += 8f)
                {
                    float s1 = MathF.Min(s + 4f, g.Length);
                    _linesMesh.SurfaceSetColor(new Color(1f, 0.85f, 0.2f, 0.8f));
                    _linesMesh.SurfaceAddVertex(g.PointAt(s).ToGodot() + Vector3.Up * 0.15f);
                    _linesMesh.SurfaceSetColor(new Color(1f, 0.85f, 0.2f, 0.8f));
                    _linesMesh.SurfaceAddVertex(g.PointAt(s1).ToGodot() + Vector3.Up * 0.15f);
                }
            }
        }

        if (placement is not null)
        {
            var material = placement.IsValid ? Materials.GhostValid : Materials.GhostInvalid;
            foreach (var pc in placement.Proposal.Curves)
            {
                float width = RoadCatalog.Get(placement.Proposal.Type).Width;
                var mesh = MeshBuilders.BuildGhostStrip(pc.Curve, width);
                if (mesh is null)
                    continue;
                var inst = new MeshInstance3D { Mesh = mesh, MaterialOverride = material };
                AddChild(inst);
                _strips.Add(inst);
            }

            // crossing markers
            if (placement.CrossingPoints.Count > 0)
            {
                if (!anyLines)
                {
                    _linesMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                    anyLines = true;
                }
                foreach (var p in placement.CrossingPoints)
                {
                    var c = p.ToGodot() + Vector3.Up * 0.3f;
                    var col = new Color(0.3f, 1f, 0.5f);
                    _linesMesh.SurfaceSetColor(col);
                    _linesMesh.SurfaceAddVertex(c + new Vector3(-2, 0, -2));
                    _linesMesh.SurfaceSetColor(col);
                    _linesMesh.SurfaceAddVertex(c + new Vector3(2, 0, 2));
                    _linesMesh.SurfaceSetColor(col);
                    _linesMesh.SurfaceAddVertex(c + new Vector3(-2, 0, 2));
                    _linesMesh.SurfaceSetColor(col);
                    _linesMesh.SurfaceAddVertex(c + new Vector3(2, 0, -2));
                }
            }
        }

        if (anyLines)
            _linesMesh.SurfaceEnd();

        ShowHandles(handles, hotHandle);
    }

    private void ShowHandles(IReadOnlyList<System.Numerics.Vector3>? handles, int hot)
    {
        foreach (var h in _handles)
            h.QueueFree();
        _handles.Clear();
        if (handles is null)
            return;
        for (int i = 0; i < handles.Count; i++)
        {
            var inst = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 1.4f, Height = 2.8f },
                MaterialOverride = i == hot ? Materials.SnapIndicator : Materials.GhostValid,
                Position = handles[i].ToGodot() + Vector3.Up * 0.5f,
            };
            AddChild(inst);
            _handles.Add(inst);
        }
    }
}
