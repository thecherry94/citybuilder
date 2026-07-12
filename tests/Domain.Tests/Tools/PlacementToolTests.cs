using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class PlacementToolTests
{
    private static SnapResult Click(float x, float z) => SnapResult.Free(new Vector3(x, 0, z));

    [Fact]
    public void StraightToolCommitsOnSecondClick()
    {
        var tool = new StraightTool { RoadType = RoadCatalog.TwoLane.Id };
        Assert.Null(tool.AddClick(Click(0, 0)));
        var proposal = tool.AddClick(Click(100, 0));
        Assert.NotNull(proposal);
        var curve = Assert.Single(proposal!.Curves).Curve;
        Assert.Equal(new Vector3(0, 0, 0), curve.P0);
        Assert.Equal(new Vector3(100, 0, 0), curve.P3);
        Assert.Equal(0, tool.ClickCount); // reset after commit
    }

    [Fact]
    public void StraightToolPropagatesBindings()
    {
        var tool = new StraightTool { RoadType = RoadCatalog.TwoLane.Id };
        var nodeSnap = new SnapResult(new Vector3(0, 0, 0), SnapKind.Node, new NodeId(7), null, null,
            Array.Empty<Guideline>());
        var edgeSnap = new SnapResult(new Vector3(100, 0, 0), SnapKind.Edge, null, (new EdgeId(3), 0.4f), null,
            Array.Empty<Guideline>());
        tool.AddClick(nodeSnap);
        var proposal = tool.AddClick(edgeSnap)!;
        var pc = Assert.Single(proposal.Curves);
        var atNode = Assert.IsType<EndpointBinding.AtNode>(pc.Start);
        Assert.Equal(new NodeId(7), atNode.Node);
        var onEdge = Assert.IsType<EndpointBinding.OnEdge>(pc.End);
        Assert.Equal(new EdgeId(3), onEdge.Edge);
        Assert.Equal(0.4f, onEdge.T);
    }

    [Fact]
    public void SimpleCurveToolUsesMiddleClickAsControl()
    {
        var tool = new SimpleCurveTool { RoadType = RoadCatalog.TwoLane.Id };
        Assert.Null(tool.AddClick(Click(0, 0)));
        Assert.Null(tool.AddClick(Click(50, 50)));
        var proposal = tool.AddClick(Click(100, 0))!;
        var curve = Assert.Single(proposal.Curves).Curve;
        // degree-elevated quadratic: P1 = A + 2/3 (C - A)
        Assert.True(Vector3.Distance(curve.P1, new Vector3(100f / 3, 0, 100f / 3)) < 0.01f);
        Assert.Equal(new Vector3(100, 0, 0), curve.P3);
    }

    [Fact]
    public void ComplexCurveToolUsesTwoControls()
    {
        var tool = new ComplexCurveTool { RoadType = RoadCatalog.TwoLane.Id };
        tool.AddClick(Click(0, 0));
        tool.AddClick(Click(30, 60));
        tool.AddClick(Click(70, 60));
        var proposal = tool.AddClick(Click(100, 0))!;
        var curve = Assert.Single(proposal.Curves).Curve;
        Assert.Equal(new Vector3(30, 0, 60), curve.P1);
        Assert.Equal(new Vector3(70, 0, 60), curve.P2);
    }

    [Fact]
    public void StepBackRemovesLastClick()
    {
        var tool = new SimpleCurveTool { RoadType = RoadCatalog.TwoLane.Id };
        tool.AddClick(Click(0, 0));
        tool.AddClick(Click(50, 50));
        tool.StepBack();
        Assert.Equal(1, tool.ClickCount);
        // finish differently
        tool.AddClick(Click(50, -50));
        var proposal = tool.AddClick(Click(100, 0))!;
        Assert.True(proposal.Curves[0].Curve.P1.Z < 0);
    }

    [Fact]
    public void ContinuousToolChainsTangentContinuously()
    {
        var tool = new ContinuousTool { RoadType = RoadCatalog.TwoLane.Id };
        Assert.Null(tool.AddClick(Click(0, 0)));
        Assert.Null(tool.AddClick(Click(50, 20)));
        var first = tool.AddClick(Click(100, 0));
        Assert.NotNull(first); // first segment commits on 3rd click
        var c1 = first!.Curves[0].Curve;

        // next click commits the second segment immediately, chained G1
        var second = tool.AddClick(Click(160, -40));
        Assert.NotNull(second);
        var c2 = second!.Curves[0].Curve;
        Assert.Equal(c1.P3, c2.P0);
        var endTangent = Vector3.Normalize(c1.P3 - c1.P2);
        var startTangent = Vector3.Normalize(c2.P1 - c2.P0);
        Assert.True(Vector3.Distance(endTangent, startTangent) < 0.01f,
            $"tangent break: {endTangent} vs {startTangent}");
    }

    [Fact]
    public void GridToolStampsWholeCells()
    {
        var tool = new GridTool { RoadType = RoadCatalog.TwoLane.Id };
        Assert.Null(tool.AddClick(Click(0, 0)));
        Assert.Null(tool.AddClick(Click(96, 0)));   // axis1: 2 cells of 48
        var proposal = tool.AddClick(Click(96, 96))!; // axis2: 2 cells
        // 3 lines along each axis
        Assert.Equal(6, proposal.Curves.Count);

        var n = Net.New();
        var v = n.Validate(proposal);
        Assert.True(v.IsValid);
        Assert.True(n.Commit(v).Success);
        Assert.Equal(9, n.Nodes.Count);
        Assert.Equal(12, n.Edges.Count);
        Assert.True(LaneGraph.IsStronglyConnected(n));
    }

    [Fact]
    public void GridToolDropsPartialCells()
    {
        var tool = new GridTool { RoadType = RoadCatalog.TwoLane.Id };
        tool.AddClick(Click(0, 0));
        tool.AddClick(Click(70, 0));    // 1 whole cell (48), 22 m remainder dropped
        var proposal = tool.AddClick(Click(70, 50));
        Assert.NotNull(proposal);
        Assert.Equal(4, proposal!.Curves.Count); // 2 lines per axis
    }

    [Fact]
    public void GridToolWithSubCellDragIsEmpty()
    {
        var tool = new GridTool { RoadType = RoadCatalog.TwoLane.Id };
        tool.AddClick(Click(0, 0));
        tool.AddClick(Click(30, 0));
        Assert.Null(tool.AddClick(Click(30, 30))); // no whole cell: nothing to commit
    }

    [Fact]
    public void ReadoutReportsLengthAndAngle()
    {
        var tool = new StraightTool { RoadType = RoadCatalog.TwoLane.Id };
        tool.AddClick(Click(0, 0));
        var readout = tool.Readout(Click(50, 50));
        Assert.NotNull(readout);
        Assert.Equal(70.7f, readout!.Value.lengthM, 0);
        Assert.Equal(45f, readout.Value.angleDeg, 0);
    }

    [Fact]
    public void PreviewProducesGhostWithoutMutatingState()
    {
        var tool = new StraightTool { RoadType = RoadCatalog.TwoLane.Id };
        tool.AddClick(Click(0, 0));
        var ghost = tool.Preview(Click(80, 0));
        Assert.NotNull(ghost);
        Assert.Equal(1, tool.ClickCount);
        Assert.Equal(new Vector3(80, 0, 0), ghost!.Curves[0].Curve.P3);
    }
}
