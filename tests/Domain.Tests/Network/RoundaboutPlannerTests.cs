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
