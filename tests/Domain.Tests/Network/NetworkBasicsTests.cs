using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public static class Net
{
    public static RoadNetwork New() => new();

    public static PlacementProposal Straight(Vector3 a, Vector3 b, RoadTypeId? type = null,
        EndpointBinding? start = null, EndpointBinding? end = null)
        => new(new[]
        {
            new ProposedCurve(Bezier3.Line(a, b), start ?? EndpointBinding.None, end ?? EndpointBinding.None)
        }, type ?? RoadCatalog.TwoLane.Id);

    public static CommitResult Commit(RoadNetwork n, PlacementProposal p)
    {
        var v = n.Validate(p);
        Assert.True(v.IsValid, $"expected valid placement, errors: {string.Join(",", v.Errors)}");
        var r = n.Commit(v);
        Assert.True(r.Success, $"commit failed: {r.FailureReason}");
        return r;
    }
}

public class NetworkBasicsTests
{
    [Fact]
    public void AddFreeEdgeCreatesTwoNodesAndLanes()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        Assert.Equal(2, n.Nodes.Count);
        var edge = n.Edges[Assert.Single(r.CreatedEdges)];
        Assert.Equal(2, edge.Lanes.Count);
        Assert.Equal(RoadCatalog.TwoLane.Id, edge.Type);
    }

    [Fact]
    public void BindingToExistingNodeReusesIt()
    {
        var n = Net.New();
        var r1 = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var endNode = n.Edges[r1.CreatedEdges[0]].EndNode;
        Net.Commit(n, Net.Straight(new(100, 0, 0), new(100, 0, 80),
            start: new EndpointBinding.AtNode(endNode)));
        Assert.Equal(3, n.Nodes.Count);
        Assert.Equal(2, n.Nodes[endNode].Edges.Count);
    }

    [Fact]
    public void RemoveEdgeDropsOrphanNodes()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        n.RemoveEdge(r.CreatedEdges[0]);
        Assert.Empty(n.Edges);
        Assert.Empty(n.Nodes);
    }

    [Fact]
    public void ChangedEventCarriesExactDelta()
    {
        var n = Net.New();
        NetworkDelta? delta = null;
        n.Changed += d => delta = d;
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        Assert.NotNull(delta);
        Assert.Equal(r.CreatedEdges.ToHashSet(), delta!.EdgesAdded);
        Assert.Equal(2, delta.NodesAdded.Count);
        Assert.Empty(delta.EdgesRemoved);
    }

    [Fact]
    public void VersionIncrementsPerMutation()
    {
        var n = Net.New();
        int v0 = n.Version;
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        Assert.True(n.Version > v0);
        int v1 = n.Version;
        n.RemoveEdge(r.CreatedEdges[0]);
        Assert.True(n.Version > v1);
    }

    [Fact]
    public void FindClosestEdgeReturnsHit()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var hit = n.FindClosestEdge(new Vector3(50, 0, 3), 6f);
        Assert.NotNull(hit);
        Assert.Equal(r.CreatedEdges[0], hit!.Value.id);
        Assert.Equal(3f, hit.Value.dist, 1);
        Assert.Null(n.FindClosestEdge(new Vector3(50, 0, 30), 6f));
    }
}
