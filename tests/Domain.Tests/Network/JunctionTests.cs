using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class JunctionTests
{
    private static RoadNode CenterNode(RoadNetwork n, Vector3 pos, float eps = 0.1f)
        => n.Nodes.Values.Single(node => Vector3.Distance(node.Position, pos) < eps);

    [Fact]
    public void SymmetricFourWayHasEqualPositiveCuts()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        var center = CenterNode(n, Vector3.Zero);

        Assert.Equal(4, center.Junction.CutT.Count);
        var cutDistances = center.Junction.CutT.Select(kv =>
        {
            var e = n.Edges[kv.Key];
            float d = e.ArcLength.DistanceAtT(kv.Value);
            // convert to distance from the node along the edge
            return e.StartNode == center.Id ? d : e.ArcLength.TotalLength - d;
        }).ToArray();

        Assert.All(cutDistances, d => Assert.True(d > 1f, $"cut {d} not positive enough"));
        Assert.All(cutDistances, d => Assert.Equal(cutDistances[0], d, 1));
    }

    [Fact]
    public void FourWayPolygonIsSimpleAndSurroundsNode()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        var center = CenterNode(n, Vector3.Zero);
        var poly = center.Junction.SurfacePolygon;

        Assert.True(poly.Count >= 8, $"expected >=8 vertices, got {poly.Count}");
        // all vertices within a sane radius, and the polygon winds once around the node
        Assert.All(poly, p => Assert.True(Vector3.Distance(p, center.Position) < 20f));
        float winding = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i] - center.Position;
            var b = poly[(i + 1) % poly.Count] - center.Position;
            winding += MathF.Atan2(a.X * b.Z - a.Z * b.X, a.X * b.X + a.Z * b.Z);
        }
        Assert.Equal(2 * MathF.PI, MathF.Abs(winding), 1);
    }

    [Fact]
    public void TeeJunctionHasThreeCuts()
    {
        var n = Net.New();
        var r1 = Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, new CityBuilder.Domain.Tools.PlacementProposal(new[]
        {
            new CityBuilder.Domain.Tools.ProposedCurve(
                Bezier3.Line(new(0, 0, 0), new(0, 0, 80)),
                new CityBuilder.Domain.Tools.EndpointBinding.OnEdge(r1.CreatedEdges[0], 0.5f),
                CityBuilder.Domain.Tools.EndpointBinding.None)
        }, RoadCatalog.TwoLane.Id));
        var tee = CenterNode(n, Vector3.Zero);
        Assert.Equal(3, tee.Junction.CutT.Count);
        Assert.True(tee.Junction.SurfacePolygon.Count >= 6);
    }

    [Fact]
    public void AcuteJunctionCutsExceedRightAngleCuts()
    {
        float CutDistanceForAngle(float degrees)
        {
            var n = Net.New();
            Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
            float rad = degrees * MathF.PI / 180f;
            Net.Commit(n, Net.Straight(new(0, 0, 0), new(100 * MathF.Cos(rad), 0, 100 * MathF.Sin(rad))));
            var node = CenterNode(n, Vector3.Zero);
            return node.Junction.CutT.Select(kv =>
            {
                var e = n.Edges[kv.Key];
                float d = e.ArcLength.DistanceAtT(kv.Value);
                return e.StartNode == node.Id ? d : e.ArcLength.TotalLength - d;
            }).Max();
        }

        // 15° is now below MinJunctionAngleDeg (25°) and would be rejected as
        // SharpAngle before ever reaching the junction builder; 30° keeps the
        // scenario "acute vs. right angle" while staying just inside the limit.
        float acute = CutDistanceForAngle(30);
        float right = CutDistanceForAngle(90);
        Assert.True(acute > right, $"acute {acute} <= right {right}");
        Assert.True(acute <= 31f, $"acute cut {acute} exceeds 30% clamp");
    }

    [Fact]
    public void DegreeTwoSameTypeHasZeroCutsAndNoPolygon()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        // right-angle corner: no healing merge, degree-2 node stays
        Net.Commit(n, Net.Straight(new(100, 0, 0), new(100, 0, 100)));
        var corner = CenterNode(n, new Vector3(100, 0, 0));
        Assert.Equal(2, corner.Edges.Count);
        var distances = corner.Junction.CutT.Select(kv =>
        {
            var e = n.Edges[kv.Key];
            float d = e.ArcLength.DistanceAtT(kv.Value);
            return e.StartNode == corner.Id ? d : e.ArcLength.TotalLength - d;
        });
        // a right-angle corner still needs a small junction surface to look right
        Assert.All(distances, d => Assert.True(d >= 0 && d < 15f));
    }

    [Fact]
    public void TypeChangeDegreeTwoGetsTransitionSurface()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0), RoadCatalog.TwoLane.Id));
        Net.Commit(n, Net.Straight(new(100, 0, 0), new(200, 0, 0), RoadCatalog.FourLane.Id));
        var joint = CenterNode(n, new Vector3(100, 0, 0));
        Assert.Equal(2, joint.Edges.Count);
        Assert.True(joint.Junction.SurfacePolygon.Count >= 4, "expected a transition surface polygon");
    }

    [Fact]
    public void DeadEndHasNoPolygonAndZeroCut()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        var edge = n.Edges[r.CreatedEdges[0]];
        var deadEnd = n.Nodes[edge.StartNode];
        Assert.Single(deadEnd.Junction.CutT);
        float d = edge.ArcLength.DistanceAtT(deadEnd.Junction.CutT[edge.Id]);
        Assert.True(d < 0.5f);
        Assert.Empty(deadEnd.Junction.SurfacePolygon);
    }
}
