using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Godot;
using NVec = System.Numerics.Vector3;

namespace CityBuilder.Game;

/// <summary>
/// Visual regression harness: builds a set of road configurations, frames the camera,
/// and saves screenshots for human/agent inspection. Activated by launching with
/// CITYBUILDER_SHOTS=&lt;output dir&gt; (needs a real renderer, not --headless).
/// </summary>
public partial class VisualShots : Node3D
{
    private CameraRig _camera = null!;
    private string _dir = "shots";

    public void Bind(CameraRig camera, string dir)
    {
        _camera = camera;
        _dir = dir;
    }

    public override void _Ready() => CallDeferred(MethodName.Run);

    private async void Run()
    {
        try
        {
            DisplayServer.WindowSetSize(new Vector2I(1280, 720));
            System.IO.Directory.CreateDirectory(_dir);
            int count = 0;

            foreach (var scenario in Scenarios())
            {
                var network = new RoadNetwork();
                var view = new RoadNetworkView
                {
                    Name = $"view_{scenario.Name}",
                    DebugTint = OS.GetEnvironment("CITYBUILDER_SHOTS_TINT") == "1",
                };
                view.Bind(network);
                AddChild(view);
                LaneDebugOverlay? overlay = null;

                scenario.Build(network);
                view.FlushDirty();

                TrafficView? trafficView = null;
                SignalLampView? lampView = null;
                CityBuilder.Domain.Traffic.TrafficSim? sim = null;
                if (scenario.Traffic is not null)
                {
                    sim = new CityBuilder.Domain.Traffic.TrafficSim(network, seed: 7);
                    scenario.Traffic(network, sim);
                    for (int i = 0; i < scenario.WarmupTicks; i++)
                        sim.Tick(1f / 60f);
                    trafficView = new TrafficView();
                    trafficView.Bind(sim);
                    AddChild(trafficView);
                    lampView = new SignalLampView();
                    lampView.Bind(network, sim);
                    AddChild(lampView);
                }

                if (scenario.Name == OS.GetEnvironment("CITYBUILDER_SHOTS_DUMP"))
                    DumpMarkingQuads(view);

                if (scenario.ShowLanes)
                {
                    overlay = new LaneDebugOverlay();
                    overlay.Bind(network);
                    AddChild(overlay);
                    overlay.SetShown(true);
                }

                foreach (var shot in scenario.Shots)
                {
                    _camera.Frame(shot.Target.ToGodot(), shot.Distance, shot.PitchDeg, shot.YawDeg);
                    await CaptureAsync($"{_dir}/{scenario.Name}_{shot.Suffix}.png");
                    count++;
                }

                // motion filmstrip: frames ~0.33 s apart while the sim runs, from the
                // first camera. Composite with `magick *_motion*.png -compose lighten
                // -flatten trails.png` — evenly spaced car ghosts = smooth motion,
                // clumps or gaps = snapping. This is how movement gets debugged.
                if (sim is not null)
                {
                    var cam = scenario.Shots[0];
                    _camera.Frame(cam.Target.ToGodot(), cam.Distance, cam.PitchDeg, cam.YawDeg);
                    for (int f = 0; f < 8; f++)
                    {
                        await CaptureAsync($"{_dir}/{scenario.Name}_motion{f}.png");
                        count++;
                        for (int k = 0; k < 20; k++)
                            sim.Tick(1f / 60f);
                    }
                }

                view.QueueFree();
                overlay?.QueueFree();
                trafficView?.QueueFree();
                lampView?.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            GD.Print($"SHOTS OK {count}");
            GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SHOTS FAIL: {ex}");
            GetTree().Quit(1);
        }
    }

    private async Task CaptureAsync(string path)
    {
        // two full frames so mesh uploads and the moved camera are visible
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(path);
    }

    private static void DumpMarkingQuads(RoadNetworkView view)
    {
        foreach (var child in view.GetChildren().OfType<MeshInstance3D>())
        {
            if (child.Mesh is not ArrayMesh mesh)
                continue;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var verts = (Vector3[])mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex];
                for (int i = 0; i + 5 < verts.Length; i += 6)
                {
                    var quad = verts.Skip(i).Take(6).ToArray();
                    var min = quad.Aggregate((a, b) => new Vector3(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y), Mathf.Min(a.Z, b.Z)));
                    var max = quad.Aggregate((a, b) => new Vector3(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y), Mathf.Max(a.Z, b.Z)));
                    // markings only: flat quads at marking height near the junction
                    bool markingHeight = min.Y > MeshBuilders.MarkingY - 0.02f && max.Y < MeshBuilders.MarkingY + 0.02f;
                    if (markingHeight && min.X < 30 && max.X > -30 && min.Z < 30 && max.Z > -30)
                    {
                        var mid = (min + max) / 2;
                        GD.Print($"{child.Name} s{s} quad x[{min.X:F2},{max.X:F2}] z[{min.Z:F2},{max.Z:F2}] mid=({mid.X:F2},{mid.Z:F2})");
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------- scenarios

    private sealed record Shot(string Suffix, NVec Target, float Distance, float PitchDeg, float YawDeg);

    private sealed record Scenario(string Name, Action<RoadNetwork> Build, Shot[] Shots, bool ShowLanes = false,
        Action<RoadNetwork, CityBuilder.Domain.Traffic.TrafficSim>? Traffic = null, int WarmupTicks = 0);

    private static Shot Top(NVec target, float dist) => new("top", target, dist, -89f, 0f);
    private static Shot Oblique(NVec target, float dist) => new("oblique", target, dist, -35f, 30f);

    private static Shot[] Standard(NVec target, float dist) => new[] { Top(target, dist), Oblique(target, dist * 0.8f) };

    private static IEnumerable<Scenario> Scenarios()
    {
        yield return new Scenario("cross_2lane", n =>
        {
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0)));
            Commit(n, Straight(new(0, 0, -80), new(0, 0, 80)));
        }, Standard(new(0, 0, 0), 60));

        yield return new Scenario("cross_4lane", n =>
        {
            Commit(n, Straight(new(-90, 0, 0), new(90, 0, 0), RoadCatalog.FourLane.Id));
            Commit(n, Straight(new(0, 0, -90), new(0, 0, 90), RoadCatalog.FourLane.Id));
        }, Standard(new(0, 0, 0), 75));

        yield return new Scenario("tee", n =>
        {
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0)));
            Commit(n, Straight(new(0, 0, 0), new(0, 0, 80)));
        }, Standard(new(0, 0, 0), 55));

        yield return new Scenario("acute_y", n =>
        {
            Commit(n, Straight(new(0, 0, 0), new(120, 0, 0)));
            // 30°: as acute as the M4 MinJunctionAngleDeg (25°) floor allows with margin;
            // also dodges BezierOps.SelfIntersects' known false-positive angles on
            // straight lines (27/28/31/33/35/40°, see docs/gotchas.md).
            float rad = 30 * MathF.PI / 180;
            Commit(n, Straight(new(0, 0, 0), new(120 * MathF.Cos(rad), 0, 120 * MathF.Sin(rad))));
        }, Standard(new(25, 0, 6), 55));

        yield return new Scenario("mixed_cross", n =>
        {
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0), RoadCatalog.FourLane.Id));
            Commit(n, Straight(new(0, 0, -80), new(0, 0, 80)));
        }, Standard(new(0, 0, 0), 60));

        yield return new Scenario("corner", n =>
        {
            Commit(n, Straight(new(-70, 0, 0), new(0, 0, 0)));
            Commit(n, Straight(new(0, 0, 0), new(0, 0, 70)));
        }, Standard(new(0, 0, 0), 45));

        yield return new Scenario("priority_tee", n =>
        {
            // Auto control: the wider street pair is the main road, the stub yields —
            // teeth on the stub, no stop lines on the mains, priority signage
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, 0), new(0, 0, 80), RoadCatalog.TwoLane.Id));
        }, Standard(new(0, 0, 2), 45));

        yield return new Scenario("allway_cross", n =>
        {
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, -80), new(0, 0, 80), RoadCatalog.Street.Id));
            var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
            n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.AllWayStop });
        }, Standard(new(0, 0, 0), 55));

        yield return new Scenario("lights_cross", n =>
        {
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, -80), new(0, 0, 80), RoadCatalog.Street.Id));
            var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
            n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.TrafficLights });
        }, new[]
        {
            Top(new(0, 0, 0), 55),
            Oblique(new(0, 0, 0), 45),
            new Shot("low", new(2, 0, 2), 26, -16f, 40f),
        });

        yield return new Scenario("resized_cross", n =>
        {
            // node-controller style: junction grown 6 m on every leg; paint,
            // crosswalks and corner zones must follow the moved cuts seamlessly
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, -80), new(0, 0, 80), RoadCatalog.Street.Id));
            var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
            n.ConfigureJunction(node.Id, node.Config with { SizeOffset = 6f });
        }, new[]
        {
            Top(new(0, 0, 0), 70),
            Oblique(new(0, 0, 0), 55),
            new Shot("corner_low", new(8, 0, 8), 26, -28f, 30f),
        });

        yield return new Scenario("asym_resize", n =>
        {
            // only the east leg grows: junction stretches toward it, others stay
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, -80), new(0, 0, 80), RoadCatalog.Street.Id));
            var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
            var east = node.Edges.First(e =>
            {
                var edge = n.Edges[e];
                var mid = edge.Curve.Point(0.5f);
                return mid.X > 1f && MathF.Abs(mid.Z) < 1f;
            });
            n.ConfigureJunction(node.Id, node.Config with
            {
                LegOffsets = new Dictionary<EdgeId, float> { [east] = 6f },
            });
        }, Standard(new(3, 0, 0), 60));

        yield return new Scenario("street_corner", n =>
        {
            // 90° bend in an urban street: corner marking continuation must keep
            // the center dash equidistant from both curbs through the bend
            Commit(n, Straight(new(-50, 0, 0), new(0, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, 0), new(0, 0, 40), RoadCatalog.Street.Id));
        }, new[]
        {
            Top(new(0, 0, 0), 40),
            new Shot("bend_topdown", new(-2, 0, 2), 18, -89f, 0f),
            new Shot("bend_low", new(-2, 0, 2), 20, -30f, 120f),
        });

        yield return new Scenario("street_corner_short", n =>
        {
            // bend with a short dead-end leg (clamped cut): the user's reported case
            Commit(n, Straight(new(-50, 0, 0), new(0, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, 0), new(0, 0, 15), RoadCatalog.Street.Id));
        }, new[]
        {
            new Shot("bend_topdown", new(-1, 0, 4), 24, -89f, 0f),
        });

        yield return new Scenario("transition", n =>
        {
            Commit(n, Straight(new(-70, 0, 0), new(0, 0, 0), RoadCatalog.TwoLane.Id));
            Commit(n, Straight(new(0, 0, 0), new(70, 0, 0), RoadCatalog.FourLane.Id));
        }, Standard(new(0, 0, 0), 40));

        yield return new Scenario("dead_end", n =>
        {
            Commit(n, Straight(new(0, 0, 0), new(40, 0, 0), RoadCatalog.FourLane.Id));
        }, Standard(new(20, 0, 0), 35));

        yield return new Scenario("curved_junction", n =>
        {
            Commit(n, Straight(new(-90, 0, 0), new(90, 0, 0)));
            // strongly curved road crossing the straight twice at healthy angles
            Commit(n, Curve(new(-60, 0, -60), new(0, 0, 80), new(60, 0, -60)));
        }, Standard(new(0, 0, 8), 70));

        yield return new Scenario("boulevard_grid", n =>
        {
            var grid = new RoadDraft(new GridStampShape(), RoadCatalog.TwoLane.Id);
            grid.AddHandle(SnapResult.Free(new NVec(0, 0, 0)));
            grid.AddHandle(SnapResult.Free(new NVec(144, 0, 0)));
            grid.AddHandle(SnapResult.Free(new NVec(144, 0, 144)));
            var gp = grid.BuildProposal()!;
            Commit(n, gp);
            Commit(n, new PlacementProposal(new[]
            {
                // control points widened vs. the original diagonal so the crossings
                // with the grid's (96,96) corner edges stay >= FourLane's 16 m
                // consecutive-crossing minimum apart (M4's sliver-crossing guard).
                new ProposedCurve(
                    new Bezier3(new(-30, 0, 30), new(50, 0, 40), new(100, 0, 100), new(190, 0, 130)),
                    EndpointBinding.None, EndpointBinding.None)
            }, RoadCatalog.FourLane.Id));
        }, new[] { Top(new(72, 0, 72), 220), Oblique(new(72, 0, 72), 150) });

        yield return new Scenario("street_cross", n =>
        {
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, -80), new(0, 0, 80), RoadCatalog.Street.Id));
        }, new[]
        {
            Top(new(0, 0, 0), 65),
            Oblique(new(0, 0, 0), 52),
            // corner close-ups from two angles — corner geometry regressions show
            // here long before they are visible in the wide shots
            new Shot("corner_low", new(5, 0, 5), 22, -28f, 30f),
            new Shot("corner_high", new(6, 0, 6), 25, -55f, 140f),
            new Shot("corner_topdown", new(6, 0, 6), 16, -89f, 0f),
        });

        yield return new Scenario("avenue_mix", n =>
        {
            // avenue (bike lanes + sidewalks) crossed by an urban street
            Commit(n, Straight(new(-90, 0, 0), new(90, 0, 0), RoadCatalog.Avenue.Id));
            Commit(n, Straight(new(0, 0, -90), new(0, 0, 90), RoadCatalog.Street.Id));
        }, new[]
        {
            Top(new(0, 0, 0), 85),
            Oblique(new(0, 0, 0), 68),
            new Shot("corner_low", new(8, 0, 12), 20, -22f, 210f),
        });

        yield return new Scenario("asym_tee", n =>
        {
            // four-lane avenue with a two-lane side road: asymmetric approaches
            Commit(n, Straight(new(-90, 0, 0), new(90, 0, 0), RoadCatalog.FourLane.Id));
            Commit(n, Straight(new(0, 0, 0), new(0, 0, 90)));
        }, Standard(new(0, 0, 8), 55));

        yield return new Scenario("short_block", n =>
        {
            // two street crossings only 14 m apart: cuts get clamped, paint must not
            // land mid-junction and sidewalks must stay closed
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, -70), new(0, 0, 70), RoadCatalog.Street.Id));
            Commit(n, Straight(new(14, 0, -70), new(14, 0, 70), RoadCatalog.Street.Id));
        }, Standard(new(7, 0, 0), 55));

        yield return new Scenario("traffic_flow", n =>
        {
            // street grid with ambient traffic after a minute of warmup
            for (int i = 0; i < 3; i++)
            {
                Commit(n, Straight(new(-60, 0, i * 120), new(300, 0, i * 120), RoadCatalog.Street.Id));
                Commit(n, Straight(new(i * 120, 0, -60), new(i * 120, 0, 300), RoadCatalog.Street.Id));
            }
        }, new[]
        {
            Top(new(120, 0, 120), 200),
            Oblique(new(120, 0, 120), 130),
        }, Traffic: (n, sim) => sim.TargetPopulation = 120, WarmupTicks: 3600);

        yield return new Scenario("traffic_red_light", n =>
        {
            Commit(n, Straight(new(-150, 0, 0), new(150, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, -150), new(0, 0, 150), RoadCatalog.Street.Id));
            var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
            n.ConfigureJunction(node.Id, node.Config with { Mode = JunctionControlMode.TrafficLights });
        }, new[]
        {
            Top(new(0, 0, 0), 70),
            new Shot("low", new(0, 0, 12), 40, -22f, 15f),
        }, Traffic: (n, sim) =>
        {
            // keep both axes fed so one shows a red-light queue
            sim.TargetPopulation = 24;
        }, WarmupTicks: 2400);

        yield return new Scenario("traffic_yield", n =>
        {
            Commit(n, Straight(new(-200, 0, 0), new(200, 0, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 0, 0), new(0, 0, 200), RoadCatalog.TwoLane.Id));
        }, new[]
        {
            Top(new(0, 0, 10), 60),
            Oblique(new(0, 0, 10), 50),
        }, Traffic: (n, sim) =>
        {
            EdgeId At(NVec mid) => n.Edges.Values
                .Single(e => NVec.Distance(e.Curve.Point(0.5f), mid) < 8f).Id;
            // heavy main flow, side road wants in
            sim.TargetPopulation = 0;
            for (int i = 0; i < 10; i++)
            {
                sim.Spawn(At(new(-100, 0, 0)), true, At(new(100, 0, 0)));
                sim.Spawn(At(new(100, 0, 0)), false, At(new(-100, 0, 0)));
                sim.Spawn(At(new(0, 0, 100)), false, At(new(100, 0, 0)));
                for (int t = 0; t < 90; t++)
                    sim.Tick(1f / 60f);
            }
        }, WarmupTicks: 300);

        yield return new Scenario("m4_drafting", n =>
        {
            // straight base road
            Commit(n, Straight(new(-70, 0, 0), new(0, 0, 0)));
            var baseEdge = n.Edges.Values.Single();
            var endNode = n.Nodes[baseEdge.EndNode];
            var tangent = baseEdge.Curve.Tangent(1);

            // arc continuing from the base road's end node, tangent-locked to it —
            // this is the RoadDraft + ArcShape path a real drag gesture takes.
            var arc = new RoadDraft(new ArcShape(), RoadCatalog.TwoLane.Id);
            arc.AddHandle(
                new SnapResult(endNode.Position, SnapKind.Node, endNode.Id, null, null, Array.Empty<Guideline>()),
                tangent);
            arc.AddHandle(SnapResult.Free(new NVec(50, 0, 50)));
            Commit(n, arc.BuildProposal()!);

            // parallel road exactly curb-to-curb: offset by the sum of both types'
            // OuterHalf (built directly here, not via snapping — VisualShots commits
            // proposals straight into the network).
            float offset = RoadCatalog.TwoLane.OuterHalf + RoadCatalog.TwoLane.OuterHalf;
            Commit(n, Straight(new(-70, 0, -offset), new(0, 0, -offset)));
        }, Standard(new(-5, 0, 25), 105));

        yield return new Scenario("lanes_overlay", n =>
        {
            Commit(n, Straight(new(-60, 0, 0), new(60, 0, 0), RoadCatalog.FourLane.Id));
            Commit(n, Straight(new(0, 0, -60), new(0, 0, 60)));
        }, Standard(new(0, 0, 0), 55), ShowLanes: true);
    }

    // ---------------------------------------------------------------- helpers

    private static PlacementProposal Straight(NVec a, NVec b, RoadTypeId? type = null)
        => new(new[] { new ProposedCurve(Bezier3.Line(a, b), EndpointBinding.None, EndpointBinding.None) },
            type ?? RoadCatalog.TwoLane.Id);

    private static PlacementProposal Curve(NVec a, NVec ctrl, NVec b, RoadTypeId? type = null)
        => new(new[]
        {
            new ProposedCurve(Bezier3.FromQuadratic(a, ctrl, b), EndpointBinding.None, EndpointBinding.None)
        }, type ?? RoadCatalog.TwoLane.Id);

    private static void Commit(RoadNetwork n, PlacementProposal p)
    {
        var v = n.Validate(p);
        if (!v.IsValid)
            throw new InvalidOperationException("scenario proposal invalid: " + string.Join(",", v.Errors));
        var r = n.Commit(v);
        if (!r.Success)
            throw new InvalidOperationException("scenario commit failed: " + r.FailureReason);
    }
}
