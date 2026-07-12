using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Godot;

namespace CityBuilder.Game;

/// <summary>
/// Intersection paint, derived entirely from the lane/connector graph so it adapts
/// to any road combination (asymmetric approaches, unequal lane counts):
/// - stop lines across the incoming lanes of every approach
/// - a turn arrow on each incoming lane, composed from that lane's actual movements
/// - dashed guidance lines along turning connectors through the junction
/// </summary>
public static class JunctionMarkings
{
    private const float StopLineThickness = 0.5f;
    private const float StopLineGapToJunction = 0.3f;
    private const float ArrowGapToStopLine = 2.5f;
    private const float MinLaneLengthForArrow = 16f;
    private const float GuidanceDashOn = 0.8f, GuidanceDashOff = 1.0f;
    private const float GuidanceWidth = 0.12f;
    private const float MinGuidanceLength = 10f; // small junctions carry no guidance paint

    private static int _triCount;

    /// <summary>Build the junction's paint surface, or null when there is nothing to
    /// draw. Only nodes with 3+ approaches get markings (corners/dead ends stay bare).</summary>
    public static SurfaceTool? Build(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)
    {
        if (node.Edges.Count < 3)
            return null;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        _triCount = 0;

        var movementsByLane = node.Connectors
            .GroupBy(c => c.From)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Turn).ToHashSet());

        foreach (var edgeId in node.Edges)
        {
            var edge = edges[edgeId];
            bool startsHere = edge.StartNode == node.Id;
            float tCut = node.Junction.CutT.TryGetValue(edgeId, out var t) ? t : (startsHere ? 0f : 1f);

            var incoming = edge.Lanes
                .Where(l => l.Kind == LaneKind.Driving
                    && (l.Direction == LaneDirection.Forward ? !startsHere : startsHere))
                .ToList();
            if (incoming.Count == 0)
                continue;

            AddStopLine(st, edge, incoming, tCut, startsHere);

            foreach (var lane in incoming)
                if (movementsByLane.TryGetValue(lane.Id, out var moves))
                    AddTurnArrow(st, node, edge, lane, tCut, startsHere, moves);
        }

        // guidance paint: left turns only (right turns hug the corner), and one line
        // per movement — the tightest connector of each approach→exit pair — rather
        // than one per lane pair, which reads as a starburst on wide roads
        var laneEdge = new Dictionary<LaneId, EdgeId>();
        foreach (var edgeId in node.Edges)
        foreach (var lane in edges[edgeId].Lanes)
            laneEdge[lane.Id] = edgeId;

        var leftMovements = node.Connectors
            .Where(c => c.Turn == TurnKind.Left)
            .GroupBy(c => (From: laneEdge[c.From], To: laneEdge[c.To]))
            .Select(g => g.MinBy(c => c.Curve.Length())!);
        foreach (var connector in leftMovements)
            AddGuidanceDashes(st, connector.Curve);

        return _triCount > 0 ? st : null;
    }

    // ---------------------------------------------------------------- stop line

    private static void AddStopLine(SurfaceTool st, RoadEdge edge, List<Lane> incoming, float tCut, bool startsHere)
    {
        float lo = incoming.Min(l => l.Offset - l.Width / 2) + 0.1f;
        float hi = incoming.Max(l => l.Offset + l.Width / 2) - 0.1f;

        // place the line just before the junction, along the approach direction
        float dCut = edge.ArcLength.DistanceAtT(tCut);
        float dNear = startsHere ? dCut + StopLineGapToJunction : dCut - StopLineGapToJunction;
        float dFar = startsHere ? dNear + StopLineThickness : dNear - StopLineThickness;
        float ta = edge.ArcLength.TAtDistance(dNear);
        float tb = edge.ArcLength.TAtDistance(dFar);

        AddLateralQuad(st, edge, ta, tb, lo, hi);
    }

    private static void AddLateralQuad(SurfaceTool st, RoadEdge edge, float ta, float tb, float lo, float hi)
    {
        var up = Vector3.Up * MeshBuilders.MarkingY;
        var a1 = edge.Curve.OffsetPoint(ta, lo).ToGodot() + up;
        var a2 = edge.Curve.OffsetPoint(ta, hi).ToGodot() + up;
        var b1 = edge.Curve.OffsetPoint(tb, lo).ToGodot() + up;
        var b2 = edge.Curve.OffsetPoint(tb, hi).ToGodot() + up;
        AddQuadUp(st, a1, a2, b2, b1);
    }

    // -------------------------------------------------------------- turn arrows

    private static void AddTurnArrow(SurfaceTool st, RoadNode node, RoadEdge edge, Lane lane,
        float tCut, bool startsHere, HashSet<TurnKind> moves)
    {
        moves.Remove(TurnKind.UTurn);
        if (moves.Count == 0)
            return;
        if (edge.ArcLength.TotalLength < MinLaneLengthForArrow)
            return;

        float dCut = edge.ArcLength.DistanceAtT(tCut);
        float glyphLen = ArrowGlyph.Length;
        float dTip = startsHere
            ? dCut + StopLineGapToJunction + StopLineThickness + ArrowGapToStopLine
            : dCut - StopLineGapToJunction - StopLineThickness - ArrowGapToStopLine;
        float dBase = startsHere ? dTip + glyphLen : dTip - glyphLen;
        if (dBase < 1f || dBase > edge.ArcLength.TotalLength - 1f)
            return;

        // local frame at the arrow position: forward = travel direction
        float tMid = edge.ArcLength.TAtDistance((dTip + dBase) / 2);
        var tangent = edge.Curve.Tangent(tMid).ToGodot();
        var forward = startsHere ? -tangent : tangent; // travel toward the node
        forward.Y = 0;
        if (forward.LengthSquared() < 1e-8f)
            return;
        forward = forward.Normalized();
        var right = forward.Cross(Vector3.Up); // driver's right (heading +X → +Z)
        var origin = edge.Curve.OffsetPoint(edge.ArcLength.TAtDistance(dBase), lane.Offset).ToGodot();
        origin.Y = MeshBuilders.MarkingY;

        foreach (var tri in ArrowGlyph.Triangles(moves))
            AddTriangle(st,
                origin + forward * tri.a.Y + right * tri.a.X,
                origin + forward * tri.b.Y + right * tri.b.X,
                origin + forward * tri.c.Y + right * tri.c.X);
    }

    // --------------------------------------------------------- guidance dashes

    private static void AddGuidanceDashes(SurfaceTool st, in Bezier3 curve)
    {
        var table = new ArcLengthTable(curve, 32);
        float total = table.TotalLength;
        if (total < MinGuidanceLength)
            return;
        var up = Vector3.Up * (MeshBuilders.MarkingY - 0.005f);
        for (float d = GuidanceDashOff / 2; d < total - 0.3f; d += GuidanceDashOn + GuidanceDashOff)
        {
            float d1 = MathF.Min(d + GuidanceDashOn, total - 0.1f);
            float ta = table.TAtDistance(d);
            float tb = table.TAtDistance(d1);
            var a1 = curve.OffsetPoint(ta, -GuidanceWidth / 2).ToGodot() + up;
            var a2 = curve.OffsetPoint(ta, +GuidanceWidth / 2).ToGodot() + up;
            var b1 = curve.OffsetPoint(tb, -GuidanceWidth / 2).ToGodot() + up;
            var b2 = curve.OffsetPoint(tb, +GuidanceWidth / 2).ToGodot() + up;
            AddQuadUp(st, a1, a2, b2, b1);
        }
    }

    // ------------------------------------------------------------------ shared

    private static void AddQuadUp(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        AddTriangle(st, a, b, c);
        AddTriangle(st, a, c, d);
    }

    private static void AddTriangle(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
    {
        if ((c - a).Cross(b - a).Y < 0)
            (b, c) = (c, b);
        st.SetNormal(Vector3.Up);
        st.AddVertex(a);
        st.SetNormal(Vector3.Up);
        st.AddVertex(b);
        st.SetNormal(Vector3.Up);
        st.AddVertex(c);
        _triCount++;
    }
}

/// <summary>Road-paint arrow glyphs in a local frame: +Y toward the junction
/// (travel direction), +X to the driver's right, origin at the glyph base.
/// Composable: one shaft, plus a head per allowed movement.</summary>
public static class ArrowGlyph
{
    public const float Length = 4.2f;

    private const float ShaftHalf = 0.18f;
    private const float ShaftTop = 2.6f;

    public readonly record struct Tri(Vector2 a, Vector2 b, Vector2 c);

    public static IEnumerable<Tri> Triangles(IReadOnlySet<TurnKind> moves)
    {
        bool straight = moves.Contains(TurnKind.Straight);
        bool left = moves.Contains(TurnKind.Left);
        bool right = moves.Contains(TurnKind.Right);

        // shaft (shorter when there is no straight head so side heads dominate)
        float shaftTop = straight ? ShaftTop : 2.0f;
        foreach (var t in Quad(new(-ShaftHalf, 0), new(ShaftHalf, 0), new(ShaftHalf, shaftTop), new(-ShaftHalf, shaftTop)))
            yield return t;

        if (straight)
        {
            yield return new Tri(new(-0.55f, ShaftTop), new(0.55f, ShaftTop), new(0, Length));
        }

        // stagger the side branches when both are present so the glyph doesn't
        // collapse into a plus sign
        if (left)
        {
            foreach (var t in SideBranch(-1f, shaftTop))
                yield return t;
        }

        if (right)
        {
            // stagger only three-way glyphs; a pure left/right fork keeps level heads
            foreach (var t in SideBranch(+1f, left && straight ? shaftTop - 0.9f : shaftTop))
                yield return t;
        }
    }

    private static IEnumerable<Tri> SideBranch(float side, float shaftTop)
    {
        float y0 = shaftTop - 1.1f;
        float y1 = shaftTop - 0.55f;
        float xReach = side * 0.85f;
        // branch bar from the shaft sideways
        foreach (var t in Quad(new(0, y0), new(xReach, y0), new(xReach, y1), new(0, y1)))
            yield return t;
        // head pointing sideways
        yield return new Tri(
            new(xReach, y0 - 0.45f),
            new(xReach, y1 + 0.45f),
            new(xReach + side * 0.75f, (y0 + y1) / 2));
    }

    private static IEnumerable<Tri> Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        yield return new Tri(a, b, c);
        yield return new Tri(a, c, d);
    }
}
