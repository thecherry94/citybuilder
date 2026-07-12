using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Network;

/// <summary>
/// Generates junction geometry for a node. Two outlines are computed from the same
/// wedge solve: the full-width outline (drives the cut distances and bounds the whole
/// junction footprint) and the carriageway outline (the asphalt surface). The ring
/// between them, per wedge, becomes a raised sidewalk corner zone — so sidewalks wrap
/// around junction corners for any combination of road profiles.
/// Corners are computed on a straight-line approximation of the curves near the node
/// (borders as offset lines); the polygon's road cross-sections use the true curve
/// offsets so meshes join exactly.
/// </summary>
public static class JunctionBuilder
{
    private const float CornerMargin = 0.5f;
    private const float MaxCutFraction = 0.3f;
    private const float ZoneMinBand = 0.05f;

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
                Array.Empty<Vector3>(), Array.Empty<JunctionSegmentKind>(), Array.Empty<CornerZone>(),
                new HashSet<EdgeId>());

        if (degree == 2 && legs[0].FullHalf == legs[1].FullHalf
            && legs[0].CwHalf == legs[1].CwHalf
            && Vector2.Dot(legs[0].Dir, legs[1].Dir) <= -0.95f)
        {
            // continuing road: edges meet seamlessly, no junction surface
            return new JunctionGeometry(
                legs.ToDictionary(l => l.Edge.Id, l => l.TAtCutDistance(0)),
                Array.Empty<Vector3>(), Array.Empty<JunctionSegmentKind>(), Array.Empty<CornerZone>(),
                new HashSet<EdgeId>());
        }

        // --- per adjacent CCW wedge: matching outer (full width) and inner
        // (carriageway width) inserts — either corner points or arcs around the node
        var outerInserts = new List<Vector3>[degree];
        var innerInserts = new List<Vector3>[degree];
        var cornerSolved = new bool[degree];
        for (int i = 0; i < degree; i++)
        {
            var a = legs[i];
            var b = legs[(i + 1) % degree];
            if (SolveCorner(node.Position, a, b, a.FullHalf, b.FullHalf) is { } outerCorner)
            {
                a.CutDistance = MathF.Max(a.CutDistance, outerCorner.sa);
                b.CutDistance = MathF.Max(b.CutDistance, outerCorner.sb);
                cornerSolved[i] = true;
                outerInserts[i] = new List<Vector3> { outerCorner.point };
                innerInserts[i] = SolveCorner(node.Position, a, b, a.CwHalf, b.CwHalf) is { } innerCorner
                    ? new List<Vector3> { innerCorner.point }
                    : NodeArc(node.Position, a, b, a.CwHalf, b.CwHalf);
            }
            else
            {
                outerInserts[i] = NodeArc(node.Position, a, b, a.FullHalf, b.FullHalf);
                innerInserts[i] = NodeArc(node.Position, a, b, a.CwHalf, b.CwHalf);
            }
        }

        // --- finalize cuts: margin + clamp (always from the full-width solve)
        var cutT = new Dictionary<EdgeId, float>();
        var tightCuts = new HashSet<EdgeId>();
        foreach (var leg in legs)
        {
            float len = leg.Edge.ArcLength.TotalLength;
            float wanted = leg.CutDistance + CornerMargin;
            leg.CutDistance = MathF.Min(wanted, len * MaxCutFraction);
            if (leg.CutDistance < wanted - 1e-3f)
                tightCuts.Add(leg.Edge.Id); // edge too short for the full junction
            cutT[leg.Edge.Id] = leg.TAtCutDistance(leg.CutDistance);
        }

        // --- carriageway polygon + segment kinds + corner zones
        var poly = new List<Vector3>();
        var kinds = new List<JunctionSegmentKind>();
        var zones = new List<CornerZone>();

        for (int i = 0; i < degree; i++)
        {
            var a = legs[i];
            var b = legs[(i + 1) % degree];
            float ta = cutT[a.Edge.Id];
            float tb = cutT[b.Edge.Id];

            // cut cross-section of leg a (carriageway width)
            poly.Add(SectionPoint(a, ta, cw: true, ccwSide: false));
            kinds.Add(JunctionSegmentKind.Cut);

            // wedge from leg a's CCW side to leg b's CW side; solved corner points
            // become rounded curb returns (quadratic through the corner)
            var innerStart = SectionPoint(a, ta, cw: true, ccwSide: true);
            var outerStart = SectionPoint(a, ta, cw: false, ccwSide: true);
            var innerEnd = SectionPoint(b, tb, cw: true, ccwSide: false);
            var outerEnd = SectionPoint(b, tb, cw: false, ccwSide: false);

            var innerRun = new List<Vector3> { innerStart };
            var outerRun = new List<Vector3> { outerStart };
            if (cornerSolved[i])
            {
                outerRun.AddRange(RoundCorner(outerStart, outerInserts[i][0], outerEnd));
                innerRun.AddRange(innerInserts[i].Count == 1
                    ? RoundCorner(innerStart, innerInserts[i][0], innerEnd)
                    : innerInserts[i]);
            }
            else
            {
                innerRun.AddRange(innerInserts[i]);
                outerRun.AddRange(outerInserts[i]);
            }

            bool zone = HasBand(innerRun, outerRun, innerEnd, outerEnd);
            if (zone)
            {
                var ring = new List<Vector3>(innerRun) { innerEnd };
                int innerCount = ring.Count;
                ring.Add(outerEnd);
                for (int k = outerRun.Count - 1; k >= 0; k--)
                    ring.Add(outerRun[k]);
                zones.Add(new CornerZone(ring, innerCount));
            }

            var wedgeKind = zone ? JunctionSegmentKind.Curbed : JunctionSegmentKind.Open;
            foreach (var p in innerRun)
            {
                poly.Add(p);
                kinds.Add(wedgeKind);
            }
        }

        return new JunctionGeometry(cutT, poly, kinds, zones, tightCuts);
    }

    private static Vector3 SectionPoint(Leg leg, float t, bool cw, bool ccwSide)
    {
        float half = cw ? leg.CwHalf : leg.FullHalf;
        // +offset in the curve frame is the curve's own CCW side; flip when the edge
        // travels into the node so sides are consistent in the leaving frame
        float sign = (leg.StartsHere ? 1f : -1f) * (ccwSide ? 1f : -1f);
        return leg.Edge.Curve.OffsetPoint(t, sign * half);
    }

    private static bool HasBand(List<Vector3> innerRun, List<Vector3> outerRun, Vector3 innerEnd, Vector3 outerEnd)
    {
        if (Vector3.Distance(innerEnd, outerEnd) > ZoneMinBand)
            return true;
        if (Vector3.Distance(innerRun[0], outerRun[0]) > ZoneMinBand)
            return true;
        // compare wedge inserts pairwise where counts match; otherwise assume a band
        if (innerRun.Count != outerRun.Count)
            return true;
        for (int i = 1; i < innerRun.Count; i++)
            if (Vector3.Distance(innerRun[i], outerRun[i]) > ZoneMinBand)
                return true;
        return false;
    }

    /// <summary>Rounded curb return: quadratic bezier from one cut section through
    /// the sharp corner point to the other (endpoints excluded).</summary>
    private static IEnumerable<Vector3> RoundCorner(Vector3 from, Vector3 corner, Vector3 to)
    {
        foreach (var t in new[] { 0.25f, 0.5f, 0.75f })
        {
            float u = 1 - t;
            yield return u * u * from + 2 * u * t * corner + t * t * to;
        }
    }

    private static (Vector3 point, float sa, float sb)? SolveCorner(
        Vector3 nodePos, Leg a, Leg b, float halfA, float halfB)
    {
        var pa = RotCcw(a.Dir) * halfA;
        var pb = RotCw(b.Dir) * halfB;
        float det = a.Dir.X * -b.Dir.Y - a.Dir.Y * -b.Dir.X;
        if (MathF.Abs(det) <= 1e-4f)
            return null;
        var rhs = pb - pa;
        float sa = (rhs.X * -b.Dir.Y - rhs.Y * -b.Dir.X) / det;
        float sb = (a.Dir.X * rhs.Y - a.Dir.Y * rhs.X) / det;
        if (sa < 0 || sb < 0)
            return null;
        var corner2 = pa + a.Dir * sa;
        return (nodePos + new Vector3(corner2.X, 0, corner2.Y), sa, sb);
    }

    /// <summary>Boundary points sweeping around the node from leg a's CCW border to
    /// leg b's CW border, interpolating the given radii. Handles junction tips
    /// (acute Y), rounded outer corners, and width transitions.</summary>
    private static List<Vector3> NodeArc(Vector3 nodePos, Leg a, Leg b, float radiusA, float radiusB)
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
            float radius = float.Lerp(radiusA, radiusB, f);
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
        public required float FullHalf;    // includes sidewalks
        public required float CwHalf;      // carriageway only
        public required bool StartsHere;
        public float CutDistance;
        public float Angle => MathF.Atan2(Dir.Y, Dir.X);

        public static Leg From(RoadEdge edge, RoadNode node)
        {
            bool startsHere = edge.StartNode == node.Id;
            var tan = startsHere ? edge.Curve.Tangent(0) : -edge.Curve.Tangent(1);
            var dir = new Vector2(tan.X, tan.Z);
            dir = dir.LengthSquared() > 0 ? Vector2.Normalize(dir) : Vector2.UnitX;
            var type = RoadCatalog.Get(edge.Type);
            return new Leg
            {
                Edge = edge,
                Dir = dir,
                FullHalf = type.OuterHalf,
                CwHalf = type.CarriagewayHalf,
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
