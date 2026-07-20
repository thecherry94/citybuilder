using System.Numerics;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Persistence;
using CityBuilder.Domain.Tools;
using CityBuilder.Domain.Traffic;

namespace CityBuilder.Domain.Tests.Fuzzing;

public sealed record FuzzOptions(int Seed, int Actions, int BurstEvery = 25, int RoundTripEvery = 10);

public sealed class FuzzResult
{
    public bool Ok;
    public int FailedAtAction = -1;     // -1 when Ok
    public string Failure = "";         // first violation or exception
    public IReadOnlyList<string> ActionTail = Array.Empty<string>(); // last <= 10 actions, replayable text
}

/// <summary>Seeded driver over the REAL editor surface: a fresh RoadNetwork +
/// SnapEngine + DraftSession, driven exactly the way <c>ToolController</c> drives them
/// (Click/StepBack/Cancel/RoadType/EnabledSnaps), plus RoadNetwork.RemoveEdge and
/// ConfigureJunction for the non-drawing tools. After every action,
/// <see cref="NetworkInvariants"/> is checked against the live network; any violation
/// or exception stops the run and is reported with a replayable tail of the last
/// actions so a failure can be reproduced and minimized by seed + action count alone.</summary>
public static class GestureFuzzer
{
    // A representative fixed pick/snap radius (CameraRig.SnapRadius() is clamped to
    // [1, 20]; mid-zoom sits around 6). Kept constant rather than randomized so a
    // failing seed's snapping behavior is reproducible from the seed alone.
    private const float ClickRadius = 6f;
    private const float WorldHalfExtent = 400f;

    private static readonly DraftMode[] Modes =
    {
        DraftMode.Straight, DraftMode.QuadCurve, DraftMode.CubicCurve,
        DraftMode.Arc, DraftMode.Chain, DraftMode.GridStamp,
    };

    private static readonly JunctionControlMode[] ControlModes =
    {
        JunctionControlMode.Auto, JunctionControlMode.None, JunctionControlMode.PrioritySigns,
        JunctionControlMode.AllWayStop, JunctionControlMode.TrafficLights,
    };

    private static readonly LegRole[] LegRoles = { LegRole.Main, LegRole.Yield, LegRole.Stop };

    private static readonly SnapTypes[] SnapFlags =
    {
        SnapTypes.Nodes, SnapTypes.Edges, SnapTypes.Angle, SnapTypes.Guidelines,
        SnapTypes.Grid, SnapTypes.Parallel, SnapTypes.Perpendicular, SnapTypes.CellLength,
    };

    private static readonly int[] GridCells = { 4, 8, 16, 32 };

    /// <summary>Debug/triage hook: the network of the most recent <see cref="Run"/>, so a
    /// failing seed's terminal state can be inspected by a probe test without re-plumbing.</summary>
    public static RoadNetwork? LastNetwork;

    public static FuzzResult Run(FuzzOptions opts)
    {
        var network = new RoadNetwork();
        LastNetwork = network;
        var snap = new SnapEngine(network);
        var session = new DraftSession(network, snap);
        var undo = new UndoStack(network);
        var rng = new Random(opts.Seed);
        var tail = new List<string>();

        void Log(string s)
        {
            tail.Add(s);
            if (tail.Count > 10)
                tail.RemoveAt(0);
        }

        for (int i = 0; i < opts.Actions; i++)
        {
            try
            {
                int pick = rng.Next(100);
                if (pick < 42) { undo.Checkpoint(); DrawGesture(session, network, rng, Log); }
                else if (pick < 56) { undo.Checkpoint(); Bulldoze(network, rng, Log); }
                else if (pick < 65) { undo.Checkpoint(); ConfigureJunctionAction(network, rng, Log); }
                else if (pick < 72) { undo.Checkpoint(); Retype(network, rng, Log); }
                else if (pick < 77) { undo.Checkpoint(); Flip(network, rng, Log); }
                else if (pick < 82) { undo.Checkpoint(); ConvertRoundabout(network, rng, Log); }
                else if (pick < 85) { undo.Checkpoint(); AdjustRoundaboutRadius(network, rng, Log); }
                else if (pick < 87) { undo.Checkpoint(); RemoveRoundaboutAction(network, rng, Log); }
                else if (pick < 90) { undo.Checkpoint(); ToggleCovered(network, rng, Log); }
                else if (pick < 95) UndoRedo(undo, session, rng, Log);
                else if (pick < 98) ToggleSnap(session, rng, Log);
                else StepBackCancel(session, network, rng, Log);
            }
            catch (Exception ex)
            {
                return new FuzzResult
                {
                    Ok = false,
                    FailedAtAction = i,
                    Failure = ex.ToString(),
                    ActionTail = tail.ToArray(),
                };
            }

            var violations = NetworkInvariants.Check(network);
            if (violations.Count > 0)
            {
                return new FuzzResult
                {
                    Ok = false,
                    FailedAtAction = i,
                    Failure = violations[0],
                    ActionTail = tail.ToArray(),
                };
            }

            if (opts.BurstEvery > 0 && (i + 1) % opts.BurstEvery == 0)
            {
                int burstSeed = opts.Seed ^ i;
                IReadOnlyList<string> burstViolations;
                try
                {
                    burstViolations = SimInvariants.CheckBurst(network, seed: burstSeed, ticks: 180, population: 8);
                }
                catch (Exception ex)
                {
                    return new FuzzResult
                    {
                        Ok = false,
                        FailedAtAction = i,
                        Failure = $"burst: exception (seed {burstSeed}): {ex}",
                        ActionTail = tail.ToArray(),
                    };
                }

                if (burstViolations.Count > 0)
                {
                    return new FuzzResult
                    {
                        Ok = false,
                        FailedAtAction = i,
                        Failure = $"burst: {burstViolations[0]}",
                        ActionTail = tail.ToArray(),
                    };
                }
            }

            if (opts.RoundTripEvery > 0 && (i + 1) % opts.RoundTripEvery == 0)
            {
                string? roundTripFailure = null;
                try
                {
                    string saved1 = SaveLoad.Save(network);
                    var loaded = SaveLoad.Load(saved1);
                    string saved2 = SaveLoad.Save(loaded);

                    if (saved1 != saved2)
                        roundTripFailure = "roundtrip: Save(Load(Save(n))) is not byte-equal to Save(n)";
                    else if (loaded.Nodes.Count != network.Nodes.Count)
                        roundTripFailure = $"roundtrip: node count mismatch (expected {network.Nodes.Count}, got {loaded.Nodes.Count})";
                    else if (loaded.Edges.Count != network.Edges.Count)
                        roundTripFailure = $"roundtrip: edge count mismatch (expected {network.Edges.Count}, got {loaded.Edges.Count})";
                }
                catch (Exception ex)
                {
                    roundTripFailure = $"roundtrip: exception: {ex}";
                }

                if (roundTripFailure != null)
                {
                    return new FuzzResult
                    {
                        Ok = false,
                        FailedAtAction = i,
                        Failure = roundTripFailure,
                        ActionTail = tail.ToArray(),
                    };
                }
            }
        }

        return new FuzzResult { Ok = true, FailedAtAction = -1 };
    }

    // ------------------------------------------------------------------ actions

    private static void DrawGesture(DraftSession session, RoadNetwork network, Random rng, Action<string> log)
    {
        var mode = Modes[rng.Next(Modes.Length)];
        var type = RoadCatalog.All[rng.Next(RoadCatalog.All.Count)];
        session.SetMode(mode);
        session.RoadType = type.Id;
        // elevation (M8): mostly ground (70%), else a step multiple in [5, 50] — grade
        // separation, clash refusals, and steep-ramp refusals all get organic coverage
        // signed since M8.5: 70% ground, 15% elevated, 15% dug (±5..50 m). The
        // off-ground FREQUENCY matches the pre-M8.5 alphabet (which was 30% elevated)
        // so certification wall-clock stays tractable — only the sign is newly split,
        // which is what exercises trenches/tunnels without doubling the elevation load.
        session.CurrentElevation = rng.Next(100) < 70 ? 0f
            : (rng.Next(2) == 0 ? 5f : -5f) * rng.Next(1, 11);

        var points = new List<Vector3>();
        int clicks = rng.Next(2, 5); // 2..4 inclusive
        for (int c = 0; c < clicks; c++)
        {
            var p = RandomPoint(network, rng);
            points.Add(p);
            session.Click(p, ClickRadius);
        }

        if (mode == DraftMode.Chain)
        {
            // continuous mode: a real user keeps clicking to extend the chain; the
            // session auto-starts the next tangent-locked segment after each commit.
            if (rng.NextDouble() < 0.30)
            {
                int extra = rng.Next(1, 3); // 1..2
                for (int c = 0; c < extra; c++)
                {
                    var p = RandomPoint(network, rng);
                    points.Add(p);
                    session.Click(p, ClickRadius);
                }
            }
            session.Cancel(); // end the chain (also cleans up any dangling next-segment draft)
        }
        else if (session.State == SessionState.Adjustable)
        {
            // last click produced an invalid placement: expected outcome, not a failure.
            session.Cancel();
        }

        log($"draw {mode} type={type.Id.Value} elev={session.CurrentElevation:F0} clicks=" + FormatPoints(points));
    }

    private static readonly RoadTypeId[] AllTypes =
        RoadCatalog.All.Select(t => t.Id).ToArray();

    private static void Retype(RoadNetwork network, Random rng, Action<string> log)
    {
        var ids = network.Edges.Keys.OrderBy(e => e.Value).ToArray();
        if (ids.Length == 0) { log("retype skip=empty"); return; }
        var id = ids[rng.Next(ids.Length)];
        var type = AllTypes[rng.Next(AllTypes.Length)];
        var err = network.RetypeEdge(id, type);
        log($"retype edge={id.Value} type={type.Value} result={(err is null ? "ok" : err.ToString())}");
    }

    private static void ToggleCovered(RoadNetwork network, Random rng, Action<string> log)
    {
        if (network.Edges.Count == 0)
            return;
        var edge = network.Edges.Values.ElementAt(rng.Next(network.Edges.Count));
        bool ok = network.SetCovered(edge.Id, !edge.Covered);
        log($"cover edge={edge.Id.Value} now={!edge.Covered} ok={ok}");
    }

    private static void Flip(RoadNetwork network, Random rng, Action<string> log)
    {
        var ids = network.Edges.Keys.OrderBy(e => e.Value).ToArray();
        if (ids.Length == 0) { log("flip skip=empty"); return; }
        var id = ids[rng.Next(ids.Length)];
        network.FlipEdge(id);
        log($"flip edge={id.Value}");
    }

    private static void ConvertRoundabout(RoadNetwork network, Random rng, Action<string> log)
    {
        // a plain junction: degree >= 3, not already a ring node
        var candidates = network.Nodes.Values
            .Where(n => n.Ring == null && n.Edges.Count >= 3)
            .OrderBy(n => n.Id.Value)
            .ToArray();
        if (candidates.Length == 0) { log("convert skip=no-junction"); return; }
        var node = candidates[rng.Next(candidates.Length)];
        float radius = 12f + (float)rng.NextDouble() * 28f; // 12..40 m
        var res = network.ConvertToRoundabout(node.Id, radius);
        log($"convert node={node.Id.Value} r={radius:F1} result={(res.Success ? "ok" : res.Error.ToString())}");
    }

    private static void AdjustRoundaboutRadius(RoadNetwork network, Random rng, Action<string> log)
    {
        var ids = network.Roundabouts.Keys.OrderBy(r => r.Value).ToArray();
        if (ids.Length == 0) { log("radius skip=none"); return; }
        var id = ids[rng.Next(ids.Length)];
        float radius = 12f + (float)rng.NextDouble() * 28f;
        var res = network.SetRoundaboutRadius(id, radius);
        log($"radius rb={id.Value} r={radius:F1} result={(res.Success ? "ok" : res.Error.ToString())}");
    }

    private static void RemoveRoundaboutAction(RoadNetwork network, Random rng, Action<string> log)
    {
        var ids = network.Roundabouts.Keys.OrderBy(r => r.Value).ToArray();
        if (ids.Length == 0) { log("rb-remove skip=none"); return; }
        var id = ids[rng.Next(ids.Length)];
        network.RemoveRoundabout(id);
        log($"rb-remove rb={id.Value}");
    }

    private static void UndoRedo(UndoStack undo, DraftSession session, Random rng, Action<string> log)
    {
        // the editor clears the active gesture on restore; mirror that or a held
        // draft would reference pre-undo ids
        session.Cancel();
        int steps = rng.Next(1, 4);
        int done = 0;
        bool redo = rng.NextDouble() < 0.4;
        for (int i = 0; i < steps; i++)
            if (redo ? undo.Redo() : undo.Undo())
                done++;
        log($"{(redo ? "redo" : "undo")} steps={done}/{steps}");
    }

    private static void Bulldoze(RoadNetwork network, Random rng, Action<string> log)
    {
        var ids = network.Edges.Keys.OrderBy(e => e.Value).ToArray();
        if (ids.Length == 0)
        {
            log("bulldoze skip=empty");
            return;
        }
        var id = ids[rng.Next(ids.Length)];
        network.RemoveEdge(id);
        log($"bulldoze edge={id.Value}");
    }

    private static void ConfigureJunctionAction(RoadNetwork network, Random rng, Action<string> log)
    {
        var candidates = network.Nodes.Values.Where(n => n.Edges.Count >= 3)
            .OrderBy(n => n.Id.Value).ToArray();
        if (candidates.Length == 0)
        {
            log("configure skip=none");
            return;
        }
        var node = candidates[rng.Next(candidates.Length)];
        var mode = ControlModes[rng.Next(ControlModes.Length)];
        float sizeOffset = (float)(rng.NextDouble() * 4.0);
        var roles = new Dictionary<EdgeId, LegRole>();
        foreach (var edgeId in node.Edges.OrderBy(e => e.Value))
            roles[edgeId] = LegRoles[rng.Next(LegRoles.Length)];
        var config = new JunctionConfig(mode, roles, sizeOffset, new Dictionary<EdgeId, float>());
        network.ConfigureJunction(node.Id, config);
        log($"configure node={node.Id.Value} mode={mode} size={sizeOffset:F2} roles=" +
            string.Join(",", roles.Select(kv => $"{kv.Key.Value}:{kv.Value}")));
    }

    private static void ToggleSnap(DraftSession session, Random rng, Action<string> log)
    {
        var flag = SnapFlags[rng.Next(SnapFlags.Length)];
        session.EnabledSnaps ^= flag;
        session.Grid = new GridConfig(GridCells[rng.Next(GridCells.Length)]);
        log($"snap flag={flag} enabled={(session.EnabledSnaps & flag) != 0} grid={session.Grid.CellSize}");
    }

    private static void StepBackCancel(DraftSession session, RoadNetwork network, Random rng, Action<string> log)
    {
        var mode = Modes[rng.Next(Modes.Length)];
        var type = RoadCatalog.All[rng.Next(RoadCatalog.All.Count)];
        session.SetMode(mode);
        session.RoadType = type.Id;
        var points = new List<Vector3>();
        int clicks = rng.Next(1, 3); // 1..2
        for (int c = 0; c < clicks; c++)
        {
            var p = RandomPoint(network, rng);
            points.Add(p);
            session.Click(p, ClickRadius);
        }
        session.StepBack();
        session.Cancel();
        log($"stepback mode={mode} clicks=" + FormatPoints(points));
    }

    // ------------------------------------------------------------------- points

    private static Vector3 RandomPoint(RoadNetwork network, Random rng)
    {
        double r = rng.NextDouble();
        if (r < 0.60)
            return UniformPoint(rng);
        if (r < 0.85)
        {
            var nodes = network.Nodes.Values.OrderBy(n => n.Id.Value).ToArray();
            if (nodes.Length == 0)
                return UniformPoint(rng);
            var node = nodes[rng.Next(nodes.Length)];
            return node.Position + RandomOffset(rng, 12f);
        }
        var edges = network.Edges.Values.OrderBy(e => e.Id.Value).ToArray();
        if (edges.Length == 0)
            return UniformPoint(rng);
        var edge = edges[rng.Next(edges.Length)];
        return edge.Curve.Point(0.5f) + RandomOffset(rng, 8f);
    }

    private static Vector3 UniformPoint(Random rng)
    {
        float x = (float)(rng.NextDouble() * 2 * WorldHalfExtent - WorldHalfExtent);
        float z = (float)(rng.NextDouble() * 2 * WorldHalfExtent - WorldHalfExtent);
        return new Vector3(x, 0, z);
    }

    /// <summary>Uniform point within a disc of radius <paramref name="maxR"/> around
    /// the origin (sqrt-scaled radius so the distribution is uniform by area).</summary>
    private static Vector3 RandomOffset(Random rng, float maxR)
    {
        float angle = (float)(rng.NextDouble() * Math.PI * 2);
        float r = maxR * MathF.Sqrt((float)rng.NextDouble());
        return new Vector3(r * MathF.Cos(angle), 0, r * MathF.Sin(angle));
    }

    private static string FormatPoints(IEnumerable<Vector3> pts)
        => string.Concat(pts.Select(p => $"({p.X:F1},{p.Y:F1},{p.Z:F1})"));
}
