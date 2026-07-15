using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using CityBuilder.Domain.Traffic;
using Xunit;

namespace CityBuilder.Domain.Tests.Fuzzing;

/// <summary>Seed-pinned regressions harvested from <see cref="GestureFuzzer"/> findings.
/// Every fact here corresponds to a real bug the fuzzer found, minimized to the
/// smallest (seed, action count) that reproduces it, fixed at the root cause in domain
/// code, and pinned permanently — so the exact scenario that once broke
/// <see cref="CityBuilder.Domain.Network.NetworkInvariants"/> stays covered even if the
/// general fuzz sweep's seeds or default action count later change. Each fact carries a
/// comment naming the root cause it guards against.</summary>
public class FuzzRegressionTests
{
    /// <summary>Root cause: a Chain-mode gesture split an existing edge at a point
    /// where the new curve's start tangent landed within
    /// <see cref="CityBuilder.Domain.Network.RoadNetwork.TangentContinuationDeg"/> of
    /// the split edge's own tangent — a legitimate G1 ramp-exit that
    /// <c>RoadNetwork.Validate</c> already allows and commits (see the passing,
    /// pre-existing <c>PlacementTests.TangentialDepartureFromMidEdgeIsValidAndCommits</c>).
    /// <see cref="CityBuilder.Domain.Network.NetworkInvariants.CheckLegAngles"/> didn't
    /// know about that exemption and flagged the resulting 0 deg leg pair as
    /// <c>SharpAngle</c>-violating, even though the network was built exactly per
    /// contract. Fixed by mirroring the exemption: pairs within
    /// <c>TangentContinuationDeg</c> of each other are skipped, since that's the only
    /// way a validly committed network can ever have two legs that close together.</summary>
    [Fact]
    public void Seed202RampContinuationIsNotAFalseSharpAngle()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(202, 5));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>Root cause: the same tangent-continuation ramp (above) can land a
    /// SECOND arm within <c>ConnectorBuilder.Classify</c>'s +-30 deg "Straight" window
    /// for one approach, alongside the arm it was already straight-through with (here:
    /// the ramp's own arm has less receiving capacity than the "real" through-arm).
    /// <c>ConnectorBuilder.Build</c>'s capacity-aware straight-block sizing sized each
    /// (source, target) pair independently against the FULL incoming lane count, so
    /// the smaller-capacity ramp target still claimed every source lane on top of the
    /// through-arm already covering them — double-booking lanes and tripping
    /// <see cref="CityBuilder.Domain.Network.NetworkInvariants.CheckStraightCapacity"/>.
    /// Fixed by capping each simultaneous Straight target to its OWN receiving
    /// capacity (a lane can still be eligible for several targets at once — that's a
    /// legitimate fork/wye, see <c>RoutePlannerTests.ControlDelaySwaysRouteChoice</c> —
    /// but no single target is ever handed more lanes than it can receive).</summary>
    [Fact]
    public void Seed202DualStraightTargetsDoNotDoubleBookLanes()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(202, 8));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    // ------------------------------------------------------------------------
    // Spec amendment 2026-07-16 (commit ceb1887, CS2-style, user-decided):
    // direction-asymmetric road types can create arriving driving lanes with
    // categorically zero destinations; such placements commit, the stranded lane is
    // a LEGAL state routing never uses, and stranding a lane when receiving capacity
    // DOES exist on another edge remains a hard violation (never-strand fallback in
    // ConnectorBuilder). The three default fuzz seeds all hit the pre-amendment
    // false positive at these action counts; pinned so the amendment semantics
    // (CheckLaneCoverage iff-rule + never-strand fallback) stay covered.
    // ------------------------------------------------------------------------

    /// <summary>Pre-amendment failure: "lane on edge arrives with no outgoing
    /// connector" after bulldozing arms off a mixed OneWay junction.</summary>
    [Fact]
    public void Seed202StrandedLanesAfterBulldozeAreLegal()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(202, 28));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>Pre-amendment failure: same class via a OneWay/Asymmetric mix drawn
    /// directly (no bulldozing required).</summary>
    [Fact]
    public void Seed101StrandedLanesFromDirectionAsymmetricTypesAreLegal()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(101, 48));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>Pre-amendment failure at a live 3-way junction (two OneWays + a
    /// FourLane): the FourLane's second arriving lane was stranded by rank rules even
    /// though a OneWay offered departing capacity — now covered by the never-strand
    /// fallback, while truly destination-less lanes at the same node stay legal.</summary>
    [Fact]
    public void Seed303NeverStrandFallbackCoversRestrictedLanes()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(303, 143));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>The minimal direct-draw shape of the amendment (from the Task 4
    /// BLOCKED report): a OneWay ending where an Asymmetric continues. The
    /// Asymmetric's arriving backward lane has categorically zero destinations
    /// (the OneWay offers no departing driving lane) — legally stranded, zero
    /// violations — and the network must stay DRIVABLE for ambient traffic: the
    /// spawner/routing operate on the connector graph, so the stranded lane is
    /// simply never used.</summary>
    [Fact]
    public void OneWayIntoAsymmetricContinuationIsLegalAndDrivable()
    {
        var n = new RoadNetwork();
        Commit(n, Line(new Vector3(0, 0, 0), new Vector3(100, 0, 0), RoadCatalog.OneWay.Id));
        Commit(n, Line(new Vector3(100, 0, 0), new Vector3(200, 0, 0), RoadCatalog.Asymmetric.Id));

        Assert.Empty(NetworkInvariants.Check(n));
        Assert.Empty(SimInvariants.CheckBurst(n, seed: 42));

        // the stranded backward lane really is stranded (no connector from it) —
        // this guards the iff: legal absence, not accidental coverage
        var mid = n.Nodes.Values.Single(x => Vector3.Distance(x.Position, new Vector3(100, 0, 0)) < 1f);
        var asym = n.Edges.Values.Single(e => e.Type == RoadCatalog.Asymmetric.Id);
        var strandedLane = asym.Lanes.Single(l =>
            l.Kind == LaneKind.Driving && l.Direction == LaneDirection.Backward);
        Assert.DoesNotContain(mid.Connectors, c => c.From == strandedLane.Id);
    }

    private static void Commit(RoadNetwork n, PlacementProposal p)
    {
        var v = n.Validate(p);
        Assert.True(v.IsValid, "expected valid placement, errors: " + string.Join(",", v.Errors));
        Assert.True(n.Commit(v).Success);
    }

    private static PlacementProposal Line(Vector3 a, Vector3 b, RoadTypeId type)
        => new(new[] { new ProposedCurve(Bezier3.Line(a, b), EndpointBinding.None, EndpointBinding.None) }, type);
}
