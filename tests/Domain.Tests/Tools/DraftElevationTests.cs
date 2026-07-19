using System.Numerics;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tests.Network;
using CityBuilder.Domain.Tools;
using Xunit;

namespace CityBuilder.Domain.Tests.Tools;

public class DraftElevationTests
{
    private static (RoadNetwork n, DraftSession s) NewSession()
    {
        var n = Net.New();
        var s = new DraftSession(n, new SnapEngine(n));
        s.SetMode(DraftMode.Straight);
        return (n, s);
    }

    [Fact]
    public void FreeEndpointsTakeTheCurrentElevation()
    {
        var (n, s) = NewSession();
        s.CurrentElevation = 10f;
        s.Click(new Vector3(0, 0, 0), 6f);   // clicks arrive as ground picks (y=0)
        s.Click(new Vector3(100, 0, 0), 6f);
        Assert.Single(n.Edges);
        var e = n.Edges.Values.Single();
        Assert.Equal(10f, e.Curve.P0.Y, 1);
        Assert.Equal(10f, e.Curve.P3.Y, 1);
    }

    [Fact]
    public void SnappedEndpointAdoptsTargetElevationAndRampsToCurrent()
    {
        var (n, s) = NewSession();
        s.CurrentElevation = 8f;
        // pre-existing ground road; start the new draft snapped to its end node
        Net.Commit(n, Net.Straight(new Vector3(-100, 0, 0), new Vector3(0, 0, 0)));
        s.Click(new Vector3(0, 0, 0), 6f);      // snaps to the ground node → Y 0
        s.Click(new Vector3(120, 0, 0), 6f);    // free → Y 8 (6.7% on TwoLane, legal)
        Assert.Equal(2, n.Edges.Count);
        var ramp = n.Edges.Values.First(e => e.Curve.Point(1).Y > 4f || e.Curve.Point(0).Y > 4f);
        float y0 = MathF.Min(ramp.Curve.P0.Y, ramp.Curve.P3.Y);
        float y1 = MathF.Max(ramp.Curve.P0.Y, ramp.Curve.P3.Y);
        Assert.Equal(0f, y0, 1);
        Assert.Equal(8f, y1, 1);
        Assert.Empty(NetworkInvariants.Check(n));
    }

    [Fact]
    public void ElevationSetterClampsToEditorRange()
    {
        var (_, s) = NewSession();
        s.CurrentElevation = 999f;
        Assert.Equal(GeoConstants.MaxElevation, s.CurrentElevation);
        s.CurrentElevation = -5f;
        Assert.Equal(0f, s.CurrentElevation);
    }
}
