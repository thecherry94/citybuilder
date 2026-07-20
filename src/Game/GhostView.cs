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
    private readonly List<MeshInstance3D> _structures = new();
    private readonly List<MeshInstance3D> _shadows = new();
    private readonly List<Label3D> _elevLabels = new();
    private readonly List<MeshInstance3D> _handles = new();
    private MeshInstance3D _lines = null!;
    private ImmediateMesh _linesMesh = null!;
    private MeshInstance3D _snapDot = null!;
    private MeshInstance3D _nodeRing = null!;
    private MeshInstance3D _edgeTick = null!;
    private Node3D _perpGlyph = null!;
    private MeshInstance3D _gridQuad = null!;
    private Label3D _angleLabel = null!;
    private readonly List<MeshInstance3D> _crossDots = new();
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
        _nodeRing = new MeshInstance3D
        {
            Name = "snap_node",
            Mesh = new TorusMesh { InnerRadius = 2.0f, OuterRadius = 2.7f },
            MaterialOverride = Materials.SnapNode,
            Visible = false,
        };
        AddChild(_nodeRing);
        _edgeTick = new MeshInstance3D
        {
            Name = "snap_edge",
            Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.15f, 5.5f) },
            MaterialOverride = Materials.SnapAccent,
            Visible = false,
        };
        AddChild(_edgeTick);
        _perpGlyph = new Node3D { Name = "snap_perp", Visible = false };
        _perpGlyph.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.35f, 0.15f, 3.0f) },
            Position = new Vector3(0, 0, 1.5f),
            MaterialOverride = Materials.SnapAccent,
        });
        _perpGlyph.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(3.0f, 0.15f, 0.35f) },
            Position = new Vector3(1.5f, 0, 0),
            MaterialOverride = Materials.SnapAccent,
        });
        AddChild(_perpGlyph);
        _gridQuad = new MeshInstance3D
        {
            Name = "snap_grid",
            Mesh = new BoxMesh { Size = new Vector3(1.6f, 0.1f, 1.6f) },
            MaterialOverride = Materials.SnapAccent,
            Visible = false,
        };
        AddChild(_gridQuad);
        _angleLabel = new Label3D
        {
            Name = "snap_angle",
            FontSize = 64,
            PixelSize = 0.05f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = new Color(0.55f, 0.85f, 1f),
            OutlineSize = 12,
            Visible = false,
        };
        AddChild(_angleLabel);
    }

    public void Clear()
    {
        HideFrom(_strips, 0);
        HideFrom(_structures, 0);
        HideFrom(_shadows, 0);
        HideLabelsFrom(_elevLabels, 0);
        HideFrom(_handles, 0);
        HideFrom(_crossDots, 0);
        _linesMesh.ClearSurfaces();
        _snapDot.Visible = false;
        _nodeRing.Visible = false;
        _edgeTick.Visible = false;
        _perpGlyph.Visible = false;
        _gridQuad.Visible = false;
        _angleLabel.Visible = false;
        _lastPlacement = null;
    }

    private static void HideFrom(List<MeshInstance3D> pool, int from)
    {
        for (int i = from; i < pool.Count; i++)
            pool[i].Visible = false;
    }

    private static void HideLabelsFrom(List<Label3D> pool, int from)
    {
        for (int i = from; i < pool.Count; i++)
            pool[i].Visible = false;
    }

    private Label3D PooledLabel(ref int used)
    {
        if (used == _elevLabels.Count)
        {
            var l = new Label3D
            {
                FontSize = 56,
                PixelSize = 0.05f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(0.55f, 0.85f, 1f),
                OutlineSize = 12,
            };
            AddChild(l);
            _elevLabels.Add(l);
        }
        var node = _elevLabels[used++];
        node.Visible = true;
        return node;
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
        IReadOnlyList<System.Numerics.Vector3>? handles = null, int hotHandle = -1,
        System.Numerics.Vector3? edgeTangent = null,
        System.Numerics.Vector3? referenceDir = null,
        System.Numerics.Vector3? anchor = null)
    {
        ShowIndicator(snap, edgeTangent);

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
            ShowGuideCrossings(snap.ActiveGuidelines);
        }
        else
        {
            HideFrom(_crossDots, 0);
        }

        // cell ticks: length landed on the 8 m rhythm — cross-ticks along the last
        // stretch toward the anchor (CellLength snap, or Angle with quantized length)
        if (anchor is { } anch
            && snap.Kind is SnapKind.CellLength or SnapKind.Angle
            && System.Numerics.Vector3.Distance(snap.Position, anch) is > 1f and var len
            && MathF.Abs(len / 8f - MathF.Round(len / 8f)) < 0.01f)
        {
            if (!anyLines)
            {
                _linesMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                anyLines = true;
            }
            var dirN = System.Numerics.Vector3.Normalize(snap.Position - anch);
            var side = (new System.Numerics.Vector3(dirN.Z, 0, -dirN.X) * 0.9f).ToGodot();
            int ticks = (int)MathF.Round(len / 8f);
            var col = new Color(0.55f, 0.8f, 1f, 0.9f);
            for (int k = Math.Max(1, ticks - 4); k <= ticks; k++)
            {
                var p = (anch + dirN * (k * 8f)).ToGodot() + Vector3.Up * 0.25f;
                _linesMesh.SurfaceSetColor(col);
                _linesMesh.SurfaceAddVertex(p + side);
                _linesMesh.SurfaceSetColor(col);
                _linesMesh.SurfaceAddVertex(p - side);
            }
        }

        if (placement is not null)
        {
            if (!ReferenceEquals(placement, _lastPlacement))
            {
                int used = 0;
                int usedStructures = 0, usedShadows = 0, usedLabels = 0;
                var material = placement.IsValid ? Materials.GhostValid : Materials.GhostInvalid;
                float width = RoadCatalog.Get(placement.Proposal.Type).Width;
                var labelled = new List<Vector3>();
                foreach (var pc in placement.Proposal.Curves)
                {
                    var mesh = MeshBuilders.BuildGhostStrip(pc.Curve, width);
                    if (mesh is null)
                        continue;
                    var inst = Pooled(_strips, ref used);
                    inst.Mesh = mesh;
                    inst.MaterialOverride = material;

                    bool elevated = pc.Curve.P0.Y > 0.5f || pc.Curve.P1.Y > 0.5f
                        || pc.Curve.P2.Y > 0.5f || pc.Curve.P3.Y > 0.5f;
                    if (elevated)
                    {
                        // the exact structures a commit would produce (same mesher)
                        var arc = new CityBuilder.Domain.Geometry.ArcLengthTable(pc.Curve);
                        if (StructureView.BuildStructures(pc.Curve, arc, width) is { } structMesh)
                        {
                            var s = Pooled(_structures, ref usedStructures);
                            s.Mesh = structMesh;
                            s.MaterialOverride = material;
                        }

                        // ground footprint: the curve flattened to Y=0 (a cubic's Y
                        // is bounded by its control net, so this is the exact XZ shadow)
                        var flat = new CityBuilder.Domain.Geometry.Bezier3(
                            pc.Curve.P0 with { Y = 0 }, pc.Curve.P1 with { Y = 0 },
                            pc.Curve.P2 with { Y = 0 }, pc.Curve.P3 with { Y = 0 });
                        if (MeshBuilders.BuildGhostStrip(flat, width) is { } shadowMesh)
                        {
                            var sh = Pooled(_shadows, ref usedShadows);
                            sh.Mesh = shadowMesh;
                            sh.MaterialOverride = Materials.GhostShadow;
                        }

                        AddElevationLabel(pc.Curve.P0, labelled, ref usedLabels);
                        AddElevationLabel(pc.Curve.P3, labelled, ref usedLabels);
                    }
                }
                HideFrom(_strips, used);
                HideFrom(_structures, usedStructures);
                HideFrom(_shadows, usedShadows);
                HideLabelsFrom(_elevLabels, usedLabels);
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
            HideFrom(_structures, 0);
            HideFrom(_shadows, 0);
            HideLabelsFrom(_elevLabels, 0);
        }
        _lastPlacement = placement;

        if (anyLines)
            _linesMesh.SurfaceEnd();

        ShowHandles(handles, hotHandle);
    }

    /// <summary>One indicator per snap kind — the user always sees WHAT they are
    /// snapped to (spec §4): node = lock ring, edge = tick across the road,
    /// perpendicular = right-angle glyph, grid = cell quad, angle = degree badge,
    /// guides/cell ticks = the plain dot at the snapped tip.</summary>
    private void ShowIndicator(SnapResult snap, System.Numerics.Vector3? edgeTangent)
    {
        _nodeRing.Visible = false;
        _edgeTick.Visible = false;
        _perpGlyph.Visible = false;
        _gridQuad.Visible = false;
        _angleLabel.Visible = false;
        _snapDot.Visible = false;
        var pos = snap.Position.ToGodot() + Vector3.Up * 0.25f;
        switch (snap.Kind)
        {
            case SnapKind.Node:
                _nodeRing.Visible = true;
                _nodeRing.Position = pos;
                break;
            case SnapKind.Edge when edgeTangent is { } tan:
                _edgeTick.Visible = true;
                _edgeTick.Position = pos;
                _edgeTick.Rotation = new Vector3(0, MathF.Atan2(tan.X, tan.Z) + MathF.PI / 2, 0);
                break;
            case SnapKind.Edge:
                ShowSnapDot(pos);
                break;
            case SnapKind.Perpendicular when snap.DirectionConstraint is { } arrive:
                _perpGlyph.Visible = true;
                _perpGlyph.Position = pos;
                _perpGlyph.Rotation = new Vector3(0, MathF.Atan2(arrive.X, arrive.Z), 0);
                break;
            case SnapKind.GridPoint or SnapKind.GridLine:
                _gridQuad.Visible = true;
                _gridQuad.Position = pos;
                break;
            case SnapKind.Angle when snap.SnappedAngleDeg is { } deg:
                _angleLabel.Visible = true;
                _angleLabel.Position = pos + Vector3.Up * 2.5f;
                _angleLabel.Text = $"{NormalizeDeg(deg):0}°";
                ShowSnapDot(pos);
                break;
            case SnapKind.GuidelineIntersection or SnapKind.Guideline or SnapKind.CellLength
                or SnapKind.Perpendicular:
                ShowSnapDot(pos);
                break;
        }
    }

    /// <summary>"⬆/⬇ N m" badge over an elevated or dug ghost endpoint. Dedupes shared
    /// joints of chained curves by proximity (1 m).</summary>
    private void AddElevationLabel(System.Numerics.Vector3 p, List<Vector3> labelled, ref int used)
    {
        if (MathF.Abs(p.Y) <= 0.5f)
            return;
        var pos = p.ToGodot();
        foreach (var seen in labelled)
            if (pos.DistanceTo(seen) < 1f)
                return;
        labelled.Add(pos);
        var l = PooledLabel(ref used);
        // below-ground badges float just above the ground plane, not the deck
        l.Position = (p.Y > 0 ? pos : pos with { Y = 0 }) + Vector3.Up * (p.Y > 0 ? 4f : 1.5f);
        l.Text = p.Y > 0 ? $"⬆ {p.Y:0} m" : $"⬇ {-p.Y:0} m";
    }

    private void ShowSnapDot(Vector3 pos)
    {
        _snapDot.Visible = true;
        _snapDot.Position = pos + Vector3.Up * 0.15f;
    }

    private static float NormalizeDeg(float deg)
    {
        deg %= 360f;
        if (deg < 0) deg += 360f;
        return deg;
    }

    /// <summary>Dots where active guides cross — the snappable intersections
    /// (CS2 shows these so you can aim for them).</summary>
    private void ShowGuideCrossings(IReadOnlyList<Guideline> guides)
    {
        int used = 0;
        for (int i = 0; i < guides.Count; i++)
        for (int j = i + 1; j < guides.Count; j++)
        {
            var a = guides[i];
            var b = guides[j];
            if (!GuidesCross(a, b, out float u))
                continue;
            var dot = Pooled(_crossDots, ref used);
            dot.Mesh ??= new CylinderMesh { TopRadius = 0.7f, BottomRadius = 0.7f, Height = 0.15f };
            dot.MaterialOverride = Materials.SnapAccent;
            dot.Position = a.PointAt(u * a.Length).ToGodot() + Vector3.Up * 0.3f;
        }
        HideFrom(_crossDots, used);
    }

    /// <summary>XZ segment intersection of two guides; u = normalized position on a.</summary>
    private static bool GuidesCross(Guideline a, Guideline b, out float u)
    {
        u = 0;
        float ax = a.Direction.X * a.Length, az = a.Direction.Z * a.Length;
        float bx = b.Direction.X * b.Length, bz = b.Direction.Z * b.Length;
        float denom = ax * bz - az * bx;
        if (MathF.Abs(denom) < 1e-6f)
            return false;
        float dx = b.Origin.X - a.Origin.X, dz = b.Origin.Z - a.Origin.Z;
        u = (dx * bz - dz * bx) / denom;
        float v = (dx * az - dz * ax) / denom;
        return u is >= 0 and <= 1 && v is >= 0 and <= 1;
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
