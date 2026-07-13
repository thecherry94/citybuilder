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
    private const float CrosswalkInset = 0.5f;   // gap between cut and zebra band
    private const float CrosswalkLength = 2.8f;  // zebra bar length (walking direction)
    private const float CrosswalkBar = 0.55f, CrosswalkGap = 0.45f;
    private const float CrosswalkMargin = 0.35f;

    private static int _triCount;

    /// <summary>Build the junction's paint surface, or null when there is nothing to
    /// draw. Nodes with 3+ approaches get full intersection paint; degree-2 corners
    /// get their road markings continued around the bend; dead ends stay bare.</summary>
    public static SurfaceTool? Build(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)
    {
        if (node.Edges.Count < 2)
            return null;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetMaterial(Materials.Marking);
        _triCount = 0;

        if (node.Edges.Count == 2)
        {
            AddCornerContinuation(st, node, edges);
            return _triCount > 0 ? st : null;
        }

        var movementsByLane = node.Connectors
            .GroupBy(c => c.From)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Turn).ToHashSet());

        foreach (var edgeId in node.Edges)
        {
            if (node.Junction.TightCuts.Contains(edgeId))
                continue; // edge too short for a full junction: no paint on this leg

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

            var type = RoadCatalog.Get(edge.Type);
            if (type.HasSidewalks)
                AddCrosswalk(st, edge, type, tCut, startsHere);
        }

        // guidance paint: driving lanes' left turns only (right turns hug the corner),
        // and one line per movement — the tightest connector of each approach→exit
        // pair — rather than one per lane pair, which reads as a starburst
        var laneEdge = new Dictionary<LaneId, EdgeId>();
        var laneInfo = new Dictionary<LaneId, Lane>();
        foreach (var edgeId in node.Edges)
        foreach (var lane in edges[edgeId].Lanes)
        {
            laneEdge[lane.Id] = edgeId;
            laneInfo[lane.Id] = lane;
        }

        var leftMovements = node.Connectors
            .Where(c => c.Turn == TurnKind.Left && laneInfo[c.From].Kind == LaneKind.Driving)
            .GroupBy(c => (From: laneEdge[c.From], To: laneEdge[c.To]))
            .Select(g => g.MinBy(c => c.Curve.Length())!);
        foreach (var connector in leftMovements)
            // guidance paint runs along the *left edge* of the turning path, not the
            // lane center (negative offset = driver's left)
            AddGuidanceDashes(st, connector.Curve, -laneInfo[connector.From].Width / 2);

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

    // ------------------------------------------------------ corner continuation

    /// <summary>Degree-2 nodes (90° bends, curves too sharp to heal) get their road
    /// markings swept around the corner along a tangent-continuous path, so lines
    /// don't just stop at the junction cuts.</summary>
    private static void AddCornerContinuation(SurfaceTool st, RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)
    {
        if (node.Junction.SurfacePolygon.Count == 0)
            return; // seamless continuation: the edges already carry their markings

        var ids = node.Edges.ToArray();
        var a = edges[ids[0]];
        var b = edges[ids[1]];
        if (a.Type != b.Type)
            return; // type transitions would need taper markings; skip for now
        if (!node.Junction.CutT.TryGetValue(a.Id, out float tA)
            || !node.Junction.CutT.TryGetValue(b.Id, out float tB))
            return;

        bool aEnds = a.EndNode == node.Id;
        bool bStarts = b.StartNode == node.Id;
        var pa = a.Curve.Point(tA);
        var pb = b.Curve.Point(tB);
        var dirIn = aEnds ? a.Curve.Tangent(tA) : -a.Curve.Tangent(tA);
        var dirOut = bStarts ? b.Curve.Tangent(tB) : -b.Curve.Tangent(tB);
        float chord = System.Numerics.Vector3.Distance(pa, pb);
        if (chord < 0.3f)
            return;

        // offsets are given in edge a's frame; flip when a points away from the node
        float flipA = aEnds ? 1f : -1f;
        float flipB = bStarts ? 1f : -1f;
        var type = RoadCatalog.Get(a.Type);

        // every marking line follows its own corner: quadratic through the
        // intersection of its two offset lines — the same construction the curb
        // returns use, so lines stay parallel to the curbs (offsets of one shared
        // curve would cusp inside and chamfer outside on wide roads)
        foreach (var (offset, dashed) in MeshBuilders.MarkingLayout(type))
        {
            var paO = a.Curve.OffsetPoint(tA, offset);
            var pbO = b.Curve.OffsetPoint(tB, flipA * flipB * offset);
            // corner must lie ahead of the entry and behind the exit; otherwise
            // (near-straight or diverging offset lines) a straight join is correct
            var corner = IntersectXZ(paO, dirIn, pbO, dirOut);
            bool valid = corner is { } c
                && System.Numerics.Vector3.Dot(c - paO, dirIn) >= 0
                && System.Numerics.Vector3.Dot(c - pbO, dirOut) <= 0;
            var curve = valid
                ? Bezier3.FromQuadratic(paO, corner!.Value, pbO)
                : Bezier3.Line(paO, pbO);
            SweepLine(st, curve, new ArcLengthTable(curve, 24), 0f, dashed, centerOnApex: true);
        }
    }

    /// <summary>Intersection of two lines in the XZ plane (point + direction each).</summary>
    private static System.Numerics.Vector3? IntersectXZ(
        System.Numerics.Vector3 p, System.Numerics.Vector3 dp,
        System.Numerics.Vector3 q, System.Numerics.Vector3 dq)
    {
        float det = dp.X * -dq.Z - dp.Z * -dq.X;
        if (MathF.Abs(det) < 1e-4f)
            return null;
        float rx = q.X - p.X, rz = q.Z - p.Z;
        float s = (rx * -dq.Z - rz * -dq.X) / det;
        return p + dp * s;
    }

    private static void SweepLine(SurfaceTool st, Bezier3 curve, ArcLengthTable table, float offset, bool dashed,
        bool centerOnApex = false)
    {
        float total = table.TotalLength;
        float hw = MeshBuilders.MarkingLineWidth / 2;

        void Quad(float d0, float d1)
        {
            const int steps = 3; // short spans: a few segments follow the bend fine
            var up = Vector3.Up * MeshBuilders.MarkingY;
            for (int s = 0; s < steps; s++)
            {
                float ta = table.TAtDistance(d0 + (d1 - d0) * s / steps);
                float tb = table.TAtDistance(d0 + (d1 - d0) * (s + 1) / steps);
                AddQuadUp(st,
                    curve.OffsetPoint(ta, offset - hw).ToGodot() + up,
                    curve.OffsetPoint(ta, offset + hw).ToGodot() + up,
                    curve.OffsetPoint(tb, offset + hw).ToGodot() + up,
                    curve.OffsetPoint(tb, offset - hw).ToGodot() + up);
            }
        }

        if (!dashed)
        {
            Quad(0, total);
            return;
        }

        const float period = MeshBuilders.MarkingDashOn + MeshBuilders.MarkingDashOff;

        if (centerOnApex)
        {
            // bends put all their curvature at mid-arc: a dash gap there reads as a
            // straight chord hugging the inner curb, so pin a dash onto the apex
            float half = MeshBuilders.MarkingDashOn / 2;
            float first = total / 2 - period * MathF.Ceiling(total / (2 * period));
            for (float c = first; c < total + half; c += period)
            {
                float d0 = MathF.Max(0, c - half);
                float d1 = MathF.Min(total, c + half);
                if (d1 - d0 > 0.6f)
                    Quad(d0, d1);
            }
            return;
        }

        int count = (int)MathF.Floor((total + MeshBuilders.MarkingDashOff) / period);
        if (count < 1)
        {
            // too short for a full dash: paint one centered stub so the line reads
            if (total > 1f)
                Quad(total / 2 - 0.5f, total / 2 + 0.5f);
            return;
        }
        float used = count * period - MeshBuilders.MarkingDashOff;
        float lead = MathF.Max(0, (total - used) / 2);
        for (int k = 0; k < count; k++)
        {
            float d0 = lead + k * period;
            Quad(d0, MathF.Min(d0 + MeshBuilders.MarkingDashOn, total));
        }
    }

    // ---------------------------------------------------------------- crosswalk

    /// <summary>Zebra band across the carriageway, just inside the junction from the
    /// cut, connecting the raised corner sidewalks on either side.</summary>
    private static void AddCrosswalk(SurfaceTool st, RoadEdge edge, RoadType type, float tCut, bool startsHere)
    {
        float cwHalf = type.CarriagewayHalf;
        var basePt = edge.Curve.Point(tCut).ToGodot();
        var tangent = edge.Curve.Tangent(tCut).ToGodot();
        var toNode = startsHere ? -tangent : tangent; // from the cut into the junction
        toNode.Y = 0;
        if (toNode.LengthSquared() < 1e-8f)
            return;
        toNode = toNode.Normalized();
        var right = toNode.Cross(Vector3.Up);
        basePt.Y = MeshBuilders.MarkingY;

        float x = -cwHalf + CrosswalkMargin + CrosswalkBar / 2;
        for (; x + CrosswalkBar / 2 <= cwHalf - CrosswalkMargin; x += CrosswalkBar + CrosswalkGap)
        {
            var near = basePt + toNode * CrosswalkInset;
            var far = basePt + toNode * (CrosswalkInset + CrosswalkLength);
            AddQuadUp(st,
                near + right * (x - CrosswalkBar / 2),
                near + right * (x + CrosswalkBar / 2),
                far + right * (x + CrosswalkBar / 2),
                far + right * (x - CrosswalkBar / 2));
        }
    }

    // --------------------------------------------------------- guidance dashes

    private static void AddGuidanceDashes(SurfaceTool st, in Bezier3 curve, float lateral)
    {
        var table = new ArcLengthTable(curve, 32);
        float total = table.TotalLength;
        if (total < MinGuidanceLength)
            return;
        var up = Vector3.Up * (MeshBuilders.MarkingY - 0.005f);
        // start past the crosswalk band so guidance paint doesn't overlap the zebra
        float first = MathF.Max(GuidanceDashOff / 2, CrosswalkInset + CrosswalkLength + 0.5f);
        for (float d = first; d < total - 0.3f; d += GuidanceDashOn + GuidanceDashOff)
        {
            float d1 = MathF.Min(d + GuidanceDashOn, total - 0.1f);
            float ta = table.TAtDistance(d);
            float tb = table.TAtDistance(d1);
            var a1 = curve.OffsetPoint(ta, lateral - GuidanceWidth / 2).ToGodot() + up;
            var a2 = curve.OffsetPoint(ta, lateral + GuidanceWidth / 2).ToGodot() + up;
            var b1 = curve.OffsetPoint(tb, lateral - GuidanceWidth / 2).ToGodot() + up;
            var b2 = curve.OffsetPoint(tb, lateral + GuidanceWidth / 2).ToGodot() + up;
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
