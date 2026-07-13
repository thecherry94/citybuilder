using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

/// <summary>Scene root: builds the domain model, the presentation nodes, and the UI.
/// With CITYBUILDER_SMOKE=1 it runs a scripted end-to-end scenario headlessly and
/// exits (used by CI/verification).</summary>
public partial class Main : Node3D
{
    private RoadNetwork _network = null!;
    private ToolController _controller = null!;
    private RoadNetworkView _view = null!;
    private LaneDebugOverlay _lanes = null!;
    private CityBuilder.Domain.Traffic.TrafficSim _traffic = null!;
    private double _trafficAccum;

    public CityBuilder.Domain.Traffic.TrafficSim Traffic => _traffic;
    public bool TrafficEnabled { get; set; }

    public override void _Process(double delta)
    {
        if (!TrafficEnabled)
            return;
        _trafficAccum = Math.Min(_trafficAccum + delta, 4.0 / 60);
        while (_trafficAccum >= 1.0 / 60)
        {
            _traffic.Tick(1f / 60f);
            _trafficAccum -= 1.0 / 60;
        }
    }

    public override void _Ready()
    {
        _network = new RoadNetwork();
        var snap = new SnapService(_network);

        // thin road markings (0.15 m) vanish below ~1 px without multisampling
        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;

        AddChild(BuildLighting());
        AddChild(BuildGround());

        var camera = new CameraRig { Name = "CameraRig" };
        AddChild(camera);

        // Harness env vars can leak into an editor launched from a dev shell; ignore
        // them when this instance was started by the play button so the game plays
        // normally. Editor play runs attach the remote debugger and pass --editor-pid;
        // OS.GetCmdlineArgs strips engine flags, so check the raw process cmdline.
        bool fromEditor = EngineDebugger.IsActive() || RawCmdlineHasEditorPid();

        var shotsDir = OS.GetEnvironment("CITYBUILDER_SHOTS");
        if (!fromEditor && !string.IsNullOrEmpty(shotsDir))
        {
            var shots = new VisualShots { Name = "VisualShots" };
            shots.Bind(camera, shotsDir);
            AddChild(shots);
            return; // screenshot mode: no interactive tooling
        }

        _view = new RoadNetworkView { Name = "RoadNetworkView" };
        _view.Bind(_network);
        AddChild(_view);

        var ghost = new GhostView { Name = "GhostView" };
        AddChild(ghost);

        _lanes = new LaneDebugOverlay { Name = "LaneDebugOverlay" };
        _lanes.Bind(_network);
        AddChild(_lanes);

        _controller = new ToolController { Name = "ToolController" };
        _controller.Bind(_network, snap, camera, ghost, _view);
        AddChild(_controller);

        _traffic = new CityBuilder.Domain.Traffic.TrafficSim(_network, seed: 1);
        var trafficView = new TrafficView { Name = "TrafficView" };
        trafficView.Bind(_traffic);
        AddChild(trafficView);

        var lampView = new SignalLampView { Name = "SignalLampView" };
        lampView.Bind(_network, _traffic);
        AddChild(lampView);

        var highlight = new JunctionHighlight { Name = "JunctionHighlight" };
        highlight.Bind(_network);
        AddChild(highlight);

        var ui = new CanvasLayer { Name = "Ui" };
        AddChild(ui);
        var toolbar = new Toolbar { Name = "Toolbar" };
        toolbar.Bind(_controller, _lanes, this);
        ui.AddChild(toolbar);

        var junctionPanel = new JunctionPanel { Name = "JunctionPanel" };
        junctionPanel.Bind(_network);
        ui.AddChild(junctionPanel);
        _controller.NodeSelected += id =>
        {
            highlight.SetNode(id);
            if (id is { } nodeId)
                junctionPanel.ShowNode(nodeId);
            else
                junctionPanel.HideNode();
        };

        if (!fromEditor && OS.GetEnvironment("CITYBUILDER_SMOKE") == "1")
            CallDeferred(MethodName.RunSmoke);
        if (!fromEditor && OS.GetEnvironment("CITYBUILDER_UITEST") is { Length: > 0 })
            CallDeferred(MethodName.RunUiTest);
    }

    private static bool RawCmdlineHasEditorPid()
    {
        try
        {
            return System.IO.File.ReadAllText("/proc/self/cmdline").Contains("--editor-pid");
        }
        catch
        {
            return false; // not Linux (or /proc unavailable) — debugger check covers it
        }
    }

    private static Node BuildLighting()
    {
        var root = new Node3D { Name = "Lighting" };
        var sun = new DirectionalLight3D
        {
            Rotation = new Vector3(Mathf.DegToRad(-55), Mathf.DegToRad(30), 0),
            ShadowEnabled = true,
            LightEnergy = 1.2f,
        };
        root.AddChild(sun);
        var env = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.7f,
            },
        };
        root.AddChild(env);
        return root;
    }

    private static Node BuildGround()
    {
        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                varying vec3 world_pos;
                void vertex() { world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
                void fragment() {
                    vec2 g = abs(fract(world_pos.xz / 8.0 - 0.5) - 0.5) * 8.0 / fwidth(world_pos.xz);
                    float line = 1.0 - min(min(g.x, g.y), 1.0);
                    vec3 base = vec3(0.30, 0.36, 0.27);
                    ALBEDO = mix(base, base * 1.10, line * 0.5);
                    ROUGHNESS = 1.0;
                }
                """,
        };
        return new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(2048, 2048) },
            MaterialOverride = new ShaderMaterial { Shader = shader },
        };
    }

    // ----------------------------------------------------------------- ui test

    /// <summary>Scripted reproduction of the inspect flow with a real window:
    /// build a cross, enter Inspect, click the junction, screenshot, quit.</summary>
    private async void RunUiTest()
    {
        try
        {
            static System.Numerics.Vector3 V(float x, float z) => new(x, 0, z);
            _controller.SetMode(ToolMode.Straight);
            _controller.HandleClickAt(V(-80, 0));
            _controller.HandleClickAt(V(80, 0));
            _controller.HandleClickAt(V(0, -80));
            _controller.HandleClickAt(V(0, 80));
            _controller.SetMode(ToolMode.Inspect);
            _controller.HandleClickAt(V(0, 0));
            _view.FlushDirty();

            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(OS.GetEnvironment("CITYBUILDER_UITEST"));
            var panel = GetNode<JunctionPanel>("Ui/JunctionPanel");
            GD.Print($"UITEST OK visible={panel.Visible} rect={panel.GetGlobalRect()}");
            GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"UITEST FAIL: {ex}");
            GetTree().Quit(1);
        }
    }

    // ------------------------------------------------------------------- smoke

    private void RunSmoke()
    {
        try
        {
            static System.Numerics.Vector3 V(float x, float z) => new(x, 0, z);

            // straight road with a hover in between (exercises ghost + validation)
            _controller.SetMode(ToolMode.Straight);
            _controller.HandleClickAt(V(-100, 0));
            _controller.HandleHoverAt(V(0, 50));
            _controller.HandleClickAt(V(100, 0));
            Expect(_network.Edges.Count == 1, $"expected 1 edge, got {_network.Edges.Count}");

            // crossing road -> auto intersection
            _controller.HandleClickAt(V(0, -100));
            _controller.HandleClickAt(V(0, 100));
            Expect(_network.Edges.Count == 4, $"expected 4 edges after crossing, got {_network.Edges.Count}");
            Expect(_network.Nodes.Count == 5, $"expected 5 nodes after crossing, got {_network.Nodes.Count}");

            // grid stamp away from the cross
            _controller.SetMode(ToolMode.Grid);
            _controller.HandleClickAt(V(200, 0));
            _controller.HandleClickAt(V(296, 0));
            _controller.HandleClickAt(V(296, 96));
            Expect(_network.Edges.Count == 16, $"expected 16 edges after grid, got {_network.Edges.Count}");

            // link the cross and the grid so the whole network is one component
            _controller.SetMode(ToolMode.Straight);
            _controller.HandleClickAt(V(100, 0));
            _controller.HandleClickAt(V(200, 0));
            Expect(_network.Edges.Count == 17, $"expected 17 edges after link, got {_network.Edges.Count}");

            // meshes exist for every entity
            _view.FlushDirty();
            Expect(_view.EdgeInstanceCount == _network.Edges.Count,
                $"edge meshes {_view.EdgeInstanceCount} != edges {_network.Edges.Count}");
            Expect(_view.NodeInstanceCount == _network.Nodes.Count,
                $"node meshes {_view.NodeInstanceCount} != nodes {_network.Nodes.Count}");

            // bulldoze the north leg of the cross -> T junction, network heals derived data
            _controller.SetMode(ToolMode.Bulldoze);
            _controller.HandleClickAt(V(0, 50));
            Expect(_network.Edges.Count == 16, $"expected 16 edges after bulldoze, got {_network.Edges.Count}");

            // lane overlay renders without errors
            _lanes.SetShown(true);
            Expect(LaneGraph.IsStronglyConnected(_network), "lane graph not strongly connected");

            // junction control: lights + resize on the T junction left by the bulldoze
            var tee = _network.Nodes.Values.First(n => n.Edges.Count == 3);
            var leg = tee.Edges.First();
            float cutBefore = System.Numerics.Vector3.Distance(
                _network.Edges[leg].Curve.Point(tee.Junction.CutT[leg]), tee.Position);
            _network.ConfigureJunction(tee.Id, tee.Config with
            {
                Mode = CityBuilder.Domain.Network.JunctionControlMode.TrafficLights,
                SizeOffset = 3f,
            });
            float cutAfter = System.Numerics.Vector3.Distance(
                _network.Edges[leg].Curve.Point(tee.Junction.CutT[leg]), tee.Position);
            Expect(MathF.Abs(cutAfter - cutBefore - 3f) < 0.2f,
                $"resize: cut moved {cutAfter - cutBefore:F2}, wanted ~3");
            Expect(tee.Connectors.Any(), "no connectors at controlled junction");
            Expect(tee.Connectors.All(c => c.Row == CityBuilder.Domain.Network.RightOfWay.Signal),
                "connectors at a lights junction must be Signal");
            _view.FlushDirty(); // rebuild with props; must not throw

            GD.Print("SMOKE OK");
            GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SMOKE FAIL: {ex}");
            GetTree().Quit(1);
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
