using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

/// <summary>
/// Generates junction geometry for a node: per-edge cut parameters (where edge meshes
/// stop) and the intersection surface polygon between them. Corners are computed on a
/// straight-line approximation of the curves near the node (borders as offset lines),
/// which is accurate at typical cut distances; the polygon's edge cross-sections use
/// the true curve offsets so meshes join exactly.
/// </summary>
public static class JunctionBuilder
{
    private const float CornerMargin = 0.5f;
    private const float MaxCutFraction = 0.3f;

    public static JunctionGeometry Build(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)
    {
        int degree = node.Edges.Count;
        if (degree == 0)
            return JunctionGeometry.Empty;

        var legs = node.Edges
            .Select(id => Leg.From(edges[id], node))
            .OrderBy(l => l.Angle)
            .ToArray();

        if (degree == 1)
            return new JunctionGeometry(
                new Dictionary<EdgeId, float> { [legs[0].Edge.Id] = legs[0].TAtCutDistance(0) },
                Array.Empty<Vector3>(), new HashSet<int>());

        if (degree == 2 && legs[0].HalfWidth == legs[1].HalfWidth
            && Vector2.Dot(legs[0].Dir, legs[1].Dir) <= -0.95f)
        {
            // continuing road: edges meet seamlessly, no junction surface
            return new JunctionGeometry(
                legs.ToDictionary(l => l.Edge.Id, l => l.TAtCutDistance(0)),
                Array.Empty<Vector3>(), new HashSet<int>());
        }

        // --- per adjacent CCW wedge: either a corner point (borders converge) or an
        // arc around the node (reflex/diverging side — rounds junction tips and
        // outer corners, and traces width transitions)
        var wedgeInserts = new List<Vector3>[degree];
        for (int i = 0; i < degree; i++)
        {
            var a = legs[i];
            var b = legs[(i + 1) % degree];
            // border of a on its CCW side, border of b on its CW side
            var pa = RotCcw(a.Dir) * a.HalfWidth;
            var pb = RotCw(b.Dir) * b.HalfWidth;
            float det = a.Dir.X * -b.Dir.Y - a.Dir.Y * -b.Dir.X;
            bool solved = false;
            float sa = 0, sb = 0;
            if (MathF.Abs(det) > 1e-4f)
            {
                var rhs = pb - pa;
                sa = (rhs.X * -b.Dir.Y - rhs.Y * -b.Dir.X) / det;
                sb = (a.Dir.X * rhs.Y - a.Dir.Y * rhs.X) / det;
                solved = sa >= 0 && sb >= 0;
            }
            if (solved)
            {
                a.CutDistance = MathF.Max(a.CutDistance, sa);
                b.CutDistance = MathF.Max(b.CutDistance, sb);
                var corner2 = pa + a.Dir * sa;
                wedgeInserts[i] = new List<Vector3> { node.Position + new Vector3(corner2.X, 0, corner2.Y) };
            }
            else
            {
                wedgeInserts[i] = NodeArc(node.Position, a, b);
            }
        }

        // --- finalize cuts: margin + clamp
        var cutT = new Dictionary<EdgeId, float>();
        foreach (var leg in legs)
        {
            float len = leg.Edge.ArcLength.TotalLength;
            leg.CutDistance = MathF.Min(leg.CutDistance + CornerMargin, len * MaxCutFraction);
            cutT[leg.Edge.Id] = leg.TAtCutDistance(leg.CutDistance);
        }

        // --- polygon: CCW walk, per edge [CW-side point, CCW-side point], then wedge corner
        var poly = new List<Vector3>();
        var cutSegments = new HashSet<int>();
        for (int i = 0; i < degree; i++)
        {
            var leg = legs[i];
            float t = cutT[leg.Edge.Id];
            // +offset in the curve frame is the curve's own CCW side; flip when the
            // edge travels into the node so sides are consistent in the leaving frame
            float sign = leg.StartsHere ? 1f : -1f;
            cutSegments.Add(poly.Count); // segment CW→CCW crosses the road: no skirt
            poly.Add(leg.Edge.Curve.OffsetPoint(t, -sign * leg.HalfWidth)); // CW side
            poly.Add(leg.Edge.Curve.OffsetPoint(t, +sign * leg.HalfWidth)); // CCW side
            poly.AddRange(wedgeInserts[i]);
        }

        return new JunctionGeometry(cutT, poly, cutSegments);
    }

    /// <summary>Boundary points sweeping around the node from leg a's CCW border to
    /// leg b's CW border, interpolating the two half-widths. Handles junction tips
    /// (acute Y), rounded outer corners, and width transitions.</summary>
    private static List<Vector3> NodeArc(Vector3 nodePos, Leg a, Leg b)
    {
        float start = MathF.Atan2(a.Dir.Y, a.Dir.X) + MathF.PI / 2;
        float end = MathF.Atan2(b.Dir.Y, b.Dir.X) - MathF.PI / 2;
        while (end < start - 1e-3f)
            end += 2 * MathF.PI;

        var points = new List<Vector3>();
        float span = end - start;
        int steps = Math.Max(1, (int)MathF.Ceiling(span / (20 * MathF.PI / 180)));
        for (int s = 0; s <= steps; s++)
        {
            float f = s / (float)steps;
            float angle = start + span * f;
            float radius = float.Lerp(a.HalfWidth, b.HalfWidth, f);
            var p = nodePos + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * radius;
            if (points.Count == 0 || Vector3.Distance(points[^1], p) > 1e-3f)
                points.Add(p);
        }
        return points;
    }

    private static Vector2 RotCcw(Vector2 v) => new(-v.Y, v.X);
    private static Vector2 RotCw(Vector2 v) => new(v.Y, -v.X);

    private sealed class Leg
    {
        public required RoadEdge Edge;
        public required Vector2 Dir;       // leaving the node, XZ
        public required float HalfWidth;
        public required bool StartsHere;
        public float CutDistance;
        public float Angle => MathF.Atan2(Dir.Y, Dir.X);

        public static Leg From(RoadEdge edge, RoadNode node)
        {
            bool startsHere = edge.StartNode == node.Id;
            var tan = startsHere ? edge.Curve.Tangent(0) : -edge.Curve.Tangent(1);
            var dir = new Vector2(tan.X, tan.Z);
            dir = dir.LengthSquared() > 0 ? Vector2.Normalize(dir) : Vector2.UnitX;
            return new Leg
            {
                Edge = edge,
                Dir = dir,
                HalfWidth = RoadCatalog.Get(edge.Type).Width / 2,
                StartsHere = startsHere,
            };
        }

        /// <summary>Curve parameter at the given distance from the node along this edge.</summary>
        public float TAtCutDistance(float d)
            => StartsHere
                ? Edge.ArcLength.TAtDistance(d)
                : Edge.ArcLength.TAtDistance(Edge.ArcLength.TotalLength - d);
    }
}
