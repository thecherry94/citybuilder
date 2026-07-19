using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class ElevationValidationTests
{
    internal static Bezier3 Ramp(Vector3 a, Vector3 b) => new(
        a, a + (b - a) / 3f, a + (b - a) * (2f / 3f), b); // linear XYZ interpolation

    internal static PlacementProposal One(Bezier3 c, RoadTypeId? type = null,
        EndpointBinding? start = null, EndpointBinding? end = null)
        => new(new[] { new ProposedCurve(c, start ?? EndpointBinding.None, end ?? EndpointBinding.None) },
            type ?? RoadCatalog.TwoLane.Id);

    [Fact]
    public void GentleRampIsValid()
    {
        var n = Net.New();
        var v = n.Validate(One(Ramp(new(0, 0, 0), new(100, 6, 0)))); // 6%
        Assert.True(v.IsValid, string.Join(",", v.Errors));
    }

    [Fact]
    public void SteepRampIsTooSteep()
    {
        var n = Net.New();
        var v = n.Validate(One(Ramp(new(0, 0, 0), new(100, 12, 0)))); // 12% > TwoLane 8%
        Assert.Contains(PlacementError.TooSteep, v.Errors);
    }

    [Fact]
    public void GradientLimitIsPerType()
    {
        var n = Net.New();
        // 9% is TooSteep for TwoLane (8%) but fine for Street (10%)
        Assert.Contains(PlacementError.TooSteep,
            n.Validate(One(Ramp(new(0, 0, 0), new(100, 9, 0)), RoadCatalog.TwoLane.Id)).Errors);
        Assert.True(n.Validate(One(Ramp(new(0, 0, 0), new(100, 9, 0)), RoadCatalog.Street.Id)).IsValid);
    }

    [Fact]
    public void BridgeOverRoadIsValidWithNoCrossingMarker()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        // level +6 m bridge crossing above the road
        var v = n.Validate(One(Ramp(new(0, 6, -80), new(0, 6, 80))));
        Assert.True(v.IsValid, string.Join(",", v.Errors));
        Assert.Empty(v.CrossingPoints); // grade-separated: not a crossing at all
    }

    [Fact]
    public void ClashBandIsRefused()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var v = n.Validate(One(Ramp(new(0, 2, -80), new(0, 2, 80)))); // 2 m over: clash
        Assert.Contains(PlacementError.VerticalClash, v.Errors);
    }

    [Fact]
    public void CoplanarCrossingStillJunctions()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var v = n.Validate(One(Ramp(new(0, 0.3f, -80), new(0, 0.3f, 80)))); // within 0.6 m
        Assert.True(v.IsValid, string.Join(",", v.Errors));
        Assert.Single(v.CrossingPoints);
    }

    [Fact]
    public void EndpointBindingMustMeetNodeElevation()
    {
        var n = Net.New();
        var r = Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        var node = n.Edges[r.CreatedEdges[0]].StartNode; // ground node at (-100,0,0)
        // a curve ending 8 m above that node cannot bind to it
        var v = n.Validate(One(Ramp(new(-100, 8, 80), new(-100, 8, 0)),
            end: new EndpointBinding.AtNode(node)));
        Assert.Contains(PlacementError.VerticalClash, v.Errors);
    }
}
