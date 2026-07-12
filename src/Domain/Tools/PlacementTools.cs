using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tools;

/// <summary>A drawing mode's click state machine. Feed it snapped clicks; when a
/// gesture completes it returns the proposal to commit. Pure logic — the Godot layer
/// only translates input events into these calls.</summary>
public interface IPlacementTool
{
    RoadTypeId RoadType { get; set; }
    int ClickCount { get; }

    /// <summary>Register a click. Non-null result = commit this proposal now.</summary>
    PlacementProposal? AddClick(SnapResult click);

    /// <summary>Ghost proposal for the current hover position (never mutates state).</summary>
    PlacementProposal? Preview(SnapResult hover);

    void StepBack();
    void Reset();

    /// <summary>Length/angle HUD readout for the current hover, if drawing.</summary>
    (float lengthM, float angleDeg)? Readout(SnapResult hover);
}

public abstract class PlacementToolBase : IPlacementTool
{
    protected readonly List<SnapResult> Clicks = new();

    public RoadTypeId RoadType { get; set; } = RoadCatalog.TwoLane.Id;
    public int ClickCount => Clicks.Count;

    public virtual PlacementProposal? AddClick(SnapResult click)
    {
        Clicks.Add(click);
        var proposal = BuildProposal(Clicks);
        if (proposal is not null)
            OnCommitted(proposal);
        return proposal;
    }

    public virtual PlacementProposal? Preview(SnapResult hover)
    {
        if (Clicks.Count == 0)
            return null;
        var candidate = new List<SnapResult>(Clicks) { hover };
        var proposal = BuildProposal(candidate);
        if (proposal is not null)
            return proposal;
        // not enough clicks for the real shape yet: show a straight hint from the last click
        var from = Clicks[^1];
        if (Vector3.Distance(from.Position, hover.Position) < GeoConstants.Eps)
            return null;
        return new PlacementProposal(new[]
        {
            new ProposedCurve(Bezier3.Line(from.Position, hover.Position), BindingOf(from), BindingOf(hover))
        }, RoadType);
    }

    public virtual void StepBack()
    {
        if (Clicks.Count > 0)
            Clicks.RemoveAt(Clicks.Count - 1);
    }

    public virtual void Reset() => Clicks.Clear();

    public virtual (float lengthM, float angleDeg)? Readout(SnapResult hover)
    {
        if (Clicks.Count == 0)
            return null;
        var v = hover.Position - Clicks[^1].Position;
        float len = v.Length();
        float angle = MathF.Atan2(v.Z, v.X) * 180f / MathF.PI;
        return (len, angle);
    }

    /// <summary>Return the finished proposal once the click sequence completes, else null.</summary>
    protected abstract PlacementProposal? BuildProposal(IReadOnlyList<SnapResult> clicks);

    protected virtual void OnCommitted(PlacementProposal proposal) => Clicks.Clear();

    protected static EndpointBinding BindingOf(SnapResult s) => s.Kind switch
    {
        SnapKind.Node when s.Node is { } n => new EndpointBinding.AtNode(n),
        SnapKind.Edge when s.Edge is { } e => new EndpointBinding.OnEdge(e.Edge, e.T),
        _ => EndpointBinding.None,
    };

    protected PlacementProposal Single(in Bezier3 curve, SnapResult start, SnapResult end)
        => new(new[] { new ProposedCurve(curve, BindingOf(start), BindingOf(end)) }, RoadType);
}

/// <summary>Two clicks: start, end.</summary>
public sealed class StraightTool : PlacementToolBase
{
    protected override PlacementProposal? BuildProposal(IReadOnlyList<SnapResult> clicks)
        => clicks.Count < 2
            ? null
            : Single(Bezier3.Line(clicks[0].Position, clicks[1].Position), clicks[0], clicks[1]);
}

/// <summary>Three clicks: start, control (the road bends toward it), end.</summary>
public sealed class SimpleCurveTool : PlacementToolBase
{
    protected override PlacementProposal? BuildProposal(IReadOnlyList<SnapResult> clicks)
        => clicks.Count < 3
            ? null
            : Single(Bezier3.FromQuadratic(clicks[0].Position, clicks[1].Position, clicks[2].Position),
                clicks[0], clicks[2]);
}

/// <summary>Four clicks: start, two controls, end.</summary>
public sealed class ComplexCurveTool : PlacementToolBase
{
    protected override PlacementProposal? BuildProposal(IReadOnlyList<SnapResult> clicks)
        => clicks.Count < 4
            ? null
            : Single(new Bezier3(clicks[0].Position, clicks[1].Position, clicks[2].Position, clicks[3].Position),
                clicks[0], clicks[3]);
}

/// <summary>First segment like SimpleCurve (3 clicks); afterwards every click commits
/// one more segment whose start tangent continues the chain (G1), with the control
/// point implied at 40% of the chord along that tangent. Esc/Reset ends the chain.</summary>
public sealed class ContinuousTool : PlacementToolBase
{
    private Vector3? _chainTangent;

    protected override PlacementProposal? BuildProposal(IReadOnlyList<SnapResult> clicks)
    {
        if (_chainTangent is not { } tangent)
            return clicks.Count < 3
                ? null
                : Single(Bezier3.FromQuadratic(clicks[0].Position, clicks[1].Position, clicks[2].Position),
                    clicks[0], clicks[2]);

        if (clicks.Count < 2)
            return null;
        var anchor = clicks[0];
        var end = clicks[^1];
        float chord = Vector3.Distance(anchor.Position, end.Position);
        var ctrl = anchor.Position + tangent * (0.4f * chord);
        return Single(Bezier3.FromQuadratic(anchor.Position, ctrl, end.Position), anchor, end);
    }

    protected override void OnCommitted(PlacementProposal proposal)
    {
        var curve = proposal.Curves[^1].Curve;
        var last = Clicks[^1];
        Clicks.Clear();
        Clicks.Add(last); // becomes the next segment's anchor
        var tan = curve.P3 - curve.P2;
        _chainTangent = tan.LengthSquared() > 0 ? Vector3.Normalize(tan) : curve.Tangent(1);
    }

    public override void StepBack()
    {
        // the anchor of an ongoing chain cannot be un-clicked (its segment is committed)
        if (_chainTangent is not null && Clicks.Count <= 1)
            return;
        base.StepBack();
    }

    public override void Reset()
    {
        base.Reset();
        _chainTangent = null;
    }
}

/// <summary>Three clicks: corner, extent along the first axis, perpendicular extent.
/// Stamps straight roads along both axes at 48 m spacing; only whole cells are kept.</summary>
public sealed class GridTool : PlacementToolBase
{
    public const float CellSize = 48f;

    protected override PlacementProposal? BuildProposal(IReadOnlyList<SnapResult> clicks)
    {
        if (clicks.Count < 3)
            return null;

        var origin = clicks[0].Position;
        var axis1 = clicks[1].Position - origin;
        axis1.Y = 0;
        if (axis1.Length() < GeoConstants.Eps)
            return null;
        var dir1 = Vector3.Normalize(axis1);
        int n1 = (int)MathF.Floor(axis1.Length() / CellSize);

        var raw2 = clicks[2].Position - origin;
        raw2.Y = 0;
        var perp = raw2 - dir1 * Vector3.Dot(raw2, dir1);
        if (perp.Length() < GeoConstants.Eps)
            return null;
        var dir2 = Vector3.Normalize(perp);
        int n2 = (int)MathF.Floor(perp.Length() / CellSize);

        if (n1 < 1 || n2 < 1)
            return null;

        var curves = new List<ProposedCurve>();
        for (int i = 0; i <= n1; i++)
        {
            var a = origin + dir1 * (i * CellSize);
            curves.Add(new ProposedCurve(Bezier3.Line(a, a + dir2 * (n2 * CellSize)),
                EndpointBinding.None, EndpointBinding.None));
        }
        for (int j = 0; j <= n2; j++)
        {
            var a = origin + dir2 * (j * CellSize);
            curves.Add(new ProposedCurve(Bezier3.Line(a, a + dir1 * (n1 * CellSize)),
                EndpointBinding.None, EndpointBinding.None));
        }
        return new PlacementProposal(curves, RoadType);
    }

    public override PlacementProposal? AddClick(SnapResult click)
    {
        Clicks.Add(click);
        var proposal = BuildProposal(Clicks);
        if (proposal is not null)
        {
            OnCommitted(proposal);
            return proposal;
        }
        // a completed gesture that stamps nothing keeps the drag alive for a better 3rd click
        if (Clicks.Count >= 3)
            Clicks.RemoveAt(Clicks.Count - 1);
        return null;
    }
}
