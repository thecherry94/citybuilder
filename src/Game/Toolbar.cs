using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Tools;
using Godot;

namespace CityBuilder.Game;

/// <summary>Top-left panel: drawing mode, road type, snap toggles, lane-debug toggle,
/// plus the floating length/angle readout and a status flash line.</summary>
public partial class Toolbar : Control
{
    private ToolController _controller = null!;
    private LaneDebugOverlay _lanes = null!;
    private Main _main = null!;
    private Label _readout = null!;
    private Label _status = null!;
    private Label _trafficCount = null!;
    private double _statusTtl;

    public void Bind(ToolController controller, LaneDebugOverlay lanes, Main main)
    {
        _controller = controller;
        _lanes = lanes;
        _main = main;
        controller.StatusFlashed += FlashStatus;
        main.StatusFlashed += FlashStatus;
        controller.ReadoutChanged += t => _readout.Text = t;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var panel = new PanelContainer
        {
            Position = new Vector2(12, 12),
            MouseFilter = MouseFilterEnum.Stop,
        };
        AddChild(panel);
        var box = new VBoxContainer();
        panel.AddChild(box);

        box.AddChild(new Label { Text = "Roads" });

        var modes = new HBoxContainer();
        box.AddChild(modes);
        var group = new ButtonGroup();
        foreach (var (label, mode) in new[]
        {
            ("Straight", ToolMode.Straight),
            ("Curve", ToolMode.SimpleCurve),
            ("Curve+", ToolMode.ComplexCurve),
            ("Arc", ToolMode.Arc),
            ("Chain", ToolMode.Continuous),
            ("Grid", ToolMode.Grid),
            ("Upgrade", ToolMode.Upgrade),
            ("Bulldoze", ToolMode.Bulldoze),
            ("Junction", ToolMode.Inspect),
            ("Car", ToolMode.SpawnVehicle),
        })
        {
            var b = new Button
            {
                Text = label,
                ToggleMode = true,
                ButtonGroup = group,
                ButtonPressed = mode == ToolMode.Straight,
            };
            b.Pressed += () => _controller.SetMode(mode);
            modes.AddChild(b);
        }

        var typeRow = new HBoxContainer();
        box.AddChild(typeRow);
        typeRow.AddChild(new Label { Text = "Type " });
        var typePick = new OptionButton();
        foreach (var t in RoadCatalog.All)
            typePick.AddItem(t.Name, t.Id.Value);
        typePick.Selected = 0;
        typePick.ItemSelected += idx =>
            _controller.SetRoadType(new Domain.Network.RoadTypeId(typePick.GetItemId((int)idx)));
        typeRow.AddChild(typePick);

        var snapRow = new HBoxContainer();
        box.AddChild(snapRow);
        snapRow.AddChild(new Label { Text = "Snap " });
        foreach (var (label, flag, initial) in new[]
        {
            ("Nodes", SnapTypes.Nodes, true),
            ("Edges", SnapTypes.Edges, true),
            ("Angle", SnapTypes.Angle, true),
            ("Guides", SnapTypes.Guidelines, true),
            ("Parallel", SnapTypes.Parallel, true),
            ("Perp", SnapTypes.Perpendicular, true),
            ("8 m", SnapTypes.CellLength, true),
            ("Grid", SnapTypes.Grid, false),
        })
        {
            var cb = new CheckBox { Text = label, ButtonPressed = initial };
            cb.Toggled += on => _controller.SetSnapType(flag, on);
            snapRow.AddChild(cb);
            // push every initial state: the session default differs (CellLength is
            // off there so raw domain tests stay unquantized; the game wants it on)
            _controller.SetSnapType(flag, initial);
        }
        var cellPick = new OptionButton();
        foreach (var size in new[] { 4, 8, 16, 32 })
            cellPick.AddItem($"{size} m", size);
        cellPick.Selected = 1; // 8 m
        cellPick.ItemSelected += idx =>
            _controller.Session.Grid = new GridConfig(cellPick.GetItemId((int)idx));
        snapRow.AddChild(cellPick);

        var adjust = new CheckBox { Text = "Adjust before commit" };
        adjust.Toggled += on => _controller.Session.AdjustMode = on;
        box.AddChild(adjust);

        var lanesToggle = new CheckBox { Text = "Show lanes (debug)" };
        lanesToggle.Toggled += on => _lanes.SetShown(on);
        box.AddChild(lanesToggle);

        var trafficRow = new HBoxContainer();
        box.AddChild(trafficRow);
        var trafficToggle = new CheckBox { Text = "Traffic" };
        trafficToggle.Toggled += on => _main.TrafficEnabled = on;
        trafficRow.AddChild(trafficToggle);
        var density = new HSlider
        {
            MinValue = 0, MaxValue = 300, Step = 10, Value = 100,
            CustomMinimumSize = new Vector2(120, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        density.ValueChanged += val => _main.Traffic.TargetPopulation = (int)val;
        trafficRow.AddChild(density);
        _trafficCount = new Label { Text = "0" };
        trafficRow.AddChild(_trafficCount);
        _main.Traffic.TargetPopulation = (int)density.Value;

        var saveRow = new HBoxContainer();
        box.AddChild(saveRow);
        var saveBtn = new Button { Text = "Save (F5)" };
        saveBtn.Pressed += () => _main.QuickSave();
        saveRow.AddChild(saveBtn);
        var loadBtn = new Button { Text = "Load (F9)" };
        loadBtn.Pressed += () => _main.QuickLoad();
        saveRow.AddChild(loadBtn);
        var undoBtn = new Button { Text = "Undo (^Z)" };
        undoBtn.Pressed += () => _main.TryUndo();
        saveRow.AddChild(undoBtn);
        var redoBtn = new Button { Text = "Redo (^Y)" };
        redoBtn.Pressed += () => _main.TryRedo();
        saveRow.AddChild(redoBtn);

        var hint = new Label
        {
            Text = "LMB place/drag handle · RMB step back · Enter confirm · Esc cancel · T release tangent lock · PgUp/PgDn elevation (Ctrl fine) · Ctrl+Z undo · Ctrl+Y redo · WASD pan · wheel zoom · Q/E rotate",
            Modulate = new Color(1, 1, 1, 0.6f),
        };
        box.AddChild(hint);

        _status = new Label
        {
            Modulate = new Color(1f, 0.5f, 0.4f),
            Visible = false,
        };
        box.AddChild(_status);

        _readout = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_readout);
    }

    public override void _Process(double delta)
    {
        var mouse = GetViewport().GetMousePosition();
        _readout.Position = mouse + new Vector2(18, 14);
        _trafficCount.Text = _main.Traffic.Vehicles.Count.ToString();

        if (_statusTtl > 0)
        {
            _statusTtl -= delta;
            if (_statusTtl <= 0)
                _status.Visible = false;
        }
    }

    private void FlashStatus(string message)
    {
        _status.Text = message;
        _status.Visible = true;
        _statusTtl = 2.5;
    }
}
