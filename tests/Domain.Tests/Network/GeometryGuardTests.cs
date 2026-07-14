using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

/// <summary>Invariants the M4 geometry guards must keep: whatever sequence of valid
/// commits happens, the network never contains sliver edges or sharp junction legs.</summary>
public class GeometryGuardTests
{
    [Fact]
    public void CommittedNetworkHasNoSliversAndNoSharpLegs()
    {
        var n = new RoadNetwork();
        // a representative editing session: grid-ish mesh, diagonals, T-connections
        TryCommit(n, Net.Straight(new(0, 0, 0), new(200, 0, 0)));
        TryCommit(n, Net.Straight(new(0, 0, 100), new(200, 0, 100)));
        TryCommit(n, Net.Straight(new(50, 0, -50), new(50, 0, 150)));
        TryCommit(n, Net.Straight(new(150, 0, -50), new(150, 0, 150)));
        TryCommit(n, Net.Straight(new(0, 0, -30), new(200, 0, 130)));       // diagonal, crosses several
        TryCommit(n, Net.Straight(new(52, 0, -48), new(50, 0, 150)));       // near-duplicate — must be rejected, not committed
        TryCommit(n, Net.Straight(new(148, 0, 2), new(152, 0, 98)));        // sliver-ish vertical near existing
        foreach (var e in n.Edges.Values)
        {
            float min = RoadCatalog.Get(e.Type).MinSegmentLength;
            Assert.True(e.Curve.Length() >= min - 0.1f,
                $"edge {e.Id} length {e.Curve.Length():F1} < min {min}");
        }
        foreach (var node in n.Nodes.Values)
        {
            var legs = node.EdgeSet.Select(id =>
            {
                var e = n.Edges[id];
                return e.StartNode == node.Id ? e.Curve.Tangent(0) : -e.Curve.Tangent(1);
            }).ToArray();
            for (int i = 0; i < legs.Length; i++)
            for (int j = i + 1; j < legs.Length; j++)
            {
                float cross = MathF.Abs(legs[i].X * legs[j].Z - legs[i].Z * legs[j].X);
                float dot = legs[i].X * legs[j].X + legs[i].Z * legs[j].Z;
                float deg = MathF.Atan2(cross, dot) * 180f / MathF.PI;
                Assert.True(deg >= RoadNetwork.MinJunctionAngleDeg - 0.5f,
                    $"node {node.Id}: legs {deg:F1}° apart");
            }
        }
    }

    private static void TryCommit(RoadNetwork n, PlacementProposal p)
    {
        var v = n.Validate(p);
        if (v.IsValid)
            n.Commit(v);
    }
}
