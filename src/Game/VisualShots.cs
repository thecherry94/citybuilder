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

                if (scenario.Name == "cross_2lane" && OS.GetEnvironment("CITYBUILDER_SHOTS_DUMP") == "1")
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

                view.QueueFree();
                overlay?.QueueFree();
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
            if (child.Mesh is not ArrayMesh mesh || mesh.GetSurfaceCount() < 2)
                continue;
            var verts = (Vector3[])mesh.SurfaceGetArrays(1)[(int)Mesh.ArrayType.Vertex];
            for (int i = 0; i + 5 < verts.Length; i += 6)
            {
                var quad = verts.Skip(i).Take(6).ToArray();
                var min = quad.Aggregate((a, b) => new Vector3(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y), Mathf.Min(a.Z, b.Z)));
                var max = quad.Aggregate((a, b) => new Vector3(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y), Mathf.Max(a.Z, b.Z)));
                // only quads near the junction are interesting
                if (min.X < 6 && max.X > -6 && min.Z < 6 && max.Z > -6)
                    GD.Print($"{child.Name} quad x[{min.X:F2},{max.X:F2}] z[{min.Z:F2},{max.Z:F2}]");
            }
        }
    }

    // ------------------------------------------------------------- scenarios

    private sealed record Shot(string Suffix, NVec Target, float Distance, float PitchDeg, float YawDeg);

    private sealed record Scenario(string Name, Action<RoadNetwork> Build, Shot[] Shots, bool ShowLanes = false);

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

        yield return new Scenario("tee", n =>
        {
            Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0)));
            Commit(n, Straight(new(0, 0, 0), new(0, 0, 80)));
        }, Standard(new(0, 0, 0), 55));

        yield return new Scenario("acute_y", n =>
        {
            Commit(n, Straight(new(0, 0, 0), new(120, 0, 0)));
            float rad = 20 * MathF.PI / 180;
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
            var grid = new GridTool { RoadType = RoadCatalog.TwoLane.Id };
            grid.AddClick(SnapResult.Free(new NVec(0, 0, 0)));
            grid.AddClick(SnapResult.Free(new NVec(144, 0, 0)));
            var gp = grid.AddClick(SnapResult.Free(new NVec(144, 0, 144)))!;
            Commit(n, gp);
            Commit(n, new PlacementProposal(new[]
            {
                new ProposedCurve(
                    new Bezier3(new(-30, 0, 30), new(50, 0, 40), new(90, 0, 110), new(180, 0, 120)),
                    EndpointBinding.None, EndpointBinding.None)
            }, RoadCatalog.FourLane.Id));
        }, new[] { Top(new(72, 0, 72), 220), Oblique(new(72, 0, 72), 150) });

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
