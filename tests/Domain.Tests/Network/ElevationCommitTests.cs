using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class ElevationCommitTests
{
    private static Bezier3 Ramp(Vector3 a, Vector3 b) => ElevationValidationTests.Ramp(a, b);
    private static PlacementProposal One(Bezier3 c) => ElevationValidationTests.One(c);

    [Fact]
    public void BridgeCommitCreatesNoJunction()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var r = Net.Commit(n, One(Ramp(new(0, 6, -80), new(0, 6, 80))));
        Assert.Equal(0, r.DroppedSegments);
        // 1 ground edge + 1 bridge edge — the ground road was NOT split
        Assert.Equal(2, n.Edges.Count);
        Assert.Equal(4, n.Nodes.Count);
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void RampToBridgeToRampOverRoadIsBuildable()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        // up-ramp, level bridge over the road, down-ramp — drawn as three gestures
        Net.Commit(n, One(Ramp(new(0, 0, -160), new(0, 6, -60))));   // 6%
        Net.Commit(n, One(Ramp(new(0, 6, -60), new(0, 6, 60))));     // level, over the road
        Net.Commit(n, One(Ramp(new(0, 6, 60), new(0, 0, 160))));     // down
        Assert.Equal(4, n.Edges.Count); // ground road intact + 3 bridge pieces
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void StackedNodesAtSameXZAreDistinct()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, One(Ramp(new(-100, 8, 0), new(100, 8, 0)))); // directly above, +8 m
        // endpoints at the same XZ as the ground road's ends must NOT have reused them
        Assert.Equal(2, n.Edges.Count);
        Assert.Equal(4, n.Nodes.Count);
        Assert.Empty(NetworkInvariants.Check(n));
    }
}
