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
}
