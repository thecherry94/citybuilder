using System.Numerics;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tools;

public enum SnapKind { Free, Node, Edge, GuidelineIntersection, Guideline, Angle }

[Flags]
public enum SnapTypes
{
    None = 0,
    Nodes = 1,
    Edges = 2,
    Angle = 4,
    Guidelines = 8,
    All = Nodes | Edges | Angle | Guidelines,
}

/// <summary>A construction guide: the tangent of an existing road extended past its
/// node, used both for snapping and for rendering dashed guide lines.</summary>
public sealed record Guideline(Vector3 Origin, Vector3 Direction, float Length)
{
    public Vector3 PointAt(float s) => Origin + Direction * s;
}

/// <summary>Drawing context that influences snapping: the previous click (anchor) and the
/// direction angle snap measures from (e.g. the tangent of the road being extended).</summary>
public sealed record SnapContext(Vector3? Anchor, Vector3? ReferenceTangent)
{
    public static readonly SnapContext Empty = new(null, null);
}

public sealed record SnapResult(
    Vector3 Position,
    SnapKind Kind,
    NodeId? Node,
    (EdgeId Edge, float T)? Edge,
    float? SnappedAngleDeg,
    IReadOnlyList<Guideline> ActiveGuidelines)
{
    public static SnapResult Free(Vector3 p)
        => new(p, SnapKind.Free, null, null, null, Array.Empty<Guideline>());
}

/// <summary>Resolves a raw ground point to a snapped position, honoring the enabled
/// snap types with priority Node > GuidelineIntersection > Edge > Guideline > Angle > Free.</summary>
public sealed class SnapService(RoadNetwork network)
{
    private const float GuidelineReach = 200f;   // how far guides extend past nodes
    private const float GuidelineSearch = 200f;  // only nodes this close spawn guides
    private const float AngleStepDeg = 15f;

    public SnapResult Resolve(Vector3 raw, float radius, SnapTypes enabled, SnapContext ctx)
    {
        var guidelines = (enabled & SnapTypes.Guidelines) != 0
            ? CollectGuidelines(raw)
            : new List<Guideline>();

        if ((enabled & SnapTypes.Nodes) != 0 && network.FindNodeNear(raw, radius) is { } nodeId)
        {
            var pos = network.Nodes[nodeId].Position;
            return new SnapResult(pos, SnapKind.Node, nodeId, null, null, NearbyGuides(guidelines, pos, radius));
        }

        if ((enabled & SnapTypes.Guidelines) != 0
            && GuidelineIntersection(guidelines, raw, radius) is var (gi, giGuides) && gi is { } giPos)
            return new SnapResult(giPos, SnapKind.GuidelineIntersection, null, null, null, giGuides);

        if ((enabled & SnapTypes.Edges) != 0 && network.FindClosestEdge(raw, radius) is { } hit)
        {
            var pos = network.Edges[hit.id].Curve.Point(hit.t);
            return new SnapResult(pos, SnapKind.Edge, null, (hit.id, hit.t), null, NearbyGuides(guidelines, pos, radius));
        }

        if ((enabled & SnapTypes.Guidelines) != 0)
        {
            Guideline? best = null;
            float bestD = radius;
            Vector3 bestPos = raw;
            foreach (var g in guidelines)
            {
                if (ProjectOntoGuide(g, raw) is not { } p)
                    continue;
                float d = Vector3.Distance(p, raw);
                if (d <= bestD) { bestD = d; best = g; bestPos = p; }
            }
            if (best is not null)
                return new SnapResult(bestPos, SnapKind.Guideline, null, null, null, new[] { best });
        }

        if ((enabled & SnapTypes.Angle) != 0 && ctx.Anchor is { } anchor)
        {
            var v = raw - anchor;
            v.Y = 0;
            float len = v.Length();
            if (len > GeoConstants.Eps)
            {
                var reference = ctx.ReferenceTangent is { } rt && new Vector2(rt.X, rt.Z).LengthSquared() > 0
                    ? Vector3.Normalize(new Vector3(rt.X, 0, rt.Z))
                    : Vector3.UnitX;
                float rel = SignedAngleDeg(reference, v / len);
                float snapped = MathF.Round(rel / AngleStepDeg) * AngleStepDeg;
                var dir = RotateXZ(reference, snapped * MathF.PI / 180f);
                return new SnapResult(anchor + dir * len, SnapKind.Angle, null, null, snapped,
                    Array.Empty<Guideline>());
            }
        }

        return SnapResult.Free(raw);
    }

    private List<Guideline> CollectGuidelines(Vector3 near)
    {
        var guides = new List<Guideline>();
        foreach (var node in network.Nodes.Values)
        {
            if (Vector3.Distance(node.Position, near) > GuidelineSearch)
                continue;
            foreach (var edgeId in node.Edges)
            {
                var edge = network.Edges[edgeId];
                bool startsHere = edge.StartNode == node.Id;
                var leaving = startsHere ? edge.Curve.Tangent(0) : -edge.Curve.Tangent(1);
                leaving.Y = 0;
                if (leaving.LengthSquared() < GeoConstants.Eps)
                    continue;
                // extend the road's line past the node, away from the edge
                guides.Add(new Guideline(node.Position, Vector3.Normalize(-leaving), GuidelineReach));
            }
        }
        return guides;
    }

    private static (Vector3?, IReadOnlyList<Guideline>) GuidelineIntersection(
        List<Guideline> guides, Vector3 raw, float radius)
    {
        Vector3? best = null;
        float bestD = radius;
        var parents = new List<Guideline>();
        for (int i = 0; i < guides.Count; i++)
        for (int j = i + 1; j < guides.Count; j++)
        {
            var a = guides[i];
            var b = guides[j];
            if (!BezierOps.SegmentIntersect(
                    new Vector2(a.Origin.X, a.Origin.Z), new Vector2(a.PointAt(a.Length).X, a.PointAt(a.Length).Z),
                    new Vector2(b.Origin.X, b.Origin.Z), new Vector2(b.PointAt(b.Length).X, b.PointAt(b.Length).Z),
                    out float u, out _))
                continue;
            var p = a.PointAt(u * a.Length);
            float d = Vector3.Distance(p, raw);
            if (d <= bestD)
            {
                bestD = d;
                best = p;
                parents.Clear();
                parents.Add(a);
                parents.Add(b);
            }
        }
        return (best, parents);
    }

    private static Vector3? ProjectOntoGuide(Guideline g, Vector3 p)
    {
        float s = Vector3.Dot(p - g.Origin, g.Direction);
        if (s < 0 || s > g.Length)
            return null;
        return g.PointAt(s);
    }

    private static IReadOnlyList<Guideline> NearbyGuides(List<Guideline> guides, Vector3 pos, float radius)
        => guides.Where(g => ProjectOntoGuide(g, pos) is { } p && Vector3.Distance(p, pos) <= radius).ToArray();

    private static float SignedAngleDeg(Vector3 from, Vector3 to)
    {
        float cross = from.X * to.Z - from.Z * to.X;
        float dot = from.X * to.X + from.Z * to.Z;
        return MathF.Atan2(cross, dot) * 180f / MathF.PI;
    }

    private static Vector3 RotateXZ(Vector3 v, float rad)
    {
        float c = MathF.Cos(rad), s = MathF.Sin(rad);
        return new Vector3(v.X * c - v.Z * s, 0, v.X * s + v.Z * c);
    }
}
