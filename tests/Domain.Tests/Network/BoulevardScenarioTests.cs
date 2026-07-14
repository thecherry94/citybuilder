using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

/// <summary>Spec success criterion: draw a curved boulevard through an existing grid,
/// get intersections at every crossing; bulldoze it and the network heals.</summary>
public class BoulevardScenarioTests
{
    [Fact]
    public void CurvedBoulevardThroughGridCreatesIntersectionsAndHealsOnBulldoze()
    {
        var n = Net.New();

        // 3×3-node grid via the grid tool (2 cells per axis)
        var grid = new GridTool { RoadType = RoadCatalog.TwoLane.Id };
        grid.AddClick(SnapResult.Free(new Vector3(0, 0, 0)));
        grid.AddClick(SnapResult.Free(new Vector3(96, 0, 0)));
        var gridProposal = grid.AddClick(SnapResult.Free(new Vector3(96, 0, 96)))!;
        Assert.True(n.Commit(n.Validate(gridProposal)).Success);
        int gridEdges = n.Edges.Count;
        int gridNodes = n.Nodes.Count;
        Assert.Equal(12, gridEdges);
        Assert.Equal(9, gridNodes);

        // gentle diagonal boulevard (four-lane complex curve) across the grid;
        // asymmetric control points (flat early, steep late) so the x=48 and z=48
        // grid-line crossings land well apart instead of both grazing the (48,48)
        // grid node — task 5's crossing-spacing guard rejects that near-duplicate pair.
        var boulevard = new Bezier3(new(-30, 0, 10), new(60, 0, 15), new(80, 0, 70), new(130, 0, 85));
        var v = n.Validate(new PlacementProposal(
            new[] { new ProposedCurve(boulevard, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.FourLane.Id));
        Assert.True(v.IsValid, string.Join(",", v.Errors));
        Assert.True(v.CrossingPoints.Count >= 3, $"expected several crossings, got {v.CrossingPoints.Count}");
        var r = n.Commit(v);
        Assert.True(r.Success);

        // every crossing became a real shared node with junction geometry and connectors
        Assert.True(n.Edges.Count > gridEdges + 1);
        var boulevardNodes = n.Nodes.Values
            .Where(node => node.Edges.Count >= 3
                && node.Edges.Any(e => n.Edges[e].Type == RoadCatalog.FourLane.Id))
            .ToList();
        Assert.True(boulevardNodes.Count >= 3, $"expected >=3 boulevard junctions, got {boulevardNodes.Count}");
        Assert.All(boulevardNodes, node =>
        {
            Assert.NotEmpty(node.Junction.SurfacePolygon);
            Assert.NotEmpty(node.Connectors);
        });
        Assert.True(LaneGraph.IsStronglyConnected(n));

        // bulldoze the whole boulevard: every four-lane edge
        foreach (var e in n.Edges.Values.Where(e => e.Type == RoadCatalog.FourLane.Id).Select(e => e.Id).ToList())
            n.RemoveEdge(e);

        // grid heals back to its original shape
        Assert.Equal(gridEdges, n.Edges.Count);
        Assert.Equal(gridNodes, n.Nodes.Count);
        Assert.True(LaneGraph.IsStronglyConnected(n));
    }
}
