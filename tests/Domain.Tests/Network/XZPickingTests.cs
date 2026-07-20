using System.Numerics;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

/// <summary>M8.5 (found by the covered-toggle UITEST): tool picking happens from the
/// ground-plane cursor, but FindClosestEdge measures 3D distance — an elevated or dug
/// deck is |Y| metres away before any lateral error, so bridges (M8!) and tunnels
/// could never be hovered by upgrade/bulldoze/inspect. The XZ variants pick in plan
/// view and tie-break stacked decks toward the one nearest the ground.</summary>
public class XZPickingTests
{
    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void DugEdgeIsPickableFromTheGroundCursor()
    {
        var n = Net.New();
        var id = Net.Commit(n, Net.Straight(V(0, -8, 0), V(120, -8, 0))).CreatedEdges.Single();

        Assert.Null(n.FindClosestEdge(V(60, 0, 0), 6f));          // the 3D pick misses…
        var hit = n.FindClosestEdgeXZ(V(60, 0, 0), 6f);           // …the XZ pick lands
        Assert.NotNull(hit);
        Assert.Equal(id, hit!.Value.id);
        Assert.True(hit.Value.dist < 0.5f);
    }

    [Fact]
    public void StackedDecksResolveToTheOneNearestTheGround()
    {
        var n = Net.New();
        var ground = Net.Commit(n, Net.Straight(V(0, 0, 0), V(120, 0, 0))).CreatedEdges.Single();
        Net.Commit(n, Net.Straight(V(0, 10, 1), V(120, 10, 1))); // bridge right above

        var hit = n.FindClosestEdgeXZ(V(60, 0, 0.5f), 6f);
        Assert.NotNull(hit);
        Assert.Equal(ground, hit!.Value.id);
    }

    [Fact]
    public void ElevatedNodeIsPickableFromTheGroundCursor()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(V(0, 10, 0), V(120, 10, 0)));
        var end = n.Edges[r.CreatedEdges.Single()].EndNode;

        Assert.Null(n.FindNodeNear(V(120, 0, 0), 8f));            // 3D pick misses
        Assert.Equal(end, n.FindNodeNearXZ(V(120, 0, 0), 8f));    // XZ pick lands
    }
}
