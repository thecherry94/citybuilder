using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tools;

public enum DraftMode { Straight, QuadCurve, CubicCurve, Arc, Chain, GridStamp }

public enum SessionState { Idle, Placing, Adjustable }

/// <summary>Domain-side drawing-tool state machine. Owns the current draft, resolves
/// snapping with full context (anchor, reference tangent, grid), validates and commits
/// against the network. The Godot layer only forwards input and renders this state.</summary>
public sealed class DraftSession(RoadNetwork network, SnapEngine snap)
{
    public DraftMode Mode { get; private set; } = DraftMode.Straight;
    public SessionState State { get; private set; } = SessionState.Idle;
    public RoadTypeId RoadType
    {
        get => _roadType;
        set { _roadType = value; if (Draft is { } d) d.Type = value; Revalidate(); }
    }
    private RoadTypeId _roadType = RoadCatalog.TwoLane.Id;

    // CellLength is excluded here (domain tests build raw sessions and would see 8 m
    // quantization everywhere); the Toolbar turns it ON for the game — spec default-ON
    // is user-facing.
    public SnapTypes EnabledSnaps { get; set; } = SnapTypes.All & ~SnapTypes.Grid & ~SnapTypes.CellLength;
    public GridConfig Grid { get; set; } = GridConfig.Default;
    public bool AdjustMode { get; set; }

    /// <summary>Elevation applied to FREE draft endpoints (M8); snapped endpoints adopt
    /// their target's Y instead. Persists across gestures until changed (CS2 behavior).
    /// Clamped to the editor range [−MaxDepth, MaxElevation] — the domain itself is unclamped, the
    /// negative half unlocks with M8.5 trenches.</summary>
    public float CurrentElevation
    {
        get => _currentElevation;
        set { _currentElevation = Math.Clamp(value, -GeoConstants.MaxDepth, GeoConstants.MaxElevation); Revalidate(); }
    }
    private float _currentElevation;

    public RoadDraft? Draft { get; private set; }
    public ValidatedPlacement? Ghost { get; private set; }
    public SnapResult LastSnap { get; private set; } = SnapResult.Free(default);
    public int DraggingHandle { get; private set; } = -1;
    public (float LengthM, float AngleDeg, float? RadiusM)? Readout { get; private set; }

    public event Action<string>? Flashed;
    /// <summary>A click appended a handle to the draft (game layer: placement click).</summary>
    public event Action? HandlePlaced;
    /// <summary>A proposal committed to the network (game layer: commit plop).</summary>
    public event Action? Committed;
    /// <summary>Completion or commit was refused (game layer: error blip).</summary>
    public event Action? Rejected;
    /// <summary>Fires immediately before a validated proposal is committed — the
    /// game layer's undo-checkpoint hook (M7).</summary>
    public event Action? BeforeCommit;

    public void SetMode(DraftMode mode)
    {
        Mode = mode;
        Cancel();
    }

    // ---------------------------------------------------------------- elevation (M8)

    /// <summary>Lift a built proposal onto its elevation profile: snapped endpoints
    /// adopt their target's Y; a free START keeps the elevation captured when its
    /// handle was clicked (<see cref="_draftStartElevation"/> — stepping PgUp/PgDn
    /// mid-draft is how inclined roads are drawn); a free END takes
    /// <see cref="CurrentElevation"/>. Control points interpolate linearly so the
    /// gradient is uniform. Draft shapes stay planar — elevation is applied to every
    /// proposal exactly here, so ghost validation and commit always see the same
    /// lifted geometry. GridStamp is the exception: a stamp is one flat plate at the
    /// current elevation (its curves' Start/End orientation is arbitrary — a per-end
    /// rule would tilt cells randomly).</summary>
    private PlacementProposal? ApplyElevation(PlacementProposal? p)
        => p is null ? null : new PlacementProposal(p.Curves.Select(Elevate).ToArray(), p.Type);

    // elevation captured when the current draft's FIRST handle was placed
    private float? _draftStartElevation;

    private ProposedCurve Elevate(ProposedCurve pc)
    {
        bool flat = Mode == DraftMode.GridStamp;
        float y0 = ResolveY(pc.Start, flat ? null : _draftStartElevation);
        float y3 = ResolveY(pc.End, null);
        var c = pc.Curve;
        var lifted = new Bezier3(
            new Vector3(c.P0.X, y0, c.P0.Z),
            new Vector3(c.P1.X, y0 + (y3 - y0) / 3f, c.P1.Z),
            new Vector3(c.P2.X, y0 + (y3 - y0) * 2f / 3f, c.P2.Z),
            new Vector3(c.P3.X, y3, c.P3.Z));
        return pc with { Curve = lifted };
    }

    private float ResolveY(EndpointBinding b, float? freeElevation) => b switch
    {
        EndpointBinding.AtNode(var id) when network.Nodes.TryGetValue(id, out var nd) => nd.Position.Y,
        EndpointBinding.OnEdge(var eid, var t) when network.Edges.TryGetValue(eid, out var e) => e.Curve.Point(t).Y,
        _ => freeElevation ?? CurrentElevation,
    };

    public void Cancel()
    {
        Draft = null;
        Ghost = null;
        Readout = null;
        DraggingHandle = -1;
        _chainTangent = null;
        _draftStartElevation = null;
        State = SessionState.Idle;
    }

    public void StepBack()
    {
        if (Draft is not { } d)
            return;
        if (State == SessionState.Adjustable)
        {
            // back to editing clicks: the draft must be genuinely incomplete again,
            // or the next click would append a junk handle past the shape's count
            State = SessionState.Placing;
            if (!d.RemoveLastHandle() || d.Handles.Count == 0)
            {
                Cancel();
                return;
            }
            Revalidate();
            return;
        }
        if (!d.RemoveLastHandle() || d.Handles.Count == 0)
            Cancel();
        else
            Revalidate();
    }

    public void PointerMoved(Vector3 raw, float radius)
    {
        var s = Resolve(raw, radius, forHandleIndex: DraggingHandle);
        LastSnap = s;
        if (Draft is not { } d)
            return;
        if (DraggingHandle >= 0)
        {
            d.MoveHandle(DraggingHandle, s, DraggingHandle == 0 ? BoundTangent(s) : null);
            Revalidate();
            return;
        }
        if (State != SessionState.Placing)
            return;
        var proposal = ApplyElevation(d.Preview(s));
        Ghost = proposal is null ? null : network.Validate(proposal);
        UpdateReadout(d, s);
    }

    public void Click(Vector3 raw, float radius)
    {
        if (State == SessionState.Adjustable)
            return; // handles are dragged, commit is Confirm()
        var s = Resolve(raw, radius, forHandleIndex: -1);
        LastSnap = s;
        var d = Draft;
        if (d is null)
        {
            d = Draft = new RoadDraft(ShapeOf(Mode), RoadType);
            _draftStartElevation = CurrentElevation; // the start keeps its click-time height
            if (_chainTangent is { } inherited)
            {
                d.LockStartTangent(inherited);
                _chainTangent = null;
            }
            State = SessionState.Placing;
        }
        if (d.IsComplete)
        {
            // belt-and-braces: a click on an already-complete draft routes to the
            // completion path instead of appending a handle the shape ignores
            CompleteDraft(d);
            return;
        }
        d.AddHandle(s, d.Handles.Count == 0 ? BoundTangent(s) : null);
        HandlePlaced?.Invoke();
        if (!d.IsComplete)
        {
            Revalidate();
            return;
        }
        CompleteDraft(d);
    }

    /// <summary>Release the G1 start-tangent lock on the current draft (spec'd lock
    /// toggle — game layer binds it to a key).</summary>
    public void ReleaseTangentLock()
    {
        if (Draft is not { } d || !d.TangentLocked)
            return;
        d.UnlockStartTangent();
        Revalidate();
    }

    public void Confirm()
    {
        if (State != SessionState.Adjustable || Draft is not { } d)
            return;
        TryCommit(d);
    }

    public bool TryBeginHandleDrag(Vector3 raw, float pickRadius)
    {
        if (Draft is not { } d)
            return false;
        int best = -1;
        float bestD = pickRadius;
        for (int i = 0; i < d.Handles.Count; i++)
        {
            float dist = Vector3.Distance(d.Handles[i].Position, raw);
            if (dist <= bestD)
            {
                bestD = dist;
                best = i;
            }
        }
        if (best < 0)
            return false;
        DraggingHandle = best;
        return true;
    }

    public void EndHandleDrag() => DraggingHandle = -1;

    // ------------------------------------------------------------------ internal

    private Vector3? _chainTangent;

    private void CompleteDraft(RoadDraft d)
    {
        var proposal = ApplyElevation(d.BuildProposal());
        var validated = proposal is null ? null : network.Validate(proposal);
        Ghost = validated;
        if (validated is null || !validated.IsValid || AdjustMode)
        {
            if (validated is null)
                Flashed?.Invoke("shape is not buildable here");
            else if (!validated.IsValid)
                Flashed?.Invoke("invalid placement: " + string.Join(", ", validated.Errors));
            if (validated is null || !validated.IsValid)
                Rejected?.Invoke();
            State = SessionState.Adjustable;
            return;
        }
        TryCommit(d);
    }

    private void TryCommit(RoadDraft d)
    {
        var proposal = ApplyElevation(d.BuildProposal());
        var validated = proposal is null ? null : network.Validate(proposal);
        Ghost = validated;
        if (validated is null || !validated.IsValid)
        {
            Flashed?.Invoke(validated is null
                ? "shape is not buildable here"
                : "invalid placement: " + string.Join(", ", validated.Errors));
            Rejected?.Invoke();
            State = SessionState.Adjustable;
            return;
        }
        BeforeCommit?.Invoke();
        var result = network.Commit(validated);
        if (!result.Success)
        {
            Flashed?.Invoke(result.FailureReason ?? "could not build");
            Rejected?.Invoke();
            State = SessionState.Adjustable;
            return;
        }
        Committed?.Invoke();
        if (result.DroppedSegments > 0)
            Flashed?.Invoke(result.DroppedSegments == 1
                ? "1 segment degenerated while merging and was skipped"
                : $"{result.DroppedSegments} segments degenerated while merging and were skipped");
        var endSnap = d.Handles[^1].Snap;
        var lastCurve = validated.Proposal.Curves[^1].Curve;
        Draft = null;
        Ghost = null;
        Readout = null;
        State = SessionState.Idle;
        if (Mode == DraftMode.Chain)
        {
            // chain continues: next segment starts at the committed end, G1-locked
            _chainTangent = lastCurve.Tangent(1);
            var next = new RoadDraft(ShapeOf(Mode), RoadType);
            next.LockStartTangent(_chainTangent.Value);
            _chainTangent = null;
            next.AddHandle(endSnap);
            Draft = next;
            State = SessionState.Placing;
        }
    }

    private void Revalidate()
    {
        if (Draft is not { } d)
            return;
        var proposal = ApplyElevation(d.BuildProposal());
        Ghost = proposal is null ? null : network.Validate(proposal);
        if (Ghost is not null && d.Handles.Count > 0)
            UpdateReadout(d, d.Handles[^1].Snap);
    }

    private void UpdateReadout(RoadDraft d, SnapResult tip)
    {
        if (d.Handles.Count == 0)
        {
            Readout = null;
            return;
        }
        var from = d.Handles[0].Position;
        var v = tip.Position - from;
        float len = v.Length();
        float angle = MathF.Atan2(v.Z, v.X) * 180f / MathF.PI;
        float? radius = null;
        var curves = Ghost?.Proposal.Curves;
        if (curves is { Count: > 0 })
        {
            float min = float.PositiveInfinity;
            foreach (var pc in curves)
                min = MathF.Min(min, BezierOps.MinRadius(pc.Curve));
            if (!float.IsPositiveInfinity(min))
                radius = min;
        }
        Readout = (len, angle, radius);
    }

    private SnapResult Resolve(Vector3 raw, float radius, int forHandleIndex)
    {
        Vector3? anchor = null;
        Vector3? reference = null;
        if (Draft is { } d && d.Handles.Count > 0 && forHandleIndex != 0)
        {
            // the anchor must be a FIXED handle: while dragging handle i > 0 it is
            // the start handle (anchoring to the dragged handle itself makes angle
            // snap wiggle and feeds perpendicular snap a noise direction); when
            // placing the next click it is the last placed handle as before
            anchor = forHandleIndex > 0 ? d.Handles[0].Position : d.Handles[^1].Position;
            reference = d.StartTangent;
        }
        var ctx = new SnapContext(anchor, reference,
            (EnabledSnaps & SnapTypes.Grid) != 0 ? Grid : null, RoadType,
            HeldNode: LastSnap.Kind == SnapKind.Node ? LastSnap.Node : null);
        return snap.Resolve(raw, radius, EnabledSnaps, ctx);
    }

    private Vector3? BoundTangent(SnapResult s)
    {
        switch (s.Kind)
        {
            case SnapKind.Edge or SnapKind.Perpendicular when s.Edge is { } e
                && network.Edges.TryGetValue(e.Edge, out var edge):
                return edge.Curve.Tangent(e.T);
            case SnapKind.Node when s.Node is { } id
                && network.Nodes.TryGetValue(id, out var node) && node.EdgeSet.Count == 1:
            {
                var e = network.Edges[node.EdgeSet.First()];
                // continuation direction: away from the existing edge
                return e.StartNode == id ? -e.Curve.Tangent(0) : e.Curve.Tangent(1);
            }
            default:
                return null;
        }
    }

    private static IDraftShape ShapeOf(DraftMode mode) => mode switch
    {
        DraftMode.Straight => new StraightShape(),
        DraftMode.QuadCurve or DraftMode.Chain => new QuadCurveShape(),
        DraftMode.CubicCurve => new CubicCurveShape(),
        DraftMode.Arc => new ArcShape(),
        DraftMode.GridStamp => new GridStampShape(),
        _ => new StraightShape(),
    };
}
