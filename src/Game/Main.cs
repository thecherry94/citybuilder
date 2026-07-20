using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Persistence;
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

/// <summary>Scene root: builds the domain model, the presentation nodes, and the UI.
/// With CITYBUILDER_SMOKE=1 it runs a scripted end-to-end scenario headlessly and
/// exits (used by CI/verification).</summary>
public partial class Main : Node3D
{
    private const string QuickSavePath = "user://saves/quick.json";

    private RoadNetwork _network = null!;
    private ToolController _controller = null!;
    private RoadNetworkView _view = null!;
    private StructureView _structures = null!;
    private LaneDebugOverlay _lanes = null!;
    private CityBuilder.Domain.Traffic.TrafficSim _traffic = null!;
    private AudioFx _audio = null!;
    private UndoStack _undo = null!;

    public UndoStack Undo => _undo;
    private double _trafficAccum;

    public CityBuilder.Domain.Traffic.TrafficSim Traffic => _traffic;
    public bool TrafficEnabled { get; set; }

    /// <summary>Status text for the toolbar's flash line (mirrors ToolController.StatusFlashed).</summary>
    public event Action<string>? StatusFlashed;

    public override void _Process(double delta)
    {
        PollXRay();
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
        var snap = new SnapEngine(_network);
        var session = new DraftSession(_network, snap);
        _undo = new UndoStack(_network);
        session.BeforeCommit += _undo.Checkpoint;

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

        _structures = new StructureView { Name = "StructureView" };
        _structures.Bind(_network);
        AddChild(_structures);

        _view = new RoadNetworkView { Name = "RoadNetworkView" };
        _view.Bind(_network);
        AddChild(_view);

        var ghost = new GhostView { Name = "GhostView" };
        ghost.BindNetwork(_network);
        AddChild(ghost);

        _lanes = new LaneDebugOverlay { Name = "LaneDebugOverlay" };
        _lanes.Bind(_network);
        AddChild(_lanes);

        _controller = new ToolController { Name = "ToolController" };
        _controller.Bind(_network, session, camera, ghost, _view);
        AddChild(_controller);

        _audio = new AudioFx { Name = "AudioFx" };
        AddChild(_audio);
        _controller.BindAudio(_audio);
        _controller.BindUndo(_undo);

        var gridOverlay = new GridOverlay { Name = "GridOverlay" };
        gridOverlay.Bind(_controller, camera);
        AddChild(gridOverlay);

        _traffic = new CityBuilder.Domain.Traffic.TrafficSim(_network, seed: 1);
        // bind after construction — binding the field before assignment passed null
        // and silently disabled the two-click vehicle-spawn tool
        _controller.BindTraffic(_traffic);
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
        junctionPanel.Bind(_network, () => _undo.Checkpoint());
        ui.AddChild(junctionPanel);
        _controller.NodeSelected += id =>
        {
            highlight.SetNode(id);
            if (id is { } nodeId)
                junctionPanel.ShowNode(nodeId);
            else
                junctionPanel.HideNode();
        };
        // roundabout regeneration re-keys ring nodes; keep selection on the successor
        junctionPanel.ReselectRequested += id => _controller.ReselectNode(id);

        if (!fromEditor && OS.GetEnvironment("CITYBUILDER_SMOKE") == "1")
            CallDeferred(MethodName.RunSmoke);
        if (!fromEditor && OS.GetEnvironment("CITYBUILDER_UITEST") is { Length: > 0 })
            CallDeferred(MethodName.RunUiTest);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Keycode: Key.F5, Pressed: true }:
                QuickSave();
                break;
            case InputEventKey { Keycode: Key.F9, Pressed: true }:
                QuickLoad();
                break;
            case InputEventKey { Keycode: Key.Pageup, Pressed: true } pgUp:
                _controller.StepElevation(pgUp.CtrlPressed ? 1f : 5f);
                break;
            case InputEventKey { Keycode: Key.Pagedown, Pressed: true } pgDn:
                _controller.StepElevation(pgDn.CtrlPressed ? -1f : -5f);
                break;
            case InputEventKey { Keycode: Key.U, Pressed: true, CtrlPressed: false }:
                _xrayManual = !_xrayManual;
                break;
            case InputEventKey { Keycode: Key.Z, Pressed: true, CtrlPressed: true }:
                TryUndo();
                break;
            case InputEventKey { Keycode: Key.Y, Pressed: true, CtrlPressed: true }:
                TryRedo();
                break;
        }
    }

    // ------------------------------------------------------------- save/load

    /// <summary>Write the network to the quick-save slot (user://saves/quick.json).</summary>
    public void QuickSave()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(QuickSavePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, SaveLoad.Save(_network));
            StatusFlashed?.Invoke("Saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusFlashed?.Invoke($"save failed: {ex.Message}");
        }
    }

    /// <summary>Load the quick-save slot into the live network in place. A single
    /// RoadNetwork.Changed event resyncs the view/overlays; traffic and the active
    /// tool gesture are resynced explicitly since they cache derived state.</summary>
    public void QuickLoad()
    {
        string path = ProjectSettings.GlobalizePath(QuickSavePath);
        if (!File.Exists(path))
        {
            StatusFlashed?.Invoke("No quick save");
            return;
        }
        try
        {
            _undo.Checkpoint(); // a quickload is itself undoable
            SaveLoad.LoadInto(File.ReadAllText(path), _network);
            _traffic.EnsureSynced();
            // restored ids may describe different geometry — drop the draft, hover
            // targets, and the inspected node rather than trusting id continuity
            _controller.ClearTransientState();
            StatusFlashed?.Invoke("Loaded");
        }
        catch (Exception ex) when (ex is SaveFormatException or IOException or UnauthorizedAccessException)
        {
            StatusFlashed?.Invoke($"load failed: {ex.Message}");
        }
    }

    /// <summary>Undo the last network mutation (Ctrl+Z). Restores a snapshot in
    /// place, then reruns the quickload resync: traffic drops strandees, the active
    /// gesture and selections are cleared (restored ids may describe different
    /// geometry).</summary>
    public void TryUndo()
    {
        if (!_undo.Undo()) { StatusFlashed?.Invoke("Nothing to undo"); return; }
        _traffic.EnsureSynced();
        _controller.ClearTransientState();
        StatusFlashed?.Invoke("Undone");
    }

    public void TryRedo()
    {
        if (!_undo.Redo()) { StatusFlashed?.Invoke("Nothing to redo"); return; }
        _traffic.EnsureSynced();
        _controller.ClearTransientState();
        StatusFlashed?.Invoke("Redone");
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

    private Node BuildGround()
    {
        // two variants of the same grid shader: opaque, and the x-ray ground that
        // lets below-ground carriageways read through (M8.5)
        const string groundShader = """
            shader_type spatial;
            {0}varying vec3 world_pos;
            void vertex() {{ world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }}
            void fragment() {{
                vec2 g = abs(fract(world_pos.xz / 8.0 - 0.5) - 0.5) * 8.0 / fwidth(world_pos.xz);
                float line = 1.0 - min(min(g.x, g.y), 1.0);
                vec3 base = vec3(0.30, 0.36, 0.27);
                ALBEDO = mix(base, base * 1.10, line * 0.5);
                ROUGHNESS = 1.0;{1}
            }}
            """;
        _groundOpaque = new ShaderMaterial
        {
            Shader = new Shader { Code = string.Format(groundShader, "", "") },
        };
        _groundXray = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = string.Format(groundShader, "render_mode cull_disabled;\n", "\n                ALPHA = 0.22;"),
            },
        };
        _ground = new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(2048, 2048) },
            MaterialOverride = _groundOpaque,
        };
        return _ground;
    }

    // ---------------------------------------------------------------- x-ray (M8.5)

    private MeshInstance3D _ground = null!;
    private ShaderMaterial _groundOpaque = null!, _groundXray = null!;
    private bool _xrayManual, _xrayActive;

    /// <summary>The U key holds the manual toggle; drafting below ground engages
    /// x-ray automatically for the draft's duration and restores the manual state
    /// after (spec: auto never fights the toggle).</summary>
    private void PollXRay()
    {
        if (_controller is null)
            return;
        bool want = _xrayManual || _controller.DraftBelowGround;
        if (want != _xrayActive)
            ApplyXRay(want);
    }

    private void ApplyXRay(bool on)
    {
        _xrayActive = on;
        _ground.MaterialOverride = on ? _groundXray : _groundOpaque;
        _view?.SetXRay(on);
        _structures?.SetXRay(on);
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

            // draft-handle drag: place a straight draft in adjust mode, drag its end, confirm
            // (grid snap stays on until after the screenshot so it captures the overlay)
            _controller.Session.AdjustMode = true;
            _controller.SetSnapType(SnapTypes.Grid, true);
            _controller.SetMode(ToolMode.Straight);
            _controller.HandleClickAt(V(-80, 60));
            _controller.HandleClickAt(V(0, 60));       // complete → Adjustable
            _controller.HandleMouseDownAt(V(0, 60));   // grab end handle
            _controller.HandleHoverAt(V(40, 60));      // drag
            _controller.HandleMouseUpAt();
            _controller.ConfirmDraft();                // commit
            _controller.Session.AdjustMode = false;
            Expect(_network.Edges.Values.Any(e =>
                    System.Numerics.Vector3.Distance(e.Curve.P3, V(40, 60)) < 1f
                    || System.Numerics.Vector3.Distance(e.Curve.P0, V(40, 60)) < 1f),
                "dragged draft endpoint not committed at (40, 60)");

            // quick save/load round trip: save, add a road, confirm it registered,
            // reload, confirm the network is back to the saved state. One vehicle
            // rides a surviving road, one rides the road that vanishes on load —
            // the latter exercises TrafficSim.Sync's stranded-vehicle purge.
            _controller.SetMode(ToolMode.SpawnVehicle);
            _controller.HandleClickAt(V(-60, 1));
            _controller.HandleClickAt(V(60, 1));
            Expect(_traffic.Vehicles.Count == 1,
                $"expected 1 vehicle before save, got {_traffic.Vehicles.Count}");
            int edgesAtSave = _network.Edges.Count;
            QuickSave();
            _controller.SetMode(ToolMode.Straight);
            _controller.HandleClickAt(V(150, -150));
            _controller.HandleClickAt(V(200, -150));
            Expect(_network.Edges.Count > edgesAtSave, "post-save test road not added");
            _controller.SetMode(ToolMode.SpawnVehicle);
            _controller.HandleClickAt(V(160, -150));
            _controller.HandleClickAt(V(190, -150)); // same-edge trip on the doomed road
            Expect(_traffic.Vehicles.Count == 2,
                $"expected 2 vehicles after doomed-road spawn, got {_traffic.Vehicles.Count}");
            QuickLoad();
            Expect(_network.Edges.Count == edgesAtSave, "quick-load did not restore edge count");
            Expect(_traffic.Vehicles.Count == 1,
                $"expected the doomed-road vehicle purged (1 survivor), got {_traffic.Vehicles.Count}");
            Expect(_traffic.Vehicles.All(v => v.Lane is null
                    || _network.Edges.Values.Any(e => e.Lanes.Any(l => l.Id == v.Lane))),
                "a vehicle survived the load on a lane that no longer exists");
            for (int i = 0; i < 30; i++)
                _traffic.Tick(1f / 60f); // sim must keep ticking after the purge

            // spawn a vehicle via the two-click tool and tick a bit of traffic
            _controller.SetMode(ToolMode.SpawnVehicle);
            _controller.HandleClickAt(V(-60, 1));
            _controller.HandleClickAt(V(60, 1));
            TrafficEnabled = true;
            for (int i = 0; i < 60; i++)
                _traffic.Tick(1f / 60f);

            // ghost stress: 900 hovers on an active draft — with CITYBUILDER_GHOSTPROBE=1
            // this prints avg RenderGhost cost (the M6.75 before/after pooling evidence)
            _controller.SetMode(ToolMode.Straight);
            _controller.HandleClickAt(V(-80, -60));
            for (int i = 0; i < 900; i++)
                _controller.HandleHoverAt(V(-80 + i * 0.15f, -60 + (i % 7) * 0.3f));
            _controller.CancelGesture();

            // M8: elevated draw via the elevation-step surface — a +10 m road far from
            // everything, committed and verified, then elevation reset for later steps
            _controller.SetMode(ToolMode.Straight);
            _controller.StepElevation(+5f);
            _controller.StepElevation(+5f);
            _controller.HandleClickAt(V(-300, -300));
            _controller.HandleClickAt(V(-180, -300));
            var elevated = _network.Edges.Values.First(e =>
                System.Numerics.Vector3.Distance(e.Curve.Point(0.5f), new System.Numerics.Vector3(-240, 10, -300)) < 6f);
            Expect(MathF.Abs(elevated.Curve.P0.Y - 10f) < 0.5f,
                $"elevated draw committed at Y={elevated.Curve.P0.Y:F1}, wanted ~10");
            _controller.StepElevation(-10f);

            // M7: upgrade via the tool surface + Ctrl+Z path (calls, not raw keys)
            _controller.SetMode(ToolMode.Upgrade);
            var upEdge = _network.Edges.Values.First(e =>
                System.Numerics.Vector3.Distance(e.Curve.Point(0.5f), V(-40, 0)) < 6f);
            _controller.SetRoadType(RoadCatalog.Street.Id);
            _controller.HandleHoverAt(V(-40, 0));
            _controller.HandleClickAt(V(-40, 0));
            Expect(_network.Edges[upEdge.Id].Type == RoadCatalog.Street.Id,
                "upgrade tool did not retype");
            TryUndo();
            Expect(_network.Edges[upEdge.Id].Type != RoadCatalog.Street.Id,
                "undo did not revert the upgrade");
            _controller.SetRoadType(RoadCatalog.TwoLane.Id);
            _controller.SetMode(ToolMode.Straight);

            // M8.5: dig a −8 m road far from everything, cover it via the upgrade
            // toggle, verify the flag + x-ray auto-engage, then restore state
            _controller.SetMode(ToolMode.Straight);
            _controller.StepElevation(-5f);
            _controller.StepElevation(-3f);
            Expect(_controller.DraftBelowGround, "below-ground elevation did not report DraftBelowGround");
            _controller.HandleClickAt(V(-300, 300));
            _controller.HandleClickAt(V(-180, 300));
            var dug = _network.Edges.Values.First(e =>
                System.Numerics.Vector3.Distance(e.Curve.Point(0.5f), new System.Numerics.Vector3(-240, -8, 300)) < 6f);
            Expect(MathF.Abs(dug.Curve.P0.Y + 8f) < 0.5f,
                $"below-ground draw committed at Y={dug.Curve.P0.Y:F1}, wanted ~-8");
            _controller.SetMode(ToolMode.Upgrade);
            _controller.CoveredToggleActive = true;
            _controller.HandleHoverAt(V(-240, 300));
            _controller.HandleClickAt(V(-240, 300));
            Expect(_network.Edges[dug.Id].Covered, "covered toggle did not set the flag");
            TryUndo();
            Expect(!_network.Edges[dug.Id].Covered, "undo did not revert the covered toggle");
            _controller.CoveredToggleActive = false;
            _controller.SetMode(ToolMode.Straight);
            _controller.StepElevation(+8f); // back to ground
            GD.Print("UITEST covered OK");

            // park the cursor over open ground so the grid overlay lands deterministically
            Input.WarpMouse(GetViewport().GetVisibleRect().Size * new Vector2(0.72f, 0.55f));
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(OS.GetEnvironment("CITYBUILDER_UITEST"));
            _controller.SetSnapType(SnapTypes.Grid, false);
            var panel = GetNode<JunctionPanel>("Ui/JunctionPanel");
            GD.Print($"UITEST OK visible={panel.Visible} rect={panel.GetGlobalRect()} vehicles={_traffic.Vehicles.Count}");
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

            // one-way triangle loop off the grid: a lone one-way stub would break
            // strong connectivity, so close it into a loop (three one-way edges),
            // tied back to the grid corner by a two-way link
            _controller.HandleClickAt(V(296, 96));
            _controller.HandleClickAt(V(320, 140));
            Expect(_network.Edges.Count == 18, $"expected 18 edges after loop link-in, got {_network.Edges.Count}");

            _controller.SetRoadType(RoadCatalog.OneWay.Id);
            _controller.HandleClickAt(V(320, 140));
            _controller.HandleClickAt(V(400, 140));
            _controller.HandleClickAt(V(400, 140));
            _controller.HandleClickAt(V(400, 220));
            _controller.HandleClickAt(V(400, 220));
            _controller.HandleClickAt(V(320, 140));
            Expect(_network.Edges.Count == 21, $"expected 21 edges after one-way loop, got {_network.Edges.Count}");
            _controller.SetRoadType(RoadCatalog.TwoLane.Id);

            // meshes exist for every entity
            _view.FlushDirty();
            Expect(_view.EdgeInstanceCount == _network.Edges.Count,
                $"edge meshes {_view.EdgeInstanceCount} != edges {_network.Edges.Count}");
            Expect(_view.NodeInstanceCount == _network.Nodes.Count,
                $"node meshes {_view.NodeInstanceCount} != nodes {_network.Nodes.Count}");

            // bulldoze the north leg of the cross -> T junction, network heals derived data
            _controller.SetMode(ToolMode.Bulldoze);
            _controller.HandleClickAt(V(0, 50));
            Expect(_network.Edges.Count == 20, $"expected 20 edges after bulldoze, got {_network.Edges.Count}");

            // lane overlay renders without errors
            _lanes.SetShown(true);
            // kind: Driving — the one-way loop's sidewalks are their own isolated
            // graph by design (ConnectorBuilder never links Sidewalk/Bicycle lanes;
            // see UrbanRoadTests.MixedKindNetworkIsNotConnectedAcrossKinds), so the
            // all-kinds check would fail the moment any sidewalk-carrying type (like
            // OneWay) enters the network regardless of the driving topology.
            if (!LaneGraph.IsStronglyConnected(_network, LaneKind.Driving))
            {
                foreach (var e in _network.Edges.Values)
                    GD.Print($"SMOKE-DUMP edge {e.Id.Value} type={e.Type.Value} " +
                        $"({e.Curve.P0.X:F1},{e.Curve.P0.Z:F1})->({e.Curve.P3.X:F1},{e.Curve.P3.Z:F1})");
                Expect(false, "driving lane graph not strongly connected");
            }

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

            // traffic: seeded ambient population must flow and complete trips,
            // including through the lights junction configured above
            _traffic.TargetPopulation = 15;
            for (int i = 0; i < 3600; i++)
                _traffic.Tick(1f / 60f);
            Expect(_traffic.Vehicles.Count >= 5,
                $"expected ≥5 ambient vehicles, got {_traffic.Vehicles.Count}");
            Expect(_traffic.Arrived > 0, "no vehicle completed a trip");

            // M7: upgrade-in-place — retype a grid edge, flip a one-way loop edge
            // (and back — a lone flipped loop edge breaks strong connectivity),
            // then undo everything and assert the network state rewinds
            int edgesBeforeM7 = _network.Edges.Count;
            var gridEdge = _network.Edges.Values.First(e =>
                System.Numerics.Vector3.Distance(e.Curve.Point(0.5f), V(224, 0)) < 5f);
            _undo.Checkpoint();
            Expect(_network.RetypeEdge(gridEdge.Id, RoadCatalog.Street.Id) is null,
                "retype grid edge failed");
            Expect(_network.Edges[gridEdge.Id].Type == RoadCatalog.Street.Id,
                "retype did not stick");
            var loopEdge = _network.Edges.Values.First(e => e.Type == RoadCatalog.OneWay.Id);
            _undo.Checkpoint();
            Expect(_network.FlipEdge(loopEdge.Id), "flip failed");
            Expect(!LaneGraph.IsStronglyConnected(_network, LaneKind.Driving),
                "flipped loop edge should break strong connectivity");
            _undo.Checkpoint();
            Expect(_network.FlipEdge(loopEdge.Id), "flip back failed");
            Expect(LaneGraph.IsStronglyConnected(_network, LaneKind.Driving),
                "flip back did not restore connectivity");
            TryUndo(); // undo flip-back
            Expect(!LaneGraph.IsStronglyConnected(_network, LaneKind.Driving),
                "undo did not restore the flipped state");
            TryUndo(); // undo flip
            TryUndo(); // undo retype
            Expect(_network.Edges[gridEdge.Id].Type == RoadCatalog.TwoLane.Id,
                "undo did not restore the original type");
            Expect(_network.Edges.Count == edgesBeforeM7,
                $"edge count after undos: {_network.Edges.Count} != {edgesBeforeM7}");

            Expect(_audio.LoadedCount == 5, $"audio streams loaded {_audio.LoadedCount}/5");

            // M7.5: roundabout conversion on an isolated 4-way far from the rest
            _controller.SetMode(ToolMode.Straight);
            _controller.HandleClickAt(V(600, 0));
            _controller.HandleClickAt(V(760, 0));
            _controller.HandleClickAt(V(680, -80));
            _controller.HandleClickAt(V(680, 80));
            var rbCenter = _network.Nodes.Values.First(n =>
                System.Numerics.Vector3.Distance(n.Position, V(680, 0)) < 1f);
            Expect(rbCenter.Edges.Count == 4, $"roundabout precursor not a 4-way ({rbCenter.Edges.Count})");
            _undo.Checkpoint();
            var conv = _network.ConvertToRoundabout(rbCenter.Id, 20f);
            Expect(conv.Success, $"convert to roundabout failed: {conv.Error}");
            Expect(_network.Roundabouts.Count == 1, "roundabout not registered");
            Expect(!_network.Nodes.ContainsKey(rbCenter.Id), "center node not replaced by ring");
            _view.FlushDirty(); // ring meshes build without throwing
            // bulldoze one approach -> ring re-arcs, roundabout survives (3 approaches left)
            var approach = _network.Edges.Values.First(e =>
                (_network.Nodes[e.StartNode].Ring == null) ^ (_network.Nodes[e.EndNode].Ring == null));
            _undo.Checkpoint();
            _network.RemoveEdge(approach.Id);
            Expect(_network.Roundabouts.Count == 1, "roundabout dissolved after a single approach bulldoze");
            _view.FlushDirty();
            // undo back to the pre-conversion 4-way
            TryUndo(); // undo bulldoze
            TryUndo(); // undo convert
            Expect(_network.Roundabouts.Count == 0, "undo did not remove the roundabout");
            _view.FlushDirty();

            // M8: a real bridge — ramp up, deck over the E-W road far from the cross,
            // ramp down; the ground road must NOT split, structures must mesh cleanly,
            // and traffic must flow on both levels
            int edgesBeforeBridge = _network.Edges.Count;
            _controller.SetMode(ToolMode.Straight);
            _controller.StepElevation(+5f); // deck height 5 m ≥ MinClearance 4.7
            _controller.HandleClickAt(V(900, -160));   // start at ground? no: free ends take current elevation
            _controller.HandleClickAt(V(900, 160));    // deck spanning z=-160..160 over nothing yet
            var deck = _network.Edges.Values.First(e =>
                System.Numerics.Vector3.Distance(e.Curve.Point(0.5f), new System.Numerics.Vector3(900, 5, 0)) < 6f);
            Expect(MathF.Abs(deck.Curve.P0.Y - 5f) < 0.5f, $"deck at Y={deck.Curve.P0.Y:F1}, wanted 5");
            _controller.StepElevation(-5f);
            // ground road passing under the deck: no junction may form
            _controller.HandleClickAt(V(820, 0));
            _controller.HandleClickAt(V(980, 0));
            if (_network.Edges.Count != edgesBeforeBridge + 2)
                foreach (var e in _network.Edges.Values.Where(e => e.Curve.Point(0.5f).X > 780f))
                    GD.Print($"SMOKE-DUMP bridge-area edge {e.Id.Value} type={e.Type.Value} " +
                        $"({e.Curve.P0.X:F1},{e.Curve.P0.Y:F1},{e.Curve.P0.Z:F1})->({e.Curve.P3.X:F1},{e.Curve.P3.Y:F1},{e.Curve.P3.Z:F1})");
            Expect(_network.Edges.Count == edgesBeforeBridge + 2,
                $"grade separation split something: {_network.Edges.Count} vs {edgesBeforeBridge}+2");
            _view.FlushDirty();
            _structures.FlushDirty(); // bridge fascia/pillars must build without throwing
            Expect(_structures.GetChildCount() > 0, "no structure mesh built for the deck");

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
