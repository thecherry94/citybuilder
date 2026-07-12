using System.Numerics;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Network;

public class CurveFitTests
{
    [Fact]
    public void TangentConstrainedFitRecoversExactCubicFromSplitSamples()
    {
        var original = new Bezier3(new(-100, 0, 0), new(-30, 0, 40), new(30, 0, 40), new(100, 0, 0));
        var (l, r) = original.Split(0.5f);

        var samples = new Vector3[65];
        float lenL = l.Length(), lenR = r.Length(), total = lenL + lenR;
        var tl = new ArcLengthTable(l);
        var tr = new ArcLengthTable(r);
        for (int i = 0; i <= 64; i++)
        {
            float d = total * i / 64;
            samples[i] = d <= lenL ? l.Point(tl.TAtDistance(d)) : r.Point(tr.TAtDistance(d - lenL));
        }

        var fit = CurveFit.FitCubic(samples, original.Tangent(0), -original.Tangent(1));
        for (int iter = 0; iter < 16; iter++)
        {
            var u = new float[65];
            for (int i = 0; i < 65; i++) u[i] = BezierOps.ClosestPoint(fit, samples[i]).t;
            u[0] = 0; u[^1] = 1;
            fit = CurveFit.FitCubic(samples, original.Tangent(0), -original.Tangent(1), u);
        }

        float maxErr = 0;
        foreach (var q in samples)
            maxErr = MathF.Max(maxErr, BezierOps.ClosestPoint(fit, q).dist);
        Assert.True(maxErr <= 0.01f, $"maxErr {maxErr}");
    }
}
