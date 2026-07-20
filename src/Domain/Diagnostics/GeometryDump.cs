using System.Globalization;
using System.Text;
using System.Text.Json;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Diagnostics;

/// <summary>
/// Agent-readable exports of the domain road network: JSON for exact-coordinate
/// queries, SVG for at-a-glance plan-view reading. Reads only cached derived
/// data — never mutates or rebuilds.
/// </summary>
public static class GeometryDump
{
    private const float ChordTolerance = 0.25f;

    public static string Json(RoadNetwork network)
    {
        var dto = new
        {
            version = network.Version,
            nodes = network.Nodes.Values.OrderBy(n => n.Id.Value).Select(n => new
            {
                id = n.Id.Value,
                position = Pt(n.Position),
                edges = n.Edges.Select(e => e.Value).OrderBy(v => v).ToArray(),
                ring = n.Ring?.Value,
                junctionPolygon = n.Junction.SurfacePolygon.Select(Pt).ToArray(),
            }).ToArray(),
            edges = network.Edges.Values.OrderBy(e => e.Id.Value).Select(e =>
            {
                var type = RoadCatalog.Get(e.Type);
                return new
                {
                    id = e.Id.Value,
                    type = type.Name,
                    width = type.Width,
                    covered = e.Covered,
                    start = e.StartNode.Value,
                    end = e.EndNode.Value,
                    polyline = Polyline(e.Curve),
                    lanes = e.Lanes.Select(l => new
                    {
                        id = l.Id.Value,
                        offset = l.Offset,
                        direction = l.Direction.ToString(),
                        kind = l.Kind.ToString(),
                        width = l.Width,
                    }).ToArray(),
                };
            }).ToArray(),
            roundabouts = network.Roundabouts.Values.OrderBy(r => r.Id.Value).Select(r => new
            {
                id = r.Id.Value,
                center = Pt(r.Center),
                radius = r.Radius,
                ringNodes = r.RingNodes.Select(n => n.Value).ToArray(),
                ringEdges = r.RingEdges.Select(e => e.Value).ToArray(),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
    }

    public static void JsonToFile(RoadNetwork network, string path) =>
        File.WriteAllText(path, Json(network));

    public static string Svg(RoadNetwork network)
    {
        // bounds over node positions + edge control points, padded
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        void Grow(System.Numerics.Vector3 v)
        {
            minX = MathF.Min(minX, v.X); maxX = MathF.Max(maxX, v.X);
            minZ = MathF.Min(minZ, v.Z); maxZ = MathF.Max(maxZ, v.Z);
        }
        foreach (var n in network.Nodes.Values) Grow(n.Position);
        foreach (var e in network.Edges.Values) { Grow(e.Curve.P0); Grow(e.Curve.P1); Grow(e.Curve.P2); Grow(e.Curve.P3); }
        if (network.Nodes.Count == 0) { minX = minZ = -30; maxX = maxZ = 30; }
        const float pad = 15f;
        minX -= pad; minZ -= pad; maxX += pad; maxZ += pad;
        if (maxX - minX < 60) { var c = (minX + maxX) / 2; minX = c - 30; maxX = c + 30; }
        if (maxZ - minZ < 60) { var c = (minZ + maxZ) / 2; minZ = c - 30; maxZ = c + 30; }

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(minX)} {F(minZ)} {F(maxX - minX)} {F(maxZ - minZ)}\">");
        sb.AppendLine("<!-- plan view: svg x = world X (m), svg y = world Z (m); elevation (world Y) appears in labels -->");
        sb.AppendLine("<defs><marker id=\"arrow\" viewBox=\"0 0 4 4\" refX=\"3\" refY=\"2\" markerWidth=\"4\" markerHeight=\"4\" orient=\"auto\"><path d=\"M0,0 L4,2 L0,4 z\"/></marker></defs>");
        sb.AppendLine($"<rect x=\"{F(minX)}\" y=\"{F(minZ)}\" width=\"{F(maxX - minX)}\" height=\"{F(maxZ - minZ)}\" fill=\"white\"/>");

        // ---- edges: carriageway band + thin centerline
        sb.AppendLine("<g id=\"edges\">");
        foreach (var e in network.Edges.Values.OrderBy(e => e.Id.Value))
        {
            var type = RoadCatalog.Get(e.Type);
            var d = PathD(e.Curve);
            var band = e.Covered
                ? "stroke=\"#b0c4de\" stroke-dasharray=\"4 2\""
                : "stroke=\"#d8d8d8\"";
            sb.AppendLine($"<path d=\"{d}\" fill=\"none\" {band} stroke-width=\"{F(type.Width)}\"/>");
            sb.AppendLine($"<path d=\"{d}\" fill=\"none\" stroke=\"#555\" stroke-width=\"0.3\"/>");
        }
        sb.AppendLine("</g>");

        // ---- junction polygons + roundabout rings
        sb.AppendLine("<g id=\"junctions\">");
        foreach (var n in network.Nodes.Values.OrderBy(n => n.Id.Value))
        {
            var poly = n.Junction.SurfacePolygon;
            if (poly.Count >= 3)
            {
                var pts = string.Join(" ", poly.Select(p => $"{F(p.X)},{F(p.Z)}"));
                sb.AppendLine($"<polygon points=\"{pts}\" fill=\"#c8c8c8\" stroke=\"#666\" stroke-width=\"0.3\"/>");
            }
        }
        foreach (var r in network.Roundabouts.Values.OrderBy(r => r.Id.Value))
            sb.AppendLine($"<circle cx=\"{F(r.Center.X)}\" cy=\"{F(r.Center.Z)}\" r=\"{F(r.Radius)}\" fill=\"none\" stroke=\"#8a2be2\" stroke-width=\"0.4\"/>");
        sb.AppendLine("</g>");

        // ---- lanes (in travel order, arrowheads) + node connectors
        sb.AppendLine("<g id=\"lanes\">");
        foreach (var e in network.Edges.Values.OrderBy(e => e.Id.Value))
        {
            var c = e.Curve;
            var ts = BezierOps.Tessellate(c, ChordTolerance);
            foreach (var lane in e.Lanes)
            {
                var pts = ts.Select(t => c.OffsetPoint(t, lane.Offset)).ToList();
                if (lane.Direction == LaneDirection.Backward) pts.Reverse();
                var poly = string.Join(" ", pts.Select(p => $"{F(p.X)},{F(p.Z)}"));
                var color = lane.Kind switch
                {
                    LaneKind.Driving => "#2b6cb0",
                    LaneKind.Bicycle => "#2f855a",
                    _ => "#718096",
                };
                sb.AppendLine($"<polyline points=\"{poly}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"0.25\" marker-end=\"url(#arrow)\"/>");
            }
        }
        foreach (var n in network.Nodes.Values.OrderBy(n => n.Id.Value))
            foreach (var conn in n.Connectors)
                sb.AppendLine($"<path d=\"{PathD(conn.Curve)}\" fill=\"none\" stroke=\"#9f7aea\" stroke-width=\"0.2\" stroke-dasharray=\"1 0.7\"/>");
        sb.AppendLine("</g>");

        // ---- conflict points (dedupe: only pairs with Other > my index)
        sb.AppendLine("<g id=\"conflicts\">");
        foreach (var n in network.Nodes.Values.OrderBy(n => n.Id.Value))
        {
            for (int ci = 0; ci < n.ConnectorConflicts.Count; ci++)
            {
                var curve = n.Connectors[ci].Curve;
                var len = MathF.Max(curve.Length(), 0.01f);
                foreach (var cp in n.ConnectorConflicts[ci])
                {
                    if (cp.Other <= ci) continue;
                    var p = curve.Point(MathF.Min(1f, cp.SMine / len));
                    sb.AppendLine($"<circle cx=\"{F(p.X)}\" cy=\"{F(p.Z)}\" r=\"0.5\" fill=\"#e53e3e\"/>");
                }
            }
        }
        sb.AppendLine("</g>");

        // ---- labels: node ids, edge id/type/elevation/covered, roundabouts
        sb.AppendLine("<g id=\"labels\" font-size=\"2.5\" fill=\"#333\" font-family=\"monospace\">");
        foreach (var n in network.Nodes.Values.OrderBy(n => n.Id.Value))
            sb.AppendLine($"<text x=\"{F(n.Position.X + 1)}\" y=\"{F(n.Position.Z - 1)}\">n{n.Id.Value}</text>");
        foreach (var e in network.Edges.Values.OrderBy(e => e.Id.Value))
        {
            var mid = e.Curve.Point(0.5f);
            var type = RoadCatalog.Get(e.Type);
            var label = $"e{e.Id.Value} {type.Name}";
            if (MathF.Abs(mid.Y) > 0.05f) label += $" y={F(mid.Y)}";
            if (e.Covered) label += " covered";
            sb.AppendLine($"<text x=\"{F(mid.X + 1)}\" y=\"{F(mid.Z + 3)}\">{label}</text>");
        }
        foreach (var r in network.Roundabouts.Values.OrderBy(r => r.Id.Value))
            sb.AppendLine($"<text x=\"{F(r.Center.X)}\" y=\"{F(r.Center.Z)}\">rb{r.Id.Value} r={F(r.Radius)}</text>");
        sb.AppendLine("</g>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    public static void SvgToFile(RoadNetwork network, string path) =>
        File.WriteAllText(path, Svg(network));

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string PathD(in Bezier3 c) =>
        $"M {F(c.P0.X)} {F(c.P0.Z)} C {F(c.P1.X)} {F(c.P1.Z)}, {F(c.P2.X)} {F(c.P2.Z)}, {F(c.P3.X)} {F(c.P3.Z)}";

    private static float[] Pt(System.Numerics.Vector3 v) =>
        new[] { MathF.Round(v.X, 2), MathF.Round(v.Y, 2), MathF.Round(v.Z, 2) };

    private static float[][] Polyline(Bezier3 curve) =>
        BezierOps.Tessellate(curve, ChordTolerance).Select(t => Pt(curve.Point(t))).ToArray();
}
