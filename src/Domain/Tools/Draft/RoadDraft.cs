using System.Numerics;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tools;

public enum HandleRole { Endpoint, Control, Direction }

/// <summary>A draggable point of an in-progress road gesture, remembering how it
/// snapped (bindings and direction constraints travel with it).</summary>
public sealed record DraftHandle(HandleRole Role, SnapResult Snap)
{
    public Vector3 Position => Snap.Position;
}

/// <summary>Maps a draft's handles to curve geometry. Stateless.</summary>
public interface IDraftShape
{
    int RequiredHandles(bool tangentLocked);
    HandleRole RoleOf(int index, bool tangentLocked);
    /// <summary>Curves for the given handles (possibly a prefix + hover); null if too few.</summary>
    IReadOnlyList<Bezier3>? Curves(IReadOnlyList<DraftHandle> handles, Vector3? startTangent);
}

/// <summary>An editable in-progress road gesture: ordered handles + a shape strategy.
/// First-class replacement for the old click-state-machine tools — handles can be
/// added, moved, and removed until the proposal is committed.</summary>
public sealed class RoadDraft(IDraftShape shape, RoadTypeId type)
{
    private readonly List<DraftHandle> _handles = new();

    public IDraftShape Shape => shape;
    public RoadTypeId Type { get; set; } = type;
    public Vector3? StartTangent { get; private set; }
    public bool TangentLocked => StartTangent is not null;
    public IReadOnlyList<DraftHandle> Handles => _handles;
    public bool IsComplete => _handles.Count >= shape.RequiredHandles(TangentLocked);

    /// <summary>Seed the lock for chained segments (continuous mode).</summary>
    public void LockStartTangent(Vector3 tangent) => StartTangent = tangent;

    public void AddHandle(SnapResult snap, Vector3? boundTangent = null)
    {
        if (_handles.Count == 0 && boundTangent is { } t)
            StartTangent = Normalized(t);
        _handles.Add(new DraftHandle(shape.RoleOf(_handles.Count, TangentLocked), snap));
    }

    public void MoveHandle(int index, SnapResult snap, Vector3? boundTangent = null)
    {
        if (index < 0 || index >= _handles.Count)
            return;
        if (index == 0)
            StartTangent = boundTangent is { } t ? Normalized(t) : null;
        _handles[index] = _handles[index] with { Snap = snap };
    }

    public bool RemoveLastHandle()
    {
        if (_handles.Count == 0)
            return false;
        _handles.RemoveAt(_handles.Count - 1);
        if (_handles.Count == 0)
            StartTangent = null;
        return true;
    }

    public PlacementProposal? BuildProposal() => Proposal(_handles);

    public PlacementProposal? Preview(SnapResult hover)
    {
        if (_handles.Count == 0)
            return null;
        if (IsComplete)
            return BuildProposal();
        var withHover = new List<DraftHandle>(_handles)
        {
            new(shape.RoleOf(_handles.Count, TangentLocked), hover),
        };
        var full = Proposal(withHover);
        if (full is not null)
            return full;
        // not enough handles for the real shape yet: straight hint from the last handle
        var from = _handles[^1].Position;
        if (Vector3.Distance(from, hover.Position) < GeoConstants.Eps)
            return null;
        return new PlacementProposal(new[]
        {
            new ProposedCurve(Bezier3.Line(from, hover.Position),
                _handles.Count == 1 ? BindingOf(_handles[0].Snap) : EndpointBinding.None,
                BindingOf(hover)),
        }, Type);
    }

    public float? MinRadius()
    {
        var curves = shape.Curves(_handles, StartTangent);
        if (curves is null || curves.Count == 0)
            return null;
        float min = float.PositiveInfinity;
        foreach (var c in curves)
            min = MathF.Min(min, BezierOps.MinRadius(c));
        return min;
    }

    private PlacementProposal? Proposal(IReadOnlyList<DraftHandle> handles)
    {
        var curves = shape.Curves(handles, StartTangent);
        if (curves is null || curves.Count == 0)
            return null;
        var endHandle = handles[^1];
        var list = new List<ProposedCurve>(curves.Count);
        for (int i = 0; i < curves.Count; i++)
            list.Add(new ProposedCurve(curves[i],
                i == 0 ? BindingOf(handles[0].Snap) : EndpointBinding.None,
                i == curves.Count - 1 ? BindingOf(endHandle.Snap) : EndpointBinding.None));
        return new PlacementProposal(list, Type);
    }

    internal static EndpointBinding BindingOf(SnapResult s) => s.Kind switch
    {
        SnapKind.Node when s.Node is { } n => new EndpointBinding.AtNode(n),
        SnapKind.Edge when s.Edge is { } e => new EndpointBinding.OnEdge(e.Edge, e.T),
        SnapKind.Perpendicular when s.Edge is { } e => new EndpointBinding.OnEdge(e.Edge, e.T),
        _ => EndpointBinding.None,
    };

    private static Vector3 Normalized(Vector3 v)
    {
        v.Y = 0;
        return v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitX;
    }
}
