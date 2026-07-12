using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Godot;

namespace CityBuilder.Game;

/// <summary>Procedural meshes for edges (asphalt strip + painted markings), junction
/// surfaces, and dead-end caps. All geometry sits slightly above Y=0; markings float
/// just above the asphalt to avoid z-fighting.</summary>
public static class MeshBuilders
{
    public const float SurfaceY = 0.07f;
    public const float MarkingY = 0.10f;
    private const float SkirtWidth = 0.3f;
    private const float ChordTolerance = 0.15f;
    private const float MarkingWidth = 0.15f;
    private const float DashOn = 3f, DashOff = 3f;
    private const float EdgeLineInset = 0.4f;

    /// <summary>Edge mesh between the junction cuts. Surface 0 = asphalt, surface 1
    /// (when present) = markings; assign materials by surface index.</summary>
    public static ArrayMesh? BuildEdgeMesh(RoadEdge edge, RoadType type, float tStart, float tEnd)
    {
        if (tEnd - tStart < 1e-4f)
            return null;

        var ts = SampleRange(edge.Curve, tStart, tEnd);
        float half = type.Width / 2;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i + 1 < ts.Count; i++)
        {
            var a = CrossSection(edge.Curve, ts[i], half);
            var b = CrossSection(edge.Curve, ts[i + 1], half);
            for (int q = 0; q + 1 < a.Length; q++)
            {
                AddQuad(st, a[q], a[q + 1], b[q + 1], b[q]);
            }
        }
        st.GenerateNormals();
        var mesh = st.Commit();

        var markings = BuildMarkings(edge, type, tStart, tEnd);
        if (markings is not null)
            markings.Commit(mesh);
        return mesh;
    }

    private static List<float> SampleRange(in Bezier3 curve, float tStart, float tEnd)
    {
        var ts = new List<float> { tStart };
        foreach (var t in BezierOps.Tessellate(curve, ChordTolerance))
            if (t > tStart + 1e-5f && t < tEnd - 1e-5f)
                ts.Add(t);
        ts.Add(tEnd);
        return ts;
    }

    private static Vector3[] CrossSection(in Bezier3 curve, float t, float half)
    {
        var up = Vector3.Up;
        return new[]
        {
            curve.OffsetPoint(t, -half - SkirtWidth).ToGodot(),
            curve.OffsetPoint(t, -half).ToGodot() + up * SurfaceY,
            curve.OffsetPoint(t, +half).ToGodot() + up * SurfaceY,
            curve.OffsetPoint(t, +half + SkirtWidth).ToGodot(),
        };
    }

    private static void AddQuad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
        st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
    }

    // ------------------------------------------------------------------ markings

    private static SurfaceTool? BuildMarkings(RoadEdge edge, RoadType type, float tStart, float tEnd)
    {
        var lines = MarkingLayout(type).ToList();
        if (lines.Count == 0)
            return null;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var (offset, dashed) in lines)
        {
            if (dashed)
                AddDashedLine(st, edge, offset, tStart, tEnd);
            else
                AddSolidLine(st, edge, offset, tStart, tEnd);
        }
        st.GenerateNormals();
        return st;
    }

    private static IEnumerable<(float offset, bool dashed)> MarkingLayout(RoadType type)
    {
        float half = type.Width / 2;
        if (type.Lanes.Count <= 2)
        {
            yield return (0f, true);                       // dashed center
        }
        else
        {
            yield return (-0.18f, false);                  // double solid center
            yield return (+0.18f, false);
            // dashed separators between same-direction lanes
            var offsets = type.Lanes.Select(l => l.Offset).OrderBy(o => o).ToArray();
            for (int i = 0; i + 1 < offsets.Length; i++)
                if (MathF.Sign(offsets[i]) == MathF.Sign(offsets[i + 1]))
                    yield return ((offsets[i] + offsets[i + 1]) / 2, true);
        }
        yield return (-(half - EdgeLineInset), false);     // solid side lines
        yield return (+(half - EdgeLineInset), false);
    }

    private static void AddSolidLine(SurfaceTool st, RoadEdge edge, float offset, float tStart, float tEnd)
    {
        var ts = SampleRange(edge.Curve, tStart, tEnd);
        for (int i = 0; i + 1 < ts.Count; i++)
            AddMarkQuad(st, edge, ts[i], ts[i + 1], offset);
    }

    private static void AddDashedLine(SurfaceTool st, RoadEdge edge, float offset, float tStart, float tEnd)
    {
        float dStart = edge.ArcLength.DistanceAtT(tStart);
        float dEnd = edge.ArcLength.DistanceAtT(tEnd);
        for (float d = dStart; d < dEnd; d += DashOn + DashOff)
        {
            float d1 = MathF.Min(d + DashOn, dEnd);
            float ta = edge.ArcLength.TAtDistance(d);
            float tb = edge.ArcLength.TAtDistance(d1);
            // short dash: two segments are plenty
            float tm = (ta + tb) / 2;
            AddMarkQuad(st, edge, ta, tm, offset);
            AddMarkQuad(st, edge, tm, tb, offset);
        }
    }

    private static void AddMarkQuad(SurfaceTool st, RoadEdge edge, float ta, float tb, float offset)
    {
        var up = Vector3.Up * MarkingY;
        float hw = MarkingWidth / 2;
        var a1 = edge.Curve.OffsetPoint(ta, offset - hw).ToGodot() + up;
        var a2 = edge.Curve.OffsetPoint(ta, offset + hw).ToGodot() + up;
        var b1 = edge.Curve.OffsetPoint(tb, offset - hw).ToGodot() + up;
        var b2 = edge.Curve.OffsetPoint(tb, offset + hw).ToGodot() + up;
        AddQuad(st, a1, a2, b2, b1);
    }

    // ------------------------------------------------------------------ junctions

    public static ArrayMesh? BuildJunctionMesh(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)
    {
        if (node.Edges.Count == 1)
            return BuildCapMesh(node, edges);

        var poly = node.Junction.SurfacePolygon;
        if (poly.Count < 3)
            return null;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var center = node.Position.ToGodot() + Vector3.Up * SurfaceY;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i].ToGodot() + Vector3.Up * SurfaceY;
            var b = poly[(i + 1) % poly.Count].ToGodot() + Vector3.Up * SurfaceY;
            st.AddVertex(center);
            st.AddVertex(a);
            st.AddVertex(b);
        }
        st.GenerateNormals();
        return st.Commit();
    }

    private static ArrayMesh? BuildCapMesh(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)
    {
        var edge = edges[node.Edges.First()];
        bool startsHere = edge.StartNode == node.Id;
        var leaving = (startsHere ? edge.Curve.Tangent(0) : -edge.Curve.Tangent(1)).ToGodot();
        leaving.Y = 0;
        if (leaving.LengthSquared() < 1e-8f)
            return null;
        leaving = leaving.Normalized();
        var right = leaving.Cross(Vector3.Up);
        float half = RoadCatalog.Get(edge.Type).Width / 2;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var center = node.Position.ToGodot() + Vector3.Up * SurfaceY;
        const int segments = 12;
        for (int i = 0; i < segments; i++)
        {
            float a0 = Mathf.Pi * i / segments;
            float a1 = Mathf.Pi * (i + 1) / segments;
            var p0 = center + (right * Mathf.Cos(a0) - leaving * Mathf.Sin(a0)) * half;
            var p1 = center + (right * Mathf.Cos(a1) - leaving * Mathf.Sin(a1)) * half;
            st.AddVertex(center);
            st.AddVertex(p0);
            st.AddVertex(p1);
        }
        st.GenerateNormals();
        return st.Commit();
    }

    // ------------------------------------------------------------------ ghosts

    /// <summary>Simple full-length strip used for placement previews.</summary>
    public static ArrayMesh? BuildGhostStrip(in Bezier3 curve, float width)
    {
        float len = curve.Length();
        if (len < 0.05f)
            return null;
        var ts = BezierOps.Tessellate(curve, ChordTolerance);
        float half = width / 2;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var up = Vector3.Up * (SurfaceY + 0.04f);
        for (int i = 0; i + 1 < ts.Count; i++)
        {
            var a1 = curve.OffsetPoint(ts[i], -half).ToGodot() + up;
            var a2 = curve.OffsetPoint(ts[i], +half).ToGodot() + up;
            var b1 = curve.OffsetPoint(ts[i + 1], -half).ToGodot() + up;
            var b2 = curve.OffsetPoint(ts[i + 1], +half).ToGodot() + up;
            AddQuad(st, a1, a2, b2, b1);
        }
        st.GenerateNormals();
        return st.Commit();
    }
}
