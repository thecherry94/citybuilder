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

    // Hard node capture (M6.75 spec §1): within this fraction of the resolve radius a
    // node wins outright over every soft candidate — the T-junction "slides along the
    // leg" fix. CS2 uses the same tier-then-distance architecture (net candidates
    // score at a hard higher tier than guides/grid; see the M6.75 research notes).
    public const float NodeCaptureFraction = 0.6f;

    // Hysteresis (M6.75 spec §1): a captured node only releases when the cursor leaves
    // ReleaseFactor × the capture ring — kills candidate flicker, CS2's top complaint.
    // Node captures only; the engine stays stateless (the session remembers the held
    // node between resolves via SnapContext.HeldNode).
    public const float ReleaseFactor = 1.4f;

    // CS2's zoning-cell rhythm (Game.Zones.ZoneUtils.CELL_SIZE = 8f): with an anchor,
    // segment length ratchets in 8 m ticks. Weak — loses to any geometry snap nearby.
    public const float CellLength = 8f;
    public const float WeightCellLength = 1.2f;

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
        {
            if (HardNodeCapture(raw, radius, ctx) is { } captured)
                return new SnapResult(captured.Position, SnapKind.Node, captured.Id, null, null,
                    NearbyGuides(guidelines, captured.Position, radius));
            AddNodeCandidates(raw, radius, candidates);
        }
        if ((enabled & SnapTypes.Edges) != 0)
            AddEdgeCandidates(raw, radius, candidates);
        if ((enabled & SnapTypes.Guidelines) != 0)
            AddGuidelineCandidates(guidelines, raw, radius, candidates);
        if ((enabled & SnapTypes.Perpendicular) != 0 && ctx.Anchor is { } anchor)
            AddPerpendicularCandidates(raw, radius, anchor, candidates);
        if ((enabled & SnapTypes.Grid) != 0 && ctx.Grid is { } grid)
            AddGridCandidates(raw, radius, grid, candidates);
        if ((enabled & SnapTypes.CellLength) != 0 && ctx.Anchor is { } cellAnchor)
            AddCellLengthCandidates(raw, radius, cellAnchor, candidates);

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
            return (enabled & SnapTypes.CellLength) != 0 && QuantizeToCell(angled.Position, a2) is { } ticked
                ? angled with { Position = ticked }
                : angled;

        return SnapResult.Free(raw);
    }

    // ------------------------------------------------------------- producers

    /// <summary>Nearest node inside the hard-capture ring, or null. Hysteresis (M6.75
    /// spec §1) extends this via <see cref="SnapContext.HeldNode"/>.</summary>
    private (NodeId Id, Vector3 Position)? HardNodeCapture(Vector3 raw, float radius, SnapContext ctx)
    {
        float captureR = NodeCaptureFraction * radius;
        (NodeId Id, Vector3 Position)? best = null;
        float bestDist = float.MaxValue;
        foreach (var n in network.Nodes.Values)
        {
            float d = Vector3.Distance(n.Position, raw);
            if (d <= captureR && d < bestDist)
            {
                bestDist = d;
                best = (n.Id, n.Position);
            }
        }
        // the held node survives out to the release ring and wins ties; a different
        // node captured strictly closer transfers the hold
        if (ctx.HeldNode is { } heldId && network.Nodes.TryGetValue(heldId, out var held))
        {
            float dHeld = Vector3.Distance(held.Position, raw);
            if (dHeld <= ReleaseFactor * captureR && dHeld <= bestDist)
                best = (heldId, held.Position);
        }
        return best;
    }

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

    private static void AddCellLengthCandidates(Vector3 raw, float radius, Vector3 anchor,
        List<SnapCandidate> outList)
    {
        if (QuantizeToCell(raw, anchor) is { } pos && Vector3.Distance(pos, raw) <= radius)
            outList.Add(new SnapCandidate(pos, SnapKind.CellLength, WeightCellLength));
    }

    /// <summary>Position at the 8 m-quantized distance from the anchor along
    /// anchor→p, or null when degenerate/zero-length.</summary>
    private static Vector3? QuantizeToCell(Vector3 p, Vector3 anchor)
    {
        var v = p - anchor;
        v.Y = 0;
        float d = v.Length();
        if (d < GeoConstants.Eps)
            return null;
        float q = MathF.Round(d / CellLength) * CellLength;
        if (q < CellLength)
            return null;
        return anchor + v / d * q;
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
        if ((enabled & SnapTypes.Parallel) != 0 && ctx.DrawingType is { } drawType)
            AddParallelGuides(near, drawType, guides);
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

    private void AddParallelGuides(Vector3 near, RoadTypeId drawType, List<Guideline> guides)
    {
        float newHalf = RoadCatalog.Get(drawType).OuterHalf;
        foreach (var e in network.Edges.Values)
        {
            var chord = e.Curve.P3 - e.Curve.P0;
            chord.Y = 0;
            float len = chord.Length();
            if (len < 10f)
                continue;
            var dir = chord / len;
            // straightness: both control points within 0.5 m of the chord line (XZ)
            if (DistToLineXZ(e.Curve.P1, e.Curve.P0, dir) > 0.5f
                || DistToLineXZ(e.Curve.P2, e.Curve.P0, dir) > 0.5f)
                continue;
            if (BezierOps.ClosestPoint(e.Curve, near).dist > GuidelineSearch)
                continue;
            float off = RoadCatalog.Get(e.Type).OuterHalf + newHalf;
            var n = Vector3.Cross(dir, Vector3.UnitY);
            n.Y = 0;
            if (n.LengthSquared() < GeoConstants.Eps)
                continue;
            n = Vector3.Normalize(n);
            guides.Add(new Guideline(e.Curve.P0 + n * off, dir, len));
            guides.Add(new Guideline(e.Curve.P0 - n * off, dir, len));
        }
    }

    private static float DistToLineXZ(Vector3 p, Vector3 origin, Vector3 dir)
    {
        var rel = p - origin;
        return MathF.Abs(rel.X * dir.Z - rel.Z * dir.X);
    }

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
