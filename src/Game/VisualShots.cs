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

                scenario.PostBuild?.Invoke(network, sim, view);

                Node? extra = null;
                if (scenario.Extra is not null)
                {
                    extra = scenario.Extra(network);
                    AddChild(extra);
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

                if (scenario.XRay)
                {
                    (GetParentOrNull<Main>())?.ApplyXRay(true);
                    view.SetXRay(true);
                }

                foreach (var shot in scenario.Shots)
                {
                    _camera.Frame(shot.Target.ToGodot(), shot.Distance, shot.PitchDeg, shot.YawDeg);
                    await CaptureAsync($"{_dir}/{scenario.Name}_{shot.Suffix}.png");
                    count++;
                }

                if (scenario.XRay)
                    (GetParentOrNull<Main>())?.ApplyXRay(false);

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
                extra?.QueueFree();
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
        Action<RoadNetwork, CityBuilder.Domain.Traffic.TrafficSim>? Traffic = null, int WarmupTicks = 0,
        // Runs once after Build/Traffic/warmup, before the shots are taken — for
        // scenarios that need to react to the built network/sim (e.g. the speed
        // heatmap tints edges from sampled vehicle speeds). Sim is null when the
        // scenario has no Traffic.
        Action<RoadNetwork, CityBuilder.Domain.Traffic.TrafficSim?, RoadNetworkView>? PostBuild = null,
        // Extra scene content (e.g. GhostViews showing live snap states) added after
        // Build and freed with the scenario.
        Func<RoadNetwork, Node>? Extra = null,
        // M8.5: shoot this scenario in x-ray (translucent ground + dimmed surface
        // roads) via the owning Main — the tunnel gallery evidence.
        bool XRay = false);

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

        yield return new Scenario("bridge", n =>
        {
            // ramp up – level deck over the ground road – ramp down (M8); the ground
            // road is NOT split (grade-separated crossing, no junction)
            Commit(n, Straight(new(-90, 0, 0), new(90, 0, 0)));
            Commit(n, Straight(new(0, 0, -140), new(0, 6, -50)));
            Commit(n, Straight(new(0, 6, -50), new(0, 6, 50)));
            Commit(n, Straight(new(0, 6, 50), new(0, 0, 140)));
        }, new[]
        {
            Top(new(0, 0, 0), 85),
            Oblique(new(0, 2, 0), 70),
            new Shot("low", new(0, 3, 12), 38, -10f, 55f), // under-deck hero: pillars + fascia
        },
        Extra: n =>
        {
            var sv = new StructureView();
            sv.Bind(n);
            sv.RebuildAll(); // the network pre-exists this view: no deltas will come
            return sv;
        });

        yield return new Scenario("elevated_junction", n =>
        {
            // two Street decks crossing at +10: elevated junction surface, corner
            // sidewalk zones, dead-end caps — all must sit on the node plane, with no
            // curtain geometry dropping to the ground (M8 mesh fix reference scene)
            Commit(n, Straight(new(-70, 10, 0), new(70, 10, 0), RoadCatalog.Street.Id));
            Commit(n, Straight(new(0, 10, -70), new(0, 10, 70), RoadCatalog.Street.Id));
        }, new[]
        {
            Oblique(new(0, 10, 0), 60),
            new Shot("low", new(20, 8, 8), 45, -8f, 40f),
            new Shot("corner", new(8, 10, 8), 40, -18f, 140f), // close corner: slab edge + props
            new Shot("under", new(5, 6, 3), 35, 10f, 50f),     // soffit + skirt from below
        },
        Extra: n =>
        {
            var sv = new StructureView();
            sv.Bind(n);
            sv.RebuildAll();
            return sv;
        });

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
                // ~45° diagonal along z = x + 24: crosses every grid line near the
                // middle of its 48 m segment, ~34 m between consecutive crossings —
                // clear of the 25° crossing floor (M4 final: was 15°) and both
                // sliver guards (TwoLane 8 m on grid edges, FourLane 16 m along the
                // diagonal). Gently bowed: an exactly straight diagonal trips the
                // known SelfIntersects false positive (see docs/gotchas.md).
                new ProposedCurve(
                    new Bezier3(new(-30, 0, -6), new(32.4f, 0, 64.9f), new(99.1f, 0, 131.6f), new(170, 0, 194)),
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

        yield return new Scenario("m5_new_types", n =>
        {
            // one-way street (both lanes forward, painted arrows) crossing an
            // Asymmetric 2+1 (double-solid center line off-center at -2.5m,
            // toward the single backward lane's side — see MarkingRules.Layout)
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0), RoadCatalog.OneWay.Id));
            Commit(n, Straight(new(0, 0, -80), new(0, 0, 80), RoadCatalog.Asymmetric.Id));
            // neck-down T at the asymmetric road's forward end: two lanes arrive,
            // the straight continuation is Two-Lane (one receiving lane) — the inner
            // lane's arrow must render as a dedicated LEFT (the M5 arrow-bug fix)
            Commit(n, Straight(new(-80, 0, 80), new(80, 0, 80)));
            Commit(n, Straight(new(0, 0, 80), new(0, 0, 160)));
        }, Standard(new(0, 0, 40), 80));

        yield return new Scenario("m5_congestion", n =>
        {
            // busy priority x minor cross: TwoLane (E-W, priority) x Street (N-S,
            // minor). Street is nominally wider (OuterHalf 6 vs TwoLane's 4), so Auto
            // control would flip the priority — override all four legs explicitly
            // (mirrors AssertivenessGuardTests.BusyCross).
            Commit(n, Straight(new(-200, 0, 0), new(200, 0, 0), RoadCatalog.TwoLane.Id));
            Commit(n, Straight(new(0, 0, -200), new(0, 0, 200), RoadCatalog.Street.Id));
            var node = n.Nodes.Values.Single(x => x.Edges.Count == 4);
            EdgeId At(NVec mid) => n.Edges.Values.Single(e => NVec.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;
            var wEdge = At(new(-100, 0, 0));
            var eEdge = At(new(100, 0, 0));
            var nEdge = At(new(0, 0, -100));
            var sEdge = At(new(0, 0, 100));
            n.ConfigureJunction(node.Id, node.Config with
            {
                Mode = JunctionControlMode.PrioritySigns,
                RoleOverrides = new Dictionary<EdgeId, LegRole>
                {
                    [wEdge] = LegRole.Main,
                    [eEdge] = LegRole.Main,
                    [nEdge] = LegRole.Yield,
                    [sEdge] = LegRole.Yield,
                },
            });
        }, new[]
        {
            Top(new(0, 0, 0), 70),
            Oblique(new(0, 0, 0), 55),
        }, Traffic: (n, sim) => sim.TargetPopulation = 40, WarmupTicks: 1800);

        yield return new Scenario("m6_speed_heatmap", n =>
        {
            // 3x3 grid of 100 m TwoLane blocks, built as individual 100 m segments
            // (not full-span lines) so exactly one segment can be committed with a
            // different RoadTypeId — edges can't be retyped after commit, only
            // chosen at commit time (see RoadNetwork.CommitCurve).
            for (int row = 0; row <= 3; row++)
            for (int col = 0; col < 3; col++)
            {
                // bottleneck: the middle segment of the middle row is a slower
                // Street (same lane count as TwoLane, half the design speed) on
                // the shortest through-route across the grid — traffic queues
                // behind it rather than merely posting a lower limit.
                var type = (row == 1 && col == 1) ? RoadCatalog.Street.Id : RoadCatalog.TwoLane.Id;
                Commit(n, Straight(new(col * 100, 0, row * 100), new((col + 1) * 100, 0, row * 100), type));
                Commit(n, Straight(new(row * 100, 0, col * 100), new(row * 100, 0, (col + 1) * 100), RoadCatalog.TwoLane.Id));
            }
        }, new[] { Top(new(150, 0, 150), 260) },
        Traffic: (n, sim) =>
        {
            EdgeId At(NVec mid) => n.Edges.Values.Single(e => NVec.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;
            var west = At(new(50, 0, 100));
            var east = At(new(250, 0, 100));
            sim.TargetPopulation = 60;
            // seed heavy directional flow straight across the bottleneck (the
            // shortest path end to end) so it visibly queues, plus ambient
            // background traffic on the rest of the grid
            for (int i = 0; i < 15; i++)
            {
                sim.Spawn(west, true, east);
                sim.Spawn(east, false, west);
                for (int t = 0; t < 40; t++)
                    sim.Tick(1f / 60f);
            }
        },
        WarmupTicks: 0,
        PostBuild: (n, sim, view) =>
        {
            if (sim is null)
                return;
            EdgeId At(NVec mid) => n.Edges.Values.Single(e => NVec.Distance(e.Curve.Point(0.5f), mid) < 5f).Id;
            var west = At(new(50, 0, 100));
            var east = At(new(250, 0, 100));
            var laneEdge = n.Edges.Values
                .SelectMany(e => e.Lanes.Select(l => (Lane: l.Id, Edge: e.Id)))
                .ToDictionary(p => p.Lane, p => p.Edge);

            var speedSum = new Dictionary<EdgeId, float>();
            var sampleCount = new Dictionary<EdgeId, int>();
            const int totalTicks = 30 * 60; // 30 sim-seconds at a fixed 1/60 dt
            for (int t = 0; t < totalTicks; t++)
            {
                sim.Tick(1f / 60f);
                if (t % 60 == 0)
                {
                    // keep the bottleneck route under load for the whole window
                    sim.Spawn(west, true, east);
                    sim.Spawn(east, false, west);
                }
                if (t % 10 != 0)
                    continue;
                foreach (var v in sim.Vehicles)
                {
                    if (v.Lane is not { } laneId || !laneEdge.TryGetValue(laneId, out var edgeId))
                        continue;
                    speedSum[edgeId] = speedSum.GetValueOrDefault(edgeId) + v.Speed;
                    sampleCount[edgeId] = sampleCount.GetValueOrDefault(edgeId) + 1;
                }
            }

            foreach (var edge in n.Edges.Values)
            {
                float limit = RoadCatalog.Get(edge.Type).SpeedLimit;
                float mean = sampleCount.TryGetValue(edge.Id, out var c) && c > 0
                    ? speedSum[edge.Id] / c
                    : limit; // never sampled: render as free-flowing rather than falsely red
                float ratio = Mathf.Clamp(mean / limit, 0f, 1f);
                // squared: a bottleneck averaging ~half its own limit (stopped at
                // the yield line half the time, free the rest) should still read
                // unambiguously congested rather than a wishy-washy yellow-green
                float shade = ratio * ratio;
                view.SetEdgeTint(edge.Id, new Color(1f - shade, shade, 0f));
            }
        });

        yield return SnapGallery();
        yield return ElevatedGhostGallery();

        // ---- M8.5: trenches, tunnels, x-ray, pillar awareness ----

        yield return new Scenario("trench", n =>
        {
            // ramp down – open cut at −4 – ramp up: retaining walls to the ground
            // lip both sides, coping strip readable from above, ⬇ structure only
            Commit(n, Straight(new(0, 0, -140), new(0, -4, -50)));
            Commit(n, Straight(new(0, -4, -50), new(0, -4, 50)));
            Commit(n, Straight(new(0, -4, 50), new(0, 0, 140)));
        }, new[]
        {
            Top(new(0, 0, 0), 85),
            Oblique(new(0, -2, 0), 70),
            new Shot("low", new(0, -2, 12), 30, -12f, 55f), // wall + coping hero
        }, Extra: StructuresFor);

        yield return new Scenario("tunnel", n =>
        {
            // dig to −8 and cover: portals must appear exactly where the covered
            // deck crosses PortalDepth (−3 m) on each ramp — nowhere else
            var r1 = Commit(n, Straight(new(0, 0, -160), new(0, -8, -60)));
            var r2 = Commit(n, Straight(new(0, -8, -60), new(0, -8, 60)));
            var r3 = Commit(n, Straight(new(0, -8, 60), new(0, 0, 160)));
            foreach (var r in new[] { r1, r2, r3 })
                foreach (var e in r.CreatedEdges)
                    n.SetCovered(e, true);
        }, new[]
        {
            // the entry portal sits where the covered ramp crosses −3 m: z ≈ −122
            Oblique(new(0, -1, -122), 45),                   // entry portal hero
            new Shot("portal_low", new(0, -2, -124), 26, -6f, 185f),
            Top(new(0, 0, 0), 100),                          // deep span: surface stays clean
        }, Extra: StructuresFor);

        yield return new Scenario("tunnel_xray", n =>
        {
            // same dig, plus a surface arterial grade-separated above the tube;
            // x-ray: translucent ground, dimmed surface road, tunnel carriageway reads
            var r1 = Commit(n, Straight(new(0, 0, -160), new(0, -8, -60)));
            var r2 = Commit(n, Straight(new(0, -8, -60), new(0, -8, 60)));
            var r3 = Commit(n, Straight(new(0, -8, 60), new(0, 0, 160)));
            foreach (var r in new[] { r1, r2, r3 })
                foreach (var e in r.CreatedEdges)
                    n.SetCovered(e, true);
            Commit(n, Straight(new(-90, 0, 0), new(90, 0, 0), RoadCatalog.FourLane.Id));
        }, new[]
        {
            Oblique(new(0, -4, 0), 90),
            Top(new(0, 0, 0), 110),
        }, Extra: StructuresFor, XRay: true);

        // ---- elevated junction close-ups (user report 2026-07-20): junctions with
        // ramped legs, seen from driver height, corner, and below — the angles the
        // high oblique/top shots never covered ----

        yield return new Scenario("elevated_tee_ramps", n =>
        {
            // T-junction ON the deck, west leg climbing past it, south leg ramping
            // to ground: paint must drape the slopes, the slab must not read
            // paper-thin at the corner bulges, props must stand on the deck
            Commit(n, Straight(new(-120, 12, 0), new(0, 8, 0), RoadCatalog.FourLane.Id));
            Commit(n, Straight(new(0, 8, 0), new(120, 8, 0), RoadCatalog.FourLane.Id));
            Commit(n, Straight(new(0, 8, 0), new(0, 0, 130), RoadCatalog.FourLane.Id));
        }, new[]
        {
            new Shot("corner", new(6, 8, 6), 45, -20f, 145f),   // the report's angle
            new Shot("approach", new(0, 8, 4), 40, -10f, 5f),   // driver coming up the ramp
            new Shot("under", new(4, 5, 2), 35, 8f, 60f),       // from below the slab
            Oblique(new(0, 8, 0), 60),
            Top(new(0, 8, 0), 80),
        }, Extra: StructuresFor);

        yield return new Scenario("ramp_into_tee", n =>
        {
            // ground junction with one leg climbing away — the mirror case: sloped
            // approach paint at a ground node
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0), RoadCatalog.FourLane.Id));
            Commit(n, Straight(new(0, 0, 0), new(0, 8, -110), RoadCatalog.FourLane.Id));
        }, new[]
        {
            new Shot("approach", new(0, 1, -16), 40, -9f, 185f), // looking down the ramp at the node
            Oblique(new(0, 0, 0), 55),
        }, Extra: StructuresFor);

        yield return new Scenario("underpass_pillars", n =>
        {
            // the M8 known-limit fix: a road crossing diagonally under the deck —
            // pillars near the carriageway must shift along the span or skip, never
            // stand in the road
            Commit(n, Straight(new(0, 0, -140), new(0, 6, -50)));
            Commit(n, Straight(new(0, 6, -50), new(0, 6, 50)));
            Commit(n, Straight(new(0, 6, 50), new(0, 0, 140)));
            Commit(n, Straight(new(-60, 0, -45), new(60, 0, 45)));
        }, new[]
        {
            new Shot("low", new(0, 3, 0), 45, -10f, 55f),
            Oblique(new(0, 2, 0), 70),
            Top(new(0, 0, 0), 85),
        }, Extra: StructuresFor);
    }

    /// <summary>Standard Extra: a StructureView over the pre-built network.</summary>
    private static Node StructuresFor(RoadNetwork n)
    {
        var sv = new StructureView();
        sv.Bind(n);
        sv.RebuildAll();
        return sv;
    }

    /// <summary>Two live elevated drafts: a legal 8% ramp bridging a ground road
    /// (blue, with previewed pillars + footprint shadow + ⬆ badge) and a ~20%
    /// TooSteep one (same visuals, red). Guards the M8.5 ghost feedback.</summary>
    private static Scenario ElevatedGhostGallery()
    {
        return new Scenario("elevated_ghost", n =>
        {
            // a ground road under station 1 so the footprint shadow reads against it
            Commit(n, Straight(new(-40, 0, 20), new(40, 0, 20)));
        }, new[]
        {
            new Shot("valid", new NVec(0, 6, 15), 90, -28f, 18f),
            new Shot("steep", new NVec(198, 6, 50), 75, -28f, 18f),
        }, Extra: n =>
        {
            var root = new Node3D { Name = "elevated_ghost" };

            void Station(NVec from, NVec to, float elevation)
            {
                var session = new DraftSession(n, new SnapEngine(n));
                session.SetMode(DraftMode.Straight);
                var ghost = new GhostView();
                root.AddChild(ghost);
                ghost.Ready += () =>
                {
                    session.Click(from, 6f);
                    session.CurrentElevation = elevation;
                    session.PointerMoved(to, 6f);
                    ghost.Show(session.Ghost, session.LastSnap);
                };
            }

            // 1: +12 m over 150 m (8%, legal on TwoLane 15%), grade-separated over
            //    the ground road: pillars + fascia, shadow footprint, ⬆ 12 m badge
            Station(new NVec(-60, 0, 60), new NVec(60, 0, -30), 12f);
            // 2: +12 m over ~58 m (≈21% > 15%): same visuals but red (TooSteep)
            Station(new NVec(170, 0, 60), new NVec(225, 0, 40), 12f);

            return root;
        });
    }

    private static Scenario SnapGallery()
    {
        return new Scenario("m675_snap_gallery", n =>
        {
            // stations 1+2 (x≈0): node-capture ring + edge tick on a T junction
            Commit(n, Straight(new(-60, 0, 0), new(60, 0, 0)));
            Commit(n, Straight(new(0, 0, 0), new(0, 0, 60)));
            // station 3 (x≈200): two distant roads for the perpendicular guide crossing
            Commit(n, Straight(new(140, 0, 0), new(200, 0, 0)));
            Commit(n, Straight(new(250, 0, 60), new(330, 0, 60)));
            // stations 4+5 (x≈400/580): edge for the perpendicular-arrival glyph;
            // 580 sits on the continuation guide of the x=460 end, harmlessly
            Commit(n, Straight(new(360, 0, 0), new(460, 0, 0)));
        }, new[]
        {
            new Shot("node_ring", new NVec(57, 0, 1), 26, -55f, 20f),
            new Shot("edge_tick", new NVec(-30, 0, 1), 26, -55f, 20f),
            new Shot("guide_cross", new NVec(205, 0, 50), 60, -60f, 15f),
            new Shot("perp_glyph", new NVec(402, 0, 20), 55, -60f, 15f),
            new Shot("angle_ticks", new NVec(592, 0, 10), 50, -60f, 15f),
        }, Extra: n =>
        {
            var root = new Node3D { Name = "snap_gallery" };

            // each station drives a real DraftSession into the target snap state and
            // renders its ghost with its own GhostView once the node enters the tree
            void Station(Action<DraftSession> drive,
                Func<DraftSession, RoadNetwork, (NVec? tan, NVec? refDir, NVec? anchor)>? extras = null)
            {
                var session = new DraftSession(n, new SnapEngine(n));
                session.SetMode(DraftMode.Straight);
                var ghost = new GhostView();
                root.AddChild(ghost);
                ghost.Ready += () =>
                {
                    drive(session);
                    var (tan, refDir, anchor) = extras?.Invoke(session, n) ?? (null, null, null);
                    ghost.Show(session.Ghost, session.LastSnap,
                        session.Draft?.Handles.Select(h => h.Position).ToArray(),
                        -1, tan, refDir, anchor);
                };
            }

            // 1: hard node capture — cursor on the leg 3 m from the T-junction end node
            Station(s => s.PointerMoved(new NVec(57, 0, 0.5f), 6f));
            // 2: edge tick mid-span (tangent passed like ToolController does)
            Station(s => s.PointerMoved(new NVec(-30, 0, 2f), 6f),
                (s, net) => (s.LastSnap.Edge is { } e ? net.Edges[e.Edge].Curve.Tangent(e.T) : null, null, null));
            // 3: guide intersection — perp guide off (200,0) × continuation of (250,60)
            Station(s => s.PointerMoved(new NVec(201, 0, 59), 6f));
            // 4: perpendicular arrival — draft anchored above the road, Edges snap off
            // so the foot candidate wins over the edge underneath the cursor
            Station(s =>
            {
                s.EnabledSnaps &= ~SnapTypes.Edges;
                s.Click(new NVec(400, 0, 60), 6f);
                s.PointerMoved(new NVec(403, 0, 0.5f), 6f);
            });
            // 5: angle badge + 8 m cell ticks — free-space draft, 46° drag; radius 3
            // keeps the CellLength candidate out of range so the angle fallback
            // (which then quantizes length) is what fires
            Station(s =>
            {
                s.EnabledSnaps |= SnapTypes.CellLength;
                s.Click(new NVec(580, 0, 0), 6f);
                float rad = 46f * MathF.PI / 180f;
                s.PointerMoved(new NVec(580, 0, 0) + 27.3f * new NVec(MathF.Cos(rad), 0, MathF.Sin(rad)), 3f);
            }, (s, _) => (null, new NVec(1, 0, 0), new NVec(580, 0, 0)));

            return root;
        });
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

    private static CommitResult Commit(RoadNetwork n, PlacementProposal p)
    {
        var v = n.Validate(p);
        if (!v.IsValid)
            throw new InvalidOperationException("scenario proposal invalid: " + string.Join(",", v.Errors));
        var r = n.Commit(v);
        if (!r.Success)
            throw new InvalidOperationException("scenario commit failed: " + r.FailureReason);
        return r;
    }
}
