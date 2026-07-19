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

    /// <summary>Root cause: at a 3-way where a FourLane approach's only non-U-turn
    /// outlet is a single-receiving-lane arm (the third arm doubles back, classifying
    /// UTurn — no left/right alternative anywhere), ConnectorBuilder's documented
    /// merge-straight fallback correctly sends both lanes into the one receiving lane
    /// ("lanes with neither alternative keep a merge-straight rather than going
    /// dead"), but CheckStraightCapacity flagged the merge as surplus. The checker
    /// now mirrors the builder's own drop logic: surplus straight sources are only a
    /// violation when a left/right alternative with receiving capacity existed to
    /// shed them into (allowed = max(capacity, approach lanes − left − right)) — the
    /// M5 arrow-bug guard is unchanged wherever alternatives exist.</summary>
    [Fact]
    public void Seed202MergeStraightWithoutAlternativeIsLegal()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(202, 155));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>Root cause: commit-time stop relocation. Validation approves crossing
    /// spacing using exact intersection points, but commit may substitute a reused
    /// node — by up to NodeReuseRadius against edges validation saw, and by up to the
    /// split edge's own MinSegmentLength against SAME-BATCH siblings validation never
    /// saw (one grid-stamp line splitting another line of the same proposal that
    /// existing roads had already subdivided). The old SubCurve pinned only P0/P3 to
    /// the relocated nodes, so a straight grid line came out kinked (observed radius
    /// 1.6 m on a FourLane, min 35) and/or below the length floor (observed 15.7 m
    /// and 10.0 m vs 15.9). Fixed twofold: SubCurve blends endpoint displacement
    /// linearly across the whole control net (straight stays straight), and
    /// CommitCurve refuses to build segments below the type's length/radius floors
    /// (honoring RoadNetwork's documented "no sliver edges" contract), pruning any
    /// nodes left edgeless by the drop.</summary>
    [Fact]
    public void Seed101GridStampAbsorptionKeepsCommittedGeometryClean()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(101, 65));
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

    // ------------------------------------------------------------------------
    // 10k-action certification sweep (2026-07-16, task 6): three new false
    // negatives in RoadNetwork.Validate's sharp-angle gate, all sharing one root
    // cause, plus a fixed-resolution sampling bug in BezierOps.MinRadius. Neither
    // was reachable at the 300-action default; all four needed the 10k sweep.
    // ------------------------------------------------------------------------

    /// <summary>Root cause: RoadNetwork.SplitEdgeWithReuse snaps a crossing/endpoint
    /// straight to an existing edge's end node whenever the split point lands within
    /// that edge's OWN MinSegmentLength of the end (up to 16 m here, a FourLane) —
    /// correct, and far more generous than the tight 0.5 m NodeReuseRadius. But
    /// Validate's sharp-angle gate (HasSharpLeg/ExistingLegDirections, for both an
    /// explicit OnEdge binding and the Free-endpoint edge fallback) checked the
    /// crossed edge's tangent at the ORIGINAL requested parameter, not at the node
    /// Commit was actually about to reuse. A FourLane's 35 m MinRadius floor lets its
    /// tangent swing over 26 deg across a 16 m MinSegmentLength stretch, so the two
    /// tangents can legitimately differ by more than MinJunctionAngleDeg: Validate
    /// approved a leg that, once snapped to the real node, was sharp against a leg
    /// already there (edge887, 13.97 deg from the new edge here) — a real
    /// Validate/Commit divergence, not NetworkInvariants over-strictness. Fixed by
    /// re-checking sharp-angle in CommitCurve itself, against the live network, at
    /// the exact node/tangent each segment actually attaches to (RoadNetwork's new
    /// HasSharpLegAtNode helper) — dropping the segment rather than committing
    /// corrupt geometry, same policy CommitCurve already applies to the
    /// length/radius floors just above it.</summary>
    [Fact]
    public void Seed101NodeReuseAbsorptionCantSkipTheSharpAngleGate()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(101, 3091));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>Same root cause as <see cref="Seed101NodeReuseAbsorptionCantSkipTheSharpAngleGate"/>,
    /// reached via the OTHER half of the gap it fixes: a multi-curve GridStamp
    /// proposal commits its curves in a sequential per-curve loop, each one seeing
    /// edges/nodes the EARLIER curves of the SAME proposal already created — state
    /// Validate's single pre-batch snapshot never saw at all, so a later grid line
    /// attaching to a node an earlier sibling just created was never checked against
    /// that sibling's leg (here: two arms 20.2 deg apart at a brand-new 5-leg node).
    /// Covered by the same live-network recheck in CommitCurve.</summary>
    [Fact]
    public void Seed202GridStampSiblingCurvesShareTheSharpAngleGate()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(202, 3298));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>Third instance of the same root cause (see
    /// <see cref="Seed101NodeReuseAbsorptionCantSkipTheSharpAngleGate"/>): an 8.2 deg
    /// pair at a 5-leg node produced by the identical reuse-absorption gap.</summary>
    [Fact]
    public void Seed303NodeReuseAbsorptionCantSkipTheSharpAngleGate()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(303, 3751));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>Root cause: BezierOps.MinRadius sampled a fixed 32 points across the
    /// WHOLE curve regardless of its length — fine for the short segments most
    /// drawing gestures produce, but a long edge repeatedly cut down by
    /// SplitEdgeWithReuse (here: an OneWay edge whittled from roughly 500 m to under
    /// 400 m over three successive splits, each one a legal reuse-absorption, never
    /// changing the underlying curve's shape) spaces those 32 samples many metres
    /// apart. A short, sharp bend can sit entirely between two samples: the ORIGINAL
    /// long edge measured a safe-looking radius (10.29 m, above the 10 m OneWay
    /// floor) at commit time, and each subsequent split re-sampled the SAME
    /// unchanged geometry more densely over a shorter range, eventually landing a
    /// sample on the bend and reporting its true (sub-floor) radius — 9.86 m — for
    /// geometry that was never re-shaped, only re-measured more precisely. Fixed by
    /// making MinRadius's sample count scale with the curve's own arc length (one
    /// sample per ~8 m, floor at the old fixed 32) when no explicit count is given,
    /// so long and short curves get the same resolution instead of the same sample
    /// budget.</summary>
    [Fact]
    public void Seed303LongEdgeSplitRevealsTrueMinRadiusRegardlessOfSampleSpacing()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(303, 6874));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>M7.5-hardening finds (both seeds, same class): in a dense network,
    /// SubCurve's displacement blending toward a reuse-absorbed stop dragged a committed
    /// segment far enough sideways that it RE-crossed the very edge whose crossing had
    /// just been absorbed into a node — an off-node crossing no later pass would split,
    /// invisible until the no-crossing invariant existed (cars drove straight through).
    /// Fixed by the third member of the commit-side recheck family: after floors and
    /// sharp-leg rechecks, a candidate segment that genuinely crosses any live edge away
    /// from its own endpoints is dropped rather than committed corrupt
    /// (RoadNetwork.SegmentCrossesLiveEdgeOffNode).</summary>
    [Fact]
    public void Seed101AbsorptionDisplacedSegmentNeverCommitsOffNodeCrossing()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(101, 8350));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>Second seed of the class above (edges 2664/2581 at action 8673).</summary>
    [Fact]
    public void Seed202AbsorptionDisplacedSegmentNeverCommitsOffNodeCrossing()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(202, 8700));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
    }

    /// <summary>M8 find: the commit-side floor guard rechecked length and radius after
    /// reuse-absorption relocation but not GRADIENT — a stop relocated onto a node at a
    /// different Y drags the displacement-blended segment steep (10.2% on an 8% type at
    /// seed 303@241, within minutes of elevation entering the fuzz alphabet). Gradient
    /// joined the commit-side floor family: recheck live, drop rather than commit.</summary>
    [Fact]
    public void Seed303RelocatedStopNeverCommitsOverGradientSegment()
    {
        var result = GestureFuzzer.Run(new FuzzOptions(303, 300));
        Assert.True(result.Ok, result.Failure + "\n" + string.Join("\n", result.ActionTail));
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
