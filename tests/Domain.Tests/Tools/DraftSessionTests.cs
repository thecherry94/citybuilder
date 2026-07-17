using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class DraftSessionTests
{
    private static (RoadNetwork n, DraftSession s) Setup()
    {
        var n = Net.New();
        return (n, new DraftSession(n, new SnapEngine(n)));
    }

    private static void ClickAt(DraftSession s, float x, float z) => s.Click(new Vector3(x, 0, z), 5f);

    [Fact]
    public void SessionHoldsNodeSnapWhileCursorDriftsAlongLeg()
    {
        // capture the node, then drift 4.9 m away along the edge: the session-threaded
        // hold keeps the node; drifting past the release ring lets go.
        var (n, s) = Setup();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
        s.SetMode(DraftMode.Straight);
        s.PointerMoved(new Vector3(98f, 0, 0.5f), 6f);
        Assert.Equal(SnapKind.Node, s.LastSnap.Kind);
        s.PointerMoved(new Vector3(95.2f, 0, 1.0f), 6f);
        Assert.Equal(SnapKind.Node, s.LastSnap.Kind);
        s.PointerMoved(new Vector3(90f, 0, 1.0f), 6f);
        Assert.Equal(SnapKind.Edge, s.LastSnap.Kind);
    }

    [Fact]
    public void BeforeCommitFiresOnlyWhenCommitProceeds()
    {
        var (n, s) = Setup();
        int before = 0, committed = 0;
        s.BeforeCommit += () => before++;
        s.Committed += () => committed++;
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        ClickAt(s, 5, 0); // TooShort → Adjustable, no commit attempt
        Assert.Equal(0, before);
        s.Cancel();
        ClickAt(s, 0, 0);
        ClickAt(s, 100, 0);
        Assert.Equal(1, before);
        Assert.Equal(1, committed);
    }

    [Fact]
    public void EventsFireOnPlaceAndCommit()
    {
        var (n, s) = Setup();
        int placed = 0, committed = 0, rejected = 0;
        s.HandlePlaced += () => placed++;
        s.Committed += () => committed++;
        s.Rejected += () => rejected++;
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        ClickAt(s, 100, 0);
        Assert.Equal(2, placed);
        Assert.Equal(1, committed);
        Assert.Equal(0, rejected);
        Assert.Single(n.Edges);
    }

    [Fact]
    public void RejectedFiresOnInvalidCompletion()
    {
        var (n, s) = Setup();
        int rejected = 0;
        s.Rejected += () => rejected++;
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        ClickAt(s, 5, 0); // TooShort → Adjustable
        Assert.Equal(1, rejected);
        Assert.Empty(n.Edges);
    }

    [Fact]
    public void StraightRoadCommitsInstantlyOnSecondClick()
    {
        var (n, s) = Setup();
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        Assert.Equal(SessionState.Placing, s.State);
        ClickAt(s, 100, 0);
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Single(n.Edges);
    }

    [Fact]
    public void AdjustModeHoldsTheDraftUntilConfirm()
    {
        var (n, s) = Setup();
        s.AdjustMode = true;
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        ClickAt(s, 100, 0);
        Assert.Equal(SessionState.Adjustable, s.State);
        Assert.Empty(n.Edges);
        s.Confirm();
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Single(n.Edges);
    }

    [Fact]
    public void InvalidCompletionBecomesAdjustableAndFixable()
    {
        var (n, s) = Setup();
        s.SetMode(DraftMode.Straight);
        string? flashed = null;
        s.Flashed += m => flashed = m;
        ClickAt(s, 0, 0);
        ClickAt(s, 5, 0); // 5 m TwoLane: TooShort
        Assert.Equal(SessionState.Adjustable, s.State);
        Assert.NotNull(flashed);
        Assert.Empty(n.Edges);
        // drag the end handle out to a valid length, then confirm
        Assert.True(s.TryBeginHandleDrag(new Vector3(5, 0, 0), 3f));
        s.PointerMoved(new Vector3(100, 0, 0), 5f);
        s.EndHandleDrag();
        s.Confirm();
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Single(n.Edges);
    }

    [Fact]
    public void ChainModeLocksNextSegmentToEndTangent()
    {
        var (n, s) = Setup();
        s.SetMode(DraftMode.Chain);
        ClickAt(s, 0, 0);
        ClickAt(s, 50, 30);   // control
        ClickAt(s, 100, 30);  // first segment commits
        Assert.Single(n.Edges);
        Assert.Equal(SessionState.Placing, s.State); // chain continues
        Assert.True(s.Draft!.TangentLocked);
        ClickAt(s, 180, 30);  // second segment: 2 clicks thanks to the lock
        Assert.Equal(2, n.Edges.Count);
        // G1 across the chain joint
        var joint = n.Nodes.Values.Single(nd => nd.EdgeSet.Count == 2);
        var tans = joint.EdgeSet.Select(id =>
        {
            var e = n.Edges[id];
            return e.StartNode == joint.Id ? e.Curve.Tangent(0) : -e.Curve.Tangent(1);
        }).ToArray();
        Assert.True(Vector3.Dot(tans[0], tans[1]) < -0.99f, "chain joint must be straight-through");
    }

    [Fact]
    public void StepBackAndCancelUnwindTheDraft()
    {
        var (_, s) = Setup();
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        s.StepBack();
        Assert.Equal(SessionState.Idle, s.State);
        ClickAt(s, 0, 0);
        s.Cancel();
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Null(s.Draft);
    }

    [Fact]
    public void HoverProducesGhostAndReadoutWithRadius()
    {
        var (_, s) = Setup();
        s.SetMode(DraftMode.Arc);
        ClickAt(s, 0, 0);
        ClickAt(s, 20, 0); // direction handle
        s.PointerMoved(new Vector3(50, 0, 50), 5f);
        Assert.NotNull(s.Ghost);
        Assert.NotNull(s.Readout);
        Assert.NotNull(s.Readout!.Value.RadiusM);
        Assert.InRange(s.Readout.Value.RadiusM!.Value, 45f, 55f);
    }

    [Fact]
    public void StartingOnAnExistingEdgeLocksTheTangent()
    {
        var (n, s) = Setup();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        s.SetMode(DraftMode.QuadCurve);
        s.Click(new Vector3(50, 0, 1f), 5f); // snaps onto the edge
        Assert.True(s.Draft!.TangentLocked);
    }

    // C1: the spec's flagship gesture — tangent-locked curve starting mid-edge —
    // must validate (G1 ramp exit) and commit, not die on SharpAngle.
    [Fact]
    public void TangentLockedQuadFromMidEdgeCommits()
    {
        var (n, s) = Setup();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        s.SetMode(DraftMode.QuadCurve);
        s.EnabledSnaps = SnapTypes.Nodes | SnapTypes.Edges; // keep click positions exact
        s.Click(new Vector3(50, 0, 0.3f), 5f);              // snaps onto the edge, locks
        Assert.True(s.Draft!.TangentLocked);
        s.Click(new Vector3(90, 0, 60), 5f);                // locked quad: 2nd click completes
        Assert.Equal(SessionState.Idle, s.State);           // committed instantly
        Assert.Equal(3, n.Edges.Count);                     // edge split in two + ramp
        var ramp = n.Edges.Values.Single(e => e.Curve.Point(1).Z > 50f);
        Assert.True(ramp.Curve.Tangent(0).X > 0.999f, "ramp must leave G1 along the edge tangent");
    }

    // C1b: T (game layer) releases the lock; the shape falls back to its unlocked
    // handle count and a free-angle departure can be drawn instead.
    [Fact]
    public void ReleaseTangentLockRestoresFreeControl()
    {
        var (n, s) = Setup();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        s.SetMode(DraftMode.QuadCurve);
        s.EnabledSnaps = SnapTypes.Nodes | SnapTypes.Edges;
        s.Click(new Vector3(50, 0, 0.3f), 5f);
        Assert.True(s.Draft!.TangentLocked);
        s.ReleaseTangentLock();
        Assert.False(s.Draft!.TangentLocked);
        s.Click(new Vector3(50, 0, 40), 5f);   // unlocked quad needs a control handle again
        Assert.Equal(SessionState.Placing, s.State);
        s.Click(new Vector3(90, 0, 60), 5f);   // perpendicular departure — legal unlocked
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Equal(3, n.Edges.Count);
    }

    // I3: stepping back from Adjustable must actually shorten the draft — the next
    // click then completes the corrected geometry instead of appending junk handles.
    [Fact]
    public void StepBackFromAdjustableThenClickCommitsCorrectedGeometry()
    {
        var (n, s) = Setup();
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        ClickAt(s, 5, 0); // 5 m TwoLane: TooShort → Adjustable
        Assert.Equal(SessionState.Adjustable, s.State);
        s.StepBack();
        Assert.Equal(SessionState.Placing, s.State);
        Assert.Single(s.Draft!.Handles);      // genuinely incomplete again
        ClickAt(s, 100, 0);                   // corrected end point
        Assert.Equal(SessionState.Idle, s.State);
        var edge = Assert.Single(n.Edges.Values);
        Assert.Equal(100f, edge.Curve.Length(), 0);
    }

    // I2: while dragging handle i > 0 the snap anchor must be the FIXED start
    // handle, not the dragged handle itself (which made angle snap self-referential).
    [Fact]
    public void DraggingEndHandleAnchorsAngleSnapAtStartHandle()
    {
        var (n, s) = Setup();
        s.EnabledSnaps = SnapTypes.None; // place raw handles first
        s.AdjustMode = true;
        s.SetMode(DraftMode.Straight);
        ClickAt(s, 0, 0);
        s.Click(new Vector3(100, 0, 3), 5f);
        Assert.Equal(SessionState.Adjustable, s.State);
        s.EnabledSnaps = SnapTypes.Angle;
        Assert.True(s.TryBeginHandleDrag(new Vector3(100, 0, 3), 3f));
        s.PointerMoved(new Vector3(120, 0, 8), 5f);
        // measured from handle 0 at the origin: 3.8° → snaps onto the 0° ray (z = 0);
        // anchored at the dragged handle it would read 14° and snap to the 15° ray
        Assert.Equal(SnapKind.Angle, s.LastSnap.Kind);
        Assert.Equal(0f, s.LastSnap.SnappedAngleDeg!.Value, 1);
        Assert.True(MathF.Abs(s.LastSnap.Position.Z) < 0.01f, $"snapped Z {s.LastSnap.Position.Z}");
        s.EndHandleDrag();
        s.Confirm();
        Assert.Equal(SessionState.Idle, s.State);
        var edge = Assert.Single(n.Edges.Values);
        Assert.True(MathF.Abs(edge.Curve.P3.Z) < 0.01f, "committed end must sit on the 0° ray");
    }
}
