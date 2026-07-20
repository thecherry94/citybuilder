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
    public void SteppingBetweenClicksBuildsAnInclinedRoad()
    {
        // user find (2026-07-19): elevation applied at proposal-build time lifted BOTH
        // free endpoints to the current value — the elevation at each endpoint's CLICK
        // must stick to that endpoint, or inclined roads can't be drawn at all
        var (n, s) = NewSession();
        s.CurrentElevation = 0f;
        s.Click(new Vector3(0, 0, 0), 6f);     // start placed at ground
        s.CurrentElevation = 8f;               // PgUp x N mid-draft
        s.Click(new Vector3(120, 0, 0), 6f);   // end placed at +8
        Assert.Single(n.Edges);
        var e = n.Edges.Values.Single();
        float yStart = MathF.Min(e.Curve.P0.Y, e.Curve.P3.Y);
        float yEnd = MathF.Max(e.Curve.P0.Y, e.Curve.P3.Y);
        Assert.Equal(0f, yStart, 1);
        Assert.Equal(8f, yEnd, 1);
    }

    [Fact]
    public void ElevationSetterClampsToEditorRange()
    {
        var (_, s) = NewSession();
        s.CurrentElevation = 999f;
        Assert.Equal(GeoConstants.MaxElevation, s.CurrentElevation);
        s.CurrentElevation = -999f;
        Assert.Equal(-GeoConstants.MaxDepth, s.CurrentElevation); // M8.5: signed range
        s.CurrentElevation = -12f;
        Assert.Equal(-12f, s.CurrentElevation);
    }

    [Fact]
    public void BelowGroundDraftCommitsANegativeDeck()
    {
        // the M8.5 unlock end-to-end: a −8 m draft must land a −8 m edge
        var (n, s) = NewSession();
        s.CurrentElevation = -8f;
        s.Click(new Vector3(0, 0, 0), 6f);
        s.Click(new Vector3(120, 0, 0), 6f);
        var e = Assert.Single(n.Edges.Values);
        Assert.Equal(-8f, e.Curve.P0.Y, 1);
        Assert.Equal(-8f, e.Curve.P3.Y, 1);
    }
}
