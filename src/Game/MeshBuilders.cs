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
        float span = dEnd - dStart;
        const float period = DashOn + DashOff;

        // center the pattern so both ends (usually junctions) get equal margins
        int count = (int)MathF.Floor((span + DashOff) / period);
        if (count < 1)
        {
            if (span < 1f)
                return;
            count = 1;
        }
        float used = count * period - DashOff;
        float lead = MathF.Max(0, (span - used) / 2);

        for (int k = 0; k < count; k++)
        {
            float d0 = dStart + lead + k * period;
            float d1 = MathF.Min(d0 + DashOn, dEnd);
            float ta = edge.ArcLength.TAtDistance(d0);
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
        var centerXZ = node.Position.ToGodot();

        // proper triangulation: the node can lie outside its polygon (acute tips),
        // so a naive fan produces flipped triangles that break lighting/shadows
        var poly2d = poly.Select(p => new Vector2(p.X, p.Z)).ToArray();
        var indices = Geometry2D.TriangulatePolygon(poly2d);
        if (indices.Length >= 3)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
                AddTriangleUp(st,
                    poly[indices[i]].ToGodot() + Vector3.Up * SurfaceY,
                    poly[indices[i + 1]].ToGodot() + Vector3.Up * SurfaceY,
                    poly[indices[i + 2]].ToGodot() + Vector3.Up * SurfaceY);
        }
        else
        {
            // degenerate/self-intersecting outline: fall back to a centroid fan
            var centroid = poly.Aggregate(System.Numerics.Vector3.Zero, (acc, p) => acc + p) / poly.Count;
            var c = centroid.ToGodot() + Vector3.Up * SurfaceY;
            for (int i = 0; i < poly.Count; i++)
                AddTriangleUp(st, c,
                    poly[i].ToGodot() + Vector3.Up * SurfaceY,
                    poly[(i + 1) % poly.Count].ToGodot() + Vector3.Up * SurfaceY);
        }

        // outer boundary segments get a skirt down to the ground, matching the
        // bevel on edge meshes — this closes the notches at junction corners
        for (int i = 0; i < poly.Count; i++)
        {
            if (node.Junction.CutSegments.Contains(i))
                continue;
            var a = poly[i].ToGodot() + Vector3.Up * SurfaceY;
            var b = poly[(i + 1) % poly.Count].ToGodot() + Vector3.Up * SurfaceY;
            AddBoundarySkirt(st, centerXZ, a, b);
        }
        return st.Commit();
    }

    /// <summary>Flat top-facing triangle: winding is normalized so the front face
    /// (Godot fronts are clockwise) points up — otherwise the shadow pass darkens it.</summary>
    private static void AddTriangleUp(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
    {
        if ((c - a).Cross(b - a).Y < 0)
            (b, c) = (c, b);
        st.SetNormal(Vector3.Up);
        st.AddVertex(a);
        st.SetNormal(Vector3.Up);
        st.AddVertex(b);
        st.SetNormal(Vector3.Up);
        st.AddVertex(c);
    }

    private static void AddBoundarySkirt(SurfaceTool st, Vector3 nodePos, Vector3 a, Vector3 b)
    {
        var seg = b - a;
        seg.Y = 0;
        if (seg.LengthSquared() < 1e-8f)
            return;
        var outward = seg.Normalized().Cross(Vector3.Up);
        var mid = (a + b) / 2;
        var toMid = mid - nodePos;
        toMid.Y = 0;
        if (outward.Dot(toMid) < 0)
            outward = -outward;

        var aOut = new Vector3(a.X, 0, a.Z) + outward * SkirtWidth;
        var bOut = new Vector3(b.X, 0, b.Z) + outward * SkirtWidth;
        var n = (outward + Vector3.Up).Normalized();

        // orient the front face outward/up (Godot fronts are clockwise)
        if ((bOut - a).Cross(b - a).Dot(n) < 0)
        {
            (a, b) = (b, a);
            (aOut, bOut) = (bOut, aOut);
        }
        st.SetNormal(n); st.AddVertex(a);
        st.SetNormal(n); st.AddVertex(b);
        st.SetNormal(n); st.AddVertex(bOut);
        st.SetNormal(n); st.AddVertex(a);
        st.SetNormal(n); st.AddVertex(bOut);
        st.SetNormal(n); st.AddVertex(aOut);
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
        var centerXZ = node.Position.ToGodot();
        var center = centerXZ + Vector3.Up * SurfaceY;
        const int segments = 12;
        for (int i = 0; i < segments; i++)
        {
            float a0 = Mathf.Pi * i / segments;
            float a1 = Mathf.Pi * (i + 1) / segments;
            var p0 = center + (right * Mathf.Cos(a0) - leaving * Mathf.Sin(a0)) * half;
            var p1 = center + (right * Mathf.Cos(a1) - leaving * Mathf.Sin(a1)) * half;
            AddTriangleUp(st, center, p0, p1);
            AddBoundarySkirt(st, centerXZ, p0, p1);
        }
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
