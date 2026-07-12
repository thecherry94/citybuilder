using System.Numerics;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class SnapServiceTests
{
    private static (RoadNetwork n, SnapService snap) Setup()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        return (n, new SnapService(n));
    }

    [Fact]
    public void NodeBeatsEdgeWhenBothInRange()
    {
        var (n, snap) = Setup();
        // 2 m from the end node, 1.2 m from the edge centerline: node must win
        var result = snap.Resolve(new Vector3(98.5f, 0, 1.2f), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Node, result.Kind);
        var node = n.Nodes[result.Node!.Value];
        Assert.Equal(new Vector3(100, 0, 0), node.Position);
        Assert.Equal(node.Position, result.Position);
    }

    [Fact]
    public void EdgeSnapProjectsOntoCenterline()
    {
        var (_, snap) = Setup();
        var result = snap.Resolve(new Vector3(50, 0, 3), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Edge, result.Kind);
        Assert.NotNull(result.Edge);
        Assert.True(Vector3.Distance(result.Position, new Vector3(50, 0, 0)) < 0.1f);
    }

    [Fact]
    public void DisabledSnapTypesAreSkipped()
    {
        var (_, snap) = Setup();
        var raw = new Vector3(50, 0, 3);
        var result = snap.Resolve(raw, 5f, SnapTypes.None, SnapContext.Empty);
        Assert.Equal(SnapKind.Free, result.Kind);
        Assert.Equal(raw, result.Position);
    }

    [Fact]
    public void OutOfRadiusStaysFree()
    {
        var (_, snap) = Setup();
        var raw = new Vector3(50, 0, 30);
        var result = snap.Resolve(raw, 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Free, result.Kind);
    }

    [Fact]
    public void AngleSnapRoundsToFifteenDegrees()
    {
        var (_, snap) = Setup();
        // anchor at (200,0,200) far from the network; 43° from +X should snap to 45°
        var anchor = new Vector3(200, 0, 200);
        float rad = 43 * MathF.PI / 180;
        var raw = anchor + 50 * new Vector3(MathF.Cos(rad), 0, MathF.Sin(rad));
        var result = snap.Resolve(raw, 5f, SnapTypes.Angle, new SnapContext(anchor, null));
        Assert.Equal(SnapKind.Angle, result.Kind);
        Assert.Equal(45f, result.SnappedAngleDeg!.Value, 1);
        var dir = Vector3.Normalize(result.Position - anchor);
        Assert.Equal(MathF.Cos(45 * MathF.PI / 180), dir.X, 2);
        // distance preserved
        Assert.Equal(50f, Vector3.Distance(result.Position, anchor), 1);
    }

    [Fact]
    public void AngleSnapUsesReferenceTangent()
    {
        var (_, snap) = Setup();
        var anchor = new Vector3(200, 0, 200);
        // reference direction 10°; raw at 53° from +X = 43° from reference → snaps to 45° from reference = 55° absolute
        float refRad = 10 * MathF.PI / 180;
        var reference = new Vector3(MathF.Cos(refRad), 0, MathF.Sin(refRad));
        float rawRad = 53 * MathF.PI / 180;
        var raw = anchor + 50 * new Vector3(MathF.Cos(rawRad), 0, MathF.Sin(rawRad));
        var result = snap.Resolve(raw, 5f, SnapTypes.Angle, new SnapContext(anchor, reference));
        var dir = Vector3.Normalize(result.Position - anchor);
        float resultDeg = MathF.Atan2(dir.Z, dir.X) * 180 / MathF.PI;
        Assert.Equal(55f, resultDeg, 1);
    }

    [Fact]
    public void GuidelineExtendsFromDeadEndTangent()
    {
        var (_, snap) = Setup();
        // dead end at (100,0,0) pointing +X: a point near the extension line should snap onto it
        var raw = new Vector3(150, 0, 2.5f);
        var result = snap.Resolve(raw, 5f, SnapTypes.Guidelines, SnapContext.Empty);
        Assert.Equal(SnapKind.Guideline, result.Kind);
        Assert.True(MathF.Abs(result.Position.Z) < 0.1f, $"expected on x-axis, got {result.Position}");
        Assert.Equal(150f, result.Position.X, 0);
        Assert.NotEmpty(result.ActiveGuidelines);
    }

    [Fact]
    public void GuidelineIntersectionBeatsPlainGuideline()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(150, 0, 50), new(150, 0, 40)));
        var snap = new SnapService(n);
        // extension of first road (+X from (100,0,0)) meets extension of second (−Z from (150,0,40)) at (150,0,0)
        var result = snap.Resolve(new Vector3(148, 0, 1.5f), 5f, SnapTypes.Guidelines, SnapContext.Empty);
        Assert.Equal(SnapKind.GuidelineIntersection, result.Kind);
        Assert.True(Vector3.Distance(result.Position, new Vector3(150, 0, 0)) < 0.1f,
            $"got {result.Position}");
    }

    [Fact]
    public void NodeBeatsGuidelineIntersection()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(100, 0, 5), new(100, 0, 50)));
        var snap = new SnapService(n);
        var result = snap.Resolve(new Vector3(99, 0, 1), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Node, result.Kind);
    }
}
