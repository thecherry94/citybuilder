using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Godot;

namespace CityBuilder.Game;

/// <summary>
/// Procedural road geometry, driven entirely by the road type's lane profile:
/// - asphalt carriageway (everything that is not sidewalk) with bevel skirts on
///   open sides
/// - raised concrete sidewalks with curb faces, ramping down to road level at
///   junction cuts (dropped curbs)
/// - tinted bicycle-lane strips
/// - painted markings derived from lane adjacency (dashed between same-direction
///   driving lanes, center line between directions, solid at bike separations,
///   rural edge lines only where no sidewalk/bike lane follows)
/// Materials are embedded per surface, so views just assign the mesh.
/// </summary>
public static class MeshBuilders
{
    public const float SurfaceY = 0.07f;
    public const float MarkingY = 0.10f;
    public const float SidewalkRise = 0.13f;
    private const float SkirtWidth = 0.3f;
    private const float ChordTolerance = 0.15f;
    private const float MarkingWidth = 0.15f;
    private const float DashOn = 3f, DashOff = 3f;
    private const float EdgeLineInset = 0.4f;
    private const float CurbRampLength = 1.4f;
    private const float BikeTintY = SurfaceY + 0.004f;

    // ---------------------------------------------------------------- edge mesh

    public static ArrayMesh? BuildEdgeMesh(RoadEdge edge, RoadType type, float tStart, float tEnd,
        bool rampStart, bool rampEnd)
    {
        if (tEnd - tStart < 1e-4f)
            return null;

        var ts = SampleRange(edge.Curve, tStart, tEnd);
        var sidewalks = type.Lanes.Where(l => l.Kind == LaneKind.Sidewalk).ToArray();
        var leftWalk = sidewalks.Where(l => l.Offset < 0).OrderBy(l => l.Offset).Cast<LaneSpec?>().FirstOrDefault();
        var rightWalk = sidewalks.Where(l => l.Offset > 0).OrderByDescending(l => l.Offset).Cast<LaneSpec?>().FirstOrDefault();
        float asphaltLeft = leftWalk is { } lw ? lw.Offset + lw.Width / 2 : -type.Width / 2;
        float asphaltRight = rightWalk is { } rw ? rw.Offset - rw.Width / 2 : type.Width / 2;

        var mesh = BuildAsphalt(edge, ts, asphaltLeft, asphaltRight, leftWalk is null, rightWalk is null);

        var markings = BuildMarkings(edge, type, tStart, tEnd);
        markings?.Commit(mesh);

        if (sidewalks.Length > 0)
            BuildSidewalks(edge, ts, sidewalks, tStart, tEnd, rampStart, rampEnd).Commit(mesh);

        var bikes = type.Lanes.Where(l => l.Kind == LaneKind.Bicycle).ToArray();
        if (bikes.Length > 0)
            BuildBikeTint(edge, ts, bikes).Commit(mesh);

        return mesh;
    }

    private static ArrayMesh BuildAsphalt(RoadEdge edge, List<float> ts,
        float left, float right, bool skirtLeft, bool skirtRight)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetMaterial(Materials.Asphalt);
        for (int i = 0; i + 1 < ts.Count; i++)
        {
            var a = AsphaltSection(edge.Curve, ts[i], left, right, skirtLeft, skirtRight);
            var b = AsphaltSection(edge.Curve, ts[i + 1], left, right, skirtLeft, skirtRight);
            for (int q = 0; q + 1 < a.Length; q++)
            {
                // one smooth group per cross-section band: smooth along the road,
                // hard edges between top and skirts, top normals exactly up — this
                // matches junction surfaces so seams don't catch the light
                st.SetSmoothGroup((uint)(q + 1));
                AddQuad(st, a[q], a[q + 1], b[q + 1], b[q]);
            }
        }
        st.GenerateNormals();
        return st.Commit();
    }

    private static Vector3[] AsphaltSection(in Bezier3 curve, float t,
        float left, float right, bool skirtLeft, bool skirtRight)
    {
        var up = Vector3.Up;
        var pts = new List<Vector3>(4);
        if (skirtLeft)
            pts.Add(curve.OffsetPoint(t, left - SkirtWidth).ToGodot());
        // tuck slightly under the curb so no crack shows
        pts.Add(curve.OffsetPoint(t, skirtLeft ? left : left - 0.05f).ToGodot() + up * SurfaceY);
        pts.Add(curve.OffsetPoint(t, skirtRight ? right : right + 0.05f).ToGodot() + up * SurfaceY);
        if (skirtRight)
            pts.Add(curve.OffsetPoint(t, right + SkirtWidth).ToGodot());
        return pts.ToArray();
    }

    // ---------------------------------------------------------------- sidewalks

    private static SurfaceTool BuildSidewalks(RoadEdge edge, List<float> ts, LaneSpec[] sidewalks,
        float tStart, float tEnd, bool rampStart, bool rampEnd)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetMaterial(Materials.Concrete);

        float dStart = edge.ArcLength.DistanceAtT(tStart);
        float dEnd = edge.ArcLength.DistanceAtT(tEnd);

        // ramps on very short pieces would dip the whole strip: keep it level instead
        bool allowRamp = dEnd - dStart > CurbRampLength * 3;

        float HeightAt(float t)
        {
            if (!allowRamp)
                return SurfaceY + SidewalkRise;
            float d = edge.ArcLength.DistanceAtT(t);
            float factor = 1f;
            if (rampStart)
                factor = MathF.Min(factor, (d - dStart) / CurbRampLength);
            if (rampEnd)
                factor = MathF.Min(factor, (dEnd - d) / CurbRampLength);
            return SurfaceY + SidewalkRise * Math.Clamp(factor, 0f, 1f);
        }

        foreach (var walk in sidewalks)
        {
            bool leftSide = walk.Offset < 0;
            float inner = walk.Offset + (leftSide ? walk.Width / 2 : -walk.Width / 2);
            float outer = walk.Offset + (leftSide ? -walk.Width / 2 : walk.Width / 2);

            for (int i = 0; i + 1 < ts.Count; i++)
            {
                var a = SidewalkSection(edge.Curve, ts[i], inner, outer, HeightAt(ts[i]));
                var b = SidewalkSection(edge.Curve, ts[i + 1], inner, outer, HeightAt(ts[i + 1]));
                for (int q = 0; q + 1 < a.Length; q++)
                {
                    // hard edges between curb face / top / outer wall (see BuildAsphalt)
                    st.SetSmoothGroup((uint)(q + 1));
                    AddQuad(st, a[q], a[q + 1], b[q + 1], b[q]);
                }
            }
        }
        st.GenerateNormals();
        return st;
    }

    private static Vector3[] SidewalkSection(in Bezier3 curve, float t, float inner, float outer, float topY)
    {
        var up = Vector3.Up;
        return new[]
        {
            curve.OffsetPoint(t, inner).ToGodot() + up * SurfaceY,   // curb bottom
            curve.OffsetPoint(t, inner).ToGodot() + up * topY,       // curb top
            curve.OffsetPoint(t, outer).ToGodot() + up * topY,       // outer top
            curve.OffsetPoint(t, outer).ToGodot(),                   // vertical outer wall
        };
    }

    // --------------------------------------------------------------- bike lanes

    private static SurfaceTool BuildBikeTint(RoadEdge edge, List<float> ts, LaneSpec[] bikes)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetMaterial(Materials.BikeLane);
        var up = Vector3.Up * BikeTintY;
        foreach (var bike in bikes)
        {
            float lo = bike.Offset - bike.Width / 2;
            float hi = bike.Offset + bike.Width / 2;
            for (int i = 0; i + 1 < ts.Count; i++)
            {
                var a1 = edge.Curve.OffsetPoint(ts[i], lo).ToGodot() + up;
                var a2 = edge.Curve.OffsetPoint(ts[i], hi).ToGodot() + up;
                var b1 = edge.Curve.OffsetPoint(ts[i + 1], lo).ToGodot() + up;
                var b2 = edge.Curve.OffsetPoint(ts[i + 1], hi).ToGodot() + up;
                AddQuad(st, a1, a2, b2, b1);
            }
        }
        st.GenerateNormals();
        return st;
    }

    // ------------------------------------------------------------------ shared

    private static List<float> SampleRange(in Bezier3 curve, float tStart, float tEnd)
    {
        var ts = new List<float> { tStart };
        foreach (var t in BezierOps.Tessellate(curve, ChordTolerance))
            if (t > tStart + 1e-5f && t < tEnd - 1e-5f)
                ts.Add(t);
        ts.Add(tEnd);
        return ts;
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
        st.SetMaterial(Materials.Marking);
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

    public const float MarkingDashOn = DashOn, MarkingDashOff = DashOff;
    public const float MarkingLineWidth = MarkingWidth;

    /// <summary>Paint rules from lane adjacency, valid for any lane profile.
    /// Also used to continue markings across degree-2 corner junctions.</summary>
    public static IEnumerable<(float offset, bool dashed)> MarkingLayout(RoadType type)
    {
        var driving = type.Lanes.Where(l => l.Kind == LaneKind.Driving).OrderBy(l => l.Offset).ToArray();
        if (driving.Length == 0)
            yield break;

        for (int i = 0; i + 1 < driving.Length; i++)
        {
            float boundary = (driving[i].Offset + driving[i].Width / 2
                + driving[i + 1].Offset - driving[i + 1].Width / 2) / 2;
            if (driving[i].Direction == driving[i + 1].Direction)
                yield return (boundary, true);              // lane separator
            else if (driving.Length <= 2)
                yield return (boundary, true);              // small road: dashed center
            else
            {
                yield return (boundary - 0.18f, false);     // double solid center
                yield return (boundary + 0.18f, false);
            }
        }

        foreach (int side in new[] { -1, +1 })
        {
            var outermost = side < 0 ? driving[0] : driving[^1];
            float carriagewayEdge = outermost.Offset + side * outermost.Width / 2;
            var beyond = type.Lanes
                .Where(l => l.Kind != LaneKind.Driving
                    && MathF.Sign(l.Offset) == side
                    && MathF.Abs(l.Offset) > MathF.Abs(outermost.Offset))
                .OrderBy(l => MathF.Abs(l.Offset))
                .FirstOrDefault();

            if (beyond is null)
                yield return (side * (type.Width / 2 - EdgeLineInset), false); // rural edge line
            else if (beyond.Kind == LaneKind.Bicycle)
                yield return (carriagewayEdge, false);      // solid bike separation
            // sidewalk adjacent: the curb is the boundary, no paint
        }
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
        st.SetMaterial(Materials.Asphalt);
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

        // open outer boundary segments get a skirt down to the ground; curbed ones
        // are covered by the raised corner zones instead
        for (int i = 0; i < poly.Count; i++)
        {
            if (node.Junction.SegmentKinds[i] != JunctionSegmentKind.Open)
                continue;
            var a = poly[i].ToGodot() + Vector3.Up * SurfaceY;
            var b = poly[(i + 1) % poly.Count].ToGodot() + Vector3.Up * SurfaceY;
            AddBoundarySkirt(st, centerXZ, a, b);
        }
        var mesh = st.Commit();

        if (node.Junction.Corners.Count > 0)
            BuildCornerZones(node.Junction.Corners).Commit(mesh);
        return mesh;
    }

    /// <summary>Raised concrete corner sidewalks: flat top, curb faces along the
    /// inner boundary (toward the asphalt), ground skirt along the outer boundary.</summary>
    private static SurfaceTool BuildCornerZones(IReadOnlyList<CornerZone> zones)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetMaterial(Materials.Concrete);
        float topY = SurfaceY + SidewalkRise;

        foreach (var zone in zones)
        {
            var ring = zone.Polygon;

            // top surface (triangulation can fail on near-degenerate rings: fall
            // back to a centroid fan rather than dropping the surface)
            var ring2d = ring.Select(p => new Vector2(p.X, p.Z)).ToArray();
            var indices = Geometry2D.TriangulatePolygon(ring2d);
            var centroid = ring.Aggregate(System.Numerics.Vector3.Zero, (acc, p) => acc + p) / ring.Count;
            var centroidG = centroid.ToGodot();
            if (indices.Length >= 3)
            {
                for (int i = 0; i + 2 < indices.Length; i += 3)
                    AddTriangleUpAt(st, ring[indices[i]], ring[indices[i + 1]], ring[indices[i + 2]], topY);
            }
            else
            {
                var c = new Vector3(centroidG.X, topY, centroidG.Z);
                for (int i = 0; i + 1 < ring.Count; i++)
                    AddTriangleUp(st, c,
                        new Vector3(ring[i].X, topY, ring[i].Z),
                        new Vector3(ring[i + 1].X, topY, ring[i + 1].Z));
            }

            // curb faces along the inner boundary
            for (int i = 0; i + 1 < zone.InnerCount; i++)
                AddWallQuad(st,
                    ring[i].ToGodot(), ring[i + 1].ToGodot(),
                    SurfaceY, topY, centroidG);

            // vertical walls along the outer boundary — straight down, so adjacent
            // segments share corner vertices and can never open gaps
            // (the two side edges — ring[InnerCount-1]→ring[InnerCount] and the
            // closing segment — stay open, flush against the approach sidewalks)
            for (int i = zone.InnerCount; i + 1 < ring.Count; i++)
                AddWallQuad(st, ring[i].ToGodot(), ring[i + 1].ToGodot(), 0f, topY, centroidG);
        }
        return st;
    }

    private static void AddTriangleUpAt(SurfaceTool st, System.Numerics.Vector3 a,
        System.Numerics.Vector3 b, System.Numerics.Vector3 c, float y)
        => AddTriangleUp(st,
            new Vector3(a.X, y, a.Z), new Vector3(b.X, y, b.Z), new Vector3(c.X, y, c.Z));

    private static void AddWallQuad(SurfaceTool st, Vector3 a, Vector3 b, float yBottom, float yTop, Vector3 interior)
    {
        var seg = b - a;
        seg.Y = 0;
        if (seg.LengthSquared() < 1e-8f)
            return;
        var normal = seg.Normalized().Cross(Vector3.Up);
        var mid = (a + b) / 2;
        var toInterior = interior - mid;
        toInterior.Y = 0;
        if (normal.Dot(toInterior) > 0)
            normal = -normal; // face away from the zone interior

        var a0 = new Vector3(a.X, yBottom, a.Z);
        var b0 = new Vector3(b.X, yBottom, b.Z);
        var a1 = new Vector3(a.X, yTop, a.Z);
        var b1 = new Vector3(b.X, yTop, b.Z);
        if ((b0 - a1).Cross(b1 - a1).Dot(normal) < 0)
        {
            (a0, b0) = (b0, a0);
            (a1, b1) = (b1, a1);
        }
        st.SetNormal(normal); st.AddVertex(a1);
        st.SetNormal(normal); st.AddVertex(b1);
        st.SetNormal(normal); st.AddVertex(b0);
        st.SetNormal(normal); st.AddVertex(a1);
        st.SetNormal(normal); st.AddVertex(b0);
        st.SetNormal(normal); st.AddVertex(a0);
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
        st.SetMaterial(Materials.Asphalt);
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
