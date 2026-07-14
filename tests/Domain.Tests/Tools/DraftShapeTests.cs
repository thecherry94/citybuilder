using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class DraftShapeTests
{
    private static SnapResult Free(float x, float z) => SnapResult.Free(new Vector3(x, 0, z));

    private static RoadDraft Draft(IDraftShape shape, Vector3? lockTangent, params SnapResult[] snaps)
    {
        var d = new RoadDraft(shape, RoadCatalog.TwoLane.Id);
        for (int i = 0; i < snaps.Length; i++)
            d.AddHandle(snaps[i], i == 0 ? lockTangent : null);
        return d;
    }

    [Fact]
    public void UnlockedQuadNeedsThreeHandles()
    {
        var d = Draft(new QuadCurveShape(), null, Free(0, 0), Free(50, 30), Free(100, 0));
        Assert.True(d.IsComplete);
        var c = d.BuildProposal()!.Curves[0].Curve;
        // quadratic through the control: curve bends toward (50,30)
        Assert.True(c.Point(0.5f).Z > 10f);
    }

    [Fact]
    public void LockedQuadNeedsTwoHandlesAndLeavesG1()
    {
        var tangent = new Vector3(1, 0, 0);
        var d = Draft(new QuadCurveShape(), tangent, Free(0, 0), Free(80, 40));
        Assert.True(d.IsComplete);
        var c = d.BuildProposal()!.Curves[0].Curve;
        // G1: start tangent parallel to the lock
        Assert.True(Vector3.Dot(c.Tangent(0), tangent) > 0.999f);
        Assert.Equal(new Vector3(80, 0, 40), c.P3);
    }

    [Fact]
    public void LockedQuadFlipsTangentTowardTheEnd()
    {
        // lock points +X but the end is behind: shape must leave along −X
        var d = Draft(new QuadCurveShape(), new Vector3(1, 0, 0), Free(0, 0), Free(-80, 40));
        var c = d.BuildProposal()!.Curves[0].Curve;
        Assert.True(Vector3.Dot(c.Tangent(0), new Vector3(-1, 0, 0)) > 0.999f);
    }

    [Fact]
    public void EndDirectionConstraintIsHonored()
    {
        var arrival = Vector3.Normalize(new Vector3(0, 0, -1));
        var endSnap = new SnapResult(new Vector3(80, 0, 40), SnapKind.Perpendicular, null, null, null,
            Array.Empty<Guideline>(), arrival);
        var d = Draft(new QuadCurveShape(), new Vector3(1, 0, 0), Free(0, 0), endSnap);
        var c = d.BuildProposal()!.Curves[0].Curve;
        Assert.True(Vector3.Dot(c.Tangent(1), arrival) > 0.99f,
            $"arrival tangent {c.Tangent(1)} vs {arrival}");
    }

    [Fact]
    public void LockedCubicNeedsThreeHandles()
    {
        var d = Draft(new CubicCurveShape(), new Vector3(1, 0, 0), Free(0, 0), Free(70, 25), Free(120, 10));
        Assert.True(d.IsComplete);
        var c = d.BuildProposal()!.Curves[0].Curve;
        Assert.True(Vector3.Dot(c.Tangent(0), new Vector3(1, 0, 0)) > 0.999f);
        Assert.Equal(new Vector3(120, 0, 10), c.P3);
    }

    [Fact]
    public void UnlockedCubicUsesFourHandles()
    {
        var d = Draft(new CubicCurveShape(), null, Free(0, 0), Free(30, 30), Free(70, 30), Free(100, 0));
        Assert.True(d.IsComplete);
        var c = d.BuildProposal()!.Curves[0].Curve;
        Assert.Equal(new Vector3(30, 0, 30), c.P1);
        Assert.Equal(new Vector3(70, 0, 30), c.P2);
    }
}
