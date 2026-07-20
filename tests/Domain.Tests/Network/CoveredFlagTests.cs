using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

/// <summary>M8.5: the explicit per-edge Covered (tunnel) flag. It has no validation
/// surface — toggling is always legal on any ordinary edge — so what matters here are
/// the propagation invariants: no editing operation may silently invent or drop a
/// tunnel, and mixed merges come out uncovered (conservative and visible).</summary>
public class CoveredFlagTests
{
    [Fact]
    public void SetCoveredRaisesEdgesChangedAndBumpsVersion()
    {
        var n = Net.New();
        var id = Net.Commit(n, Net.Straight(new(0, 0, 0), new(60, 0, 0))).CreatedEdges.Single();
        int v = n.Version;
        NetworkDelta? seen = null;
        n.Changed += d => seen = d;

        Assert.True(n.SetCovered(id, true));
        Assert.True(n.Edges[id].Covered);
        Assert.True(n.Version > v);
        Assert.NotNull(seen);
        Assert.Contains(id, seen!.EdgesChanged);
    }

    [Fact]
    public void SetCoveredIsFalseOnNoOpAndUnknownEdge()
    {
        var n = Net.New();
        var id = Net.Commit(n, Net.Straight(new(0, 0, 0), new(60, 0, 0))).CreatedEdges.Single();
        Assert.False(n.SetCovered(id, false));              // already uncovered: no-op
        Assert.False(n.SetCovered(new EdgeId(9999), true)); // unknown edge
        Assert.False(n.Edges[id].Covered);
    }

    [Fact]
    public void SetCoveredRefusesRoundaboutRingEdges()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        var center = n.Nodes.Values.Single(node => node.EdgeSet.Count == 4);
        var res = n.ConvertToRoundabout(center.Id, 16f);
        Assert.True(res.Success, $"conversion failed: {res.Error}");
        var ringEdge = n.Roundabouts.Values.Single().RingEdges[0];

        Assert.False(n.SetCovered(ringEdge, true));
        Assert.False(n.Edges[ringEdge].Covered);
    }

    [Fact]
    public void SplitChildrenInheritCovered()
    {
        var n = Net.New();
        var id = Net.Commit(n, Net.Straight(new(0, 0, 0), new(120, 0, 0))).CreatedEdges.Single();
        n.SetCovered(id, true);

        // crossing road splits the covered edge; both halves must stay covered
        Net.Commit(n, Net.Straight(new(60, 0, -60), new(60, 0, 60)));
        var halves = n.Edges.Values
            .Where(e => MathF.Abs(e.Curve.P0.Z) < 0.1f && MathF.Abs(e.Curve.P3.Z) < 0.1f)
            .ToList();
        Assert.Equal(2, halves.Count);
        Assert.All(halves, e => Assert.True(e.Covered, $"edge {e.Id.Value} lost its cover on split"));
    }

    [Fact]
    public void HealKeepsCoveredOnlyWhenBothAgree()
    {
        var n = Net.New();
        var left = Net.Commit(n, Net.Straight(new(0, 0, 0), new(60, 0, 0))).CreatedEdges.Single();
        Net.Commit(n, Net.Straight(new(60, 0, 0), new(120, 0, 0)));
        var arm = Net.Commit(n, Net.Straight(new(60, 0, 0), new(60, 0, 80))).CreatedEdges.Single();
        n.SetCovered(left, true); // only one side of the future merge is covered

        n.RemoveEdge(arm); // degree-2 heal merges left+right
        var healed = Assert.Single(n.Edges.Values);
        Assert.False(healed.Covered, "mixed heal must come out uncovered (conservative)");
    }

    [Fact]
    public void HealPreservesCoveredWhenBothAgree()
    {
        var n = Net.New();
        var left = Net.Commit(n, Net.Straight(new(0, 0, 0), new(60, 0, 0))).CreatedEdges.Single();
        var right = Net.Commit(n, Net.Straight(new(60, 0, 0), new(120, 0, 0))).CreatedEdges.Single();
        var arm = Net.Commit(n, Net.Straight(new(60, 0, 0), new(60, 0, 80))).CreatedEdges.Single();
        n.SetCovered(left, true);
        n.SetCovered(right, true);

        n.RemoveEdge(arm);
        var healed = Assert.Single(n.Edges.Values);
        Assert.True(healed.Covered, "agreeing covered halves must heal covered");
    }

    [Fact]
    public void RetypeAndFlipPreserveCovered()
    {
        var n = Net.New();
        var id = Net.Commit(n, Net.Straight(new(0, 0, 0), new(60, 0, 0))).CreatedEdges.Single();
        n.SetCovered(id, true);

        Assert.Null(n.RetypeEdge(id, RoadCatalog.Street.Id));
        Assert.True(n.Edges[id].Covered, "retype must not drop the tunnel");
        Assert.True(n.FlipEdge(id));
        Assert.True(n.Edges[id].Covered, "flip must not drop the tunnel");
    }
}
