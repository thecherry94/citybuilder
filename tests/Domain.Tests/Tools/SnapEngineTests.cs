using System.Numerics;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class SnapEngineTests
{
    private static (RoadNetwork n, SnapEngine snap) Setup()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));
        return (n, new SnapEngine(n));
    }

    [Fact]
    public void NodeBeatsEdgeWhenBothInRange()
    {
        var (n, snap) = Setup();
        var result = snap.Resolve(new Vector3(98.5f, 0, 1.2f), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Node, result.Kind);
        Assert.Equal(n.Nodes[result.Node!.Value].Position, result.Position);
    }

    [Fact]
    public void EdgeSnapProjectsOntoCenterline()
    {
        var (_, snap) = Setup();
        var result = snap.Resolve(new Vector3(50, 0, 3), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Edge, result.Kind);
        Assert.True(Vector3.Distance(result.Position, new Vector3(50, 0, 0)) < 0.1f);
    }

    [Fact]
    public void GuidelineExtensionSnapsPastTheNode()
    {
        var (_, snap) = Setup();
        var result = snap.Resolve(new Vector3(130, 0, 2.5f), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Guideline, result.Kind);
        Assert.True(Vector3.Distance(result.Position, new Vector3(130, 0, 0)) < 0.1f);
    }

    [Fact]
    public void DeadOnWeakSnapBeatsBarelyInRangeStrongSnap()
    {
        var (_, snap) = Setup();
        // cursor exactly on the guideline extension, 4.6 m from the node: guideline wins
        var result = snap.Resolve(new Vector3(104.6f, 0, 0.01f), 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Guideline, result.Kind);
    }

    [Fact]
    public void AngleSnapMeasuresFromReferenceTangent()
    {
        var (_, snap) = Setup();
        // anchor at the far node, reference tangent +X (extending the road):
        // cursor ~46° up-right must snap to the 45° ray FROM +X, not from world axes
        var ctx = new SnapContext(new Vector3(100, 0, 0), new Vector3(1, 0, 0));
        var raw = new Vector3(100, 0, 0) + 40f * new Vector3(MathF.Cos(0.82f), 0, MathF.Sin(0.82f));
        var result = snap.Resolve(raw, 2f, SnapTypes.Angle, ctx);
        Assert.Equal(SnapKind.Angle, result.Kind);
        Assert.Equal(45f, result.SnappedAngleDeg!.Value, 1);
        var dir = Vector3.Normalize(result.Position - new Vector3(100, 0, 0));
        Assert.Equal(MathF.Cos(MathF.PI / 4), dir.X, 2);
        Assert.Equal(MathF.Sin(MathF.PI / 4), dir.Z, 2);
    }

    [Fact]
    public void FreeWhenNothingInRange()
    {
        var (_, snap) = Setup();
        var raw = new Vector3(500, 0, 500);
        var result = snap.Resolve(raw, 5f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Free, result.Kind);
        Assert.Equal(raw, result.Position);
    }
}
