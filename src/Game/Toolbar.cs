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
            ("Chain", ToolMode.Continuous),
            ("Grid", ToolMode.Grid),
            ("Bulldoze", ToolMode.Bulldoze),
            ("Junction", ToolMode.Inspect),
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
        foreach (var (label, flag) in new[]
        {
            ("Nodes", SnapTypes.Nodes),
            ("Edges", SnapTypes.Edges),
            ("Angle", SnapTypes.Angle),
            ("Guides", SnapTypes.Guidelines),
        })
        {
            var cb = new CheckBox { Text = label, ButtonPressed = true };
            cb.Toggled += on => _controller.SetSnapType(flag, on);
            snapRow.AddChild(cb);
        }

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

        var hint = new Label
        {
            Text = "LMB place · RMB step back · Esc cancel · WASD pan · wheel zoom · Q/E rotate",
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
