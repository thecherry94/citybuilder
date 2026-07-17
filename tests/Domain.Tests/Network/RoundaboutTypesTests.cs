using System.Numerics;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class RoundaboutTypesTests
{
    [Fact]
    public void ResultHelpersCarryOutcome()
    {
        var ok = RoundaboutResult.Ok(new RoundaboutId(3));
        Assert.True(ok.Success);
        Assert.Equal(new RoundaboutId(3), ok.Id);
        Assert.Null(ok.Error);

        var bad = RoundaboutResult.Failed(RoundaboutError.RadiusTooTight);
        Assert.False(bad.Success);
        Assert.Null(bad.Id);
        Assert.Equal(RoundaboutError.RadiusTooTight, bad.Error);
    }

    [Fact]
    public void RoundaboutRecordHoldsRingMembership()
    {
        var rb = new Roundabout(new RoundaboutId(1), Vector3.Zero, 20f,
            new[] { new NodeId(2), new NodeId(3), new NodeId(4) },
            new[] { new EdgeId(5), new EdgeId(6), new EdgeId(7) },
            new Dictionary<EdgeId, CityBuilder.Domain.Geometry.Bezier3>());
        Assert.Equal(3, rb.RingNodes.Count);
        Assert.Equal(20f, rb.Radius);
    }
}
