using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class RoundaboutPlannerTests
{
    // Four straight legs N/E/S/W into origin, each 60 m long, ending at center.
    private static IReadOnlyList<ApproachLeg> FourWay()
    {
        var dirs = new[] { new Vector3(60,0,0), new Vector3(0,0,60), new Vector3(-60,0,0), new Vector3(0,0,-60) };
        var legs = new List<ApproachLeg>();
        int id = 10;
        foreach (var d in dirs)
            legs.Add(new ApproachLeg(new EdgeId(id++), Bezier3.Line(d, Vector3.Zero), true, RoadCatalog.TwoLane.Id));
        return legs;
    }

    [Fact]
    public void FourWayProducesFourSlotsInCcwOrder()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, FourWay());
        Assert.Null(plan.Error);
        Assert.Equal(4, plan.Slots.Count);
        for (int i = 0; i + 1 < plan.Slots.Count; i++)
            Assert.True(plan.Slots[i].Bearing < plan.Slots[i + 1].Bearing);
    }

    [Fact]
    public void SlotsSitOnTheCircle()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, FourWay());
        foreach (var s in plan.Slots)
            Assert.Equal(20f, Vector3.Distance(s.Position, Vector3.Zero), 2);
    }

    [Fact]
    public void RingArcsLieOnTheCircle()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, FourWay());
        foreach (var chain in plan.RingArcs)
        foreach (var arc in chain)
            for (float t = 0; t <= 1f; t += 0.25f)
                Assert.Equal(20f, Vector3.Distance(arc.Point(t), Vector3.Zero), 1);
    }

    [Fact]
    public void RingArcChainsAreContiguousAndCcw()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, FourWay());
        Assert.Equal(plan.Slots.Count, plan.RingArcs.Count);
        for (int i = 0; i < plan.RingArcs.Count; i++)
        {
            var chain = plan.RingArcs[i];
            // chain starts at slot i, ends at slot (i+1)%n
            Assert.Equal(0f, Vector3.Distance(chain[0].P0, plan.Slots[i].Position), 1);
            var end = plan.Slots[(i + 1) % plan.Slots.Count].Position;
            Assert.Equal(0f, Vector3.Distance(chain[^1].P3, end), 1);
            // consecutive cubics in the chain share endpoints
            for (int k = 0; k + 1 < chain.Count; k++)
                Assert.Equal(0f, Vector3.Distance(chain[k].P3, chain[k + 1].P0), 1);
        }
    }

    [Fact]
    public void TrimmedLegsEndOnTheCircleNotCenter()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, FourWay());
        foreach (var s in plan.Slots)
        {
            var inner = s.TrimmedLegEndsAtCenter ? s.TrimmedLeg.Point(1) : s.TrimmedLeg.Point(0);
            Assert.Equal(20f, Vector3.Distance(inner, Vector3.Zero), 1);
        }
    }

    [Fact]
    public void RadiusBelowFeasibleFails()
    {
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 1f, FourWay());
        Assert.Equal(RoundaboutError.RadiusTooTight, plan.Error);
    }

    [Fact]
    public void CoincidentBearingsFail()
    {
        var legs = new[]
        {
            new ApproachLeg(new EdgeId(1), Bezier3.Line(new(60,0,0), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
            new ApproachLeg(new EdgeId(2), Bezier3.Line(new(61,0,0.2f), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
            new ApproachLeg(new EdgeId(3), Bezier3.Line(new(0,0,60), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
        };
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, legs);
        Assert.Equal(RoundaboutError.DegenerateBearings, plan.Error);
    }

    [Fact]
    public void ThreeLegTeeSucceeds()
    {
        var legs = new[]
        {
            new ApproachLeg(new EdgeId(1), Bezier3.Line(new(60,0,0), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
            new ApproachLeg(new EdgeId(2), Bezier3.Line(new(-60,0,0), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
            new ApproachLeg(new EdgeId(3), Bezier3.Line(new(0,0,60), Vector3.Zero), true, RoadCatalog.TwoLane.Id),
        };
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, legs);
        Assert.Null(plan.Error);
        Assert.Equal(3, plan.Slots.Count);
        // the 180° gap (between +X and -X passing the south side) splits into 2 sub-arcs
        Assert.Contains(plan.RingArcs, chain => chain.Count == 2);
    }

    [Fact]
    public void HookLegWithMultipleRadiusCrossingsNeverTrimsInsideTheRing()
    {
        // A committable hook (MinRadius 10.3 ≥ Street's floor, no self-intersection)
        // whose distance-to-center crosses r=21.15 three times. A bisection over the
        // whole span converges to the INNERMOST crossing (measured: tCut≈0.914, trimmed
        // leg dipping to 6.4 m inside the ring). The contract: a successful plan's
        // trimmed legs never re-enter the circle — cut at the outermost crossing or
        // refuse the conversion, never emit ring-piercing geometry.
        const float r = 21.15f;
        var hook = new Bezier3(new(88.6f, 0, -39.0f), new(-79.8f, 0, 79.9f), new(-69.2f, 0, -68.6f), Vector3.Zero);
        var legs = new[]
        {
            new ApproachLeg(new EdgeId(1), hook, true, RoadCatalog.Street.Id),
            new ApproachLeg(new EdgeId(2), Bezier3.Line(RadialPoint(0f, 70f), Vector3.Zero), true, RoadCatalog.Street.Id),
            new ApproachLeg(new EdgeId(3), Bezier3.Line(RadialPoint(100f, 70f), Vector3.Zero), true, RoadCatalog.Street.Id),
        };
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, r, legs);
        if (plan.Error is not null)
            return; // refusing the conversion is a legal outcome; piercing geometry is not
        foreach (var s in plan.Slots)
            for (float t = 0; t <= 1.0001f; t += 1f / 64f)
                Assert.True(Vector3.Distance(s.TrimmedLeg.Point(t), Vector3.Zero) >= r - 0.6f,
                    $"trimmed leg dips inside the ring at t={t:F2} " +
                    $"(d={Vector3.Distance(s.TrimmedLeg.Point(t), Vector3.Zero):F1})");
    }

    private static Vector3 RadialPoint(float deg, float r)
    {
        float a = deg * MathF.PI / 180f;
        return new Vector3(r * MathF.Cos(a), 0, r * MathF.Sin(a));
    }

    [Fact]
    public void LegStartingAtCenterTrimsFromInnerEnd()
    {
        // leg oriented center -> outer (StartsAtCenter): EndsAtCenter = false
        var legs = new[]
        {
            new ApproachLeg(new EdgeId(1), Bezier3.Line(Vector3.Zero, new(60,0,0)), false, RoadCatalog.TwoLane.Id),
            new ApproachLeg(new EdgeId(2), Bezier3.Line(Vector3.Zero, new(-60,0,0)), false, RoadCatalog.TwoLane.Id),
            new ApproachLeg(new EdgeId(3), Bezier3.Line(Vector3.Zero, new(0,0,60)), false, RoadCatalog.TwoLane.Id),
        };
        var plan = RoundaboutPlanner.Plan(Vector3.Zero, 20f, legs);
        Assert.Null(plan.Error);
        foreach (var s in plan.Slots)
        {
            Assert.False(s.TrimmedLegEndsAtCenter);
            Assert.Equal(20f, Vector3.Distance(s.TrimmedLeg.Point(0), Vector3.Zero), 1); // inner end on circle
            Assert.Equal(60f, Vector3.Distance(s.TrimmedLeg.Point(1), Vector3.Zero), 1); // outer end unchanged
        }
    }
}
