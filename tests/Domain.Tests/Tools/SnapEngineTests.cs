using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
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
    public void NodeCaptureBeatsEdgeOnLeg()
    {
        // THE T-junction complaint: cursor ON the edge (dist 0.5) but 3.04 m from the
        // node — inside the hard-capture ring (0.6 × 6 = 3.6). Node must win outright;
        // the old weight scoring gave the edge score 0.25 vs node 0.76 and slid forever.
        var (n, snap) = Setup();
        var result = snap.Resolve(new Vector3(97f, 0, 0.5f), 6f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Node, result.Kind);
        Assert.Equal(new Vector3(100, 0, 0), result.Position);
    }

    [Fact]
    public void SoftZonePreservesEdgeForMidSpanIntent()
    {
        // node 4.90 m away — outside the capture ring (3.6), inside the resolve radius:
        // weight scoring still lets the dead-on edge win, so mid-span splits near (but
        // not at) a junction remain reachable.
        var (_, snap) = Setup();
        var result = snap.Resolve(new Vector3(95.2f, 0, 1.0f), 6f, SnapTypes.All, SnapContext.Empty);
        Assert.Equal(SnapKind.Edge, result.Kind);
    }

    [Fact]
    public void HysteresisHoldsInsideReleaseRing()
    {
        // held node 4.90 m away: outside capture (3.6) but inside release (1.4 × 3.6 =
        // 5.04) — the hold keeps it winning over the dead-on edge (contrast with
        // SoftZonePreservesEdgeForMidSpanIntent, same cursor without a hold).
        var (n, snap) = Setup();
        var held = n.Nodes.Values.Single(x => x.Position == new Vector3(100, 0, 0)).Id;
        var ctx = SnapContext.Empty with { HeldNode = held };
        var result = snap.Resolve(new Vector3(95.2f, 0, 1.0f), 6f, SnapTypes.All, ctx);
        Assert.Equal(SnapKind.Node, result.Kind);
        Assert.Equal(held, result.Node);
    }

    [Fact]
    public void HysteresisReleasesBeyondRing()
    {
        // 6.08 m from the held node > release ring 5.04 — hold drops, edge wins again.
        var (n, snap) = Setup();
        var held = n.Nodes.Values.Single(x => x.Position == new Vector3(100, 0, 0)).Id;
        var ctx = SnapContext.Empty with { HeldNode = held };
        var result = snap.Resolve(new Vector3(94f, 0, 1.0f), 6f, SnapTypes.All, ctx);
        Assert.Equal(SnapKind.Edge, result.Kind);
    }

    [Fact]
    public void HysteresisTransfersToNearerCapturedNode()
    {
        // a different node inside the capture ring and strictly closer than the held
        // one takes over — no dead zone between adjacent junctions.
        var (n, snap) = Setup();
        Net.Commit(n, Net.Straight(new(100, 0, 6), new(200, 0, 6)));
        var held = n.Nodes.Values.Single(x => x.Position == new Vector3(100, 0, 0)).Id;
        var other = n.Nodes.Values.Single(x => x.Position == new Vector3(100, 0, 6)).Id;
        var ctx = SnapContext.Empty with { HeldNode = held };
        var result = snap.Resolve(new Vector3(100f, 0, 4f), 6f, SnapTypes.All, ctx);
        Assert.Equal(SnapKind.Node, result.Kind);
        Assert.Equal(other, result.Node);
    }

    [Fact]
    public void StaleHeldNodeIsIgnored()
    {
        // held node bulldozed mid-gesture: the id no longer resolves — no crash,
        // normal resolution.
        var (_, snap) = Setup();
        var ctx = SnapContext.Empty with { HeldNode = new NodeId(9999) };
        var result = snap.Resolve(new Vector3(50f, 0, 3f), 5f, SnapTypes.All, ctx);
        Assert.Equal(SnapKind.Edge, result.Kind);
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

    [Theory]
    [InlineData(40f)]
    [InlineData(400f)]
    public void AngleSnapIsExactAndLengthIndependent(float length)
    {
        // CS2 accepts within a constant *lateral* band, so its angular window shrinks
        // with length and long roads commit 179.6°. Ours is angular: a ~6°-off cursor
        // snaps to the exact 15° ray at ANY length, and the snapped direction is
        // exactly on the ray (cross product ~0).
        var (_, snap) = Setup();
        var anchor = new Vector3(100, 0, 0);
        var ctx = new SnapContext(anchor, new Vector3(1, 0, 0));
        float offRad = 51f * MathF.PI / 180f; // 6° off the 45° ray
        var raw = anchor + length * new Vector3(MathF.Cos(offRad), 0, MathF.Sin(offRad));
        var result = snap.Resolve(raw, 2f, SnapTypes.Angle, ctx);
        Assert.Equal(SnapKind.Angle, result.Kind);
        Assert.Equal(45f, result.SnappedAngleDeg!.Value, 3);
        var dir = Vector3.Normalize(result.Position - anchor);
        var exact = new Vector3(MathF.Cos(MathF.PI / 4), 0, MathF.Sin(MathF.PI / 4));
        float cross = MathF.Abs(dir.X * exact.Z - dir.Z * exact.X);
        Assert.True(cross < 1e-5f, $"direction off the exact ray by cross={cross}");
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

    [Fact]
    public void CellLengthQuantizesFreeDrag()
    {
        // anchor set, empty surroundings: 27.3 m raw drag ratchets to the 24 m tick
        var (_, snap) = Setup();
        var anchor = new Vector3(300, 0, 300);
        var ctx = new SnapContext(anchor, null);
        var result = snap.Resolve(new Vector3(327.3f, 0, 300f), 6f, SnapTypes.CellLength, ctx);
        Assert.Equal(SnapKind.CellLength, result.Kind);
        Assert.Equal(324f, result.Position.X, 3);
        Assert.Equal(300f, result.Position.Z, 3);
    }

    [Fact]
    public void CellLengthComposesWithAngleSnap()
    {
        // angle fallback fires (46°→45°) AND the length lands on an 8 m tick (27.3→24):
        // the CS2 rhythm — clean diagonals on both the ray and the tick.
        var (_, snap) = Setup();
        var anchor = new Vector3(300, 0, 300);
        var ctx = new SnapContext(anchor, new Vector3(1, 0, 0));
        float rad = 46f * MathF.PI / 180f;
        var raw = anchor + 27.3f * new Vector3(MathF.Cos(rad), 0, MathF.Sin(rad));
        var result = snap.Resolve(raw, 2f, SnapTypes.Angle | SnapTypes.CellLength, ctx);
        Assert.Equal(SnapKind.Angle, result.Kind);
        Assert.Equal(45f, result.SnappedAngleDeg!.Value, 3);
        Assert.Equal(24f, Vector3.Distance(result.Position, anchor), 3);
    }

    [Fact]
    public void CellLengthNeedsAnchor()
    {
        var (_, snap) = Setup();
        var result = snap.Resolve(new Vector3(327.3f, 0, 300f), 6f, SnapTypes.CellLength, SnapContext.Empty);
        Assert.Equal(SnapKind.Free, result.Kind);
    }

    [Fact]
    public void GridPointSnapsToNearestIntersection()
    {
        var (_, snap) = Setup();
        var ctx = SnapContext.Empty with { Grid = new GridConfig(8f) };
        var result = snap.Resolve(new Vector3(302.2f, 0, 297.9f), 5f, SnapTypes.Grid, ctx);
        Assert.Equal(SnapKind.GridPoint, result.Kind);
        Assert.Equal(new Vector3(304, 0, 296), result.Position);
    }

    [Fact]
    public void GridLineWinsWhenIntersectionIsFar()
    {
        var (_, snap) = Setup();
        var ctx = SnapContext.Empty with { Grid = new GridConfig(8f) };
        // 0.4 m off the x=304 line but 3.9 m from the nearest intersection:
        // line score 0.4/1.0 = 0.4 < point score 3.92/1.2 ≈ 3.3
        var result = snap.Resolve(new Vector3(304.4f, 0, 300f), 5f, SnapTypes.Grid, ctx);
        Assert.Equal(SnapKind.GridLine, result.Kind);
        Assert.Equal(304f, result.Position.X, 3);
        Assert.Equal(300f, result.Position.Z, 3);
    }

    [Fact]
    public void NodeStillBeatsGridWhenBothClose()
    {
        var (n, snap) = Setup();
        var ctx = SnapContext.Empty with { Grid = new GridConfig(8f) };
        // cursor past the road end so the edge can't shadow the node:
        // node (100,0,0) 1.80 m → score .45; guideline proj 1.0 m → .67;
        // grid point (104,0,0) 2.69 m → 2.24; grid lines ≥ 1.0 → node wins
        var result = snap.Resolve(new Vector3(101.5f, 0, 1.0f), 5f, SnapTypes.All, ctx);
        Assert.Equal(SnapKind.Node, result.Kind);
    }

    [Fact]
    public void GridIgnoredWithoutConfig()
    {
        var (_, snap) = Setup();
        var result = snap.Resolve(new Vector3(302.2f, 0, 297.9f), 5f, SnapTypes.Grid, SnapContext.Empty);
        Assert.Equal(SnapKind.Free, result.Kind);
    }

    [Fact]
    public void PerpendicularFootSnapsToExact90Degrees()
    {
        var (n, snap) = Setup(); // edge (0,0,0)→(100,0,0)
        var anchor = new Vector3(40, 0, 60);
        // cursor near the edge but 3 m off the true foot (40, 0, 0)
        var ctx = new SnapContext(anchor, null);
        var result = snap.Resolve(new Vector3(43, 0, 0.5f), 5f, SnapTypes.Perpendicular, ctx);
        Assert.Equal(SnapKind.Perpendicular, result.Kind);
        Assert.True(Vector3.Distance(result.Position, new Vector3(40, 0, 0)) < 0.05f,
            $"foot at {result.Position}");
        Assert.NotNull(result.DirectionConstraint);
        // arrival direction points from anchor to foot: (0, 0, -1)
        Assert.Equal(-1f, result.DirectionConstraint!.Value.Z, 2);
    }

    [Fact]
    public void PerpendicularNeedsAnchor()
    {
        var (_, snap) = Setup();
        var result = snap.Resolve(new Vector3(43, 0, 0.5f), 5f, SnapTypes.Perpendicular, SnapContext.Empty);
        Assert.Equal(SnapKind.Free, result.Kind);
    }

    [Fact]
    public void ParallelGuideSitsCurbToCurb()
    {
        var (_, snap) = Setup(); // TwoLane (width 8, OuterHalf 4) along z=0
        var ctx = SnapContext.Empty with { DrawingType = RoadCatalog.TwoLane.Id };
        // expected guide at z = ±(4 + 4) = ±8; cursor near z=7.4 above mid-edge
        var result = snap.Resolve(new Vector3(50, 0, 7.4f), 3f, SnapTypes.Parallel | SnapTypes.Guidelines, ctx);
        Assert.Equal(SnapKind.Guideline, result.Kind);
        Assert.Equal(8f, result.Position.Z, 2);
    }

    [Fact]
    public void CurvedEdgeSpawnsNoParallelGuide()
    {
        var n = Net.New();
        var bend = Bezier3.FromQuadratic(new(0, 0, 0), new(50, 0, 40), new(100, 0, 0));
        Net.Commit(n, new PlacementProposal(
            new[] { new ProposedCurve(bend, EndpointBinding.None, EndpointBinding.None) },
            RoadCatalog.TwoLane.Id));
        var snap = new SnapEngine(n);
        var ctx = SnapContext.Empty with { DrawingType = RoadCatalog.TwoLane.Id };
        var result = snap.Resolve(new Vector3(50, 0, 28f), 3f, SnapTypes.Parallel | SnapTypes.Guidelines, ctx);
        Assert.NotEqual(SnapKind.Guideline, result.Kind);
    }
}
