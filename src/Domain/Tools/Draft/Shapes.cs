using System.Numerics;
using CityBuilder.Domain.Geometry;

namespace CityBuilder.Domain.Tools;

/// <summary>Two endpoints. Tangent lock is irrelevant to a straight line's handle
/// count; validation's SharpAngle guard rejects non-tangential exits anyway.</summary>
public sealed class StraightShape : IDraftShape
{
    public int RequiredHandles(bool tangentLocked) => 2;
    public HandleRole RoleOf(int index, bool tangentLocked) => HandleRole.Endpoint;

    public IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent)
    {
        if (handles.Count < 2)
            return null;
        var a = handles[0].Position;
        var b = handles[1].Position;
        return Vector3.Distance(a, b) < GeoConstants.Eps ? null : new[] { Bezier3.Line(a, b) };
    }
}

internal static class ShapeUtil
{
    /// <summary>±tangent, whichever faces <paramref name="toward"/> (XZ).</summary>
    public static Vector3 Orient(Vector3 tangent, Vector3 toward)
    {
        float d = tangent.X * toward.X + tangent.Z * toward.Z;
        return d >= 0 ? tangent : -tangent;
    }

    /// <summary>Re-aim the last control point along the end handle's arrival
    /// direction, preserving its distance to the endpoint (G1 at the far end).</summary>
    public static Bezier3 ApplyArrival(Bezier3 c, DraftHandle end)
    {
        if (end.Snap.DirectionConstraint is not { } dir)
            return c;
        float reach = Vector3.Distance(c.P3, c.P2);
        if (reach < GeoConstants.Eps)
            reach = Vector3.Distance(c.P0, c.P3) / 3f;
        var arrive = Vector3.Normalize(new Vector3(dir.X, 0, dir.Z));
        return new Bezier3(c.P0, c.P1, c.P3 - arrive * reach, c.P3);
    }
}

/// <summary>Start, control, end (3 clicks). Tangent-locked: start, end (control
/// implied on the tangent ray at 40 % of the chord, like the old continuous tool).</summary>
public sealed class QuadCurveShape : IDraftShape
{
    public int RequiredHandles(bool tangentLocked) => tangentLocked ? 2 : 3;

    public HandleRole RoleOf(int index, bool tangentLocked)
        => !tangentLocked && index == 1 ? HandleRole.Control : HandleRole.Endpoint;

    public IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent)
    {
        if (startTangent is { } t0)
        {
            if (handles.Count < 2)
                return null;
            var a = handles[0].Position;
            var b = handles[^1].Position;
            float chord = Vector3.Distance(a, b);
            if (chord < GeoConstants.Eps)
                return null;
            var dir = ShapeUtil.Orient(t0, b - a);
            var curve = Bezier3.FromQuadratic(a, a + dir * (0.4f * chord), b);
            return new[] { ShapeUtil.ApplyArrival(curve, handles[^1]) };
        }
        if (handles.Count < 3)
            return null;
        var q = Bezier3.FromQuadratic(handles[0].Position, handles[1].Position, handles[2].Position);
        return new[] { ShapeUtil.ApplyArrival(q, handles[2]) };
    }
}

/// <summary>Start, two controls, end (4 clicks). Tangent-locked: start, control,
/// end — the first control is implied at a third of the chord along the tangent.</summary>
public sealed class CubicCurveShape : IDraftShape
{
    public int RequiredHandles(bool tangentLocked) => tangentLocked ? 3 : 4;

    public HandleRole RoleOf(int index, bool tangentLocked)
    {
        int last = RequiredHandles(tangentLocked) - 1;
        return index == 0 || index >= last ? HandleRole.Endpoint : HandleRole.Control;
    }

    public IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent)
    {
        if (startTangent is { } t0)
        {
            if (handles.Count < 3)
                return null;
            var a = handles[0].Position;
            var c2 = handles[1].Position;
            var b = handles[^1].Position;
            float chord = Vector3.Distance(a, b);
            if (chord < GeoConstants.Eps)
                return null;
            var dir = ShapeUtil.Orient(t0, b - a);
            var curve = new Bezier3(a, a + dir * (chord / 3f), c2, b);
            return new[] { ShapeUtil.ApplyArrival(curve, handles[^1]) };
        }
        if (handles.Count < 4)
            return null;
        var full = new Bezier3(handles[0].Position, handles[1].Position,
            handles[2].Position, handles[3].Position);
        return new[] { ShapeUtil.ApplyArrival(full, handles[3]) };
    }
}

/// <summary>Constant-radius circular arc. Unlocked: start, a direction handle the
/// arc leaves toward, end. Tangent-locked: start, end (the lock is the direction).
/// The most predictable curve primitive — radius shown live in the readout.</summary>
public sealed class ArcShape : IDraftShape
{
    public int RequiredHandles(bool tangentLocked) => tangentLocked ? 2 : 3;

    public HandleRole RoleOf(int index, bool tangentLocked)
        => !tangentLocked && index == 1 ? HandleRole.Direction : HandleRole.Endpoint;

    public IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent)
    {
        int needed = RequiredHandles(startTangent is not null);
        if (handles.Count < needed)
            return null;
        var start = handles[0].Position;
        var end = handles[^1].Position;
        Vector3 tangent;
        if (startTangent is not null)
        {
            tangent = startTangent.Value;
        }
        else
        {
            tangent = handles[1].Position - start;
            tangent.Y = 0;
            if (tangent.LengthSquared() < GeoConstants.Eps * GeoConstants.Eps)
                return null;
            tangent = Vector3.Normalize(tangent);
        }
        return BezierOps.ArcFromTangent(start, tangent, end)?.Curves;
    }
}

/// <summary>Three endpoint handles: corner, extent along the first axis,
/// perpendicular extent. Stamps straight roads along both axes at 48 m spacing;
/// only whole cells are kept.</summary>
public sealed class GridStampShape : IDraftShape
{
    public const float CellSize = 48f;

    public int RequiredHandles(bool tangentLocked) => 3;
    public HandleRole RoleOf(int index, bool tangentLocked) => HandleRole.Endpoint;

    public IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent)
    {
        if (handles.Count < 3)
            return null;

        var origin = handles[0].Position;
        var axis1 = handles[1].Position - origin;
        axis1.Y = 0;
        if (axis1.Length() < GeoConstants.Eps)
            return null;
        var dir1 = Vector3.Normalize(axis1);
        int n1 = (int)MathF.Floor(axis1.Length() / CellSize);

        var raw2 = handles[2].Position - origin;
        raw2.Y = 0;
        var perp = raw2 - dir1 * Vector3.Dot(raw2, dir1);
        if (perp.Length() < GeoConstants.Eps)
            return null;
        var dir2 = Vector3.Normalize(perp);
        int n2 = (int)MathF.Floor(perp.Length() / CellSize);

        if (n1 < 1 || n2 < 1)
            return null;

        var curves = new List<Bezier3>();
        for (int i = 0; i <= n1; i++)
        {
            var a = origin + dir1 * (i * CellSize);
            curves.Add(Bezier3.Line(a, a + dir2 * (n2 * CellSize)));
        }
        for (int j = 0; j <= n2; j++)
        {
            var a = origin + dir2 * (j * CellSize);
            curves.Add(Bezier3.Line(a, a + dir1 * (n1 * CellSize)));
        }
        return curves;
    }
}
