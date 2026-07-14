using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tools;

/// <summary>One potential snap target. Score = distance / weight; lowest wins,
/// so heavier kinds win ties but a dead-on weak snap beats a distant strong one.</summary>
public readonly record struct SnapCandidate(
    Vector3 Position,
    SnapKind Kind,
    float Weight,
    NodeId? Node = null,
    (EdgeId Edge, float T)? Edge = null,
    Vector3? Direction = null,
    Guideline? Guide = null,
    Guideline? Guide2 = null);

/// <summary>Candidate-scored snap resolution: every enabled producer emits position
/// candidates, the best score wins; with no winner, angle snap (relative to the
/// context's reference tangent) is the directional fallback, then Free.</summary>
public sealed class SnapEngine(RoadNetwork network)
{
    public const float GuidelineReach = 200f;
    public const float GuidelineSearch = 200f;
    public const float AngleStepDeg = 15f;

    // node is 4.0 (not the spec's sketched 3.0): with 3.0, a node 1.9 m away
    // loses to the edge underneath it 1.2 m away — the ported NodeBeatsEdge test fails
    public const float WeightNode = 4.0f;
    public const float WeightGuideIntersection = 2.5f;
    public const float WeightPerpendicular = 2.2f;
    public const float WeightEdge = 2.0f;
    public const float WeightGuideline = 1.5f;
    public const float WeightGridPoint = 1.5f;
    public const float WeightGridLine = 1.0f;

    public SnapResult Resolve(Vector3 raw, float radius, SnapTypes enabled, SnapContext ctx)
    {
        var guidelines = (enabled & SnapTypes.Guidelines) != 0
            ? CollectGuidelines(raw, enabled, ctx)
            : new List<Guideline>();

        var candidates = new List<SnapCandidate>();
        if ((enabled & SnapTypes.Nodes) != 0)
            AddNodeCandidates(raw, radius, candidates);
        if ((enabled & SnapTypes.Edges) != 0)
            AddEdgeCandidates(raw, radius, candidates);
        if ((enabled & SnapTypes.Guidelines) != 0)
            AddGuidelineCandidates(guidelines, raw, radius, candidates);
        if ((enabled & SnapTypes.Perpendicular) != 0 && ctx.Anchor is { } anchor)
            AddPerpendicularCandidates(raw, radius, anchor, candidates);
        if ((enabled & SnapTypes.Grid) != 0 && ctx.Grid is { } grid)
            AddGridCandidates(raw, radius, grid, candidates);

        SnapCandidate? best = null;
        float bestScore = float.MaxValue;
        foreach (var c in candidates)
        {
            float d = Vector3.Distance(c.Position, raw);
            if (d > radius)
                continue;
            float score = d / c.Weight;
            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        if (best is { } win)
        {
            var active = win.Guide is { } g
                ? (win.Guide2 is { } g2 ? new[] { g, g2 } : new[] { g })
                : NearbyGuides(guidelines, win.Position, radius);
            return new SnapResult(win.Position, win.Kind, win.Node, win.Edge, null, active, win.Direction);
        }

        if ((enabled & SnapTypes.Angle) != 0 && ctx.Anchor is { } a2 && AngleSnap(raw, a2, ctx) is { } angled)
            return angled;

        return SnapResult.Free(raw);
    }

    // ------------------------------------------------------------- producers

    private void AddNodeCandidates(Vector3 raw, float radius, List<SnapCandidate> outList)
    {
        foreach (var n in network.Nodes.Values)
            if (Vector3.Distance(n.Position, raw) <= radius)
                outList.Add(new SnapCandidate(n.Position, SnapKind.Node, WeightNode, Node: n.Id));
    }

    private void AddEdgeCandidates(Vector3 raw, float radius, List<SnapCandidate> outList)
    {
        if (network.FindClosestEdge(raw, radius) is { } hit)
        {
            var pos = network.Edges[hit.id].Curve.Point(hit.t);
            outList.Add(new SnapCandidate(pos, SnapKind.Edge, WeightEdge, Edge: (hit.id, hit.t)));
        }
    }

    private static void AddGuidelineCandidates(List<Guideline> guides, Vector3 raw, float radius,
        List<SnapCandidate> outList)
    {
        // pairwise intersections
        for (int i = 0; i < guides.Count; i++)
        for (int j = i + 1; j < guides.Count; j++)
        {
            var a = guides[i];
            var b = guides[j];
            if (!BezierOps.SegmentIntersect(
                    new Vector2(a.Origin.X, a.Origin.Z),
                    new Vector2(a.PointAt(a.Length).X, a.PointAt(a.Length).Z),
                    new Vector2(b.Origin.X, b.Origin.Z),
                    new Vector2(b.PointAt(b.Length).X, b.PointAt(b.Length).Z),
                    out float u, out _))
                continue;
            var p = a.PointAt(u * a.Length);
            if (Vector3.Distance(p, raw) <= radius)
                outList.Add(new SnapCandidate(p, SnapKind.GuidelineIntersection, WeightGuideIntersection,
                    Guide: a, Guide2: b));
        }
        // projections
        foreach (var g in guides)
            if (ProjectOntoGuide(g, raw) is { } p)
                outList.Add(new SnapCandidate(p, SnapKind.Guideline, WeightGuideline, Guide: g));
    }

    private void AddPerpendicularCandidates(Vector3 raw, float radius, Vector3 anchor,
        List<SnapCandidate> outList)
    {
        foreach (var e in network.Edges.Values)
        {
            // f(t) = (P(t) − anchor) · T(t) is zero where the chord is perpendicular
            const int coarse = 32;
            float F(float t)
            {
                var p = e.Curve.Point(t) - anchor;
                var tan = e.Curve.Tangent(t);
                return p.X * tan.X + p.Z * tan.Z;
            }
            float f0 = F(0);
            for (int i = 1; i <= coarse; i++)
            {
                float t1 = i / (float)coarse;
                float f1 = F(t1);
                if (f0 * f1 <= 0 && (f0 != 0 || f1 != 0))
                {
                    float lo = (i - 1) / (float)coarse, hi = t1;
                    for (int k = 0; k < 24; k++)
                    {
                        float mid = (lo + hi) / 2;
                        if (F(lo) * F(mid) <= 0) hi = mid;
                        else lo = mid;
                    }
                    float tm = (lo + hi) / 2;
                    var foot = e.Curve.Point(tm);
                    var dir = foot - anchor;
                    dir.Y = 0;
                    if (Vector3.Distance(foot, raw) <= radius && dir.LengthSquared() > 1e-6f)
                        outList.Add(new SnapCandidate(foot, SnapKind.Perpendicular, WeightPerpendicular,
                            Edge: (e.Id, tm), Direction: Vector3.Normalize(dir)));
                }
                f0 = f1;
            }
        }
    }

    private static void AddGridCandidates(Vector3 raw, float radius, GridConfig grid,
        List<SnapCandidate> outList)
    {
        float cs = grid.CellSize;
        float gx = MathF.Round(raw.X / cs) * cs;
        float gz = MathF.Round(raw.Z / cs) * cs;

        var point = new Vector3(gx, raw.Y, gz);
        if (Vector3.Distance(point, raw) <= radius)
            outList.Add(new SnapCandidate(point, SnapKind.GridPoint, WeightGridPoint));

        // nearest grid line: keep the closer axis projection
        var lineX = new Vector3(gx, raw.Y, raw.Z);   // vertical line x = gx
        var lineZ = new Vector3(raw.X, raw.Y, gz);   // horizontal line z = gz
        var line = MathF.Abs(raw.X - gx) <= MathF.Abs(raw.Z - gz) ? lineX : lineZ;
        if (Vector3.Distance(line, raw) <= radius)
            outList.Add(new SnapCandidate(line, SnapKind.GridLine, WeightGridLine));
    }

    // ---------------------------------------------------------------- guides

    private List<Guideline> CollectGuidelines(Vector3 near, SnapTypes enabled, SnapContext ctx)
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
                guides.Add(new Guideline(node.Position, Vector3.Normalize(-leaving), GuidelineReach));
            }
        }
        // Task 9 adds parallel guides here when (enabled & SnapTypes.Parallel) != 0.
        return guides;
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

    // ----------------------------------------------------------------- angle

    private static SnapResult? AngleSnap(Vector3 raw, Vector3 anchor, SnapContext ctx)
    {
        var v = raw - anchor;
        v.Y = 0;
        float len = v.Length();
        if (len <= GeoConstants.Eps)
            return null;
        var reference = ctx.ReferenceTangent is { } rt && new Vector2(rt.X, rt.Z).LengthSquared() > 0
            ? Vector3.Normalize(new Vector3(rt.X, 0, rt.Z))
            : Vector3.UnitX;
        float rel = SignedAngleDeg(reference, v / len);
        float snapped = MathF.Round(rel / AngleStepDeg) * AngleStepDeg;
        var dir = RotateXZ(reference, snapped * MathF.PI / 180f);
        return new SnapResult(anchor + dir * len, SnapKind.Angle, null, null, snapped,
            Array.Empty<Guideline>());
    }

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
