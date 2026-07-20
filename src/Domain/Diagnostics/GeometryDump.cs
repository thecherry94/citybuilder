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

    private static float[] Pt(System.Numerics.Vector3 v) =>
        new[] { MathF.Round(v.X, 2), MathF.Round(v.Y, 2), MathF.Round(v.Z, 2) };

    private static float[][] Polyline(Bezier3 curve) =>
        BezierOps.Tessellate(curve, ChordTolerance).Select(t => Pt(curve.Point(t))).ToArray();
}
