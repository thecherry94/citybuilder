using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Godot;

namespace CityBuilder.Game;

/// <summary>
/// Street furniture for controlled junctions: yield/stop/priority signs and traffic
/// light poles, one per incoming approach, placed on the driver's right at the cut.
/// Placeholder procedural meshes — committed as extra surfaces onto the node's mesh.
/// </summary>
public static class JunctionProps
{
    private const float SignPoleHeight = 2.4f;
    private const float LightPoleHeight = 4.0f;
    private const float PoleRadius = 0.06f;
    private const float PlateThickness = 0.04f;

    public static void Build(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges, ArrayMesh mesh)
    {
        if (node.Edges.Count < 3)
            return;
        var control = JunctionControl.Resolve(node, edges);
        if (control.Mode == JunctionControlMode.None)
            return;

        var buckets = new Dictionary<StandardMaterial3D, SurfaceTool>();

        foreach (var (edgeId, basePos, facing) in ApproachAnchors(node, edges))
        {
            switch (control.Mode)
            {
                case JunctionControlMode.TrafficLights:
                    AddTrafficLight(buckets, basePos, facing);
                    break;
                case JunctionControlMode.AllWayStop:
                    AddStopSign(buckets, basePos, facing);
                    break;
                case JunctionControlMode.PrioritySigns:
                    switch (control.Roles.GetValueOrDefault(edgeId))
                    {
                        case LegRole.Main: AddPrioritySign(buckets, basePos, facing); break;
                        case LegRole.Yield: AddYieldSign(buckets, basePos, facing); break;
                        case LegRole.Stop: AddStopSign(buckets, basePos, facing); break;
                    }
                    break;
            }
        }

        foreach (var st in buckets.Values)
        {
            st.Index();
            st.Commit(mesh);
        }
    }

    /// <summary>Per incoming approach: prop anchor on the driver's right at the cut,
    /// and the facing direction (toward the approaching driver). Shared with the
    /// animated signal lamp view so lamps sit exactly on the prop poles.</summary>
    public static IEnumerable<(EdgeId Leg, Vector3 BasePos, Vector3 Facing)> ApproachAnchors(
        RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)
    {
        foreach (var edgeId in node.Edges)
        {
            var edge = edges[edgeId];
            bool startsHere = edge.StartNode == node.Id;
            var type = RoadCatalog.Get(edge.Type);

            bool hasIncoming = edge.Lanes.Any(l => l.Kind == LaneKind.Driving
                && (l.Direction == LaneDirection.Forward ? !startsHere : startsHere));
            if (!hasIncoming)
                continue;

            float tCut = node.Junction.CutT.TryGetValue(edgeId, out var t) ? t : (startsHere ? 0f : 1f);
            float sideDist = type.HasSidewalks ? type.OuterHalf - 0.5f : type.CarriagewayHalf + 0.4f;
            var basePos = edge.Curve.OffsetPoint(tCut, startsHere ? -sideDist : sideDist).ToGodot();
            basePos.Y += MeshBuilders.SurfaceY + (type.HasSidewalks ? MeshBuilders.SidewalkRise : 0f); // relative (M8)

            var tangent = edge.Curve.Tangent(tCut).ToGodot();
            var facing = (startsHere ? tangent : -tangent).Normalized();
            yield return (edgeId, basePos, facing);
        }
    }

    /// <summary>The three lamp centres (red, amber, green) of a signal head anchored
    /// at the given pole position.</summary>
    public static Vector3[] SignalLampCenters(Vector3 basePos, Vector3 facing)
    {
        var center = basePos + Vector3.Up * (LightPoleHeight - 0.75f) + facing * (PoleRadius + 0.13f);
        var result = new Vector3[3];
        for (int i = 0; i < 3; i++)
            result[i] = center + Vector3.Up * (0.28f - i * 0.28f) + facing * 0.115f;
        return result;
    }

    // ------------------------------------------------------------------- props

    private static void AddYieldSign(Dictionary<StandardMaterial3D, SurfaceTool> b, Vector3 basePos, Vector3 facing)
    {
        AddPole(b, basePos, SignPoleHeight);
        var c = basePos + Vector3.Up * (SignPoleHeight - 0.45f) + facing * (PoleRadius + PlateThickness);
        var right = facing.Cross(Vector3.Up).Normalized();
        // point-down triangle, red plate with a smaller white inset
        AddTriPlate(b, Materials.SignRed, c, right, 0.75f, facing);
        AddTriPlate(b, Materials.SignWhite, c + facing * 0.012f, right, 0.45f, facing);
    }

    private static void AddStopSign(Dictionary<StandardMaterial3D, SurfaceTool> b, Vector3 basePos, Vector3 facing)
    {
        AddPole(b, basePos, SignPoleHeight);
        var c = basePos + Vector3.Up * (SignPoleHeight - 0.4f) + facing * (PoleRadius + PlateThickness);
        var right = facing.Cross(Vector3.Up).Normalized();
        AddNGonPlate(b, Materials.SignRed, c, right, radius: 0.4f, sides: 8, facing, rotate: Mathf.Pi / 8);
        AddNGonPlate(b, Materials.SignWhite, c + facing * 0.012f, right, 0.28f, 8, facing, Mathf.Pi / 8);
    }

    private static void AddPrioritySign(Dictionary<StandardMaterial3D, SurfaceTool> b, Vector3 basePos, Vector3 facing)
    {
        AddPole(b, basePos, SignPoleHeight);
        var c = basePos + Vector3.Up * (SignPoleHeight - 0.4f) + facing * (PoleRadius + PlateThickness);
        var right = facing.Cross(Vector3.Up).Normalized();
        // yellow diamond with white inset
        AddNGonPlate(b, Materials.SignWhite, c, right, 0.42f, 4, facing, 0f);
        AddNGonPlate(b, Materials.SignYellow, c + facing * 0.012f, right, 0.30f, 4, facing, 0f);
    }

    private static void AddTrafficLight(Dictionary<StandardMaterial3D, SurfaceTool> b, Vector3 basePos, Vector3 facing)
    {
        // housing only — the lamps are animated MeshInstances (SignalLampView)
        AddPole(b, basePos, LightPoleHeight);
        var right = facing.Cross(Vector3.Up).Normalized();
        var center = basePos + Vector3.Up * (LightPoleHeight - 0.75f) + facing * (PoleRadius + 0.13f);
        AddBox(b, Materials.PropMetal, center, right * 0.16f, Vector3.Up * 0.46f, facing * 0.11f);
    }

    // --------------------------------------------------------------- primitives

    private static void AddPole(Dictionary<StandardMaterial3D, SurfaceTool> b, Vector3 basePos, float height)
    {
        var st = Bucket(b, Materials.PropMetal);
        const int sides = 8;
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides;
            float a1 = Mathf.Tau * (i + 1) / sides;
            var r0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * PoleRadius;
            var r1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * PoleRadius;
            var n = (r0 + r1).Normalized();
            Quad(st, basePos + r0, basePos + r1, basePos + r1 + Vector3.Up * height,
                basePos + r0 + Vector3.Up * height, n);
        }
        // top cap
        var top = basePos + Vector3.Up * height;
        var stTop = Bucket(b, Materials.PropMetal);
        for (int i = 1; i < sides - 1; i++)
        {
            float a0 = Mathf.Tau * 0 / sides, ai = Mathf.Tau * i / sides, aj = Mathf.Tau * (i + 1) / sides;
            Tri(stTop,
                top + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * PoleRadius,
                top + new Vector3(Mathf.Cos(ai), 0, Mathf.Sin(ai)) * PoleRadius,
                top + new Vector3(Mathf.Cos(aj), 0, Mathf.Sin(aj)) * PoleRadius,
                Vector3.Up);
        }
    }

    /// <summary>Regular n-gon plate centered at c in the plane spanned by right/up,
    /// facing `normal` (double-sided material).</summary>
    private static void AddNGonPlate(Dictionary<StandardMaterial3D, SurfaceTool> b, StandardMaterial3D mat,
        Vector3 c, Vector3 right, float radius, int sides, Vector3 normal, float rotate)
    {
        var st = Bucket(b, mat);
        var up = Vector3.Up;
        Vector3 P(int i)
        {
            float a = Mathf.Tau * i / sides + rotate + Mathf.Pi / 2; // vertex at top
            return c + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * radius;
        }
        for (int i = 1; i < sides - 1; i++)
            Tri(st, P(0), P(i), P(i + 1), normal);
    }

    /// <summary>Point-down triangle plate (yield shape), side length s.</summary>
    private static void AddTriPlate(Dictionary<StandardMaterial3D, SurfaceTool> b, StandardMaterial3D mat,
        Vector3 c, Vector3 right, float s, Vector3 normal)
    {
        var st = Bucket(b, mat);
        float h = s * 0.866f;
        var top = c + Vector3.Up * (h / 2);
        Tri(st, top - right * (s / 2), top + right * (s / 2), c - Vector3.Up * (h / 2), normal);
    }

    private static void AddBox(Dictionary<StandardMaterial3D, SurfaceTool> b, StandardMaterial3D mat,
        Vector3 c, Vector3 hx, Vector3 hy, Vector3 hz)
    {
        var st = Bucket(b, mat);
        var nx = hx.Normalized(); var ny = hy.Normalized(); var nz = hz.Normalized();
        Quad(st, c - hx - hy + hz, c + hx - hy + hz, c + hx + hy + hz, c - hx + hy + hz, nz);
        Quad(st, c - hx - hy - hz, c + hx - hy - hz, c + hx + hy - hz, c - hx + hy - hz, -nz);
        Quad(st, c + hx - hy - hz, c + hx + hy - hz, c + hx + hy + hz, c + hx - hy + hz, nx);
        Quad(st, c - hx - hy - hz, c - hx + hy - hz, c - hx + hy + hz, c - hx - hy + hz, -nx);
        Quad(st, c - hx + hy - hz, c + hx + hy - hz, c + hx + hy + hz, c - hx + hy + hz, ny);
        Quad(st, c - hx - hy - hz, c + hx - hy - hz, c + hx - hy + hz, c - hx - hy + hz, -ny);
    }

    private static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
    {
        Tri(st, a, b, c, n);
        Tri(st, a, c, d, n);
    }

    /// <summary>Triangle with winding normalized so the Godot front face (clockwise)
    /// looks along +n.</summary>
    private static void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 n)
    {
        if ((c - a).Cross(b - a).Dot(n) < 0)
            (b, c) = (c, b);
        st.SetNormal(n);
        st.AddVertex(a);
        st.SetNormal(n);
        st.AddVertex(b);
        st.SetNormal(n);
        st.AddVertex(c);
    }

    private static SurfaceTool Bucket(Dictionary<StandardMaterial3D, SurfaceTool> b, StandardMaterial3D mat)
    {
        if (b.TryGetValue(mat, out var st))
            return st;
        st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetMaterial(mat);
        b[mat] = st;
        return st;
    }
}
