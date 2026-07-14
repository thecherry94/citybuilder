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
