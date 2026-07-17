using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Godot;

namespace CityBuilder.Game;

/// <summary>Right-side inspector for the selected junction: control mode, per-leg
/// roles and size offsets, node-wide size. Every change flows through
/// RoadNetwork.ConfigureJunction; the panel re-reads resolved state afterwards.</summary>
public partial class JunctionPanel : PanelContainer
{
    private RoadNetwork _network = null!;
    private NodeId? _node;
    private bool _updating;

    private OptionButton _modePick = null!;
    private HSlider _size = null!;
    private Label _sizeLabel = null!;
    private VBoxContainer _legRows = null!;

    private static readonly JunctionControlMode[] ModeOrder =
    {
        JunctionControlMode.Auto,
        JunctionControlMode.PrioritySigns,
        JunctionControlMode.AllWayStop,
        JunctionControlMode.TrafficLights,
        JunctionControlMode.None,
    };
    private static readonly string[] ModeNames =
        { "Auto", "Priority signs", "All-way stop", "Traffic lights", "None" };

    private Action? _beforeMutate;

    public void Bind(RoadNetwork network, Action? beforeMutate = null)
    {
        _network = network;
        _beforeMutate = beforeMutate;
        network.Changed += OnNetworkChanged;
    }

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.Minsize, 12);
        GrowHorizontal = GrowDirection.Begin;
        MouseFilter = MouseFilterEnum.Stop;

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        AddChild(box);

        box.AddChild(new Label { Text = "Junction" });

        var modeRow = new HBoxContainer();
        box.AddChild(modeRow);
        modeRow.AddChild(new Label { Text = "Control " });
        _modePick = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var name in ModeNames)
            _modePick.AddItem(name);
        _modePick.ItemSelected += _ => Apply();
        modeRow.AddChild(_modePick);

        var sizeRow = new HBoxContainer();
        box.AddChild(sizeRow);
        sizeRow.AddChild(new Label { Text = "Size " });
        _size = new HSlider
        {
            MinValue = -0.5, MaxValue = 12, Step = 0.5,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(140, 0),
        };
        _size.ValueChanged += _ => Apply();
        sizeRow.AddChild(_size);
        _sizeLabel = new Label { Text = "+0.0 m" };
        sizeRow.AddChild(_sizeLabel);

        _legRows = new VBoxContainer();
        box.AddChild(_legRows);

        var reset = new Button { Text = "Reset" };
        reset.Pressed += () =>
        {
            if (_node is { } id && _network.Nodes.ContainsKey(id))
            {
                _beforeMutate?.Invoke();
                _network.ConfigureJunction(id, JunctionConfig.Default);
                Refresh();
            }
        };
        box.AddChild(reset);
    }

    public void ShowNode(NodeId id)
    {
        _node = id;
        Visible = true;
        Refresh();
    }

    public void HideNode()
    {
        _node = null;
        Visible = false;
    }

    private void OnNetworkChanged(NetworkDelta delta)
    {
        if (_node is not { } id)
            return;
        if (!_network.Nodes.ContainsKey(id))
            HideNode();
        else if (delta.NodesChanged.Contains(id))
            Refresh();
    }

    // ---------------------------------------------------------------- UI <-> config

    private void Refresh()
    {
        if (_node is not { } id || !_network.Nodes.TryGetValue(id, out var node))
            return;
        _updating = true;

        var eff = JunctionControl.Resolve(node, _network.Edges);
        _modePick.Selected = Array.IndexOf(ModeOrder, node.Config.Mode);
        _size.Value = node.Config.SizeOffset;
        _sizeLabel.Text = $"{node.Config.SizeOffset:+0.0;-0.0} m";

        foreach (var child in _legRows.GetChildren())
            child.QueueFree();

        bool priority = eff.Mode == JunctionControlMode.PrioritySigns;
        foreach (var eid in node.Edges.OrderBy(e => e.Value))
        {
            var edge = _network.Edges[eid];
            var row = new HBoxContainer();
            _legRows.AddChild(row);

            row.AddChild(new Label
            {
                Text = $"{RoadCatalog.Get(edge.Type).Name} {Bearing(node, edge)}",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            });

            var roleBtn = new Button
            {
                Text = eff.Roles.GetValueOrDefault(eid).ToString(),
                Disabled = !priority || node.Config.Mode == JunctionControlMode.Auto,
                CustomMinimumSize = new Vector2(60, 0),
            };
            var capturedId = eid;
            roleBtn.Pressed += () => CycleRole(capturedId);
            row.AddChild(roleBtn);

            var offset = new SpinBox
            {
                MinValue = -0.5, MaxValue = 12, Step = 0.5,
                Value = node.Config.LegOffsets.GetValueOrDefault(eid),
                CustomMinimumSize = new Vector2(70, 0),
            };
            offset.ValueChanged += _ => Apply();
            offset.SetMeta("edge", eid.Value);
            row.AddChild(offset);
        }

        _updating = false;
    }

    private void CycleRole(EdgeId eid)
    {
        if (_node is not { } id || !_network.Nodes.TryGetValue(id, out var node))
            return;
        var eff = JunctionControl.Resolve(node, _network.Edges);
        var next = eff.Roles.GetValueOrDefault(eid) switch
        {
            LegRole.Main => LegRole.Yield,
            LegRole.Yield => LegRole.Stop,
            _ => LegRole.Main,
        };
        var overrides = new Dictionary<EdgeId, LegRole>(node.Config.RoleOverrides) { [eid] = next };
        _beforeMutate?.Invoke();
        _network.ConfigureJunction(id, node.Config with { RoleOverrides = overrides });
        Refresh();
    }

    /// <summary>Assemble a config from the current widget state and apply it.</summary>
    private void Apply()
    {
        if (_updating || _node is not { } id || !_network.Nodes.TryGetValue(id, out var node))
            return;

        var legOffsets = new Dictionary<EdgeId, float>();
        foreach (var child in _legRows.GetChildren())
            foreach (var widget in ((Node)child).GetChildren())
                if (widget is SpinBox sb && sb.HasMeta("edge") && MathF.Abs((float)sb.Value) > 0.01f)
                    legOffsets[new EdgeId(sb.GetMeta("edge").AsInt32())] = (float)sb.Value;

        var mode = ModeOrder[Math.Max(0, _modePick.Selected)];
        var config = node.Config with
        {
            Mode = mode,
            SizeOffset = (float)_size.Value,
            LegOffsets = legOffsets,
            // switching away from priority clears role overrides so Auto stays clean
            RoleOverrides = mode is JunctionControlMode.PrioritySigns or JunctionControlMode.Auto
                ? node.Config.RoleOverrides
                : new Dictionary<EdgeId, LegRole>(),
        };
        _beforeMutate?.Invoke();
        _network.ConfigureJunction(id, config);
        Refresh();
    }

    private string Bearing(RoadNode node, RoadEdge edge)
    {
        var t = edge.StartNode == node.Id ? edge.Curve.Tangent(0) : -edge.Curve.Tangent(1);
        return MathF.Abs(t.X) >= MathF.Abs(t.Z)
            ? t.X >= 0 ? "E" : "W"
            : t.Z >= 0 ? "S" : "N";
    }
}

/// <summary>Line-loop highlight over the selected junction's surface polygon.</summary>
public partial class JunctionHighlight : MeshInstance3D
{
    private RoadNetwork _network = null!;
    private NodeId? _node;

    public void Bind(RoadNetwork network)
    {
        _network = network;
        Mesh = new ImmediateMesh();
        MaterialOverride = Materials.SnapIndicator;
        network.Changed += _ => Redraw();
    }

    public void SetNode(NodeId? id)
    {
        _node = id;
        Redraw();
    }

    private void Redraw()
    {
        var mesh = (ImmediateMesh)Mesh;
        mesh.ClearSurfaces();
        if (_node is not { } id || !_network.Nodes.TryGetValue(id, out var node))
            return;

        var poly = node.Junction.SurfacePolygon;
        mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        var up = Vector3.Up * (MeshBuilders.MarkingY + 0.03f);
        if (poly.Count >= 2)
        {
            foreach (var p in poly)
                mesh.SurfaceAddVertex(p.ToGodot() + up);
            mesh.SurfaceAddVertex(poly[0].ToGodot() + up);
        }
        else
        {
            // no junction surface (dead end): small circle around the node
            for (int i = 0; i <= 16; i++)
            {
                float a = Mathf.Tau * i / 16;
                mesh.SurfaceAddVertex(node.Position.ToGodot() + up
                    + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 2f);
            }
        }
        mesh.SurfaceEnd();
    }
}
