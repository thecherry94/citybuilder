using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

/// <summary>Renders the placement preview: ghost road strips (blue = valid, red =
/// invalid), dashed guide lines, crossing markers, and the snap indicator. All scene
/// nodes are pooled — hidden rather than freed — so continuous mouse motion never
/// allocates or frees nodes; strip meshes rebuild only when the validated placement
/// actually changed (the session emits a fresh instance whenever geometry or
/// validity moved, so reference identity is the dirty flag).</summary>
public partial class GhostView : Node3D
{
    private readonly List<MeshInstance3D> _strips = new();
    private readonly List<MeshInstance3D> _handles = new();
    private MeshInstance3D _lines = null!;
    private ImmediateMesh _linesMesh = null!;
    private MeshInstance3D _snapDot = null!;
    private ValidatedPlacement? _lastPlacement;

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
        HideFrom(_strips, 0);
        HideFrom(_handles, 0);
        _linesMesh.ClearSurfaces();
        _snapDot.Visible = false;
        _lastPlacement = null;
    }

    private static void HideFrom(List<MeshInstance3D> pool, int from)
    {
        for (int i = from; i < pool.Count; i++)
            pool[i].Visible = false;
    }

    private MeshInstance3D Pooled(List<MeshInstance3D> pool, ref int used)
    {
        if (used == pool.Count)
        {
            var inst = new MeshInstance3D();
            AddChild(inst);
            pool.Add(inst);
        }
        var node = pool[used++];
        node.Visible = true;
        return node;
    }

    public void Show(ValidatedPlacement? placement, SnapResult snap,
        IReadOnlyList<System.Numerics.Vector3>? handles = null, int hotHandle = -1)
    {
        // snap indicator
        _snapDot.Visible = snap.Kind != SnapKind.Free;
        if (_snapDot.Visible)
            _snapDot.Position = snap.Position.ToGodot() + Vector3.Up * 0.4f;

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
            if (!ReferenceEquals(placement, _lastPlacement))
            {
                int used = 0;
                var material = placement.IsValid ? Materials.GhostValid : Materials.GhostInvalid;
                foreach (var pc in placement.Proposal.Curves)
                {
                    float width = RoadCatalog.Get(placement.Proposal.Type).Width;
                    var mesh = MeshBuilders.BuildGhostStrip(pc.Curve, width);
                    if (mesh is null)
                        continue;
                    var inst = Pooled(_strips, ref used);
                    inst.Mesh = mesh;
                    inst.MaterialOverride = material;
                }
                HideFrom(_strips, used);
            }

            // direction arrows: drawing an asymmetric type, show which way it will flow
            var ghostType = RoadCatalog.Get(placement.Proposal.Type);
            if (ghostType.IsDirectionAsymmetric)
            {
                if (!anyLines)
                {
                    _linesMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                    anyLines = true;
                }
                foreach (var pc in placement.Proposal.Curves)
                    AddGhostArrows(pc.Curve);
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
        else
        {
            HideFrom(_strips, 0);
        }
        _lastPlacement = placement;

        if (anyLines)
            _linesMesh.SurfaceEnd();

        ShowHandles(handles, hotHandle);
    }

    private void ShowHandles(IReadOnlyList<System.Numerics.Vector3>? handles, int hot)
    {
        int used = 0;
        if (handles is not null)
        {
            for (int i = 0; i < handles.Count; i++)
            {
                var inst = Pooled(_handles, ref used);
                inst.Mesh ??= new SphereMesh { Radius = 1.4f, Height = 2.8f };
                inst.MaterialOverride = i == hot ? Materials.SnapIndicator : Materials.GhostValid;
                inst.Position = handles[i].ToGodot() + Vector3.Up * 0.5f;
            }
        }
        HideFrom(_handles, used);
    }

    private void AddGhostArrows(CityBuilder.Domain.Geometry.Bezier3 curve)
    {
        var col = new Color(0.4f, 0.9f, 1f, 0.9f);
        float len = curve.Length();
        for (float d = 10f; d < len - 6f; d += 20f)
        {
            float t = d / len; // chord-parameter approximation is fine for a hint arrow
            var p = curve.Point(t).ToGodot() + Vector3.Up * 0.3f;
            var f = curve.Tangent(t).ToGodot();
            var right = new Vector3(f.Z, 0, -f.X);
            foreach (var wing in new[] { -right, right })
            {
                _linesMesh.SurfaceSetColor(col);
                _linesMesh.SurfaceAddVertex(p + f * 2.2f);
                _linesMesh.SurfaceSetColor(col);
                _linesMesh.SurfaceAddVertex(p + wing * 1.1f);
            }
            _linesMesh.SurfaceSetColor(col);
            _linesMesh.SurfaceAddVertex(p);
            _linesMesh.SurfaceSetColor(col);
            _linesMesh.SurfaceAddVertex(p + f * 2.2f);
        }
    }
}
