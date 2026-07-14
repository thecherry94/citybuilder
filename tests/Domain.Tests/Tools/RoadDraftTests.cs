using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class RoadDraftTests
{
    private static SnapResult Free(float x, float z) => SnapResult.Free(new Vector3(x, 0, z));

    [Fact]
    public void StraightDraftCompletesAfterTwoHandles()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        Assert.False(d.IsComplete);
        d.AddHandle(Free(0, 0));
        Assert.False(d.IsComplete);
        d.AddHandle(Free(100, 0));
        Assert.True(d.IsComplete);
        var p = d.BuildProposal();
        Assert.NotNull(p);
        var curve = Assert.Single(p!.Curves);
        Assert.Equal(new Vector3(0, 0, 0), curve.Curve.P0);
        Assert.Equal(new Vector3(100, 0, 0), curve.Curve.P3);
    }

    [Fact]
    public void MoveHandleReshapesTheProposal()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        d.AddHandle(Free(0, 0));
        d.AddHandle(Free(100, 0));
        d.MoveHandle(1, Free(100, 50));
        Assert.Equal(new Vector3(100, 0, 50), d.BuildProposal()!.Curves[0].Curve.P3);
    }

    [Fact]
    public void PreviewAppendsHoverWithoutMutating()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        d.AddHandle(Free(0, 0));
        var p = d.Preview(Free(60, 0));
        Assert.NotNull(p);
        Assert.Equal(new Vector3(60, 0, 0), p!.Curves[0].Curve.P3);
        Assert.Single(d.Handles); // unchanged
    }

    [Fact]
    public void RemoveLastHandleStepsBack()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        d.AddHandle(Free(0, 0));
        Assert.True(d.RemoveLastHandle());
        Assert.False(d.RemoveLastHandle());
        Assert.Empty(d.Handles);
    }

    [Fact]
    public void NodeSnapBecomesAtNodeBinding()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        var nodeSnap = new SnapResult(new Vector3(0, 0, 0), SnapKind.Node, new NodeId(7), null, null,
            Array.Empty<Guideline>());
        d.AddHandle(nodeSnap);
        d.AddHandle(Free(100, 0));
        var p = d.BuildProposal()!;
        var start = Assert.IsType<EndpointBinding.AtNode>(p.Curves[0].Start);
        Assert.Equal(new NodeId(7), start.Node);
        Assert.IsType<EndpointBinding.Free>(p.Curves[0].End);
    }

    [Fact]
    public void BoundTangentOnFirstHandleLocksTheDraft()
    {
        var d = new RoadDraft(new StraightShape(), RoadCatalog.TwoLane.Id);
        d.AddHandle(Free(0, 0), boundTangent: new Vector3(1, 0, 0));
        Assert.True(d.TangentLocked);
        d.MoveHandle(0, Free(5, 0), boundTangent: null);
        Assert.False(d.TangentLocked); // moving off the edge releases the lock
    }
}
